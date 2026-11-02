using System;
using CloudIsle.Domain.Commands;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 19 固定 kind 写入口：形状校验 → 同 lane Coordinator → Publisher（Batch→Resp）。
/// 未注册规则由 Coordinator 在查账本后对未记录键返回 504；已记账仍可重放。
/// </summary>
public sealed class GameplayWriteHandler
{
    readonly PlayerQueue _queue;
    readonly SessionRegistry _sessions;
    readonly GameplayCommandCoordinator _coordinator;
    readonly CommittedOutcomePublisher _publisher;
    readonly CommitFaultProbe? _probe;

    public GameplayWriteHandler(
        PlayerQueue queue,
        SessionRegistry sessions,
        GameplayCommandCoordinator coordinator,
        CommittedOutcomePublisher publisher,
        CommitFaultProbe? probe = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _probe = probe;
    }

    public Task HandleBagSellAsync(RequestContext ctx, BagSellReq req, CancellationToken ct)
        => HandleAsync(ctx, "BagSell", req, ct);
    public Task HandleBagExpandAsync(RequestContext ctx, BagExpandReq req, CancellationToken ct)
        => HandleAsync(ctx, "BagExpand", req, ct);
    public Task HandleGuideStepAsync(RequestContext ctx, GuideStepReq req, CancellationToken ct)
        => HandleAsync(ctx, "GuideStep", req, ct);
    public Task HandlePlotUnlockAsync(RequestContext ctx, PlotUnlockReq req, CancellationToken ct)
        => HandleAsync(ctx, "PlotUnlock", req, ct);
    public Task HandlePlantAsync(RequestContext ctx, PlantReq req, CancellationToken ct)
        => HandleAsync(ctx, "Plant", req, ct);
    public Task HandleHarvestAsync(RequestContext ctx, HarvestReq req, CancellationToken ct)
        => HandleAsync(ctx, "Harvest", req, ct);
    public Task HandleWaterAsync(RequestContext ctx, WaterReq req, CancellationToken ct)
        => HandleAsync(ctx, "Water", req, ct);
    public Task HandleFertilizeAsync(RequestContext ctx, FertilizeReq req, CancellationToken ct)
        => HandleAsync(ctx, "Fertilize", req, ct);
    public Task HandleGatherAsync(RequestContext ctx, GatherReq req, CancellationToken ct)
        => HandleAsync(ctx, "Gather", req, ct);
    public Task HandleToolUpgradeAsync(RequestContext ctx, ToolUpgradeReq req, CancellationToken ct)
        => HandleAsync(ctx, "ToolUpgrade", req, ct);
    public Task HandleFacilityBuildAsync(RequestContext ctx, FacilityBuildReq req, CancellationToken ct)
        => HandleAsync(ctx, "FacilityBuild", req, ct);
    public Task HandleFacilityUpgradeAsync(RequestContext ctx, FacilityUpgradeReq req, CancellationToken ct)
        => HandleAsync(ctx, "FacilityUpgrade", req, ct);
    public Task HandleCraftStartAsync(RequestContext ctx, CraftStartReq req, CancellationToken ct)
        => HandleAsync(ctx, "CraftStart", req, ct);
    public Task HandleCraftCollectAsync(RequestContext ctx, CraftCollectReq req, CancellationToken ct)
        => HandleAsync(ctx, "CraftCollect", req, ct);
    public Task HandleCraftSpeedupAsync(RequestContext ctx, CraftSpeedupReq req, CancellationToken ct)
        => HandleAsync(ctx, "CraftSpeedup", req, ct);
    public Task HandleOrderSubmitAsync(RequestContext ctx, OrderSubmitReq req, CancellationToken ct)
        => HandleAsync(ctx, "OrderSubmit", req, ct);
    public Task HandleOrderRerollAsync(RequestContext ctx, OrderRerollReq req, CancellationToken ct)
        => HandleAsync(ctx, "OrderReroll", req, ct);
    public Task HandleOrderMilestoneClaimAsync(RequestContext ctx, OrderMilestoneClaimReq req, CancellationToken ct)
        => HandleAsync(ctx, "OrderMilestoneClaim", req, ct);
    public Task HandleShopBuyAsync(RequestContext ctx, ShopBuyReq req, CancellationToken ct)
        => HandleAsync(ctx, "ShopBuy", req, ct);

