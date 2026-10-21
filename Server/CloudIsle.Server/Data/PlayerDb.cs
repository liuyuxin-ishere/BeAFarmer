using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Data;

public sealed class PlayerRecord
{
    public long PlayerId { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Nickname { get; set; } = "";
    public int Level { get; set; }
    public long Gold { get; set; }
    public string Token { get; set; } = "";
}

/// <summary>
/// PoC #6：最小 SQLite 访问——账号一行，读写 gold。
/// </summary>
public sealed class PlayerDb : IDisposable
{
    readonly SqliteConnection _conn;

    public PlayerDb(string dbPath)
    {
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS player (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              username TEXT NOT NULL UNIQUE,
              password TEXT NOT NULL,
              nickname TEXT NOT NULL,
              level INTEGER NOT NULL DEFAULT 1,
              gold INTEGER NOT NULL DEFAULT 100,
              token TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public PlayerRecord? FindByUsername(string username)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, username, password, nickname, level, gold, token FROM player WHERE username = $u LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return Read(reader);
    }

    public PlayerRecord Create(string username, string password)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO player (username, password, nickname, level, gold, token)
            VALUES ($u, $p, $n, 1, 100, '');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$p", password);
        cmd.Parameters.AddWithValue("$n", username);
        long id = (long)(cmd.ExecuteScalar() ?? 0L);
        return FindById(id) ?? throw new InvalidOperationException("insert player failed");
    }

    public PlayerRecord FindById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, username, password, nickname, level, gold, token FROM player WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"player {id} missing");
        return Read(reader);
    }

    public void UpdateToken(long playerId, string token)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE player SET token = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$id", playerId);
        cmd.ExecuteNonQuery();
    }

    public long AddGold(long playerId, long delta)
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE player SET gold = gold + $d WHERE id = $id;";
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.Parameters.AddWithValue("$id", playerId);
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("add gold failed");
        }

        long gold;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT gold FROM player WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", playerId);
            gold = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        tx.Commit();
        return gold;
    }

    public long SetGold(long playerId, long gold)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE player SET gold = $g WHERE id = $id;";
        cmd.Parameters.AddWithValue("$g", gold);
        cmd.Parameters.AddWithValue("$id", playerId);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("set gold failed");
        return gold;
    }

    static PlayerRecord Read(SqliteDataReader reader) => new()
    {
        PlayerId = reader.GetInt64(0),
        Username = reader.GetString(1),
        Password = reader.GetString(2),
        Nickname = reader.GetString(3),
        Level = reader.GetInt32(4),
        Gold = reader.GetInt64(5),
        Token = reader.GetString(6)
    };

    public void Dispose() => _conn.Dispose();
}
