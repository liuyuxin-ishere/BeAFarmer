using System;
using System.IO;
using System.Text;
using CloudIsle.Proto;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 账本 <c>result_blob</c> 版本化业务归档（非网络 Resp、不含 Token/seq）。
/// <para>
/// 契约：
/// <list type="bullet">
/// <item><c>cmdresult-v1</c>：kind/code/rev/cfg + 可选后态全量投影。仅含后态时无法可靠还原背包删除等增量 tombstone，
/// 不得用「同投影克隆 before/after」伪造 revision-advancing Batch。</item>
/// <item><c>cmdresult-v2</c>：在 v1 字段后追加可选<strong>提交瞬间冻结</strong>的 <see cref="PlayerDeltaBatchNtf"/>。
/// 重放必须原样回放该 delta（可刷新 ServerTime），零规则/RNG。</item>
/// <item>旧 T011 将 QueryResp 直接入库的字节与上述前缀互不混淆；解码失败视为 opaque。</item>
/// </list>
/// </para>
/// </summary>
public static class CommandResultArchive
{
    public const string FormatVersionV1 = "cmdresult-v1";
    public const string FormatVersionV2 = "cmdresult-v2";
    /// <summary>v2 字段后追加可选冻结业务摘要（Harvest/Gather/farm-batch）；旧 v1/v2 严格 EOF 不变。</summary>
    public const string FormatVersionV3 = "cmdresult-v3";

    /// <summary>兼容旧名；等同 <see cref="FormatVersionV1"/>。</summary>
    public const string FormatVersion = FormatVersionV1;

    /// <summary>编码 v1（仅后态投影）。新成功写应优先 <see cref="EncodeV2"/>。</summary>
    public static byte[] EncodeV1(
        string kind,
        int code,
        long committedRevision,
        int configVersion,
        PlayerFullData? projection)
    {
        ValidateHeader(kind, committedRevision, configVersion);
        byte[] proj = projection == null ? Array.Empty<byte>() : projection.ToByteArray();
        using var ms = new MemoryStream(64 + proj.Length);
        WriteLpUtf8(ms, FormatVersionV1);
        WriteHeaderFields(ms, kind, code, committedRevision, configVersion);
        WriteLpBytes(ms, proj);
        return ms.ToArray();
    }

    /// <summary>
    /// 编码 v2：投影 + 可选原始冻结 delta。
    /// 成功资产变更必须带非空 delta；拒绝/no-op 两者均可为 null。
    /// </summary>
    public static byte[] EncodeV2(
        string kind,
        int code,
        long committedRevision,
        int configVersion,
        PlayerFullData? projection,
        PlayerDeltaBatchNtf? frozenDelta)
    {
        ValidateHeader(kind, committedRevision, configVersion);
        byte[] proj = projection == null ? Array.Empty<byte>() : projection.ToByteArray();
        byte[] delta = frozenDelta == null ? Array.Empty<byte>() : frozenDelta.ToByteArray();
        using var ms = new MemoryStream(64 + proj.Length + delta.Length);
        WriteLpUtf8(ms, FormatVersionV2);
        WriteHeaderFields(ms, kind, code, committedRevision, configVersion);
        WriteLpBytes(ms, proj);
        WriteLpBytes(ms, delta);
        return ms.ToArray();
    }

    /// <summary>
    /// 编码 v3：v2 字段 + 可选冻结业务摘要（严格 EOF）。不得静默当作 v2 尾扩展。
    /// </summary>
    public static byte[] EncodeV3(
        string kind,
        int code,
        long committedRevision,
        int configVersion,
        PlayerFullData? projection,
        PlayerDeltaBatchNtf? frozenDelta,
        byte[]? operationSummary)
    {
        ValidateHeader(kind, committedRevision, configVersion);
        byte[] proj = projection == null ? Array.Empty<byte>() : projection.ToByteArray();
        byte[] delta = frozenDelta == null ? Array.Empty<byte>() : frozenDelta.ToByteArray();
        byte[] summary = operationSummary == null ? Array.Empty<byte>() : operationSummary;
        using var ms = new MemoryStream(64 + proj.Length + delta.Length + summary.Length);
        WriteLpUtf8(ms, FormatVersionV3);
        WriteHeaderFields(ms, kind, code, committedRevision, configVersion);
        WriteLpBytes(ms, proj);
        WriteLpBytes(ms, delta);
        WriteLpBytes(ms, summary);
        return ms.ToArray();
    }

