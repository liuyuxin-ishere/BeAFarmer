using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Gather;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Random;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 正式 Gather：严格解码 node_id → GatherCommandApplication（Policy+Composer 恰好一次）。
/// 成功摘要 <c>gather-summary-v1</c> 写入 cmdresult-v3；默认 RNG 为 <see cref="ServerRandomSource.Shared"/>。
/// 无 SQL/网络/再入队/自增 revision。客户端不得选种子或命中结果。
/// </summary>
public sealed class GatherGameplayRule : IGameplayRule
{
    public const string KindName = "Gather";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形。</summary>
    public const int PublishedParamError = 101;

    readonly IRandomSource _random;

    /// <summary>生产默认 <see cref="ServerRandomSource.Shared"/>；测试/FaultWorker 可注入受控源。</summary>
    public GatherGameplayRule(IRandomSource? random = null)
    {
        _random = random ?? ServerRandomSource.Shared;
    }

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

        if (!TryDecodeGatherIntent(command.CopyCanonicalBusinessPayload(), out GatherIntent intent, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);

        // 本命令独立计数：异常/拒绝仍如实回报；禁止跨命令共享可变计数器。
        var counting = new CallCountingRandomSource(_random);
        DomainResult<PlayerAggregate> composed;
        GatherResult? gather = null;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config, counting);
            // 先走 Policy 以保留 RngCalls；再 Compose（与 Application.Evaluate 等价路径）。
            GatherRules rules = GatherRules.FromSnapshot(ctx.Config);
            GatherInput input = GatherInput.FromAggregate(source);
            gather = GatherPolicy.Evaluate(input, intent, rules, ctx.NowMs, counting);
            composed = GatherCommandApplication.ComposeGatherSettlement(source, gather, cmdCtx);
        }
        catch (InvalidOperationException ex)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: counting.Calls,
                message: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: counting.Calls,
                message: ex.Message);
        }

        int rngCalls = Math.Max(gather?.RngCalls ?? 0, counting.Calls);
        if (!composed.Success)
        {
            return new RuleEvaluation(
                MapPublishedGatherFailure(composed), mutatesAssets: false, candidate: null,
                rngCalls: rngCalls,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "gather refused");
        }

        if (gather == null || !gather.Success || !gather.HasSettlement || gather.NodeAfter == null)
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: rngCalls,
                message: "gather settlement missing");
        }

        // NodeAfter 为 Nullable<NodeStateView>：null 检查后仍须 .Value 解包（与 FarmCommandApplication 同）。
        NodeStateView nodeAfter = gather.NodeAfter.Value;

        var gains = new List<GatherOpSummaryCodec.ItemGain>(2)
        {
            new GatherOpSummaryCodec.ItemGain(gather.OrdinaryItemId, gather.OrdinaryCount)
        };
        if (gather.RareHit)
            gains.Add(new GatherOpSummaryCodec.ItemGain(gather.RareItemId, gather.RareCount));

        byte[] summary = GatherOpSummaryCodec.Encode(new GatherOpSummaryCodec.Summary(
            intent.NodeInstanceId,
            gather.RareHit,
            gains,
            gather.ExpGained,
            nodeAfter.NextReadyTime));

        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(composed.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId,
            nextData,
            observed.Revision,
            observed.SavedRevision,
            composed.Candidate!.ShopPurchases,
            composed.Candidate!.AcceptedTasks);
        return new RuleEvaluation(
            0, mutatesAssets: true, candidate, rngCalls: rngCalls, message: "ok",
            frozenOperationSummary: summary);
    }

    /// <summary>
    /// 正式公开码：畸形/授权/缺规则/溢出等 BadPacket → 101；
    /// 冷却406、缺工具407、等级108、满包301 等非 BadPacket 保持原码。
    /// </summary>
    internal static int MapPublishedGatherFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;
        return PublishedParamError;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodeGather</c> 对称：
    /// tag node_id + i32；拒非正/尾随垃圾。
    /// </summary>
    internal static bool TryDecodeGatherIntent(byte[] payload, out GatherIntent intent, out string message)
    {
        intent = new GatherIntent(0);
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag) || !string.Equals(tag, "node_id", StringComparison.Ordinal))
            {
                message = "missing node_id tag";
                return false;
            }

            if (!TryReadI32(ms, out int nodeId))
            {
                message = "bad node_id";
                return false;
            }

            if (nodeId <= 0)
            {
                message = "non-positive node";
                return false;
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            intent = new GatherIntent(nodeId);
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
