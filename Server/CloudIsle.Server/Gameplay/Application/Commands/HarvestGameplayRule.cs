using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Random;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 正式 Harvest：严格解码 plot_ids → 单地块（T033）或稳定批量（T034，2..24）。
/// 单地块成功摘要保持 harvest-summary-v1；批量用 farm-batch-summary-v1。
/// 无 SQL/网络/再入队/自增 revision。默认 RNG 为 <see cref="ServerRandomSource.Shared"/>。
/// </summary>
public sealed class HarvestGameplayRule : IGameplayRule
{
    public const string KindName = "Harvest";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形 / 整包拒绝。</summary>
    public const int PublishedParamError = 101;

    readonly IRandomSource _random;

    /// <summary>生产默认 <see cref="ServerRandomSource.Shared"/>；测试可注入/装饰同一接口（受控 Worker 须标注）。</summary>
    public HarvestGameplayRule(IRandomSource? random = null)
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

        if (!TryDecodeHarvestPlotIds(command.CopyCanonicalBusinessPayload(), out IReadOnlyList<int> plotIds, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);

        if (plotIds.Count == 1)
            return EvaluateSingle(observed, source, new PlotIntent(plotIds[0]), ctx);

        return EvaluateBatch(observed, source, plotIds, ctx);
    }

    RuleEvaluation EvaluateSingle(
        PlayerState observed,
        PlayerAggregate source,
        PlotIntent intent,
        CommandEvalContext ctx)
    {
        // 单地块亦按本命令独立计数，异常路径不丢已发生抽签。
        var counting = new CallCountingRandomSource(_random);
        FarmHarvestResult harvest;
        DomainResult<PlayerAggregate> composed;
        try
        {
            CropRules cropRules = CropRules.FromSnapshot(ctx.Config!);
            FarmInput farmInput = FarmInput.FromAggregate(source);
            harvest = HarvestPolicy.Evaluate(
                farmInput, intent, cropRules, ctx.NowMs, counting);
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config!, counting);
            composed = FarmCommandApplication.ComposeHarvestSettlement(source, harvest, cmdCtx);
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

        int rngCalls = Math.Max(harvest.RngCalls, counting.Calls);
        if (!composed.Success)
        {
            return new RuleEvaluation(
                MapPublishedHarvestFailure(composed), mutatesAssets: false, candidate: null,
                rngCalls: rngCalls,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "harvest refused");
        }

        if (!harvest.Success || !harvest.HasSettlement)
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: rngCalls,
                message: "harvest settlement missing");
        }

        byte[] summary = HarvestOpSummaryCodec.Encode(new HarvestOpSummaryCodec.Summary(
            intent.PlotId,
            plotResultCode: 0,
            new[] { new HarvestOpSummaryCodec.ItemGain(harvest.YieldItemId, harvest.YieldCount) },
            harvest.ExpGained));

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

    RuleEvaluation EvaluateBatch(
        PlayerState observed,
        PlayerAggregate source,
        IReadOnlyList<int> plotIds,
        CommandEvalContext ctx)
    {
        var commands = new PlotCommand[plotIds.Count];
        for (int i = 0; i < plotIds.Count; i++)
            commands[i] = new PlotCommand(plotIds[i], seedId: 0);

        FarmBatchNormalizeResult normalized = FarmBatchNormalizer.Normalize(
            new BatchIntent(FarmBatchKind.Harvest, commands), FarmBatchNormalizer.DefaultMaxItems);
        if (!normalized.Success)
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: normalized.Diagnostics.Count > 0 ? normalized.Diagnostics[0] : "harvest batch refused");
        }

        // 本命令独立计数：溢出/异常拒绝仍如实回报实际抽签次数；禁止跨命令共享可变计数器。
        var counting = new CallCountingRandomSource(_random);
        BatchDomainResult batch;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config!, counting);
            batch = FarmCommandApplication.EvaluateBatch(source, normalized.Batch!, cmdCtx);
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

        byte[] summary = FarmBatchSummaryCodec.EncodeFromBatch(FarmBatchKind.Harvest, batch);
        if (!batch.MutatesAssets)
        {
            return new RuleEvaluation(
                0, mutatesAssets: false, candidate: null, rngCalls: batch.TotalRngCalls,
                message: "harvest batch recorded no-change",
                frozenOperationSummary: summary);
        }

        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(batch.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId,
            nextData,
            observed.Revision,
            observed.SavedRevision,
            batch.Candidate!.ShopPurchases,
            batch.Candidate!.AcceptedTasks);
        return new RuleEvaluation(
            0, mutatesAssets: true, candidate, rngCalls: batch.TotalRngCalls, message: "ok",
            frozenOperationSummary: summary);
    }

    /// <summary>
    /// 正式公开码：畸形/缺规格/公式等 BadPacket → 101；锁地401；未成熟403；满包301 等保持。
    /// </summary>
    internal static int MapPublishedHarvestFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;

        IReadOnlyList<string> diagnostics = result.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, HarvestPolicy.DiagHarvestBadIntent, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestNegativeClock, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestPlotUnknownInstance, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestPlotMissing, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestPlotEmpty, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestPlotUnusable, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestMalformed, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestSpecMismatch, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestUnsupportedFormula, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestRandomRequired, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestRangeOverflow, StringComparison.Ordinal)
                || string.Equals(d, HarvestPolicy.DiagHarvestSettlementRequired, StringComparison.Ordinal)
                || string.Equals(d, FarmLegacyHarvestPolicy.DiagMissingAcceptedSpec, StringComparison.Ordinal))
            {
                return PublishedParamError;
            }
        }

        return result.Code;
    }

    /// <summary>兼容旧单意图解码：恰好 1 个 plotId 时成功。</summary>
    internal static bool TryDecodeHarvestIntent(byte[] payload, out PlotIntent intent, out string message)
    {
        intent = new PlotIntent(0);
        if (!TryDecodeHarvestPlotIds(payload, out IReadOnlyList<int> ids, out message))
            return false;
        if (ids.Count != 1)
        {
            message = ids.Count <= 0 ? "empty harvest plot_ids" : "harvest batch multi";
            return false;
        }

        intent = new PlotIntent(ids[0]);
        return true;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodeHarvest</c> 对称：
    /// tag plot_ids + i32 count + ids；接受 1..24；拒空/&gt;24/重复/非正/尾随垃圾。
    /// </summary>
    internal static bool TryDecodeHarvestPlotIds(byte[] payload, out IReadOnlyList<int> plotIds, out string message)
    {
        plotIds = Array.AsReadOnly(Array.Empty<int>());
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag) || !string.Equals(tag, "plot_ids", StringComparison.Ordinal))
            {
                message = "missing plot_ids tag";
                return false;
            }

            if (!TryReadI32(ms, out int count))
            {
                message = "bad plot_ids count";
                return false;
            }

            if (count <= 0)
            {
                message = "empty harvest plot_ids";
                return false;
            }

            if (count > FarmBatchNormalizer.DefaultMaxItems)
            {
                message = "harvest batch too many";
                return false;
            }

            var list = new int[count];
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (!TryReadI32(ms, out int plotId))
                {
                    message = "bad plot_id";
                    return false;
                }

                if (plotId <= 0)
                {
                    message = "non-positive plot";
                    return false;
                }

                if (!seen.Add(plotId))
                {
                    message = "duplicate plot";
                    return false;
                }

                list[i] = plotId;
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            plotIds = Array.AsReadOnly(list);
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
