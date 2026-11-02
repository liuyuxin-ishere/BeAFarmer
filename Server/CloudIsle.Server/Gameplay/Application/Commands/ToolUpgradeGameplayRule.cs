using System;
using System.IO;
using System.Text;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Tool;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 正式 ToolUpgrade：严格解码 tool_id → ToolCommandApplication（Policy+Composer 恰好一次）。
/// 成功摘要 <c>tool-upgrade-summary-v1</c> 写入 cmdresult-v3；无 RNG。
/// 无 SQL/网络/再入队/自增 revision。客户端只选 toolId，成本由服务端捕获。
/// </summary>
public sealed class ToolUpgradeGameplayRule : IGameplayRule
{
    public const string KindName = "ToolUpgrade";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形。</summary>
    public const int PublishedParamError = 101;

    public string Kind => KindName;

    public RuleEvaluation Evaluate(PlayerState observed, CommandEnvelope command, CommandEvalContext ctx)
    {
        if (observed == null)
            throw new ArgumentNullException(nameof(observed));
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (ctx == null)
            throw new ArgumentNullException(nameof(ctx));
        if (!string.Equals(command.Kind, KindName, StringComparison.Ordinal))
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: "kind mismatch");
        }

        if (ctx.Config == null)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: "config required");
        }

        if (!TryDecodeToolUpgradeIntent(command.CopyCanonicalBusinessPayload(), out ToolUpgradeIntent intent, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);

        DomainResult<PlayerAggregate> composed;
        ToolUpgradeResult? upgrade = null;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config);
            ToolRules rules = ToolRules.FromSnapshot(ctx.Config);
            ToolInput input = ToolInput.FromAggregate(source);
            upgrade = ToolUpgradePolicy.Evaluate(input, intent, rules);
            composed = ToolCommandApplication.ComposeToolUpgradeSettlement(source, upgrade, cmdCtx);
        }
        catch (InvalidOperationException ex)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: ex.Message);
        }

        if (!composed.Success)
        {
            return new RuleEvaluation(
                MapPublishedToolUpgradeFailure(composed), mutatesAssets: false, candidate: null,
                rngCalls: 0,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "tool upgrade refused");
        }

        if (upgrade == null || !upgrade.Success || !upgrade.HasSettlement || upgrade.ToolAfter == null)
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: "tool upgrade settlement missing");
        }

        ToolStateView toolAfter = upgrade.ToolAfter.Value;
        byte[] summary = ToolUpgradeOpSummaryCodec.Encode(
            new ToolUpgradeOpSummaryCodec.Summary(toolAfter.ToolId, toolAfter.Level));

        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(composed.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId,
            nextData,
            observed.Revision,
            observed.SavedRevision,
            composed.Candidate!.ShopPurchases,
            composed.Candidate!.AcceptedTasks);
        return new RuleEvaluation(
            0, mutatesAssets: true, candidate, rngCalls: 0, message: "ok",
            frozenOperationSummary: summary);
    }

    /// <summary>
    /// 正式公开码：畸形/溢出/缺规格等 BadPacket → 101；
    /// 407/108/303/302 等非 BadPacket 保持原码。
    /// </summary>
    internal static int MapPublishedToolUpgradeFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;
        return PublishedParamError;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodeToolUpgrade</c> 对称：
    /// tag tool_id + i32；拒非正/尾随垃圾。
    /// </summary>
    internal static bool TryDecodeToolUpgradeIntent(byte[] payload, out ToolUpgradeIntent intent, out string message)
    {
        intent = new ToolUpgradeIntent(0);
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag) || !string.Equals(tag, "tool_id", StringComparison.Ordinal))
            {
                message = "missing tool_id tag";
                return false;
            }

            if (!TryReadI32(ms, out int toolId))
            {
                message = "bad tool_id";
                return false;
            }

            if (toolId <= 0)
            {
                message = "non-positive tool";
                return false;
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            intent = new ToolUpgradeIntent(toolId);
            return true;
        }
        catch (EndOfStreamException)
        {
            message = "truncated payload";
            return false;
        }
        catch (InvalidDataException)
        {
            message = "corrupt payload";
            return false;
        }
    }

    static bool TryReadTag(Stream s, out string tag)
    {
        tag = "";
        if (!TryReadI32(s, out int len) || len < 0 || len > 1024)
            return false;
        if (s.Length - s.Position < len)
            return false;
        var buf = new byte[len];
        int read = s.Read(buf, 0, len);
        if (read != len)
            return false;
        tag = Encoding.UTF8.GetString(buf);
        return true;
    }

    static bool TryReadI32(Stream s, out int value)
    {
        value = 0;
        if (s.Length - s.Position < 4)
            return false;
        int b0 = s.ReadByte();
        int b1 = s.ReadByte();
        int b2 = s.ReadByte();
        int b3 = s.ReadByte();
        if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
            return false;
        value = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        return true;
    }
}
