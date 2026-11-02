using System;

namespace CloudIsle.Domain.Commands;

/// <summary>写命令身份键。(playerId, scopeId, opId) 均为正数。</summary>
public readonly struct CommandKey : IEquatable<CommandKey>
{
    public CommandKey(long playerId, long scopeId, long opId)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId), "playerId must be > 0");
        if (scopeId <= 0) throw new ArgumentOutOfRangeException(nameof(scopeId), "scopeId must be > 0");
        if (opId <= 0) throw new ArgumentOutOfRangeException(nameof(opId), "opId must be > 0");
        PlayerId = playerId;
        ScopeId = scopeId;
        OpId = opId;
    }

    public long PlayerId { get; }
    public long ScopeId { get; }
    public long OpId { get; }

    public bool Equals(CommandKey other)
        => PlayerId == other.PlayerId && ScopeId == other.ScopeId && OpId == other.OpId;

    public override bool Equals(object? obj) => obj is CommandKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(PlayerId, ScopeId, OpId);
}

/// <summary>规范摘要：固定格式版本的 64 位小写十六进制 SHA-256。</summary>
public readonly struct CommandHash : IEquatable<CommandHash>
{
    public const int HexLength = 64;

    public CommandHash(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != HexLength)
            throw new ArgumentException("CommandHash must be 64 lowercase hex chars", nameof(hex));
        for (int i = 0; i < hex.Length; i++)
        {
            char c = hex[i];
            if (c is < '0' or > '9' and (< 'a' or > 'f'))
                throw new ArgumentException("CommandHash must be lowercase hex", nameof(hex));
        }
        Hex = hex;
    }

    public string Hex { get; }

    public bool Equals(CommandHash other) => string.Equals(Hex, other.Hex, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is CommandHash other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hex);
    public override string ToString() => Hex;
}

/// <summary>账本详情快照（不可变；blob 存防御拷贝，每次 get 再拷贝）。</summary>
public sealed class ReceiptDetails
{
    readonly byte[] _resultBlob;

    public ReceiptDetails(
        int code,
        long committedRevision,
        int configVersion,
        byte[] resultBlob,
        long committedAtMs,
        CommandHash requestHash)
    {
        if (committedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(committedRevision), "committedRevision must be >= 0");
        if (configVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(configVersion), "configVersion must be >= 1");
        if (committedAtMs < 0)
            throw new ArgumentOutOfRangeException(nameof(committedAtMs), "committedAtMs must be >= 0");
        Code = code;
        CommittedRevision = committedRevision;
        ConfigVersion = configVersion;
        _resultBlob = resultBlob == null || resultBlob.Length == 0
            ? Array.Empty<byte>()
            : (byte[])resultBlob.Clone();
        CommittedAtMs = committedAtMs;
        RequestHash = requestHash;
    }

    public int Code { get; }
    public long CommittedRevision { get; }
    public int ConfigVersion { get; }

    /// <summary>每次访问返回新拷贝，防止调用方污染 L1/缓存。</summary>
    public byte[] ResultBlob =>
        _resultBlob.Length == 0 ? Array.Empty<byte>() : (byte[])_resultBlob.Clone();

    /// <summary>缓存校验载荷长度时避免读取并复制公开数组。</summary>
    internal int ResultBlobLength => _resultBlob.Length;

    public long CommittedAtMs { get; }
    public CommandHash RequestHash { get; }

    internal bool MetadataEquals(ReceiptDetails other)
        => Code == other.Code
           && CommittedRevision == other.CommittedRevision
           && ConfigVersion == other.ConfigVersion
           && CommittedAtMs == other.CommittedAtMs
           && RequestHash.Equals(other.RequestHash);
}

/// <summary>Lookup 分类结果。</summary>
public enum ReceiptLookupKind
{
    RecordedMatched = 1,
    PayloadConflict = 2,
    NotRecorded = 3,
    Expired = 4,
    ScopeInvalid = 5,
    OpGap = 6
}

/// <summary>Query 分类结果。未来 op（&gt;next）映射为 NotRecorded，不另造 OpGap 查询态。</summary>
public enum ReceiptQueryKind
{
    Recorded = 1,
    NotRecorded = 2,
    Expired = 3,
    ScopeInvalid = 4
}

/// <summary>Lookup 返回：含权威 next_op 与 scope 开闭（归属校验后）。</summary>
public sealed class ReceiptLookup
{
    public ReceiptLookup(
        ReceiptLookupKind kind,
        long nextOpId,
        bool scopeOpen,
        ReceiptDetails? details)
    {
        Kind = kind;
        NextOpId = nextOpId;
        ScopeOpen = scopeOpen;
        Details = details;
    }

    public ReceiptLookupKind Kind { get; }
    public long NextOpId { get; }
    public bool ScopeOpen { get; }
    public ReceiptDetails? Details { get; }
}

/// <summary>Query 返回：只读；不执行 Domain/RNG。</summary>
public sealed class ReceiptQuery
{
    public ReceiptQuery(
        ReceiptQueryKind kind,
        int transportCode,
        long nextOpId,
        bool scopeOpen,
        ReceiptDetails? details)
    {
        Kind = kind;
        TransportCode = transportCode;
        NextOpId = nextOpId;
        ScopeOpen = scopeOpen;
        Details = details;
    }

    public ReceiptQueryKind Kind { get; }
    /// <summary>0 NotRecorded/Recorded；110 外 scope；111 Expired。</summary>
    public int TransportCode { get; }
    public long NextOpId { get; }
    public bool ScopeOpen { get; }
    public ReceiptDetails? Details { get; }
}

/// <summary>
/// 不可变写命令信封。仅由 <c>CommandEnvelopeFactory</c> 构造；禁止外部任意 kind/未校验载荷。
/// expectedRevision &gt;= 0；configVersion &gt;= 1。
/// </summary>
public sealed class CommandEnvelope
{
    readonly byte[] _canonicalPayload;

    /// <summary>仅 Application 工厂与同程序集测试 InternalsVisibleTo 可构造。</summary>
    internal CommandEnvelope(
        CommandKey key,
        string kind,
        long expectedRevision,
        int configVersion,
        byte[] canonicalBusinessPayload)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind required", nameof(kind));
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "expectedRevision must be >= 0");
        if (configVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(configVersion), "configVersion must be >= 1");
        if (canonicalBusinessPayload == null)
            throw new ArgumentNullException(nameof(canonicalBusinessPayload));

        Key = key;
        Kind = kind;
        ExpectedRevision = expectedRevision;
        ConfigVersion = configVersion;
        _canonicalPayload = (byte[])canonicalBusinessPayload.Clone();
    }

    public CommandKey Key { get; }
    public string Kind { get; }
    public long ExpectedRevision { get; }
    public int ConfigVersion { get; }

    /// <summary>规范业务载荷防御性拷贝（不含 player/scope/op/seq/token）。</summary>
    public byte[] CopyCanonicalBusinessPayload() => (byte[])_canonicalPayload.Clone();
}
