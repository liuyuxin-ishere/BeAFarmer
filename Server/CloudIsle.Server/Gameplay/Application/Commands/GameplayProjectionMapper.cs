using System;
using System.Collections.Generic;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 已提交投影：全量深拷贝与单次逻辑提交的 DeltaBatch。
/// 拒绝无状态 batch；模块字段缺省表示本批无该模块变更。
/// </summary>
public static class GameplayProjectionMapper
{
    /// <summary>全量 PlayerFullData 深拷贝（含未知字段）。</summary>
    public static PlayerFullData ToFullClone(PlayerState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        var projection = state.Data.Clone();
        projection.Revision = state.Revision;
        return projection;
    }

    /// <summary>
    /// 系统日切/初始化 Batch：operation_scope_id=0、op_id=0（不消费客户端 op、无假 receipt）。
    /// marker-only 亦抬 revision；客户端按连续 revision 应用。
    /// </summary>
    public static PlayerDeltaBatchNtf ToSystemDelta(
        PlayerState before,
        PlayerState after,
        long serverTimeMs,
        int configVersion)
    {
        PlayerDeltaBatchNtf? ntf = ToDeltaCore(before, after, operationScopeId: 0, opId: 0, serverTimeMs, configVersion);
        return ntf ?? throw new InvalidOperationException("system delta requires revision advance");
    }

    /// <summary>
    /// 成功资产变更时构造 Batch；同玩家 revision 相同返回 null；跨玩家、回退或跳号拒绝。
    /// </summary>
    public static PlayerDeltaBatchNtf? ToDelta(
        PlayerState before,
        PlayerState after,
        long operationScopeId,
        long opId,
        long serverTimeMs,
        int configVersion)
    {
        if (operationScopeId <= 0) throw new ArgumentOutOfRangeException(nameof(operationScopeId));
        if (opId <= 0) throw new ArgumentOutOfRangeException(nameof(opId));
        return ToDeltaCore(before, after, operationScopeId, opId, serverTimeMs, configVersion);
    }

    static PlayerDeltaBatchNtf? ToDeltaCore(
        PlayerState before,
        PlayerState after,
        long operationScopeId,
        long opId,
        long serverTimeMs,
        int configVersion)
    {
        if (before == null) throw new ArgumentNullException(nameof(before));
        if (after == null) throw new ArgumentNullException(nameof(after));
        if (serverTimeMs < 0) throw new ArgumentOutOfRangeException(nameof(serverTimeMs));
        if (configVersion < 1) throw new ArgumentOutOfRangeException(nameof(configVersion));
        if (after.PlayerId != before.PlayerId)
            throw new ArgumentException("player mismatch");
        if (after.Revision == before.Revision)
            return null;
        if (before.Revision == long.MaxValue || after.Revision != before.Revision + 1)
            throw new ArgumentException("delta must advance revision by exactly one");

        PlayerFullData b = before.Data;
        PlayerFullData a = after.Data;
        var ntf = new PlayerDeltaBatchNtf
        {
            BaseRevision = before.Revision,
            NewRevision = after.Revision,
            OperationScopeId = operationScopeId,
            OpId = opId,
            ServerTime = serverTimeMs,
            ConfigVersion = configVersion
        };

        // Attr：等级/货币等
        if (a.Info != null)
        {
            ntf.Attr = new PlayerAttrNtf
            {
                Level = a.Info.Level,
                Exp = a.Info.Exp,
                Gold = a.Info.Gold,
                Gem = a.Info.Gem,
                Ticket = a.Info.Ticket,
                Shard = a.Info.Shard,
                BagSlots = a.Info.BagSlots
            };
        }

        if (!RepeatedEqual(b.Bag, a.Bag))
        {
            ntf.Bag = new BagChangeNtf();
            var remainingItems = new HashSet<int>();
            for (int i = 0; i < a.Bag.Count; i++)
            {
                ntf.Bag.Changes.Add(a.Bag[i].Clone());
                remainingItems.Add(a.Bag[i].ItemId);
            }
            // Changes 表示最终数量；删除也必须显式发送，客户端才能清除旧堆叠。
            for (int i = 0; i < b.Bag.Count; i++)
                if (!remainingItems.Contains(b.Bag[i].ItemId))
                    ntf.Bag.Changes.Add(new ItemStack { ItemId = b.Bag[i].ItemId, Count = 0 });
        }

        if (!RepeatedEqual(b.Plots, a.Plots))
        {
            ntf.Plots = new PlotChangeNtf();
            for (int i = 0; i < a.Plots.Count; i++)
                ntf.Plots.Plots.Add(a.Plots[i].Clone());
        }

        if (!RepeatedEqual(b.Nodes, a.Nodes))
        {
            ntf.Nodes = new NodeChangeNtf();
            for (int i = 0; i < a.Nodes.Count; i++)
                ntf.Nodes.Nodes.Add(a.Nodes[i].Clone());
        }

        if (!RepeatedEqual(b.Tools, a.Tools))
        {
            ntf.Tools = new ToolChangeNtf();
            for (int i = 0; i < a.Tools.Count; i++)
                ntf.Tools.Tools.Add(a.Tools[i].Clone());
        }

        if (!RepeatedEqual(b.Facilities, a.Facilities))
        {
            ntf.Facilities = new FacilityChangeNtf();
            for (int i = 0; i < a.Facilities.Count; i++)
                ntf.Facilities.Facilities.Add(a.Facilities[i].Clone());
        }

        if (!RepeatedEqual(b.Orders, a.Orders) || !object.Equals(b.OrderProgress, a.OrderProgress))
        {
            ntf.Orders = new OrderChangeNtf();
            ntf.Orders.Progress = a.OrderProgress?.Clone();
            for (int i = 0; i < a.Orders.Count; i++)
                ntf.Orders.Orders.Add(a.Orders[i].Clone());
        }

        return ntf;
    }

    static bool RepeatedEqual<T>(Google.Protobuf.Collections.RepeatedField<T> left, Google.Protobuf.Collections.RepeatedField<T> right)
        where T : IMessage<T>
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }
        return true;
    }
}
