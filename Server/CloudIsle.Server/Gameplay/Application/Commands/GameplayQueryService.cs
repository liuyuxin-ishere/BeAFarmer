using CloudIsle.Domain.Clock;
using CloudIsle.Domain.Commands;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Infrastructure.Commands;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>SyncState 一致快照结果（已提交投影 + 当前 owned open scope/next）。</summary>
public sealed class SyncStateResult
{
    public SyncStateResult(
        int code,
        long serverTimeMs,
        PlayerFullData? data,
        long operationScopeId,
        long nextOpId,
        OperationScopeState scopeState,
        PlayerDeltaBatchNtf? precedingSystemDelta = null)
    {
        Code = code;
        ServerTimeMs = serverTimeMs;
        Data = data;
        OperationScopeId = operationScopeId;
        NextOpId = nextOpId;
        ScopeState = scopeState;
        PrecedingSystemDelta = precedingSystemDelta?.Clone();
    }

    public int Code { get; }
    public long ServerTimeMs { get; }
    public PlayerFullData? Data { get; }
    public long OperationScopeId { get; }
    public long NextOpId { get; }
    public OperationScopeState ScopeState { get; }
    /// <summary>Sync 前同 lane 日切产生的系统 Batch；可选先投递再 SyncResp。</summary>
    public PlayerDeltaBatchNtf? PrecedingSystemDelta { get; }
}

/// <summary>
/// Sync / CommandResultQuery。Query 只认证+归属读账本，不 init/day/RNG/配置业务。
/// Sync 同 lane 先必要系统日切再读快照。
/// </summary>
public sealed class GameplayQueryService
{
    readonly PlayerQueue _queue;
    readonly SessionRegistry _sessions;
    readonly PlayerStore _players;
    readonly CommandReceiptRepository _receipts;
    readonly SqliteStore _store;
    readonly Func<long> _utcNowMs;
    readonly IGameClock? _clock;
    readonly SystemWriteCoordinator? _systemWrites;

    public GameplayQueryService(
        PlayerQueue queue,
        SessionRegistry sessions,
        PlayerStore players,
        CommandReceiptRepository receipts,
        SqliteStore store,
        Func<long>? utcNowMs = null,
        IGameClock? clock = null,
        SystemWriteCoordinator? systemWrites = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _clock = clock;
        _systemWrites = systemWrites;
    }

    public async Task<SyncStateResult> GetStateAsync(SessionIdentity caller, CancellationToken ct)
    {
        SyncStateResult? result = null;
        await GetStateInLaneAsync(caller, async state =>
        {
            result = state;
            await Task.CompletedTask.ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("GetState produced no result");
    }

    public Task GetStateInLaneAsync(
        SessionIdentity caller,
        Func<SyncStateResult, Task> deliverAsync,
        CancellationToken ct)
    {
        if (deliverAsync == null) throw new ArgumentNullException(nameof(deliverAsync));
        if (!_sessions.IsCurrent(caller.PlayerId, caller.SessionId))
            return deliverAsync(new SyncStateResult(102, _utcNowMs(), null, 0, 0, OperationScopeState.Unknown));

        return _queue.EnqueueAsync(caller.PlayerId, async laneCt =>
        {
            _ = laneCt;
            SyncStateResult state;
            if (!_sessions.IsCurrent(caller.PlayerId, caller.SessionId))
            {
                state = new SyncStateResult(102, await CaptureNowAsync().ConfigureAwait(false), null, 0, 0, OperationScopeState.Unknown);
            }
            else
            {
                long now = await CaptureNowAsync().ConfigureAwait(false);
                PlayerDeltaBatchNtf? systemDelta = null;
                if (_systemWrites != null)
                {
                    DailyLaneResult day = await _systemWrites
                        .ReconcileDailyInLaneAsync(caller.PlayerId, now, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (day.Applied)
                        systemDelta = day.SystemDelta;
                }

                (long scopeId, long nextOp, bool open) = await ReadOpenScopeAsync(caller.PlayerId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (scopeId <= 0 || !open || nextOp < 1)
                {
                    state = new SyncStateResult(110, now, null, 0, 0, OperationScopeState.Unknown, systemDelta);
                }
                else
                {
                    PlayerState player = await _players.LoadAsync(caller.PlayerId, CancellationToken.None)
                        .ConfigureAwait(false);
                    PlayerFullData clone = GameplayProjectionMapper.ToFullClone(player);
                    state = new SyncStateResult(0, now, clone, scopeId, nextOp, OperationScopeState.Open, systemDelta);
                }
            }

            await deliverAsync(state).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// 只读账本；旧关闭 owned scope 允许；外 scope → 110 零提示。
    /// 不跑规则/init/day/config/RNG。
    /// </summary>
    public async Task<ReceiptQuery> QueryCommandAsync(
        SessionIdentity caller, long scopeId, long opId, CancellationToken ct)
    {
        if (!_sessions.IsCurrent(caller.PlayerId, caller.SessionId))
        {
            return new ReceiptQuery(ReceiptQueryKind.ScopeInvalid, 102, 0, false, null);
        }

        long now = await CaptureNowAsync().ConfigureAwait(false);
        return await _receipts.QueryAsync(caller.PlayerId, scopeId, opId, now, ct).ConfigureAwait(false);
    }

    async Task<long> CaptureNowAsync()
    {
        if (_clock != null)
            return await _clock.GetEffectiveNowMsAsync(CancellationToken.None).ConfigureAwait(false);
        return _utcNowMs();
    }

    Task<(long ScopeId, long NextOpId, bool Open)> ReadOpenScopeAsync(long playerId, CancellationToken ct)
    {
        return _store.ExecuteAsync(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT scope_id, next_op_id, closed FROM command_scope
                WHERE player_id = $p AND closed = 0
                ORDER BY scope_id DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (0L, 0L, false);
            long scopeId = reader.GetInt64(0);
            long next = reader.GetInt64(1);
            bool open = reader.GetInt64(2) == 0;
            return (scopeId, next, open);
        }, ct);
    }
}