    async Task HandleAsync(RequestContext context, string kind, IMessage request, CancellationToken admissionCt)
    {
        if (context.PlayerId <= 0)
        {
            await DeliverNotAcceptedAsync(context, kind, request, 102, hasNext: false, 0, false, admissionCt)
                .ConfigureAwait(false);
            return;
        }

        if (!_sessions.IsCurrent(context.PlayerId, context.SessionId))
        {
            await DeliverNotAcceptedAsync(context, kind, request, 102, hasNext: false, 0, false, admissionCt)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            CommandEnvelopeFactory.RejectUnknownWireFields(request);
        }
        catch (ArgumentException)
        {
            await DeliverNotAcceptedAsync(context, kind, request, 101, hasNext: false, 0, false, admissionCt)
                .ConfigureAwait(false);
            return;
        }

        CommandEnvelope envelope;
        try
        {
            envelope = CommandEnvelopeFactory.FromRequest(context.PlayerId, kind, request);
        }
        catch (ArgumentException)
        {
            await DeliverNotAcceptedAsync(context, kind, request, 101, hasNext: false, 0, false, admissionCt)
                .ConfigureAwait(false);
            return;
        }

        var caller = new SessionIdentity(context.PlayerId, context.SessionId);
        await _queue.EnqueueAsync(context.PlayerId, async laneCt =>
        {
            _ = laneCt;
            // 队内完成提交与有序发布，lane 释放前不得让后续写越过 Batch/Resp。
            // 规则是否注册由 Coordinator 在 Lookup 之后判定，避免挡掉已记账重放。
            CommandOutcome outcome = await _coordinator
                .ExecuteInLaneAsync(caller, envelope, admissionCt)
                .ConfigureAwait(false);
            // 真实 COMMIT 之后、首个 Socket 发送之前：强杀 barrier / 丢响应挂点。
            if (outcome.Disposition == CommandDisposition.Recorded)
                _probe?.Hit(CommitPhase.AfterCommitBeforeDeliver, envelope.Key);
            // Plant/Water/Fertilize/Harvest：已哈希校验的原请求 target plotId → 发布端从冻结 Batch/摘要提取。
            // Gather：已哈希校验的 node_instance_id → 发布端交叉校验摘要/冻结 Node。
            // ToolUpgrade：已哈希校验的 tool_id → 发布端交叉校验摘要/冻结 Tool。
            int? targetPlotId = TryReadValidatedTargetPlotId(kind, request);
            int? targetNodeId = TryReadValidatedTargetNodeId(kind, request);
            int? targetToolId = TryReadValidatedTargetToolId(kind, request);
            await _publisher
                .DeliverAsync(
                    context, outcome, kind, envelope.Key.ScopeId, envelope.Key.OpId,
                    CancellationToken.None, targetPlotId, targetNodeId, targetToolId)
                .ConfigureAwait(false);
        }, admissionCt).ConfigureAwait(false);
    }

