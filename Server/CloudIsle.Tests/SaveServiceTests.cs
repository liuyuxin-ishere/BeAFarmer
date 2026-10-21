using System.Globalization;
using CloudIsle.Server.Data;
using CloudIsle.Server.Players;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>SaveService 的 5 秒周期、失败重试、登出刷新和停止。</summary>
public sealed class SaveServiceTests
{
    /// <summary>5 秒前不写库；虚拟时间到达 5 秒时只保存一轮最新昵称。</summary>
    [Test]
    public async Task BeforeFiveSeconds_DoesNotSave_AndDueCycleSavesOnce()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Due");
        }, CancellationToken.None);

        service.Start();
        Task cycle = service.WaitNextCycleAsync();
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.That(service.CycleCount, Is.Zero);
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Ann"));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await cycle;

        Assert.That(service.CycleCount, Is.EqualTo(1));
        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(1));
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Due"));
        Assert.That(ReadSavedRevision(dir, 11), Is.EqualTo(1));
    }

    /// <summary>一个间隔内多次改昵称，只落最新的一份快照。</summary>
    [Test]
    public async Task RepeatedEdits_SaveLatestSnapshotOnce()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "One");
            cache.ReplaceNickname(11, "Two");
            cache.ReplaceNickname(11, "Three");
        }, CancellationToken.None);

        service.Start();
        Task cycle = service.WaitNextCycleAsync();
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await cycle;

        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(1));
        Assert.That(service.CycleCount, Is.EqualTo(1));
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Three"));
        Assert.That(ReadSavedRevision(dir, 11), Is.EqualTo(3));
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState state = await cache.GetAsync(11, ct);
            Assert.That(state.Data.Info.Nickname, Is.EqualTo("Three"));
            Assert.That(state.Dirty, Is.False);
            Assert.That(state.SavedRevision, Is.EqualTo(3));
        }, CancellationToken.None);
    }

    /// <summary>第一次周期失败后 Dirty 和 SavedRevision 保持原样；去掉故障后下一轮才确认。</summary>
    [Test]
    public async Task Failure_KeepsDirty_RetryUpdatesSavedRevision()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Kept");
        }, CancellationToken.None);
        using (var connection = Open(DatabasePath(dir)))
        {
            Execute(connection, """
                CREATE TRIGGER player_snapshot_fail BEFORE INSERT ON player_snapshot
                BEGIN
                  SELECT RAISE(ABORT, 'snapshot-fail');
                END;
                """);
        }

        service.Start();
        Task failed = service.WaitNextCycleAsync();
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await failed;

        Assert.That(service.FailedSaveCount, Is.EqualTo(1));
        Assert.That(service.LastFailure, Is.InstanceOf<SqliteException>());
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Ann"));
        Assert.That(CountSnapshots(dir), Is.Zero);
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState dirty = await cache.GetAsync(11, ct);
            Assert.That(dirty.Dirty, Is.True);
            Assert.That(dirty.SavedRevision, Is.Zero);
            Assert.That(dirty.Data.Info.Nickname, Is.EqualTo("Kept"));
        }, CancellationToken.None);

        using (var connection = Open(DatabasePath(dir)))
            Execute(connection, "DROP TRIGGER player_snapshot_fail;");

        Task retried = service.WaitNextCycleAsync();
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await retried;

        Assert.That(service.CycleCount, Is.EqualTo(2));
        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(1));
        Assert.That(service.FailedSaveCount, Is.EqualTo(1));
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Kept"));
        Assert.That(ReadSavedRevision(dir, 11), Is.EqualTo(1));
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState clean = await cache.GetAsync(11, ct);
            Assert.That(clean.Dirty, Is.False);
            Assert.That(clean.SavedRevision, Is.EqualTo(1));
        }, CancellationToken.None);
    }

    /// <summary>写入前又修改时，先确认的旧版本不能把新昵称标成已保存。</summary>
    [Test]
    public async Task ConcurrentFlushAndEdit_DoesNotDropNewRevision()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Mid");
        }, CancellationToken.None);

        service.BeforePersistAsync = (_, _) => queue.EnqueueAsync(11, _ =>
        {
            cache.ReplaceNickname(11, "New");
            return Task.CompletedTask;
        }, CancellationToken.None);

        await service.FlushPlayerAsync(11, CancellationToken.None);
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Mid"));
        Assert.That(ReadSavedRevision(dir, 11), Is.EqualTo(1));
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState newer = await cache.GetAsync(11, ct);
            Assert.That(newer.Data.Info.Nickname, Is.EqualTo("New"));
            Assert.That(newer.Revision, Is.EqualTo(2));
            Assert.That(newer.SavedRevision, Is.EqualTo(1));
            Assert.That(newer.Dirty, Is.True);
        }, CancellationToken.None);

        service.BeforePersistAsync = null;
        await service.FlushPlayerAsync(11, CancellationToken.None);
        store.Dispose();
        using var reopened = new SqliteStore();
        reopened.Initialize(DatabasePath(dir));
        PlayerState loaded = await new PlayerStore(reopened).LoadAsync(11, CancellationToken.None);
        Assert.That(loaded.Data.Info.Nickname, Is.EqualTo("New"));
        Assert.That(loaded.SavedRevision, Is.EqualTo(2));
        Assert.That(loaded.Dirty, Is.False);
    }

    /// <summary>登出在玩家队列内部等待 FlushPlayerAsync，重开库后昵称一致。</summary>
    [Test]
    public async Task LogoutFlush_ReloadsSameNickname()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Bye");
            await service.FlushPlayerAsync(11, ct);
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(1));
        store.Dispose();
        using var reopened = new SqliteStore();
        reopened.Initialize(DatabasePath(dir));
        PlayerState loaded = await new PlayerStore(reopened).LoadAsync(11, CancellationToken.None);
        Assert.That(loaded.Data.Info.Nickname, Is.EqualTo("Bye"));
        Assert.That(loaded.Revision, Is.EqualTo(1));
        Assert.That(loaded.SavedRevision, Is.EqualTo(1));
    }

    /// <summary>取消不确认版本；停止后再推进时间也不再开新周期，队列里不留动作。</summary>
    [Test]
    public async Task StopAndCancel_DoesNotStartAnotherCycleOrLeaveWork()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Held");
        }, CancellationToken.None);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforePersistAsync = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task;
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
        };

        service.Start();
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await entered.Task;
        int cycles = service.CycleCount;
        Task stopping = service.StopAsync();
        release.TrySetResult();
        await stopping;

        Assert.That(cycles, Is.EqualTo(1));
        Assert.That(service.CycleCount, Is.EqualTo(1));
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Ann"));
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState dirty = await cache.GetAsync(11, ct);
            Assert.That(dirty.Dirty, Is.True);
            Assert.That(dirty.SavedRevision, Is.Zero);
        }, CancellationToken.None);
        await queue.WaitForIdleAsync(11);
        Assert.That(queue.ActiveLaneCount, Is.Zero);

        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds * 3));
        Assert.That(service.CycleCount, Is.EqualTo(1));
        Assert.That(service.ConfirmedSaveCount, Is.Zero);

        var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await service.FlushPlayerAsync(11, canceled.Token));
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        await queue.WaitForIdleAsync(11);
        Assert.That(queue.ActiveLaneCount, Is.Zero);
    }

    /// <summary>未缓存或已经干净的玩家不会被定时保存加载进缓存。</summary>
    [Test]
    public async Task CleanOrUncached_DoesNotLoad()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        Seed(dir, 12, 2, "Bea");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await service.FlushPlayerAsync(11, CancellationToken.None);
        Assert.That(players.LoadQueryCount, Is.Zero);
        Assert.That(cache.Count, Is.Zero);

        await queue.EnqueueAsync(12, async ct =>
        {
            await cache.GetAsync(12, ct);
        }, CancellationToken.None);
        int queries = players.LoadQueryCount;

        service.Start();
        Task cycle = service.WaitNextCycleAsync();
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await cycle;

        Assert.That(players.LoadQueryCount, Is.EqualTo(queries));
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(CountSnapshots(dir), Is.Zero);
        Assert.That(cache.Count, Is.EqualTo(1));
    }

    /// <summary>两名玩家的保存在写入前可以同时到达，不串成一条队列。</summary>
    [Test]
    public async Task TwoPlayers_FlushIndependently()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        Seed(dir, 12, 2, "Bea");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Ann2");
        }, CancellationToken.None);
        await queue.EnqueueAsync(12, async ct =>
        {
            await cache.GetAsync(12, ct);
            cache.ReplaceNickname(12, "Bea2");
        }, CancellationToken.None);

        int arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforePersistAsync = (_, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                both.TrySetResult();
            return both.Task;
        };

        await service.FlushDirtyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(2));
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Ann2"));
        Assert.That(ReadNickname(dir, 12), Is.EqualTo("Bea2"));
    }

    /// <summary>运行中的第二次 Start 不会再开一个周期。</summary>
    [Test]
    public async Task StartTwice_DoesNotOverlap()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Once");
        }, CancellationToken.None);

        service.Start();
        service.Start();
        Task cycle = service.WaitNextCycleAsync();
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await cycle;

        Assert.That(service.CycleCount, Is.EqualTo(1));
        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(1));
        Assert.That(ReadNickname(dir, 11), Is.EqualTo("Once"));
    }

    /// <summary>关闭标记先于等待生效。之后的 Start 和刷新都不能再开工作，已有周期也不再增加。</summary>
    [Test]
    public async Task Close_RejectsNewLoopAndFlush()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        await using var service = new SaveService(queue, cache, players, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforePersistAsync = async (_, token) =>
        {
            entered.TrySetResult();
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            if (token.IsCancellationRequested)
                canceled.TrySetResult();
            await canceled.Task.ConfigureAwait(false);
            // 取消会同步结束本次保存。先留在钩子里，断言才能看到关闭尚未把循环清掉。
            await releaseLoop.Task.ConfigureAwait(false);
        };

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Due");
        }, CancellationToken.None);

        service.Start();
        await WaitUntilAsync(() => service.ActiveLoopCount == 1);
        clock.Advance(TimeSpan.FromMilliseconds(SaveService.IntervalMilliseconds));
        await entered.Task;

        Task closing = service.DisposeAsync().AsTask();
        Assert.Throws<ObjectDisposedException>(() => service.Start());
        Assert.Throws<ObjectDisposedException>(() => service.FlushPlayerAsync(11, CancellationToken.None));
        Assert.That(service.ActiveLoopCount, Is.EqualTo(1));
        releaseLoop.TrySetResult();
        await closing;

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.That(service.CycleCount, Is.EqualTo(1));
        Assert.That(service.ActiveLoopCount, Is.Zero);
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
    }

    /// <summary>停止还停在旧循环上时，重复 Start 和 Stop 不能再开一条循环，也不能换成另一个停止任务。</summary>
    [Test]
    public async Task Stop_RepeatedStartAndStop_DoesNotOverlap()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StopGate = gate.Task;

        service.Start();
        await WaitUntilAsync(() => service.ActiveLoopCount == 1);
        Task first = service.StopAsync();
        Task second = service.StopAsync();
        service.Start();
        service.Start();
        Task third = service.StopAsync();

        Assert.That(second, Is.SameAs(first));
        Assert.That(third, Is.SameAs(first));
        Assert.That(service.ActiveLoopCount, Is.EqualTo(1));
        Assert.That(first.IsCompleted, Is.False);

        gate.TrySetResult();
        await first;
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.That(service.ActiveLoopCount, Is.Zero);
        Assert.That(service.CycleCount, Is.Zero);
    }

    /// <summary>玩家队列动作里同步关闭会等回当前动作，因此必须立即拒绝。</summary>
    [Test]
    public async Task DisposeInsidePlayerQueue_IsRejected()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, _ =>
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => service.Dispose());
            Assert.That(error.Message, Does.Contain("DisposeAsync"));
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        service.Start();
        await WaitUntilAsync(() => service.ActiveLoopCount == 1);
        await service.StopAsync();
        Assert.That(service.ActiveLoopCount, Is.Zero);
    }

    /// <summary>确认入队因容量失败时保留 Dirty 并释放持有。队列空出来后重试可以确认并移除。</summary>
    [Test]
    public async Task ConfirmEnqueueFull_KeepsDirty_RetryThenRemove()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue(1);
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforePersistAsync = (_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Held");
        }, CancellationToken.None);

        Task saving = service.FlushPlayerAsync(11, CancellationToken.None);
        await entered.Task;
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = queue.EnqueueAsync(11, async _ =>
        {
            blockerEntered.TrySetResult();
            await blockerRelease.Task;
        }, CancellationToken.None);
        await blockerEntered.Task;
        release.TrySetResult();

        Assert.ThrowsAsync<InvalidOperationException>(async () => await saving);
        Assert.That(saving.IsCompleted, Is.True);
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
        Assert.That(cache.CopyDirtyPlayerIds(), Is.EqualTo(new[] { 11L }));
        Assert.That(service.ConfirmedSaveCount, Is.Zero);

        blockerRelease.TrySetResult();
        await blocker;
        service.BeforePersistAsync = null;
        await service.FlushPlayerAsync(11, CancellationToken.None);

        Assert.That(service.ConfirmedSaveCount, Is.EqualTo(1));
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
        Assert.That(cache.CopyDirtyPlayerIds(), Is.Empty);
        await queue.EnqueueAsync(11, _ =>
        {
            cache.Remove(11);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.That(cache.Count, Is.Zero);
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
    }

    /// <summary>保存过程中玩家队列已关闭时，失败任务结束，保存持有不留下。</summary>
    [Test]
    public async Task QueueDisposedDuringSave_ReleasesHold()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);
        service.BeforePersistAsync = (_, _) =>
        {
            queue.Dispose();
            return Task.CompletedTask;
        };

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Gone");
        }, CancellationToken.None);

        Task saving = service.FlushPlayerAsync(11, CancellationToken.None);
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await saving);
        Assert.That(saving.IsCompleted, Is.True);
        Assert.That(saving.IsFaulted, Is.True);
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
        Assert.That(cache.CopyDirtyPlayerIds(), Is.EqualTo(new[] { 11L }));
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
    }

    /// <summary>一次 SQLite 失败只增加一次失败计数，异常仍传给调用方。</summary>
    [Test]
    public async Task DatabaseFailure_IncrementsFailedSaveCountOnce()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Kept");
        }, CancellationToken.None);
        using (var connection = Open(DatabasePath(dir)))
        {
            Execute(connection, """
                CREATE TRIGGER player_snapshot_fail BEFORE INSERT ON player_snapshot
                BEGIN
                  SELECT RAISE(ABORT, 'snapshot-fail');
                END;
                """);
        }

        Assert.ThrowsAsync<SqliteException>(async () => await service.FlushPlayerAsync(11, CancellationToken.None));
        Assert.That(service.FailedSaveCount, Is.EqualTo(1));
        Assert.That(service.LastFailure, Is.InstanceOf<SqliteException>());
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
        Assert.That(cache.CopyDirtyPlayerIds(), Is.EqualTo(new[] { 11L }));
    }

    /// <summary>队列内直接保存时，钩子异常也只记录一次，并且继续抛出。</summary>
    [Test]
    public async Task InlineHookFailure_RecordsOnce()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann");
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        var clock = new ManualSaveClock();
        using var service = new SaveService(queue, cache, players, clock);
        service.BeforePersistAsync = (_, _) => throw new InvalidOperationException("hook-fail");

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Hook");
            Assert.ThrowsAsync<InvalidOperationException>(async () => await service.FlushPlayerAsync(11, ct));
        }, CancellationToken.None);

        Assert.That(service.FailedSaveCount, Is.EqualTo(1));
        Assert.That(service.LastFailure, Is.InstanceOf<InvalidOperationException>());
        Assert.That(service.ConfirmedSaveCount, Is.Zero);
        Assert.That(cache.CopyHoldCount(11), Is.Zero);
        Assert.That(cache.CopyDirtyPlayerIds(), Is.EqualTo(new[] { 11L }));
    }

    /// <summary>等到条件成立。超时则失败，避免关闭或停止测试挂住。</summary>
    /// <param name="condition">要成立的观察条件。</param>
    static async Task WaitUntilAsync(Func<bool> condition)
    {
        long start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 5000)
                Assert.Fail("等待条件超时");
            await Task.Yield();
        }
    }

    static SqliteStore OpenStore(IsolatedDirectory dir)
    {
        var store = new SqliteStore();
        store.Initialize(DatabasePath(dir));
        return store;
    }

    static void Seed(IsolatedDirectory dir, long playerId, long accountId, string nickname)
    {
        using var connection = Open(DatabasePath(dir));
        Execute(connection, string.Format(CultureInfo.InvariantCulture,
            "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES ({0}, 'user{0}', 'hash', 'salt', 1);",
            accountId));
        Execute(connection, string.Format(CultureInfo.InvariantCulture,
            "INSERT INTO player (id, account_id, nickname) VALUES ({0}, {1}, '{2}');",
            playerId, accountId, nickname));
    }

    static string ReadNickname(IsolatedDirectory dir, long playerId)
    {
        using var connection = Open(DatabasePath(dir));
        return ScalarString(connection, string.Format(CultureInfo.InvariantCulture,
            "SELECT nickname FROM player WHERE id = {0};", playerId));
    }

    static long ReadSavedRevision(IsolatedDirectory dir, long playerId)
    {
        using var connection = Open(DatabasePath(dir));
        return ScalarLong(connection, string.Format(CultureInfo.InvariantCulture,
            "SELECT saved_revision FROM player_snapshot WHERE player_id = {0};", playerId));
    }

    static long CountSnapshots(IsolatedDirectory dir)
    {
        using var connection = Open(DatabasePath(dir));
        return ScalarLong(connection, "SELECT COUNT(*) FROM player_snapshot;");
    }

    static string DatabasePath(IsolatedDirectory dir)
    {
        return Path.Combine(dir.FullPath, "game.db");
    }

    static SqliteConnection Open(string database)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingSaveServiceTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingSaveServiceTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
