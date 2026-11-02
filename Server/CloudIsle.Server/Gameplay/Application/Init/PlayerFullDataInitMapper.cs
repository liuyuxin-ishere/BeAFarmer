using System;
using System.Collections.Generic;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Trade;
using CloudIsle.Proto;

namespace CloudIsle.Application.Init
{
    /// <summary>
    /// Proto ↔ 领域聚合映射（P03 PlayerAggregateMapper）。可变 <see cref="PlayerFullData"/> 仅在此深拷贝；
    /// Domain 不持有 Proto。模板行按稳定 ID Clone 后只改本模块拥有字段，保留 Slots/UnknownFields。
    /// AcceptedTaskState 为 typed sidecar，经参数注入，不进 Proto。
    /// </summary>
    public static class PlayerFullDataInitMapper
    {
        public static PlayerAggregate ToAggregate(
            PlayerFullData source,
            ShopPurchaseCounts? shopPurchases = null,
            AcceptedTaskState? acceptedTasks = null)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var plots = new List<PlotStateView>(source.Plots.Count);
            var plotIds = new HashSet<int>();
            for (int i = 0; i < source.Plots.Count; i++)
            {
                PlotData p = source.Plots[i];
                if (p.PlotId <= 0 || !plotIds.Add(p.PlotId))
                    throw new InvalidOperationException("duplicate-or-invalid-plot:" + p.PlotId);
                if (p.PlantTime < 0 || p.ReadyTime < 0)
                    throw new InvalidOperationException("invalid-plot-deadline:" + p.PlotId);
                plots.Add(new PlotStateView(
                    p.PlotId,
                    MapPlotStateStrict(p.State),
                    p.CropId,
                    p.PlantTime,
                    p.ReadyTime,
                    p.Watered,
                    p.Fertilized));
            }

            var nodes = new List<NodeStateView>(source.Nodes.Count);
            var nodeIds = new HashSet<int>();
            for (int i = 0; i < source.Nodes.Count; i++)
            {
                NodeData n = source.Nodes[i];
                if (n.NodeId <= 0 || !nodeIds.Add(n.NodeId))
                    throw new InvalidOperationException("duplicate-or-invalid-node:" + n.NodeId);
                if (n.NextReadyTime < 0)
                    throw new InvalidOperationException("invalid-node-deadline:" + n.NodeId);
                nodes.Add(new NodeStateView(n.NodeId, n.ConfigNodeId, n.NextReadyTime));
            }

            var tools = new List<ToolStateView>(source.Tools.Count);
            var toolIds = new HashSet<int>();
            for (int i = 0; i < source.Tools.Count; i++)
            {
                ToolData t = source.Tools[i];
                if (t.ToolId <= 0 || t.Level <= 0 || !toolIds.Add(t.ToolId))
                    throw new InvalidOperationException("duplicate-or-invalid-tool:" + t.ToolId);
                tools.Add(new ToolStateView(t.ToolId, t.Level));
            }

            var facilities = new List<FacilityStateView>(source.Facilities.Count);
            var facilityIds = new HashSet<int>();
            for (int i = 0; i < source.Facilities.Count; i++)
            {
                FacilityData f = source.Facilities[i];
                if (f.FacilityId <= 0 || !facilityIds.Add(f.FacilityId))
                    throw new InvalidOperationException("duplicate-or-invalid-facility:" + f.FacilityId);
                // Proto FacilityData 无配置类型字段；ConfigFacilityId=0 表示未解析，绝不用 instanceId 冒充 typeId。
                facilities.Add(new FacilityStateView(f.FacilityId, configFacilityId: 0, f.Level));
            }

            var bag = new List<BagItemView>(source.Bag.Count);
            var bagIds = new HashSet<int>();
            for (int i = 0; i < source.Bag.Count; i++)
            {
                ItemStack s = source.Bag[i];
                if (s.ItemId <= 0 || s.Count <= 0)
                    throw new InvalidOperationException("invalid-bag-stack:" + s.ItemId);
                if (!bagIds.Add(s.ItemId))
                    throw new InvalidOperationException("duplicate-bag-item:" + s.ItemId);
                bag.Add(new BagItemView(s.ItemId, s.Count));
            }