    /// <summary>
    /// 单地块 Plant/Harvest：返回已哈希校验的唯一 plotId 供 Publisher 从冻结 Delta 提取。
    /// 批量（2..24）返回 null——Publisher 必须走 farm-batch-summary，不得静默丢 Results。
    /// Water/Fertilize：plotId&gt;0。畸形/非正 → null，不向 Resp 捏造成功 Plot。
    /// </summary>
    static int? TryReadValidatedTargetPlotId(string kind, IMessage request)
    {
        if (string.Equals(kind, "Plant", StringComparison.Ordinal))
        {
            if (request is not PlantReq plant)
                return null;
            if (plant.Items.Count != 1)
                return null;
            int plantPlot = plant.Items[0].PlotId;
            return plantPlot > 0 ? plantPlot : null;
        }

        if (string.Equals(kind, "Water", StringComparison.Ordinal))
        {
            if (request is not WaterReq water)
                return null;
            return water.PlotId > 0 ? water.PlotId : null;
        }

        if (string.Equals(kind, "Fertilize", StringComparison.Ordinal))
        {
            if (request is not FertilizeReq fert)
                return null;
            return fert.PlotId > 0 ? fert.PlotId : null;
        }

        if (string.Equals(kind, "Harvest", StringComparison.Ordinal))
        {
            if (request is not HarvestReq harvest)
                return null;
            if (harvest.PlotIds.Count != 1)
                return null;
            int harvestPlot = harvest.PlotIds[0];
            return harvestPlot > 0 ? harvestPlot : null;
        }

        return null;
    }

    /// <summary>Gather：返回已哈希校验的 node_instance_id；畸形/非正 → null。</summary>
    static int? TryReadValidatedTargetNodeId(string kind, IMessage request)
    {
        if (!string.Equals(kind, "Gather", StringComparison.Ordinal))
            return null;
        if (request is not GatherReq gather)
            return null;
        return gather.NodeId > 0 ? gather.NodeId : null;
    }

    /// <summary>ToolUpgrade：返回已哈希校验的 tool_id；畸形/非正 → null。</summary>
    static int? TryReadValidatedTargetToolId(string kind, IMessage request)
    {
        if (!string.Equals(kind, "ToolUpgrade", StringComparison.Ordinal))
            return null;
        if (request is not ToolUpgradeReq tool)
            return null;
        return tool.ToolId > 0 ? tool.ToolId : null;
    }

    async Task DeliverNotAcceptedAsync(
        RequestContext context,
        string kind,
        IMessage request,
        int code,
        bool hasNext,
        long next,
        bool open,
        CancellationToken ct)
    {
        (long scopeId, long opId) = ReadScopeOp(request);
        var outcome = new CommandOutcome(
            CommandDisposition.NotAccepted,
            code,
            next,
            open,
            hasNext,
            details: null,
            isReplay: false,
            ruleInvocations: 0,
            rngCalls: 0,
            message: "not accepted");
        await _publisher.DeliverAsync(context, outcome, kind, scopeId, opId, ct).ConfigureAwait(false);
    }

    static (long ScopeId, long OpId) ReadScopeOp(IMessage request) => request switch
    {
        BagSellReq r => (r.OperationScopeId, r.OpId),
        BagExpandReq r => (r.OperationScopeId, r.OpId),
        GuideStepReq r => (r.OperationScopeId, r.OpId),
        PlotUnlockReq r => (r.OperationScopeId, r.OpId),
        PlantReq r => (r.OperationScopeId, r.OpId),
        HarvestReq r => (r.OperationScopeId, r.OpId),
        WaterReq r => (r.OperationScopeId, r.OpId),
        FertilizeReq r => (r.OperationScopeId, r.OpId),
        GatherReq r => (r.OperationScopeId, r.OpId),
        ToolUpgradeReq r => (r.OperationScopeId, r.OpId),
        FacilityBuildReq r => (r.OperationScopeId, r.OpId),
        FacilityUpgradeReq r => (r.OperationScopeId, r.OpId),
        CraftStartReq r => (r.OperationScopeId, r.OpId),
        CraftCollectReq r => (r.OperationScopeId, r.OpId),
        CraftSpeedupReq r => (r.OperationScopeId, r.OpId),
        OrderSubmitReq r => (r.OperationScopeId, r.OpId),
        OrderRerollReq r => (r.OperationScopeId, r.OpId),
        OrderMilestoneClaimReq r => (r.OperationScopeId, r.OpId),
        ShopBuyReq r => (r.OperationScopeId, r.OpId),
        _ => (0, 0)
    };
}
