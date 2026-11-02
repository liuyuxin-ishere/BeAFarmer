using System;
using System.Collections.Generic;
using CloudIsle.Domain.Commands;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Commands;

/// <summary>
/// 只读回执/高水位查询。复用唯一 <see cref="SqliteStore"/> 队列；不另开连接/线程/锁。
/// Lookup/Query 在单次 deferred 读事务内读 scope+行，避免跨自动提交拼旧 next 与新回执。
/// L1 至多 64：权威行元数据一致才复用缓存载荷；不权威缓存 NotRecorded。
/// 正式原子提交属 T011；本类无伪造写入口。
/// </summary>
public sealed class CommandReceiptRepository
{
    /// <summary>详情逻辑过期：now - committed_at &gt;= RetentionMs。</summary>
    public static readonly long DefaultRetentionMs = 7L * 24 * 60 * 60 * 1000;

    public const int L1Capacity = 64;

    readonly SqliteStore _store;
    readonly long _retentionMs;
    readonly object _l1Gate = new();
    readonly LinkedList<L1Entry> _l1Order = new();
    readonly Dictionary<string, LinkedListNode<L1Entry>> _l1Map = new(StringComparer.Ordinal);
    int _payloadLoadCount;

    /// <summary>测试观测：实际从 SQLite 物化 BLOB 的次数，不包含权威元数据读取。</summary>
    internal int PayloadLoadCount => Volatile.Read(ref _payloadLoadCount);

