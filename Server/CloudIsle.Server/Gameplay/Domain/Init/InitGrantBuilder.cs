using System;
using System.Collections.Generic;
using System.Globalization;
using CloudIsle.Domain.Gather;

namespace CloudIsle.Domain.Init
{
    /// <summary>
    /// 纯规则构造初始化候选。收完整 <see cref="PlayerAggregate"/>；无 SQL/发奖。
    /// Builder 集中判断缺失：已有 Plot/Node 不再进入候选；解锁位仍按全表稳定 ID 序。
    /// grant &gt; 目标：原样保留标记并 NoOp（不降版本）；损坏重复键仍拒写。
    /// T037：未知旧节点保留并诊断；已知节点存档类型与正式映射冲突仅诊断、不改写冷却/类型；不重复发放已有实例。
    /// </summary>
    public static class InitGrantBuilder
    {
        /// <summary>与 <see cref="GameplayInstanceRegistry.DiagTypeConflict"/> 同前缀，便于授权入口共用。</summary>
        public const string DiagNodeTypeConflictPrefix = GameplayInstanceRegistry.DiagTypeConflict;
        public static InitGrantCandidate Build(
            PlayerAggregate existing,
            PlayerOrigin origin,
            InitConfigView config)
        {
            if (existing == null)
                throw new ArgumentNullException(nameof(existing));
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            ValidateConfig(config);
            // 存档损坏：NoOp 也不能掩盖
            ValidateAggregateIntegrity(existing);

            var diagnostics = new List<string>();
            DiagnoseUnknownBagItems(existing.Bag, config, diagnostics);
            DiagnoseUnknownMapInstances(existing, config, diagnostics);
            DiagnoseExistingPlots(existing.Plots, diagnostics);

            int target = config.GrantSchemaVersion;
            if (existing.GrantVersion > target)
            {
                diagnostics.Add(
                    "grant-above-target:"
                    + existing.GrantVersion.ToString(CultureInfo.InvariantCulture)
                    + ">"
                    + target.ToString(CultureInfo.InvariantCulture)
                    + ";policy=keep-no-downgrade");
            }

            if (existing.GrantVersion >= target)
            {
                return new InitGrantCandidate(
                    target,
                    isNoOp: true,
                    Array.Empty<InitPlotCandidate>(),
                    Array.Empty<InitNodeCandidate>(),
                    Array.Empty<InitToolCandidate>(),
                    diagnostics);
            }

            if (config.StarterSeeds.Count != 0)
                throw new InvalidOperationException("InitConfig.StarterSeeds must be empty for M2");

            var existingPlotIds = new HashSet<int>();
            for (int i = 0; i < existing.Plots.Count; i++)
                existingPlotIds.Add(existing.Plots[i].PlotId);

            var existingNodeIds = new HashSet<int>();
            for (int i = 0; i < existing.Nodes.Count; i++)
                existingNodeIds.Add(existing.Nodes[i].NodeId);

            // 稳定 instanceId 排序；解锁判定用全表下标，与是否缺失无关
            var sortedPlots = new List<MapPlotView>(config.Plots.Count);
            for (int i = 0; i < config.Plots.Count; i++)
                sortedPlots.Add(config.Plots[i]);
            sortedPlots.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));

            var plots = new List<InitPlotCandidate>(sortedPlots.Count);
            for (int i = 0; i < sortedPlots.Count; i++)
            {
                int id = sortedPlots[i].InstanceId;
                if (existingPlotIds.Contains(id))
                    continue; // 已有地块：不生成“新建”候选；Applicator 仍防御性保留
                bool unlocked = i < config.InitialPlotCount;
                plots.Add(new InitPlotCandidate(id, unlocked));
            }

