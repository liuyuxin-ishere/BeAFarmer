using System.Collections;
using System.Globalization;
using System.Reflection;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Network;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>经 MessageRouter 调用正式注册。临时库与 PoC 账号库分开。</summary>
public sealed class AccountHandlerTests
{
    /// <summary>正式路由注册成功后只回一条 code 为 0 的 RegisterResp，且会话仍未认证。</summary>
    [Test]
    public async Task RegisterRoutes_Success_LeavesSessionUnauthenticated()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        var traces = new List<string>();
        var router = new MessageRouter(
            (context, packet, token) =>
            {
                Assert.That(context.PlayerId, Is.EqualTo(0));
                sent.Add(packet);
                return Task.CompletedTask;
            },
            (context, reason, token) => Task.CompletedTask,
            new SteadyClock(),
            line => traces.Add(line));
        var accounts = new AccountHandler(new AccountRepository(store, new PasswordHasher()), (context, packet, token) =>
        {
            Assert.That(context.PlayerId, Is.EqualTo(0));
            sent.Add(packet);
            return Task.CompletedTask;
        });
        var heartbeat = new HeartbeatHandler((context, packet, token) =>
        {
            Assert.That(context.PlayerId, Is.EqualTo(0));
            sent.Add(packet);
            return Task.CompletedTask;
        });
        accounts.RegisterRoutes(router);
        heartbeat.Register(router);
        int logoutCalls = 0;
        router.Register<LogoutReq>(MsgRegistry.GetId(typeof(LogoutReq)), (context, request, token) =>
        {
            logoutCalls++;
            return Task.CompletedTask;
        });

        await DispatchAsync(router, 41, new RegisterReq
        {
            Username = "ada",
            Password = "secret",
            Nickname = "Ann",
            ProtoVersion = 1,
            DeviceId = "device-1"
        }, 44);

        Assert.That(sent, Has.Count.EqualTo(1));
        AssertRegister(sent[0], 44, 0);
        using (var connection = Open(DatabasePath(dir)))
        {
            Assert.That(Count(connection, "account"), Is.EqualTo(1));
            Assert.That(Count(connection, "player"), Is.EqualTo(1));
            Assert.That(Count(connection, "auth_token"), Is.EqualTo(0));
            Assert.That(ScalarString(connection, "SELECT nickname FROM player;"), Is.EqualTo("Ann"));
        }

