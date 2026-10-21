using System.Security.Cryptography;
using System.Text;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Account;

/// <summary>
/// 发放、校验和撤销玩家令牌。原文只从发放返回，库里只留 SHA-256 摘要。
/// </summary>
public sealed class TokenService
{
    /// <summary>CSPRNG 输出的字节数。十六进制后长度为 64。</summary>
    const int TokenByteLength = 32;

    readonly SqliteStore _store;

    /// <summary>使用已经初始化的存储。不打开新的数据库线程。</summary>
    /// <param name="store">T29 的唯一队列。为空时抛出异常。</param>
    public TokenService(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 撤销该玩家尚未撤销的摘要，并在同一事务写入新摘要。提交成功后才返回原文。
    /// </summary>
    /// <param name="playerId">必须是已存在玩家的正数编号。非法值不返回令牌。</param>
    /// <param name="expiresAtMs">调用方给定的到期 UTC Unix 毫秒。本方法不改期限。</param>
    /// <param name="ct">入队前取消则不写库，也不返回令牌。</param>
    /// <returns>64 位小写十六进制原文。调用方负责只把它交给客户端。</returns>
    public async Task<string> IssueAsync(long playerId, long expiresAtMs, CancellationToken ct)
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

    /// <summary>
    /// 按持久化摘要校验。到期边界是 now 大于等于库存到期时间。
    /// </summary>
    /// <param name="token">调用方持有的原文。空白或坏格式直接无效，不查库。</param>
    /// <param name="nowUnixMs">调用方给定的当前 UTC Unix 毫秒。测试不必真实等待。</param>
    /// <param name="ct">入队前取消则抛出取消，不返回 204。</param>
    /// <returns>有效时 Code 为 0；无效时 Code 为 204 且 PlayerId 为 0。</returns>
    public Task<TokenValidation> ValidateAsync(string token, long nowUnixMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCanonicalToken(token))
            return Task.FromResult(TokenValidation.Invalid());

        string tokenHash = Digest(token);
        return _store.ExecuteAsync(connection => Judge(connection, tokenHash, nowUnixMs), ct);
    }

    /// <summary>把指定玩家全部未撤销记录标成已撤销。没有可撤销记录时也成功。</summary>
    /// <param name="playerId">要撤销的正数玩家编号。其他玩家不受影响。</param>
    /// <param name="ct">入队前取消则不写库。</param>
    public Task RevokePlayerAsync(long playerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家编号必须为正数");

        return _store.ExecuteAsync(connection =>
        {
            Revoke(connection, transaction: null, playerId);
            return 0;
        }, ct);
    }

    /// <summary>生成 32 字节高熵原文，并编成小写十六进制。</summary>
    static string CreateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return ToHex(bytes);
    }

    /// <summary>计算原文 UTF-8 的 SHA-256，再编成小写十六进制。</summary>
    /// <param name="token">恰好 64 位小写十六进制的原文。</param>
    static string Digest(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return ToHex(hash);
    }

    /// <summary>把字节编成小写十六进制，不附加分隔符。</summary>
    /// <param name="bytes">待编码的字节。</param>
    static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>只接受 64 位小写十六进制。null、空白、大写和其他长度都不是原文格式。</summary>
    /// <param name="token">待检查的文本。</param>
    static bool IsCanonicalToken(string? token)
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

    /// <summary>同一事务先撤销该玩家的旧摘要，再插入新摘要。任一语句失败都回滚。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="playerId">新摘要所属玩家。</param>
    /// <param name="expiresAtMs">新摘要的到期时间。</param>
    /// <param name="tokenHash">新摘要。不是原文。</param>
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

    /// <summary>按摘要读取一行。没有记录时返回无效，不把数据库异常改成 204。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="tokenHash">原文算出的摘要。</param>
    /// <param name="nowUnixMs">调用方传入的当前时间。</param>
    static TokenValidation Judge(SqliteConnection connection, string tokenHash, long nowUnixMs)
    {
        TokenRecord? record = Read(connection, tokenHash);
        if (record == null || record.Revoked || nowUnixMs >= record.ExpiresAtMs || record.PlayerId <= 0)
            return TokenValidation.Invalid();
        return TokenValidation.Valid(record.PlayerId);
    }

    /// <summary>按主键读取摘要行。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="tokenHash">要查找的摘要。</param>
    static TokenRecord? Read(SqliteConnection connection, string tokenHash)
    {
        using var command = connection.CreateCommand();
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

    /// <summary>把该玩家尚未撤销的行全部标成已撤销。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="transaction">发放事务。单独撤销时为空，语句自动提交。</param>
    /// <param name="playerId">要撤销的玩家。</param>
    static void Revoke(SqliteConnection connection, SqliteTransaction? transaction, long playerId)
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

    /// <summary>插入一条未撤销摘要。玩家不存在时由外键失败，事务回滚。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="transaction">当前发放事务。</param>
    /// <param name="playerId">新摘要所属玩家。</param>
    /// <param name="expiresAtMs">新摘要的到期时间。</param>
    /// <param name="tokenHash">新摘要。</param>
    static void Insert(
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

    /// <summary>结束未提交的事务。已经结束时忽略，避免盖住原来的异常。</summary>
    /// <param name="transaction">当前发放事务。</param>
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
