using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>系统初始化 lane 结果；失败时不应对外发布 ready 假快照。</summary>
public sealed class InitLaneResult
{
    public InitLaneResult(
        bool applied,
        bool wasNoOp,
        long revision,
        int grantVersion,
        PlayerState state,
        CommitOutcome? commit,
        string message)
    {
        Applied = applied;
        WasNoOp = wasNoOp;
        Revision = revision;
        GrantVersion = grantVersion;
        State = state ?? throw new ArgumentNullException(nameof(state));
        Commit = commit;
        Message = message ?? "";
    }

    /// <summary>是否发生了持久发放（非 NoOp）。</summary>
    public bool Applied { get; }
    public bool WasNoOp { get; }
    public long Revision { get; }
    public int GrantVersion { get; }
    public PlayerState State { get; }
    public CommitOutcome? Commit { get; }
    public string Message { get; }
}
