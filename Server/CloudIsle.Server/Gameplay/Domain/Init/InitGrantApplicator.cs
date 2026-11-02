using System;
using System.Collections.Generic;

namespace CloudIsle.Domain.Init
{
    /// <summary>
    /// 纯领域应用候选到聚合克隆。只补缺失；不重锁/不重置已有 Empty/Growing/工具高等级。
    /// 不无条件改写订单标志；旧 candidate 不得把较新 grant 降级。
    /// </summary>
    public static class InitGrantApplicator
    {
        public static PlayerAggregate ApplyToClone(PlayerAggregate source, InitGrantCandidate candidate)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            // NoOp / 已达目标 / 损坏存档：均先完整性检查
            InitGrantBuilder.ValidateAggregateIntegrity(source);

            // grant 单调：source 已 >= 目标则原样保留（含旧 candidate 试图授 1 而当前已是 2）
            if (candidate.IsNoOp || source.GrantVersion >= candidate.TargetGrantVersion)
                return CloneUnchanged(source);

            var plots = new List<PlotStateView>(source.Plots.Count + candidate.Plots.Count);
            var plotIds = new HashSet<int>();
            for (int i = 0; i < source.Plots.Count; i++)
            {
                plots.Add(source.Plots[i]);
                plotIds.Add(source.Plots[i].PlotId);
            }

            for (int i = 0; i < candidate.Plots.Count; i++)
            {
                InitPlotCandidate spec = candidate.Plots[i];
                if (plotIds.Contains(spec.PlotId))
                    continue; // 防御：已有 Empty/Locked/生长/未知一律保留
                plots.Add(new PlotStateView(
                    spec.PlotId,
                    spec.UnlockedEmpty ? DomainPlotState.Empty : DomainPlotState.Locked,
                    0, 0, 0, false, false));
                plotIds.Add(spec.PlotId);
            }

            var nodes = new List<NodeStateView>(source.Nodes.Count + candidate.Nodes.Count);
            var nodeIds = new HashSet<int>();
            for (int i = 0; i < source.Nodes.Count; i++)
            {
                nodes.Add(source.Nodes[i]);
                nodeIds.Add(source.Nodes[i].NodeId);
            }

            for (int i = 0; i < candidate.Nodes.Count; i++)
            {
                InitNodeCandidate spec = candidate.Nodes[i];
                if (nodeIds.Contains(spec.NodeId))
                    continue;
                nodes.Add(new NodeStateView(spec.NodeId, spec.ConfigNodeId, 0));
                nodeIds.Add(spec.NodeId);
            }

            var tools = new List<ToolStateView>(source.Tools.Count + candidate.ToolsToEnsure.Count);
            var toolMap = new Dictionary<int, int>();
            for (int i = 0; i < source.Tools.Count; i++)
            {
                tools.Add(source.Tools[i]);
                toolMap[source.Tools[i].ToolId] = source.Tools[i].Level;
            }

            for (int i = 0; i < candidate.ToolsToEnsure.Count; i++)
            {
                InitToolCandidate spec = candidate.ToolsToEnsure[i];
                if (toolMap.TryGetValue(spec.ToolId, out int have))
                {
                    if (have < spec.Level)
                    {
                        for (int t = 0; t < tools.Count; t++)
                        {
                            if (tools[t].ToolId == spec.ToolId)
                            {
                                tools[t] = new ToolStateView(spec.ToolId, spec.Level);
                                toolMap[spec.ToolId] = spec.Level;
                                break;
                            }
                        }
                    }

                    continue;
                }

                tools.Add(new ToolStateView(spec.ToolId, spec.Level));
                toolMap[spec.ToolId] = spec.Level;
            }

            return new PlayerAggregate(
                source.PlayerId,
                source.Nickname,
                source.Level,
                source.Exp,
                source.Gold,
                source.Gem,
                source.Ticket,
                source.Shard,
                source.BagSlots,
                source.GuideStep,
                candidate.TargetGrantVersion,
                source.Revision,
                plots,
                nodes,
                tools,
                source.Facilities,
                source.Bag,
                // 保留来源订单/领取标志；不得强制 true 导致 Mapper 清订单
                ordersInitializedEmpty: source.OrdersInitializedEmpty,
                shopPurchases: source.ShopPurchases,
                acceptedTasks: source.AcceptedTasks);
        }

        static PlayerAggregate CloneUnchanged(PlayerAggregate source)
        {
            return new PlayerAggregate(
                source.PlayerId,
                source.Nickname,
                source.Level,
                source.Exp,
                source.Gold,
                source.Gem,
                source.Ticket,
                source.Shard,
                source.BagSlots,
                source.GuideStep,
                source.GrantVersion,
                source.Revision,
                source.Plots,
                source.Nodes,
                source.Tools,
                source.Facilities,
                source.Bag,
                source.OrdersInitializedEmpty,
                source.ShopPurchases,
                source.AcceptedTasks);
        }
    }
}
