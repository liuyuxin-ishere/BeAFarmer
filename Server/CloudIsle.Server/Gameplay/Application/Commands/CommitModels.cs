using System.Collections.Generic;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Daily;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>测试/内部：同步 SQL 写点与 COMMIT 前后故障注入。生产默认 None。</summary>
public enum CommitFaultPoint
{
    None = 0,
    BeforePlayerUpdate = 1,
    AfterPlayerUpdate = 2,
    BeforeSnapshot = 3,
    AfterSnapshot = 4,
    BeforeReceipt = 5,
    AfterReceipt = 6,
    BeforeScopeAdvance = 7,
    AfterScopeAdvance = 8,
    BeforeCommit = 9,
    AfterCommit = 10
}

/// <summary>外部写命令的 scope 推进意图（next 变为 op+1）。</summary>
public sealed class ScopeAdvanceSpec
{
    public ScopeAdvanceSpec(long scopeId, long opId, CommandHash requestHash)
    {
        if (scopeId <= 0) throw new ArgumentOutOfRangeException(nameof(scopeId));
        if (opId <= 0) throw new ArgumentOutOfRangeException(nameof(opId));
        ScopeId = scopeId;
        OpId = opId;
        RequestHash = requestHash;
    }

    public long ScopeId { get; }
    public long OpId { get; }
    public CommandHash RequestHash { get; }
}

/// <summary>回执行内容（不可变；ResultBlob 每次 get 防御拷贝）。</summary>
public sealed class ReceiptWriteSpec
{
    readonly byte[] _resultBlob;

    public ReceiptWriteSpec(int code, long committedRevision, int configVersion, byte[] resultBlob, long committedAtMs)
    {
        if (committedRevision < 0) throw new ArgumentOutOfRangeException(nameof(committedRevision));
        if (configVersion < 1) throw new ArgumentOutOfRangeException(nameof(configVersion));
        if (committedAtMs < 0) throw new ArgumentOutOfRangeException(nameof(committedAtMs));
        Code = code;
        CommittedRevision = committedRevision;
        ConfigVersion = configVersion;
        _resultBlob = resultBlob == null || resultBlob.Length == 0
            ? Array.Empty<byte>()
            : (byte[])resultBlob.Clone();
        CommittedAtMs = committedAtMs;
    }

    public int Code { get; }
    public long CommittedRevision { get; }
    public int ConfigVersion { get; }
    public byte[] ResultBlob =>
        _resultBlob.Length == 0 ? Array.Empty<byte>() : (byte[])_resultBlob.Clone();
    public long CommittedAtMs { get; }
}

/// <summary>
/// 系统护栏（grant / 日切 marker CAS）；不创建 op0、不消费客户端 next。
/// ExpectedMarkers 非 null 时表示本提交携带日切 sidecar（含空表=清模块）。
/// </summary>
public sealed class SystemGuardSpec
{
    public SystemGuardSpec(
        int? expectedGrantVersion = null,
        IReadOnlyDictionary<string, DailyModuleMarker>? expectedMarkers = null,
        IReadOnlyDictionary<string, DailyModuleMarker>? candidateMarkers = null)
    {
        ExpectedGrantVersion = expectedGrantVersion;
        ExpectedMarkers = expectedMarkers;
        CandidateMarkers = candidateMarkers;
        if (candidateMarkers != null && expectedMarkers == null)
            throw new ArgumentException("candidateMarkers requires expectedMarkers for CAS");
    }

    public int? ExpectedGrantVersion { get; }
    /// <summary>提交前观察到的 marker 快照（CAS）；null=本提交不碰 sidecar。</summary>
    public IReadOnlyDictionary<string, DailyModuleMarker>? ExpectedMarkers { get; }
    /// <summary>提交后 marker；仅当 ExpectedMarkers 非 null 时有意义。</summary>
    public IReadOnlyDictionary<string, DailyModuleMarker>? CandidateMarkers { get; }
}

/// <summary>
/// 提交请求。构造时克隆 Candidate；属性 get 再 DeepCopy，调用方无法通过持有引用污染已准备的事务意图。
/// ObservedStoredRevision 为队首读到的 DB revision（CAS），不同于命令里可能过期的 ExpectedRevision。
/// 形状/修订语义由 <see cref="ICommandCommitStore.CommitAsync"/> 在写库前校验（构造不抛业务畸形）。
/// </summary>
public sealed class CommitRequest
{
    readonly PlayerState _candidate;

    public CommitRequest(
        long playerId,
        long observedStoredRevision,
        PlayerState candidate,
        ScopeAdvanceSpec? scopeAdvance,
        ReceiptWriteSpec? receipt,
        SystemGuardSpec? systemGuard,
        long committedAtMs)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (observedStoredRevision < 0) throw new ArgumentOutOfRangeException(nameof(observedStoredRevision));
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        if (candidate.PlayerId != playerId)
            throw new ArgumentException("candidate.PlayerId mismatch", nameof(candidate));
        if (committedAtMs < 0) throw new ArgumentOutOfRangeException(nameof(committedAtMs));

        PlayerId = playerId;
        ObservedStoredRevision = observedStoredRevision;
        _candidate = candidate.DeepCopy();
        ScopeAdvance = scopeAdvance;
        Receipt = receipt;
        SystemGuard = systemGuard;
        CommittedAtMs = committedAtMs;
    }

    public long PlayerId { get; }
    public long ObservedStoredRevision { get; }
    /// <summary>每次 get 返回 DeepCopy，防止调用方在 prepared/await 后改写事务意图。</summary>
    public PlayerState Candidate => _candidate.DeepCopy();
    public ScopeAdvanceSpec? ScopeAdvance { get; }
    public ReceiptWriteSpec? Receipt { get; }
    public SystemGuardSpec? SystemGuard { get; }
    public long CommittedAtMs { get; }
}

/// <summary>提交结果；AfterCommit 故障时仍可标记已持久。CommittedState 每次 get DeepCopy。</summary>
public sealed class CommitOutcome
{
    readonly PlayerState _committedState;

    public CommitOutcome(
        bool durableCommitted,
        long committedRevision,
        long nextOpId,
        bool scopeOpen,
        ReceiptDetails? receiptDetails,
        PlayerState committedState,
        bool postCommitFault)
    {
        DurableCommitted = durableCommitted;
        CommittedRevision = committedRevision;
        NextOpId = nextOpId;
        ScopeOpen = scopeOpen;
        ReceiptDetails = receiptDetails;
        _committedState = (committedState ?? throw new ArgumentNullException(nameof(committedState))).DeepCopy();
        PostCommitFault = postCommitFault;
    }

    public bool DurableCommitted { get; }
    public long CommittedRevision { get; }
    public long NextOpId { get; }
    public bool ScopeOpen { get; }
    public ReceiptDetails? ReceiptDetails { get; }
    public PlayerState CommittedState => _committedState.DeepCopy();
    /// <summary>COMMIT 后钩子失败：DB 已落盘，不得当回滚。</summary>
    public bool PostCommitFault { get; }
}

public interface ICommandCommitStore
{
    Task<CommitOutcome> CommitAsync(CommitRequest request, CancellationToken completionCt);
}
