using System;
using CloudIsle.Domain.Commands;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Network;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 已提交结果有序投递：同连接 SendQueue 上 Batch → Resp。
/// 不在 DB 事务内 await socket；投递失败只关目标会话，不回滚已 COMMIT。
/// Body 超 64KiB 硬门槛显式关连接，不假部分发送。
/// </summary>
public sealed class CommittedOutcomePublisher
{
    readonly Func<RequestContext, Packet, CancellationToken, Task> _sendAsync;
    readonly Func<RequestContext, string, CancellationToken, Task> _closeAsync;
    readonly CommitFaultProbe? _probe;

    public CommittedOutcomePublisher(
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync,
        Func<RequestContext, string, CancellationToken, Task> closeAsync,
        CommitFaultProbe? probe = null)
    {
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        _closeAsync = closeAsync ?? throw new ArgumentNullException(nameof(closeAsync));
        _probe = probe;
    }

    /// <summary>
    /// 在玩家 lane 内调用：先 Batch（seq=0）再配对 Resp（request seq）。
    /// 拒绝/noop 无 Batch。模型永不消费 Resp 资产字段（仅 code+Ack）；
    /// Plant 成功另附一项冻结 PlotResult；Water/Fertilize 成功填冻结 Plot；
    /// Harvest 成功填冻结 Results/Gained/Exp（摘要 + 目标 Empty，见 <paramref name="targetPlotId"/>）；
    /// Gather 成功填冻结 Gained/RareHit/Node（gather-summary-v1 + 目标 Node，见 <paramref name="targetNodeId"/>）；
    /// ToolUpgrade 成功填冻结 Tool（tool-upgrade-summary-v1 + 目标 Tool，见 <paramref name="targetToolId"/>）。
    /// </summary>
    /// <param name="targetPlotId">
    /// 可选：已通过哈希校验的原 Plant/Water/Fertilize/Harvest 请求 plotId；旧调用者省略则对应字段保持空。
    /// </param>
    /// <param name="targetNodeId">
    /// 可选：已通过哈希校验的原 Gather 请求 node_instance_id；旧调用者省略则仅用摘要/冻结 Nodes。
    /// </param>
    /// <param name="targetToolId">
    /// 可选：已通过哈希校验的原 ToolUpgrade 请求 tool_id；旧调用者省略则仅用摘要/冻结 Tools。
    /// </param>
    public async Task DeliverAsync(
        RequestContext context,
        CommandOutcome outcome,
        string kind,
        long scopeId,
        long opId,
        CancellationToken transportCt,
        int? targetPlotId = null,
        int? targetNodeId = null,
        int? targetToolId = null)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (outcome == null) throw new ArgumentNullException(nameof(outcome));
        if (string.IsNullOrEmpty(kind)) throw new ArgumentException("kind required", nameof(kind));

