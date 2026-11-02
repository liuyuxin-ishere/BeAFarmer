using System.Collections.Generic;
using CloudIsle.Domain.Commands;
using CloudIsle.Proto;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>Coordinator 对外结果；不发网络。成功资产变更携带冻结 Delta（非事后再读库）。</summary>
public sealed class CommandOutcome
{
    static readonly IReadOnlyList<PlayerDeltaBatchNtf> EmptySystem = Array.Empty<PlayerDeltaBatchNtf>();

    public CommandOutcome(
        CommandDisposition disposition,
        int transportCode,
        long nextOpId,
        bool scopeOpen,
        bool hasAuthoritativeNext,
        ReceiptDetails? details,
        bool isReplay,
        int ruleInvocations,
        int rngCalls,
        string? message = null,
        PlayerDeltaBatchNtf? frozenDelta = null,
        bool requiresClientResync = false,
        IReadOnlyList<PlayerDeltaBatchNtf>? precedingSystemDeltas = null)
    {
        Disposition = disposition;
        TransportCode = transportCode;
        NextOpId = nextOpId;
        ScopeOpen = scopeOpen;
        HasAuthoritativeNext = hasAuthoritativeNext;
        Details = details;
        IsReplay = isReplay;
        RuleInvocations = ruleInvocations;
        RngCalls = rngCalls;
        Message = message ?? "";
        FrozenDelta = frozenDelta?.Clone();
        RequiresClientResync = requiresClientResync;
        PrecedingSystemDeltas = FreezeSystem(precedingSystemDeltas);
    }

    public CommandDisposition Disposition { get; }
    /// <summary>0 成功/Recorded/NotRecorded；110/111/112/504 等。</summary>
    public int TransportCode { get; }
    public long NextOpId { get; }
    public bool ScopeOpen { get; }
    public bool HasAuthoritativeNext { get; }
    public ReceiptDetails? Details { get; }
    public bool IsReplay { get; }
    public int RuleInvocations { get; }
    public int RngCalls { get; }
    public string Message { get; }
    /// <summary>提交瞬间冻结的增量；拒绝/noop 为 null。发布器用此字段，不读事后可变状态。</summary>
    public PlayerDeltaBatchNtf? FrozenDelta { get; }
    /// <summary>
    /// 已记账成功但归档无法给出完整资产 Batch（如旧 cmdresult-v1）。
    /// 仍发配对 Resp；客户端须走全量 Sync，不得依赖残缺增量。
    /// </summary>
    public bool RequiresClientResync { get; }
    /// <summary>
    /// 日切等系统 Batch，须在外部命令 Batch/Resp 之前投递；scope/op=0，不消耗客户端 next。
    /// </summary>
    public IReadOnlyList<PlayerDeltaBatchNtf> PrecedingSystemDeltas { get; }

    static IReadOnlyList<PlayerDeltaBatchNtf> FreezeSystem(IReadOnlyList<PlayerDeltaBatchNtf>? source)
    {
        if (source == null || source.Count == 0)
            return EmptySystem;
        var arr = new PlayerDeltaBatchNtf[source.Count];
        for (int i = 0; i < source.Count; i++)
            arr[i] = source[i]?.Clone() ?? throw new ArgumentException("null system delta");
        return Array.AsReadOnly(arr);
    }
}
