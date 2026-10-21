using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

public sealed class SqliteStoreQueueTests
{
    /// <summary>首次打开队列连接失败时，异常交付给调用方；恢复路径后队列仍可使用。</summary>
    [Test]
    public async Task InitialConnectionFailure_IsDeliveredAndQueueRecovers()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        string backup = Path.Combine(dir.FullPath, "saved.db");
        using var store = new SqliteStore();
        store.Initialize(database);
        File.Move(database, backup);
        Directory.CreateDirectory(database);

        Task<int> failed = store.ExecuteAsync(_ => 1, CancellationToken.None);
        Assert.ThrowsAsync<SqliteException>(async () => await failed.WaitAsync(TimeSpan.FromSeconds(5)));

        // 只移除本测试创建的空目录，再恢复原数据库；不接触真实账号库。
        Directory.Delete(database);
        File.Move(backup, database);
        int count = await store.ExecuteAsync(
            connection => ScalarInt(connection, "SELECT COUNT(*) FROM account;"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(count, Is.Zero);
    }

    [Test]
    public void ExecuteAsync_BeforeInitialize_Throws()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using var store = new SqliteStore();

        Assert.Throws<InvalidOperationException>(() => store.ExecuteAsync(connection => 1, CancellationToken.None));
        Assert.That(File.Exists(database), Is.False);
    }

    [Test]
    public async Task ConcurrentExecute_DoesNotOverlapAndSharesOneConnection()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using var store = new SqliteStore();
        store.Initialize(database);

        int busy = 0;
        int overlaps = 0;
        SqliteConnection? seen = null;
        var tasks = new Task<int>[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            int id = i + 1;
            tasks[i] = store.ExecuteAsync(connection =>
            {
                if (Interlocked.Increment(ref busy) != 1)
                    Interlocked.Increment(ref overlaps);
                SqliteConnection? previous = Interlocked.CompareExchange(ref seen, connection, null);
                if (previous != null && !ReferenceEquals(previous, connection))
                    throw new InvalidOperationException("队列使用了另一条连接");

                Thread.Sleep(30);
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES ($id, $name, 'hash', 'salt', 1);";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$name", "user-" + id.ToString());
                command.ExecuteNonQuery();
                Interlocked.Decrement(ref busy);
                return id;
            }, CancellationToken.None);
        }

        int[] ids = await Task.WhenAll(tasks);
        Assert.That(overlaps, Is.EqualTo(0));
        Assert.That(ids, Is.EquivalentTo(Enumerable.Range(1, 8)));
        int count = await store.ExecuteAsync(connection => ScalarInt(connection, "SELECT COUNT(*) FROM account;"), CancellationToken.None);
        Assert.That(count, Is.EqualTo(8));
    }

    [Test]
    public async Task FailedOperation_DeliversExceptionAndQueueContinues()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using var store = new SqliteStore();
        store.Initialize(database);

        Task<int> failed = store.ExecuteAsync<int>(connection => throw new InvalidOperationException("boom"), CancellationToken.None);
        Task<int> inserted = store.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (1, 'kept', 'hash', 'salt', 2);";
            command.ExecuteNonQuery();
            return ScalarInt(connection, "SELECT COUNT(*) FROM account;");
        }, CancellationToken.None);

        InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(async () => await failed);
        Assert.That(error!.Message, Is.EqualTo("boom"));
        Assert.That(await inserted, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedTransaction_IsDiscardedBeforeNextOperation()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using var store = new SqliteStore();
        store.Initialize(database);

        Task<int> failed = store.ExecuteAsync<int>(connection =>
        {
            SqliteTransaction transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (1, 'temp', 'hash', 'salt', 3);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("uncommitted");
        }, CancellationToken.None);

        Task<int> followed = store.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            int count = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM account;");
            transaction.Commit();
            return count;
        }, CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await failed);
        Assert.That(await followed, Is.EqualTo(0));
    }

    [Test]
    public async Task CanceledBeforeStart_IsNotRunAndLaterWriteSucceeds()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using var store = new SqliteStore();
        store.Initialize(database);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Task<int> blocked = store.ExecuteAsync(connection =>
        {
            entered.Set();
            release.Wait();
            return 1;
        }, CancellationToken.None);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

        int ran = 0;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Task<int> skipped = store.ExecuteAsync(connection =>
        {
            Interlocked.Increment(ref ran);
            return 1;
        }, canceled.Token);
        Task<int> wrote = store.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (4, 'later', 'hash', 'salt', 4);";
            command.ExecuteNonQuery();
            return ScalarInt(connection, "SELECT COUNT(*) FROM account;");
        }, CancellationToken.None);

        release.Set();
        Assert.That(await blocked, Is.EqualTo(1));
        Assert.ThrowsAsync<TaskCanceledException>(async () => await skipped);
        Assert.That(ran, Is.EqualTo(0));
        Assert.That(await wrote, Is.EqualTo(1));
    }

    [Test]
    public async Task Initialize_AfterQueueStarts_IsRejectedAndDataRemains()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using var store = new SqliteStore();
        store.Initialize(database);
        store.Initialize(database);
        using (var connection = Open(database))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (8, 'bea', 'hash', 'salt', 5);";
            command.ExecuteNonQuery();
        }

        string name = await store.ExecuteAsync(
            connection => ScalarString(connection, "SELECT username FROM account WHERE id = 8;"),
            CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => store.Initialize(database));
        string again = await store.ExecuteAsync(
            connection => ScalarString(connection, "SELECT username FROM account WHERE id = 8;"),
            CancellationToken.None);

        Assert.That(name, Is.EqualTo("bea"));
        Assert.That(again, Is.EqualTo("bea"));
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
        return connection;
    }

    static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    static int ScalarInt(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingSqliteStoreQueueTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingSqliteStoreQueueTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (!Directory.Exists(full))
                return;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(full, true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
