using System.Collections.Generic;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Daily;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Application.Commands;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Trade;
using CloudIsle.Server.Gameplay.Infrastructure.Daily;
using CloudIsle.Server.Gameplay.Infrastructure.Farm;
using CloudIsle.Server.Gameplay.Infrastructure.Trade;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Commands;

/// <summary>
/// 单同步 SQLite 事务：写前自校验请求形状/修订/内容 CAS、再写 player+snapshot+receipt+next。
/// 不调用 Socket/Policy/PlayerQueue；无嵌套 PlayerStore.Save 事务。
/// 完整候选为 <see cref="PlayerState"/>（含未知 wire 字段的 PlayerFullData），非投影聚合。
/// </summary>
public sealed class SqliteCommandCommitStore : ICommandCommitStore
{
    readonly SqliteStore _store;

    /// <summary>测试钩：在指定写点抛错。生产保持 None。</summary>
    internal CommitFaultPoint FaultPoint { get; set; }

    /// <summary>测试探针：默认 null。挂在真实写点/COMMIT 后，不合成提交结果。</summary>
    internal CommitFaultProbe? Probe { get; set; }

    public SqliteCommandCommitStore(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<CommitOutcome> CommitAsync(CommitRequest request, CancellationToken completionCt)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        completionCt.ThrowIfCancellationRequested();

        // 形状校验在任何写之前（无需 DB）
        ValidateRequestShape(request);

        CommitFaultPoint fault = FaultPoint;
        CommitFaultProbe? probe = Probe;
        // Correlate every external commit phase with the validated scope advance.
        // System writes have no client command identity and keep a null probe key.
        CommandKey? commandKey = request.ScopeAdvance is ScopeAdvanceSpec advance
            ? new CommandKey(request.PlayerId, advance.ScopeId, advance.OpId)
            : null;
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            bool durable = false;
            try
            {
                PlayerState live = PlayerStore.ReadInTransaction(connection, tx, request.PlayerId);
                if (live.Revision != request.ObservedStoredRevision)
                    throw new InvalidOperationException("CAS precondition failed: revision drifted");

                if (request.SystemGuard?.ExpectedGrantVersion is int eg
                    && live.Data.GrantVersion != eg)
                    throw new InvalidOperationException("grant_version CAS failed");

                IReadOnlyDictionary<string, DailyModuleMarker>? liveMarkers = null;
                if (request.SystemGuard?.ExpectedMarkers != null)
                {
                    liveMarkers = DailyMarkerStore.ReadAll(connection, tx, request.PlayerId);
                    if (!DailyMarkerStore.SameMarkers(liveMarkers, request.SystemGuard.ExpectedMarkers))
                        throw new InvalidOperationException("daily marker CAS failed");
                }

                PlayerState candidate = request.Candidate;
                ValidateRevisionAndContent(request, live, candidate, liveMarkers);

                long nextOp = 0;
                bool scopeOpen = false;
                if (request.ScopeAdvance != null)
                {
                    if (!TryReadScope(
                            connection, tx, request.PlayerId, request.ScopeAdvance.ScopeId,
                            out nextOp, out scopeOpen))
                        throw new InvalidOperationException("scope missing at commit");
                    if (!scopeOpen)
                        throw new InvalidOperationException("scope closed at commit");
                    if (nextOp != request.ScopeAdvance.OpId)
                        throw new InvalidOperationException("scope next_op mismatch at commit");
                    if (nextOp == long.MaxValue)
                        throw new InvalidOperationException("op overflow: cannot advance next");
                }

                long newRevision = candidate.Revision;
                PlayerFullData dataCopy = candidate.Data.Clone();
                // Data.Revision 与 PlayerState.Revision 对齐（测试夹具可能只设外层 Revision）
                dataCopy.Revision = newRevision;
                if (dataCopy.Info == null)
                    throw new InvalidOperationException("candidate missing Info");
                if (dataCopy.Info.PlayerId != request.PlayerId)
                    throw new InvalidOperationException("Info.PlayerId mismatch");
                byte[] body = dataCopy.ToByteArray();
                int grant = dataCopy.GrantVersion;

                PlayerStore.WriteBaseAndSnapshotInTransaction(
                    connection, tx, request.PlayerId, dataCopy.Info, newRevision, grant, body,
                    casObservedRevision: request.ObservedStoredRevision,
                    beforePlayerUpdate: () => Hit(fault, CommitFaultPoint.BeforePlayerUpdate, probe, commandKey),
                    afterPlayerUpdate: () => Hit(fault, CommitFaultPoint.AfterPlayerUpdate, probe, commandKey),
                    beforeSnapshot: () => Hit(fault, CommitFaultPoint.BeforeSnapshot, probe, commandKey),
                    afterSnapshot: () => Hit(fault, CommitFaultPoint.AfterSnapshot, probe, commandKey));

                // 商店计数与货币/背包/receipt/scope 同事务；始终以候选全量替换（含清空）。
                ShopPurchaseStore.ReplaceAll(
                    connection, tx, request.PlayerId, candidate.ShopPurchases ?? ShopPurchaseCounts.Empty);
                // 已接受任务规格与 base/snapshot/receipt/next 同事务。
                AcceptedTaskStore.Replace(
                    connection, tx, request.PlayerId, candidate.AcceptedTasks ?? AcceptedTaskState.Empty);

                if (request.SystemGuard?.CandidateMarkers != null)
                {
                    DailyMarkerStore.ReplaceAll(
                        connection, tx, request.PlayerId, request.SystemGuard.CandidateMarkers);
                }

                ReceiptDetails? receiptDetails = null;
                if (request.ScopeAdvance != null && request.Receipt != null)
                {
                    ScopeAdvanceSpec adv = request.ScopeAdvance;
                    ReceiptWriteSpec rec = request.Receipt;
                    Hit(fault, CommitFaultPoint.BeforeReceipt, probe, commandKey);
                    InsertReceipt(connection, tx, request.PlayerId, adv, rec);
                    Hit(fault, CommitFaultPoint.AfterReceipt, probe, commandKey);
                    receiptDetails = new ReceiptDetails(
                        rec.Code, rec.CommittedRevision, rec.ConfigVersion, rec.ResultBlob,
                        rec.CommittedAtMs, adv.RequestHash);

                    long advanced = checked(adv.OpId + 1);
                    Hit(fault, CommitFaultPoint.BeforeScopeAdvance, probe, commandKey);
                    AdvanceScope(connection, tx, request.PlayerId, adv.ScopeId, advanced);
                    Hit(fault, CommitFaultPoint.AfterScopeAdvance, probe, commandKey);
                    nextOp = advanced;
                    scopeOpen = true;
                }

                Hit(fault, CommitFaultPoint.BeforeCommit, probe, commandKey);
                tx.Commit();
                durable = true;

                bool postFault = fault == CommitFaultPoint.AfterCommit;
                // COMMIT 已可见：探针可 barrier/抛错，但不得回滚（catch 见 durable 短路）。
                Hit(fault, CommitFaultPoint.AfterCommit, probe, commandKey);

                var committed = new PlayerState(
                    request.PlayerId, dataCopy.Clone(), newRevision, newRevision,
                    candidate.ShopPurchases, candidate.AcceptedTasks);
                return new CommitOutcome(
                    durableCommitted: true,
                    committedRevision: newRevision,
                    nextOpId: nextOp,
                    scopeOpen: scopeOpen,
                    receiptDetails: receiptDetails,
                    committedState: committed,
                    postCommitFault: postFault);
            }
            catch
            {
                if (!durable)
                {
                    try { tx.Rollback(); } catch (SqliteException) { }
                }
                throw;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 写前形状：external=scope+receipt 且无 system；system=两者皆无；拒混合/半套。
    /// receipt.revision/time 与 candidate/request 对齐。
    /// </summary>
    internal static void ValidateRequestShape(CommitRequest request)
    {
        bool hasScope = request.ScopeAdvance != null;
        bool hasReceipt = request.Receipt != null;
        bool hasSystem = request.SystemGuard != null;

        if (hasSystem && (hasScope || hasReceipt))
            throw new InvalidOperationException("mixed system guard with external scope/receipt");
        if (hasScope != hasReceipt)
            throw new InvalidOperationException(
                hasScope ? "external write commit requires receipt" : "receipt without scope advance");
        if (!hasScope && !hasReceipt && !hasSystem)
            throw new InvalidOperationException("commit requires external scope+receipt or system guard");

        PlayerState candidate = request.Candidate;
        if (candidate.Revision < 0)
            throw new InvalidOperationException("candidate revision negative");
        if (candidate.Data.Info == null)
            throw new InvalidOperationException("candidate missing Info");
        if (candidate.Data.Info.PlayerId != request.PlayerId)
            throw new InvalidOperationException("Info.PlayerId mismatch");
        if (candidate.PlayerId != request.PlayerId)
            throw new InvalidOperationException("candidate.PlayerId mismatch");

        if (hasReceipt)
        {
            ReceiptWriteSpec rec = request.Receipt!;
            if (rec.CommittedRevision != candidate.Revision)
                throw new InvalidOperationException("receipt.committedRevision must equal candidate.Revision");
            if (rec.CommittedAtMs != request.CommittedAtMs)
                throw new InvalidOperationException("receipt.committedAtMs must equal request.CommittedAtMs");
        }
    }

    /// <summary>
    /// 相对 live 的严格修订规则：PlayerFullData（Revision 归零）+ 可选日切 marker 一并视为完整内容；
    /// 全等 → NoOp 保持 revision；任一变更 → 必须 observed+1。
    /// 非零业务拒绝码必须保留全部状态+revision+marker。
    /// </summary>
    internal static void ValidateRevisionAndContent(
        CommitRequest request,
        PlayerState live,
        PlayerState candidate,
        IReadOnlyDictionary<string, DailyModuleMarker>? liveMarkers = null)
    {
        long observed = request.ObservedStoredRevision;
        bool sameData = SameCompleteContent(candidate.Data, live.Data);
        bool sameShop = ShopPurchaseCounts.Same(candidate.ShopPurchases, live.ShopPurchases);
        bool sameTasks = AcceptedTaskState.Same(candidate.AcceptedTasks, live.AcceptedTasks);
        bool sameMarkers = true;
        if (request.SystemGuard?.ExpectedMarkers != null)
        {
            IReadOnlyDictionary<string, DailyModuleMarker> expectedLive = liveMarkers
                ?? request.SystemGuard.ExpectedMarkers;
            IReadOnlyDictionary<string, DailyModuleMarker> next =
                request.SystemGuard.CandidateMarkers
                ?? throw new InvalidOperationException("system day commit missing candidateMarkers");
            sameMarkers = DailyMarkerStore.SameMarkers(expectedLive, next);
        }

        bool sameContent = sameData && sameShop && sameTasks && sameMarkers;

        if (request.Receipt is { Code: not 0 })
        {
            if (!sameContent || candidate.Revision != observed)
                throw new InvalidOperationException(
                    "nonzero business refusal must preserve all state and revision");
            return;
        }

        if (sameContent)
        {
            // 严格 NoOp：完整内容不变不得抬 revision（含系统 grant/marker 元数据未变）
            if (candidate.Revision != observed)
                throw new InvalidOperationException(
                    "strict NoOp: unchanged complete content must keep observed revision");
            return;
        }

        long expected;
        try { expected = checked(observed + 1); }
        catch (OverflowException)
        {
            throw new InvalidOperationException("revision overflow at commit validation");
        }

        if (candidate.Revision != expected)
            throw new InvalidOperationException(
                "content change requires candidate.Revision == observed+1 (no jump/rollback/same-rev mutate)");
    }

    /// <summary>完整 PlayerFullData 相等；仅将 Revision 归一为 0 后比较，保留未知字段。</summary>
    internal static bool SameCompleteContent(PlayerFullData a, PlayerFullData b)
    {
        PlayerFullData ca = a.Clone();
        PlayerFullData cb = b.Clone();
        ca.Revision = 0;
        cb.Revision = 0;
        return ca.Equals(cb);
    }

    static void Hit(CommitFaultPoint configured, CommitFaultPoint point, CommitFaultProbe? probe, CommandKey? key)
    {
        probe?.Hit(CommitFaultProbe.FromStorePoint(point), key);
        if (configured == point && point != CommitFaultPoint.AfterCommit)
            throw new InvalidOperationException("commit fault injected: " + point);
        // AfterCommit：历史 FaultPoint 仅标记 PostCommitFault，不抛；探针可另行 Throw/Hang。
    }

    static bool TryReadScope(
        SqliteConnection connection, SqliteTransaction tx, long playerId, long scopeId,
        out long nextOp, out bool open)
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

    static void InsertReceipt(
        SqliteConnection connection, SqliteTransaction tx, long playerId,
        ScopeAdvanceSpec adv, ReceiptWriteSpec rec)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO command_receipt
              (player_id, scope_id, op_id, request_hash, code, committed_revision, result_blob, config_version, committed_at_ms)
            VALUES ($p, $s, $o, $h, $c, $r, $b, $cfg, $t);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$s", adv.ScopeId);
        cmd.Parameters.AddWithValue("$o", adv.OpId);
        cmd.Parameters.AddWithValue("$h", adv.RequestHash.Hex);
        cmd.Parameters.AddWithValue("$c", rec.Code);
        cmd.Parameters.AddWithValue("$r", rec.CommittedRevision);
        cmd.Parameters.Add("$b", SqliteType.Blob).Value = rec.ResultBlob;
        cmd.Parameters.AddWithValue("$cfg", rec.ConfigVersion);
        cmd.Parameters.AddWithValue("$t", rec.CommittedAtMs);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidDataException("receipt insert failed");
    }

    static void AdvanceScope(
        SqliteConnection connection, SqliteTransaction tx, long playerId, long scopeId, long nextOp)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE command_scope SET next_op_id = $n
            WHERE scope_id = $s AND player_id = $p AND closed = 0;
            """;
        cmd.Parameters.AddWithValue("$n", nextOp);
        cmd.Parameters.AddWithValue("$s", scopeId);
        cmd.Parameters.AddWithValue("$p", playerId);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("scope advance failed");
    }
}