        // 构造错误在任何投递前暴露；进入发送范围后，异常只影响目标连接。
        IMessage resp = BuildWriteResp(kind, outcome, scopeId, opId, targetPlotId, targetNodeId, targetToolId);
        try
        {
            // 仅在已 Recorded（COMMIT 完成）路径命中；不合成提交。
            if (outcome.Disposition == CommandDisposition.Recorded
                && context.PlayerId > 0 && scopeId > 0 && opId > 0)
            {
                _probe?.Hit(
                    CommitPhase.BeforeDeliverSend,
                    new CommandKey(context.PlayerId, scopeId, opId));
            }

            byte[] respBody = resp.ToByteArray();
            EnsureBodyFits(respBody);
            ushort respId = MsgRegistry.GetId(resp.GetType());
            // 日切系统 Batch 先于外部命令 Batch/Resp（含 Recorded 109）
            for (int i = 0; i < outcome.PrecedingSystemDeltas.Count; i++)
            {
                byte[] sysBody = outcome.PrecedingSystemDeltas[i].ToByteArray();
                EnsureBodyFits(sysBody);
                await _sendAsync(
                        context,
                        new Packet(MsgRegistry.GetId(typeof(PlayerDeltaBatchNtf)), seq: 0, sysBody),
                        transportCt)
                    .ConfigureAwait(false);
            }

            if (outcome.FrozenDelta != null)
            {
                byte[] batchBody = outcome.FrozenDelta.ToByteArray();
                EnsureBodyFits(batchBody);
                var batchPacket = new Packet(
                    MsgRegistry.GetId(typeof(PlayerDeltaBatchNtf)),
                    seq: 0,
                    batchBody);
                await _sendAsync(context, batchPacket, transportCt).ConfigureAwait(false);
            }

            var respPacket = new Packet(respId, context.Seq, respBody);
            await _sendAsync(context, respPacket, transportCt).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "body" || IsBodyOversize(ex))
        {
            await SafeCloseAsync(context, "body exceeds 64KiB frame gate", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Batch 已发、Resp 失败或任意真实发送故障：只关本连接，不回滚 COMMIT、不停 Host。
            await SafeCloseAsync(context, "delivery failed after commit", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Query / Sync 单帧；超限关连接。
    /// <paramref name="seq"/> 缺省用 request seq；系统 Ntf 须显式传 0。
    /// </summary>
    public async Task DeliverSingleAsync(
        RequestContext context,
        ushort msgId,
        IMessage message,
        CancellationToken transportCt,
        uint? seq = null)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (message == null) throw new ArgumentNullException(nameof(message));
        try
        {
            byte[] body = message.ToByteArray();
            EnsureBodyFits(body);
            uint wireSeq = seq ?? context.Seq;
            await _sendAsync(context, new Packet(msgId, wireSeq, body), transportCt).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "body" || IsBodyOversize(ex))
        {
            await SafeCloseAsync(context, "body exceeds 64KiB frame gate", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SafeCloseAsync(context, "delivery failed", CancellationToken.None).ConfigureAwait(false);
        }
    }

    static void EnsureBodyFits(byte[] body)
    {
        if (body.Length > FrameCodec.MaxBodyLength)
            throw new ArgumentOutOfRangeException(nameof(body), "body exceeds 64KiB frame gate");
    }

    static bool IsBodyOversize(ArgumentOutOfRangeException ex)
        => ex.Message.Contains("65536", StringComparison.Ordinal)
           || ex.Message.Contains("64KiB", StringComparison.OrdinalIgnoreCase);

    async Task SafeCloseAsync(RequestContext context, string reason, CancellationToken ct)
    {
        try { await _closeAsync(context, reason, ct).ConfigureAwait(false); }
        catch { /* 关闭本身失败不得冒充 Host 故障 */ }
    }

    internal static IMessage BuildWriteResp(
        string kind,
        CommandOutcome outcome,
        long scopeId,
        long opId,
        int? targetPlotId = null,
        int? targetNodeId = null,
        int? targetToolId = null)
    {
        CommandAck ack = BuildAck(outcome, scopeId, opId);
        long committed = outcome.Details?.CommittedRevision ?? 0;
        int code = outcome.TransportCode;

        return kind switch
        {
            "BagSell" => new BagSellResp { Code = code, CommittedRevision = committed, Ack = ack },
            "BagExpand" => new BagExpandResp
            {
                Code = code,
                // 成功/重放：与 Attr.BagSlots 同源；失败无 Batch 时为 0（proto3 默认）。
                BagSlots = outcome.FrozenDelta?.Attr?.BagSlots ?? 0,
                CommittedRevision = committed,
                Ack = ack
            },
            "GuideStep" => new GuideStepResp { Code = code, CommittedRevision = committed, Ack = ack },
            "PlotUnlock" => new PlotUnlockResp { Code = code, CommittedRevision = committed, Ack = ack },
            "Plant" => BuildPlantResp(outcome, code, committed, ack, targetPlotId),
            "Harvest" => BuildHarvestResp(outcome, code, committed, ack, targetPlotId),
            "Water" => BuildWaterResp(outcome, code, committed, ack, targetPlotId),
            "Fertilize" => BuildFertilizeResp(outcome, code, committed, ack, targetPlotId),
            "Gather" => BuildGatherResp(outcome, code, committed, ack, targetNodeId),
            "ToolUpgrade" => BuildToolUpgradeResp(outcome, code, committed, ack, targetToolId),
            "FacilityBuild" => new FacilityBuildResp { Code = code, CommittedRevision = committed, Ack = ack },
            "FacilityUpgrade" => new FacilityUpgradeResp { Code = code, CommittedRevision = committed, Ack = ack },
            "CraftStart" => new CraftStartResp { Code = code, CommittedRevision = committed, Ack = ack },
            "CraftCollect" => new CraftCollectResp { Code = code, CommittedRevision = committed, Ack = ack },
            "CraftSpeedup" => new CraftSpeedupResp { Code = code, CommittedRevision = committed, Ack = ack },
            "OrderSubmit" => new OrderSubmitResp { Code = code, CommittedRevision = committed, Ack = ack },
            "OrderReroll" => new OrderRerollResp { Code = code, CommittedRevision = committed, Ack = ack },
            "OrderMilestoneClaim" => new OrderMilestoneClaimResp { Code = code, CommittedRevision = committed, Ack = ack },
            "ShopBuy" => new ShopBuyResp { Code = code, CommittedRevision = committed, Ack = ack },
            _ => throw new ArgumentException("unknown write kind: " + kind, nameof(kind))
        };
    }

    /// <summary>
    /// 单地块成功：Results 恰一项（原 plotId / code0 / 冻结 Plot 克隆）。
    /// 批量（farm-batch-summary-v1）：稳定顺序填逐项 code；成功项自冻结 Delta 取 Plot；失败无 Plot。
    /// FrozenDelta.Plots 为全地块列表——只按目标 plotId 提取，不得全塞或按 timestamp 猜。
    /// 单项失败/畸形/未传 plotId：Results 空；不读当前缓存或配置重算。
    /// 截断/残缺新 v3（TryDecodeV3=false 但前缀可识别）禁止回退 legacy 空成功。
    /// </summary>
    static PlantResp BuildPlantResp(
        CommandOutcome outcome,
        int code,
        long committed,
        CommandAck ack,
        int? targetPlotId)
    {
        var resp = new PlantResp { Code = code, CommittedRevision = committed, Ack = ack };
        if (code != 0)
            return resp;

        byte[]? blob = outcome.Details?.ResultBlob;
        // 识别为新 Plant cmdresult-v3：必须完整合法 farm-batch-summary-v1；禁止回退 legacy 空 Results 假成功。
        if (blob != null
            && CommandResultArchive.TryDecodeV3(
                blob, out string kind, out int archivedCode,
                out long archivedRev, out int archivedCfg,
                out PlayerFullData? projection, out PlayerDeltaBatchNtf? archivedDelta, out byte[]? summaryBytes))
        {
            if (!string.Equals(kind, "Plant", StringComparison.Ordinal))
                throw new InvalidOperationException("plant publish requires archive kind Plant");
            if (archivedCode != 0)
                throw new InvalidOperationException("plant success publish requires archive code 0");
            if (summaryBytes == null || summaryBytes.Length == 0)
                throw new InvalidOperationException("plant v3 requires farm-batch-summary before Batch/Resp");
            if (!FarmBatchSummaryCodec.TryDecode(summaryBytes, out FarmBatchSummaryCodec.Summary? batch)
                || !FarmBatchSummaryCodec.IsValidBatchSummary(batch)
                || batch!.Kind != FarmBatchSummaryCodec.KindPlant)
            {
                throw new InvalidOperationException(
                    "plant v3 requires valid plant farm-batch-summary-v1; refuse legacy empty success");
            }

            EnsureReceiptDetailsMatchArchive(outcome, archivedRev, archivedCfg);

            bool anySuccess = false;
            for (int i = 0; i < batch.Items.Count; i++)
            {
                if (batch.Items[i].Code == 0)
                {
                    anySuccess = true;
                    break;
                }
            }

            // A3：全失败须无 delta/projection（含 raw archivedDelta；GenericReplay detach 后仍查原档）。
            // 有成功：冻结 Growing、header/projection/delta revision·config、投递 scope/op 一致。
            PlayerDeltaBatchNtf? publishDelta = outcome.FrozenDelta ?? archivedDelta;
            if (!anySuccess)
            {
                if (publishDelta != null || archivedDelta != null || projection != null
                    || outcome.FrozenDelta != null)
                    throw new InvalidOperationException(
                        "plant all-failed batch requires null delta and projection");
            }
            else if (publishDelta == null
                     || !CommandResultArchive.TryValidateFarmBatchSuccessPlots(batch, publishDelta)
                     || !CommandResultArchive.TryValidateFarmBatchArchiveAssets(
                         archivedRev, archivedCfg, projection, publishDelta, anySuccess: true)
                     || !CommandResultArchive.TryValidateFarmBatchDeliveryKey(
                         publishDelta, ack.OperationScopeId, ack.OpId))
            {
                throw new InvalidOperationException(
                    "plant batch success requires consistent Growing plots/header/delivery key");
            }

            for (int i = 0; i < batch.Items.Count; i++)
            {
                FarmBatchSummaryCodec.ItemResult row = batch.Items[i];
                var pr = new PlotResult { PlotId = row.PlotId, Code = row.Code };
                if (row.Code == 0)
                    pr.Plot = RequireFrozenTargetPlot(outcome, row.PlotId, "plant").Clone();
                resp.Results.Add(pr);
            }

            return resp;
        }

        // 前缀可识别的新 v3 但完整解码失败（截断等）：不得当 legacy 空 Results。
        if (blob != null
            && CommandResultArchive.LooksLikeFormatVersion(blob, CommandResultArchive.FormatVersionV3))
        {
            throw new InvalidOperationException(
                "plant truncated/corrupt v3 must not fall back to legacy empty success");
        }

        // 合法旧 v1/v2 或 null-blob 单元路径：保持既有单目标/空 Results 行为。
        if (targetPlotId is not int plantId || plantId <= 0)
            return resp;

        PlotData frozen = RequireFrozenTargetPlot(outcome, plantId, "plant");
        resp.Results.Add(new PlotResult
        {
            PlotId = plantId,
            Code = 0,
            Plot = frozen.Clone()
        });
        return resp;
    }

    /// <summary>
    /// Water 成功：Plot = 冻结目标地块克隆。Delta.Plots 全表——只按原请求 plotId 提取。
    /// 失败/未传 plotId：Plot 空（兼容旧调用）；不读活缓存。
    /// </summary>
    static WaterResp BuildWaterResp(
        CommandOutcome outcome,
        int code,
        long committed,
        CommandAck ack,
        int? targetPlotId)
    {
        var resp = new WaterResp { Code = code, CommittedRevision = committed, Ack = ack };
        if (code != 0 || targetPlotId is not int waterId || waterId <= 0)
            return resp;

        resp.Plot = RequireFrozenTargetPlot(outcome, waterId, "water").Clone();
        return resp;
    }

    /// <summary>
    /// Fertilize 成功：Plot = 冻结目标地块克隆。Delta.Plots 全表——只按原请求 plotId 提取。
    /// 失败/未传 plotId：Plot 空（兼容旧调用）；不读活缓存。
    /// </summary>
    static FertilizeResp BuildFertilizeResp(
        CommandOutcome outcome,
        int code,
        long committed,
        CommandAck ack,
        int? targetPlotId)
    {
        var resp = new FertilizeResp { Code = code, CommittedRevision = committed, Ack = ack };
        if (code != 0 || targetPlotId is not int fertId || fertId <= 0)
            return resp;

        resp.Plot = RequireFrozenTargetPlot(outcome, fertId, "fertilize").Clone();
        return resp;
    }

    /// <summary>
    /// Harvest 顶层 code0：单地块走 harvest-summary-v1；批量走 farm-batch-summary-v1。
    /// 失败项无假 Plot；Gained/Exp 仅自冻结摘要聚合；不得从后态 bag/exp 倒算。
    /// 缺/坏摘要在任何 Batch/Resp 发送前抛错；旧 v1/v2 严格兼容仍空 Results。
    /// </summary>
    static HarvestResp BuildHarvestResp(
        CommandOutcome outcome,
        int code,
        long committed,
        CommandAck ack,
        int? targetPlotId)
    {
        var resp = new HarvestResp { Code = code, CommittedRevision = committed, Ack = ack };
        if (code != 0)
            return resp;

        byte[]? blob = outcome.Details?.ResultBlob;
        if (blob == null)
        {
            // 无归档（单元路径）：保持空 Results；生产成功路径必有 blob。
            return resp;
        }

        if (CommandResultArchive.TryDecodeV3(
                blob, out string kind, out int archivedCode,
                out long archivedRev, out int archivedCfg,
                out PlayerFullData? projection, out PlayerDeltaBatchNtf? archivedDelta,
                out byte[]? summaryBytes))
        {
            if (!string.Equals(kind, "Harvest", StringComparison.Ordinal))
                throw new InvalidOperationException("harvest publish requires archive kind Harvest");
            if (archivedCode != 0)
                throw new InvalidOperationException("harvest success publish requires archive code 0");
            if (summaryBytes == null || summaryBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "harvest v3 requires operation summary before Batch/Resp");
            }

            if (FarmBatchSummaryCodec.TryDecode(summaryBytes, out FarmBatchSummaryCodec.Summary? batch)
                && FarmBatchSummaryCodec.IsValidBatchSummary(batch)
                && batch!.Kind == FarmBatchSummaryCodec.KindHarvest)
            {
                EnsureReceiptDetailsMatchArchive(outcome, archivedRev, archivedCfg);

                bool anySuccess = false;
                for (int i = 0; i < batch.Items.Count; i++)
                {
                    if (batch.Items[i].Code == 0)
                    {
                        anySuccess = true;
                        break;
                    }
                }

                // 全失败：即使 GenericReplay 已 detach FrozenDelta，仍须查原 archivedDelta 拒夹带资产。
                PlayerDeltaBatchNtf? publishDelta = outcome.FrozenDelta ?? archivedDelta;
                if (!anySuccess)
                {
                    if (outcome.FrozenDelta != null || archivedDelta != null || projection != null)
                        throw new InvalidOperationException(
                            "harvest all-failed batch requires null delta and projection in raw archive");
                }
                else if (publishDelta == null
                         || !CommandResultArchive.TryValidateFarmBatchSuccessPlots(batch, publishDelta)
                         || !CommandResultArchive.TryValidateFarmBatchArchiveAssets(
                             archivedRev, archivedCfg, projection, publishDelta, anySuccess: true)
                         || !CommandResultArchive.TryValidateFarmBatchDeliveryKey(
                             publishDelta, ack.OperationScopeId, ack.OpId))
                {
                    throw new InvalidOperationException(
                        "harvest batch success requires consistent plots/header/delivery key");
                }

                for (int i = 0; i < batch.Items.Count; i++)
                {
                    FarmBatchSummaryCodec.ItemResult row = batch.Items[i];
                    var pr = new PlotResult { PlotId = row.PlotId, Code = row.Code };
                    if (row.Code == 0)
                    {
                        PlotData frozen = RequireFrozenTargetPlot(outcome, row.PlotId, "harvest");
                        if (frozen.State != PlotState.PlotEmpty)
                            throw new InvalidOperationException("harvest frozen target must be Empty");
                        pr.Plot = frozen.Clone();
                    }

                    resp.Results.Add(pr);
                }

                for (int i = 0; i < batch.AggregateGained.Count; i++)
                {
                    FarmBatchSummaryCodec.ItemGain g = batch.AggregateGained[i];
                    resp.Gained.Add(new ItemStack { ItemId = g.ItemId, Count = g.Count });
                }

                resp.ExpGained = batch.AggregateExp;
                return resp;
            }

            // 单地块 harvest-summary-v1：保持原严格语义；不套用 farm-batch revision/config/scope 新约束。
            if (!HarvestOpSummaryCodec.TryDecode(summaryBytes, out HarvestOpSummaryCodec.Summary? summary)
                || !HarvestOpSummaryCodec.IsSuccessfulSettlement(summary))
            {
                throw new InvalidOperationException(
                    "harvest v3 requires valid harvest-summary-v1 before Batch/Resp");
            }

            int plotId = summary!.PlotId;
            if (targetPlotId is int wired && wired > 0 && wired != plotId)
                throw new InvalidOperationException("harvest summary plotId mismatches wire target");

            PlotData singleFrozen = RequireFrozenTargetPlot(outcome, plotId, "harvest");
            if (singleFrozen.State != PlotState.PlotEmpty)
                throw new InvalidOperationException("harvest frozen target must be Empty");

            resp.Results.Add(new PlotResult
            {
                PlotId = plotId,
                Code = summary.PlotResultCode,
                Plot = singleFrozen.Clone()
            });
            for (int i = 0; i < summary.Gained.Count; i++)
            {
                HarvestOpSummaryCodec.ItemGain g = summary.Gained[i];
                resp.Gained.Add(new ItemStack { ItemId = g.ItemId, Count = g.Count });
            }

            resp.ExpGained = summary.ExpGained;
            return resp;
        }

        // 旧 v1/v2（严格 EOF）：保持空 Results/Gained（兼容历史）。不得把畸形 v3 误判到此分支。
        if (CommandResultArchive.TryDecodeV2(blob, out _, out _, out _, out _, out _, out _)
            || CommandResultArchive.TryDecodeV1(blob, out _, out _, out _, out _, out _))
        {
            return resp;
        }

        throw new InvalidOperationException("harvest success archive unrecognized; refuse fake publish");
    }

    /// <summary>
    /// Gather 顶层 code0：走 gather-summary-v1；Gained/RareHit/Node 仅自冻结摘要+Nodes。
    /// 缺/坏摘要在任何 Batch/Resp 发送前抛错；旧 v1/v2 严格兼容仍空 Gained/Node。
    /// </summary>
    static GatherResp BuildGatherResp(
        CommandOutcome outcome,
        int code,
        long committed,
        CommandAck ack,
        int? targetNodeId)
    {
        var resp = new GatherResp { Code = code, CommittedRevision = committed, Ack = ack };
        if (code != 0)
            return resp;

        byte[]? blob = outcome.Details?.ResultBlob;
        if (blob == null)
            return resp;

        if (CommandResultArchive.TryDecodeV3(
                blob, out string kind, out int archivedCode,
                out long archivedRev, out int archivedCfg,
                out _, out _, out byte[]? summaryBytes))
        {
            if (!string.Equals(kind, "Gather", StringComparison.Ordinal))
                throw new InvalidOperationException("gather publish requires archive kind Gather");
            if (archivedCode != 0)
                throw new InvalidOperationException("gather success publish requires archive code 0");
            if (summaryBytes == null || summaryBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "gather v3 requires operation summary before Batch/Resp");
            }

            if (!GatherOpSummaryCodec.TryDecode(summaryBytes, out GatherOpSummaryCodec.Summary? summary)
                || !GatherOpSummaryCodec.IsSuccessfulSettlement(summary))
            {
                throw new InvalidOperationException(
                    "gather v3 requires valid gather-summary-v1 before Batch/Resp");
            }

            EnsureReceiptDetailsMatchArchive(outcome, archivedRev, archivedCfg);

            int nodeId = summary!.NodeInstanceId;
            if (targetNodeId is int wired && wired > 0 && wired != nodeId)
                throw new InvalidOperationException("gather summary nodeId mismatches wire target");

            NodeData frozen = RequireFrozenTargetNode(outcome, nodeId, "gather");
            if (frozen.NextReadyTime != summary.NextReadyTime)
                throw new InvalidOperationException("gather frozen node nextReady mismatches summary");

            for (int i = 0; i < summary.Gained.Count; i++)
            {
                GatherOpSummaryCodec.ItemGain g = summary.Gained[i];
                resp.Gained.Add(new ItemStack { ItemId = g.ItemId, Count = g.Count });
            }

            resp.RareHit = summary.RareHit;
            resp.Node = frozen.Clone();
            return resp;
        }

        if (CommandResultArchive.LooksLikeFormatVersion(blob, CommandResultArchive.FormatVersionV3))
        {
            throw new InvalidOperationException(
                "gather truncated/corrupt v3 must not fall back to legacy empty success");
        }

        if (CommandResultArchive.TryDecodeV2(blob, out _, out _, out _, out _, out _, out _)
            || CommandResultArchive.TryDecodeV1(blob, out _, out _, out _, out _, out _))
        {
            return resp;
        }

        throw new InvalidOperationException("gather success archive unrecognized; refuse fake publish");
    }

    /// <summary>
    /// ToolUpgrade 顶层 code0：自 tool-upgrade-summary-v1 + 冻结 Tools 填 Tool；
    /// 旧 v1/v2 空 Tool 兼容；截断 v3 禁回退 legacy 空成功。
    /// </summary>
    static ToolUpgradeResp BuildToolUpgradeResp(
        CommandOutcome outcome,
        int code,
        long committed,
        CommandAck ack,
        int? targetToolId)
    {
        var resp = new ToolUpgradeResp { Code = code, CommittedRevision = committed, Ack = ack };
        if (code != 0)
            return resp;

        byte[]? blob = outcome.Details?.ResultBlob;
        if (blob == null)
            return resp;

        if (CommandResultArchive.TryDecodeV3(
                blob, out string kind, out int archivedCode,
                out long archivedRev, out int archivedCfg,
                out _, out _, out byte[]? summaryBytes))
        {
            if (!string.Equals(kind, "ToolUpgrade", StringComparison.Ordinal))
                throw new InvalidOperationException("tool-upgrade publish requires archive kind ToolUpgrade");
            if (archivedCode != 0)
                throw new InvalidOperationException("tool-upgrade success publish requires archive code 0");
            if (summaryBytes == null || summaryBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "tool-upgrade v3 requires operation summary before Batch/Resp");
            }

            if (!ToolUpgradeOpSummaryCodec.TryDecode(summaryBytes, out ToolUpgradeOpSummaryCodec.Summary? summary)
                || !ToolUpgradeOpSummaryCodec.IsSuccessfulSettlement(summary))
            {
                throw new InvalidOperationException(
                    "tool-upgrade v3 requires valid tool-upgrade-summary-v1 before Batch/Resp");
            }

            EnsureReceiptDetailsMatchArchive(outcome, archivedRev, archivedCfg);

            int toolId = summary!.ToolId;
            if (targetToolId is int wired && wired > 0 && wired != toolId)
                throw new InvalidOperationException("tool-upgrade summary toolId mismatches wire target");

            ToolData frozen = RequireFrozenTargetTool(outcome, toolId, "tool-upgrade");
            if (frozen.Level != summary.LevelAfter)
                throw new InvalidOperationException("tool-upgrade frozen tool level mismatches summary");

            resp.Tool = frozen.Clone();
            return resp;
        }

        if (CommandResultArchive.LooksLikeFormatVersion(blob, CommandResultArchive.FormatVersionV3))
        {
            throw new InvalidOperationException(
                "tool-upgrade truncated/corrupt v3 must not fall back to legacy empty success");
        }

        if (CommandResultArchive.TryDecodeV2(blob, out _, out _, out _, out _, out _, out _)
            || CommandResultArchive.TryDecodeV1(blob, out _, out _, out _, out _, out _))
        {
            return resp;
        }

        throw new InvalidOperationException("tool-upgrade success archive unrecognized; refuse fake publish");
    }

