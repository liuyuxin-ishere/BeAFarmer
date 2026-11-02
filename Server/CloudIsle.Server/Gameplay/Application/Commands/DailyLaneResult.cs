using System.Collections.Generic;
using CloudIsle.Domain.Daily;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>同 lane 日切结果；NoOp 不增 revision/op；Applied 时携带系统 Batch（scope/op=0）。</summary>
public sealed class DailyLaneResult
{
    public DailyLaneResult(
        bool applied,
        bool wasNoOp,
        long revision,
        int targetDay,
        PlayerState state,
        CommitOutcome? commit,
        PlayerDeltaBatchNtf? systemDelta,
        IReadOnlyDictionary<string, DailyModuleMarker> markers,
        string message)
    {
        Applied = applied;
        WasNoOp = wasNoOp;
        Revision = revision;
        TargetDay = targetDay;
        State = state ?? throw new ArgumentNullException(nameof(state));
        Commit = commit;
        SystemDelta = systemDelta?.Clone();
        Markers = markers ?? throw new ArgumentNullException(nameof(markers));
        Message = message ?? "";
    }

    public bool Applied { get; }
    public bool WasNoOp { get; }
    public long Revision { get; }
    public int TargetDay { get; }
    public PlayerState State { get; }
    public CommitOutcome? Commit { get; }
    /// <summary>系统独立 Batch；marker-only 仍抬 revision 并带 NewRevision 元数据。</summary>
    public PlayerDeltaBatchNtf? SystemDelta { get; }
    public IReadOnlyDictionary<string, DailyModuleMarker> Markers { get; }
    public string Message { get; }
}
