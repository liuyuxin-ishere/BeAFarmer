using CloudIsle.Server.Data;

namespace CloudIsle.Server.Players;

/// <summary>
/// 单调毫秒时钟。生产使用系统计时；测试推进虚拟时间。
/// 间隔语义对齐 T11：循环间隔大于 0，取消后不再把本次等待算成到期。不引用 Unity。
/// </summary>
public interface ISaveClock
{
    /// <summary>当前单调毫秒。</summary>
    long NowMilliseconds { get; }

    /// <summary>等到绝对时间，或等到取消。</summary>
    /// <param name="dueMilliseconds">到期的单调毫秒。小于等于当前时间时立即返回 true。</param>
    /// <param name="ct">取消标记。取消后返回 false，调用方不得把这次等待当成一次到期。</param>
    /// <returns>时间到达时为 true；取消时为 false。</returns>
    Task<bool> WaitUntilAsync(long dueMilliseconds, CancellationToken ct);
}

/// <summary>用系统单调计时等待。组合根没有测试时钟时使用它。</summary>
public sealed class SystemSaveClock : ISaveClock
{
    /// <summary>进程内共用的系统时钟。</summary>
    public static readonly SystemSaveClock Shared = new();

    /// <summary>系统单调毫秒。</summary>
    public long NowMilliseconds => Environment.TickCount64;

