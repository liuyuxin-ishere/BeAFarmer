using System;
using System.Collections.Generic;
using System.Globalization;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Net;

namespace CloudIsle.Domain.Gather
{
    /// <summary>
    /// D09/T037：按单次不可变 <see cref="GameConfigSnapshot"/> 解析稳定地图实例。
    /// node_instance_id = MapInstance.instanceId；ConfigNodeId = GatherNode.id。
    /// 未知 / 跨 Kind / 存档类型冲突明确拒绝，不创建状态、不静默改写冷却或类型。
    /// Facility 仅提供映射入口，不加工业务。布局数量校验沿用 LoadMap，本类型不另立分歧规则。
    /// </summary>
    public static class GameplayInstanceRegistry
    {
        public const string KindPlot = "Plot";
        public const string KindNode = "Node";
        public const string KindFacility = "Facility";

        public const string DiagUnknownInstance = "instance-unknown";
        public const string DiagCrossKind = "instance-cross-kind";
        public const string DiagTypeConflict = "node-type-conflict";
        public const string DiagMissingNodeState = "node-state-missing";
        public const string DiagDuplicateNodeState = "node-state-duplicate";
        public const string DiagInvalidNextReadyTime = "node-next-ready-invalid";
        public const string DiagInvalidInstanceId = "instance-id-invalid";

        /// <summary>
        /// 稳定 instanceId → 采集类型。未知、非正 ID、跨 Kind（Plot/Facility）拒绝并抛错；不创建状态。
        /// </summary>
        public static NodeSpec ResolveNode(int instanceId, GameConfigSnapshot config)
        {
            MapInstanceRow row = RequireMappedRow(instanceId, config, KindNode, "ResolveNode");
            if (row.TypeId <= 0)
            {
                throw new InvalidOperationException(
                    DiagUnknownInstance
                    + ":"
                    + instanceId.ToString(CultureInfo.InvariantCulture)
                    + ";reason=non-positive-type");
            }

            return new NodeSpec(row.InstanceId, row.TypeId);
        }

        /// <summary>
        /// 稳定 instanceId → 设施类型。未知/跨 Kind 拒绝；不创建设施实体、不加工业务。
        /// </summary>
        public static FacilitySpec ResolveFacility(int instanceId, GameConfigSnapshot config)
        {
            MapInstanceRow row = RequireMappedRow(instanceId, config, KindFacility, "ResolveFacility");
            if (row.TypeId <= 0)
            {
                throw new InvalidOperationException(
                    DiagUnknownInstance
                    + ":"
                    + instanceId.ToString(CultureInfo.InvariantCulture)
                    + ";reason=non-positive-type");
            }

            return new FacilitySpec(row.InstanceId, row.TypeId);
        }

        /// <summary>
        /// 命令使用入口：正式映射 + 存档冷却身份。
        /// 未知旧实例 / 跨 Kind / 缺存档 / 同 instanceId 重复存档行 / NextReadyTime&lt;0 /
        /// 存档类型与正式映射冲突 → 拒绝（BadPacket），不改写付费/冷却/类型、不去重洗数据。
        /// 成功时 Spec.ConfigNodeId 取正式映射，NextReadyTime 取该 instance 唯一存档值（同类实例互不影响）。
        /// </summary>
        public static NodeAuthorizeResult AuthorizeNodeUse(
            int instanceId,
            IReadOnlyList<NodeStateView> nodes,
            GameConfigSnapshot config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (nodes == null)
                throw new ArgumentNullException(nameof(nodes));

            var diagnostics = new List<string>(2);
            if (instanceId <= 0)
            {
                diagnostics.Add(DiagInvalidInstanceId + ":" + instanceId.ToString(CultureInfo.InvariantCulture));
                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!config.MapInstances.TryGetValue(instanceId, out MapInstanceRow row))
            {
                diagnostics.Add(DiagUnknownInstance + ":" + instanceId.ToString(CultureInfo.InvariantCulture));
                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!string.Equals(row.Kind, KindNode, StringComparison.Ordinal))
            {
                diagnostics.Add(FormatCrossKind(instanceId, row.Kind, KindNode));
                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (row.TypeId <= 0)
            {
                diagnostics.Add(DiagUnknownInstance + ":" + instanceId.ToString(CultureInfo.InvariantCulture));
                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryFindUniqueSavedNode(nodes, instanceId, out NodeStateView saved, out int matchCount))
            {
                if (matchCount > 1)
                {
                    diagnostics.Add(
                        DiagDuplicateNodeState
                        + ":"
                        + instanceId.ToString(CultureInfo.InvariantCulture)
                        + ";count="
                        + matchCount.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    diagnostics.Add(DiagMissingNodeState + ":" + instanceId.ToString(CultureInfo.InvariantCulture));
                }

                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (saved.ConfigNodeId != row.TypeId)
            {
                diagnostics.Add(FormatTypeConflict(instanceId, saved.ConfigNodeId, row.TypeId));
                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (saved.NextReadyTime < 0)
            {
                diagnostics.Add(
                    DiagInvalidNextReadyTime
                    + ":"
                    + instanceId.ToString(CultureInfo.InvariantCulture)
                    + ";nextReady="
                    + saved.NextReadyTime.ToString(CultureInfo.InvariantCulture));
                return NodeAuthorizeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            return NodeAuthorizeResult.Ok(new NodeSpec(row.InstanceId, row.TypeId), saved.NextReadyTime, diagnostics);
        }

        /// <summary>
        /// 枚举正式 Node 映射（按 instanceId 升序）。输入 MapInstances 枚举次序不影响结果。
        /// </summary>
        public static IReadOnlyList<NodeSpec> ListNodes(GameConfigSnapshot config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var list = new List<NodeSpec>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in config.MapInstances)
            {
                MapInstanceRow row = pair.Value;
                if (string.Equals(row.Kind, KindNode, StringComparison.Ordinal) && row.TypeId > 0)
                    list.Add(new NodeSpec(row.InstanceId, row.TypeId));
            }

            list.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));
            return Array.AsReadOnly(list.ToArray());
        }

        /// <summary>
        /// 枚举正式 Facility 映射（按 instanceId 升序）；仅映射，不加工业务。
        /// </summary>
        public static IReadOnlyList<FacilitySpec> ListFacilities(GameConfigSnapshot config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var list = new List<FacilitySpec>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in config.MapInstances)
            {
                MapInstanceRow row = pair.Value;
                if (string.Equals(row.Kind, KindFacility, StringComparison.Ordinal) && row.TypeId > 0)
                    list.Add(new FacilitySpec(row.InstanceId, row.TypeId));
            }

            list.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));
            return Array.AsReadOnly(list.ToArray());
        }

