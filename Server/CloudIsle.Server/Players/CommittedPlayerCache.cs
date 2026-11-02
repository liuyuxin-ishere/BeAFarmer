using CloudIsle.Domain.Init;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Application.Commands;
using CloudIsle.Application.Init;

namespace CloudIsle.Server.Players;

/// <summary>
/// 封装现有 <see cref="PlayerCache"/>：仅发布已 COMMIT 快照；失败隔离本玩家并回源。
/// 不接受未提交候选；无公开 debug API（测试故障缝为 internal）。
/// </summary>
public sealed class CommittedPlayerCache
{
    readonly PlayerCache _cache;
    readonly PlayerStore _store;

    /// <summary>测试：下一次 PublishCommitted 抛错（用后自动清除）。</summary>
    internal bool FaultNextPublish { get; set; }

    /// <summary>测试探针：默认 null。在真实 PublishCommitted 入口命中。</summary>
    internal CommitFaultProbe? Probe { get; set; }

    public CommittedPlayerCache(PlayerCache cache, PlayerStore store)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>底层缓存（SaveService / 登录 GetAsync 仍用）。</summary>
    public PlayerCache Inner => _cache;

    /// <summary>仅玩家 lane；安装 <paramref name="committed"/> 的已提交状态。</summary>
    public void PublishCommitted(long playerId, CommitOutcome committed)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (committed == null) throw new ArgumentNullException(nameof(committed));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("PublishCommitted requires player lane");
        if (!committed.DurableCommitted)
            throw new InvalidOperationException("refusing to publish non-durable outcome");
        if (committed.CommittedState.PlayerId != playerId)
            throw new ArgumentException("outcome player mismatch", nameof(committed));

        Probe?.Hit(CommitPhase.BeforeCachePublish, key: null);

        if (FaultNextPublish)
        {
            FaultNextPublish = false;
            throw new InvalidOperationException("committed-cache publish fault injected");
        }

        PlayerState clean = committed.CommittedState;
        // CommitOutcome getter 已 DeepCopy；再确保 SavedRevision==Revision；保留商店/accepted sidecar。
        var installed = new PlayerState(
            clean.PlayerId, clean.Data.Clone(), clean.Revision, clean.Revision,
            clean.ShopPurchases, clean.AcceptedTasks);
        _cache.ReplaceWithCommitted(playerId, installed);
    }

    /// <summary>隔离本玩家缓存条目（原因仅诊断）。</summary>
    public void Invalidate(long playerId, string reason)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("Invalidate requires player lane");
        _ = reason;
        _cache.ForceEvict(playerId);
    }

    /// <summary>失效后从 SQLite 重载并回填缓存；返回领域聚合视图。</summary>
    public async Task<PlayerAggregate> ReloadInLaneAsync(long playerId, CancellationToken ct)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("ReloadInLaneAsync requires player lane");

        Invalidate(playerId, "reload");
        PlayerState loaded = await _cache.GetAsync(playerId, ct).ConfigureAwait(false);
        return PlayerFullDataInitMapper.ToAggregate(
            loaded.Data, loaded.ShopPurchases, loaded.AcceptedTasks);
    }

    /// <summary>失效后重载为 PlayerState（写路径观察 revision）。</summary>
    public async Task<PlayerState> ReloadStateInLaneAsync(long playerId, CancellationToken ct)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("ReloadStateInLaneAsync requires player lane");

        Invalidate(playerId, "reload-state");
        return await _cache.GetAsync(playerId, ct).ConfigureAwait(false);
    }
}
