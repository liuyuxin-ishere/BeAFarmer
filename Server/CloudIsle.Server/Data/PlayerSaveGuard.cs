using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Trade;
using CloudIsle.Proto;

namespace CloudIsle.Server.Data;

/// <summary>旧 Save / SaveService 兼容决策（非经济 CommitStore 旁路）。</summary>
public enum SaveDecisionKind
{
    /// <summary>同 revision 且完整规范内容相等：不写 SQL。</summary>
    NoOp = 0,
    /// <summary>grant==0 有界 bootstrap（首快照或无 snapshot 行时的兼容落盘）。</summary>
    AllowBootstrap = 1,
    /// <summary>grant&gt;0 后仅非经济字段（昵称/引导步）抬 revision 的 M1 Dirty 冲洗。</summary>
    AllowCompatibility = 2,
    /// <summary>同 revision 异内容（含 snapshot 已存在）。</summary>
    Conflict = 3,
    /// <summary>incoming.revision &lt; stored。</summary>
    RejectStale = 4,
    /// <summary>试图用更高 revision 写入经济变更，或 grant&gt;0 后改经济域。</summary>
    RejectEconomicBypass = 5
}

/// <summary>守卫结果；由 <see cref="PlayerStore.SaveAsync"/> 在事务内执行。</summary>
public readonly struct SaveDecision
{
    public SaveDecision(SaveDecisionKind kind, string message)
    {
        Kind = kind;
        Message = message ?? "";
    }

    public SaveDecisionKind Kind { get; }
    public string Message { get; }

    public bool AllowsWrite =>
        Kind is SaveDecisionKind.AllowBootstrap or SaveDecisionKind.AllowCompatibility;
}

/// <summary>
/// 规范完整内容 = <see cref="PlayerFullData"/> 全字段（含 sidecar / unknown proto）；
/// 仅将 Revision 归零后做消息相等。重复字段顺序按 protobuf 语义；稳定业务 ID 由上游保证。
/// </summary>
public static class PlayerSaveGuard
{
    /// <summary>
    /// 评估兼容 Save。storedSnapshotExists=false 且 grant==0 时允许首写 bootstrap；
    /// 一旦存在 snapshot，同 revision 异内容一律 Conflict（含 grant 仍为 0）。
    /// </summary>
    public static SaveDecision Evaluate(
        PlayerState stored,
        PlayerState incoming,
        bool storedSnapshotExists)
    {
        if (stored == null) throw new ArgumentNullException(nameof(stored));
        if (incoming == null) throw new ArgumentNullException(nameof(incoming));
        if (stored.PlayerId != incoming.PlayerId)
            return new SaveDecision(SaveDecisionKind.Conflict, "playerId mismatch");

        if (incoming.Revision < stored.Revision)
            return new SaveDecision(SaveDecisionKind.RejectStale, "stale revision cannot overwrite newer");

        bool sameContent = SameCanonicalContent(stored, incoming);

        if (incoming.Revision == stored.Revision)
        {
            if (sameContent)
            {
                // 已有 snapshot：物理 NoOp。尚无行时仍允许物化首份快照（与 base 投影同内容）。
                if (storedSnapshotExists)
                    return new SaveDecision(SaveDecisionKind.NoOp, "same revision and canonical content");
                return new SaveDecision(SaveDecisionKind.AllowBootstrap, "materialize first identical snapshot");
            }

            if (storedSnapshotExists)
                return new SaveDecision(SaveDecisionKind.Conflict, "same revision different content");
            // 尚无 snapshot：grant0 首写
            if (stored.Data.GrantVersion == 0 && incoming.Data.GrantVersion == 0)
                return new SaveDecision(SaveDecisionKind.AllowBootstrap, "first snapshot bootstrap");
            return new SaveDecision(SaveDecisionKind.Conflict, "same revision different content without bootstrap");
        }

        // incoming.Revision > stored.Revision
        bool economic = HasEconomicDelta(stored, incoming);
        int grant = Math.Max(stored.Data.GrantVersion, incoming.Data.GrantVersion);

        if (economic)
        {
            if (grant > 0)
                return new SaveDecision(
                    SaveDecisionKind.RejectEconomicBypass,
                    "economic change after grant must use GameplayCommitStore");
            // grant==0：有界 M1 bootstrap（首包经济列/快照）
            return new SaveDecision(SaveDecisionKind.AllowBootstrap, "pre-grant bootstrap save");
        }

        // 仅非经济差或纯抬版本
        if (grant > 0)
            return new SaveDecision(SaveDecisionKind.AllowCompatibility, "non-economic dirty flush");
        return new SaveDecision(SaveDecisionKind.AllowBootstrap, "pre-grant non-economic save");
    }

    /// <summary>完整 PlayerFullData（Revision 归零后 Equals，保留 unknown）。</summary>
    public static bool SameCanonicalContent(PlayerFullData a, PlayerFullData b)
        => SameCanonicalData(a, b);

    /// <summary>含商店计数与已接受任务 sidecar 的完整规范内容。</summary>
    public static bool SameCanonicalContent(PlayerState a, PlayerState b)
    {
        if (a == null || b == null)
            return false;
        return SameCanonicalData(a.Data, b.Data)
               && ShopPurchaseCounts.Same(a.ShopPurchases, b.ShopPurchases)
               && AcceptedTaskState.Same(a.AcceptedTasks, b.AcceptedTasks);
    }

    static bool SameCanonicalData(PlayerFullData a, PlayerFullData b)
    {
        PlayerFullData ca = a.Clone();
        PlayerFullData cb = b.Clone();
        ca.Revision = 0;
        cb.Revision = 0;
        return ca.Equals(cb);
    }

    /// <summary>
    /// 经济差：在昵称/引导步对齐后仍不等。GrantVersion / 金币 / 背包 / 地块 / 商店计数 / 已接受任务等均属经济域。
    /// </summary>
    public static bool HasEconomicDelta(PlayerFullData stored, PlayerFullData incoming)
        => HasEconomicDataDelta(stored, incoming);

    public static bool HasEconomicDelta(PlayerState stored, PlayerState incoming)
    {
        if (stored == null || incoming == null)
            return true;
        if (!ShopPurchaseCounts.Same(stored.ShopPurchases, incoming.ShopPurchases))
            return true;
        if (!AcceptedTaskState.Same(stored.AcceptedTasks, incoming.AcceptedTasks))
            return true;
        return HasEconomicDataDelta(stored.Data, incoming.Data);
    }

    static bool HasEconomicDataDelta(PlayerFullData stored, PlayerFullData incoming)
    {
        PlayerFullData a = stored.Clone();
        PlayerFullData b = incoming.Clone();
        a.Revision = 0;
        b.Revision = 0;
        if (a.Info != null && b.Info != null)
        {
            b.Info.Nickname = a.Info.Nickname;
            b.Info.GuideStep = a.Info.GuideStep;
        }
        else if (a.Info == null && b.Info != null)
        {
            b.Info.Nickname = "";
            b.Info.GuideStep = 0;
        }

        return !a.Equals(b);
    }
}