            var sortedNodes = new List<MapNodeView>(config.Nodes.Count);
            for (int i = 0; i < config.Nodes.Count; i++)
                sortedNodes.Add(config.Nodes[i]);
            sortedNodes.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));

            var nodes = new List<InitNodeCandidate>(sortedNodes.Count);
            for (int i = 0; i < sortedNodes.Count; i++)
            {
                MapNodeView n = sortedNodes[i];
                if (existingNodeIds.Contains(n.InstanceId))
                    continue;
                nodes.Add(new InitNodeCandidate(n.InstanceId, n.GatherNodeTypeId));
            }

            IReadOnlyDictionary<int, int> toolLevels = existing.ToolLevelsById();
            var tools = new List<InitToolCandidate>();
            EnsureTools(config.StarterTools, 1, toolLevels, tools);
            for (int i = 0; i < config.UnlockGiftTools.Count; i++)
            {
                int giftId = config.UnlockGiftTools[i];
                if (!config.ItemUnlockLevels.TryGetValue(giftId, out int unlockLv) || unlockLv <= 0)
                    throw new InvalidOperationException("UnlockGiftTools missing unlockLevel: " + giftId);
                if (existing.Level >= unlockLv)
                    EnsureTools(new[] { giftId }, 1, toolLevels, tools);
            }

            _ = origin; // 供后续差异策略；当前规则统一只补缺失
            return new InitGrantCandidate(target, isNoOp: false, plots, nodes, tools, diagnostics);
        }

        /// <summary>重复键 / 非法数量 / 负 deadline：拒绝可写初始化（含 NoOp 路径）。</summary>
        public static void ValidateAggregateIntegrity(PlayerAggregate existing)
        {
            var plotIds = new HashSet<int>();
            for (int i = 0; i < existing.Plots.Count; i++)
            {
                PlotStateView p = existing.Plots[i];
                if (p.PlotId <= 0 || !plotIds.Add(p.PlotId))
                    throw new InvalidOperationException("duplicate-or-invalid-plot:" + p.PlotId.ToString(CultureInfo.InvariantCulture));
                if (p.PlantTime < 0 || p.ReadyTime < 0)
                    throw new InvalidOperationException("invalid-plot-deadline:" + p.PlotId.ToString(CultureInfo.InvariantCulture));
                if (p.State == DomainPlotState.Unknown)
                    throw new InvalidOperationException("invalid-plot-state:" + p.PlotId.ToString(CultureInfo.InvariantCulture));
            }

            var nodeIds = new HashSet<int>();
            for (int i = 0; i < existing.Nodes.Count; i++)
            {
                NodeStateView n = existing.Nodes[i];
                if (n.NodeId <= 0 || !nodeIds.Add(n.NodeId))
                    throw new InvalidOperationException("duplicate-or-invalid-node:" + n.NodeId.ToString(CultureInfo.InvariantCulture));
                if (n.NextReadyTime < 0)
                    throw new InvalidOperationException("invalid-node-deadline:" + n.NodeId.ToString(CultureInfo.InvariantCulture));
            }

            var toolIds = new HashSet<int>();
            for (int i = 0; i < existing.Tools.Count; i++)
            {
                ToolStateView t = existing.Tools[i];
                if (t.ToolId <= 0 || t.Level <= 0 || !toolIds.Add(t.ToolId))
                    throw new InvalidOperationException("duplicate-or-invalid-tool:" + t.ToolId.ToString(CultureInfo.InvariantCulture));
            }

            var facilityIds = new HashSet<int>();
            for (int i = 0; i < existing.Facilities.Count; i++)
            {
                int id = existing.Facilities[i].FacilityId;
                if (id <= 0 || !facilityIds.Add(id))
                    throw new InvalidOperationException("duplicate-or-invalid-facility:" + id.ToString(CultureInfo.InvariantCulture));
            }

            var bagKeys = new HashSet<int>();
            for (int i = 0; i < existing.Bag.Count; i++)
            {
                BagItemView item = existing.Bag[i];
                if (item.ItemId <= 0 || item.Count <= 0)
                    throw new InvalidOperationException("invalid-bag-stack:" + item.ItemId.ToString(CultureInfo.InvariantCulture));
                if (!bagKeys.Add(item.ItemId))
                    throw new InvalidOperationException("duplicate-bag-item:" + item.ItemId.ToString(CultureInfo.InvariantCulture));
            }
        }

        static void ValidateConfig(InitConfigView config)
        {
            var seenPlot = new HashSet<int>();
            for (int i = 0; i < config.Plots.Count; i++)
            {
                int id = config.Plots[i].InstanceId;
                if (id <= 0 || !seenPlot.Add(id))
                    throw new InvalidOperationException("invalid or duplicate plot instanceId");
            }

            var seenNode = new HashSet<int>();
            for (int i = 0; i < config.Nodes.Count; i++)
            {
                MapNodeView n = config.Nodes[i];
                if (n.InstanceId <= 0 || n.GatherNodeTypeId <= 0 || !seenNode.Add(n.InstanceId))
                    throw new InvalidOperationException("invalid or duplicate node instance");
            }

            for (int i = 0; i < config.StarterTools.Count; i++)
            {
                int id = config.StarterTools[i];
                if (id <= 0 || !config.IsKnownItem(id))
                    throw new InvalidOperationException("starter tool unknown item: " + id);
            }

            for (int i = 0; i < config.UnlockGiftTools.Count; i++)
            {
                int id = config.UnlockGiftTools[i];
                if (id <= 0 || !config.IsKnownItem(id))
                    throw new InvalidOperationException("unlock gift unknown item: " + id);
            }
        }

        static void DiagnoseUnknownBagItems(
            IReadOnlyList<BagItemView> bag,
            InitConfigView config,
            List<string> diagnostics)
        {
            for (int i = 0; i < bag.Count; i++)
            {
                BagItemView item = bag[i];
                if (!config.IsKnownItem(item.ItemId))
                {
                    diagnostics.Add(
                        "legacy-unknown-item:"
                        + item.ItemId.ToString(CultureInfo.InvariantCulture)
                        + "x"
                        + item.Count.ToString(CultureInfo.InvariantCulture)
                        + ";stack_default="
                        + config.StackDefault.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// 未知实例 ID（合法状态）原样保留 + 诊断；不得删除/重键。
        /// 已知节点存档 ConfigNodeId 与正式映射冲突：诊断且保留冷却/类型，不得静默改写。
        /// 设施配置类型本模块未解析（见 FacilityStateView.ConfigFacilityId=0），仅诊断存在性。
        /// </summary>
        static void DiagnoseUnknownMapInstances(
            PlayerAggregate existing,
            InitConfigView config,
            List<string> diagnostics)
        {
            var configPlots = new HashSet<int>();
            for (int i = 0; i < config.Plots.Count; i++)
                configPlots.Add(config.Plots[i].InstanceId);

            for (int i = 0; i < existing.Plots.Count; i++)
            {
                int id = existing.Plots[i].PlotId;
                if (!configPlots.Contains(id))
                    diagnostics.Add("legacy-unknown-plot-instance:" + id.ToString(CultureInfo.InvariantCulture));
            }

            var canonicalNodeTypes = new Dictionary<int, int>();
            for (int i = 0; i < config.Nodes.Count; i++)
            {
                MapNodeView n = config.Nodes[i];
                canonicalNodeTypes[n.InstanceId] = n.GatherNodeTypeId;
            }

            for (int i = 0; i < existing.Nodes.Count; i++)
            {
                NodeStateView saved = existing.Nodes[i];
                int id = saved.NodeId;
                if (!canonicalNodeTypes.TryGetValue(id, out int canonicalType))
                {
                    diagnostics.Add("legacy-unknown-node-instance:" + id.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (saved.ConfigNodeId != canonicalType)
                {
                    diagnostics.Add(
                        GameplayInstanceRegistry.FormatTypeConflict(id, saved.ConfigNodeId, canonicalType));
                }
            }

            for (int i = 0; i < existing.Tools.Count; i++)
            {
                int id = existing.Tools[i].ToolId;
                if (!config.IsKnownItem(id))
                    diagnostics.Add("legacy-unknown-tool:" + id.ToString(CultureInfo.InvariantCulture));
            }

            for (int i = 0; i < existing.Facilities.Count; i++)
            {
                // 无可信 type 映射时仅标记未解析设施实体；不删、不改键
                diagnostics.Add(
                    "facility-type-unresolved:"
                    + existing.Facilities[i].FacilityId.ToString(CultureInfo.InvariantCulture)
                    + ";configFacilityId=0");
            }
        }

        static void DiagnoseExistingPlots(IReadOnlyList<PlotStateView> plots, List<string> diagnostics)
        {
            for (int i = 0; i < plots.Count; i++)
            {
                PlotStateView p = plots[i];
                if (p.State == DomainPlotState.Growing || p.State == DomainPlotState.Ripe)
                    diagnostics.Add("preserve-in-progress-plot:" + p.PlotId.ToString(CultureInfo.InvariantCulture));
            }
        }

        static void EnsureTools(
            IReadOnlyList<int> toolIds,
            int minLevel,
            IReadOnlyDictionary<int, int> existing,
            List<InitToolCandidate> sink)
        {
            for (int i = 0; i < toolIds.Count; i++)
            {
                int id = toolIds[i];
                if (existing.TryGetValue(id, out int have) && have >= minLevel)
                    continue;
                bool listed = false;
                for (int j = 0; j < sink.Count; j++)
                {
                    if (sink[j].ToolId == id)
                    {
                        listed = true;
                        break;
                    }
                }

                if (!listed)
                    sink.Add(new InitToolCandidate(id, minLevel));
            }
        }
    }
}
