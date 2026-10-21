using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;

namespace CloudIsle.Tests;

public sealed class LogoutSessionLoopTests
{
    /// <summary>玩家执行器来自后台上下文时，真实收包回调中的登出也不能等待自己的 Completion。</summary>
    [Test]
    public async Task Logout_FromSessionLoop_WithExistingBackgroundLane_DoesNotWaitForItself()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "FarmingLogoutLoopReviewTests");
        string testDirectory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            using var store = new SqliteStore();
            store.Initialize(Path.Combine(testDirectory, "game.db"));
            using var queue = new PlayerQueue();
            var accounts = new AccountRepository(store, new PasswordHasher());
            Assert.That(await accounts.RegisterAsync(new RegisterReq
            {
                Username = "loop-review", Password = "test-value", Nickname = "Ann"
            }, CancellationToken.None), Is.Zero);
            long playerId = await accounts.AuthenticateAsync("loop-review", "test-value", CancellationToken.None);
            var players = new PlayerStore(store);
            var cache = new PlayerCache(players);
            var tokens = new TokenService(store);
            string token = await tokens.IssueAsync(playerId, 10_000, CancellationToken.None);
            Assert.That((await tokens.ValidateAsync(token, 0, CancellationToken.None)).Code, Is.Zero);
            var sessions = new SessionRegistry(queue, TimeSpan.FromSeconds(2), static _ => { });
            using var saves = new SaveService(queue, cache, players);
            var entered = Signal();
            var allowDispatch = Signal();
            var submitted = Signal();
            var handled = Signal();
            var backgroundEntered = Signal();
            var releaseBackground = Signal();
            int responses = 0;
            ClientSession? session = null;
            MessageRouter router = null!;
            using var input = new MemoryStream(FrameCodec.Encode(
                MsgRegistry.GetId(typeof(LogoutReq)), 17, new LogoutReq().ToByteArray()));
            session = ClientSession.CreateForTransport(975, input, new StreamFrameTransport(Stream.Null), 8,
                async (peer, packet, ct) =>
                {
                    entered.TrySetResult();
                    await allowDispatch.Task;
                    try
                    {
                        // 合法 Logout 在第一个不完成的 await 前已入玩家队列；返回 Task 后才发 submitted。
                        Task dispatch = router.DispatchAsync(sessions.CreateRequestContext(peer, packet.MsgId, packet.Seq), packet, ct);
                        submitted.TrySetResult();
                        await dispatch;
                        handled.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        handled.TrySetException(error);
                        throw;
                    }
                });
            Func<RequestContext, Packet, CancellationToken, Task> send = (context, packet, ct) =>
            {
                Interlocked.Increment(ref responses);
                return session.SendAsync(packet, ct);
            };
            router = new MessageRouter(send, (context, reason, ct) => session.CloseAsync(reason, ct));
            var handler = new AccountHandler(accounts, queue, cache, tokens, sessions,
                id => id == session.SessionId ? session : null,
                new LoginSessionOptions(1, 100, 100), saves, send);
            handler.RegisterRoutes(router);
            Task run = session.RunAsync(CancellationToken.None);
            Task? background = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await sessions.BindAsync(playerId, session, CancellationToken.None);
                await queue.WaitForIdleAsync(playerId).WaitAsync(TimeSpan.FromSeconds(5));
                // 先从普通测试/后台上下文创建玩家执行器，后来的收包回调复用这个执行器。
                background = queue.EnqueueAsync(playerId, async _ =>
                {
                    backgroundEntered.TrySetResult();
                    await releaseBackground.Task;
                }, CancellationToken.None);
                await backgroundEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                allowDispatch.TrySetResult();
                await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseBackground.TrySetResult();
                // 旧实现会等2秒关闭预算；这里只等不含网络/密码派生的登出队列处理。
                await handled.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                await sessions.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(responses, Is.Zero);
                Assert.That(sessions.MappedPlayerCount, Is.Zero);
                Assert.That(sessions.WatchCount, Is.Zero);
                Assert.That((await tokens.ValidateAsync(token, 0, CancellationToken.None)).Code, Is.EqualTo(204));
            }
            finally
            {
                allowDispatch.TrySetResult();
                releaseBackground.TrySetResult();
                await session.CloseAsync("测试收尾", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                if (background != null)
                    await background.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            RemoveTestDirectory(testDirectory, testRoot);
        }
    }

    /// <summary>创建异步续体屏障，避免在设置屏障的一方内联运行测试后半段。</summary>
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>只清理本测试在指定根下创建的 GUID 子目录。</summary>
    /// <param name="directory">本用例创建的目录。</param>
    /// <param name="allowedRoot">测试专用临时根，不能删除其他路径。</param>
    static void RemoveTestDirectory(string directory, string allowedRoot)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理测试根之外的目录");
        if (Directory.Exists(full))
            Directory.Delete(full, true);
    }
}