        await DispatchAsync(router, 41, new HeartbeatReq { ClientTime = 5 }, 45);
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(sent[1].MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(HeartbeatResp))));
        Assert.That(sent[1].Seq, Is.EqualTo(45));
        Assert.That(HeartbeatResp.Parser.ParseFrom(sent[1].Body).Code, Is.EqualTo(0));

        await DispatchAsync(router, 41, new LogoutReq(), 46);
        Assert.That(logoutCalls, Is.EqualTo(0));
        Assert.That(sent, Has.Count.EqualTo(2));
        Assert.That(string.Join("\n", traces), Does.Contain("session=41 msg=1009 seq=46 code=102 action=reject"));
    }

    /// <summary>同一用户名第二次注册返回 201，库里仍然各一行。</summary>
    [Test]
    public async Task DuplicateUsername_Returns201AndKeepsOneRow()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);

        await DispatchAsync(router, 7, Request("ada", "secret", "Ann"), 1);
        await DispatchAsync(router, 7, Request("ada", "other", "Bea"), 2);

        Assert.That(sent, Has.Count.EqualTo(2));
        AssertRegister(sent[0], 1, 0);
        AssertRegister(sent[1], 2, 201);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));
        Assert.That(Count(connection, "player"), Is.EqualTo(1));
        Assert.That(ScalarString(connection, "SELECT nickname FROM player;"), Is.EqualTo("Ann"));
    }

    /// <summary>空串和纯空白昵称返回 203，不创建账号或玩家。</summary>
    [Test]
    public async Task BlankNickname_Returns203AndWritesNothing()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);
        string[] nicknames = { "", " ", "\t\r\n", "\u00a0" };

        for (uint seq = 1; seq <= nicknames.Length; seq++)
            await DispatchAsync(router, 8, Request("ada" + seq.ToString(CultureInfo.InvariantCulture), "secret", nicknames[seq - 1]), seq);

        Assert.That(sent, Has.Count.EqualTo(nicknames.Length));
        for (int i = 0; i < nicknames.Length; i++)
            AssertRegister(sent[i], (uint)(i + 1), 203);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(0));
        Assert.That(Count(connection, "player"), Is.EqualTo(0));
    }

    /// <summary>含控制字符的昵称返回 203，不写库。</summary>
    [Test]
    public async Task ControlNickname_Returns203AndWritesNothing()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);

        await DispatchAsync(router, 9, Request("ada", "secret", "Ann\u0001"), 6);

        Assert.That(sent, Has.Count.EqualTo(1));
        AssertRegister(sent[0], 6, 203);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(0));
        Assert.That(Count(connection, "player"), Is.EqualTo(0));
    }

    /// <summary>协议未规定是否裁剪。两端有空格的昵称原样入库。</summary>
    [Test]
    public async Task SurroundingSpaces_AreStoredVerbatim()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);

        await DispatchAsync(router, 10, Request("ada", "secret", " Ann "), 3);

        Assert.That(sent, Has.Count.EqualTo(1));
        AssertRegister(sent[0], 3, 0);
        using var connection = Open(DatabasePath(dir));
        Assert.That(ScalarString(connection, "SELECT nickname FROM player;"), Is.EqualTo(" Ann "));
    }

    /// <summary>引号和语句片段只作为参数值，不删除表。</summary>
    [Test]
    public async Task InjectedText_IsStoredAsLiteral()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);
        string username = "x' OR 1=1; DROP TABLE account; --";
        string nickname = "n'; DELETE FROM player; --";

        await DispatchAsync(router, 11, Request(username, "p' OR '1'='1", nickname), 4);

        Assert.That(sent, Has.Count.EqualTo(1));
        AssertRegister(sent[0], 4, 0);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));
        Assert.That(Count(connection, "player"), Is.EqualTo(1));
        Assert.That(Count(connection, "auth_token"), Is.EqualTo(0));
        Assert.That(ScalarString(connection, "SELECT username FROM account;"), Is.EqualTo(username));
        Assert.That(ScalarString(connection, "SELECT nickname FROM player;"), Is.EqualTo(nickname));
        Assert.That(ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'account';"), Is.EqualTo(1));
    }

    /// <summary>玩家插入失败时不发送成功响应，账号和玩家都回滚。</summary>
    [Test]
    public async Task PlayerInsertFailure_DoesNotSendSuccessAndRollsBack()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        using (var connection = Open(DatabasePath(dir)))
        {
            Execute(connection, """
                CREATE TRIGGER abort_player BEFORE INSERT ON player
                BEGIN
                  SELECT RAISE(ABORT, 'player-insert-aborted');
                END;
                """);
        }

        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);

        SqliteException? error = null;
        try
        {
            await DispatchAsync(router, 12, Request("ada", "secret", "Ann"), 5);
            Assert.Fail("玩家插入失败时应抛出数据库异常");
        }
        catch (SqliteException ex)
        {
            error = ex;
        }

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("player-insert-aborted"));
        Assert.That(sent, Is.Empty);
        using var check = Open(DatabasePath(dir));
        Assert.That(Count(check, "account"), Is.EqualTo(0));
        Assert.That(Count(check, "player"), Is.EqualTo(0));
    }

    /// <summary>分发开始前已经取消时，不回包也不写库。</summary>
    [Test]
    public void AlreadyCanceled_SendsNothing()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => DispatchAsync(
            router,
            13,
            Request("ada", "secret", "Ann"),
            7,
            cts.Token));
        Assert.That(sent, Is.Empty);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(0));
        Assert.That(Count(connection, "player"), Is.EqualTo(0));
    }

    /// <summary>注册已排进数据库队列但还没执行时取消，不回成功包，也不留账号。</summary>
    [Test]
    public async Task CancelWhileQueued_SendsNothingAndLeavesNoRows()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> hold = store.ExecuteAsync(_ =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("排队占用没有被放开");
            return 0;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sent = new List<Packet>();
        MessageRouter router = Router(sent);
        Handler(store, sent).RegisterRoutes(router);
        using var cts = new CancellationTokenSource();
        Task pending = DispatchAsync(router, 14, Request("queued", "secret", "Ann"), 8, cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (PendingCount(store) < 1)
            {
                if (pending.IsCompleted)
                    Assert.Fail("注册在入队前就结束了");
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("注册没有排进数据库队列");
                await Task.Delay(15);
            }

            cts.Cancel();
            release.TrySetResult();
            Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
            await hold;
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.That(sent, Is.Empty);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(0));
        Assert.That(Count(connection, "player"), Is.EqualTo(0));
    }

    /// <summary>构造路由。拒绝响应会进入这个列表；注册成功的回包由处理器自己的发送委托写入同一列表。</summary>
    /// <param name="sent">收到的响应帧。</param>
    static MessageRouter Router(List<Packet> sent)
    {
        return new MessageRouter(
            (context, packet, token) =>
            {
                sent.Add(packet);
                return Task.CompletedTask;
            },
            (context, reason, token) => Task.CompletedTask);
    }

    /// <summary>构造处理器。正式注册响应由这里的发送委托写出。</summary>
    /// <param name="store">已经初始化的临时库。</param>
    /// <param name="sent">处理器回包写入的列表。</param>
    static AccountHandler Handler(SqliteStore store, List<Packet> sent)
    {
        return new AccountHandler(
            new AccountRepository(store, new PasswordHasher()),
            (context, packet, token) =>
            {
                sent.Add(packet);
                return Task.CompletedTask;
            });
    }

    /// <summary>用玩家编号 0 把正式消息交给路由。</summary>
    /// <param name="router">已注册注册处理函数的路由。</param>
    /// <param name="sessionId">连接会话编号。</param>
    /// <param name="message">正式请求。</param>
    /// <param name="seq">请求序号。</param>
    /// <param name="ct">取消这一次分发。</param>
    static Task DispatchAsync(MessageRouter router, long sessionId, IMessage message, uint seq, CancellationToken ct = default)
    {
        ushort msgId = MsgRegistry.GetId(message.GetType());
        return router.DispatchAsync(
            new RequestContext(sessionId, 0, msgId, seq),
            new Packet(msgId, seq, message.ToByteArray()),
            ct);
    }

    /// <summary>组装一条注册请求。</summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码原文。</param>
    /// <param name="nickname">昵称。</param>
    static RegisterReq Request(string username, string password, string nickname)
    {
        return new RegisterReq
        {
            Username = username,
            Password = password,
            Nickname = nickname
        };
    }

    /// <summary>核对注册响应的消息 ID、序号和 code。</summary>
    /// <param name="packet">路由写出的帧。</param>
    /// <param name="seq">期待的序号。</param>
    /// <param name="code">期待的业务码。</param>
    static void AssertRegister(Packet packet, uint seq, int code)
    {
        Assert.That(packet.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(RegisterResp))));
        Assert.That(packet.MsgId, Is.EqualTo((ushort)1004));
        Assert.That(packet.Seq, Is.EqualTo(seq));
        Assert.That(RegisterResp.Parser.ParseFrom(packet.Body).Code, Is.EqualTo(code));
    }

    /// <summary>打开临时库并迁移到当前 schema。</summary>
    /// <param name="dir">本用例的临时目录。</param>
    static SqliteStore OpenStore(IsolatedDirectory dir)
    {
        var store = new SqliteStore();
        store.Initialize(DatabasePath(dir));
        return store;
    }

    /// <summary>临时库文件路径。</summary>
    /// <param name="dir">本用例的临时目录。</param>
    static string DatabasePath(IsolatedDirectory dir)
    {
        return Path.Combine(dir.FullPath, "game.db");
    }

    /// <summary>另开一条连接读取结果。外键打开，避免和队列连接混用事务。</summary>
    /// <param name="database">数据库文件路径。</param>
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

    /// <summary>执行一段不带外部拼接值的 SQL。</summary>
    /// <param name="connection">当前读取连接。</param>
    /// <param name="sql">固定 SQL 文本。</param>
    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>统计一张表的行数。</summary>
    /// <param name="connection">当前读取连接。</param>
    /// <param name="table">已知表名。</param>
    static long Count(SqliteConnection connection, string table)
    {
        return ScalarLong(connection, "SELECT COUNT(*) FROM " + table + ";");
    }

    /// <summary>读取一个整数结果。</summary>
    /// <param name="connection">当前读取连接。</param>
    /// <param name="sql">固定查询。</param>
    static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>读取一个文本结果。</summary>
    /// <param name="connection">当前读取连接。</param>
    /// <param name="sql">固定查询。</param>
    static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>读取尚未被队列线程取走的数据库操作数量。</summary>
    /// <param name="store">正在使用的存储。</param>
    static int PendingCount(SqliteStore store)
    {
        FieldInfo? field = typeof(SqliteStore).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic);
        var queue = field?.GetValue(store) as ICollection;
        return queue?.Count ?? 0;
    }

    /// <summary>只删除系统临时目录里本测试创建的子目录。</summary>
    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingAccountHandlerTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingAccountHandlerTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }

    /// <summary>限流窗口用的固定时钟。本项不断言限流。</summary>
    sealed class SteadyClock : IRouterClock
    {
        public long UnixMilliseconds => 1_700_000_000_000L;
    }
}
