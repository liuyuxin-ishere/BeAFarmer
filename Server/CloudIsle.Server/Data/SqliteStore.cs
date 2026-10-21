using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Data;

/// <summary>
/// 打开指定的 SQLite 库并迁移到当前 schema。业务 SQL 经唯一队列连接同步执行。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    public const int CurrentVersion = 2;

    readonly object _gate = new();
    string? _databasePath;
    BlockingCollection<IWork>? _queue;
    Thread? _worker;
    bool _disposed;

    /// <summary>创建缺失的父目录，并把库迁移到当前版本。高于当前版本时拒绝且不改库。</summary>
    /// <param name="databasePath">数据库文件路径。空白时抛出异常。</param>
    public void Initialize(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径为空", nameof(databasePath));

        string fullPath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SqliteStore));
            if (_worker != null)
                throw new InvalidOperationException("队列连接已打开，拒绝再次初始化");

            using var connection = new SqliteConnection(ConnectionString(fullPath));
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }

            int version = ReadVersion(connection);
            if (version > CurrentVersion)
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "数据库 schema 版本 {0} 高于当前版本 {1}，拒绝降级", version, CurrentVersion));

            if (version < CurrentVersion)
                MigrateToCurrent(connection, version);

            using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
            _databasePath = fullPath;
        }
    }

    /// <summary>把同步操作排进唯一的数据库连接。Task 只表示等待排队结果。</summary>
    /// <param name="operation">在队列线程上执行的同步操作。为空时抛出异常。</param>
    /// <param name="ct">入队前取消则不执行。</param>
    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> operation, CancellationToken ct)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SqliteStore));
            if (_databasePath == null)
                throw new InvalidOperationException("数据库尚未初始化");

            EnsureWorker();
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<T>(ct);

            var item = new WorkItem<T>(operation, ct);
            _queue!.Add(item);
            return item.Task;
        }
    }

    /// <summary>拒绝新的入队，取消尚未开始的操作，并在当前操作结束后关闭连接。</summary>
    public void Dispose()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_disposed)
                return;
            Volatile.Write(ref _disposed, true);
            _queue?.CompleteAdding();
            worker = _worker;
        }

        if (worker != null && worker != Thread.CurrentThread)
            worker.Join();
    }

    /// <summary>第一次执行时启动唯一的队列线程。</summary>
    void EnsureWorker()
    {
        if (_worker != null)
            return;

        _queue = new BlockingCollection<IWork>(new ConcurrentQueue<IWork>());
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "SqliteStore"
        };
        _worker.Start();
    }

    /// <summary>按入队顺序执行操作。失败后重新打开连接，再继续下一项。</summary>
    void Loop()
    {
        SqliteConnection? connection = null;
        try
        {
            foreach (IWork item in _queue!.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _disposed) || item.Cancellation.IsCancellationRequested)
                {
                    item.Cancel();
                    continue;
                }

                try
                {
                    if (connection == null)
                        connection = OpenConnection();
                    item.Run(connection);
                }
                catch (Exception ex)
                {
                    item.Fail(ex);
                    connection = Reopen(connection);
                }
            }
        }
        finally
        {
            connection?.Dispose();
        }
    }

    /// <summary>关掉失败操作留下的连接，并打开一条新连接。</summary>
    /// <param name="connection">失败时正在使用的连接，可以为空。</param>
    SqliteConnection? Reopen(SqliteConnection? connection)
    {
        connection?.Dispose();
        try
        {
            return OpenConnection();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开队列使用的连接，并设置外键、忙等待和 WAL。</summary>
    SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString(_databasePath!));
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>生成不进入连接池的连接串。</summary>
    /// <param name="fullPath">数据库文件的绝对路径。</param>
    static string ConnectionString(string fullPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        return builder.ToString();
    }

    /// <summary>读取 schema_meta。没有记录时返回 0。</summary>
    /// <param name="connection">已经打开的连接。</param>
    static int ReadVersion(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_meta' LIMIT 1;";
        if (exists.ExecuteScalar() is null)
            return 0;

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT version FROM schema_meta WHERE id = 1;";
        object? value = read.ExecuteScalar();
        if (value is null || value is DBNull)
            return 0;
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>在一个事务里把库迁到当前版本。失败则回滚，不留下半套新表。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="version">迁移前读到的 schema 版本。0 表示还没有版本行。</param>
    static void MigrateToCurrent(SqliteConnection connection, int version)
    {
        using var transaction = connection.BeginTransaction();
        if (version < 1)
            ApplyVersion1(connection, transaction);
        if (version < 2)
            ApplyVersion2(connection, transaction);
        SetVersion(connection, transaction, CurrentVersion);
        transaction.Commit();
    }

    /// <summary>建立版本 1 的账号、玩家和令牌表。不插入账号或玩家。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="transaction">当前迁移事务。失败时由调用方回滚。</param>
    static void ApplyVersion1(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE account (
              id INTEGER PRIMARY KEY,
              username TEXT NOT NULL UNIQUE,
              pwd_hash TEXT NOT NULL,
              salt TEXT NOT NULL,
              created_at INTEGER NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE player (
              id INTEGER PRIMARY KEY,
              account_id INTEGER NOT NULL UNIQUE REFERENCES account(id),
              nickname TEXT NOT NULL,
              level INTEGER NOT NULL DEFAULT 1,
              exp INTEGER NOT NULL DEFAULT 0,
              gold INTEGER NOT NULL DEFAULT 200,
              gem INTEGER NOT NULL DEFAULT 300,
              ticket INTEGER NOT NULL DEFAULT 5,
              shard INTEGER NOT NULL DEFAULT 0,
              bag_slots INTEGER NOT NULL DEFAULT 60,
              guide_step INTEGER NOT NULL DEFAULT 0,
              last_daily_reset INTEGER NOT NULL DEFAULT 0
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE auth_token (
              token_hash TEXT PRIMARY KEY,
              player_id INTEGER NOT NULL REFERENCES player(id),
              expires_at_ms INTEGER NOT NULL,
              revoked INTEGER NOT NULL
            );
            """);
    }

    /// <summary>建立版本 2 的玩家快照表。表是空的，不改已有玩家行。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="transaction">当前迁移事务。</param>
    static void ApplyVersion2(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE player_snapshot (
              player_id INTEGER PRIMARY KEY REFERENCES player(id),
              saved_revision INTEGER NOT NULL,
              body BLOB NOT NULL
            );
            """);
    }

    /// <summary>把 schema_meta 写成当前版本。没有版本表时先建表再插入唯一一行。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="transaction">当前迁移事务。</param>
    /// <param name="version">要写成的 schema 版本。</param>
    static void SetVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        if (!TableExists(connection, transaction, "schema_meta"))
        {
            Execute(connection, transaction, """
                CREATE TABLE schema_meta (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  version INTEGER NOT NULL
                );
                """);
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            INSERT INTO schema_meta (id, version) VALUES (1, $version)
            ON CONFLICT(id) DO UPDATE SET version = excluded.version;
            """;
        update.Parameters.AddWithValue("$version", version);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidDataException("schema_meta 未能写成当前版本");
    }

    /// <summary>在当前事务里判断表是否已经存在。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="transaction">当前迁移事务。</param>
    /// <param name="name">表名。</param>
    static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>在当前事务里执行一条语句。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="transaction">当前迁移事务。</param>
    /// <param name="sql">要执行的 SQL。</param>
    static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    interface IWork
    {
        /// <summary>入队时带上的取消令牌。</summary>
        CancellationToken Cancellation { get; }

        /// <summary>在队列线程上执行。</summary>
        /// <param name="connection">队列当前持有的连接。</param>
        void Run(SqliteConnection connection);

        /// <summary>这项操作不执行，等待任务以取消结束。</summary>
        void Cancel();

        /// <summary>把异常交给等待任务。</summary>
        /// <param name="exception">操作失败时的异常。</param>
        void Fail(Exception exception);
    }

    sealed class WorkItem<T> : IWork
    {
        readonly Func<SqliteConnection, T> _operation;
        readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>记下一次排队的同步操作。</summary>
        /// <param name="operation">在队列线程上执行的操作。</param>
        /// <param name="cancellation">入队时带上的取消令牌。</param>
        public WorkItem(Func<SqliteConnection, T> operation, CancellationToken cancellation)
        {
            _operation = operation;
            Cancellation = cancellation;
        }

        public CancellationToken Cancellation { get; }

        public Task<T> Task => _completion.Task;

        /// <summary>执行操作，并用返回值完成等待任务。</summary>
        /// <param name="connection">队列当前持有的唯一连接。</param>
        public void Run(SqliteConnection connection)
        {
            _completion.TrySetResult(_operation(connection));
        }

        /// <summary>这项操作不执行，等待任务以取消结束。</summary>
        public void Cancel() => _completion.TrySetCanceled();

        /// <summary>把操作抛出的异常交给等待任务。</summary>
        /// <param name="exception">操作或重新打开连接时的异常。</param>
        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
