using CloudIsle.Config;
using CloudIsle.Domain.Clock;
using CloudIsle.Domain.Commands;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Infrastructure.Commands;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>Host 玩法依赖显式清单；无 ServiceLocator。</summary>
public sealed class GameplayDependencies
{
    public required PlayerQueue Queue { get; init; }
    public required SqliteStore Store { get; init; }
    public required SessionRegistry Sessions { get; init; }
    public required PlayerStore Players { get; init; }
    public required GameplayCommitCore CommitCore { get; init; }
    public required int HostConfigVersion { get; init; }
    public GameConfigSnapshot? Config { get; init; }
    public Func<long>? UtcNowMs { get; init; }
    public IGameClock? Clock { get; init; }
    public SystemWriteCoordinator? SystemWrites { get; init; }
    /// <summary>
    /// 测试注入：与生产表合并（同 Kind 覆盖）。生产 BagSell/BagExpand/ShopBuy 由 GameplayRuleComposition 始终注册；
    /// 禁止将 FakeGoldBump 写入生产默认表。
    /// </summary>
    public IEnumerable<IGameplayRule>? Rules { get; init; }
    /// <summary>测试专用提交探针；生产保持 null。</summary>
    public CommitFaultProbe? TestCommitProbe { get; init; }
    public required Func<RequestContext, Packet, CancellationToken, Task> SendAsync { get; init; }
    public required Func<RequestContext, string, CancellationToken, Task> CloseAsync { get; init; }
}

/// <summary>装配后的玩法服务面。</summary>
public sealed class GameplayServices
{
    public required GameplayRuleDispatcher Rules { get; init; }
    public required CommandReceiptRepository Receipts { get; init; }
    public required GameplayCommandCoordinator Coordinator { get; init; }
    public required GameplayQueryService Queries { get; init; }
    public required CommittedOutcomePublisher Publisher { get; init; }
    public required GameplayWriteHandler Writes { get; init; }
}

/// <summary>
/// 玩法组合模块：Create + RegisterHandlers。
/// 生产始终注册正式 BagSell/BagExpand/ShopBuy/PlotUnlock/Plant/Water/Fertilize/Harvest/Gather/ToolUpgrade；
/// 其余未实现 kind（如 FacilityBuild）仍 504。测试经 Rules 注入可覆盖同 Kind（见 GameplayRuleComposition）。
/// </summary>
public static class GameplayModule
{
    public static GameplayServices Create(GameplayDependencies deps)
    {
        if (deps == null) throw new ArgumentNullException(nameof(deps));
        // 生产十种规则始终在；deps.Rules 为测试覆盖/追加（同 Kind 覆盖，不抛重复）。
        IReadOnlyList<IGameplayRule> merged = GameplayRuleComposition.MergeProductionWithTestOverrides(deps.Rules);
        var rules = new GameplayRuleDispatcher(merged);
        var receipts = new CommandReceiptRepository(deps.Store);
        var coordinator = new GameplayCommandCoordinator(
            deps.Queue, deps.Sessions, deps.Players, receipts, rules, deps.CommitCore,
            deps.HostConfigVersion, deps.Config, deps.UtcNowMs, deps.Clock, deps.SystemWrites);
        var queries = new GameplayQueryService(
            deps.Queue, deps.Sessions, deps.Players, receipts, deps.Store, deps.UtcNowMs,
            deps.Clock, deps.SystemWrites);
        var publisher = new CommittedOutcomePublisher(
            deps.SendAsync, deps.CloseAsync, deps.TestCommitProbe);
        var writes = new GameplayWriteHandler(
            deps.Queue, deps.Sessions, coordinator, publisher, deps.TestCommitProbe);
        return new GameplayServices
        {
            Rules = rules,
            Receipts = receipts,
            Coordinator = coordinator,
            Queries = queries,
            Publisher = publisher,
            Writes = writes
        };
    }