    /// <summary>尝试解码 v1；非本格式返回 false。</summary>
    public static bool TryDecodeV1(
        byte[] blob,
        out string kind,
        out int code,
        out long committedRevision,
        out int configVersion,
        out PlayerFullData? projection)
    {
        kind = "";
        code = 0;
        committedRevision = 0;
        configVersion = 0;
        projection = null;
        if (blob == null || blob.Length < 8)
            return false;

        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            if (!string.Equals(ReadLpUtf8(ms), FormatVersionV1, StringComparison.Ordinal))
                return false;
            ReadHeaderFields(ms, out kind, out code, out committedRevision, out configVersion);
            byte[] proj = ReadLpBytes(ms);
            if (ms.Position != ms.Length)
                return false;
            if (proj.Length > 0)
                projection = PlayerFullData.Parser.ParseFrom(proj);
            return true;
        }
        catch
        {
            kind = "";
            code = 0;
            committedRevision = 0;
            configVersion = 0;
            projection = null;
            return false;
        }
    }

    /// <summary>尝试解码 v2；非本格式返回 false。v3 字节不得被本方法当成成功。</summary>
    public static bool TryDecodeV2(
        byte[] blob,
        out string kind,
        out int code,
        out long committedRevision,
        out int configVersion,
        out PlayerFullData? projection,
        out PlayerDeltaBatchNtf? frozenDelta)
    {
        kind = "";
        code = 0;
        committedRevision = 0;
        configVersion = 0;
        projection = null;
        frozenDelta = null;
        if (blob == null || blob.Length < 8)
            return false;

        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            if (!string.Equals(ReadLpUtf8(ms), FormatVersionV2, StringComparison.Ordinal))
                return false;
            ReadHeaderFields(ms, out kind, out code, out committedRevision, out configVersion);
            byte[] proj = ReadLpBytes(ms);
            byte[] delta = ReadLpBytes(ms);
            if (ms.Position != ms.Length)
                return false;
            if (proj.Length > 0)
                projection = PlayerFullData.Parser.ParseFrom(proj);
            if (delta.Length > 0)
                frozenDelta = PlayerDeltaBatchNtf.Parser.ParseFrom(delta);
            return true;
        }
        catch
        {
            kind = "";
            code = 0;
            committedRevision = 0;
            configVersion = 0;
            projection = null;
            frozenDelta = null;
            return false;
        }
    }

    /// <summary>尝试解码 v3；非本格式返回 false。尾垃圾拒绝。</summary>
    public static bool TryDecodeV3(
        byte[] blob,
        out string kind,
        out int code,
        out long committedRevision,
        out int configVersion,
        out PlayerFullData? projection,
        out PlayerDeltaBatchNtf? frozenDelta,
        out byte[]? operationSummary)
    {
        kind = "";
        code = 0;
        committedRevision = 0;
        configVersion = 0;
        projection = null;
        frozenDelta = null;
        operationSummary = null;
        if (blob == null || blob.Length < 8)
            return false;

        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            if (!string.Equals(ReadLpUtf8(ms), FormatVersionV3, StringComparison.Ordinal))
                return false;
            ReadHeaderFields(ms, out kind, out code, out committedRevision, out configVersion);
            byte[] proj = ReadLpBytes(ms);
            byte[] delta = ReadLpBytes(ms);
            byte[] summary = ReadLpBytes(ms);
            if (ms.Position != ms.Length)
                return false;
            if (proj.Length > 0)
                projection = PlayerFullData.Parser.ParseFrom(proj);
            if (delta.Length > 0)
                frozenDelta = PlayerDeltaBatchNtf.Parser.ParseFrom(delta);
            if (summary.Length > 0)
            {
                operationSummary = new byte[summary.Length];
                Buffer.BlockCopy(summary, 0, operationSummary, 0, summary.Length);
            }

            return true;
        }
        catch
        {
            kind = "";
            code = 0;
            committedRevision = 0;
            configVersion = 0;
            projection = null;
            frozenDelta = null;
            operationSummary = null;
            return false;
        }
    }

    /// <summary>
    /// 识别 v1/v2/v3。v1 的 <paramref name="frozenDelta"/> 恒为 null（调用方不得从投影伪造增量）。
    /// </summary>
    public static bool TryDecode(
        byte[] blob,
        out string formatVersion,
        out string kind,
        out int code,
        out long committedRevision,
        out int configVersion,
        out PlayerFullData? projection,
        out PlayerDeltaBatchNtf? frozenDelta)
    {
        formatVersion = "";
        kind = "";
        code = 0;
        committedRevision = 0;
        configVersion = 0;
        projection = null;
        frozenDelta = null;

        if (TryDecodeV3(
                blob, out kind, out code, out committedRevision, out configVersion,
                out projection, out frozenDelta, out _))
        {
            formatVersion = FormatVersionV3;
            return true;
        }

        if (TryDecodeV2(blob, out kind, out code, out committedRevision, out configVersion, out projection, out frozenDelta))
        {
            formatVersion = FormatVersionV2;
            return true;
        }

        if (TryDecodeV1(blob, out kind, out code, out committedRevision, out configVersion, out projection))
        {
            formatVersion = FormatVersionV1;
            frozenDelta = null;
            return true;
        }

        return false;
    }

    /// <summary>自 v3 取出业务摘要克隆；非 v3 或空摘要返回 false。</summary>
    public static bool TryGetOperationSummary(byte[] blob, out byte[]? summary)
    {
        summary = null;
        if (!TryDecodeV3(
                blob, out _, out _, out _, out _, out _, out _, out byte[]? raw)
            || raw == null
            || raw.Length == 0)
            return false;
        summary = new byte[raw.Length];
        Buffer.BlockCopy(raw, 0, summary, 0, raw.Length);
        return true;
    }

    /// <summary>
    /// 前缀是否为指定格式版本（即使后续截断导致完整解码失败）。
    /// 用于区分「可识别的新 v3 残缺」与合法旧 v1/v2，禁止把 decode 失败当 legacy 成功。
    /// </summary>
    public static bool LooksLikeFormatVersion(byte[] blob, string formatVersion)
    {
        if (blob == null || blob.Length < 8 || string.IsNullOrEmpty(formatVersion))
            return false;
        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            return string.Equals(ReadLpUtf8(ms), formatVersion, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自归档取出可重放的资产 delta。
    /// v2：返回克隆后的原始 delta（调用方可刷新 ServerTime）。
    /// v1 / opaque / 拒绝码：返回 null——宁可 Resp + 全量 Sync，也不发残缺 revision-advancing Batch。
    /// 新 v3 Harvest 成功但摘要/语义缺损：GenericDecode 仍可识别，走 opaque/resync，
    /// 不得被误判为旧 v1/v2 成功重放。
    /// </summary>
    public static bool TryGetReplayableDelta(
        byte[] blob,
        out PlayerDeltaBatchNtf? frozenDelta,
        out bool requiresClientResync)
    {
        frozenDelta = null;
        requiresClientResync = false;
        if (!TryDecode(
                blob, out string ver, out string kind, out int code,
                out long committedRevision, out int configVersion,
                out PlayerFullData? projection, out PlayerDeltaBatchNtf? delta))
            return false;

        if (code != 0)
            return true; // 已识别归档，但无资产 batch

        if (string.Equals(ver, FormatVersionV3, StringComparison.Ordinal))
        {
            if (string.Equals(kind, "Harvest", StringComparison.Ordinal)
                || string.Equals(kind, "Plant", StringComparison.Ordinal))
            {
                if (TryGetOperationSummary(blob, out byte[]? summaryRaw)
                    && summaryRaw != null
                    && FarmBatchSummaryCodec.TryDecode(summaryRaw, out FarmBatchSummaryCodec.Summary? batchSummary)
                    && FarmBatchSummaryCodec.IsValidBatchSummary(batchSummary)
                    && BatchSummaryKindMatchesArchive(kind, batchSummary!))
                {
                    // 批量摘要：全失败须 delta/projection 皆 null（A3）；有成功须带冻结 delta 且目标无歧义。
                    // GenericReplay 无投递 key：仅校验 header/projection/delta 可证明的 revision/config。
                    bool anySuccess = false;
                    for (int i = 0; i < batchSummary!.Items.Count; i++)
                    {
                        if (batchSummary.Items[i].Code == 0)
                        {
                            anySuccess = true;
                            break;
                        }
                    }

                    if (anySuccess)
                    {
                        if (delta == null
                            || !TryValidateFarmBatchSuccessPlots(batchSummary, delta)
                            || !TryValidateFarmBatchArchiveAssets(
                                committedRevision, configVersion, projection, delta, anySuccess: true))
                        {
                            requiresClientResync = true;
                            return true;
                        }

                        frozenDelta = delta.Clone();
                        return true;
                    }

                    // 全失败：不得夹带 advancing delta/projection；矛盾记录拒发/resync，不静默丢弃。
                    if (delta != null || projection != null)
                    {
                        requiresClientResync = true;
                        frozenDelta = null;
                        return true;
                    }

                    frozenDelta = null;
                    return true;
                }

                // 识别为 Plant/Harvest 新 v3 但摘要/kind 不匹配：不得回退 legacy 成功重放。
                if (string.Equals(kind, "Plant", StringComparison.Ordinal))
                {
                    requiresClientResync = true;
                    frozenDelta = null;
                    return true;
                }

                if (string.Equals(kind, "Harvest", StringComparison.Ordinal)
                    && !TryValidateHarvestV3SuccessSemantics(blob, delta))
                {
                    requiresClientResync = true;
                    frozenDelta = null;
                    return true;
                }
            }

            if (string.Equals(kind, "Gather", StringComparison.Ordinal)
                && !TryValidateGatherV3SuccessSemantics(blob, delta))
            {
                requiresClientResync = true;
                frozenDelta = null;
                return true;
            }

            if (string.Equals(kind, "ToolUpgrade", StringComparison.Ordinal)
                && !TryValidateToolUpgradeV3SuccessSemantics(blob, delta))
            {
                requiresClientResync = true;
                frozenDelta = null;
                return true;
            }

            if (delta != null)
                frozenDelta = delta.Clone();
            return true;
        }

        if (string.Equals(ver, FormatVersionV2, StringComparison.Ordinal))
        {
            if (delta != null)
                frozenDelta = delta.Clone();
            return true;
        }

        // v1 成功归档：有投影也无法安全重建 tombstone；显式要求客户端全量同步。
        requiresClientResync = true;
        return true;
    }

    /// <summary>
    /// Harvest cmdresult-v3 成功语义：kind/header code0、可解码成功摘要、冻结目标 Empty。
    /// 结构可 GenericDecode；语义失败由调用方走 resync/拒发。不接受 farm-batch-summary。
    /// </summary>
    public static bool TryValidateHarvestV3SuccessSemantics(
        byte[] blob,
        PlayerDeltaBatchNtf? frozenDelta)
    {
        if (!TryDecodeV3(
                blob, out string kind, out int code, out _, out _, out _, out PlayerDeltaBatchNtf? delta, out byte[]? summary)
            || !string.Equals(kind, "Harvest", StringComparison.Ordinal)
            || code != 0
            || summary == null
            || summary.Length == 0
            || !HarvestOpSummaryCodec.TryDecode(summary, out HarvestOpSummaryCodec.Summary? op)
            || !HarvestOpSummaryCodec.IsSuccessfulSettlement(op))
        {
            return false;
        }

        PlayerDeltaBatchNtf? plotsSource = frozenDelta ?? delta;
        if (plotsSource?.Plots == null)
            return false;
        for (int i = 0; i < plotsSource.Plots.Plots.Count; i++)
        {
            PlotData row = plotsSource.Plots.Plots[i];
            if (row.PlotId != op!.PlotId)
                continue;
            return row.State == PlotState.PlotEmpty;
        }

        return false;
    }

    /// <summary>
    /// Gather cmdresult-v3 成功语义：kind/header code0、可解码 gather-summary-v1、
    /// 冻结 Nodes 含唯一目标且 NextReadyTime 与摘要一致。不接受 farm-batch/harvest 摘要。
    /// </summary>
    public static bool TryValidateGatherV3SuccessSemantics(
        byte[] blob,
        PlayerDeltaBatchNtf? frozenDelta)
    {
        if (!TryDecodeV3(
                blob, out string kind, out int code, out _, out _, out _, out PlayerDeltaBatchNtf? delta, out byte[]? summary)
            || !string.Equals(kind, "Gather", StringComparison.Ordinal)
            || code != 0
            || summary == null
            || summary.Length == 0
            || !GatherOpSummaryCodec.TryDecode(summary, out GatherOpSummaryCodec.Summary? op)
            || !GatherOpSummaryCodec.IsSuccessfulSettlement(op))
        {
            return false;
        }

        PlayerDeltaBatchNtf? nodesSource = frozenDelta ?? delta;
        if (nodesSource?.Nodes == null)
            return false;
        NodeData? found = null;
        for (int i = 0; i < nodesSource.Nodes.Nodes.Count; i++)
        {
            NodeData row = nodesSource.Nodes.Nodes[i];
            if (row.NodeId != op!.NodeInstanceId)
                continue;
            if (found != null)
                return false;
            found = row;
        }

        return found != null && found.NextReadyTime == op!.NextReadyTime;
    }

    /// <summary>
    /// ToolUpgrade cmdresult-v3 成功语义：kind/header code0、可解码 tool-upgrade-summary-v1、
    /// 冻结 Tools 含唯一目标且 Level 与摘要一致。不接受 gather/harvest/farm-batch 摘要。
    /// </summary>
    public static bool TryValidateToolUpgradeV3SuccessSemantics(
        byte[] blob,
        PlayerDeltaBatchNtf? frozenDelta)
    {
        if (!TryDecodeV3(
                blob, out string kind, out int code, out _, out _, out _, out PlayerDeltaBatchNtf? delta, out byte[]? summary)
            || !string.Equals(kind, "ToolUpgrade", StringComparison.Ordinal)
            || code != 0
            || summary == null
            || summary.Length == 0
            || !ToolUpgradeOpSummaryCodec.TryDecode(summary, out ToolUpgradeOpSummaryCodec.Summary? op)
            || !ToolUpgradeOpSummaryCodec.IsSuccessfulSettlement(op))
        {
            return false;
        }

        PlayerDeltaBatchNtf? toolsSource = frozenDelta ?? delta;
        if (toolsSource?.Tools == null)
            return false;
        ToolData? found = null;
        for (int i = 0; i < toolsSource.Tools.Tools.Count; i++)
        {
            ToolData row = toolsSource.Tools.Tools[i];
            if (row.ToolId != op!.ToolId)
                continue;
            if (found != null)
                return false;
            found = row;
        }

        return found != null && found.Level == op!.LevelAfter;
    }

    /// <summary>
    /// 批量成功项：Harvest 冻结目标须 Empty；Plant 目标须规范 Growing（含作物元数据），不得仅 Empty 占位。
    /// 冻结 Plots 中任一 plotId 重复 → 歧义，拒绝（不得 first-match）。
    /// 合法无关/未知唯一地块与未知 protobuf 字段可并存。
    /// 仅作用于 <c>farm-batch-summary-v1</c>；不回溯收紧单地块 harvest-summary-v1。
    /// </summary>
    public static bool TryValidateFarmBatchSuccessPlots(
        FarmBatchSummaryCodec.Summary summary,
        PlayerDeltaBatchNtf frozenDelta)
    {
        if (summary == null || frozenDelta?.Plots == null)
            return false;
        if (HasDuplicatePlotIds(frozenDelta.Plots))
            return false;
        bool harvest = summary.Kind == FarmBatchSummaryCodec.KindHarvest;
        for (int i = 0; i < summary.Items.Count; i++)
        {
            FarmBatchSummaryCodec.ItemResult row = summary.Items[i];
            if (row.Code != 0)
                continue;
            bool found = false;
            for (int p = 0; p < frozenDelta.Plots.Plots.Count; p++)
            {
                PlotData plot = frozenDelta.Plots.Plots[p];
                if (plot.PlotId != row.PlotId)
                    continue;
                found = true;
                if (harvest)
                {
                    if (plot.State != PlotState.PlotEmpty || plot.CropId != 0
                        || plot.PlantTime != 0 || plot.ReadyTime != 0
                        || plot.Watered || plot.Fertilized)
                        return false;
                }
                else if (!IsCanonicalPlantedGrowing(plot))
                {
                    return false;
                }

                break;
            }

            if (!found)
                return false;
        }

        return true;
    }

    /// <summary>
    /// farm-batch-summary-v1：header revision/config 与 projection/delta 一致。
    /// 有成功须带 delta；全失败路径由调用方另验二者皆 null。不含投递 scope/op（GenericReplay 无外部 key）。
    /// </summary>
    public static bool TryValidateFarmBatchArchiveAssets(
        long committedRevision,
        int configVersion,
        PlayerFullData? projection,
        PlayerDeltaBatchNtf? frozenDelta,
        bool anySuccess)
    {
        if (committedRevision < 0 || configVersion < 1)
            return false;
        if (!anySuccess)
            return projection == null && frozenDelta == null;
        if (frozenDelta == null)
            return false;
        if (committedRevision <= 0 || frozenDelta.BaseRevision != committedRevision - 1)
            return false;
        if (frozenDelta.NewRevision != committedRevision)
            return false;
        if (frozenDelta.ConfigVersion != configVersion)
            return false;
        if (projection != null && projection.Revision != committedRevision)
            return false;
        return true;
    }

    /// <summary>
    /// Publisher 投递：冻结 delta 的 scope/op 须与实际投递 key 相符（key 可用时）。
    /// </summary>
    public static bool TryValidateFarmBatchDeliveryKey(
        PlayerDeltaBatchNtf frozenDelta,
        long scopeId,
        long opId)
    {
        if (frozenDelta == null)
            return false;
        if (scopeId <= 0 || opId <= 0)
            return true;
        return frozenDelta.OperationScopeId == scopeId && frozenDelta.OpId == opId;
    }

    /// <summary>成功 Plant 冻结目标：规范 Growing + 正 CropId（canonical planted 元数据）。</summary>
    static bool IsCanonicalPlantedGrowing(PlotData plot)
        => plot != null
           && plot.State == PlotState.PlotGrowing
           && plot.CropId > 0
           && plot.PlantTime >= 0
           && plot.ReadyTime > plot.PlantTime
           && !plot.Watered && !plot.Fertilized;

    static bool BatchSummaryKindMatchesArchive(string archiveKind, FarmBatchSummaryCodec.Summary summary)
    {
        if (string.Equals(archiveKind, "Plant", StringComparison.Ordinal))
            return summary.Kind == FarmBatchSummaryCodec.KindPlant;
        if (string.Equals(archiveKind, "Harvest", StringComparison.Ordinal))
            return summary.Kind == FarmBatchSummaryCodec.KindHarvest;
        return false;
    }

    static bool HasDuplicatePlotIds(PlotChangeNtf plots)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < plots.Plots.Count; i++)
        {
            if (!seen.Add(plots.Plots[i].PlotId))
                return true;
        }

        return false;
    }

    static void ValidateHeader(string kind, long committedRevision, int configVersion)
    {
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("kind required", nameof(kind));
        if (committedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(committedRevision));
        if (configVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(configVersion));
    }

    static void WriteHeaderFields(Stream s, string kind, int code, long committedRevision, int configVersion)
    {
        WriteLpUtf8(s, kind);
        WriteI32(s, code);
        WriteI64(s, committedRevision);
        WriteI32(s, configVersion);
    }

    static void ReadHeaderFields(
        Stream ms,
        out string kind,
        out int code,
        out long committedRevision,
        out int configVersion)
    {
        kind = ReadLpUtf8(ms);
        code = ReadI32(ms);
        committedRevision = ReadI64(ms);
        configVersion = ReadI32(ms);
    }

    static void WriteLpUtf8(Stream s, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
        WriteLpBytes(s, utf8);
    }

    static void WriteLpBytes(Stream s, byte[] bytes)
    {
        WriteI32(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    static void WriteI32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        buf[0] = (byte)((value >> 24) & 0xff);
        buf[1] = (byte)((value >> 16) & 0xff);
        buf[2] = (byte)((value >> 8) & 0xff);
        buf[3] = (byte)(value & 0xff);
        s.Write(buf);
    }

    static void WriteI64(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        for (int i = 7; i >= 0; i--)
        {
            buf[7 - i] = (byte)((value >> (i * 8)) & 0xff);
        }
        s.Write(buf);
    }

    static string ReadLpUtf8(Stream s) => Encoding.UTF8.GetString(ReadLpBytes(s));

    static byte[] ReadLpBytes(Stream s)
    {
        int n = ReadI32(s);
        if (n < 0 || n > 1024 * 1024)
            throw new InvalidDataException("lp length");
        byte[] buf = new byte[n];
        int read = 0;
        while (read < n)
        {
            int r = s.Read(buf, read, n - read);
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }
        return buf;
    }

    static int ReadI32(Stream s)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExact(s, buf);
        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }

    static long ReadI64(Stream s)
    {
        Span<byte> buf = stackalloc byte[8];
        ReadExact(s, buf);
        long v = 0;
        for (int i = 0; i < 8; i++)
            v = (v << 8) | buf[i];
        return v;
    }

    static void ReadExact(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf.Slice(off));
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }
    }
}
