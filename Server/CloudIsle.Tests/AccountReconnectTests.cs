using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>经正式路由和真实 ClientSession 验证重连与显式登出。临时库与 PoC 账号库分开。</summary>
public sealed class AccountReconnectTests
{
    const long NowMs = 1_700_000_000_000L;
    static readonly long DayMs = (long)TimeSpan.FromHours(24).TotalMilliseconds;

    /// <summary>有效 Token 重连回 1008、同一玩家和完整快照，并且不换发 Token。</summary>
    [Test]
    public async Task ValidToken_RestoresSamePlayerWithoutIssuingToken()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(71);
        await using LiveSession again = await world.AddSessionAsync(72);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(current, "ada", "secret-value", 2);
        int tokensBefore = world.ActiveTokenCount();

        ReconnectResp response = await world.ReconnectAsync(again, login.Token, 3);

        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(response.ServerTime, Is.EqualTo(NowMs));
        AssertSame(login.Data, response.Data);
        Assert.That(response.Data.Info.PlayerId, Is.EqualTo(login.Data.Info.PlayerId));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(tokensBefore));
        TokenValidation still = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(still.Code, Is.EqualTo(0));
        Assert.That(still.PlayerId, Is.EqualTo(login.Data.Info.PlayerId));
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, again.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, current.Session.SessionId), Is.False);
        Assert.That(string.Join("\n", world.Logs).Contains(login.Token), Is.False);
    }

    /// <summary>新的缓存和服务从已保存的库恢复完整快照，不读旧缓存里的默认值。</summary>
    [Test]
    public async Task FreshCache_RestoresSavedSnapshot()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(73);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(current, "ada", "secret-value", 2);
        long playerId = login.Data.Info.PlayerId;
        PlayerFullData saved = SavedData(playerId, "Ann");
        await new PlayerStore(world.Store).SaveAsync(new PlayerState(playerId, saved, 4, 4), CancellationToken.None);
        world.ReplaceWithFreshCache();
        await using LiveSession again = await world.AddSessionAsync(74);

        ReconnectResp response = await world.ReconnectAsync(again, login.Token, 5);

        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(response.ServerTime, Is.EqualTo(NowMs));
        AssertSameSnapshot(saved, response.Data);
        Assert.That(world.Sessions.IsCurrent(playerId, again.Session.SessionId), Is.True);
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
        Assert.That(string.Join("\n", world.Logs).Contains(login.Token), Is.False);
    }

    /// <summary>同进程自然断线后复用权威缓存，不把昵称重写成库里的旧值。</summary>
    [Test]
    public async Task CachedNickname_SurvivesNaturalDisconnect()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(75);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(current, "ada", "secret-value", 2);
        long playerId = login.Data.Info.PlayerId;
        await world.MutateNicknameAsync(playerId, "CachedNick");
        await current.Session.CloseAsync("自然断线", CancellationToken.None);

        TokenValidation still = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(still.Code, Is.EqualTo(0));
        await using LiveSession again = await world.AddSessionAsync(76);
        ReconnectResp response = await world.ReconnectAsync(again, login.Token, 3);

        Assert.That(response.Code, Is.EqualTo(0));
        Assert.That(response.Data.Info.Nickname, Is.EqualTo("CachedNick"));
        Assert.That(response.Data.Info.Gold, Is.EqualTo(200));
        Assert.That(world.ScalarString("SELECT nickname FROM player WHERE id = $id;", playerId), Is.EqualTo("Ann"));
        Assert.That(world.Sessions.IsCurrent(playerId, again.Session.SessionId), Is.True);
        Assert.That(string.Join("\n", world.Logs).Contains(login.Token), Is.False);
    }

    /// <summary>未知、已经到期和已撤销的 Token 都回 204，不带快照，也不改原绑定。</summary>
    [Test]
    public async Task UnknownExpiredOrRevoked_Return204WithoutSnapshot()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(77);
        await using LiveSession unknown = await world.AddSessionAsync(78);
        await using LiveSession expired = await world.AddSessionAsync(79);
        await using LiveSession revoked = await world.AddSessionAsync(80);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(current, "ada", "secret-value", 2);
        long playerId = login.Data.Info.PlayerId;

        ReconnectResp unknownResponse = await world.ReconnectAsync(unknown, new string('a', 64), 3);
        AssertRejected(unknownResponse, 204);
        Assert.That(world.Sessions.IsCurrent(playerId, current.Session.SessionId), Is.True);

        world.Clock.Now = NowMs + DayMs;
        ReconnectResp expiredResponse = await world.ReconnectAsync(expired, login.Token, 4);
        AssertRejected(expiredResponse, 204);
        Assert.That(world.Sessions.IsCurrent(playerId, current.Session.SessionId), Is.True);
        world.Clock.Now = NowMs;

        await world.Tokens.RevokePlayerAsync(playerId, CancellationToken.None);
        ReconnectResp revokedResponse = await world.ReconnectAsync(revoked, login.Token, 5);
        AssertRejected(revokedResponse, 204);
        Assert.That(world.Sessions.IsCurrent(playerId, current.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(playerId, revoked.Session.SessionId), Is.False);
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(0));
        Assert.That(string.Join("\n", world.Logs).Contains(login.Token), Is.False);
    }

    /// <summary>协议不一致回 106，不撤销 Token，也不改当前连接。</summary>
    [Test]
    public async Task ProtoMismatch_Returns106WithoutChangingBinding()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(81);
        await using LiveSession other = await world.AddSessionAsync(82);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(current, "ada", "secret-value", 2);
        int frames = world.Frames(current).Count;

        ReconnectResp rejected = await world.ReconnectAsync(other, login.Token, 3, protoVersion: 2);

        AssertRejected(rejected, 106);
        TokenValidation still = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(still.Code, Is.EqualTo(0));
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, current.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, other.Session.SessionId), Is.False);
        Assert.That(world.Frames(current).Count, Is.EqualTo(frames));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
    }

    /// <summary>首次校验早于 B 登录，队列内校验晚于 B。旧重连不能抢回。</summary>
    [Test]
    public async Task StalePreview_DoesNotReplaceNewerLogin()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession first = await world.AddSessionAsync(83);
        await using LiveSession newer = await world.AddSessionAsync(84);
        await using LiveSession stale = await world.AddSessionAsync(85);
        await world.RegisterAsync(first, "ada", "secret-value", "Ann", 1);
        LoginResp oldLogin = await world.LoginAsync(first, "ada", "secret-value", 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Handler.BeforeReconnectEnqueueAsync = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };
        Task pending = world.DispatchReconnectAsync(stale, oldLogin.Token, 4);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        LoginResp fresh = await world.LoginAsync(newer, "ada", "secret-value", 5);
        release.TrySetResult();
        await pending;
        ReconnectResp rejected = world.ReadReconnect(stale, 4);

        AssertRejected(rejected, 204);
        Assert.That(world.Sessions.IsCurrent(oldLogin.Data.Info.PlayerId, newer.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(oldLogin.Data.Info.PlayerId, stale.Session.SessionId), Is.False);
        TokenValidation newerToken = await world.Tokens.ValidateAsync(fresh.Token, NowMs, CancellationToken.None);
        Assert.That(newerToken.Code, Is.EqualTo(0));
        TokenValidation oldToken = await world.Tokens.ValidateAsync(oldLogin.Token, NowMs, CancellationToken.None);
        Assert.That(oldToken.Code, Is.EqualTo(204));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
        Assert.That(string.Join("\n", world.Logs).Contains(fresh.Token), Is.False);
        Assert.That(string.Join("\n", world.Logs).Contains(oldLogin.Token), Is.False);
    }

    /// <summary>排进玩家队列之前令牌到期时，队列内的第二次校验失败。</summary>
    [Test]
    public async Task ExpiresWhileQueued_Returns204()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession current = await world.AddSessionAsync(86);
        await using LiveSession again = await world.AddSessionAsync(87);
        await world.RegisterAsync(current, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(current, "ada", "secret-value", 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Handler.BeforeReconnectEnqueueAsync = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };
        Task pending = world.DispatchReconnectAsync(again, login.Token, 3);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        world.Clock.Now = NowMs + DayMs;
        release.TrySetResult();
        await pending;

        ReconnectResp rejected = world.ReadReconnect(again, 3);
        AssertRejected(rejected, 204);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, current.Session.SessionId), Is.True);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, again.Session.SessionId), Is.False);
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
    }

    /// <summary>显式登出落库、撤销 Token、解绑并关闭，且没有响应帧。</summary>
    [Test]
    public async Task Logout_SavesRevokesAndClosesWithoutResponse()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(88);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(session, "ada", "secret-value", 2);
        long playerId = login.Data.Info.PlayerId;
        await world.MutateNicknameAsync(playerId, "Bye");
        int frames = world.Frames(session).Count;

        await world.LogoutAsync(session, 3);

        Assert.That(world.Frames(session).Count, Is.EqualTo(frames));
        Assert.That(world.ScalarString("SELECT nickname FROM player WHERE id = $id;", playerId), Is.EqualTo("Bye"));
        TokenValidation revoked = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(revoked.Code, Is.EqualTo(204));
        Assert.That(world.Sessions.IsCurrent(playerId, session.Session.SessionId), Is.False);
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(session.Session.IsCloseRequested, Is.True);
        Assert.That(string.Join("\n", world.Logs).Contains(login.Token), Is.False);
    }

    /// <summary>旧连接的登出上下文产生于 B 替换之前，执行晚于 B，不能撤销 B。</summary>
    [Test]
    public async Task LateLogout_DoesNotAffectReplacement()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession first = await world.AddSessionAsync(89);
        await using LiveSession newer = await world.AddSessionAsync(90);
        await world.RegisterAsync(first, "ada", "secret-value", "Ann", 1);
        LoginResp oldLogin = await world.LoginAsync(first, "ada", "secret-value", 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Handler.BeforeLogoutEnqueueAsync = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };
        Task pending = world.DispatchLogoutAsync(first, 6);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        LoginResp fresh = await world.LoginAsync(newer, "ada", "secret-value", 3);
        long playerId = fresh.Data.Info.PlayerId;
        await world.MutateNicknameAsync(playerId, "Kept");
        int frames = world.Frames(newer).Count;
        release.TrySetResult();
        await pending;

        Assert.That(world.Frames(newer).Count, Is.EqualTo(frames));
        Assert.That(world.Sessions.IsCurrent(playerId, newer.Session.SessionId), Is.True);
        TokenValidation newerToken = await world.Tokens.ValidateAsync(fresh.Token, NowMs, CancellationToken.None);
        Assert.That(newerToken.Code, Is.EqualTo(0));
        Assert.That(newerToken.PlayerId, Is.EqualTo(playerId));
        DirtyCopy dirty = await world.CopyDirtyAsync(playerId);
        Assert.That(dirty.Dirty, Is.True);
        Assert.That(dirty.Nickname, Is.EqualTo("Kept"));
        Assert.That(string.Join("\n", world.Logs).Contains(fresh.Token), Is.False);
        Assert.That(string.Join("\n", world.Logs).Contains(oldLogin.Token), Is.False);
    }

    /// <summary>保存失败时保留 Dirty。撤销和关闭仍然执行，恢复后可以重试落库。</summary>
    [Test]
    public async Task FailedSave_KeepsDirtyAndStillRevokes()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(91);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(session, "ada", "secret-value", 2);
        long playerId = login.Data.Info.PlayerId;
        await world.MutateNicknameAsync(playerId, "Held");
        world.Saves.BeforePersistAsync = (_, _) => throw new InvalidOperationException("logout-save-aborted");
        int frames = world.Frames(session).Count;

        InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(() => world.LogoutAsync(session, 3));

        Assert.That(error.Message, Does.Contain("logout-save-aborted"));
        Assert.That(error.Message.Contains(login.Token), Is.False);
        Assert.That(world.Frames(session).Count, Is.EqualTo(frames));
        DirtyCopy dirty = await world.CopyDirtyAsync(playerId);
        Assert.That(dirty.Dirty, Is.True);
        Assert.That(dirty.Nickname, Is.EqualTo("Held"));
        Assert.That(world.Cache.IsCached(playerId), Is.True);
        Assert.That(world.ScalarString("SELECT nickname FROM player WHERE id = $id;", playerId), Is.EqualTo("Ann"));
        TokenValidation revoked = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(revoked.Code, Is.EqualTo(204));
        Assert.That(session.Session.IsCloseRequested, Is.True);
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(0));
        Assert.That(world.Saves.FailedSaveCount, Is.EqualTo(1));

        world.Saves.BeforePersistAsync = null;
        await world.Saves.FlushPlayerAsync(playerId, CancellationToken.None);
        Assert.That(world.ScalarString("SELECT nickname FROM player WHERE id = $id;", playerId), Is.EqualTo("Held"));
    }

    /// <summary>撤销失败必须传出。不能因为登出已经开始就把 Token 说成无效。</summary>
    [Test]
    public async Task RevokeFailure_PropagatesAndKeepsToken()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(92);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(session, "ada", "secret-value", 2);
        world.Execute("""
            CREATE TRIGGER abort_revoke BEFORE UPDATE ON auth_token
            BEGIN
              SELECT RAISE(ABORT, 'revoke-aborted');
            END;
            """);
        int frames = world.Frames(session).Count;

        SqliteException error = Assert.ThrowsAsync<SqliteException>(() => world.LogoutAsync(session, 3));

        Assert.That(error.Message, Does.Contain("revoke-aborted"));
        Assert.That(error.Message.Contains(login.Token), Is.False);
        Assert.That(world.Frames(session).Count, Is.EqualTo(frames));
        TokenValidation still = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(still.Code, Is.EqualTo(0));
        Assert.That(still.PlayerId, Is.EqualTo(login.Data.Info.PlayerId));
        Assert.That(world.ActiveTokenCount(), Is.EqualTo(1));
        Assert.That(session.Session.IsCloseRequested, Is.True);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, session.Session.SessionId), Is.False);
    }

    /// <summary>取消和令牌表不可读都不回 0 或 204。</summary>
    [Test]
    public async Task CancelOrDatabaseFailure_DoesNotSendSuccess()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession early = await world.AddSessionAsync(93);
        await world.RegisterAsync(early, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(early, "ada", "secret-value", 2);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        int frames = world.Frames(early).Count;
        OperationCanceledException earlyError = Assert.CatchAsync<OperationCanceledException>(() =>
            world.DispatchReconnectAsync(early, login.Token, 3, ct: canceled.Token));
        Assert.That(earlyError.Message.Contains(login.Token), Is.False);
        Assert.That(world.Frames(early).Count, Is.EqualTo(frames));

        await using LiveSession queued = await world.AddSessionAsync(94);
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
        using var queuedCancel = new CancellationTokenSource();
        Task pending = world.DispatchReconnectAsync(queued, login.Token, 4, ct: queuedCancel.Token);
        queuedCancel.Cancel();
        release.TrySetResult();
        OperationCanceledException queuedError = Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        await hold;
        Assert.That(queuedError.Message.Contains(login.Token), Is.False);
        Assert.That(world.Frames(queued), Is.Empty);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, early.Session.SessionId), Is.True);

        world.Execute("ALTER TABLE auth_token RENAME TO auth_token_hidden;");
        await using LiveSession broken = await world.AddSessionAsync(95);
        SqliteException database = Assert.ThrowsAsync<SqliteException>(() => world.DispatchReconnectAsync(broken, login.Token, 5));
        Assert.That(database.Message.Contains(login.Token), Is.False);
        Assert.That(world.Frames(broken), Is.Empty);
        Assert.That(world.Sessions.IsCurrent(login.Data.Info.PlayerId, early.Session.SessionId), Is.True);
    }

    /// <summary>登出已经进入保存后，请求取消仍撤销并关闭，且不回成功帧。</summary>
    [Test]
    public async Task LogoutCancel_StillRevokesWithoutResponse()
    {
        await using var world = await ReconnectWorld.OpenAsync();
        await using LiveSession session = await world.AddSessionAsync(96);
        await world.RegisterAsync(session, "ada", "secret-value", "Ann", 1);
        LoginResp login = await world.LoginAsync(session, "ada", "secret-value", 2);
        long playerId = login.Data.Info.PlayerId;
        await world.MutateNicknameAsync(playerId, "Held");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Saves.BeforePersistAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        };
        using var cts = new CancellationTokenSource();
        int frames = world.Frames(session).Count;
        Task pending = world.DispatchLogoutAsync(session, 3, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        release.TrySetResult();

        OperationCanceledException error = Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        Assert.That(error.Message.Contains(login.Token), Is.False);
        Assert.That(world.Frames(session).Count, Is.EqualTo(frames));
        DirtyCopy dirty = await world.CopyDirtyAsync(playerId);
        Assert.That(dirty.Dirty, Is.True);
        Assert.That(dirty.Nickname, Is.EqualTo("Held"));
        TokenValidation revoked = await world.Tokens.ValidateAsync(login.Token, NowMs, CancellationToken.None);
        Assert.That(revoked.Code, Is.EqualTo(204));
        Assert.That(session.Session.IsCloseRequested, Is.True);
        Assert.That(world.Sessions.MappedPlayerCount, Is.EqualTo(0));
    }

    /// <summary>204 和 106 都没有快照。</summary>
    /// <param name="response">重连响应。</param>
    /// <param name="code">期待的业务码。</param>
    static void AssertRejected(ReconnectResp response, int code)
    {
        Assert.That(response.Code, Is.EqualTo(code));
        Assert.That(response.Data, Is.Null);
        Assert.That(response.ServerTime, Is.EqualTo(0));
    }

    /// <summary>核对两份登录或重连快照的基础列一致。</summary>
    static void AssertSame(PlayerFullData expected, PlayerFullData actual)
    {
        Assert.That(actual, Is.Not.Null);
        Assert.That(actual.Info.PlayerId, Is.EqualTo(expected.Info.PlayerId));
        Assert.That(actual.Info.Nickname, Is.EqualTo(expected.Info.Nickname));
        Assert.That(actual.Info.Level, Is.EqualTo(expected.Info.Level));
        Assert.That(actual.Info.Exp, Is.EqualTo(expected.Info.Exp));
        Assert.That(actual.Info.Gold, Is.EqualTo(expected.Info.Gold));
        Assert.That(actual.Info.Gem, Is.EqualTo(expected.Info.Gem));
        Assert.That(actual.Info.Ticket, Is.EqualTo(expected.Info.Ticket));
        Assert.That(actual.Info.Shard, Is.EqualTo(expected.Info.Shard));
        Assert.That(actual.Info.BagSlots, Is.EqualTo(expected.Info.BagSlots));
        Assert.That(actual.Info.GuideStep, Is.EqualTo(expected.Info.GuideStep));
    }

    /// <summary>组装一份带集合字段的已保存快照。</summary>
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

    /// <summary>核对重连响应保留保存时的基础列和集合。</summary>
    static void AssertSameSnapshot(PlayerFullData saved, PlayerFullData actual)
    {
        AssertSame(saved, actual);
        Assert.That(actual.Bag.Count, Is.EqualTo(1));
        Assert.That(actual.Bag[0].ItemId, Is.EqualTo(100));
        Assert.That(actual.Bag[0].Count, Is.EqualTo(3));
        Assert.That(actual.Plots.Count, Is.EqualTo(1));
        Assert.That(actual.Plots[0].PlotId, Is.EqualTo(2));
        Assert.That(actual.OrderProgress.DayIndex, Is.EqualTo(4));
        Assert.That(actual.Home.Version, Is.EqualTo(5));
    }

    /// <summary>重连和登出测试用的临时服务端。</summary>
    sealed class ReconnectWorld : IAsyncDisposable
    {
        readonly IsolatedDirectory _dir;
        readonly ConcurrentDictionary<long, ClientSession> _connections;
        readonly List<LiveSession> _lives = new();
        readonly List<PlayerQueue> _queues = new();
        readonly List<SessionRegistry> _registries = new();

        ReconnectWorld(
            IsolatedDirectory dir,
            SqliteStore store,
            ConcurrentDictionary<long, ClientSession> connections,
            List<string> logs,
            MutableClock clock)
        {
            _dir = dir;
            _connections = connections;
            Store = store;
            Logs = logs;
            Clock = clock;
        }

        public SqliteStore Store { get; }
        public PlayerQueue Queue { get; private set; } = null!;
        public PlayerCache Cache { get; private set; } = null!;
        public TokenService Tokens { get; private set; } = null!;
        public SessionRegistry Sessions { get; private set; } = null!;
        public SaveService Saves { get; private set; } = null!;
        public AccountHandler Handler { get; private set; } = null!;
        public MessageRouter Router { get; private set; } = null!;
        public MutableClock Clock { get; }
        public List<string> Logs { get; }

        /// <summary>创建隔离库，并接上带保存服务的正式处理器。</summary>
        public static Task<ReconnectWorld> OpenAsync()
        {
            var dir = new IsolatedDirectory();
            var store = new SqliteStore();
            store.Initialize(Path.Combine(dir.FullPath, "game.db"));
            var logs = new List<string>();
            var connections = new ConcurrentDictionary<long, ClientSession>();
            var world = new ReconnectWorld(dir, store, connections, logs, new MutableClock { Now = NowMs });
            world.Install(new PlayerQueue(), new PlayerCache(new PlayerStore(store)));
            return Task.FromResult(world);
        }

        /// <summary>换一套新的队列、缓存、注册表和处理器。数据库和令牌行保持不变。</summary>
        public void ReplaceWithFreshCache()
        {
            Install(new PlayerQueue(), new PlayerCache(new PlayerStore(Store)));
        }

        /// <summary>把当前对外入口换成这套新对象。旧队列和注册表留到用例结束再释放。</summary>
        /// <param name="queue">新的玩家队列。</param>
        /// <param name="cache">新的玩家缓存。</param>
        void Install(PlayerQueue queue, PlayerCache cache)
        {
            var sessions = new SessionRegistry(queue, TimeSpan.FromSeconds(8), Logs.Add);
            var tokens = new TokenService(Store);
            var saves = new SaveService(queue, cache, new PlayerStore(Store));
            var options = new LoginSessionOptions(1, 100, 100, null, "0.1.0", () => Clock.Now);
            Func<RequestContext, Packet, CancellationToken, Task> send = (context, packet, token) =>
            {
                if (!_connections.TryGetValue(context.SessionId, out ClientSession? target))
                    throw new InvalidOperationException("会话不存在或已开始关闭");
                return target.SendAsync(packet, token);
            };
            var router = new MessageRouter(send, (context, reason, token) => Task.CompletedTask, new FixedClock(), Logs.Add);
            var handler = new AccountHandler(
                new AccountRepository(Store, new PasswordHasher()),
                queue,
                cache,
                tokens,
                sessions,
                sessionId => _connections.TryGetValue(sessionId, out ClientSession? found) ? found : null,
                options,
                saves,
                send);
            handler.RegisterRoutes(router);
            _queues.Add(queue);
            _registries.Add(sessions);
            Queue = queue;
            Cache = cache;
            Tokens = tokens;
            Sessions = sessions;
            Saves = saves;
            Handler = handler;
            Router = router;
        }

        /// <summary>启动一条受控会话并放进解析表。</summary>
        public async Task<LiveSession> AddSessionAsync(long sessionId)
        {
            var transport = new RecordingTransport();
            var stream = new GateStream();
            ClientSession session = ClientSession.CreateForTransport(sessionId, stream, transport, 8, null);
            Task run = session.RunAsync(CancellationToken.None);
            await Task.Yield();
            var live = new LiveSession(session, stream, run, transport.Snapshot);
            _connections[sessionId] = session;
            _lives.Add(live);
            return live;
        }

        /// <summary>经路由注册。</summary>
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
            Assert.That(RegisterResp.Parser.ParseFrom(frame.Body).Code, Is.EqualTo(0));
        }

        /// <summary>经路由登录并读取响应。</summary>
        public async Task<LoginResp> LoginAsync(LiveSession session, string username, string password, uint seq)
        {
            await DispatchAsync(session, new LoginReq
            {
                Username = username,
                Password = password,
                AppVersion = "0.1.0",
                ResVersion = 100,
                ProtoVersion = 1,
                ConfigVersion = 100,
                DeviceId = "device-1"
            }, seq);
            Packet frame = Frames(session).Single(item => item.MsgId == MsgRegistry.GetId(typeof(LoginResp)) && item.Seq == seq);
            return LoginResp.Parser.ParseFrom(frame.Body);
        }

        /// <summary>经路由重连并读取响应。</summary>
        public async Task<ReconnectResp> ReconnectAsync(LiveSession session, string token, uint seq, int protoVersion = 1)
        {
            await DispatchReconnectAsync(session, token, seq, protoVersion);
            return ReadReconnect(session, seq);
        }

        /// <summary>只分发重连。</summary>
        public Task DispatchReconnectAsync(LiveSession session, string token, uint seq, int protoVersion = 1, CancellationToken ct = default)
        {
            return DispatchAsync(session, new ReconnectReq { Token = token, ProtoVersion = protoVersion }, seq, ct);
        }

        /// <summary>读取指定序号的重连响应。</summary>
        public ReconnectResp ReadReconnect(LiveSession session, uint seq)
        {
            Packet frame = Frames(session).Single(item => item.MsgId == MsgRegistry.GetId(typeof(ReconnectResp)) && item.Seq == seq);
            Assert.That(frame.MsgId, Is.EqualTo((ushort)1008));
            return ReconnectResp.Parser.ParseFrom(frame.Body);
        }

        /// <summary>经路由登出。</summary>
        public Task LogoutAsync(LiveSession session, uint seq)
        {
            return DispatchLogoutAsync(session, seq);
        }

        /// <summary>只分发登出。</summary>
        public Task DispatchLogoutAsync(LiveSession session, uint seq, CancellationToken ct = default)
        {
            return DispatchAsync(session, new LogoutReq(), seq, ct);
        }

        /// <summary>用当前注册表构造上下文后分发。</summary>
        public Task DispatchAsync(LiveSession session, IMessage message, uint seq, CancellationToken ct = default)
        {
            ushort msgId = MsgRegistry.GetId(message.GetType());
            RequestContext context = Sessions.CreateRequestContext(session.Session, msgId, seq);
            return Router.DispatchAsync(context, new Packet(msgId, seq, message.ToByteArray()), ct);
        }

        /// <summary>在玩家队列里修改缓存昵称。</summary>
        public Task MutateNicknameAsync(long playerId, string nickname)
        {
            return Queue.EnqueueAsync(playerId, _ =>
            {
                Cache.ReplaceNickname(playerId, nickname);
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        /// <summary>在玩家队列里复制当前脏状态。</summary>
        public async Task<DirtyCopy> CopyDirtyAsync(long playerId)
        {
            bool dirty = false;
            string nickname = "";
            await Queue.EnqueueAsync(playerId, _ =>
            {
                dirty = Cache.TryCopyDirty(playerId, out PlayerState? snapshot);
                nickname = snapshot?.Data.Info.Nickname ?? "";
                return Task.CompletedTask;
            }, CancellationToken.None);
            return new DirtyCopy(dirty, nickname);
        }

        /// <summary>拆开这条会话已经写出的帧。</summary>
        public List<Packet> Frames(LiveSession session)
        {
            var frames = new List<Packet>();
            byte[] bytes = session.Snapshot();
            while (bytes.Length > 0)
            {
                DecodeStatus status = FrameCodec.TryDecode(bytes, out Packet packet, out int consumed);
                Assert.That(status, Is.EqualTo(DecodeStatus.Success));
                frames.Add(packet);
                bytes = bytes.AsSpan(consumed).ToArray();
            }

            return frames;
        }

        /// <summary>未撤销的令牌行数。</summary>
        public int ActiveTokenCount()
        {
            return (int)ScalarLong("SELECT COUNT(*) FROM auth_token WHERE revoked = 0;");
        }

        /// <summary>读取一个整数。</summary>
        public long ScalarLong(string sql)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
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

        /// <summary>结束会话、注册表、队列和库，并删除临时目录。</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (LiveSession live in _lives)
                await live.DisposeAsync();
            foreach (SessionRegistry registry in _registries)
            {
                try
                {
                    await registry.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                }
            }

            foreach (PlayerQueue queue in _queues)
                queue.Dispose();
            Store.Dispose();
            _dir.Dispose();
        }
    }

    /// <summary>队列内看到的脏昵称。</summary>
    /// <param name="Dirty">缓存版本尚未全部落库时为 true。</param>
    /// <param name="Nickname">脏副本里的昵称。不脏时为空串。</param>
    sealed record DirtyCopy(bool Dirty, string Nickname);

    /// <summary>测试可推进的 UTC 毫秒。</summary>
    sealed class MutableClock
    {
        /// <summary>下一次读取返回的 UTC Unix 毫秒。</summary>
        public long Now;
    }

    /// <summary>限流窗口用的固定时钟。</summary>
    sealed class FixedClock : IRouterClock
    {
        public long UnixMilliseconds => NowMs;
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

        public LiveSession(ClientSession session, GateStream stream, Task run, Func<byte[]> snapshot)
        {
            Session = session;
            _run = run;
            _snapshot = snapshot;
        }

        /// <summary>被绑定的服务端会话。</summary>
        public ClientSession Session { get; }

        /// <summary>复制这条会话已经记下的写出字节。</summary>
        public byte[] Snapshot() => _snapshot();

        /// <summary>结束读流并等待会话任务。</summary>
        public async ValueTask DisposeAsync()
        {
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

    /// <summary>读端一直等待，取消或释放之后返回。</summary>
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

        /// <summary>释放时放开等待中的读。</summary>
        protected override void Dispose(bool disposing)
        {
            _done.TrySetResult();
            base.Dispose(disposing);
        }
    }

    /// <summary>只删除系统临时目录里本测试创建的子目录。</summary>
    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingAccountReconnectTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        /// <summary>本用例的绝对目录。</summary>
        public string FullPath { get; }

        /// <summary>目录仍在指定根下时才递归删除。</summary>
        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingAccountReconnectTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