    /// <summary>按剩余时间分段等待，避免一次 Delay 超过 int 范围。</summary>
    /// <param name="dueMilliseconds">到期的单调毫秒。</param>
    /// <param name="ct">取消标记。</param>
    public async Task<bool> WaitUntilAsync(long dueMilliseconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long remaining = dueMilliseconds - Environment.TickCount64;
            if (remaining <= 0)
                return true;

            long slice = Math.Min(remaining, 1_000);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(slice), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>
/// 每 5 秒把脏玩家的保存动作投到对应玩家队列。成功后只确认本次提交的版本。
/// Start、StopAsync、Dispose、DisposeAsync 是生产组合的接线点。不包含账号 Handler。
/// 玩家队列动作里禁止同步 Dispose，组合根使用 DisposeAsync 或在队列外 Dispose。
/// </summary>
public sealed class SaveService : IDisposable, IAsyncDisposable
{
    /// <summary>固定保存间隔。大于 0，满足循环计时契约。</summary>
    public const long IntervalMilliseconds = 5000;

    readonly PlayerQueue _queue;
    readonly PlayerCache _cache;
    readonly PlayerStore _store;
    readonly ISaveClock _clock;
    readonly object _gate = new();
    readonly List<TaskCompletionSource> _cycleWaiters = new();

    CancellationTokenSource? _stop;
    Task? _loop;
    Task? _lifecycle;
    long _nextDue;
    int _confirmedSaves;
    int _failedSaves;
    int _cycles;
    int _activeLoops;
    int _closed;
    Exception? _lastFailure;

    /// <summary>使用系统时钟创建保存服务。</summary>
    /// <param name="queue">保存动作要投递到的玩家队列。</param>
    /// <param name="cache">读取脏玩家和确认版本的缓存。</param>
    /// <param name="store">写入快照的存储。</param>
    public SaveService(PlayerQueue queue, PlayerCache cache, PlayerStore store)
        : this(queue, cache, store, SystemSaveClock.Shared)
    {
    }

    /// <summary>使用指定时钟创建保存服务。测试传入 ManualSaveClock。</summary>
    /// <param name="queue">保存动作要投递到的玩家队列。</param>
    /// <param name="cache">读取脏玩家和确认版本的缓存。</param>
    /// <param name="store">写入快照的存储。</param>
    /// <param name="clock">决定 5 秒何时到期的时钟。</param>
    public SaveService(PlayerQueue queue, PlayerCache cache, PlayerStore store, ISaveClock clock)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>已经成功确认的保存次数。</summary>
    internal int ConfirmedSaveCount => Volatile.Read(ref _confirmedSaves);

    /// <summary>写入或钩子失败次数。取消不计入。同一次失败只加一。</summary>
    internal int FailedSaveCount => Volatile.Read(ref _failedSaves);

    /// <summary>最近一次写入或钩子失败。没有失败时为 null。</summary>
    internal Exception? LastFailure
    {
        get
        {
            lock (_gate)
                return _lastFailure;
        }
    }

    /// <summary>已经开始的保存周期数。停止后不再增加。</summary>
    internal int CycleCount => Volatile.Read(ref _cycles);

    /// <summary>尚未退出的周期循环数量。正常时不超过 1。</summary>
    internal int ActiveLoopCount => Volatile.Read(ref _activeLoops);

    /// <summary>
    /// 测试让停止在取消旧循环之前暂停。生产保持 null。
    /// 暂停期间 Start 不能换掉正在停止的循环。
    /// </summary>
    internal Task? StopGate { get; set; }

    /// <summary>
    /// 测试在写入前插入同步点。参数是已经在队列里复制好的快照，以及本次刷新的取消标记。
    /// 生产路径保持 null。
    /// </summary>
    internal Func<PlayerState, CancellationToken, Task>? BeforePersistAsync { get; set; }

    /// <summary>启动唯一的 5 秒循环。已经在运行或正在停止时直接返回。关闭开始后拒绝。</summary>
    public void Start()
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (_lifecycle != null && !_lifecycle.IsCompleted)
                return;
            if (_loop != null && !_loop.IsCompleted)
                return;

            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            _nextDue = _clock.NowMilliseconds + IntervalMilliseconds;
            CancellationToken token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>取消当前这一代循环，并等待它得到完成或取消结果。并发调用共用同一次停止。</summary>
    public Task StopAsync()
    {
        lock (_gate)
            return ArmStop();
    }

    /// <summary>
    /// 在队列外同步关闭。先拒绝新的启动和刷新，再等待既有循环结束。
    /// 玩家队列动作里调用会自锁，因此直接拒绝。
    /// </summary>
    public void Dispose()
    {
        RejectIfInsidePlayerQueue();
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>组合根使用的异步关闭。先在生命周期锁内拒绝新工作，再等待既有循环。玩家队列内调用会被拒绝。</summary>
    public ValueTask DisposeAsync()
    {
        RejectIfInsidePlayerQueue();
        return new ValueTask(CloseAsync());
    }

    /// <summary>把当前缓存里的脏玩家各自投到自己的队列里保存。</summary>
    /// <param name="ct">取消标记。取消后不确认任何尚未确认的版本。</param>
    public async Task FlushDirtyAsync(CancellationToken ct)
    {
        ThrowIfClosed();
        long[] ids = _cache.CopyDirtyPlayerIds();
        if (ids.Length == 0)
            return;

        var pending = new Task[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            pending[i] = FlushPlayerAsync(ids[i], ct);
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>保存一名玩家当前的脏快照并等待结束。未缓存时不加载。</summary>
    /// <param name="playerId">要保存的玩家。必须是正数。</param>
    /// <param name="ct">取消标记。入队前取消则不保存；保存过程中取消则不确认版本。</param>
    public Task FlushPlayerAsync(long playerId, CancellationToken ct)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家主键必须是正数");
        ThrowIfClosed();
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        // 已经在这名玩家的队列动作里时直接保存，避免嵌套入队把自己等死。
        if (PlayerQueue.IsCurrentPlayer(playerId))
            return SaveInlineAsync(playerId, ct);
        if (!_cache.IsCached(playerId))
            return Task.CompletedTask;
        return SaveQueuedAsync(playerId, ct);
    }

    /// <summary>等到下一次周期的刷新结束。须在推进时钟前登记，供测试观察到期。</summary>
    internal Task WaitNextCycleAsync()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfClosed();
            _cycleWaiters.Add(waiter);
        }

        return waiter.Task;
    }

    /// <summary>在锁内接上这一代停止。已经在停的调用得到同一个任务。没有循环时立即完成。</summary>
    Task ArmStop()
    {
        if (_lifecycle != null && !_lifecycle.IsCompleted)
            return _lifecycle;

        Task? loop = _loop;
        CancellationTokenSource? stop = _stop;
        if (loop == null)
        {
            _lifecycle = Task.CompletedTask;
            return _lifecycle;
        }

        // 保留 _loop 和 _stop，停止完成前不换代。
        _lifecycle = StopCoreAsync(loop, stop);
        return _lifecycle;
    }

    /// <summary>先标记关闭，再等待 ArmStop 返回的那一次停止。</summary>
    async Task CloseAsync()
    {
        Task stop;
        lock (_gate)
        {
            Volatile.Write(ref _closed, 1);
            stop = ArmStop();
        }

        await stop.ConfigureAwait(false);
        CancelCycleWaiters();
    }

    /// <summary>取消并等待调用时看到的那一代循环。结束后只清这一代的字段。</summary>
    /// <param name="loop">开始停止时的循环任务。等待期间即使字段被换掉，也只等待这一份。</param>
    /// <param name="stop">这一代循环的取消源。只由这次停止释放。</param>
    async Task StopCoreAsync(Task loop, CancellationTokenSource? stop)
    {
        await Task.Yield();
        Task? gate = StopGate;
        if (gate != null)
            await gate.ConfigureAwait(false);

        if (stop != null)
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 这一代的取消源已经释放时，循环自己会结束。
            }
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 循环因停止而结束，这是确定的取消结果。
        }

        lock (_gate)
        {
            if (ReferenceEquals(_loop, loop))
            {
                _loop = null;
                _stop = null;
            }
        }

        stop?.Dispose();
    }