        /// <summary>
        /// 统计快照中 Plot/Node/Facility 数量（只读观察；数量合法性仍以 LoadMap 为准）。
        /// </summary>
        public static void CountKinds(GameConfigSnapshot config, out int plotCount, out int nodeCount, out int facilityCount)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            int plots = 0;
            int nodes = 0;
            int facilities = 0;
            foreach (KeyValuePair<int, MapInstanceRow> pair in config.MapInstances)
            {
                string kind = pair.Value.Kind;
                if (string.Equals(kind, KindPlot, StringComparison.Ordinal))
                    plots++;
                else if (string.Equals(kind, KindNode, StringComparison.Ordinal))
                    nodes++;
                else if (string.Equals(kind, KindFacility, StringComparison.Ordinal))
                    facilities++;
            }

            plotCount = plots;
            nodeCount = nodes;
            facilityCount = facilities;
        }

        /// <summary>按 Node 类型统计正式实例数（只读；与 GatherNode.count 对齐由 LoadMap 保障）。</summary>
        public static IReadOnlyDictionary<int, int> CountNodesByType(GameConfigSnapshot config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var counts = new Dictionary<int, int>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in config.MapInstances)
            {
                MapInstanceRow row = pair.Value;
                if (!string.Equals(row.Kind, KindNode, StringComparison.Ordinal))
                    continue;
                int c;
                counts.TryGetValue(row.TypeId, out c);
                counts[row.TypeId] = c + 1;
            }

            return counts;
        }

        /// <summary>格式化类型冲突诊断（初始化与授权共用前缀）。</summary>
        public static string FormatTypeConflict(int instanceId, int savedTypeId, int canonicalTypeId)
        {
            return DiagTypeConflict
                + ":"
                + instanceId.ToString(CultureInfo.InvariantCulture)
                + ";saved="
                + savedTypeId.ToString(CultureInfo.InvariantCulture)
                + ";canonical="
                + canonicalTypeId.ToString(CultureInfo.InvariantCulture);
        }

        static string FormatCrossKind(int instanceId, string actualKind, string expectedKind)
        {
            return DiagCrossKind
                + ":"
                + instanceId.ToString(CultureInfo.InvariantCulture)
                + ";actual="
                + (actualKind ?? "")
                + ";expected="
                + expectedKind;
        }

        static MapInstanceRow RequireMappedRow(
            int instanceId,
            GameConfigSnapshot config,
            string expectedKind,
            string caller)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (instanceId <= 0)
            {
                throw new InvalidOperationException(
                    DiagInvalidInstanceId + ":" + instanceId.ToString(CultureInfo.InvariantCulture) + ";caller=" + caller);
            }

            if (!config.MapInstances.TryGetValue(instanceId, out MapInstanceRow row))
            {
                throw new InvalidOperationException(
                    DiagUnknownInstance + ":" + instanceId.ToString(CultureInfo.InvariantCulture) + ";caller=" + caller);
            }

            if (!string.Equals(row.Kind, expectedKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    FormatCrossKind(instanceId, row.Kind, expectedKind) + ";caller=" + caller);
            }

            return row;
        }

        /// <summary>
        /// 按 NodeId==instanceId 定位唯一存档行（全表扫描）。
        /// 缺行或同 id 多行均失败：多行时 matchCount&gt;1，不得取首行/去重/排序洗成合法。
        /// </summary>
        static bool TryFindUniqueSavedNode(
            IReadOnlyList<NodeStateView> nodes,
            int instanceId,
            out NodeStateView saved,
            out int matchCount)
        {
            matchCount = 0;
            saved = default;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].NodeId != instanceId)
                    continue;
                matchCount++;
                if (matchCount == 1)
                    saved = nodes[i];
            }

            return matchCount == 1;
        }
    }
}
