using System.Collections.Generic;
using CloudIsle.Application.Init;
using CloudIsle.Config;
using CloudIsle.Domain.Daily;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Trade;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Infrastructure.Daily;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 系统写表面（init/day）：调用方须已在玩家 lane。
/// 不创建 op0、不写 receipt、不推进客户端 next_op。
/// </summary>
public sealed class SystemWriteCoordinator
{
    readonly GameplayCommitCore _core;
    readonly PlayerStore _players;
    readonly SqliteStore? _store;
    readonly DailyReconciler _reconciler;
    readonly IReadOnlyDictionary<string, DailyModuleRegistration> _registrations;
    readonly int _resetHour;
    readonly int _configVersion;
    readonly GameConfigSnapshot? _config;

    public SystemWriteCoordinator(
        GameplayCommitCore core,
        PlayerStore players,
        SqliteStore? store = null,
        IEnumerable<DailyModuleRegistration>? dailyModules = null,
        GameConfigSnapshot? config = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _store = store;
        _config = config;
        _configVersion = config?.Version ?? 1;
        _resetHour = config != null ? DailyConfig.RequireResetHour(config) : 0;

        var regs = new Dictionary<string, DailyModuleRegistration>(StringComparer.Ordinal);
        var policies = new List<IDailyResetPolicy>();
        if (dailyModules != null)
        {
            foreach (DailyModuleRegistration reg in dailyModules)
            {
                if (reg == null) throw new ArgumentException("null daily registration");
                if (!regs.TryAdd(reg.Policy.ModuleKey, reg))
                    throw new ArgumentException("duplicate daily module: " + reg.Policy.ModuleKey);
                policies.Add(reg.Policy);
            }
        }

        _registrations = regs;
        _reconciler = new DailyReconciler(policies);
    }

