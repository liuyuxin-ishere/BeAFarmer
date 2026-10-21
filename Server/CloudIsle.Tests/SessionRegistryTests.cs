using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;

namespace CloudIsle.Tests;

/// <summary>SessionRegistry 的顶号、断线、取消和玩家队列配合。</summary>
public sealed class SessionRegistryTests
{
    /// <summary>外部关闭已开始而业务回调尚未结束时，也不能重新绑定该连接。</summary>
    [Test]
    public async Task ClosingSession_BeforeCompletion_BindIsRejected()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        var entered = NewSignal();
        var release = NewSignal();
        using var input = new MemoryStream(FrameCodec.Encode(1, 1, Array.Empty<byte>()));
        ClientSession session = ClientSession.CreateForTransport(901, input, new RecordingTransport(), 8,
            async (_, _, _) =>
            {
                entered.TrySetResult();
                // 故意等待业务屏障，使关闭已开始而 Completion 仍未完成。
                await release.Task;
            });
        Task run = session.RunAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task closing = session.CloseAsync("外部关闭", CancellationToken.None);
            Assert.That(session.IsCloseRequested, Is.True);
            Assert.That(session.Completion.IsCompleted, Is.False);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await registry.BindAsync(9, session, CancellationToken.None));
            Assert.That(registry.MappedPlayerCount, Is.Zero);
            Assert.That(registry.MappedSessionCount, Is.Zero);
            Assert.That(registry.WatchCount, Is.Zero);
            Assert.That(closing.IsCompleted, Is.False);
        }
        finally
        {
            release.TrySetResult();
            await session.CloseAsync("测试收尾", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>B 替换 A 后，A 只有一帧正式踢线，B 仍是当前连接。</summary>
    [Test]
    public async Task Replace_SendsOneKickAndKeepsNewCurrent()
    {
        using var queue = new PlayerQueue();
        var logs = new List<string>();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), message => logs.Add(message));
        await using var older = await LiveSession.StartAsync(11, new RecordingTransport());
        await using var newer = await LiveSession.StartAsync(12, new RecordingTransport());

        await registry.BindAsync(4, older.Session, CancellationToken.None);
        await registry.BindAsync(4, newer.Session, CancellationToken.None);

        AssertSingleKick(older.Transport.Snapshot());
        Assert.That(newer.Transport.Snapshot(), Is.Empty);
        await older.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(4, newer.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(4, older.Session.SessionId), Is.False);
        Assert.That(logs.Exists(message => message.Contains("不代表对端已经处理", StringComparison.Ordinal)), Is.True);
        await DrainAsync(registry, older, newer);
    }

    /// <summary>旧连接失去当前身份后才完成，完成回调不能清掉新连接。</summary>
    [Test]
    public async Task LateClose_DoesNotRemoveReplacement()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), static _ => { });
        var blocking = new BlockingTransport();
        await using var older = await LiveSession.StartAsync(21, blocking);
        await using var newer = await LiveSession.StartAsync(22, new RecordingTransport());

        await registry.BindAsync(5, older.Session, CancellationToken.None);
        Task replacing = registry.BindAsync(5, newer.Session, CancellationToken.None);
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(registry.IsCurrent(5, newer.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(5, older.Session.SessionId), Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.BindAsync(9, older.Session, CancellationToken.None));
        Assert.That(registry.IsCurrent(5, newer.Session.SessionId), Is.True);

        blocking.Release();
        await replacing.WaitAsync(TimeSpan.FromSeconds(5));
        await older.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(5, newer.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(5, older.Session.SessionId), Is.False);
        AssertSingleKick(blocking.Snapshot());
        await DrainAsync(registry, older, newer);
    }

    /// <summary>连续三次绑定后，只有最后一条还是当前连接。</summary>
    [Test]
    public async Task ThreeReplacements_OnlyLastStaysCurrent()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var first = await LiveSession.StartAsync(31, new RecordingTransport());
        await using var second = await LiveSession.StartAsync(32, new RecordingTransport());
        await using var third = await LiveSession.StartAsync(33, new RecordingTransport());

        await registry.BindAsync(6, first.Session, CancellationToken.None);
        await registry.BindAsync(6, second.Session, CancellationToken.None);
        await registry.BindAsync(6, third.Session, CancellationToken.None);

        AssertSingleKick(first.Transport.Snapshot());
        AssertSingleKick(second.Transport.Snapshot());
        Assert.That(third.Transport.Snapshot(), Is.Empty);
        Assert.That(registry.IsCurrent(6, third.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(6, first.Session.SessionId), Is.False);
        Assert.That(registry.IsCurrent(6, second.Session.SessionId), Is.False);
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(1));
        await DrainAsync(registry, first, second, third);
    }

    /// <summary>同一会话再次绑定不向自己发踢线。</summary>
    [Test]
    public async Task RebindSameSession_DoesNotKickItself()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(41, new RecordingTransport());

        await registry.BindAsync(7, session.Session, CancellationToken.None);
        await registry.BindAsync(7, session.Session, CancellationToken.None);

        Assert.That(session.Transport.Snapshot(), Is.Empty);
        Assert.That(registry.IsCurrent(7, session.Session.SessionId), Is.True);
        Assert.That(registry.WatchCount, Is.EqualTo(1));
        Assert.That(session.Session.Completion.IsCompleted, Is.False);
        await DrainAsync(registry, session);
    }

    /// <summary>同一连接改到另一名玩家后，只属于新玩家，并踢掉新玩家原来的连接。</summary>
    [Test]
    public async Task MoveSessionToOtherPlayer_DropsPreviousOwner()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var moving = await LiveSession.StartAsync(51, new RecordingTransport());
        await using var previous = await LiveSession.StartAsync(52, new RecordingTransport());

        await registry.BindAsync(1, moving.Session, CancellationToken.None);
        await registry.BindAsync(2, previous.Session, CancellationToken.None);
        await registry.BindAsync(2, moving.Session, CancellationToken.None);

        Assert.That(moving.Transport.Snapshot(), Is.Empty);
        AssertSingleKick(previous.Transport.Snapshot());
        Assert.That(registry.IsCurrent(2, moving.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(1, moving.Session.SessionId), Is.False);
        Assert.That(registry.IsCurrent(2, previous.Session.SessionId), Is.False);
        Assert.That(registry.TryGetPlayerId(moving.Session.SessionId, out long owner), Is.True);
        Assert.That(owner, Is.EqualTo(2));
        Assert.That(registry.TryGetPlayerId(previous.Session.SessionId, out _), Is.False);
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(1));
        Assert.That(registry.MappedSessionCount, Is.EqualTo(1));
        await DrainAsync(registry, moving, previous);
    }

    /// <summary>两条队列同时绑定同一连接时，结束后只剩一个玩家。</summary>
    [Test]
    public async Task ConcurrentBindSameSession_BelongsToOnePlayer()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(61, new RecordingTransport());
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var release = NewSignal();

        Task first = queue.EnqueueAsync(1, async _ =>
        {
            firstEntered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        Task second = queue.EnqueueAsync(2, async _ =>
        {
            secondEntered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));

        Task bindFirst = registry.BindAsync(1, session.Session, CancellationToken.None);
        Task bindSecond = registry.BindAsync(2, session.Session, CancellationToken.None);
        release.TrySetResult();
        await Task.WhenAll(first, second, bindFirst, bindSecond).WaitAsync(TimeSpan.FromSeconds(5));

        int owners = (registry.IsCurrent(1, session.Session.SessionId) ? 1 : 0)
            + (registry.IsCurrent(2, session.Session.SessionId) ? 1 : 0);
        Assert.That(owners, Is.EqualTo(1));
        Assert.That(session.Transport.Snapshot(), Is.Empty);
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(1));
        Assert.That(registry.MappedSessionCount, Is.EqualTo(1));
        await DrainAsync(registry, session);
    }

    /// <summary>对端结束后，映射和观察集合不再保留这条会话。</summary>
    [Test]
    public async Task NaturalDisconnect_ReleasesMapsAndWatch()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(71, new RecordingTransport());
        await registry.BindAsync(8, session.Session, CancellationToken.None);
        Assert.That(registry.WatchCount, Is.EqualTo(1));

        session.Stream.End();
        await session.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await registry.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(registry.IsCurrent(8, session.Session.SessionId), Is.False);
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(registry.MappedSessionCount, Is.EqualTo(0));
        Assert.That(registry.WatchCount, Is.EqualTo(0));
        Assert.That(registry.RetiringCount, Is.EqualTo(0));
    }

    /// <summary>已经关闭的会话不能再登记为在线。</summary>
    [Test]
    public async Task ClosedSession_BindIsRejected()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(81, new RecordingTransport());
        await session.Session.CloseAsync("预先关闭", CancellationToken.None);
        await session.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.BindAsync(3, session.Session, CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("已关闭"));
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(registry.WatchCount, Is.EqualTo(0));
        await DrainAsync(registry, session);
    }

    /// <summary>旧连接写出失败时，新连接仍然留下，旧连接仍被关闭。</summary>
    [Test]
    public async Task KickSendFailure_StillClosesOldAndKeepsNew()
    {
        using var queue = new PlayerQueue();
        var logs = new List<string>();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), message => logs.Add(message));
        await using var older = await LiveSession.StartAsync(91, new FailingTransport());
        await using var newer = await LiveSession.StartAsync(92, new RecordingTransport());

        await registry.BindAsync(10, older.Session, CancellationToken.None);
        await registry.BindAsync(10, newer.Session, CancellationToken.None);

        Assert.That(registry.IsCurrent(10, newer.Session.SessionId), Is.True);
        Assert.That(logs.Exists(message => message.Contains("未能确认送达", StringComparison.Ordinal)), Is.True);
        Assert.That(logs.Exists(message => message.Contains("不保证对方收到", StringComparison.Ordinal)), Is.True);
        await older.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(10, newer.Session.SessionId), Is.True);
        await DrainAsync(registry, older, newer);
    }

    /// <summary>旧连接不响应时，绑定在预算内返回，不占住玩家队列。</summary>
    [Test]
    public async Task KickSendBlocks_ReturnsWithinBudget()
    {
        using var queue = new PlayerQueue();
        var logs = new List<string>();
        var registry = new SessionRegistry(queue, TimeSpan.FromMilliseconds(200), message => logs.Add(message));
        var blocking = new BlockingTransport();
        await using var older = await LiveSession.StartAsync(101, blocking);
        await using var newer = await LiveSession.StartAsync(102, new RecordingTransport());
        await registry.BindAsync(11, older.Session, CancellationToken.None);

        var clock = Stopwatch.StartNew();
        await registry.BindAsync(11, newer.Session, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();

        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1.5)));
        Assert.That(registry.IsCurrent(11, newer.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(11, older.Session.SessionId), Is.False);
        Assert.That(logs.Exists(message => message.Contains("未能确认送达", StringComparison.Ordinal)), Is.True);
        Assert.That(logs.Exists(message => message.Contains("不再等待", StringComparison.Ordinal)), Is.True);
        await older.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await DrainAsync(registry, older, newer);
    }

    /// <summary>改映射前取消不留下映射或观察。</summary>
    [Test]
    public async Task CancelBeforeMap_LeavesNoMapping()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(111, new RecordingTransport());
        var entered = NewSignal();
        var release = NewSignal();
        Task hold = queue.EnqueueAsync(12, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancel = new CancellationTokenSource();
        Task binding = registry.BindAsync(12, session.Session, cancel.Token);
        Assert.That(registry.IsCurrent(12, session.Session.SessionId), Is.False);
        cancel.Cancel();
        release.TrySetResult();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await binding.WaitAsync(TimeSpan.FromSeconds(5)));
        await hold.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(registry.MappedSessionCount, Is.EqualTo(0));
        Assert.That(registry.WatchCount, Is.EqualTo(0));
        await DrainAsync(registry, session);
    }

    /// <summary>映射已经换成新连接后再取消，新连接保留，旧连接仍收尾。</summary>
    [Test]
    public async Task CancelAfterMap_KeepsNewAndFinishesOld()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), static _ => { });
        var blocking = new BlockingTransport();
        await using var older = await LiveSession.StartAsync(121, blocking);
        await using var newer = await LiveSession.StartAsync(122, new RecordingTransport());
        await registry.BindAsync(13, older.Session, CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        Task replacing = registry.BindAsync(13, newer.Session, cancel.Token);
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(13, newer.Session.SessionId), Is.True);
        cancel.Cancel();
        Assert.That(registry.IsCurrent(13, newer.Session.SessionId), Is.True);

        blocking.Release();
        await replacing.WaitAsync(TimeSpan.FromSeconds(5));
        await older.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(13, newer.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(13, older.Session.SessionId), Is.False);
        await DrainAsync(registry, older, newer);
    }

    /// <summary>玩家动作内部再绑定同一玩家时，动作能够结束。</summary>
    [Test]
    public async Task BindInsidePlayerAction_DoesNotDeadlock()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(131, new RecordingTransport());
        var finished = NewSignal();

        Task outer = queue.EnqueueAsync(14, async ct =>
        {
            await registry.BindAsync(14, session.Session, ct);
            finished.TrySetResult();
        }, CancellationToken.None);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await outer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(14, session.Session.SessionId), Is.True);
        await DrainAsync(registry, session);
    }

    /// <summary>两名玩家的踢线可以同时停在写出上。</summary>
    [Test]
    public async Task DifferentPlayers_ReplaceInParallel()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), static _ => { });
        var firstOldTransport = new BlockingTransport();
        var secondOldTransport = new BlockingTransport();
        await using var firstOld = await LiveSession.StartAsync(141, firstOldTransport);
        await using var secondOld = await LiveSession.StartAsync(142, secondOldTransport);
        await using var firstNew = await LiveSession.StartAsync(143, new RecordingTransport());
        await using var secondNew = await LiveSession.StartAsync(144, new RecordingTransport());
        await registry.BindAsync(15, firstOld.Session, CancellationToken.None);
        await registry.BindAsync(16, secondOld.Session, CancellationToken.None);

        Task firstReplace = registry.BindAsync(15, firstNew.Session, CancellationToken.None);
        Task secondReplace = registry.BindAsync(16, secondNew.Session, CancellationToken.None);
        await Task.WhenAll(firstOldTransport.Entered, secondOldTransport.Entered).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(firstReplace.IsCompleted, Is.False);
        Assert.That(secondReplace.IsCompleted, Is.False);

        firstOldTransport.Release();
        secondOldTransport.Release();
        await Task.WhenAll(firstReplace, secondReplace).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(15, firstNew.Session.SessionId), Is.True);
        Assert.That(registry.IsCurrent(16, secondNew.Session.SessionId), Is.True);
        await DrainAsync(registry, firstOld, secondOld, firstNew, secondNew);
    }

    /// <summary>上下文里的会话编号来自服务端对象，玩家编号来自注册表。</summary>
    [Test]
    public async Task CreateRequestContext_UsesServerSessionAndRegistryPlayer()
    {
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var session = await LiveSession.StartAsync(151, new RecordingTransport());

        RequestContext before = registry.CreateRequestContext(session.Session, 1005, 3);
        Assert.That(before.SessionId, Is.EqualTo(session.Session.SessionId));
        Assert.That(before.PlayerId, Is.EqualTo(0));
        Assert.That(before.MsgId, Is.EqualTo((ushort)1005));
        Assert.That(before.Seq, Is.EqualTo(3u));

        await registry.BindAsync(18, session.Session, CancellationToken.None);
        ushort loginId = MsgRegistry.GetId(typeof(LoginReq));
        RequestContext after = registry.CreateRequestContext(session.Session, loginId, 4);
        Assert.That(after.SessionId, Is.EqualTo(151));
        Assert.That(after.PlayerId, Is.EqualTo(18));
        Assert.That(after.MsgId, Is.EqualTo(loginId));
        Assert.That(after.Seq, Is.EqualTo(4u));
        Assert.That(registry.TryGetCurrentSession(18, out ClientSession? current), Is.True);
        Assert.That(current!.SessionId, Is.EqualTo(151));
        await DrainAsync(registry, session);
    }

    /// <summary>真实环回连接上，后进入的会话顶掉先进入的会话。</summary>
    [Test]
    public async Task Loopback_ReplacementReceivesFormalKick()
    {
        ClientSession? older = null;
        ClientSession? newer = null;
        var olderReady = NewSignal();
        var newerReady = NewSignal();
        await using var gate = new ServerGate(async (session, packet, ct) =>
        {
            if (packet.Seq == 1)
            {
                older = session;
                olderReady.TrySetResult();
                return;
            }

            if (packet.Seq == 2)
            {
                newer = session;
                newerReady.TrySetResult();
                return;
            }

            await session.SendAsync(new Packet(packet.MsgId, packet.Seq, packet.Body.ToArray()), ct);
        });

        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue);
        await using var clientA = await OwnedClient.ConnectAsync(gate.Port);
        await using var clientB = await OwnedClient.ConnectAsync(gate.Port);
        await clientA.Stream.WriteAsync(FrameCodec.Encode(1, 1, new byte[] { 1 }));
        await clientB.Stream.WriteAsync(FrameCodec.Encode(1, 2, new byte[] { 2 }));
        await Task.WhenAll(olderReady.Task, newerReady.Task).WaitAsync(TimeSpan.FromSeconds(5));

        await registry.BindAsync(19, older!, CancellationToken.None);
        await registry.BindAsync(19, newer!, CancellationToken.None);

        Packet kick = await ReadFrameAsync(clientA.Stream);
        Assert.That(kick.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(KickNtf))));
        Assert.That(kick.Seq, Is.EqualTo(0u));
        KickNtf notification = KickNtf.Parser.ParseFrom(kick.Body);
        Assert.That(notification.Reason, Is.EqualTo(KickReason.KickElsewhere));
        await AssertPeerEndedAsync(clientA.Stream);
        await older!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.IsCurrent(19, newer!.SessionId), Is.True);
        Assert.That(registry.IsCurrent(19, older.SessionId), Is.False);

        await clientB.Stream.WriteAsync(FrameCodec.Encode(7, 3, new byte[] { 9 }));
        Packet echo = await ReadFrameAsync(clientB.Stream);
        Assert.That(echo.MsgId, Is.EqualTo((ushort)7));
        Assert.That(echo.Seq, Is.EqualTo(3u));
        Assert.That(echo.Body, Is.EqualTo(new byte[] { 9 }));

        await newer.CloseAsync("测试结束", CancellationToken.None);
        await registry.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(registry.MappedSessionCount, Is.EqualTo(0));
        Assert.That(registry.WatchCount, Is.EqualTo(0));
        Assert.That(registry.RetiringCount, Is.EqualTo(0));
    }

    /// <summary>关掉全部会话后，注册表不再保留历史项。</summary>
    static async Task DrainAsync(SessionRegistry registry, params LiveSession[] sessions)
    {
        foreach (LiveSession session in sessions)
            await session.DisposeAsync();
        await registry.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(registry.MappedSessionCount, Is.EqualTo(0));
        Assert.That(registry.WatchCount, Is.EqualTo(0));
        Assert.That(registry.RetiringCount, Is.EqualTo(0));
    }

    /// <summary>缓冲里只能有一帧顶号通知。</summary>
    /// <param name="bytes">这条连接写出的全部字节。</param>
    static void AssertSingleKick(byte[] bytes)
    {
        List<Packet> frames = DecodeAll(bytes);
        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(KickNtf))));
        Assert.That(frames[0].Seq, Is.EqualTo(0u));
        KickNtf notification = KickNtf.Parser.ParseFrom(frames[0].Body);
        Assert.That(notification.Reason, Is.EqualTo(KickReason.KickElsewhere));
    }

    /// <summary>按帧边界拆开已经写出的字节。</summary>
    /// <param name="bytes">一条连接上按顺序拼好的字节。</param>
    static List<Packet> DecodeAll(byte[] bytes)
    {
        var frames = new List<Packet>();
        while (bytes.Length > 0)
        {
            DecodeStatus status = FrameCodec.TryDecode(bytes, out Packet packet, out int consumed);
            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            frames.Add(packet);
            bytes = bytes.AsSpan(consumed).ToArray();
        }

        return frames;
    }

    /// <summary>从套接字读出一整帧。</summary>
    /// <param name="stream">客户端网络流。</param>
    static async Task<Packet> ReadFrameAsync(NetworkStream stream)
    {
        byte[] header = await ReadExactAsync(stream, 4);
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        Assert.That(payloadLength, Is.InRange((uint)FrameCodec.HeaderSize, (uint)FrameCodec.MaxPayloadLength));
        byte[] rest = await ReadExactAsync(stream, (int)payloadLength);
        byte[] frame = new byte[header.Length + rest.Length];
        header.CopyTo(frame, 0);
        rest.CopyTo(frame, header.Length);
        DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumed);
        Assert.That(status, Is.EqualTo(DecodeStatus.Success));
        Assert.That(consumed, Is.EqualTo(frame.Length));
        return packet;
    }

    /// <summary>读满指定长度。对端提前结束则失败。</summary>
    /// <param name="stream">客户端网络流。</param>
    /// <param name="count">要读的字节数。</param>
    static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int got = 0;
        while (got < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(got, count - got));
            if (read == 0)
                throw new EndOfStreamException("对端提前结束");
            got += read;
        }

        return buffer;
    }

    /// <summary>对端关闭后，再读要么得到 0，要么得到连接错误。</summary>
    /// <param name="stream">客户端网络流。</param>
    static async Task AssertPeerEndedAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            int read = await stream.ReadAsync(new byte[8], timeout.Token);
            Assert.That(read, Is.EqualTo(0));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Assert.That(timeout.IsCancellationRequested, Is.False, "等待对端结束超时");
        }
    }

    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>一条已经跑起来的测试会话。</summary>
    sealed class LiveSession : IAsyncDisposable
    {
        LiveSession(ClientSession session, RecordingOrBlocking transport, GateStream stream, Task run)
        {
            Session = session;
            Transport = transport;
            Stream = stream;
            _run = run;
        }

        readonly Task _run;

        /// <summary>被绑定的服务端会话。</summary>
        public ClientSession Session { get; }

        /// <summary>这条会话的写出记录或阻塞控制。</summary>
        public RecordingOrBlocking Transport { get; }

        /// <summary>用来制造对端结束的读流。</summary>
        public GateStream Stream { get; }

        /// <summary>创建会话并等到收发循环已经开始。</summary>
        /// <param name="sessionId">测试指定的会话编号。</param>
        /// <param name="transport">可控写出端。</param>
        public static async Task<LiveSession> StartAsync(long sessionId, RecordingOrBlocking transport)
        {
            var stream = new GateStream();
            ClientSession session = ClientSession.CreateForTransport(sessionId, stream, transport, 8, null);
            Task run = session.RunAsync(CancellationToken.None);
            await Task.Yield();
            return new LiveSession(session, transport, stream, run);
        }

        /// <summary>结束读流并等待会话任务。</summary>
        public async ValueTask DisposeAsync()
        {
            Stream.End();
            try
            {
                await Session.CloseAsync("测试结束", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }

            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>测试传输的共同取字节口。</summary>
    abstract class RecordingOrBlocking : IFrameTransport
    {
        /// <summary>目前已经成功写出的字节。</summary>
        public abstract byte[] Snapshot();

        /// <summary>写出一段。返回值可以小于缓冲。</summary>
        /// <param name="buffer">这一段待写字节。</param>
        /// <param name="cancellationToken">会话取消时停止等待。</param>
        public abstract ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    }

    /// <summary>把写出的字节按顺序留下。</summary>
    sealed class RecordingTransport : RecordingOrBlocking
    {
        readonly List<byte> _bytes = new();

        /// <summary>复制目前的全部字节。</summary>
        public override byte[] Snapshot()
        {
            lock (_bytes)
                return _bytes.ToArray();
        }

        /// <summary>整段收下并立刻完成。</summary>
        /// <param name="buffer">这一段待写字节。</param>
        /// <param name="cancellationToken">已经取消时不再记录。</param>
        public override ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_bytes)
            {
                ReadOnlySpan<byte> span = buffer.Span;
                for (int i = 0; i < span.Length; i++)
                    _bytes.Add(span[i]);
            }

            return ValueTask.FromResult(buffer.Length);
        }
    }

    /// <summary>第一次写出停住，直到放行或取消。</summary>
    sealed class BlockingTransport : RecordingOrBlocking
    {
        readonly List<byte> _bytes = new();
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _seen;

        /// <summary>第一次写出已经进入等待。</summary>
        public Task Entered => _entered.Task;

        /// <summary>放行正在等待的写出。</summary>
        public void Release() => _release.TrySetResult();

        /// <summary>复制放行之后写下的字节。</summary>
        public override byte[] Snapshot()
        {
            lock (_bytes)
                return _bytes.ToArray();
        }

        /// <summary>第一次调用先报告进入，再等放行。取消时不记录字节。</summary>
        /// <param name="buffer">这一段待写字节。</param>
        /// <param name="cancellationToken">会话关闭时取消等待。</param>
        public override async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _seen, 1) == 0)
                _entered.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] copy = buffer.ToArray();
            lock (_bytes)
                _bytes.AddRange(copy);

            return buffer.Length;
        }
    }

    /// <summary>任何写出都失败。</summary>
    sealed class FailingTransport : RecordingOrBlocking
    {
        /// <summary>没有成功写出的字节。</summary>
        public override byte[] Snapshot() => Array.Empty<byte>();

        /// <summary>立即用 IO 异常失败。</summary>
        /// <param name="buffer">被拒绝的字节。</param>
        /// <param name="cancellationToken">本实现不等待。</param>
        public override ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            throw new IOException("写出失败");
        }
    }

    /// <summary>读端一直等待，End 之后返回 0。</summary>
    sealed class GateStream : Stream
    {
        readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>让下一次读返回流结束。</summary>
        public void End() => _done.TrySetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>等到结束或取消。取消继续抛出，结束返回 0。</summary>
        /// <param name="buffer">调用方提供的缓冲。结束时不写入。</param>
        /// <param name="cancellationToken">会话关闭时取消等待。</param>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var _ = cancellationToken.Register(() => _done.TrySetResult());
            await _done.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            _done.TrySetResult();
            base.Dispose(disposing);
        }
    }

    /// <summary>端口 0 的环回监听。</summary>
    sealed class ServerGate : IAsyncDisposable
    {
        readonly CancellationTokenSource _cts = new();
        readonly TcpServer _server;

        /// <summary>开始监听。</summary>
        /// <param name="onPacket">每个完整包交给它。</param>
        public ServerGate(Func<ClientSession, Packet, CancellationToken, Task> onPacket)
        {
            _server = new TcpServer(onPacket, 8);
            Run = _server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), _cts.Token);
        }

        /// <summary>监听任务。</summary>
        public Task Run { get; }

        /// <summary>系统分配的端口。</summary>
        public int Port
        {
            get
            {
                _server.Started.GetAwaiter().GetResult();
                return _server.LocalEndpoint!.Port;
            }
        }

        /// <summary>停止监听并等待会话结束。</summary>
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            Task finished = await Task.WhenAny(Run, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.That(finished, Is.SameAs(Run), "停止服务器超时");
            await Run;
            _cts.Dispose();
        }
    }

    /// <summary>测试用的客户端套接字。</summary>
    sealed class OwnedClient : IAsyncDisposable
    {
        OwnedClient(TcpClient client) => Client = client;

        /// <summary>已连接的客户端。</summary>
        public TcpClient Client { get; }

        /// <summary>客户端网络流。</summary>
        public NetworkStream Stream => Client.GetStream();

        /// <summary>连到本机端口。</summary>
        /// <param name="port">服务器实际监听端口。</param>
        public static async Task<OwnedClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new OwnedClient(client);
        }

        /// <summary>关闭套接字。</summary>
        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
