using System.Text;
using CloudIsle.Net;
using CloudIsle.Server.Players;

namespace CloudIsle.Tests;

/// <summary>
/// 幂等窗口的单次执行、冲突、隔离、副本、FIFO 上限、失败重试和队列上下文。
/// 操作类型 49001 只存在于本夹具，不写入生产 MsgRegistry，也不是新的玩法消息。
/// </summary>
public sealed class IdempotencyTests
{
    /// <summary>夹具使用的操作类型。只参与摘要和封帧，不注册为生产消息。</summary>
    const ushort FixtureOperation = 49001;

    /// <summary>
    /// 同一玩家的两次相同提交都先入队，再放开队列。业务只跑一次，两帧正文相同但 seq 各自使用本次请求值。
    /// </summary>
    [Test]
    public async Task DuplicateOp_ConcurrentSameHash_ExecutesOnceAndResealsNewSeq()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        var probe = new WriteProbe();
        byte[] payload = Encoding.UTF8.GetBytes("nickname=Ann");
        string hash = IdempotencyStore.ComputeRequestHash(FixtureOperation, payload);
        Array.Clear(payload);

        Task hold = queue.EnqueueAsync(7, async token =>
        {
            probe.HoldEntered.TrySetResult();
            await probe.ReleaseHold.Task;
        }, CancellationToken.None);
        await probe.HoldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<IdempotentReply> first = store.ExecuteAsync(
            queue, 7, 3, hash, token => Task.FromResult(probe.Apply("Ann")), CancellationToken.None);
        Task<IdempotentReply> second = store.ExecuteAsync(
            queue, 7, 3, hash, token => Task.FromResult(probe.Apply("Ann")), CancellationToken.None);
        probe.ReleaseHold.TrySetResult();

