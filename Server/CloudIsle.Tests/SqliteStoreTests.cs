using System.Globalization;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

public sealed class SqliteStoreTests
{
    [Test]
    public void Initialize_CreatesSchemaOnNewDatabase()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "nested", "game.db");

        new SqliteStore().Initialize(database);

        Assert.That(File.Exists(database), Is.True);
        using var connection = Open(database);
        Assert.That(Tables(connection), Is.EquivalentTo(new[] { "account", "auth_token", "player", "player_snapshot", "schema_meta" }));
        Assert.That(Version(connection), Is.EqualTo(SqliteStore.CurrentVersion));
        Assert.That(Count(connection, "account"), Is.EqualTo(0));
        Assert.That(Count(connection, "player"), Is.EqualTo(0));
        Assert.That(Count(connection, "schema_meta"), Is.EqualTo(1));
        Assert.That(UniqueColumns(connection, "account"), Does.Contain("username"));
        Assert.That(UniqueColumns(connection, "player"), Does.Contain("account_id"));
        Assert.That(ForeignKey(connection, "player", "account_id"), Is.EqualTo("account"));
        Assert.That(ForeignKey(connection, "auth_token", "player_id"), Is.EqualTo("player"));
        Assert.That(PrimaryKey(connection, "auth_token"), Is.EqualTo("token_hash"));
        Assert.That(DefaultValue(connection, "player", "level"), Is.EqualTo("1"));
        Assert.That(DefaultValue(connection, "player", "gold"), Is.EqualTo("200"));
        Assert.That(DefaultValue(connection, "player", "gem"), Is.EqualTo("300"));
        Assert.That(DefaultValue(connection, "player", "ticket"), Is.EqualTo("5"));
        Assert.That(DefaultValue(connection, "player", "bag_slots"), Is.EqualTo("60"));
        Assert.That(JournalMode(connection), Is.EqualTo("wal"));

        Execute(connection, "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (1, 'ada', 'hash', 'salt', 10);");
        Execute(connection, "INSERT INTO player (id, account_id, nickname) VALUES (7, 1, 'Ada');");
        Assert.That(ScalarLong(connection, "SELECT gold FROM player WHERE id = 7;"), Is.EqualTo(200));
        Assert.That(ScalarLong(connection, "SELECT bag_slots FROM player WHERE id = 7;"), Is.EqualTo(60));
    }

    [Test]
    public void Initialize_Again_KeepsExistingAccount()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        var store = new SqliteStore();
        store.Initialize(database);

        using (var connection = Open(database))
        {
            Execute(connection, "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (3, 'bea', 'hash', 'salt', 20);");
        }

        store.Initialize(database);

        using var again = Open(database);
        Assert.That(Version(again), Is.EqualTo(SqliteStore.CurrentVersion));
        Assert.That(Count(again, "schema_meta"), Is.EqualTo(1));
        Assert.That(Count(again, "account"), Is.EqualTo(1));
        Assert.That(ScalarString(again, "SELECT username FROM account WHERE id = 3;"), Is.EqualTo("bea"));
    }

    [Test]
    public void DuplicateUsername_IsRejected()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        new SqliteStore().Initialize(database);

        using var connection = Open(database);
        Execute(connection, "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (1, 'cyd', 'hash', 'salt', 30);");
        SqliteException? error = Assert.Throws<SqliteException>(() =>
            Execute(connection, "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (2, 'cyd', 'hash', 'salt', 31);"));
        Assert.That(error!.SqliteErrorCode, Is.EqualTo(19));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));

        var gate = new Barrier(2);
        int successes = 0;
        int failures = 0;
        Action insert = () =>
        {
            using var other = Open(database);
            gate.SignalAndWait();
            try
            {
                Execute(other, "INSERT INTO account (username, pwd_hash, salt, created_at) VALUES ('duo', 'hash', 'salt', 32);");
                Interlocked.Increment(ref successes);
            }
            catch (SqliteException)
            {
                Interlocked.Increment(ref failures);
            }
        };
        Parallel.Invoke(insert, insert);
        Assert.That(successes, Is.EqualTo(1));
        Assert.That(failures, Is.EqualTo(1));
        Assert.That(Count(connection, "account"), Is.EqualTo(2));
    }

    [Test]
    public void OldVersion_UpgradesAndKeepsExistingRow()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using (var connection = Open(database))
        {
            Execute(connection, """
                CREATE TABLE schema_meta (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  version INTEGER NOT NULL
                );
                """);
            Execute(connection, "INSERT INTO schema_meta (id, version) VALUES (1, 0);");
            Execute(connection, "CREATE TABLE legacy_note (id INTEGER PRIMARY KEY, note TEXT NOT NULL);");
            Execute(connection, "INSERT INTO legacy_note (id, note) VALUES (1, 'keep');");
        }

        new SqliteStore().Initialize(database);

        using var again = Open(database);
        Assert.That(Version(again), Is.EqualTo(SqliteStore.CurrentVersion));
        Assert.That(ScalarString(again, "SELECT note FROM legacy_note WHERE id = 1;"), Is.EqualTo("keep"));
        Assert.That(TableExists(again, "account"), Is.True);
        Assert.That(TableExists(again, "player"), Is.True);
        Assert.That(TableExists(again, "auth_token"), Is.True);
        Assert.That(TableExists(again, "player_snapshot"), Is.True);
    }

    [Test]
    public void FutureVersion_IsRejectedWithoutChanges()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using (var connection = Open(database))
        {
            Execute(connection, """
                CREATE TABLE schema_meta (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  version INTEGER NOT NULL
                );
                """);
            Execute(connection, "INSERT INTO schema_meta (id, version) VALUES (1, 9);");
            Execute(connection, "CREATE TABLE marker (id INTEGER PRIMARY KEY, note TEXT NOT NULL);");
            Execute(connection, "INSERT INTO marker (id, note) VALUES (1, 'untouched');");
        }

        Assert.Throws<InvalidOperationException>(() => new SqliteStore().Initialize(database));

        using var again = Open(database);
        Assert.That(Version(again), Is.EqualTo(9));
        Assert.That(ScalarString(again, "SELECT note FROM marker WHERE id = 1;"), Is.EqualTo("untouched"));
        Assert.That(TableExists(again, "account"), Is.False);
        Assert.That(TableExists(again, "player"), Is.False);
        Assert.That(TableExists(again, "auth_token"), Is.False);
        Assert.That(TableExists(again, "player_snapshot"), Is.False);
    }

    [Test]
    public void FailedMigration_RollsBackNewTables()
    {
        using var dir = new IsolatedDirectory();
        string database = Path.Combine(dir.FullPath, "game.db");
        using (var connection = Open(database))
        {
            Execute(connection, "CREATE TABLE player (id INTEGER PRIMARY KEY, note TEXT NOT NULL);");
            Execute(connection, "INSERT INTO player (id, note) VALUES (1, 'old');");
        }

        Assert.Throws<SqliteException>(() => new SqliteStore().Initialize(database));

        using var again = Open(database);
        Assert.That(TableExists(again, "schema_meta"), Is.False);
        Assert.That(TableExists(again, "account"), Is.False);
        Assert.That(TableExists(again, "auth_token"), Is.False);
        Assert.That(TableExists(again, "player_snapshot"), Is.False);
        Assert.That(ScalarString(again, "SELECT note FROM player WHERE id = 1;"), Is.EqualTo("old"));
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
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
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
        return ScalarLong(connection, string.Format(CultureInfo.InvariantCulture, "SELECT COUNT(*) FROM {0};", table));
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

    static int Version(SqliteConnection connection)
    {
        return (int)ScalarLong(connection, "SELECT version FROM schema_meta WHERE id = 1;");
    }

    static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    static string[] Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    static List<string> UniqueColumns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var indexes = connection.CreateCommand();
        indexes.CommandText = "SELECT name FROM pragma_index_list($table) WHERE \"unique\" = 1;";
        indexes.Parameters.AddWithValue("$table", table);
        using var reader = indexes.ExecuteReader();
        var indexNames = new List<string>();
        while (reader.Read())
            indexNames.Add(reader.GetString(0));
        reader.Close();

        foreach (string indexName in indexNames)
        {
            using var info = connection.CreateCommand();
            info.CommandText = "SELECT name FROM pragma_index_info($index);";
            info.Parameters.AddWithValue("$index", indexName);
            using var infoReader = info.ExecuteReader();
            while (infoReader.Read())
                columns.Add(infoReader.GetString(0));
        }

        return columns;
    }

    static string ForeignKey(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"table\" FROM pragma_foreign_key_list($table) WHERE \"from\" = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    static string PrimaryKey(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) WHERE pk > 0;";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    static string DefaultValue(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dflt_value FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    static string JournalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingSqliteStoreTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingSqliteStoreTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