    /// <summary>
    /// lane 内系统提交：仅 player+snapshot（及可选 grant/day CAS），无 ScopeAdvance/Receipt。
    /// </summary>
    public Task<CommitOutcome> CommitSystemInLaneAsync(
        long playerId,
        PlayerState candidate,
        long observedStoredRevision,
        int? expectedGrantVersion,
        long nowMs,
        CancellationToken completionCt = default,
        IReadOnlyDictionary<string, DailyModuleMarker>? expectedMarkers = null,
        IReadOnlyDictionary<string, DailyModuleMarker>? candidateMarkers = null)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("CommitSystemInLaneAsync requires player lane");
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));

        var request = new CommitRequest(
            playerId,
            observedStoredRevision,
            candidate,
            scopeAdvance: null,
            receipt: null,
            systemGuard: new SystemGuardSpec(expectedGrantVersion, expectedMarkers, candidateMarkers),
            committedAtMs: nowMs);
        return _core.CommitAsync(request, completionCt);
    }

    /// <summary>
    /// 一次初始化：T008 Builder/Applicator/Mapper + 宿主 <see cref="GameConfigSnapshot"/>；
    /// grant CAS 防止双发放；NoOp 不增 revision；不消费外部 next。
    /// </summary>
    public async Task<InitLaneResult> InitializeInLaneAsync(
        long playerId,
        PlayerOrigin origin,
        GameConfigSnapshot config,
        long nowMs,
        CancellationToken completionCt = default)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("InitializeInLaneAsync requires player lane");
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs));
        completionCt.ThrowIfCancellationRequested();

        PlayerState observed = await _players.LoadAsync(playerId, completionCt).ConfigureAwait(false);
        InitConfigView view = InitConfigProjector.FromSnapshot(config);
        PlayerAggregate aggregate = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);
        InitGrantCandidate grant = InitGrantBuilder.Build(aggregate, origin, view);

        if (grant.IsNoOp)
        {
            return new InitLaneResult(
                applied: false,
                wasNoOp: true,
                revision: observed.Revision,
                grantVersion: observed.Data.GrantVersion,
                state: observed.DeepCopy(),
                commit: null,
                message: "init no-op; grant already at/above target");
        }

        PlayerFullData nextData = PlayerFullDataInitMapper.ApplyInitGrant(observed.Data, grant);
        long newRev;
        try { newRev = checked(observed.Revision + 1); }
        catch (OverflowException)
        {
            throw new InvalidOperationException("revision overflow during init");
        }

        nextData.Revision = newRev;
        // 初始化不碰商店/accepted 计数；保留 observed sidecar。
        var candidate = new PlayerState(
            playerId, nextData, newRev, observed.SavedRevision,
            observed.ShopPurchases, observed.AcceptedTasks);

        CommitOutcome committed = await CommitSystemInLaneAsync(
                playerId,
                candidate,
                observed.Revision,
                expectedGrantVersion: observed.Data.GrantVersion,
                nowMs,
                completionCt)
            .ConfigureAwait(false);

        if (!committed.DurableCommitted)
            throw new InvalidOperationException("init commit was not durable");

        return new InitLaneResult(
            applied: true,
            wasNoOp: false,
            revision: committed.CommittedRevision,
            grantVersion: committed.CommittedState.Data.GrantVersion,
            state: committed.CommittedState,
            commit: committed,
            message: committed.PostCommitFault ? "init durable; post-commit publish fault" : "init applied");
    }

    /// <summary>
    /// 同 lane 可组合日切：空参与者 NoOp；有变化才 +1 revision；不消费客户端 op。
    /// marker-only 亦抬 revision 并生成系统 Batch（operation_scope_id=0, op_id=0）。
    /// Unavailable 参与者不阻塞其他模块。
    /// </summary>
    public async Task<DailyLaneResult> ReconcileDailyInLaneAsync(
        long playerId,
        long nowMs,
        CancellationToken completionCt = default)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("ReconcileDailyInLaneAsync requires player lane");
        if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs));
        if (_store == null)
            throw new InvalidOperationException("ReconcileDailyInLaneAsync requires SqliteStore");
        completionCt.ThrowIfCancellationRequested();

        PlayerState observed = await _players.LoadAsync(playerId, completionCt).ConfigureAwait(false);
        IReadOnlyDictionary<string, DailyModuleMarker> markers = await _store
            .ExecuteAsync(c => DailyMarkerStore.ReadAll(c, null, playerId), completionCt)
            .ConfigureAwait(false);

        var ctx = new DailyCommandContext(nowMs, _configVersion, _resetHour, _config);
        PlayerAggregate aggregate = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);
        DailyCandidate candidate = _reconciler.Evaluate(aggregate, markers, ctx);

        if (candidate.IsNoOp)
        {
            return new DailyLaneResult(
                applied: false,
                wasNoOp: true,
                revision: observed.Revision,
                targetDay: candidate.TargetDay,
                state: observed.DeepCopy(),
                commit: null,
                systemDelta: null,
                markers: markers,
                message: "daily no-op");
        }

        // 领域候选 Aggregate 为权威资产；声明字段投影 + 可选回调须对齐候选后才提交。
        PlayerFullData nextData = DailyCandidateMaterializer.Materialize(
            observed.Data, candidate, _registrations);

        long newRev;
        try { newRev = checked(observed.Revision + 1); }
        catch (OverflowException)
        {
            throw new InvalidOperationException("revision overflow during daily");
        }

        nextData.Revision = newRev;
        // 商店计数来自领域候选（ShopDailyResetPolicy 清空或保留）；不混入 SystemGuard 形状。
        ShopPurchaseCounts nextShop = candidate.Aggregate.ShopPurchases ?? ShopPurchaseCounts.Empty;
        AcceptedTaskState nextTasks = candidate.Aggregate.AcceptedTasks ?? AcceptedTaskState.Empty;
        var nextState = new PlayerState(
            playerId, nextData, newRev, observed.SavedRevision, nextShop, nextTasks);

        CommitOutcome committed = await CommitSystemInLaneAsync(
                playerId,
                nextState,
                observed.Revision,
                expectedGrantVersion: null,
                nowMs,
                completionCt,
                expectedMarkers: markers,
                candidateMarkers: candidate.NextMarkers)
            .ConfigureAwait(false);

        if (!committed.DurableCommitted)
            throw new InvalidOperationException("daily commit was not durable");

        PlayerDeltaBatchNtf systemDelta = GameplayProjectionMapper.ToSystemDelta(
            observed, committed.CommittedState, nowMs, _configVersion);

        return new DailyLaneResult(
            applied: true,
            wasNoOp: false,
            revision: committed.CommittedRevision,
            targetDay: candidate.TargetDay,
            state: committed.CommittedState,
            commit: committed,
            systemDelta: systemDelta,
            markers: candidate.NextMarkers,
            message: committed.PostCommitFault ? "daily durable; post-commit publish fault" : "daily applied");
    }

    /// <summary>NewAccount：无地块/节点/工具/设施/背包实体。否则 MigratedM1（保留 legacy 域）。</summary>
    public static PlayerOrigin ClassifyOrigin(PlayerState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        PlayerFullData d = state.Data;
        if (d.Plots.Count > 0 || d.Nodes.Count > 0 || d.Tools.Count > 0
            || d.Facilities.Count > 0 || d.Bag.Count > 0)
            return PlayerOrigin.MigratedM1;
        return PlayerOrigin.NewAccount;
    }
}