            PlayerInfo info = source.Info ?? new PlayerInfo();
            // 有现有订单或领取进度则非“空订单 marker”；008 不得据此清空 Orders。
            bool ordersEmptyMarker = source.Orders.Count == 0 && source.OrderProgress == null;
            return new PlayerAggregate(
                info.PlayerId,
                info.Nickname,
                info.Level,
                info.Exp,
                info.Gold,
                info.Gem,
                info.Ticket,
                info.Shard,
                info.BagSlots,
                info.GuideStep,
                source.GrantVersion,
                source.Revision,
                plots,
                nodes,
                tools,
                facilities,
                bag,
                ordersInitializedEmpty: ordersEmptyMarker,
                shopPurchases: shopPurchases,
                acceptedTasks: acceptedTasks);
        }

        /// <summary>
        /// 将领域聚合写回 Proto 克隆；以 <paramref name="template"/> 按稳定 ID Clone 行保留
        /// Slots / UnknownFields / Orders / OrderProgress 等本模块不拥有的字节语义。
        /// revision 不递增。
        /// </summary>
        public static PlayerFullData ToFullData(PlayerAggregate aggregate, PlayerFullData template)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            if (template == null)
                throw new ArgumentNullException(nameof(template));

            PlayerFullData clone = template.Clone();
            if (clone.Info == null)
                clone.Info = new PlayerInfo();

            clone.Info.PlayerId = aggregate.PlayerId;
            clone.Info.Nickname = aggregate.Nickname;
            clone.Info.Level = aggregate.Level;
            clone.Info.Exp = aggregate.Exp;
            clone.Info.Gold = aggregate.Gold;
            clone.Info.Gem = aggregate.Gem;
            clone.Info.Ticket = aggregate.Ticket;
            clone.Info.Shard = aggregate.Shard;
            clone.Info.BagSlots = aggregate.BagSlots;
            clone.Info.GuideStep = aggregate.GuideStep;
            clone.GrantVersion = aggregate.GrantVersion;
            clone.Revision = aggregate.Revision;

            var templatePlots = IndexPlots(template);
            clone.Plots.Clear();
            for (int i = 0; i < aggregate.Plots.Count; i++)
            {
                PlotStateView p = aggregate.Plots[i];
                PlotData row;
                if (templatePlots.TryGetValue(p.PlotId, out PlotData old))
                {
                    row = old.Clone();
                    row.State = MapPlotState(p.State);
                    row.CropId = p.CropId;
                    row.PlantTime = p.PlantTime;
                    row.ReadyTime = p.ReadyTime;
                    row.Watered = p.Watered;
                    row.Fertilized = p.Fertilized;
                }
                else
                {
                    row = new PlotData
                    {
                        PlotId = p.PlotId,
                        State = MapPlotState(p.State),
                        CropId = p.CropId,
                        PlantTime = p.PlantTime,
                        ReadyTime = p.ReadyTime,
                        Watered = p.Watered,
                        Fertilized = p.Fertilized
                    };
                }

                clone.Plots.Add(row);
            }

            var templateNodes = IndexNodes(template);
            clone.Nodes.Clear();
            for (int i = 0; i < aggregate.Nodes.Count; i++)
            {
                NodeStateView n = aggregate.Nodes[i];
                NodeData row;
                if (templateNodes.TryGetValue(n.NodeId, out NodeData old))
                {
                    row = old.Clone();
                    row.ConfigNodeId = n.ConfigNodeId;
                    row.NextReadyTime = n.NextReadyTime;
                }
                else
                {
                    row = new NodeData
                    {
                        NodeId = n.NodeId,
                        ConfigNodeId = n.ConfigNodeId,
                        NextReadyTime = n.NextReadyTime
                    };
                }

                clone.Nodes.Add(row);
            }

            var templateTools = IndexTools(template);
            clone.Tools.Clear();
            for (int i = 0; i < aggregate.Tools.Count; i++)
            {
                ToolStateView t = aggregate.Tools[i];
                ToolData row;
                if (templateTools.TryGetValue(t.ToolId, out ToolData old))
                {
                    row = old.Clone();
                    row.Level = t.Level;
                }
                else
                {
                    row = new ToolData { ToolId = t.ToolId, Level = t.Level };
                }

                clone.Tools.Add(row);
            }

            // 设施：本模块仅保留既有实体；不拥有生产槽。必须整行 Clone（含 SlotData 全字节）。
            var templateFacilities = IndexFacilities(template);
            clone.Facilities.Clear();
            for (int i = 0; i < aggregate.Facilities.Count; i++)
            {
                FacilityStateView f = aggregate.Facilities[i];
                if (templateFacilities.TryGetValue(f.FacilityId, out FacilityData old))
                {
                    clone.Facilities.Add(old.Clone());
                }
                else
                {
                    clone.Facilities.Add(new FacilityData { FacilityId = f.FacilityId, Level = f.Level });
                }
            }

