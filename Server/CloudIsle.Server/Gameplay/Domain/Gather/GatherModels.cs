using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;

namespace CloudIsle.Domain.Gather
{
    /// <summary>采集意图：仅稳定 node_instance_id；类型/工具/分布由服务端规则给出。</summary>
    public readonly struct GatherIntent
    {
        public GatherIntent(int nodeInstanceId)
        {
            NodeInstanceId = nodeInstanceId;
        }

        public int NodeInstanceId { get; }
    }

    /// <summary>
    /// 采集规则输入：等级/节点/工具视图；不持有可变聚合引用。
    /// 背包容量由 Composer 统一校验，本输入不携带袋态。
    /// </summary>
    public sealed class GatherInput
    {
        public GatherInput(
            int level,
            IReadOnlyList<NodeStateView> nodes,
            IReadOnlyList<ToolStateView> tools)
        {
            if (level < 0)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (nodes == null)
                throw new ArgumentNullException(nameof(nodes));
            if (tools == null)
                throw new ArgumentNullException(nameof(tools));

            Level = level;
            Nodes = FreezeNodes(nodes);
            Tools = FreezeTools(tools);
        }

        public int Level { get; }
        public IReadOnlyList<NodeStateView> Nodes { get; }
        public IReadOnlyList<ToolStateView> Tools { get; }

        public static GatherInput FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return new GatherInput(aggregate.Level, aggregate.Nodes, aggregate.Tools);
        }

        public long Fingerprint()
        {
            unchecked
            {
                long h = Level;
                h = (h * 397) ^ Nodes.Count;
                for (int i = 0; i < Nodes.Count; i++)
                {
                    NodeStateView n = Nodes[i];
                    h = (h * 397) ^ n.NodeId;
                    h = (h * 397) ^ n.ConfigNodeId;
                    h = (h * 397) ^ n.NextReadyTime;
                }

                h = (h * 397) ^ Tools.Count;
                for (int i = 0; i < Tools.Count; i++)
                {
                    ToolStateView t = Tools[i];
                    h = (h * 397) ^ t.ToolId;
                    h = (h * 397) ^ t.Level;
                }

                return h;
            }
        }

        static IReadOnlyList<NodeStateView> FreezeNodes(IReadOnlyList<NodeStateView> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<NodeStateView>());
            var arr = new NodeStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<ToolStateView> FreezeTools(IReadOnlyList<ToolStateView> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<ToolStateView>());
            var arr = new ToolStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的采集节点/工具等级视图；
    /// Authorize 与规则查询共用同一快照，禁止命令内重读磁盘。
    /// </summary>
    public sealed class GatherRules
    {
        GatherRules(GameConfigSnapshot snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            ConfigVersion = snapshot.Version;
            Nodes = snapshot.GatherNodes;
            ToolLevels = snapshot.ToolLevels;
        }

        public GameConfigSnapshot Snapshot { get; }
        public int ConfigVersion { get; }
        public IReadOnlyDictionary<int, GatherNodeSpec> Nodes { get; }
        public IReadOnlyDictionary<long, ToolLevelSpec> ToolLevels { get; }

        public static GatherRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.GatherNodes.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing GatherNodes; use LoadDirectory");
            if (snapshot.ToolLevels.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing ToolLevels; use LoadDirectory");
            return new GatherRules(snapshot);
        }

        public bool TryGetNode(int nodeTypeId, out GatherNodeSpec node)
            => Snapshot.TryGetGatherNode(nodeTypeId, out node);

        public bool TryGetToolLevel(int toolId, int level, out ToolLevelSpec tool)
            => Snapshot.TryGetToolLevel(toolId, level, out tool);
    }

    /// <summary>采集命令载体（应用层入口）。</summary>
    public sealed class GatherCommand
    {
        public GatherCommand(GatherIntent intent)
        {
            Intent = intent;
        }

        public GatherIntent Intent { get; }
    }

    /// <summary>
    /// 纯采集结果：成功含普通/稀有/经验/新冷却与 RNG 次数；资格-only 成功无结算字段。
    /// </summary>
    public sealed class GatherResult
    {
        GatherResult(
            bool success,
            bool hasSettlement,
            int code,
            NodeStateView? nodeAfter,
            int ordinaryItemId,
            int ordinaryCount,
            int rareItemId,
            int rareCount,
            int expGained,
            int toolLevel,
            int effectiveYieldMax,
            bool rareHit,
            int rngCalls,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            HasSettlement = hasSettlement;
            Code = code;
            NodeAfter = nodeAfter;
            OrdinaryItemId = ordinaryItemId;
            OrdinaryCount = ordinaryCount;
            RareItemId = rareItemId;
            RareCount = rareCount;
            ExpGained = expGained;
            ToolLevel = toolLevel;
            EffectiveYieldMax = effectiveYieldMax;
            RareHit = rareHit;
            RngCalls = rngCalls;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public bool HasSettlement { get; }
        public int Code { get; }
        public NodeStateView? NodeAfter { get; }
        public int OrdinaryItemId { get; }
        public int OrdinaryCount { get; }
        public int RareItemId { get; }
        public int RareCount { get; }
        public int ExpGained { get; }
        public int ToolLevel { get; }
        public int EffectiveYieldMax { get; }
        public bool RareHit { get; }
        public int RngCalls { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static GatherResult Eligible(IReadOnlyList<string>? diagnostics = null)
        {
            return new GatherResult(
                true, hasSettlement: false, 0, nodeAfter: null,
                ordinaryItemId: 0, ordinaryCount: 0, rareItemId: 0, rareCount: 0,
                expGained: 0, toolLevel: 0, effectiveYieldMax: 0, rareHit: false,
                rngCalls: 0, FreezeDiagnostics(diagnostics));
        }

        public static GatherResult Ok(
            NodeStateView nodeAfter,
            int ordinaryItemId,
            int ordinaryCount,
            int rareItemId,
            int rareCount,
            int expGained,
            int toolLevel,
            int effectiveYieldMax,
            bool rareHit,
            int rngCalls,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (ordinaryItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(ordinaryItemId));
            if (ordinaryCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(ordinaryCount));
            if (rareCount < 0 || (rareHit && (rareItemId <= 0 || rareCount != 1)) || (!rareHit && rareCount != 0))
                throw new ArgumentOutOfRangeException(nameof(rareCount));
            if (expGained < 0)
                throw new ArgumentOutOfRangeException(nameof(expGained));
            if (toolLevel <= 0)
                throw new ArgumentOutOfRangeException(nameof(toolLevel));
            if (rngCalls < 0)
                throw new ArgumentOutOfRangeException(nameof(rngCalls));
            return new GatherResult(
                true, hasSettlement: true, 0, nodeAfter,
                ordinaryItemId, ordinaryCount, rareItemId, rareCount,
                expGained, toolLevel, effectiveYieldMax, rareHit, rngCalls,
                FreezeDiagnostics(diagnostics));
        }

        public static GatherResult Fail(
            int code,
            IReadOnlyList<string>? diagnostics = null,
            int rngCalls = 0)
        {
            if (rngCalls < 0)
                throw new ArgumentOutOfRangeException(nameof(rngCalls));
            return new GatherResult(
                false, hasSettlement: false, code, nodeAfter: null,
                ordinaryItemId: 0, ordinaryCount: 0, rareItemId: 0, rareCount: 0,
                expGained: 0, toolLevel: 0, effectiveYieldMax: 0, rareHit: false,
                rngCalls, FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<string> FreezeDiagnostics(IReadOnlyList<string>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<string>());
            var arr = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i] ?? "";
            return Array.AsReadOnly(arr);
        }
    }
}