        IdempotentReply[] replies = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await hold.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(probe.Writes, Is.EqualTo(1));
        Assert.That(probe.Nickname, Is.EqualTo("Ann"));
        Assert.That(replies[0].CacheHit, Is.False);
        Assert.That(replies[1].CacheHit, Is.True);
        Assert.That(store.CountFor(7), Is.EqualTo(1));
        Assert.That(replies[0].ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Assert.That(replies[1].ResponseBody, Is.EqualTo(replies[0].ResponseBody));

        Packet older = Reseal(FixtureOperation, 11, replies[0].ResponseBody);
        Packet newer = Reseal(FixtureOperation, 22, replies[1].ResponseBody);
        Assert.That(older.Seq, Is.EqualTo(11));
        Assert.That(newer.Seq, Is.EqualTo(22));
        Assert.That(older.Body, Is.EqualTo(newer.Body));
        Assert.That(older.Body, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Assert.That(Encoding.UTF8.GetString(store.ConcatenateStoredMaterial(7)), Does.Not.Contain("nickname="));
    }

    /// <summary>同一 opId 换载荷后拒绝执行，原来的昵称和正文都还在。</summary>
    [Test]
    public async Task ConflictingPayload_IsRejectedAndKeepsOriginal()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        var probe = new WriteProbe();
        string original = IdempotencyStore.ComputeRequestHash(FixtureOperation, Encoding.UTF8.GetBytes("nickname=Ann"));
        string other = IdempotencyStore.ComputeRequestHash(FixtureOperation, Encoding.UTF8.GetBytes("nickname=Bea"));

        IdempotentReply saved = await store.ExecuteAsync(
            queue, 4, 8, original, token => Task.FromResult(probe.Apply("Ann")), CancellationToken.None);
        Assert.ThrowsAsync<IdempotencyConflictException>(async () => await store.ExecuteAsync(
            queue, 4, 8, other, token => Task.FromResult(probe.Apply("Bea")), CancellationToken.None));

        IdempotentReply replay = await store.ExecuteAsync(
            queue, 4, 8, original, token => Task.FromResult(probe.Apply("Ann")), CancellationToken.None);
        Assert.That(probe.Writes, Is.EqualTo(1));
        Assert.That(probe.Nickname, Is.EqualTo("Ann"));
        Assert.That(saved.CacheHit, Is.False);
        Assert.That(replay.CacheHit, Is.True);
        Assert.That(replay.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Assert.That(store.CountFor(4), Is.EqualTo(1));
    }

    /// <summary>不同玩家使用同一个 opId 时各自保留自己的结果。</summary>
    [Test]
    public async Task DifferentPlayers_SameOp_AreIndependent()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        var first = new WriteProbe();
        var second = new WriteProbe();
        string ann = IdempotencyStore.ComputeRequestHash(FixtureOperation, Encoding.UTF8.GetBytes("nickname=Ann"));
        string bea = IdempotencyStore.ComputeRequestHash(FixtureOperation, Encoding.UTF8.GetBytes("nickname=Bea"));

        IdempotentReply left = await store.ExecuteAsync(
            queue, 11, 4, ann, token => Task.FromResult(first.Apply("Ann")), CancellationToken.None);
        IdempotentReply right = await store.ExecuteAsync(
            queue, 12, 4, bea, token => Task.FromResult(second.Apply("Bea")), CancellationToken.None);
        IdempotentReply leftAgain = await store.ExecuteAsync(
            queue, 11, 4, ann, token => Task.FromResult(first.Apply("Ann")), CancellationToken.None);

        Assert.That(first.Writes, Is.EqualTo(1));
        Assert.That(second.Writes, Is.EqualTo(1));
        Assert.That(left.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Assert.That(right.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Bea")));
        Assert.That(leftAgain.CacheHit, Is.True);
        Assert.That(leftAgain.ResponseBody, Is.EqualTo(left.ResponseBody));
        Assert.That(store.CountFor(11), Is.EqualTo(1));
        Assert.That(store.CountFor(12), Is.EqualTo(1));
    }

    /// <summary>保存后修改输入，取出后修改输出，缓存里的正文仍是最初那一份。</summary>
    [Test]
    public async Task SavedAndLoadedBody_MutationsDoNotPolluteCache()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        byte[] input = { 9, 8, 7, 6 };
        byte[] expected = { 9, 8, 7, 6 };

        await queue.EnqueueAsync(3, token =>
        {
            store.Save(3, 4, "HASH-COPY", input);
            input[0] = 1;
            Assert.That(store.TryGet(3, 4, "HASH-COPY", out byte[] loaded), Is.True);
            Assert.That(loaded, Is.EqualTo(expected));
            Assert.That(loaded, Is.Not.SameAs(input));
            loaded[0] = 2;
            Assert.That(store.TryGet(3, 4, "HASH-COPY", out byte[] again), Is.True);
            Assert.That(again, Is.EqualTo(expected));
            Assert.That(again, Is.Not.SameAs(loaded));
            return Task.CompletedTask;
        }, CancellationToken.None);

        IdempotentReply reply = await store.ExecuteAsync(
            queue, 3, 4, "HASH-COPY", token => Task.FromResult(new byte[] { 0 }), CancellationToken.None);
        reply.ResponseBody[0] = 5;
        IdempotentReply replay = await store.ExecuteAsync(
            queue, 3, 4, "HASH-COPY", token => Task.FromResult(new byte[] { 0 }), CancellationToken.None);
        Assert.That(reply.CacheHit, Is.True);
        Assert.That(replay.CacheHit, Is.True);
        Assert.That(replay.ResponseBody, Is.EqualTo(expected));
        Assert.That(store.CountFor(3), Is.EqualTo(1));
    }

    /// <summary>
    /// 64 条之后再写入一条新操作，淘汰最早的一条。中途重复命中和相同内容的再次 Save 都不改变队头。
    /// </summary>
    [Test]
    public async Task Capacity64_EvictsOldestAndReplayDoesNotRefreshOrder()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        var bodies = new Dictionary<long, byte[]>();
        for (long opId = 1; opId <= IdempotencyStore.Capacity; opId++)
            bodies[opId] = Encoding.UTF8.GetBytes("n" + opId);

        await queue.EnqueueAsync(5, token =>
        {
            for (long opId = 1; opId <= IdempotencyStore.Capacity; opId++)
                store.Save(5, opId, "H" + opId, bodies[opId]);

            Assert.That(store.RetentionOrder(5)[0], Is.EqualTo(1));
            Assert.That(store.TryGet(5, 1, "H1", out _), Is.True);
            Assert.That(store.TryGet(5, 1, "H1", out _), Is.True);
            Assert.That(store.RetentionOrder(5)[0], Is.EqualTo(1));

            byte[] sixtyFive = Encoding.UTF8.GetBytes("n65");
            store.Save(5, 65, "H65", sixtyFive);
            long[] afterSixtyFive = store.RetentionOrder(5);
            Assert.That(store.CountFor(5), Is.EqualTo(IdempotencyStore.Capacity));
            Assert.That(afterSixtyFive[0], Is.EqualTo(2));
            Assert.That(afterSixtyFive[^1], Is.EqualTo(65));
            Assert.That(store.TryGet(5, 1, "H1", out _), Is.False);
            Assert.That(store.TryGet(5, 2, "H2", out byte[] stillTwo), Is.True);
            Assert.That(stillTwo, Is.EqualTo(bodies[2]));
            Assert.That(store.TryGet(5, 65, "H65", out byte[] newest), Is.True);
            Assert.That(newest, Is.EqualTo(sixtyFive));

            store.Save(5, 2, "H2", bodies[2]);
            Assert.That(store.RetentionOrder(5)[0], Is.EqualTo(2));
            store.Save(5, 66, "H66", Encoding.UTF8.GetBytes("n66"));
            Assert.That(store.CountFor(5), Is.EqualTo(IdempotencyStore.Capacity));
            Assert.That(store.RetentionOrder(5)[0], Is.EqualTo(3));
            Assert.That(store.TryGet(5, 2, "H2", out _), Is.False);
            Assert.That(store.TryGet(5, 3, "H3", out _), Is.True);
            Assert.That(store.TryGet(5, 66, "H66", out _), Is.True);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>失败不留下成功缓存。同一摘要再次提交会真正执行，其后的重复提交才命中。</summary>
    [Test]
    public async Task FailedAction_LeavesNoSuccessCache_AndRetryExecutes()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        var probe = new WriteProbe();
        string hash = IdempotencyStore.ComputeRequestHash(FixtureOperation, Encoding.UTF8.GetBytes("nickname=Ann"));
        bool fail = true;

        InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ExecuteAsync(queue, 6, 2, hash, token =>
            {
                probe.Apply("Ann");
                if (fail)
                    throw new InvalidOperationException("fixture-write-failed");
                return Task.FromResult(Encoding.UTF8.GetBytes(probe.Nickname));
            }, CancellationToken.None));

        Assert.That(error!.Message, Is.EqualTo("fixture-write-failed"));
        Assert.That(probe.Writes, Is.EqualTo(1));
        Assert.That(store.CountFor(6), Is.EqualTo(0));
        await queue.EnqueueAsync(6, token =>
        {
            Assert.That(store.TryGet(6, 2, hash, out _), Is.False);
            return Task.CompletedTask;
        }, CancellationToken.None);

        fail = false;
        IdempotentReply retried = await store.ExecuteAsync(queue, 6, 2, hash, token =>
        {
            probe.Apply("Ann");
            return Task.FromResult(Encoding.UTF8.GetBytes("Ann"));
        }, CancellationToken.None);
        IdempotentReply replay = await store.ExecuteAsync(queue, 6, 2, hash, token =>
        {
            probe.Apply("Ann");
            return Task.FromResult(Encoding.UTF8.GetBytes("Ann"));
        }, CancellationToken.None);

        Assert.That(probe.Writes, Is.EqualTo(2));
        Assert.That(retried.CacheHit, Is.False);
        Assert.That(replay.CacheHit, Is.True);
        Assert.That(replay.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Assert.That(store.CountFor(6), Is.EqualTo(1));
    }

    /// <summary>显式清理后该玩家计数为 0，另一名玩家的窗口还在。</summary>
    [Test]
    public async Task RemovePlayer_ClearsWindowCount()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        await store.ExecuteAsync(
            queue, 1, 1, "KEEP-A", token => Task.FromResult(Encoding.UTF8.GetBytes("Ann")), CancellationToken.None);
        await store.ExecuteAsync(
            queue, 2, 1, "KEEP-B", token => Task.FromResult(Encoding.UTF8.GetBytes("Bea")), CancellationToken.None);

        store.RemovePlayer(1);
        store.RemovePlayer(1);
        Assert.That(store.CountFor(1), Is.EqualTo(0));
        Assert.That(store.CountFor(2), Is.EqualTo(1));
        Assert.That(store.PlayerWindowCount, Is.EqualTo(1));

        await queue.EnqueueAsync(1, token =>
        {
            Assert.That(store.TryGet(1, 1, "KEEP-A", out _), Is.False);
            return Task.CompletedTask;
        }, CancellationToken.None);
        IdempotentReply remaining = await store.ExecuteAsync(
            queue, 2, 1, "KEEP-B", token => Task.FromResult(Encoding.UTF8.GetBytes("other")), CancellationToken.None);
        Assert.That(remaining.CacheHit, Is.True);
        Assert.That(remaining.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Bea")));
    }

    /// <summary>队列外、别的玩家动作里，以及非法编号，都不能读写窗口。</summary>
    [Test]
    public async Task OutsideQueueOrWrongPlayer_IsRejected()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        byte[] body = Encoding.UTF8.GetBytes("Ann");

        Assert.Throws<InvalidOperationException>(() => store.TryGet(1, 1, "HASH", out _));
        Assert.Throws<InvalidOperationException>(() => store.Save(1, 1, "HASH", body));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.RemovePlayer(0));
        Assert.That(store.CountFor(1), Is.EqualTo(0));

        await queue.EnqueueAsync(1, token =>
        {
            Assert.Throws<InvalidOperationException>(() => store.TryGet(2, 1, "HASH", out _));
            Assert.Throws<InvalidOperationException>(() => store.Save(2, 1, "HASH", body));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TryGet(0, 1, "HASH", out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TryGet(1, 0, "HASH", out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.TryGet(-5, 1, "HASH", out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(1, -1, "HASH", body));
            Assert.Throws<ArgumentNullException>(() => store.TryGet(1, 1, null!, out _));
            Assert.Throws<ArgumentException>(() => store.Save(1, 1, "   ", body));
            Assert.That(store.CountFor(1), Is.EqualTo(0));
            Assert.That(store.CountFor(2), Is.EqualTo(0));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>一名玩家停在屏障上时，另一名玩家的相同操作号仍能完成。</summary>
    [Test]
    public async Task DifferentPlayers_DoNotBlockEachOther()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        var blocked = new WriteProbe();
        var free = new WriteProbe();
        string hash = IdempotencyStore.ComputeRequestHash(FixtureOperation, Encoding.UTF8.GetBytes("nickname=Ann"));

        Task<IdempotentReply> waiting = store.ExecuteAsync(queue, 21, 5, hash, async token =>
        {
            blocked.HoldEntered.TrySetResult();
            await blocked.ReleaseHold.Task;
            return blocked.Apply("Ann");
        }, CancellationToken.None);
        await blocked.HoldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IdempotentReply finished = await store.ExecuteAsync(
            queue, 22, 5, hash, token => Task.FromResult(free.Apply("Bea")), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(waiting.IsCompleted, Is.False);
        Assert.That(finished.CacheHit, Is.False);
        Assert.That(free.Writes, Is.EqualTo(1));
        Assert.That(blocked.Writes, Is.EqualTo(0));

        blocked.ReleaseHold.TrySetResult();
        IdempotentReply released = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(released.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Assert.That(finished.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Bea")));
        Assert.That(blocked.Writes, Is.EqualTo(1));
    }

    /// <summary>
    /// 相同业务载荷配不同 seq 时正文一致，摘要却不读取 seq。
    /// 操作类型、载荷或摘要大小写变化后都不再等于原摘要。原始秘密载荷不出现在窗口材料里。
    /// </summary>
    [Test]
    public async Task RequestHash_UsesOperationAndPayloadNotSeq()
    {
        byte[] payload = Encoding.UTF8.GetBytes("password=secret-value");
        string hash = IdempotencyStore.ComputeRequestHash(FixtureOperation, payload);
        string again = IdempotencyStore.ComputeRequestHash(FixtureOperation, payload);
        string otherType = IdempotencyStore.ComputeRequestHash(49002, payload);
        string otherPayload = IdempotencyStore.ComputeRequestHash(
            FixtureOperation, Encoding.UTF8.GetBytes("password=other-value"));
        string lowered = hash.ToLowerInvariant();

        Packet first = Reseal(FixtureOperation, 5, payload);
        Packet second = Reseal(FixtureOperation, 9, payload);
        Assert.That(hash, Is.EqualTo(again));
        Assert.That(hash, Is.Not.EqualTo(otherType));
        Assert.That(hash, Is.Not.EqualTo(otherPayload));
        Assert.That(string.Equals(hash, lowered, StringComparison.Ordinal), Is.False);
        Assert.That(first.Body, Is.EqualTo(second.Body));
        Assert.That(first.Seq, Is.EqualTo(5));
        Assert.That(second.Seq, Is.EqualTo(9));
        Assert.That(IdempotencyStore.ComputeRequestHash(FixtureOperation, first.Body), Is.EqualTo(hash));

        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        Array.Clear(payload);
        IdempotentReply saved = await store.ExecuteAsync(
            queue, 9, 1, hash, token => Task.FromResult(Encoding.UTF8.GetBytes("Ann")), CancellationToken.None);
        await queue.EnqueueAsync(9, token =>
        {
            Assert.Throws<IdempotencyConflictException>(() => store.TryGet(9, 1, lowered, out _));
            Assert.That(store.TryGet(9, 1, hash, out byte[] body), Is.True);
            Assert.That(body, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
            return Task.CompletedTask;
        }, CancellationToken.None);

        string material = Encoding.UTF8.GetString(store.ConcatenateStoredMaterial(9));
        Assert.That(material, Does.Not.Contain("secret-value"));
        Assert.That(material, Does.Not.Contain("password="));
        Assert.That(saved.ResponseBody, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
        Packet resealed = Reseal(FixtureOperation, 41, saved.ResponseBody);
        Assert.That(resealed.Seq, Is.EqualTo(41));
        Assert.That(resealed.Body, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
    }

    /// <summary>同一操作再次保存相同正文不会替换；另一份正文或另一摘要都会拒绝。</summary>
    [Test]
    public async Task DuplicateSave_DoesNotReplaceStoredResult()
    {
        using var queue = new PlayerQueue();
        var store = new IdempotencyStore();
        byte[] first = Encoding.UTF8.GetBytes("Ann");
        byte[] second = Encoding.UTF8.GetBytes("Bea");

        await queue.EnqueueAsync(8, token =>
        {
            store.Save(8, 15, "SAME", first);
            store.Save(8, 15, "SAME", Encoding.UTF8.GetBytes("Ann"));
            Assert.Throws<IdempotencyConflictException>(() => store.Save(8, 15, "SAME", second));
            Assert.Throws<IdempotencyConflictException>(() => store.Save(8, 15, "OTHER", first));
            Assert.That(store.TryGet(8, 15, "SAME", out byte[] loaded), Is.True);
            Assert.That(loaded, Is.EqualTo(Encoding.UTF8.GetBytes("Ann")));
            Assert.That(store.CountFor(8), Is.EqualTo(1));
            Assert.That(store.RetentionOrder(8), Is.EqualTo(new[] { 15L }));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>用本次 seq 把业务正文封成一帧再解出来。</summary>
    /// <param name="operationType">帧里的消息编号。夹具固定用 49001，不登记到生产注册表。</param>
    /// <param name="seq">这一次请求的序号。重放时传入新值，而不是缓存里的旧值。</param>
    /// <param name="body">业务正文。方法会把它放进新帧。</param>
    /// <returns>解出来的包。调用方再核对 seq 和正文。</returns>
    static Packet Reseal(ushort operationType, uint seq, byte[] body)
    {
        byte[] frame = FrameCodec.Encode(operationType, seq, body);
        DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumed);
        Assert.That(status, Is.EqualTo(DecodeStatus.Success));
        Assert.That(consumed, Is.EqualTo(frame.Length));
        return packet;
    }

    /// <summary>夹具里的可观察写入。昵称和次数用来确认业务有没有真的执行。</summary>
    sealed class WriteProbe
    {
        /// <summary>创建屏障。续体异步执行，避免在释放方里面接着跑后半段。</summary>
        public WriteProbe()
        {
            HoldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>业务动作已经进入、并停在屏障前时完成。</summary>
        public TaskCompletionSource HoldEntered { get; }

        /// <summary>测试放行被挡住的业务动作。</summary>
        public TaskCompletionSource ReleaseHold { get; }

        /// <summary>成功进入业务函数的次数。缓存命中不会增加。</summary>
        public int Writes { get; private set; }

        /// <summary>最近一次业务函数写下的昵称。被拒绝的调用不会改它。</summary>
        public string Nickname { get; private set; } = "";

        /// <summary>记一次业务写入并返回昵称字节。调用方把返回值当作业务正文。</summary>
        /// <param name="nickname">这次要写成的昵称。会同时增加写入次数。</param>
        /// <returns>昵称的 UTF-8 字节，不含 seq。</returns>
        public byte[] Apply(string nickname)
        {
            Writes++;
            Nickname = nickname;
            return Encoding.UTF8.GetBytes(nickname);
        }
    }
}