            var templateBag = IndexBag(template);
            clone.Bag.Clear();
            for (int i = 0; i < aggregate.Bag.Count; i++)
            {
                BagItemView b = aggregate.Bag[i];
                ItemStack row;
                if (templateBag.TryGetValue(b.ItemId, out ItemStack old))
                {
                    row = old.Clone();
                    row.Count = b.Count;
                }
                else
                {
                    row = new ItemStack { ItemId = b.ItemId, Count = b.Count };
                }

                clone.Bag.Add(row);
            }

            // 订单/领取：template.Clone 已保留；仅新号首次初始化且原本无 OrderProgress 时补空 marker。
            // 绝不 Clear Orders。
            if (aggregate.OrdersInitializedEmpty && clone.OrderProgress == null)
                clone.OrderProgress = new OrderProgress();

            return clone;
        }

        /// <summary>
        /// Proto 便利入口。NoOp / grant 已达目标：完整性校验后 <c>source.Clone()</c> 字节等价，
        /// 不添加原先不存在的 OrderProgress。
        /// </summary>
        public static PlayerFullData ApplyInitGrant(
            PlayerFullData source,
            InitGrantCandidate candidate)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            PlayerAggregate agg = ToAggregate(source);
            PlayerAggregate applied = InitGrantApplicator.ApplyToClone(agg, candidate);

            if (candidate.IsNoOp || agg.GrantVersion >= candidate.TargetGrantVersion)
                return source.Clone();

            return ToFullData(applied, source);
        }

        static Dictionary<int, PlotData> IndexPlots(PlayerFullData template)
        {
            var map = new Dictionary<int, PlotData>(template.Plots.Count);
            for (int i = 0; i < template.Plots.Count; i++)
                map[template.Plots[i].PlotId] = template.Plots[i];
            return map;
        }

        static Dictionary<int, NodeData> IndexNodes(PlayerFullData template)
        {
            var map = new Dictionary<int, NodeData>(template.Nodes.Count);
            for (int i = 0; i < template.Nodes.Count; i++)
                map[template.Nodes[i].NodeId] = template.Nodes[i];
            return map;
        }

        static Dictionary<int, ToolData> IndexTools(PlayerFullData template)
        {
            var map = new Dictionary<int, ToolData>(template.Tools.Count);
            for (int i = 0; i < template.Tools.Count; i++)
                map[template.Tools[i].ToolId] = template.Tools[i];
            return map;
        }

        static Dictionary<int, FacilityData> IndexFacilities(PlayerFullData template)
        {
            var map = new Dictionary<int, FacilityData>(template.Facilities.Count);
            for (int i = 0; i < template.Facilities.Count; i++)
                map[template.Facilities[i].FacilityId] = template.Facilities[i];
            return map;
        }

        static Dictionary<int, ItemStack> IndexBag(PlayerFullData template)
        {
            var map = new Dictionary<int, ItemStack>(template.Bag.Count);
            for (int i = 0; i < template.Bag.Count; i++)
                map[template.Bag[i].ItemId] = template.Bag[i];
            return map;
        }

        /// <summary>非法枚举拒写；禁止 Unknown→Locked 静默修复。</summary>
        static DomainPlotState MapPlotStateStrict(PlotState state)
        {
            switch (state)
            {
                case PlotState.PlotLocked: return DomainPlotState.Locked;
                case PlotState.PlotEmpty: return DomainPlotState.Empty;
                case PlotState.PlotGrowing: return DomainPlotState.Growing;
                case PlotState.PlotRipe: return DomainPlotState.Ripe;
                default:
                    throw new InvalidOperationException(
                        "invalid-plot-state:" + ((int)state).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        static PlotState MapPlotState(DomainPlotState state)
        {
            switch (state)
            {
                case DomainPlotState.Locked: return PlotState.PlotLocked;
                case DomainPlotState.Empty: return PlotState.PlotEmpty;
                case DomainPlotState.Growing: return PlotState.PlotGrowing;
                case DomainPlotState.Ripe: return PlotState.PlotRipe;
                default:
                    throw new InvalidOperationException(
                        "invalid-domain-plot-state:" + ((int)state).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
