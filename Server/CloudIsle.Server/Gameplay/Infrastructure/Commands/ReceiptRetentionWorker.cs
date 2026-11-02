using System;
using CloudIsle.Domain.Clock;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Commands;

/// <summary>
/// 分批删除过期 command_receipt 详情行。永不删除 command_scope / auth 关联 / next_op / player / snapshot。
/// 默认间隔 1 小时、保留 7 天；可注入时钟与 delay 做单测。
/// 单次唤醒可多批（有界）+ Yield/取消，积压时短间隔重排程，不饿死玩家事务。
/// Host 拥有 RunAsync + shutdown CTS；清理故障不停止 Host。
/// </summary>
public sealed class ReceiptRetentionWorker
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultBacklogInterval = TimeSpan.FromMilliseconds(50);
    public static readonly long DefaultRetentionMs = CommandReceiptRepository.DefaultRetentionMs;
    public const int DefaultBatchSize = 256;
    public const int MaxBatchSize = 2000;
    public const int DefaultMaxBatchesPerWake = 8;
    public const int MaxBatchesPerWakeCap = 64;

    readonly SqliteStore _store;
    readonly Func<long> _utcNowMs;
    readonly IGameClock? _clock;
    readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    readonly TimeSpan _interval;
    readonly TimeSpan _backlogInterval;
    readonly long _retentionMs;
    readonly int _batchSize;
    readonly int _maxBatchesPerWake;
    readonly Action<string>? _log;

    public ReceiptRetentionWorker(
        SqliteStore store,
        Func<long>? utcNowMs = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? interval = null,
        long retentionMs = -1,
        int batchSize = DefaultBatchSize,
        Action<string>? log = null,
        int maxBatchesPerWake = DefaultMaxBatchesPerWake,
        TimeSpan? backlogInterval = null,
        IGameClock? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _clock = clock;
        _delayAsync = delayAsync ?? ((ts, ct) => Task.Delay(ts, ct));
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "interval must be positive");
        _backlogInterval = backlogInterval ?? DefaultBacklogInterval;
        if (_backlogInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(backlogInterval), "backlogInterval must be positive");
        if (retentionMs == -1)
            _retentionMs = DefaultRetentionMs;
        else if (retentionMs < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionMs), "use -1 for default or a non-negative window");
        else
            _retentionMs = retentionMs;
        if (batchSize < 1 || batchSize > MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "batchSize in 1..MaxBatchSize");
        _batchSize = batchSize;
        if (maxBatchesPerWake < 1 || maxBatchesPerWake > MaxBatchesPerWakeCap)
            throw new ArgumentOutOfRangeException(nameof(maxBatchesPerWake));
        _maxBatchesPerWake = maxBatchesPerWake;
        _log = log;
    }

    /// <summary>
    /// 删除 committed_at_ms &lt;= cutoffMs 的 receipt，按 (player_id,scope_id,op_id) 稳定序，至多 batchSize 行。
    /// </summary>
    public Task<int> PruneExpiredDetailsAsync(long cutoffMs, int batchSize, CancellationToken ct)
    {
        if (cutoffMs < 0)
            throw new ArgumentOutOfRangeException(nameof(cutoffMs), "cutoffMs must be >= 0");
        if (batchSize < 1 || batchSize > MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        ct.ThrowIfCancellationRequested();
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            int deleted;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM command_receipt
                    WHERE rowid IN (
                      SELECT rowid FROM command_receipt
                      WHERE committed_at_ms <= $cutoff
                      ORDER BY player_id ASC, scope_id ASC, op_id ASC
                      LIMIT $limit
                    );
                    """;
                cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
                cmd.Parameters.AddWithValue("$limit", batchSize);
                deleted = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return deleted;
        }, ct);
    }

    /// <summary>
    /// 单次唤醒：now &lt; retention → 不剪。否则有界多批 + Yield；返回删除总数。
    /// 若末批仍满（仍有积压），返回值可被 RunAsync 用于短间隔重排程。
    /// </summary>
    public async Task<(int Deleted, bool BacklogRemaining)> RunOnceWithBudgetAsync(
        long nowMs, CancellationToken ct = default)
    {
        if (nowMs < 0)
            throw new ArgumentOutOfRangeException(nameof(nowMs));
        if (nowMs < _retentionMs)
            return (0, false);
        long cutoff = checked(nowMs - _retentionMs);
        int total = 0;
        bool backlog = false;
        for (int i = 0; i < _maxBatchesPerWake; i++)
        {
            ct.ThrowIfCancellationRequested();
            int n = await PruneExpiredDetailsAsync(cutoff, _batchSize, ct).ConfigureAwait(false);
            total += n;
            if (n < _batchSize)
            {
                backlog = false;
                break;
            }
            backlog = true;
            await Task.Yield();
        }
        return (total, backlog);
    }

    /// <summary>兼容旧 API：仅返回删除行数。</summary>
    public async Task<int> RunOnceAsync(long nowMs, CancellationToken ct = default)
    {
        (int deleted, _) = await RunOnceWithBudgetAsync(nowMs, ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>循环：取消归 Host；单次失败记日志后延迟再试；积压短间隔，不抛穿未观察任务。</summary>
    public async Task RunAsync(CancellationToken shutdownCt)
    {
        while (!shutdownCt.IsCancellationRequested)
        {
            bool backlog = false;
            try
            {
                long now = _clock != null
                    ? await _clock.GetEffectiveNowMsAsync(shutdownCt).ConfigureAwait(false)
                    : _utcNowMs();
                (int n, bool more) = await RunOnceWithBudgetAsync(now, shutdownCt).ConfigureAwait(false);
                backlog = more;
                if (n > 0)
                    _log?.Invoke("receipt prune deleted=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (more ? " backlog=1" : ""));
            }
            catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke("receipt prune fault=" + ex.GetType().Name);
            }

            try
            {
                TimeSpan wait = backlog ? _backlogInterval : _interval;
                await _delayAsync(wait, shutdownCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