    /// <param name="retentionMs">-1=默认 7 天；其它负数拒绝；正数自定义窗。</param>
    public CommandReceiptRepository(SqliteStore store, long retentionMs = -1)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (retentionMs == -1)
            _retentionMs = DefaultRetentionMs;
        else if (retentionMs < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionMs), "use -1 for default or a non-negative window");
        else
            _retentionMs = retentionMs;
    }

    /// <summary>
    /// 幂等查账。nowMs 由调用方注入（时钟属 T016）。
    /// 冲突优先于过期行仍可读时的细节：若行存在且 hash 冲突 → 112；若仅时间过期 → 111。
    /// </summary>
    public Task<ReceiptLookup> LookupAsync(CommandKey key, CommandHash hash, long nowMs, CancellationToken ct)
    {
        if (nowMs < 0)
            throw new ArgumentOutOfRangeException(nameof(nowMs));
        ct.ThrowIfCancellationRequested();
        return _store.ExecuteAsync(connection => LookupCore(connection, key, hash, nowMs), ct);
    }

    /// <summary>只读查询；不执行 Domain/RNG。外 scope→110；过期→111；NotRecorded（含未来 op）→code 0。</summary>
    public Task<ReceiptQuery> QueryAsync(long playerId, long scopeId, long opId, long nowMs, CancellationToken ct)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (scopeId <= 0) throw new ArgumentOutOfRangeException(nameof(scopeId));
        if (opId <= 0) throw new ArgumentOutOfRangeException(nameof(opId));
        if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs));
        ct.ThrowIfCancellationRequested();
        var key = new CommandKey(playerId, scopeId, opId);
        return _store.ExecuteAsync(connection => QueryCore(connection, key, nowMs), ct);
    }

    ReceiptLookup LookupCore(SqliteConnection connection, CommandKey key, CommandHash hash, long nowMs)
    {
        // 显式 DEFERRED：首次读建立 WAL 快照，保持只读且允许其它连接在 WAL 中提交。
        using var tx = connection.BeginTransaction(deferred: true);
        try
        {
            if (!TryReadScope(connection, tx, key.PlayerId, key.ScopeId, out long nextOp, out bool open))
            {
                tx.Commit();
                return new ReceiptLookup(ReceiptLookupKind.ScopeInvalid, 0, false, null);
            }

            if (key.OpId > nextOp)
            {
                tx.Commit();
                return new ReceiptLookup(ReceiptLookupKind.OpGap, nextOp, open, null);
            }

            ReceiptMetadata? row = TryReadReceiptMetadata(connection, tx, key);
            if (row == null)
            {
                tx.Commit();
                if (key.OpId < nextOp)
                    return new ReceiptLookup(ReceiptLookupKind.Expired, nextOp, open, null);
                return new ReceiptLookup(ReceiptLookupKind.NotRecorded, nextOp, open, null);
            }

            if (!string.Equals(row.RequestHash.Hex, hash.Hex, StringComparison.Ordinal))
            {
                tx.Commit();
                return new ReceiptLookup(ReceiptLookupKind.PayloadConflict, nextOp, open, null);
            }

            if (IsDetailExpired(row.CommittedAtMs, nowMs))
            {
                tx.Commit();
                return new ReceiptLookup(ReceiptLookupKind.Expired, nextOp, open, null);
            }

            ReceiptDetails details = ResolveFromL1OrStore(connection, tx, key, row);
            tx.Commit();
            return new ReceiptLookup(ReceiptLookupKind.RecordedMatched, nextOp, open, details);
        }
        catch
        {
            try { tx.Rollback(); } catch (SqliteException) { }
            throw;
        }
    }

    ReceiptQuery QueryCore(SqliteConnection connection, CommandKey key, long nowMs)
    {
        using var tx = connection.BeginTransaction(deferred: true);
        try
        {
            if (!TryReadScope(connection, tx, key.PlayerId, key.ScopeId, out long nextOp, out bool open))
            {
                tx.Commit();
                return new ReceiptQuery(ReceiptQueryKind.ScopeInvalid, 110, 0, false, null);
            }

            // 纯查询：未来 op 视为 NotRecorded（非写路径 OpGap/NotAccepted）
            if (key.OpId > nextOp)
            {
                tx.Commit();
                return new ReceiptQuery(ReceiptQueryKind.NotRecorded, 0, nextOp, open, null);
            }

            ReceiptMetadata? row = TryReadReceiptMetadata(connection, tx, key);
            if (row == null)
            {
                tx.Commit();
                if (key.OpId < nextOp)
                    return new ReceiptQuery(ReceiptQueryKind.Expired, 111, nextOp, open, null);
                return new ReceiptQuery(ReceiptQueryKind.NotRecorded, 0, nextOp, open, null);
            }

            if (IsDetailExpired(row.CommittedAtMs, nowMs))
            {
                tx.Commit();
                return new ReceiptQuery(ReceiptQueryKind.Expired, 111, nextOp, open, null);
            }

            ReceiptDetails details = ResolveFromL1OrStore(connection, tx, key, row);
            tx.Commit();
            return new ReceiptQuery(ReceiptQueryKind.Recorded, 0, nextOp, open, details);
        }
        catch
        {
            try { tx.Rollback(); } catch (SqliteException) { }
            throw;
        }
    }

    /// <summary>非负差：now &gt;= at 且 now-at &gt;= window → 过期。不做 at+window 加法溢出。</summary>
    bool IsDetailExpired(long committedAtMs, long nowMs)
    {
        if (nowMs < committedAtMs)
            return false;
        return nowMs - committedAtMs >= _retentionMs;
    }

    /// <summary>
    /// 同一读快照内先查权威元数据，命中时直接复用不可变载荷；未命中才读取 BLOB。
    /// </summary>
    ReceiptDetails ResolveFromL1OrStore(
        SqliteConnection connection, SqliteTransaction tx, CommandKey key, ReceiptMetadata metadata)
    {
        string mapKey = L1Key(key);
        lock (_l1Gate)
        {
            if (_l1Map.TryGetValue(mapKey, out LinkedListNode<L1Entry>? existing))
            {
                if (metadata.Matches(existing.Value.Details))
                {
                    _l1Order.Remove(existing);
                    _l1Order.AddFirst(existing);
                    return existing.Value.Details;
                }

                _l1Order.Remove(existing);
                _l1Map.Remove(mapKey);
            }
        }

        // 事务仍持有同一 WAL 快照；元数据与载荷不能来自不同提交。
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT result_blob FROM command_receipt WHERE player_id=$p AND scope_id=$s AND op_id=$o;";
        command.Parameters.AddWithValue("$p", key.PlayerId);
        command.Parameters.AddWithValue("$s", key.ScopeId);
        command.Parameters.AddWithValue("$o", key.OpId);
        if (command.ExecuteScalar() is not byte[] blob || blob.Length != metadata.BlobLength)
            throw new InvalidDataException("回执元数据与载荷不一致");
        Interlocked.Increment(ref _payloadLoadCount);
        var details = new ReceiptDetails(metadata.Code, metadata.CommittedRevision,
            metadata.ConfigVersion, blob, metadata.CommittedAtMs, metadata.RequestHash);

        lock (_l1Gate)
        {
            var entry = new L1Entry(key, details);
            LinkedListNode<L1Entry> node = _l1Order.AddFirst(entry);
            _l1Map[mapKey] = node;
            while (_l1Map.Count > L1Capacity)
            {
                LinkedListNode<L1Entry>? last = _l1Order.Last;
                if (last == null) break;
                _l1Order.RemoveLast();
                _l1Map.Remove(L1Key(last.Value.Key));
            }
        }
        return details;
    }

    static string L1Key(CommandKey key) => key.PlayerId + ":" + key.ScopeId + ":" + key.OpId;

    static bool TryReadScope(
        SqliteConnection connection,
        SqliteTransaction tx,
        long playerId,
        long scopeId,
        out long nextOp,
        out bool open)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT next_op_id, closed FROM command_scope
            WHERE scope_id = $s AND player_id = $p;
            """;
        cmd.Parameters.AddWithValue("$s", scopeId);
        cmd.Parameters.AddWithValue("$p", playerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            nextOp = 0;
            open = false;
            return false;
        }
        nextOp = reader.GetInt64(0);
        open = reader.GetInt64(1) == 0;
        return true;
    }

    static ReceiptMetadata? TryReadReceiptMetadata(SqliteConnection connection, SqliteTransaction tx, CommandKey key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT request_hash, code, committed_revision, length(result_blob), config_version, committed_at_ms
            FROM command_receipt
            WHERE player_id = $p AND scope_id = $s AND op_id = $o;
            """;
        cmd.Parameters.AddWithValue("$p", key.PlayerId);
        cmd.Parameters.AddWithValue("$s", key.ScopeId);
        cmd.Parameters.AddWithValue("$o", key.OpId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        string hex = reader.GetString(0);
        int code = reader.GetInt32(1);
        long rev = reader.GetInt64(2);
        int blobLength = reader.GetInt32(3);
        int cfg = reader.GetInt32(4);
        long at = reader.GetInt64(5);
        return new ReceiptMetadata(new CommandHash(hex), code, rev, cfg, at, blobLength);
    }

    sealed record ReceiptMetadata(
        CommandHash RequestHash, int Code, long CommittedRevision,
        int ConfigVersion, long CommittedAtMs, int BlobLength)
    {
        public bool Matches(ReceiptDetails details)
            => RequestHash.Equals(details.RequestHash)
               && Code == details.Code
               && CommittedRevision == details.CommittedRevision
               && ConfigVersion == details.ConfigVersion
               && CommittedAtMs == details.CommittedAtMs
               && BlobLength == details.ResultBlobLength;
    }

    sealed class L1Entry
    {
        public L1Entry(CommandKey key, ReceiptDetails details)
        {
            Key = key;
            Details = details;
        }

        public CommandKey Key { get; }
        public ReceiptDetails Details { get; }
    }
}
