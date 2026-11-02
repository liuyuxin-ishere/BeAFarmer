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
/// 正式 Plant：严格解码规范业务字节 → 单地块（T030）或稳定批量（T034，2..24）→
/// Composer / FarmBatchExecutor → 模板保留 + AcceptedTasks sidecar。无 SQL/网络/再入队/自增 revision。
/// </summary>
public sealed class PlantGameplayRule : IGameplayRule
{
    public const string KindName = "Plant";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形 / 整包拒绝。</summary>
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

        if (!TryDecodePlantItems(command.CopyCanonicalBusinessPayload(), out IReadOnlyList<PlantIntent> items, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);

        if (items.Count == 1)
            return EvaluateSingle(observed, source, items[0], ctx);

        return EvaluateBatch(observed, source, items, ctx);
    }

    RuleEvaluation EvaluateSingle(
        PlayerState observed,
        PlayerAggregate source,
        PlantIntent intent,
        CommandEvalContext ctx)
    {
        DomainResult<PlayerAggregate> composed;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config!);
            composed = FarmCommandApplication.Evaluate(source, FarmCommand.Plant(intent), cmdCtx);
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
                MapPublishedPlantFailure(composed), mutatesAssets: false, candidate: null, rngCalls: 0,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "plant refused");
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

    RuleEvaluation EvaluateBatch(
        PlayerState observed,
        PlayerAggregate source,
        IReadOnlyList<PlantIntent> items,
        CommandEvalContext ctx)
    {
        var commands = new PlotCommand[items.Count];
        for (int i = 0; i < items.Count; i++)
            commands[i] = new PlotCommand(items[i].PlotId, items[i].SeedId);

        FarmBatchNormalizeResult normalized = FarmBatchNormalizer.Normalize(
            new BatchIntent(FarmBatchKind.Plant, commands), FarmBatchNormalizer.DefaultMaxItems);
        if (!normalized.Success)
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: normalized.Diagnostics.Count > 0 ? normalized.Diagnostics[0] : "plant batch refused");
        }

        BatchDomainResult batch;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config!);
            batch = FarmCommandApplication.EvaluateBatch(source, normalized.Batch!, cmdCtx);
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

        byte[] summary = FarmBatchSummaryCodec.EncodeFromBatch(FarmBatchKind.Plant, batch);
        if (!batch.MutatesAssets)
        {
            // 全业务失败：顶层 0 Recorded、无资产 revision；摘要仍可重放。
            return new RuleEvaluation(
                0, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: "plant batch recorded no-change",
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
            0, mutatesAssets: true, candidate, rngCalls: 0, message: "ok",
            frozenOperationSummary: summary);
    }

    /// <summary>
    /// 正式公开码：畸形/未知实例等 BadPacket 参数类 → 101；锁地401；
    /// 等级 108；无种 302；非空 402；未知种子 306；其余纯 Policy 码不变。
    /// </summary>
    internal static int MapPublishedPlantFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;

        IReadOnlyList<string> diagnostics = result.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, PlantPolicy.DiagPlantBadIntent, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantNegativeClock, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantDeadlineOverflow, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantPlotUnknownInstance, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantPlotMissing, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantPlotUnusable, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantBagMalformed, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantStaleAcceptedConflict, StringComparison.Ordinal)
                || string.Equals(d, PlantPolicy.DiagPlantMalformed, StringComparison.Ordinal))
            {
                return PublishedParamError;
            }
        }

        return result.Code;
    }

    /// <summary>兼容旧单意图解码：恰好 1 项时成功。</summary>
    internal static bool TryDecodePlantIntent(byte[] payload, out PlantIntent intent, out string message)
    {
        intent = new PlantIntent(0, 0);
        if (!TryDecodePlantItems(payload, out IReadOnlyList<PlantIntent> items, out message))
            return false;
        if (items.Count != 1)
        {
            message = items.Count <= 0 ? "empty plant items" : "plant batch multi";
            return false;
        }

        intent = items[0];
        return true;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodePlant</c> 对称：
    /// tag items + i32 count + (plot,seed)*；接受 1..24；拒空/&gt;24/重复/非正/尾随垃圾。
    /// </summary>
    internal static bool TryDecodePlantItems(byte[] payload, out IReadOnlyList<PlantIntent> items, out string message)
    {
        items = Array.AsReadOnly(Array.Empty<PlantIntent>());
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag) || !string.Equals(tag, "items", StringComparison.Ordinal))
            {
                message = "missing items tag";
                return false;
            }

            if (!TryReadI32(ms, out int count))
            {
                message = "bad items count";
                return false;
            }

            if (count <= 0)
            {
                message = "empty plant items";
                return false;
            }

            if (count > FarmBatchNormalizer.DefaultMaxItems)
            {
                message = "plant batch too many";
                return false;
            }

            var list = new PlantIntent[count];
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (!TryReadI32(ms, out int plotId) || !TryReadI32(ms, out int seedId))
                {
                    message = "bad plant item";
                    return false;
                }

                if (plotId <= 0 || seedId <= 0)
                {
                    message = "non-positive plot/seed";
                    return false;
                }

                if (!seen.Add(plotId))
                {
                    message = "duplicate plot";
                    return false;
                }

                list[i] = new PlantIntent(plotId, seedId);
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            items = Array.AsReadOnly(list);
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