    /// <summary>按到期时间循环。保存异常只保留已经记下的那一次，不让循环停住。</summary>
    /// <param name="ct">这一代 Start 配套的停止标记。</param>
    async Task RunAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _activeLoops);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long due;
                lock (_gate)
                    due = _nextDue;

                bool dueNow = await _clock.WaitUntilAsync(due, ct).ConfigureAwait(false);
                if (!dueNow || ct.IsCancellationRequested)
                    break;

                lock (_gate)
                    _nextDue += IntervalMilliseconds;
                Interlocked.Increment(ref _cycles);
                try
                {
                    await FlushDirtyAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FinishCycle();
                    break;
                }
                catch (Exception)
                {
                    // 失败已在保存路径记过一次。这里只让下一轮继续等待。
                }

                FinishCycle();
            }

            CancelCycleWaiters();
        }
        finally
        {
            Interlocked.Decrement(ref _activeLoops);
        }
    }

    /// <summary>在当前玩家队列动作里复制、写入并确认。</summary>
    /// <param name="playerId">当前队列正在执行的玩家。</param>
    /// <param name="ct">本次刷新的取消标记。</param>
    async Task SaveInlineAsync(long playerId, CancellationToken ct)
    {
        if (!_cache.TryCopyDirty(playerId, out PlayerState? snapshot) || snapshot == null)
            return;

        using IDisposable hold = _cache.AcquireHold(playerId);
        await PersistAndObserveAsync(snapshot, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        _cache.ConfirmSaved(playerId, snapshot.Revision);
        Interlocked.Increment(ref _confirmedSaves);
    }

    /// <summary>先入队复制快照，写入放在队列外，再入队确认。持有在 finally 里直接释放，不依赖再次入队。</summary>
    /// <param name="playerId">要保存的已缓存玩家。</param>
    /// <param name="ct">本次刷新的取消标记。</param>
    async Task SaveQueuedAsync(long playerId, CancellationToken ct)
    {
        PlayerState? captured = null;
        IDisposable? hold = null;
        await _queue.EnqueueAsync(playerId, _ =>
        {
            if (_cache.TryCopyDirty(playerId, out PlayerState? snapshot) && snapshot != null)
            {
                captured = snapshot;
                hold = _cache.AcquireHold(playerId);
            }

            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        if (captured == null || hold == null)
            return;

        try
        {
            await PersistAndObserveAsync(captured, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            long revision = captured.Revision;
            await _queue.EnqueueAsync(playerId, _ =>
            {
                _cache.ConfirmSaved(playerId, revision);
                Interlocked.Increment(ref _confirmedSaves);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _cache.ReleaseSaveHold(hold);
            hold = null;
        }
    }

    /// <summary>执行钩子和写入。除取消以外的异常只在这里记录一次，然后原样抛出。</summary>
    /// <param name="snapshot">队列里已经复制好的那一版。</param>
    /// <param name="ct">本次刷新的取消标记。</param>
    async Task PersistAndObserveAsync(PlayerState snapshot, CancellationToken ct)
    {
        try
        {
            await PersistAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(ex);
            throw;
        }
    }

    /// <summary>调用测试钩子后写入存储。钩子和写入都不在定时线程上改玩家状态。</summary>
    /// <param name="snapshot">队列里已经复制好的那一版。</param>
    /// <param name="ct">本次刷新的取消标记。</param>
    async Task PersistAsync(PlayerState snapshot, CancellationToken ct)
    {
        Func<PlayerState, CancellationToken, Task>? hook = BeforePersistAsync;
        if (hook != null)
            await hook(snapshot, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        await _store.SaveAsync(snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>记下失败，供调用方和测试观察。不修改 SavedRevision。</summary>
    /// <param name="error">存储或钩子抛出的异常。</param>
    void RecordFailure(Exception error)
    {
        Interlocked.Increment(ref _failedSaves);
        lock (_gate)
            _lastFailure = error;
    }

    /// <summary>放开正在等待这一轮结束的测试。</summary>
    void FinishCycle()
    {
        TaskCompletionSource[] waiters;
        lock (_gate)
        {
            waiters = _cycleWaiters.ToArray();
            _cycleWaiters.Clear();
        }

        foreach (TaskCompletionSource waiter in waiters)
            waiter.TrySetResult();
    }

    /// <summary>循环退出后，还在等周期的调用方得到取消，避免留下挂起任务。</summary>
    void CancelCycleWaiters()
    {
        TaskCompletionSource[] waiters;
        lock (_gate)
        {
            waiters = _cycleWaiters.ToArray();
            _cycleWaiters.Clear();
        }

        foreach (TaskCompletionSource waiter in waiters)
            waiter.TrySetCanceled();
    }

    /// <summary>玩家队列动作里同步或异步关闭都会等回同一条队列，因此直接拒绝。</summary>
    static void RejectIfInsidePlayerQueue()
    {
        if (PlayerQueue.IsInPlayerAction)
            throw new InvalidOperationException("不能在玩家队列动作内关闭 SaveService。请在组合根调用 DisposeAsync。");
    }

    /// <summary>关闭开始后拒绝新的刷新和启动。</summary>
    void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) != 0)
            throw new ObjectDisposedException(nameof(SaveService));
    }
}
