using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>经正式路由和真实 ClientSession 验证登录。临时库与 PoC 账号库分开。</summary>
public sealed class AccountLoginTests
{
    const long NowMs = 1_700_000_000_000L;

    /// <summary>正确密码返回 1006、注入的 UTC 毫秒、完整默认快照，以及可校验的令牌。</summary>
    [Test]
    public async Task CorrectPassword_ReturnsTokenSnapshotAndIdentity()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(41);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);

        LoginResp response = await world.LoginAsync(session, "ada", "secret-value", 8);

        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(response.ServerTime, Is.EqualTo(NowMs));
        Assert.That(response.NeedResVersion, Is.EqualTo(100));
        Assert.That(response.NeedConfigVersion, Is.EqualTo(100));
        Assert.That(response.Token.Length, Is.EqualTo(64));
        AssertInfo(response.Data, 0, "Ann", 1, 0, 200, 300, 5, 0, 60, 0);
        Assert.That(response.Data!.Bag.Count, Is.EqualTo(0));
        Assert.That(response.Data.Plots.Count, Is.EqualTo(0));
        Assert.That(response.Data.OrderProgress, Is.Null);
        long playerId = response.Data.Info.PlayerId;
        TokenValidation validation = await world.Tokens.ValidateAsync(response.Token, NowMs, CancellationToken.None);
        Assert.That(validation.Code, Is.EqualTo(0));
        Assert.That(validation.PlayerId, Is.EqualTo(playerId));
        Assert.That(world.Sessions.TryGetPlayerId(session.Session.SessionId, out long mapped), Is.True);
        Assert.That(mapped, Is.EqualTo(playerId));
        Assert.That(world.Sessions.IsCurrent(playerId, session.Session.SessionId), Is.True);
        RequestContext after = world.Sessions.CreateRequestContext(session.Session, 1005, 9);
        Assert.That(after.PlayerId, Is.EqualTo(playerId));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
        Assert.That(world.TokenExpiry(playerId), Is.EqualTo(NowMs + (long)TimeSpan.FromHours(24).TotalMilliseconds));
        Assert.That(string.Join("\n", world.Logs).Contains("secret-value"), Is.False);
        Assert.That(string.Join("\n", world.Logs).Contains(response.Token), Is.False);
    }

    /// <summary>错误密码和未知用户都回 202，不发令牌，不给快照，不绑定。</summary>
    [Test]
    public async Task WrongOrUnknownCredentials_Return202WithoutToken()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession wrong = await world.AddSessionAsync(42);
        await using LiveSession missing = await world.AddSessionAsync(43);
        await world.RegisterAsync(wrong, "ada", "secret-value", "Ann", 1);

        LoginResp wrongResponse = await world.LoginAsync(wrong, "ada", "other-value", 2);
        LoginResp missingResponse = await world.LoginAsync(missing, "missing-user", "secret-value", 3);

        AssertRejected(wrongResponse, 2, 202);
        AssertRejected(missingResponse, 3, 202);
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        Assert.That(string.Join("\n", world.Logs).Contains("secret-value"), Is.False);
        Assert.That(string.Join("\n", world.Logs).Contains("other-value"), Is.False);
    }

    /// <summary>协议整数不同返回 106，不撤销已有令牌，也不改当前连接。</summary>
    [Test]
    public async Task ProtoMismatch_Returns106WithoutRevokingOrBinding()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(44);
        await using LiveSession other = await world.AddSessionAsync(45);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp first = await world.LoginAsync(current, "ada", "secret-value", 4);
        int framesBefore = world.Frames(current).Count;

        LoginResp rejected = await world.LoginAsync(other, "ada", "secret-value", 5, protoVersion: 2);

        AssertRejected(rejected, 5, 106);
        Assert.That(rejected.NeedResVersion, Is.EqualTo(100));
        Assert.That(rejected.NeedConfigVersion, Is.EqualTo(100));
        Assert.That(rejected.Data, Is.Null);
        TokenValidation still = await world.Tokens.ValidateAsync(first.Token, NowMs, CancellationToken.None);
        Assert.That(still.Code, Is.EqualTo(0));
        Assert.That(still.PlayerId, Is.EqualTo(first.Data.Info.PlayerId));
        Assert.That(world.Sessions.IsCurrent(first.Data.Info.PlayerId, current.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(first.Data.Info.PlayerId, other.Session.SessionId), Is.False);
        Assert.That(CountKick(world.Frames(current)), Is.EqualTo(0));
        Assert.That(world.Frames(current).Count, Is.EqualTo(framesBefore));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
    }

    /// <summary>资源、配置或包体低于门槛时回 106。数值比较把 0.1.9 放在 0.1.10 之前。</summary>
    [Test]
    public async Task VersionFloors_RejectLowerAndAcceptHigher()
    {
        await using var world = await LoginWorld.OpenAsync(minimumApp: "0.1.10");
        await using LiveSession lowApp = await world.AddSessionAsync(46);
        await using LiveSession lowRes = await world.AddSessionAsync(47);
        await using LiveSession higher = await world.AddSessionAsync(48);
        await world.RegisterAsync(higher, "ada", "secret-value", "Ann", 1);

        LoginResp appRejected = await world.LoginAsync(lowApp, "ada", "secret-value", 2, appVersion: "0.1.9");
        LoginResp resRejected = await world.LoginAsync(lowRes, "ada", "secret-value", 3, resVersion: 99, configVersion: 100);
        LoginResp configRejected = await world.LoginAsync(lowRes, "ada", "secret-value", 4, resVersion: 100, configVersion: 99);
        LoginResp accepted = await world.LoginAsync(higher, "ada", "secret-value", 5, appVersion: "0.1.10", resVersion: 101, configVersion: 130);

        AssertRejected(appRejected, 2, 106);
        AssertRejected(resRejected, 3, 106);
        AssertRejected(configRejected, 4, 106);
        Assert.That(accepted.Code, Is.EqualTo(0));
        Assert.That(accepted.NeedResVersion, Is.EqualTo(100));
        Assert.That(accepted.NeedConfigVersion, Is.EqualTo(100));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
        Assert.That(string.Join("\n", world.Logs).Contains(accepted.Token), Is.False);
    }

    /// <summary>未配置最低包体版本时，空 app_version 不拒绝。0.1.10 不低于 0.1.9。</summary>
    [Test]
    public async Task AppRule_UsesNumericOrderOnlyWhenConfigured()
    {
        await using var open = await LoginWorld.OpenAsync(minimumApp: null);
        await using LiveSession blank = await open.AddSessionAsync(49);
        await open.RegisterAsync(blank, "ada", "secret-value", "Ann", 1);
        LoginResp blankApp = await open.LoginAsync(blank, "ada", "secret-value", 2, appVersion: "");
        Assert.That(blankApp.Code, Is.EqualTo(0));

        await using var strict = await LoginWorld.OpenAsync(minimumApp: "0.1.9");
        await using LiveSession newer = await strict.AddSessionAsync(50);
        await strict.RegisterAsync(newer, "bea", "secret-value", "Bea", 1);
        LoginResp numeric = await strict.LoginAsync(newer, "bea", "secret-value", 2, appVersion: "0.1.10");
        Assert.That(numeric.Code, Is.EqualTo(0));
        Assert.That(string.Join("\n", open.Logs).Contains(blankApp.Token), Is.False);
        Assert.That(string.Join("\n", strict.Logs).Contains(numeric.Token), Is.False);
    }

    /// <summary>数据库队列仍被占住时，不兼容协议已经返回 106。</summary>
    [Test]
    public async Task IncompatibleProto_ReturnsBeforePasswordUsesDatabase()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(51);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> hold = world.Store.ExecuteAsync(_ =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("数据库占用没有被放开");
            return 0;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Task pending = world.DispatchLoginAsync(session, "ada", "secret-value", 6, protoVersion: 9);
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(hold.IsCompleted, Is.False);
            LoginResp response = world.ReadLogin(session, 6);
            AssertRejected(response, 6, 106);
            Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        }
        finally
        {
            release.TrySetResult();
            await hold;
        }
    }

    /// <summary>已保存的完整快照原样返回，登录不把字段写回默认值。</summary>
    [Test]
    public async Task SavedSnapshot_IsReturnedWithoutReinitialize()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(52);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        long playerId = world.PlayerId("ada");
        PlayerFullData saved = SavedData(playerId, "Ann");
        await new PlayerStore(world.Store).SaveAsync(new PlayerState(playerId, saved, 3, 3), CancellationToken.None);

        LoginResp response = await world.LoginAsync(session, "ada", "secret-value", 4);

        Assert.That(response.Code, Is.EqualTo(0));
        AssertSameSnapshot(saved, response.Data);
        Assert.That(world.ScalarLong("SELECT gold FROM player WHERE id = $id;", playerId), Is.EqualTo(90));
        Assert.That(world.ScalarLong("SELECT guide_step FROM player WHERE id = $id;", playerId), Is.EqualTo(9));
        Assert.That(world.ScalarString("SELECT nickname FROM player WHERE id = $id;", playerId), Is.EqualTo("Ann"));
    }

    /// <summary>缓存中的昵称优先于库里的旧值，登录不把缓存写回默认昵称。</summary>
    [Test]
    public async Task CachedNickname_IsKeptAndDatabaseStaysUnchanged()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(53);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        long playerId = world.PlayerId("ada");
        await world.Queue.EnqueueAsync(playerId, async token =>
        {
            await world.Cache.GetAsync(playerId, token);
            world.Cache.ReplaceNickname(playerId, "CachedNick");
            return;
        }, CancellationToken.None);

        LoginResp response = await world.LoginAsync(session, "ada", "secret-value", 5);

        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(response.Data.Info.Nickname, Is.EqualTo("CachedNick"));
        Assert.That(response.Data.Info.Gold, Is.EqualTo(200));
        Assert.That(world.ScalarString("SELECT nickname FROM player WHERE id = $id;", playerId), Is.EqualTo("Ann"));
    }

    /// <summary>B 顶掉 A 时，Kick 字节写下之前 A 的令牌已经是 204，之后 B 仍是 current。</summary>
    [Test]
    public async Task SecondLogin_RevokesPreviousTokenBeforeKickBytes()
    {
        var gate = new KickGate();
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession older = await world.AddSessionAsync(54, gate);
        await using LiveSession newer = await world.AddSessionAsync(55);
        await world.RegisterAsync(older, "ada", "secret-value", "Ann", 1);
        LoginResp first = await world.LoginAsync(older, "ada", "secret-value", 2);
        long playerId = first.Data.Info.PlayerId;

        Task second = world.DispatchLoginAsync(newer, "ada", "secret-value", 3);
        await gate.KickEntered.WaitAsync(TimeSpan.FromSeconds(8));
        TokenValidation revoked = await world.Tokens.ValidateAsync(first.Token, NowMs, CancellationToken.None);
        Assert.That(revoked.Code, Is.EqualTo(204));
        Assert.That(revoked.PlayerId, Is.EqualTo(0));
        Assert.That(CountKick(DecodeAll(gate.Snapshot())), Is.EqualTo(0));
        Assert.That(world.Sessions.IsCurrent(playerId, newer.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(playerId, older.Session.SessionId), Is.False);

        gate.Release();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        LoginResp replacement = world.ReadLogin(newer, 3);
        Assert.That(replacement.Code, Is.EqualTo(0));
        TokenValidation current = await world.Tokens.ValidateAsync(replacement.Token, NowMs, CancellationToken.None);
        Assert.That(current.Code, Is.EqualTo(0));
        Assert.That(current.PlayerId, Is.EqualTo(playerId));
        Assert.That(CountKick(DecodeAll(gate.Snapshot())), Is.EqualTo(1));
        await older.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(world.Sessions.IsCurrent(playerId, newer.Session.SessionId), Is.True);
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
        Assert.That(string.Join("\n", world.Logs).Contains(first.Token), Is.False);
        Assert.That(string.Join("\n", world.Logs).Contains(replacement.Token), Is.False);
    }

    /// <summary>同一账号并发登录结束后，只有一个 current，也只有它的令牌有效。</summary>
    [Test]
    public async Task ConcurrentLogin_KeepsOnlyTheLastToken()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession first = await world.AddSessionAsync(56);
        await using LiveSession second = await world.AddSessionAsync(57);
        await world.RegisterAsync(first, "ada", "secret-value", "Ann", 1);
        long playerId = world.PlayerId("ada");

        Task left = world.DispatchLoginAsync(first, "ada", "secret-value", 4);
        Task right = world.DispatchLoginAsync(second, "ada", "secret-value", 5);
        await Task.WhenAll(left, right);

        LoginResp leftResponse = world.ReadLogin(first, 4);
        LoginResp rightResponse = world.ReadLogin(second, 5);
        Assert.That(leftResponse.Code, Is.EqualTo(0));
        Assert.That(rightResponse.Code, Is.EqualTo(0));
        bool leftCurrent = world.Sessions.IsCurrent(playerId, first.Session.SessionId);
        bool rightCurrent = world.Sessions.IsCurrent(playerId, second.Session.SessionId);
        Assert.That(Convert.ToInt32(leftCurrent) + Convert.ToInt32(rightCurrent), Is.EqualTo(1));
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(1));
        string currentToken = leftCurrent ? leftResponse.Token : rightResponse.Token;
        string previousToken = leftCurrent ? rightResponse.Token : leftResponse.Token;
        TokenValidation current = await world.Tokens.ValidateAsync(currentToken, NowMs, CancellationToken.None);
        TokenValidation previous = await world.Tokens.ValidateAsync(previousToken, NowMs, CancellationToken.None);
        Assert.That(current.Code, Is.EqualTo(0));
        Assert.That(current.PlayerId, Is.EqualTo(playerId));
        Assert.That(previous.Code, Is.EqualTo(204));
        Assert.That(previous.PlayerId, Is.EqualTo(0));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
    }

    /// <summary>上下文里伪造的玩家编号不会变成注册表身份。</summary>
    [Test]
    public async Task ForgedContextPlayer_DoesNotBecomeIdentity()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(58);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        long playerId = world.PlayerId("ada");
        ushort msgId = MsgRegistry.GetId(typeof(LoginReq));
        var forged = new RequestContext(session.Session.SessionId, 999, msgId, 6);
        await world.Router.DispatchAsync(forged, new Packet(msgId, 6, world.LoginRequest("ada", "secret-value").ToByteArray()), CancellationToken.None);

        LoginResp response = world.ReadLogin(session, 6);
        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(response.Data.Info.PlayerId, Is.EqualTo(playerId));
        Assert.That(world.Sessions.TryGetPlayerId(session.Session.SessionId, out long mapped), Is.True);
        Assert.That(mapped, Is.EqualTo(playerId));
        Assert.That(world.Sessions.IsCurrent(999, session.Session.SessionId), Is.False);
    }

    /// <summary>已开始关闭的会话和不存在的会话都拒绝，不回成功，也不写令牌。</summary>
    [Test]
    public async Task ClosedOrMissingSession_IsRejectedWithoutSuccess()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession closed = await world.AddSessionAsync(59);
        await world.RegisterAsync(closed, "ada", "secret-value", "Ann", 1);
        await closed.Session.CloseAsync("测试关闭", CancellationToken.None);
        Assert.That(closed.Session.IsCloseRequested, Is.True);

        InvalidOperationException closedError = Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.DispatchLoginAsync(closed, "ada", "secret-value", 2));
        Assert.That(closedError.Message, Does.Contain("已开始关闭"));
        Assert.That(closedError.Message.Contains("secret-value"), Is.False);

        ushort msgId = MsgRegistry.GetId(typeof(LoginReq));
        InvalidOperationException missingError = Assert.ThrowsAsync<InvalidOperationException>(() => world.Router.DispatchAsync(
            new RequestContext(590, 0, msgId, 3),
            new Packet(msgId, 3, world.LoginRequest("ada", "secret-value").ToByteArray()),
            CancellationToken.None));
        Assert.That(missingError.Message, Does.Contain("不存在"));
        Assert.That(missingError.Message.Contains("secret-value"), Is.False);
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(0));
    }

    /// <summary>令牌插入失败时不回成功，事务回滚后没有令牌行。</summary>
    [Test]
    public async Task TokenInsertFailure_DoesNotSendSuccess()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(60);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        world.Execute("""
            CREATE TRIGGER abort_token BEFORE INSERT ON auth_token
            BEGIN
              SELECT RAISE(ABORT, 'token-insert-aborted');
            END;
            """);
        int framesBefore = world.Frames(session).Count;

        SqliteException error = Assert.ThrowsAsync<SqliteException>(() => world.DispatchLoginAsync(session, "ada", "secret-value", 4));

        Assert.That(error.Message, Does.Contain("token-insert-aborted"));
        Assert.That(error.Message.Contains("secret-value"), Is.False);
        Assert.That(world.Frames(session).Count, Is.EqualTo(framesBefore));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        Assert.That(world.ScalarLong("SELECT COUNT(*) FROM auth_token;", 0), Is.EqualTo(0));
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(0));
    }

    /// <summary>分发前已取消，以及认证还排在数据库队列里时取消，都不回成功。</summary>
    [Test]
    public async Task Cancel_DoesNotSendSuccessOrToken()
    {
        await using var world = await LoginWorld.OpenAsync();
        await using LiveSession early = await world.AddSessionAsync(61);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        OperationCanceledException earlyError = Assert.CatchAsync<OperationCanceledException>(() =>
            world.DispatchLoginAsync(early, "ada", "secret-value", 1, ct: canceled.Token));
        Assert.That(earlyError.Message.Contains("secret-value"), Is.False);
        Assert.That(world.Frames(early), Is.Empty);

        await using LiveSession queued = await world.AddSessionAsync(62);
        await world.RegisterAsync(queued, "ada", "secret-value", "Ann", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> hold = world.Store.ExecuteAsync(_ =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("数据库占用没有被放开");
            return 0;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var later = new CancellationTokenSource();
        Task pending = world.DispatchLoginAsync(queued, "ada", "secret-value", 2, ct: later.Token);
        try
        {
            later.Cancel();
            release.TrySetResult();
            OperationCanceledException queuedError = Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            Assert.That(queuedError, Is.InstanceOf<OperationCanceledException>());
            Assert.That(world.Frames(queued).Count, Is.EqualTo(1));
            Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        }
        finally
        {
            release.TrySetResult();
            await hold;
        }
    }

    /// <summary>当前时间加期限溢出时失败，不写入令牌，也不回成功。</summary>
    [Test]
    public async Task ExpiryOverflow_FailsWithoutToken()
    {
        await using var world = await LoginWorld.OpenAsync(nowMs: long.MaxValue - 10);
        await using LiveSession session = await world.AddSessionAsync(63);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        int framesBefore = world.Frames(session).Count;

        InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.DispatchLoginAsync(session, "ada", "secret-value", 4));

        Assert.That(error.Message, Does.Contain("溢出"));
        Assert.That(error.Message.Contains("secret-value"), Is.False);
        Assert.That(world.Frames(session).Count, Is.EqualTo(framesBefore));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
    }

    /// <summary>零期限和不足 1 毫秒的期限在配置时失败。</summary>
    [Test]
    public void InvalidLifetime_IsRejectedByOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoginSessionOptions(1, 100, 100, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoginSessionOptions(1, 100, 100, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoginSessionOptions(1, 100, 100, TimeSpan.FromTicks(1)));
    }

    /// <summary>自定义期限写入到期时间。完整构造下注册成功仍不绑定、不签发。</summary>
    [Test]
    public async Task CustomLifetimeAndRegister_DoNotAuthorizeEarly()
    {
        await using var world = await LoginWorld.OpenAsync(lifetime: TimeSpan.FromHours(2));
        await using LiveSession session = await world.AddSessionAsync(64);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        Assert.That(world.Sessions.TryGetPlayerId(session.Session.SessionId, out _), Is.False);

        int logoutCalls = 0;
        world.Router.Register<LogoutReq>(MsgRegistry.GetId(typeof(LogoutReq)), (context, request, token) =>
        {
            logoutCalls++;
            return Task.CompletedTask;
        });
        await world.DispatchAsync(session, new LogoutReq(), 2);
        Assert.That(logoutCalls, Is.EqualTo(0));
        Assert.That(string.Join("\n", world.Logs), Does.Contain("code=102"));

        LoginResp response = await world.LoginAsync(session, "ada", "secret-value", 3);
        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(world.TokenExpiry(response.Data.Info.PlayerId), Is.EqualTo(NowMs + (long)TimeSpan.FromHours(2).TotalMilliseconds));
        Assert.That(string.Join("\n", world.Logs).Contains(response.Token), Is.False);
    }

    /// <summary>真实环回上，后登录的连接顶掉先登录的连接。</summary>
    [Test]
    public async Task Loopback_SecondLoginKicksFirst()
    {
        var connections = new ConcurrentDictionary<long, ClientSession>();
        var logs = new List<string>();
        using var dir = new IsolatedDirectory();
        using var store = new SqliteStore();
        store.Initialize(Path.Combine(dir.FullPath, "game.db"));
        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(8), logs.Add);
        var accounts = new AccountRepository(store, new PasswordHasher());
        var cache = new PlayerCache(new PlayerStore(store));
        var tokens = new TokenService(store);
        var options = new LoginSessionOptions(1, 100, 100, utcNowMs: () => NowMs);
        var router = new MessageRouter(
            (context, packet, token) =>
            {
                if (!connections.TryGetValue(context.SessionId, out ClientSession? target))
                    throw new InvalidOperationException("会话不存在或已开始关闭");
                return target.SendAsync(packet, token);
            },
            (context, reason, token) => Task.CompletedTask,
            new FixedClock(),
            logs.Add);
        var handler = new AccountHandler(
            accounts,
            queue,
            cache,
            tokens,
            registry,
            sessionId => connections.TryGetValue(sessionId, out ClientSession? found) ? found : null,
            options,
            (context, packet, token) =>
            {
                if (!connections.TryGetValue(context.SessionId, out ClientSession? target))
                    throw new InvalidOperationException("会话不存在或已开始关闭");
                return target.SendAsync(packet, token);
            });
        handler.RegisterRoutes(router);
        await using var gate = new ServerGate(async (session, packet, token) =>
        {
            connections[session.SessionId] = session;
            RequestContext context = registry.CreateRequestContext(session, packet.MsgId, packet.Seq);
            await router.DispatchAsync(context, packet, token);
        });

        await using OwnedClient clientA = await OwnedClient.ConnectAsync(gate.Port);
        await using OwnedClient clientB = await OwnedClient.ConnectAsync(gate.Port);
        await clientA.Stream.WriteAsync(Frame(new RegisterReq
        {
            Username = "ada",
            Password = "secret-value",
            Nickname = "Ann"
        }, 1));
        Packet registered = await ReadFrameAsync(clientA.Stream);
        Assert.That(registered.MsgId, Is.EqualTo((ushort)1004));
        Assert.That(registered.Seq, Is.EqualTo(1u));
        Assert.That(RegisterResp.Parser.ParseFrom(registered.Body).Code, Is.EqualTo(0));

        await clientA.Stream.WriteAsync(Frame(LoginBody("ada", "secret-value"), 2));
        Packet first = await ReadFrameAsync(clientA.Stream);
        LoginResp firstLogin = LoginResp.Parser.ParseFrom(first.Body);
        Assert.That(first.MsgId, Is.EqualTo((ushort)1006));
        Assert.That(first.Seq, Is.EqualTo(2u));
        Assert.That(firstLogin.Code, Is.EqualTo(0));
        Assert.That(firstLogin.ServerTime, Is.EqualTo(NowMs));

        await clientB.Stream.WriteAsync(Frame(LoginBody("ada", "secret-value"), 3));
        Packet kick = await ReadFrameAsync(clientA.Stream);
        Assert.That(kick.MsgId, Is.EqualTo((ushort)1010));
        Assert.That(kick.Seq, Is.EqualTo(0u));
        Assert.That(KickNtf.Parser.ParseFrom(kick.Body).Reason, Is.EqualTo(KickReason.KickElsewhere));
        Packet second = await ReadFrameAsync(clientB.Stream);
        LoginResp secondLogin = LoginResp.Parser.ParseFrom(second.Body);
        Assert.That(second.MsgId, Is.EqualTo((ushort)1006));
        Assert.That(second.Seq, Is.EqualTo(3u));
        Assert.That(secondLogin.Code, Is.EqualTo(0));
        Assert.That(secondLogin.Data.Info.PlayerId, Is.EqualTo(firstLogin.Data.Info.PlayerId));
        TokenValidation oldToken = await tokens.ValidateAsync(firstLogin.Token, NowMs, CancellationToken.None);
        TokenValidation newToken = await tokens.ValidateAsync(secondLogin.Token, NowMs, CancellationToken.None);
        Assert.That(oldToken.Code, Is.EqualTo(204));
        Assert.That(newToken.Code, Is.EqualTo(0));
        Assert.That(newToken.PlayerId, Is.EqualTo(firstLogin.Data.Info.PlayerId));
        ClientSession? newer = connections.Values.First(item => registry.IsCurrent(firstLogin.Data.Info.PlayerId, item.SessionId));
        Assert.That(registry.IsCurrent(firstLogin.Data.Info.PlayerId, newer.SessionId), Is.True);
        Assert.That(string.Join("\n", logs).Contains(firstLogin.Token), Is.False);
        Assert.That(string.Join("\n", logs).Contains(secondLogin.Token), Is.False);
        await AssertPeerEndedAsync(clientA.Stream);
    }

    /// <summary>组装一份与正式列一致、并带集合字段的快照。</summary>
    /// <param name="playerId">玩家主键。</param>
    /// <param name="nickname">已经入库的昵称。</param>
    static PlayerFullData SavedData(long playerId, string nickname)
    {
        var data = new PlayerFullData
        {
            Info = new PlayerInfo
            {
                PlayerId = playerId,
                Nickname = nickname,
                Level = 4,
                Exp = 15,
                Gold = 90,
                Gem = 8,
                Ticket = 2,
                Shard = 6,
                BagSlots = 70,
                GuideStep = 9
            },
            OrderProgress = new OrderProgress { DayIndex = 4, DoneCount = 1, NextRefreshTime = 88 },
            Home = new HomeLayout { Version = 5 }
        };
        data.Bag.Add(new ItemStack { ItemId = 100, Count = 3 });
        data.Plots.Add(new PlotData { PlotId = 2, CropId = 9, PlantTime = 10 });
        return data;
    }

    /// <summary>核对响应快照保留保存时的基础列和集合。</summary>
    /// <param name="saved">写入数据库的那一份。</param>
    /// <param name="actual">登录响应里的快照。</param>
    static void AssertSameSnapshot(PlayerFullData saved, PlayerFullData actual)
    {
        Assert.That(actual, Is.Not.Null);
        AssertInfo(actual, saved.Info.PlayerId, saved.Info.Nickname, saved.Info.Level, saved.Info.Exp, saved.Info.Gold, saved.Info.Gem, saved.Info.Ticket, saved.Info.Shard, saved.Info.BagSlots, saved.Info.GuideStep);
        Assert.That(actual.Bag.Count, Is.EqualTo(1));
        Assert.That(actual.Bag[0].ItemId, Is.EqualTo(100));
        Assert.That(actual.Bag[0].Count, Is.EqualTo(3));
        Assert.That(actual.Plots.Count, Is.EqualTo(1));
        Assert.That(actual.Plots[0].PlotId, Is.EqualTo(2));
        Assert.That(actual.OrderProgress.DayIndex, Is.EqualTo(4));
        Assert.That(actual.Home.Version, Is.EqualTo(5));
    }

    /// <summary>核对玩家基础列。</summary>
    static void AssertInfo(PlayerFullData data, long playerId, string nickname, int level, int exp, long gold, long gem, long ticket, long shard, int bagSlots, int guideStep)
    {
        Assert.That(data, Is.Not.Null);
        Assert.That(data.Info, Is.Not.Null);
        if (playerId != 0)
            Assert.That(data.Info.PlayerId, Is.EqualTo(playerId));
        else
            Assert.That(data.Info.PlayerId, Is.GreaterThan(0));
        Assert.That(data.Info.Nickname, Is.EqualTo(nickname));
        Assert.That(data.Info.Level, Is.EqualTo(level));
        Assert.That(data.Info.Exp, Is.EqualTo(exp));
        Assert.That(data.Info.Gold, Is.EqualTo(gold));
        Assert.That(data.Info.Gem, Is.EqualTo(gem));
        Assert.That(data.Info.Ticket, Is.EqualTo(ticket));
        Assert.That(data.Info.Shard, Is.EqualTo(shard));
        Assert.That(data.Info.BagSlots, Is.EqualTo(bagSlots));
        Assert.That(data.Info.GuideStep, Is.EqualTo(guideStep));
    }

    /// <summary>核对失败登录没有令牌和快照。</summary>
    /// <param name="response">解析出的登录响应。</param>
    /// <param name="seq">期待的序号。</param>
    /// <param name="code">期待的业务码。</param>
    static void AssertRejected(LoginResp response, uint seq, int code)
    {
        Assert.That(response.Code, Is.EqualTo(code));
        Assert.That(response.Token.Length, Is.EqualTo(0));
        Assert.That(response.Data, Is.Null);
        Assert.That(response.ServerTime, Is.EqualTo(0));
        Assert.That(seq, Is.GreaterThan(0));
    }

    /// <summary>统计踢线帧。</summary>
    /// <param name="frames">一条连接上的帧。</param>
    static int CountKick(IReadOnlyList<Packet> frames)
    {
        int count = 0;
        ushort kickId = MsgRegistry.GetId(typeof(KickNtf));
        foreach (Packet frame in frames)
        {
            if (frame.MsgId == kickId)
                count++;
        }

        return count;
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
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(read, Is.GreaterThan(0));
            offset += read;
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

    /// <summary>把正式消息编成一帧。</summary>
    /// <param name="message">请求正文。</param>
    /// <param name="seq">帧序号。</param>
    static byte[] Frame(IMessage message, uint seq)
    {
        return FrameCodec.Encode(MsgRegistry.GetId(message.GetType()), seq, message.ToByteArray());
    }

    /// <summary>组装一条版本匹配的登录请求。</summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码原文。测试不把它写进断言消息以外的输出。</param>
    static LoginReq LoginBody(string username, string password)
    {
        return new LoginReq
        {
            Username = username,
            Password = password,
            AppVersion = "0.1.0",
            ResVersion = 100,
            ProtoVersion = 1,
            ConfigVersion = 100,
            DeviceId = "device-1"
        };
    }

    /// <summary>登录测试用的临时服务端。发送走真实会话。</summary>
    sealed class LoginWorld : IAsyncDisposable
    {
        readonly IsolatedDirectory _dir;
        readonly ConcurrentDictionary<long, ClientSession> _connections;
        readonly List<LiveSession> _lives = new();

        LoginWorld(
            IsolatedDirectory dir,
            SqliteStore store,
            PlayerQueue queue,
            PlayerCache cache,
            TokenService tokens,
            SessionRegistry sessions,
            MessageRouter router,
            ConcurrentDictionary<long, ClientSession> connections,
            List<string> logs,
            long nowMs)
        {
            _dir = dir;
            _connections = connections;
            Store = store;
            Queue = queue;
            Cache = cache;
            Tokens = tokens;
            Sessions = sessions;
            Router = router;
            Logs = logs;
            Now = nowMs;
        }

        /// <summary>本用例的数据库。</summary>
        public SqliteStore Store { get; }

        /// <summary>与注册表共享的玩家队列。</summary>
        public PlayerQueue Queue { get; }

        /// <summary>登录读取的玩家缓存。</summary>
        public PlayerCache Cache { get; }

        /// <summary>令牌服务。</summary>
        public TokenService Tokens { get; }

        /// <summary>会话注册表。</summary>
        public SessionRegistry Sessions { get; }

        /// <summary>已注册注册和登录的路由。</summary>
        public MessageRouter Router { get; }

        /// <summary>注入的当前时间。</summary>
        public long Now { get; }

        /// <summary>注册表和路由写下的诊断。与构造时传入的列表是同一个实例。</summary>
        public List<string> Logs { get; }

        /// <summary>创建隔离库、共享队列和正式处理器。</summary>
        /// <param name="minimumApp">最低包体版本。空表示不检查。</param>
        /// <param name="lifetime">令牌期限。为空时用 24 小时。</param>
        /// <param name="nowMs">注入的 UTC 毫秒。</param>
        public static Task<LoginWorld> OpenAsync(string? minimumApp = "0.1.0", TimeSpan? lifetime = null, long nowMs = NowMs)
        {
            var dir = new IsolatedDirectory();
            var store = new SqliteStore();
            store.Initialize(Path.Combine(dir.FullPath, "game.db"));
            var queue = new PlayerQueue();
            var logs = new List<string>();
            var sessions = new SessionRegistry(queue, TimeSpan.FromSeconds(8), logs.Add);
            var connections = new ConcurrentDictionary<long, ClientSession>();
            return Task.FromResult(Create(dir, store, queue, sessions, connections, logs, minimumApp, lifetime, nowMs));
        }

        /// <summary>组装已经准备好的对象。</summary>
        static LoginWorld Create(
            IsolatedDirectory dir,
            SqliteStore store,
            PlayerQueue queue,
            SessionRegistry sessions,
            ConcurrentDictionary<long, ClientSession> connections,
            List<string> logs,
            string? minimumApp,
            TimeSpan? lifetime,
            long nowMs)
        {
            var cache = new PlayerCache(new PlayerStore(store));
            var tokens = new TokenService(store);
            var options = new LoginSessionOptions(1, 100, 100, lifetime, minimumApp, () => nowMs);
            Func<RequestContext, Packet, CancellationToken, Task> send = (context, packet, token) =>
            {
                if (!connections.TryGetValue(context.SessionId, out ClientSession? target))
                    throw new InvalidOperationException("会话不存在或已开始关闭");
                return target.SendAsync(packet, token);
            };
            var router = new MessageRouter(send, (context, reason, token) => Task.CompletedTask, new FixedClock(), logs.Add);
            var handler = new AccountHandler(
                new AccountRepository(store, new PasswordHasher()),
                queue,
                cache,
                tokens,
                sessions,
                sessionId => connections.TryGetValue(sessionId, out ClientSession? found) ? found : null,
                options,
                send);
            handler.RegisterRoutes(router);
            return new LoginWorld(dir, store, queue, cache, tokens, sessions, router, connections, logs, nowMs);
        }

        /// <summary>启动一条受控会话并放进解析表。</summary>
        /// <param name="sessionId">服务端会话编号。</param>
        /// <param name="transport">写出端。为空时用立即完成的记录传输。</param>
        public Task<LiveSession> AddSessionAsync(long sessionId)
        {
            var transport = new RecordingTransport();
            return AddSessionAsync(sessionId, transport, transport.Snapshot);
        }

        /// <summary>启动一条使用踢线闸门的会话。闸门只挡住 KickNtf。</summary>
        /// <param name="sessionId">服务端会话编号。</param>
        /// <param name="gate">第二次及以后的踢线写出在放行前不记录字节。</param>
        public Task<LiveSession> AddSessionAsync(long sessionId, KickGate gate)
        {
            return AddSessionAsync(sessionId, gate, gate.Snapshot);
        }

        /// <summary>启动会话并放进与登录解析函数相同的表。</summary>
        /// <param name="sessionId">服务端会话编号。</param>
        /// <param name="transport">会话写出端。</param>
        /// <param name="snapshot">读取目前已经记下的字节。</param>
        async Task<LiveSession> AddSessionAsync(long sessionId, IFrameTransport transport, Func<byte[]> snapshot)
        {
            LiveSession live = await LiveSession.StartAsync(sessionId, transport, snapshot);
            _connections[sessionId] = live.Session;
            _lives.Add(live);
            return live;
        }

        /// <summary>经路由注册。成功后会话仍然没有玩家。</summary>
        public async Task RegisterAsync(LiveSession session, string username, string password, string nickname, uint seq)
        {
            await DispatchAsync(session, new RegisterReq
            {
                Username = username,
                Password = password,
                Nickname = nickname,
                ProtoVersion = 1,
                DeviceId = "device-1"
            }, seq);
            Packet frame = Frames(session).Last();
            Assert.That(frame.MsgId, Is.EqualTo((ushort)1004));
            Assert.That(frame.Seq, Is.EqualTo(seq));
            Assert.That(RegisterResp.Parser.ParseFrom(frame.Body).Code, Is.EqualTo(0));
            Assert.That(Sessions.TryGetPlayerId(session.Session.SessionId, out _), Is.False);
        }

        /// <summary>经路由登录并解析指定序号的响应。</summary>
        public async Task<LoginResp> LoginAsync(
            LiveSession session,
            string username,
            string password,
            uint seq,
            int protoVersion = 1,
            int resVersion = 100,
            int configVersion = 100,
            string appVersion = "0.1.0")
        {
            await DispatchLoginAsync(session, username, password, seq, protoVersion, resVersion, configVersion, appVersion);
            return ReadLogin(session, seq);
        }

        /// <summary>只分发登录，不立刻读取响应。</summary>
        public Task DispatchLoginAsync(
            LiveSession session,
            string username,
            string password,
            uint seq,
            int protoVersion = 1,
            int resVersion = 100,
            int configVersion = 100,
            string appVersion = "0.1.0",
            CancellationToken ct = default)
        {
            return DispatchAsync(session, LoginRequest(username, password, protoVersion, resVersion, configVersion, appVersion), seq, ct);
        }

        /// <summary>组装登录请求。</summary>
        public LoginReq LoginRequest(string username, string password, int protoVersion = 1, int resVersion = 100, int configVersion = 100, string appVersion = "0.1.0")
        {
            return new LoginReq
            {
                Username = username,
                Password = password,
                AppVersion = appVersion,
                ResVersion = resVersion,
                ProtoVersion = protoVersion,
                ConfigVersion = configVersion,
                DeviceId = "device-1"
            };
        }

        /// <summary>用注册表构造上下文后分发。玩家编号来自注册表。</summary>
        public Task DispatchAsync(LiveSession session, IMessage message, uint seq, CancellationToken ct = default)
        {
            ushort msgId = MsgRegistry.GetId(message.GetType());
            RequestContext context = Sessions.CreateRequestContext(session.Session, msgId, seq);
            return Router.DispatchAsync(context, new Packet(msgId, seq, message.ToByteArray()), ct);
        }

        /// <summary>读取这条会话上指定序号的登录响应，并核对消息编号。</summary>
        public LoginResp ReadLogin(LiveSession session, uint seq)
        {
            Packet frame = Frames(session).Single(item => item.MsgId == MsgRegistry.GetId(typeof(LoginResp)) && item.Seq == seq);
            Assert.That(frame.MsgId, Is.EqualTo((ushort)1006));
            return LoginResp.Parser.ParseFrom(frame.Body);
        }

        /// <summary>拆开这条会话已经写出的帧。</summary>
        public List<Packet> Frames(LiveSession session)
        {
            return DecodeAll(session.Snapshot());
        }

        /// <summary>按用户名取玩家编号。</summary>
        public long PlayerId(string username)
        {
            return ScalarLong("SELECT p.id FROM player p JOIN account a ON a.id = p.account_id WHERE a.username = $id;", 0, username);
        }

        /// <summary>未撤销的令牌行数。</summary>
        public long ActiveTokenCount()
        {
            return ScalarLong("SELECT COUNT(*) FROM auth_token WHERE revoked = 0;", 0);
        }

        /// <summary>该玩家当前未撤销令牌的到期毫秒。</summary>
        public long TokenExpiry(long playerId)
        {
            return ScalarLong("SELECT expires_at_ms FROM auth_token WHERE player_id = $id AND revoked = 0;", playerId);
        }

        /// <summary>读取一个整数。参数按调用方传入。</summary>
        public long ScalarLong(string sql, long id, string? username = null)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            if (username != null)
                command.Parameters.AddWithValue("$id", username);
            else if (sql.Contains("$id", StringComparison.Ordinal))
                command.Parameters.AddWithValue("$id", id);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>读取一个字符串。</summary>
        public string ScalarString(string sql, long id)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            return (string)command.ExecuteScalar()!;
        }

        /// <summary>在队列外执行一段管理语句。</summary>
        public void Execute(string sql)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>另开连接，避免和队列连接混用事务。</summary>
        SqliteConnection Open()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_dir.FullPath, "game.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        /// <summary>结束会话、注册表观察、队列和库，并删除临时目录。</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (LiveSession live in _lives)
                await live.DisposeAsync();
            try
            {
                await Sessions.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }

            Queue.Dispose();
            Store.Dispose();
            _dir.Dispose();
        }
    }

    /// <summary>第一条写出立即记录，第二条先报告进入再等待放行。</summary>
    sealed class KickGate : IFrameTransport
    {
        readonly List<byte> _bytes = new();
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _writes;

        /// <summary>第二次写出已经进入、但字节还没记下。</summary>
        public Task KickEntered => _entered.Task;

        /// <summary>放行第二次写出。</summary>
        public void Release() => _release.TrySetResult();

        /// <summary>复制已经记下的字节。</summary>
        public byte[] Snapshot()
        {
            lock (_bytes)
                return _bytes.ToArray();
        }

        /// <summary>第一次整段记下。第二次先发信号，放行后才记下。</summary>
        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (IsKick(buffer.Span) && Interlocked.Exchange(ref _writes, 1) == 0)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Append(buffer.Span);
            return buffer.Length;
        }

        /// <summary>整帧的消息编号是正式 KickNtf 时返回 true。</summary>
        /// <param name="frame">会话一次交出的字节。</param>
        static bool IsKick(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 6)
                return false;
            return BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) == MsgRegistry.GetId(typeof(KickNtf));
        }

        /// <summary>把一段复制进记录。</summary>
        void Append(ReadOnlySpan<byte> span)
        {
            lock (_bytes)
            {
                for (int i = 0; i < span.Length; i++)
                    _bytes.Add(span[i]);
            }
        }
    }

    /// <summary>把写出的字节按顺序留下。</summary>
    sealed class RecordingTransport : IFrameTransport
    {
        readonly List<byte> _bytes = new();

        /// <summary>复制目前的全部字节。</summary>
        public byte[] Snapshot()
        {
            lock (_bytes)
                return _bytes.ToArray();
        }

        /// <summary>整段收下并立刻完成。</summary>
        public ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
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

    /// <summary>一条已经跑起来的测试会话。</summary>
    sealed class LiveSession : IAsyncDisposable
    {
        readonly Task _run;
        readonly Func<byte[]> _snapshot;

        LiveSession(ClientSession session, GateStream stream, Task run, Func<byte[]> snapshot)
        {
            Session = session;
            Stream = stream;
            _run = run;
            _snapshot = snapshot;
        }

        /// <summary>被绑定的服务端会话。</summary>
        public ClientSession Session { get; }

        /// <summary>用来制造对端结束的读流。</summary>
        public GateStream Stream { get; }

        /// <summary>复制这条会话已经记下的写出字节。</summary>
        public byte[] Snapshot() => _snapshot();

        /// <summary>创建会话并等到收发循环已经开始。</summary>
        /// <param name="sessionId">服务端会话编号。</param>
        /// <param name="transport">写出端。</param>
        /// <param name="snapshot">读取写出端已经记下的字节。</param>
        public static async Task<LiveSession> StartAsync(long sessionId, IFrameTransport transport, Func<byte[]> snapshot)
        {
            var stream = new GateStream();
            ClientSession session = ClientSession.CreateForTransport(sessionId, stream, transport, 8, null);
            Task run = session.RunAsync(CancellationToken.None);
            await Task.Yield();
            return new LiveSession(session, stream, run, snapshot);
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

    /// <summary>限流窗口用的固定时钟。</summary>
    sealed class FixedClock : IRouterClock
    {
        public long UnixMilliseconds => NowMs;
    }

    /// <summary>只删除系统临时目录里本测试创建的子目录。</summary>
    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingAccountLoginTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        /// <summary>本用例的绝对目录。</summary>
        public string FullPath { get; }

        /// <summary>目录仍在指定根下时才递归删除。</summary>
        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingAccountLoginTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
