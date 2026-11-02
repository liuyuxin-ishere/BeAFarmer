using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Account;

/// <summary>
/// Token+scope 唯一持久事务拥有者。禁止与 TokenService.IssueLegacyUnassociatedAsync 另开事务拼接。
/// </summary>
public sealed class AccountSessionStore
{
    readonly SqliteStore _store;

    /// <summary>测试钩子：在撤销旧 Token 之后、写入新 Token 之前注入故障。</summary>
    internal Action? FaultAfterRevokeBeforeInsert { get; set; }

    /// <summary>测试钩子：在关闭旧 scope 之后、创建新 scope 之前注入故障。</summary>
    internal Action? FaultAfterCloseScopesBeforeNew { get; set; }

    /// <summary>测试钩子：在写入关联之前注入故障。</summary>
    internal Action? FaultBeforeAssociate { get; set; }

    public AccountSessionStore(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 单事务：撤旧 Token、关旧 open scope、新建 scope(next_op=1)、新 TokenHash、持久关联。
    /// 明文 Token 不入 SQL。
    /// </summary>
    public Task<(string Token, long ScopeId)> RotateLoginAsync(
        long playerId,
        long expiresAtMs,
        long nowMs,
        CancellationToken ct)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        ct.ThrowIfCancellationRequested();

        (string token, string hash) = TokenService.CreateMaterial();
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            try
            {
                TokenService.Revoke(connection, tx, playerId);
                FaultAfterRevokeBeforeInsert?.Invoke();

                CloseOpenScopes(connection, tx, playerId, nowMs);
                FaultAfterCloseScopesBeforeNew?.Invoke();

                long scopeId = InsertScope(connection, tx, playerId, nowMs);
                TokenService.Insert(connection, tx, playerId, expiresAtMs, hash);
                FaultBeforeAssociate?.Invoke();
                Associate(connection, tx, hash, playerId, scopeId);
                tx.Commit();
                return (token, scopeId);
            }
            catch
            {
                try { tx.Rollback(); } catch (SqliteException) { }
                throw;
            }
        }, ct);
    }

    /// <summary>兼容包装：仅返回 scopeId；权威 next 请用 <see cref="RestoreScopeDetailedAsync"/>。</summary>
    public async Task<long> RestoreScopeAsync(string token, long expectedPlayerId, long nowMs, CancellationToken ct)
    {
        ScopeRestoreResult r = await RestoreScopeDetailedAsync(token, expectedPlayerId, nowMs, ct)
            .ConfigureAwait(false);
        return r.ScopeId;
    }

    /// <summary>
    /// lane 内同事务：重验 TokenHash；恢复关联 scope（含实际 next_op），或对合格 legacy 一次 CAS 补 scope（next=1）。
    /// 存储失败向上抛出；仅 UNIQUE 冲突走 CAS 回读且须再验 Token+open owned scope。
    /// </summary>
    public Task<ScopeRestoreResult> RestoreScopeDetailedAsync(
        string token, long expectedPlayerId, long nowMs, CancellationToken ct)
    {
        if (expectedPlayerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedPlayerId));
        ct.ThrowIfCancellationRequested();
        if (!TokenService.IsCanonicalToken(token))
            return Task.FromResult(ScopeRestoreResult.None);

        string hash = TokenService.HashToken(token);
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            try
            {
                TokenValidation again = TokenService.Judge(connection, tx, hash, nowMs);
                if (again.Code != TokenValidation.ValidCode || again.PlayerId != expectedPlayerId)
                {
                    tx.Commit();
                    return ScopeRestoreResult.None;
                }

                long? linked = ReadLinkedScope(connection, tx, hash, expectedPlayerId);
                if (linked is long existing)
                {
                    if (!TryReadOpenScopeNext(connection, tx, existing, expectedPlayerId, out long nextOp))
                    {
                        tx.Commit();
                        return ScopeRestoreResult.None;
                    }

                    tx.Commit();
                    return new ScopeRestoreResult(existing, nextOp);
                }

                // legacy：无关联 → 一次补 scope（next_op=1）
                long scopeId = InsertScope(connection, tx, expectedPlayerId, nowMs);
                try
                {
                    Associate(connection, tx, hash, expectedPlayerId, scopeId);
                    tx.Commit();
                    return new ScopeRestoreResult(scopeId, 1);
                }
                catch (SqliteException ex) when (IsUniqueConstraint(ex))
                {
                    try { tx.Rollback(); } catch (SqliteException) { }
                    using var tx2 = connection.BeginTransaction();
                    TokenValidation raced = TokenService.Judge(connection, tx2, hash, nowMs);
                    if (raced.Code != TokenValidation.ValidCode || raced.PlayerId != expectedPlayerId)
                    {
                        tx2.Commit();
                        return ScopeRestoreResult.None;
                    }

                    long? other = ReadLinkedScope(connection, tx2, hash, expectedPlayerId);
                    if (other is long sid
                        && TryReadOpenScopeNext(connection, tx2, sid, expectedPlayerId, out long next2))
                    {
                        tx2.Commit();
                        return new ScopeRestoreResult(sid, next2);
                    }

                    tx2.Commit();
                    return ScopeRestoreResult.None;
                }
            }
            catch
            {
                try { tx.Rollback(); } catch (SqliteException) { }
                throw;
            }
        }, ct);
    }

    /// <summary>登出：同事务撤销 Token 并关闭该玩家全部 open scope。</summary>
    public Task RevokeAndCloseScopesAsync(long playerId, long nowMs, CancellationToken ct)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            try
            {
                TokenService.Revoke(connection, tx, playerId);
                CloseOpenScopes(connection, tx, playerId, nowMs);
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

    public Task<long?> TryGetLinkedScopeAsync(string token, CancellationToken ct)
    {
        if (!TokenService.IsCanonicalToken(token))
            return Task.FromResult<long?>(null);
        string hash = TokenService.HashToken(token);
        return _store.ExecuteAsync(connection => ReadLinkedScope(connection, null, hash, playerId: null), ct);
    }

    /// <summary>供 TokenService.RevokePlayerAsync 在同一事务内关闭 open scope（不嵌套事务）。</summary>
    internal static void CloseOpenScopes(
        SqliteConnection connection,
        SqliteTransaction? tx,
        long playerId,
        long nowMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE command_scope
            SET closed = 1
            WHERE player_id = $playerId AND closed = 0;
            """;
        command.Parameters.AddWithValue("$playerId", playerId);
        command.ExecuteNonQuery();
        _ = nowMs;
    }

    /// <summary>
    /// 仅 UNIQUE 冲突可走 CAS 回读。ABORT/触发器等其它 CONSTRAINT 必须向上抛出。
    /// SQLITE_CONSTRAINT_UNIQUE = 2067。
    /// </summary>
    static bool IsUniqueConstraint(SqliteException ex)
    {
        if (ex.SqliteExtendedErrorCode == 2067)
            return true;
        string msg = ex.Message ?? "";
        return msg.IndexOf("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static long InsertScope(SqliteConnection connection, SqliteTransaction tx, long playerId, long nowMs)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO command_scope (player_id, next_op_id, closed, created_at_ms)
                VALUES ($playerId, 1, 0, $now);
                """;
            command.Parameters.AddWithValue("$playerId", playerId);
            command.Parameters.AddWithValue("$now", nowMs);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException("command_scope 未写入");
        }

        using var idCmd = connection.CreateCommand();
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT last_insert_rowid();";
        long scopeId = Convert.ToInt64(idCmd.ExecuteScalar());
        if (scopeId <= 0)
            throw new InvalidDataException("command_scope 未分配 scope_id");
        return scopeId;
    }

    static void Associate(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tokenHash,
        long playerId,
        long scopeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO auth_token_scope (token_hash, player_id, scope_id)
            VALUES ($hash, $playerId, $scopeId);
            """;
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$playerId", playerId);
        command.Parameters.AddWithValue("$scopeId", scopeId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("auth_token_scope 未写入");
    }

    static long? ReadLinkedScope(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string tokenHash,
        long? playerId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        if (playerId is long pid)
        {
            command.CommandText = """
                SELECT scope_id FROM auth_token_scope
                WHERE token_hash = $hash AND player_id = $playerId;
                """;
            command.Parameters.AddWithValue("$playerId", pid);
        }
        else
        {
            command.CommandText = """
                SELECT scope_id FROM auth_token_scope
                WHERE token_hash = $hash;
                """;
        }

        command.Parameters.AddWithValue("$hash", tokenHash);
        object? value = command.ExecuteScalar();
        if (value is null || value is DBNull)
            return null;
        return Convert.ToInt64(value);
    }

    static bool IsOpenScopeOwned(
        SqliteConnection connection,
        SqliteTransaction tx,
        long scopeId,
        long playerId)
        => TryReadOpenScopeNext(connection, tx, scopeId, playerId, out _);

    static bool TryReadOpenScopeNext(
        SqliteConnection connection,
        SqliteTransaction tx,
        long scopeId,
        long playerId,
        out long nextOpId)
    {
        nextOpId = 0;
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT next_op_id, closed FROM command_scope
            WHERE scope_id = $scopeId AND player_id = $playerId;
            """;
        command.Parameters.AddWithValue("$scopeId", scopeId);
        command.Parameters.AddWithValue("$playerId", playerId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;
        nextOpId = reader.GetInt64(0);
        return reader.GetInt64(1) == 0 && nextOpId >= 1;
    }
}

/// <summary>重连恢复的 scope 与权威 next_op；ScopeId=0 表示失败。</summary>
public readonly struct ScopeRestoreResult
{
    public static ScopeRestoreResult None => default;

    public ScopeRestoreResult(long scopeId, long nextOpId)
    {
        ScopeId = scopeId;
        NextOpId = nextOpId;
    }

    public long ScopeId { get; }
    public long NextOpId { get; }
}
