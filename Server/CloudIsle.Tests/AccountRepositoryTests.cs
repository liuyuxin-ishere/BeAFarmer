using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Players;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>PasswordHasher 不连接数据库。断言不输出密码、盐或派生结果。</summary>
public sealed class PasswordHasherTests
{
    /// <summary>同一密码配两次随机盐，结果必须不同，而且各自都能核对。</summary>
    [Test]
    public void SamePassword_DifferentSalts_HashDifferAndBothVerify()
    {
        var hasher = new PasswordHasher();
        byte[] firstSalt = RandomNumberGenerator.GetBytes(PasswordHasher.SaltLength);
        byte[] secondSalt = RandomNumberGenerator.GetBytes(PasswordHasher.SaltLength);
        string first = hasher.Hash("correct horse", firstSalt);
        string second = hasher.Hash("correct horse", secondSalt);

        Assert.That(first, Is.Not.EqualTo(second));
        Assert.That(hasher.Verify("correct horse", firstSalt, first), Is.True);
        Assert.That(hasher.Verify("correct horse", secondSalt, second), Is.True);
        Assert.That(hasher.Verify("other horse", firstSalt, first), Is.False);
    }

    /// <summary>损坏文本、未知算法和不受支持的迭代都直接失败。</summary>
    [Test]
    public void DamagedOrUnknownFormat_ReturnsFalseWithoutLargeWork()
    {
        var hasher = new PasswordHasher();
        byte[] salt = RandomNumberGenerator.GetBytes(PasswordHasher.SaltLength);
        string valid = hasher.Hash("correct horse", salt);
        string unknown = valid.Replace("pbkdf2-sha256", "sha256", StringComparison.Ordinal);
        string hugeIterations = "pbkdf2-sha256$1$999999999$32$" + new string('A', 44);
        var watch = Stopwatch.StartNew();

        Assert.That(hasher.Verify("correct horse", salt, ""), Is.False);
        Assert.That(hasher.Verify("correct horse", salt, valid.Substring(0, 12)), Is.False);
        Assert.That(hasher.Verify("correct horse", salt, unknown), Is.False);
        Assert.That(hasher.Verify("correct horse", salt, hugeIterations), Is.False);
        watch.Stop();

        Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500));
        Assert.That(hasher.Verify("correct horse", salt, valid), Is.True);
    }
}

