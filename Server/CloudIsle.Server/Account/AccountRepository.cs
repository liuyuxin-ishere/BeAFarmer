using System.Globalization;
using System.Security.Cryptography;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Account;

/// <summary>
/// 在现有 SQLite 队列里注册账号和玩家，并按库存盐核对密码。不签发 Token，也不绑定会话。
/// </summary>
public sealed class AccountRepository
{
    /// <summary>SQLite SQLITE_CONSTRAINT_UNIQUE。只有 account.username 这一条才映射为 201。</summary>
    const int SqliteConstraintUnique = 2067;

    readonly SqliteStore _store;
    readonly PasswordHasher _hasher;

    /// <summary>使用已经初始化的库和密码派生器。</summary>
    /// <param name="store">提供唯一队列连接的存储。</param>
    /// <param name="hasher">生成和核对派生结果。为空时抛出异常。</param>
    public AccountRepository(SqliteStore store, PasswordHasher hasher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
    }

    /// <summary>同一事务创建账号和玩家。成功返回 0，用户名唯一冲突返回 201。</summary>
    /// <param name="request">05 的注册请求。用户名和昵称原样写入，本项不另造格式规则。</param>
    /// <param name="ct">入队前或轮到执行前取消则不写库。</param>
    /// <returns>成功为 0，用户名已存在为 201。</returns>
    public Task<int> RegisterAsync(RegisterReq request, CancellationToken ct)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        ct.ThrowIfCancellationRequested();

        byte[] salt = RandomNumberGenerator.GetBytes(PasswordHasher.SaltLength);
        string pwdHash = _hasher.Hash(request.Password ?? "", salt);
        string saltText = Convert.ToBase64String(salt);
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string username = request.Username ?? "";
        string nickname = request.Nickname ?? "";
        return _store.ExecuteAsync(
            connection => InsertAccountAndPlayer(connection, username, pwdHash, saltText, createdAt, nickname),
            ct);
    }

    /// <summary>按用户名取出盐和派生结果。密码不对或没有这个用户名时返回 0。</summary>
    /// <param name="username">要查找的用户名，按数据库二进制比较。</param>
    /// <param name="password">待核对的密码原文。</param>
    /// <param name="ct">入队前取消则不查询。</param>
    /// <returns>密码正确时返回 player.id，否则返回 0。</returns>
    public Task<long> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (username == null)
            throw new ArgumentNullException(nameof(username));
        if (password == null)
            throw new ArgumentNullException(nameof(password));

        return _store.ExecuteAsync(connection => Authenticate(connection, username, password), ct);
    }

    /// <summary>插入账号后再插入玩家。玩家业务列留空，让 schema 默认值生效。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="username">请求中的用户名。</param>
    /// <param name="pwdHash">Hash 返回的派生文本。</param>
    /// <param name="saltText">16 字节盐的 Base64。</param>
    /// <param name="createdAt">注册时刻的 UTC Unix 毫秒。</param>
    /// <param name="nickname">请求中的昵称。非法昵称的业务码不在本项处理。</param>
    static int InsertAccountAndPlayer(
        SqliteConnection connection,
        string username,
        string pwdHash,
        string saltText,
        long createdAt,
        string nickname)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var account = connection.CreateCommand())
            {
                account.Transaction = transaction;
                account.CommandText = """
                    INSERT INTO account (username, pwd_hash, salt, created_at)
                    VALUES ($username, $pwdHash, $salt, $createdAt);
                    """;
                account.Parameters.AddWithValue("$username", username);
                account.Parameters.AddWithValue("$pwdHash", pwdHash);
                account.Parameters.AddWithValue("$salt", saltText);
                account.Parameters.AddWithValue("$createdAt", createdAt);
                account.ExecuteNonQuery();
            }

            long accountId;
            using (var id = connection.CreateCommand())
            {
                id.Transaction = transaction;
                id.CommandText = "SELECT last_insert_rowid();";
                accountId = Convert.ToInt64(id.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var player = connection.CreateCommand())
            {
                player.Transaction = transaction;
                player.CommandText = """
                    INSERT INTO player (account_id, nickname)
                    VALUES ($accountId, $nickname);
                    """;
                player.Parameters.AddWithValue("$accountId", accountId);
                player.Parameters.AddWithValue("$nickname", nickname);
                player.ExecuteNonQuery();
            }

            transaction.Commit();
            return 0;
        }
        catch (SqliteException ex)
        {
            Rollback(transaction);
            if (IsUsernameConflict(ex))
                return 201;
            throw;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    /// <summary>先查账号，再查玩家。数据库读写失败继续抛出，不改写成 0。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="username">要查找的用户名。</param>
    /// <param name="password">待核对的密码原文。</param>
    long Authenticate(SqliteConnection connection, string username, string password)
    {
        using var account = connection.CreateCommand();
        account.CommandText = """
            SELECT id, pwd_hash, salt
            FROM account
            WHERE username = $username;
            """;
        account.Parameters.AddWithValue("$username", username);
        using var reader = account.ExecuteReader();
        if (!reader.Read())
            return 0;

        long accountId = reader.GetInt64(0);
        if (reader.IsDBNull(1) || reader.IsDBNull(2))
            throw new InvalidDataException("账号缺少派生结果或盐");
        string pwdHash = reader.GetString(1);
        string saltText = reader.GetString(2);
        if (reader.Read())
            throw new InvalidDataException("同一用户名读到多行账号");
        reader.Close();

        if (!TryDecodeSalt(saltText, out byte[] salt))
            throw new InvalidDataException("账号盐无法按 16 字节解码");

        if (!_hasher.Verify(password, salt, pwdHash))
            return 0;

        using var player = connection.CreateCommand();
        player.CommandText = "SELECT id FROM player WHERE account_id = $accountId;";
        player.Parameters.AddWithValue("$accountId", accountId);
        object? playerId = player.ExecuteScalar();
        if (playerId == null || playerId is DBNull)
            throw new InvalidDataException("账号没有对应玩家");
        return Convert.ToInt64(playerId, CultureInfo.InvariantCulture);
    }

    /// <summary>判断是不是 account.username 的唯一约束。其他约束保持原异常。</summary>
    /// <param name="exception">SQLite 抛出的异常。</param>
    static bool IsUsernameConflict(SqliteException exception)
    {
        if (exception.SqliteExtendedErrorCode != SqliteConstraintUnique)
            return false;
        string message = exception.Message ?? "";
        return message.Contains("account.username", StringComparison.Ordinal)
            && !message.Contains("player.", StringComparison.Ordinal);
    }

    /// <summary>把盐文本解成恰好 16 字节。失败时不把损坏数据拿去派生。</summary>
    /// <param name="saltText">account.salt。</param>
    /// <param name="salt">解码后的盐。</param>
    static bool TryDecodeSalt(string saltText, out byte[] salt)
    {
        salt = Array.Empty<byte>();
        if (string.IsNullOrEmpty(saltText))
            return false;
        byte[] buffer = new byte[PasswordHasher.SaltLength];
        if (!Convert.TryFromBase64String(saltText, buffer, out int written) || written != PasswordHasher.SaltLength)
            return false;
        salt = buffer;
        return true;
    }

    /// <summary>结束未提交的事务。已经结束时忽略，避免盖住原来的异常。</summary>
    /// <param name="transaction">当前注册事务。</param>
    static void Rollback(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch (SqliteException)
        {
        }
    }
}
