using System.Collections.Generic;

namespace CloudIsle.Server.Players;

/// <summary>
/// 按玩家串行执行异步动作。同一玩家严格按入队顺序执行；不同玩家各有执行器，可以并发。
/// 空闲玩家会从字典移除，避免一直留下历史玩家对象。
/// 不包含玩家存档、缓存或业务状态。
/// </summary>
public sealed class PlayerQueue : IDisposable
{
    /// <summary>未另行指定时，每名玩家尚未结束的动作上限。此上限包含正在执行的一项。</summary>
    public const int DefaultCapacity = 32;

    /// <summary>当前异步流正在执行的玩家。只由执行器在调用动作前写入，动作结束后恢复。</summary>
    static readonly AsyncLocal<ExecutionScope?> CurrentScope = new();

    /// <summary>动作及其派生异步流共用的生命周期；动作结束时统一失效。</summary>
    sealed class ExecutionScope
    {
        public readonly long PlayerId;
        public int Active = 1;

        /// <param name="playerId">本次动作所属玩家。</param>
        public ExecutionScope(long playerId) { PlayerId = playerId; }
    }

    readonly object _gate = new();
    readonly Dictionary<long, Lane> _lanes = new();
    bool _disposed;

    /// <summary>使用默认上限创建队列。</summary>
    public PlayerQueue()
        : this(DefaultCapacity)
    {
    }