/// <summary>注册事务、唯一冲突和认证闭环。</summary>
public sealed class AccountRepositoryTests
{
    /// <summary>并发使用同一个用户名时，只有一次注册成功。</summary>
    [Test]
    public async Task ConcurrentSameUsername_CreatesOneAccountAndPlayer()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var repository = new AccountRepository(store, new PasswordHasher());
        var tasks = new Task<int>[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = repository.RegisterAsync(Request("same", "secret", "Ann"), CancellationToken.None);

        int[] codes = await Task.WhenAll(tasks);
        Assert.That(codes.Count(code => code == 0), Is.EqualTo(1));
        Assert.That(codes.Count(code => code == 201), Is.EqualTo(7));
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));
        Assert.That(Count(connection, "player"), Is.EqualTo(1));
        Assert.That(Count(connection, "auth_token"), Is.EqualTo(0));
        Assert.That(OrphanAccounts(connection), Is.EqualTo(0));
        Assert.That(OrphanPlayers(connection), Is.EqualTo(0));
    }

    /// <summary>未写的玩家列使用 schema 默认值，并且没有快照。</summary>
    [Test]
    public async Task Register_UsesSchemaDefaults()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var repository = new AccountRepository(store, new PasswordHasher());
        Assert.That(await repository.RegisterAsync(Request("ada", "secret", "Ada"), CancellationToken.None), Is.EqualTo(0));

        using var connection = Open(DatabasePath(dir));
        long playerId = ScalarLong(connection, "SELECT id FROM player;");
        Assert.That(ScalarLong(connection, "SELECT level FROM player;"), Is.EqualTo(1));
        Assert.That(ScalarLong(connection, "SELECT exp FROM player;"), Is.EqualTo(0));
        Assert.That(ScalarLong(connection, "SELECT gold FROM player;"), Is.EqualTo(200));
        Assert.That(ScalarLong(connection, "SELECT gem FROM player;"), Is.EqualTo(300));
        Assert.That(ScalarLong(connection, "SELECT ticket FROM player;"), Is.EqualTo(5));
        Assert.That(ScalarLong(connection, "SELECT shard FROM player;"), Is.EqualTo(0));
        Assert.That(ScalarLong(connection, "SELECT bag_slots FROM player;"), Is.EqualTo(60));
        Assert.That(ScalarLong(connection, "SELECT guide_step FROM player;"), Is.EqualTo(0));
        Assert.That(ScalarLong(connection, "SELECT last_daily_reset FROM player;"), Is.EqualTo(0));
        Assert.That(Count(connection, "player_snapshot"), Is.EqualTo(0));

        var loaded = await new PlayerStore(store).LoadAsync(playerId, CancellationToken.None);
        Assert.That(loaded.Data.Info.Nickname, Is.EqualTo("Ada"));
        Assert.That(loaded.Data.Info.Level, Is.EqualTo(1));
        Assert.That(loaded.Data.Info.Gold, Is.EqualTo(200));
        Assert.That(loaded.Data.Info.Gem, Is.EqualTo(300));
        Assert.That(loaded.Data.Info.Ticket, Is.EqualTo(5));
        Assert.That(loaded.Data.Info.BagSlots, Is.EqualTo(60));
        Assert.That(loaded.Revision, Is.EqualTo(0));
    }

    /// <summary>注册后可以认证；关闭库再打开仍然可以。</summary>
    [Test]
    public async Task Register_ThenAuthenticate_SurvivesReopen()
    {
        using var dir = new IsolatedDirectory();
        string database = DatabasePath(dir);
        long playerId;
        using (var store = OpenStore(dir))
        {
            var repository = new AccountRepository(store, new PasswordHasher());
            Assert.That(await repository.RegisterAsync(Request("ada", "secret", "Ada"), CancellationToken.None), Is.EqualTo(0));
            playerId = await repository.AuthenticateAsync("ada", "secret", CancellationToken.None);
            Assert.That(playerId, Is.GreaterThan(0));
            Assert.That(await repository.AuthenticateAsync("ada", "wrong", CancellationToken.None), Is.EqualTo(0));
            Assert.That(await repository.AuthenticateAsync("missing", "secret", CancellationToken.None), Is.EqualTo(0));
        }

        using var again = new SqliteStore();
        again.Initialize(database);
        var reopened = new AccountRepository(again, new PasswordHasher());
        Assert.That(await reopened.AuthenticateAsync("ada", "secret", CancellationToken.None), Is.EqualTo(playerId));
    }

    /// <summary>大小写不同的用户名按现有二进制唯一规则各算一个账号。</summary>
    [Test]
    public async Task UsernameComparison_KeepsBinaryCase()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var repository = new AccountRepository(store, new PasswordHasher());
        Assert.That(await repository.RegisterAsync(Request("Ada", "secret", "A"), CancellationToken.None), Is.EqualTo(0));
        Assert.That(await repository.RegisterAsync(Request("ada", "secret", "B"), CancellationToken.None), Is.EqualTo(0));
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(2));
    }

    /// <summary>玩家插入失败时，账号插入一并回滚，并且不返回重名。</summary>
    [Test]
    public async Task PlayerInsertFailure_RollsBackAccount()
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

        var repository = new AccountRepository(store, new PasswordHasher());
        SqliteException? error = null;
        try
        {
            await repository.RegisterAsync(Request("ada", "secret", "Ada"), CancellationToken.None);
            Assert.Fail("玩家插入失败时应抛出数据库异常");
        }
        catch (SqliteException ex)
        {
            error = ex;
        }

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("player-insert-aborted"));
        Assert.That(error.Message, Does.Not.Contain("account.username"));
        using var check = Open(DatabasePath(dir));
        Assert.That(Count(check, "account"), Is.EqualTo(0));
        Assert.That(Count(check, "player"), Is.EqualTo(0));
    }

    /// <summary>带引号和语句片段的用户名只作为参数值。</summary>
    [Test]
    public async Task InjectedUsername_IsStoredAsLiteral()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        string username = "x' OR 1=1; DROP TABLE account; --";
        var repository = new AccountRepository(store, new PasswordHasher());
        Assert.That(await repository.RegisterAsync(Request(username, "secret", "Ann"), CancellationToken.None), Is.EqualTo(0));

        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));
        Assert.That(ScalarString(connection, "SELECT username FROM account;"), Is.EqualTo(username));
        long playerId = ScalarLong(connection, "SELECT id FROM player;");
        Assert.That(await repository.AuthenticateAsync(username, "secret", CancellationToken.None), Is.EqualTo(playerId));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));
    }

    /// <summary>注册已经排进队列但还没执行时取消，不留下账号。</summary>
    [Test]
    public async Task CancelWhileQueued_LeavesNoAccount()
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
        using var cts = new CancellationTokenSource();
        var repository = new AccountRepository(store, new PasswordHasher());
        Task<int> pending = repository.RegisterAsync(Request("queued", "secret", "Ann"), cts.Token);
        cts.Cancel();
        release.TrySetResult();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
        await hold;
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection, "account"), Is.EqualTo(0));
        Assert.That(Count(connection, "player"), Is.EqualTo(0));
    }

    static RegisterReq Request(string username, string password, string nickname)
    {
        return new RegisterReq
        {
            Username = username,
            Password = password,
            Nickname = nickname
        };
    }

    static SqliteStore OpenStore(IsolatedDirectory dir)
    {
        var store = new SqliteStore();
        store.Initialize(DatabasePath(dir));
        return store;
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

    static long Count(SqliteConnection connection, string table)
    {
        return ScalarLong(connection, "SELECT COUNT(*) FROM " + table + ";");
    }

    static long OrphanAccounts(SqliteConnection connection)
    {
        return ScalarLong(connection, """
            SELECT COUNT(*)
            FROM account a
            LEFT JOIN player p ON p.account_id = a.id
            WHERE p.id IS NULL;
            """);
    }

    static long OrphanPlayers(SqliteConnection connection)
    {
        return ScalarLong(connection, """
            SELECT COUNT(*)
            FROM player p
            LEFT JOIN account a ON a.id = p.account_id
            WHERE a.id IS NULL;
            """);
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

    /// <summary>只删除系统临时目录里本测试创建的子目录。</summary>
    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingAccountRepositoryTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingAccountRepositoryTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
