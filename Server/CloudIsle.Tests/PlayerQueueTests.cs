using CloudIsle.Server.Players;

namespace CloudIsle.Tests;

/// <summary>PlayerQueue 的顺序、并发、取消、容量、回收和关闭。</summary>
public sealed class PlayerQueueTests
{
    /// <summary>用户取消回调抛异常时，仍取消排队项且不会提前开始它。</summary>
    [Test]
    public async Task Dispose_ThrowingCancellationCallback_StillCompletesQueuedTasks()
    {
        using var queue = new PlayerQueue();
        var started = NewSignal();
        var release = NewSignal();
        Task running = queue.EnqueueAsync(1, async ct =>
        {
            using var registration = ct.Register(() => throw new InvalidOperationException("callback"));
            started.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool ran = false;
        Task pending = queue.EnqueueAsync(1, _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        try
        {
            Assert.DoesNotThrow(() => queue.Dispose());
            Assert.ThrowsAsync<TaskCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(ran, Is.False);
            Assert.That(running.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>业务动作在首次 await 前同步阻塞时，提交任务仍能返回。</summary>
    [Test]
    public async Task SynchronousAction_DoesNotBlockEnqueueCaller()
    {
        using var queue = new PlayerQueue();
        var started = NewSignal();
        var release = NewSignal();
        Task<Task> submission = Task.Factory.StartNew(() => queue.EnqueueAsync(1, _ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return Task.CompletedTask;
        }, CancellationToken.None), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        Task? action = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            action = await submission.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(action.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); }
        await (action ?? await submission).WaitAsync(TimeSpan.FromSeconds(5));
    }

    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>同一玩家三次入队的完成顺序与入队顺序一致。</summary>
    [Test]
    public async Task SamePlayer_CompletesInEnqueueOrder()
    {
        using var queue = new PlayerQueue(8);
        var release = NewSignal();
        var order = new List<int>();

        Task first = queue.EnqueueAsync(1, async _ =>
        {
            await release.Task;
            order.Add(1);
        }, CancellationToken.None);
        Task second = queue.EnqueueAsync(1, _ =>
        {
            order.Add(2);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Task third = queue.EnqueueAsync(1, _ =>
        {
            order.Add(3);
            return Task.CompletedTask;
        }, CancellationToken.None);

        release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(order, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    /// <summary>两名玩家同时停在动作内部，说明不同玩家没有共用一把串行锁。</summary>
    [Test]
    public async Task DifferentPlayers_RunConcurrently()
    {
        using var queue = new PlayerQueue(4);
        var release = NewSignal();
        var bothInside = NewSignal();
        int inside = 0;

        Task Enter(long playerId) => queue.EnqueueAsync(playerId, async _ =>
        {
            if (Interlocked.Increment(ref inside) == 2)
                bothInside.TrySetResult();
            await release.Task;
        }, CancellationToken.None);

        Task first = Enter(10);
        Task second = Enter(11);
        await bothInside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(inside, Is.EqualTo(2));
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>前一个动作的异常只出现在它自己的任务上，同玩家下一项仍会执行。</summary>
    [Test]
    public async Task Exception_IsDeliveredAndNextRuns()
    {
        using var queue = new PlayerQueue(4);
        var started = NewSignal();
        var release = NewSignal();
        bool nextRan = false;

        Task gate = queue.EnqueueAsync(5, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task failed = queue.EnqueueAsync(5, _ => throw new InvalidOperationException("boom"), CancellationToken.None);
        Task next = queue.EnqueueAsync(5, _ =>
        {
            nextRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        release.TrySetResult();
        InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failed.WaitAsync(TimeSpan.FromSeconds(5)));
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        await gate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(error.Message, Is.EqualTo("boom"));
        Assert.That(nextRan, Is.True);
    }

    /// <summary>入队前或排队期间取消时，动作委托不会运行，后面的动作仍会运行。</summary>
    [Test]
    public async Task CancelWhileQueued_DoesNotRunAction()
    {
        using var queue = new PlayerQueue(4);
        var started = NewSignal();
        var release = NewSignal();
        int sideEffects = 0;

        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        Task beforeQueue = queue.EnqueueAsync(6, _ =>
        {
            Interlocked.Increment(ref sideEffects);
            return Task.CompletedTask;
        }, alreadyCanceled.Token);

        Task first = queue.EnqueueAsync(6, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var queuedCancel = new CancellationTokenSource();
        Task queued = queue.EnqueueAsync(6, _ =>
        {
            Interlocked.Increment(ref sideEffects);
            return Task.CompletedTask;
        }, queuedCancel.Token);
        queuedCancel.Cancel();

        bool laterRan = false;
        Task later = queue.EnqueueAsync(6, _ =>
        {
            laterRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        release.TrySetResult();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await beforeQueue.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.ThrowsAsync<TaskCanceledException>(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await later.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sideEffects, Is.Zero);
        Assert.That(laterRan, Is.True);
    }

    /// <summary>执行中的协作取消不会在动作返回前启动同玩家的下一项。</summary>
    [Test]
    public async Task CancelWhileRunning_WaitsForActionBeforeNext()
    {
        using var queue = new PlayerQueue(4);
        using var runningCancel = new CancellationTokenSource();
        var started = NewSignal();
        var sawCancel = NewSignal();
        var finish = NewSignal();
        var secondStarted = NewSignal();
        int busy = 0;
        int overlap = 0;

        Task running = queue.EnqueueAsync(7, async ct =>
        {
            if (Interlocked.Increment(ref busy) != 1)
                Interlocked.Increment(ref overlap);
            started.TrySetResult();
            using (ct.Register(() => sawCancel.TrySetResult()))
                await sawCancel.Task;
            await finish.Task;
            Interlocked.Decrement(ref busy);
        }, runningCancel.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = queue.EnqueueAsync(7, _ =>
        {
            if (Interlocked.Increment(ref busy) != 1)
                Interlocked.Increment(ref overlap);
            secondStarted.TrySetResult();
            Interlocked.Decrement(ref busy);
            return Task.CompletedTask;
        }, CancellationToken.None);

        runningCancel.Cancel();
        await sawCancel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(secondStarted.Task.IsCompleted, Is.False);
        Assert.That(running.IsCompleted, Is.False);

        finish.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(secondStarted.Task.IsCompleted, Is.True);
        Assert.That(overlap, Is.Zero);
    }

    /// <summary>上限包含正在执行的一项。满时同步拒绝且不调用动作；腾出位置后可以再入队。</summary>
    [Test]
    public async Task Capacity_IncludesRunningItem_RejectsThenRecovers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerQueue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerQueue(-1));

        using var queue = new PlayerQueue(2);
        Assert.Throws<ArgumentNullException>(() => queue.EnqueueAsync(8, null!, CancellationToken.None));

        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondStarted = NewSignal();
        var releaseSecond = NewSignal();
        int rejectedCalls = 0;

        Task first = queue.EnqueueAsync(8, async _ =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
        }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task second = queue.EnqueueAsync(8, async _ =>
        {
            secondStarted.TrySetResult();
            await releaseSecond.Task;
        }, CancellationToken.None);

        InvalidOperationException full = Assert.Throws<InvalidOperationException>(() =>
            queue.EnqueueAsync(8, _ =>
            {
                Interlocked.Increment(ref rejectedCalls);
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.That(full.Message, Does.Contain("包含正在执行"));
        Assert.That(rejectedCalls, Is.Zero);

        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task recovered = queue.EnqueueAsync(8, _ => Task.CompletedTask, CancellationToken.None);
        releaseSecond.TrySetResult();
        await Task.WhenAll(first, second, recovered).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>玩家队列清空并移除后，再次并发入队仍然只有一个执行器。</summary>
    [Test]
    public async Task ReclaimedLane_AcceptsConcurrentEnqueueWithoutOverlap()
    {
        using var queue = new PlayerQueue(32);
        const long playerId = 4;
        int overlap = 0;

        for (int round = 0; round < 25; round++)
        {
            await queue.EnqueueAsync(playerId, _ => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await queue.WaitForIdleAsync(playerId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(queue.ActiveLaneCount, Is.Zero);

            int busy = 0;
            var burst = new Task[8];
            for (int i = 0; i < burst.Length; i++)
            {
                burst[i] = Task.Run(() => queue.EnqueueAsync(playerId, async _ =>
                {
                    if (Interlocked.Increment(ref busy) != 1)
                        Interlocked.Increment(ref overlap);
                    await Task.Yield();
                    Interlocked.Decrement(ref busy);
                }, CancellationToken.None));
            }

            await Task.WhenAll(burst).WaitAsync(TimeSpan.FromSeconds(5));
            await queue.WaitForIdleAsync(playerId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(queue.ActiveLaneCount, Is.Zero);
        }

        Assert.That(overlap, Is.Zero);
    }

    /// <summary>关闭后排队任务结束为取消，并发入队不会挂起，关闭之后的新入队被拒绝。</summary>
    [Test]
    public async Task Dispose_CancelsQueuedWorkAndRejectsConcurrentEnqueue()
    {
        using var queue = new PlayerQueue(8);
        var started = NewSignal();
        var release = NewSignal();
        int queuedSideEffects = 0;
        int concurrentSideEffects = 0;

        Task running = queue.EnqueueAsync(3, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = new Task[4];
        for (int i = 0; i < pending.Length; i++)
        {
            pending[i] = queue.EnqueueAsync(3, _ =>
            {
                Interlocked.Increment(ref queuedSideEffects);
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        var go = NewSignal();
        var workers = new Task<string>[24];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await go.Task;
                try
                {
                    await queue.EnqueueAsync(9, _ =>
                    {
                        Interlocked.Increment(ref concurrentSideEffects);
                        return Task.CompletedTask;
                    }, CancellationToken.None);
                    return "ok";
                }
                catch (ObjectDisposedException)
                {
                    return "disposed";
                }
                catch (OperationCanceledException)
                {
                    return "canceled";
                }
            });
        }

        go.TrySetResult();
        queue.Dispose();
        string[] outcomes = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Task pendingTask in pending)
            Assert.ThrowsAsync<TaskCanceledException>(async () => await pendingTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.That(queuedSideEffects, Is.Zero);
        Assert.That(running.IsCompleted, Is.False);
        foreach (string outcome in outcomes)
            Assert.That(outcome, Is.EqualTo("ok").Or.EqualTo("disposed").Or.EqualTo("canceled"));
        Assert.That(concurrentSideEffects, Is.EqualTo(outcomes.Count(static value => value == "ok")));
        Assert.Throws<ObjectDisposedException>(() => queue.EnqueueAsync(9, _ => Task.CompletedTask, CancellationToken.None));

        release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