    /// <summary>创建队列。</summary>
    /// <param name="capacity">每名玩家尚未结束的动作上限，必须是正数。包含正在执行的一项；达到上限时新的入队被拒绝。</param>
    public PlayerQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "每名玩家的队列上限必须是正数");
        Capacity = capacity;
    }

    /// <summary>每名玩家尚未结束的动作上限，包含正在执行的一项。</summary>
    public int Capacity { get; }

    /// <summary>
    /// 全部玩家尚未结束的动作总数。包含正在执行的一项和仍在等待的项，与 <see cref="Capacity"/> 同一口径。
    /// 不是玩家缓存人数。
    /// </summary>
    public int QueueLength
    {
        get
        {
            lock (_gate)
            {
                int total = 0;
                foreach (Lane lane in _lanes.Values)
                    total += lane.Accepted;
                return total;
            }
        }
    }

    /// <summary>当前异步流是否正在执行这名玩家的队列动作。</summary>
    /// <param name="playerId">要核对的玩家。与执行器写入的玩家不同，或根本不在动作里时返回 false。</param>
    public static bool IsCurrentPlayer(long playerId)
    {
        ExecutionScope? scope = CurrentScope.Value;
        return scope != null && scope.PlayerId == playerId && Volatile.Read(ref scope.Active) != 0;
    }

    /// <summary>当前异步流是否正处在任意玩家的队列动作里。动作结束后为 false。</summary>
    public static bool IsInPlayerAction
    {
        get
        {
            ExecutionScope? scope = CurrentScope.Value;
            return scope != null && Volatile.Read(ref scope.Active) != 0;
        }
    }

    /// <summary>当前仍占着执行器的玩家数量。空闲玩家会被移除，供测试观察回收。</summary>
    internal int ActiveLaneCount
    {
        get
        {
            lock (_gate)
                return _lanes.Count;
        }
    }

    /// <summary>等待指定玩家的当前执行器退出。玩家已经不在字典中时立即完成。</summary>
    /// <param name="playerId">要观察的玩家。</param>
    internal Task WaitForIdleAsync(long playerId)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(playerId, out Lane? lane))
                return Task.CompletedTask;
            return lane.Idle.Task;
        }
    }

    /// <summary>把动作排到指定玩家的队尾。</summary>
    /// <param name="playerId">玩家键。同一键共享一个执行器，不同键可以同时执行。</param>
    /// <param name="action">到达队首且尚未取消时才调用。参数是该次动作的取消标记；返回的 Task 结束前不会开始同玩家的下一项。</param>
    /// <param name="ct">调用方取消标记。入队前已取消则不入队；排到队首时已取消则不调用动作；执行中取消只通知动作，仍要等它返回。</param>
    /// <returns>该次动作的完成任务。动作异常或取消只体现在这个任务上。</returns>
    public Task EnqueueAsync(long playerId, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        Lane lane;
        bool startExecutor;
        Task queued;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PlayerQueue));
            if (ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            if (!_lanes.TryGetValue(playerId, out lane!))
            {
                lane = new Lane();
                _lanes.Add(playerId, lane);
            }

            if (lane.Accepted >= Capacity)
            {
                throw new InvalidOperationException(
                    $"玩家 {playerId} 的队列已满，上限为 {Capacity}，该上限包含正在执行的一项");
            }

            var item = new WorkItem(action, ct);
            lane.Waiting.Enqueue(item);
            lane.Accepted++;
            startExecutor = !lane.ExecutorRunning;
            if (startExecutor)
            {
                lane.ExecutorRunning = true;
                if (lane.Idle.Task.IsCompleted)
                    lane.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            queued = item.Completion.Task;
        }

        // 锁外启动。锁内已经把 ExecutorRunning 设为真，后到的入队不会再创建执行器。
        if (startExecutor)
            Schedule(playerId, lane);
        return queued;
    }

    /// <summary>拒绝新的入队，取消尚未开始的动作，并立刻返回。正在执行的动作继续被等待，直到它自己结束。</summary>
    public void Dispose()
    {
        List<WorkItem> dropped = new();
        List<WorkItem> running = new();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (Lane lane in _lanes.Values)
            {
                if (lane.Current != null)
                    running.Add(lane.Current);
                dropped.AddRange(lane.DrainWaiting());
            }
        }

        // Cancel 会同步调用外部注册的回调，必须离开全局锁；一个回调抛异常不能中断其他玩家清理。
        foreach (WorkItem item in running)
        {
            try { item.Linked.Cancel(); }
            catch (ObjectDisposedException) { /* 动作已经正常结束并释放了标记。 */ }
            catch (AggregateException) { /* 回调错误不能阻止队列关闭及待处理任务完成。 */ }
        }
        CompleteDropped(dropped);
    }

    /// <summary>在锁外启动唯一执行器，并观察它的任务，避免未观察异常。</summary>
    /// <param name="playerId">这个执行器负责的玩家。</param>
    /// <param name="lane">锁内刚刚登记的那一条队列。</param>
    void Schedule(long playerId, Lane lane)
    {
        // 入队只负责提交；同步执行到首个 await 的业务代码也不能阻塞提交者。
        Task worker = Task.Run(() => RunLaneAsync(playerId, lane));
        _ = ObserveAsync(worker);
    }

    /// <summary>吞掉执行器循环自身的异常。动作异常已经写到对应调用者的任务上。</summary>
    /// <param name="worker">执行器任务。</param>
    static async Task ObserveAsync(Task worker)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 循环内部负责把动作异常交给对应任务。这里只避免后台任务无人观察。
        }
    }

    /// <summary>按入队顺序执行这一名玩家的动作。队列排空时在同一把锁里摘掉执行器并移除玩家。</summary>
    /// <param name="playerId">玩家键。</param>
    /// <param name="lane">只由这个执行器推进的队列。</param>
    async Task RunLaneAsync(long playerId, Lane lane)
    {
        while (true)
        {
            WorkItem? item;
            List<WorkItem> dropped;
            lock (_gate)
            {
                if (_disposed || lane.Waiting.Count == 0)
                {
                    dropped = lane.DrainWaiting();
                    lane.Current = null;
                    lane.ExecutorRunning = false;
                    if (lane.Waiting.Count == 0
                        && lane.Accepted == 0
                        && _lanes.TryGetValue(playerId, out Lane? mapped)
                        && ReferenceEquals(mapped, lane))
                    {
                        _lanes.Remove(playerId);
                    }

                    lane.Idle.TrySetResult();
                    item = null;
                }
                else
                {
                    item = lane.Waiting.Dequeue();
                    lane.Current = item;
                    dropped = new List<WorkItem>();
                }
            }

            CompleteDropped(dropped);
            if (item == null)
                return;

            await ExecuteAsync(playerId, lane, item).ConfigureAwait(false);
        }
    }

    /// <summary>执行一项，或在它已经取消时跳过。无论成功、失败还是取消，都要等本次 Task 结束后才返回。</summary>
    /// <param name="playerId">这一项所属的玩家。只在动作执行期间写进异步流。</param>
    /// <param name="lane">该项所属的玩家队列，用于清除“正在执行”标记。</param>
    /// <param name="item">已经从等待队列取出的动作。</param>
    async Task ExecuteAsync(long playerId, Lane lane, WorkItem item)
    {
        Exception? error = null;
        bool canceled = false;
        try
        {
            if (item.Token.IsCancellationRequested)
            {
                canceled = true;
            }
            else
            {
                ExecutionScope? previous = CurrentScope.Value;
                var scope = new ExecutionScope(playerId);
                CurrentScope.Value = scope;
                try
                {
                    Task? work = item.Action(item.Token);
                    if (work == null)
                        throw new InvalidOperationException("玩家队列动作返回了空 Task");
                    await work.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
                {
                    canceled = true;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    Volatile.Write(ref scope.Active, 0);
                    CurrentScope.Value = previous;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(lane.Current, item))
                    lane.Current = null;
                lane.Accepted--;
            }
        }

        CancellationToken token = item.Token;
        if (canceled)
            item.Completion.TrySetCanceled(token);
        else if (error != null)
            item.Completion.TrySetException(error);
        else
            item.Completion.TrySetResult();
        item.Linked.Dispose();
    }

    /// <summary>把尚未开始的动作完成成取消，不调用其委托。</summary>
    /// <param name="dropped">从等待队列取出的动作。</param>
    static void CompleteDropped(List<WorkItem> dropped)
    {
        foreach (WorkItem item in dropped)
        {
            if (!item.Token.IsCancellationRequested)
            {
                try
                {
                    item.Linked.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 取消标记已经释放时，下面仍尝试结束调用者的任务。
                }
            }

            if (item.Token.IsCancellationRequested)
                item.Completion.TrySetCanceled(item.Token);
            else
                item.Completion.TrySetCanceled();
            try
            {
                item.Linked.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 重复释放可以忽略。
            }
        }
    }

    /// <summary>一名玩家的等待队列和唯一执行器标记。</summary>
    sealed class Lane
    {
        /// <summary>尚未开始的动作，顺序与成功入队的顺序一致。</summary>
        public Queue<WorkItem> Waiting { get; } = new();

        /// <summary>已接单且尚未结束的数量，包含正在执行的一项。</summary>
        public int Accepted;

        /// <summary>为真时已经有一个执行器会把本队列排空，新的入队不能再启动执行器。</summary>
        public bool ExecutorRunning;

        /// <summary>已经取出、正在等待其 Task 的动作。没有正在执行的动作时为空。</summary>
        public WorkItem? Current;

        /// <summary>执行器退出时完成。续体异步执行，避免在锁内重入。再次启动执行器时若已完成则换新。</summary>
        public TaskCompletionSource Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>取出全部尚未开始的动作，并按取出数量减少已接单计数。</summary>
        public List<WorkItem> DrainWaiting()
        {
            var dropped = new List<WorkItem>(Waiting.Count);
            while (Waiting.Count > 0)
            {
                dropped.Add(Waiting.Dequeue());
                Accepted--;
            }

            return dropped;
        }
    }

    /// <summary>一次入队。完成任务只代表这一次动作，不代表整条玩家队列。</summary>
    sealed class WorkItem
    {
        /// <summary>创建一次入队。</summary>
        /// <param name="action">队首才会调用的动作。</param>
        /// <param name="ct">调用方取消标记，与内部标记连接。</param>
        public WorkItem(Func<CancellationToken, Task> action, CancellationToken ct)
        {
            Action = action;
            Linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>业务动作。</summary>
        public Func<CancellationToken, Task> Action { get; }

        /// <summary>调用方取消或队列关闭时会取消的标记源。</summary>
        public CancellationTokenSource Linked { get; }

        /// <summary>传给动作的取消标记。</summary>
        public CancellationToken Token => Linked.Token;

        /// <summary>交还给 EnqueueAsync 调用者的完成源。</summary>
        public TaskCompletionSource Completion { get; }
    }
}
