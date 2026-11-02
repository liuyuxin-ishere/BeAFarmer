using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 正式 Fertilize（T032 一次施肥）：严格解码规范业务字节 → FarmCommandApplication.Evaluate(Fertilize)
/// → Composer 恰好一次（扣肥+地块同提交）。无 SQL/网络/再入队/自增 revision。
/// </summary>
public sealed class FertilizeGameplayRule : IGameplayRule
{
    public const string KindName = "Fertilize";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形 / 未知实例等。</summary>
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

        if (!TryDecodeFertilizeIntent(command.CopyCanonicalBusinessPayload(), out PlotIntent intent, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);
        DomainResult<PlayerAggregate> composed;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config);
            composed = FarmCommandApplication.Evaluate(source, FarmCommand.Fertilize(intent), cmdCtx);
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
                MapPublishedFertilizeFailure(composed), mutatesAssets: false, candidate: null, rngCalls: 0,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "fertilize refused");
        }

        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(composed.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId,
            nextData,
            observed.Revision,
            observed.SavedRevision,
            composed.Candidate!.ShopPurchases,
            composed.Candidate!.AcceptedTasks);
        return new RuleEvaluation(0, mutatesAssets: true, candidate, rngCalls: 0, message: "ok");
    }

    /// <summary>
    /// 正式公开码：畸形/未知/缺规格/成熟等 BadPacket → 101；锁地401；已施肥405；缺肥302；
    /// 其余纯 Policy 码不变。成熟拒绝不得映射 403；见 ADR T032。
    /// </summary>
    internal static int MapPublishedFertilizeFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;

        IReadOnlyList<string> diagnostics = result.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, CropCarePolicy.DiagCareBadIntent, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareNegativeClock, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCarePlotUnknownInstance, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCarePlotMissing, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCarePlotEmpty, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCarePlotUnusable, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareAlreadyMature, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareMissingAccepted, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareSpecMismatch, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareMalformed, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareRatioInvalid, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareReductionOverflow, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareBagMalformed, StringComparison.Ordinal)
                || string.Equals(d, CropCarePolicy.DiagCareFertilizerItemInvalid, StringComparison.Ordinal))
            {
                return PublishedParamError;
            }
        }

        return result.Code;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodeFertilize</c> 对称：
    /// tag plot_id + i32 plotId；拒尾随垃圾。大端。
    /// </summary>
    internal static bool TryDecodeFertilizeIntent(byte[] payload, out PlotIntent intent, out string message)
    {
        intent = new PlotIntent(0);
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag) || !string.Equals(tag, "plot_id", StringComparison.Ordinal))
            {
                message = "missing plot_id tag";
                return false;
            }

            if (!TryReadI32(ms, out int plotId))
            {
                message = "bad plot_id";
                return false;
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            if (plotId <= 0)
            {
                message = "non-positive plot";
                return false;
            }

            intent = new PlotIntent(plotId);
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
