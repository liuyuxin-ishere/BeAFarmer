using System.Security.Cryptography;
using System.Text;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Account;

/// <summary>
/// 发放、校验和撤销玩家令牌。原文只从发放返回，库里只留 SHA-256 摘要。
/// 正式登录/轮换由 <see cref="AccountSessionStore"/> 拥有外层事务，调用本类 SQL 帮助方法。
/// 决策：不提供公开「只发 Token、不管 scope」旁路；legacy 无关联发放仅 internal 迁移夹具。
/// </summary>
public sealed class TokenService
{
    /// <summary>CSPRNG 输出的字节数。十六进制后长度为 64。</summary>
    public const int TokenByteLength = 32;

    readonly SqliteStore _store;

    public TokenService(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>与本服务共享的唯一库；供 AccountSessionStore 同连接编排。</summary>
    internal SqliteStore Store => _store;

    /// <summary>
    /// 迁移/测试夹具专用：撤销旧 Token 并写入<strong>无</strong> scope 关联的新摘要（schema3 legacy 模拟）。
    /// 正式路径必须用 <see cref="AccountSessionStore.RotateLoginAsync"/>。InternalsVisibleTo 测试程序集可调用。
    /// </summary>
    internal async Task<string> IssueLegacyUnassociatedAsync(long playerId, long expiresAtMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家编号必须为正数");

        string token = CreateToken();
        string tokenHash = Digest(token);
        await _store.ExecuteAsync(connection =>
        {
            Replace(connection, playerId, expiresAtMs, tokenHash);
            return 0;
        }, ct).ConfigureAwait(false);
        return token;
    }

    public Task<TokenValidation> ValidateAsync(string token, long nowUnixMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCanonicalToken(token))
            return Task.FromResult(TokenValidation.Invalid());

        string tokenHash = Digest(token);
        return _store.ExecuteAsync(connection => Judge(connection, null, tokenHash, nowUnixMs), ct);
    }

    /// <summary>
    /// 同事务撤销全部未撤销 Token 并关闭 open scope（与 Logout 语义一致）。
    /// 内部 <see cref="Revoke"/> 仍可被 Store 在已有事务内调用，不另开事务。
    /// </summary>
    public Task RevokePlayerAsync(long playerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家编号必须为正数");

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            try
            {
                Revoke(connection, tx, playerId);
                AccountSessionStore.CloseOpenScopes(connection, tx, playerId, nowMs);
                tx.Commit();
                return 0;
            }
            catch
            {
                try { tx.Rollback(); } catch (SqliteException) { }
                throw;
            }
        }, ct);
    }

    /// <summary>生成原文+摘要对；不写库。</summary>
    public static (string Token, string TokenHash) CreateMaterial()
    {
        string token = CreateToken();
        return (token, Digest(token));
    }

    public static string HashToken(string token) => Digest(token);

    public static bool IsCanonicalToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != TokenByteLength * 2)
            return false;

        foreach (char character in token)
        {
            bool hex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex)
                return false;
        }

        return true;
    }

    /// <summary>供 AccountSessionStore 在已有事务内撤销旧 Token。</summary>
    internal static void Revoke(SqliteConnection connection, SqliteTransaction? transaction, long playerId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE auth_token
            SET revoked = 1
            WHERE player_id = $playerId AND revoked = 0;
            """;
        command.Parameters.AddWithValue("$playerId", playerId);
        command.ExecuteNonQuery();
    }

    /// <summary>供 AccountSessionStore 在已有事务内插入新摘要（不自开事务）。</summary>
    internal static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long playerId,
        long expiresAtMs,
        string tokenHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO auth_token (token_hash, player_id, expires_at_ms, revoked)
            VALUES ($tokenHash, $playerId, $expiresAtMs, 0);
            """;
        command.Parameters.AddWithValue("$tokenHash", tokenHash);
        command.Parameters.AddWithValue("$playerId", playerId);
        command.Parameters.AddWithValue("$expiresAtMs", expiresAtMs);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("新令牌摘要没有写入一行");
    }

    /// <summary>供外层事务重验。</summary>
    internal static TokenValidation Judge(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tokenHash,
        long nowUnixMs)
    {
        TokenRecord? record = Read(connection, transaction, tokenHash);
        if (record == null || record.Revoked || nowUnixMs >= record.ExpiresAtMs || record.PlayerId <= 0)
            return TokenValidation.Invalid();
        return TokenValidation.Valid(record.PlayerId);
    }

    internal static TokenRecord? Read(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tokenHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT player_id, expires_at_ms, revoked
            FROM auth_token
            WHERE token_hash = $tokenHash;
            """;
        command.Parameters.AddWithValue("$tokenHash", tokenHash);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        long playerId = reader.GetInt64(0);
        long expiresAtMs = reader.GetInt64(1);
        bool revoked = reader.GetInt64(2) != 0;
        if (reader.Read())
            throw new InvalidDataException("同一令牌摘要读到多行");
        return new TokenRecord(tokenHash, playerId, expiresAtMs, revoked);
    }

    static void Replace(SqliteConnection connection, long playerId, long expiresAtMs, string tokenHash)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            Revoke(connection, transaction, playerId);
            Insert(connection, transaction, playerId, expiresAtMs, tokenHash);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    static string CreateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return ToHex(bytes);
    }

    static string Digest(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return ToHex(hash);
    }

    static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

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