    public static void RegisterHandlers(MessageRouter router, GameplayServices services)
    {
        if (router == null) throw new ArgumentNullException(nameof(router));
        if (services == null) throw new ArgumentNullException(nameof(services));

        GameplayWriteHandler w = services.Writes;
        router.Register<BagSellReq>(MsgRegistry.GetId(typeof(BagSellReq)), w.HandleBagSellAsync);
        router.Register<BagExpandReq>(MsgRegistry.GetId(typeof(BagExpandReq)), w.HandleBagExpandAsync);
        router.Register<GuideStepReq>(MsgRegistry.GetId(typeof(GuideStepReq)), w.HandleGuideStepAsync);
        router.Register<PlotUnlockReq>(MsgRegistry.GetId(typeof(PlotUnlockReq)), w.HandlePlotUnlockAsync);
        router.Register<PlantReq>(MsgRegistry.GetId(typeof(PlantReq)), w.HandlePlantAsync);
        router.Register<HarvestReq>(MsgRegistry.GetId(typeof(HarvestReq)), w.HandleHarvestAsync);
        router.Register<WaterReq>(MsgRegistry.GetId(typeof(WaterReq)), w.HandleWaterAsync);
        router.Register<FertilizeReq>(MsgRegistry.GetId(typeof(FertilizeReq)), w.HandleFertilizeAsync);
        router.Register<GatherReq>(MsgRegistry.GetId(typeof(GatherReq)), w.HandleGatherAsync);
        router.Register<ToolUpgradeReq>(MsgRegistry.GetId(typeof(ToolUpgradeReq)), w.HandleToolUpgradeAsync);
        router.Register<FacilityBuildReq>(MsgRegistry.GetId(typeof(FacilityBuildReq)), w.HandleFacilityBuildAsync);
        router.Register<FacilityUpgradeReq>(MsgRegistry.GetId(typeof(FacilityUpgradeReq)), w.HandleFacilityUpgradeAsync);
        router.Register<CraftStartReq>(MsgRegistry.GetId(typeof(CraftStartReq)), w.HandleCraftStartAsync);
        router.Register<CraftCollectReq>(MsgRegistry.GetId(typeof(CraftCollectReq)), w.HandleCraftCollectAsync);
        router.Register<CraftSpeedupReq>(MsgRegistry.GetId(typeof(CraftSpeedupReq)), w.HandleCraftSpeedupAsync);
        router.Register<OrderSubmitReq>(MsgRegistry.GetId(typeof(OrderSubmitReq)), w.HandleOrderSubmitAsync);
        router.Register<OrderRerollReq>(MsgRegistry.GetId(typeof(OrderRerollReq)), w.HandleOrderRerollAsync);
        router.Register<OrderMilestoneClaimReq>(
            MsgRegistry.GetId(typeof(OrderMilestoneClaimReq)), w.HandleOrderMilestoneClaimAsync);
        router.Register<ShopBuyReq>(MsgRegistry.GetId(typeof(ShopBuyReq)), w.HandleShopBuyAsync);

        router.Register<SyncStateReq>(MsgRegistry.GetId(typeof(SyncStateReq)),
            (ctx, req, ct) => HandleSyncAsync(services, ctx, ct));
        router.Register<CommandResultQueryReq>(MsgRegistry.GetId(typeof(CommandResultQueryReq)),
            (ctx, req, ct) => HandleQueryAsync(services, ctx, req, ct));
    }

    static async Task HandleSyncAsync(GameplayServices services, RequestContext context, CancellationToken ct)
    {
        if (context.PlayerId <= 0)
        {
            await services.Publisher.DeliverSingleAsync(
                context, MsgRegistry.GetId(typeof(SyncStateResp)),
                new SyncStateResp { Code = 102 }, ct).ConfigureAwait(false);
            return;
        }

        var caller = new SessionIdentity(context.PlayerId, context.SessionId);
        await services.Queries.GetStateInLaneAsync(caller, async state =>
        {
            if (state.PrecedingSystemDelta != null)
            {
                // 系统 Ntf 必须 seq=0；不得占用 Sync 请求 seq。
                await services.Publisher.DeliverSingleAsync(
                        context,
                        MsgRegistry.GetId(typeof(PlayerDeltaBatchNtf)),
                        state.PrecedingSystemDelta,
                        CancellationToken.None,
                        seq: 0)
                    .ConfigureAwait(false);
            }

            var resp = new SyncStateResp
            {
                Code = state.Code,
                ServerTime = state.ServerTimeMs,
                OperationScopeId = state.OperationScopeId,
                ScopeState = state.ScopeState
            };
            if (state.Data != null)
                resp.Data = state.Data;
            if (state.Code == 0 && state.NextOpId >= 1)
                resp.NextOpId = state.NextOpId;

            await services.Publisher.DeliverSingleAsync(
                context, MsgRegistry.GetId(typeof(SyncStateResp)), resp, CancellationToken.None)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    static async Task HandleQueryAsync(
        GameplayServices services, RequestContext context, CommandResultQueryReq req, CancellationToken ct)
    {
        if (context.PlayerId <= 0)
        {
            await services.Publisher.DeliverSingleAsync(
                context, MsgRegistry.GetId(typeof(CommandResultQueryResp)),
                new CommandResultQueryResp { Code = 102 }, ct).ConfigureAwait(false);
            return;
        }

        var caller = new SessionIdentity(context.PlayerId, context.SessionId);
        ReceiptQuery query = await services.Queries
            .QueryCommandAsync(caller, req.OperationScopeId, req.OpId, ct)
            .ConfigureAwait(false);

        var key = new CommandKey(context.PlayerId, req.OperationScopeId, req.OpId);
        CommandResultQueryResp resp;
        if (query.Kind == ReceiptQueryKind.ScopeInvalid && query.TransportCode == 102)
        {
            resp = new CommandResultQueryResp
            {
                Code = 102,
                ResultState = CommandResultState.Unspecified,
                Ack = new CommandAck { Disposition = CommandDisposition.Unspecified }
            };
        }
        else
        {
            resp = ReceiptAckMapper.ToQueryResp(key, query);
        }

        await services.Publisher.DeliverSingleAsync(
            context, MsgRegistry.GetId(typeof(CommandResultQueryResp)), resp, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