    /// <summary>farm-batch：ReceiptDetails revision/config 须与归档头一致（Details 存在时）。</summary>
    static void EnsureReceiptDetailsMatchArchive(CommandOutcome outcome, long archivedRev, int archivedCfg)
    {
        ReceiptDetails? details = outcome.Details;
        if (details == null)
            return;
        if (details.CommittedRevision != archivedRev || details.ConfigVersion != archivedCfg)
        {
            throw new InvalidOperationException(
                "farm-batch ReceiptDetails revision/config must match archive header");
        }
    }

    static PlotData RequireFrozenTargetPlot(CommandOutcome outcome, int targetId, string kindLabel)
    {
        PlotChangeNtf? plotsNtf = outcome.FrozenDelta?.Plots;
        if (plotsNtf == null)
            throw new InvalidOperationException(kindLabel + " success requires frozen plots delta");

        PlotData? found = null;
        for (int i = 0; i < plotsNtf.Plots.Count; i++)
        {
            PlotData row = plotsNtf.Plots[i];
            if (row.PlotId != targetId)
                continue;
            if (found != null)
                throw new InvalidOperationException(
                    kindLabel + " frozen plots duplicate plotId; refuse first-match ambiguity");
            found = row;
        }

        if (found == null)
            throw new InvalidOperationException(kindLabel + " frozen plots missing target plotId");
        return found;
    }

    static NodeData RequireFrozenTargetNode(CommandOutcome outcome, int targetId, string kindLabel)
    {
        NodeChangeNtf? nodesNtf = outcome.FrozenDelta?.Nodes;
        if (nodesNtf == null)
            throw new InvalidOperationException(kindLabel + " success requires frozen nodes delta");

        NodeData? found = null;
        for (int i = 0; i < nodesNtf.Nodes.Count; i++)
        {
            NodeData row = nodesNtf.Nodes[i];
            if (row.NodeId != targetId)
                continue;
            if (found != null)
                throw new InvalidOperationException(
                    kindLabel + " frozen nodes duplicate nodeId; refuse first-match ambiguity");
            found = row;
        }

        if (found == null)
            throw new InvalidOperationException(kindLabel + " frozen nodes missing target nodeId");
        return found;
    }

    static ToolData RequireFrozenTargetTool(CommandOutcome outcome, int targetId, string kindLabel)
    {
        ToolChangeNtf? toolsNtf = outcome.FrozenDelta?.Tools;
        if (toolsNtf == null)
            throw new InvalidOperationException(kindLabel + " success requires frozen tools delta");

        ToolData? found = null;
        for (int i = 0; i < toolsNtf.Tools.Count; i++)
        {
            ToolData row = toolsNtf.Tools[i];
            if (row.ToolId != targetId)
                continue;
            if (found != null)
                throw new InvalidOperationException(
                    kindLabel + " frozen tools duplicate toolId; refuse first-match ambiguity");
            found = row;
        }

        if (found == null)
            throw new InvalidOperationException(kindLabel + " frozen tools missing target toolId");
        return found;
    }

    static CommandAck BuildAck(CommandOutcome outcome, long scopeId, long opId)
    {
        var ack = new CommandAck
        {
            OperationScopeId = scopeId > 0 ? scopeId : 0,
            OpId = opId > 0 ? opId : 0,
            Disposition = outcome.Disposition
        };
        if (outcome.HasAuthoritativeNext && outcome.NextOpId >= 1)
        {
            ack.NextOpId = outcome.NextOpId;
            ack.ScopeState = outcome.ScopeOpen
                ? OperationScopeState.Open
                : OperationScopeState.Closed;
        }
        return ack;
    }
}
