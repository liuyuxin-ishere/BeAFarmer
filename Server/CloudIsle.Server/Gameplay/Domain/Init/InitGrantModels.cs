using System;
using System.Collections;
using System.Collections.Generic;

namespace CloudIsle.Domain.Init
{
    public readonly struct MapPlotView
    {
        public MapPlotView(int instanceId) { InstanceId = instanceId; }
        public int InstanceId { get; }
    }

    public readonly struct MapNodeView
    {
        public MapNodeView(int instanceId, int gatherNodeTypeId)
        {
            InstanceId = instanceId;
            GatherNodeTypeId = gatherNodeTypeId;
        }

        public int InstanceId { get; }
        public int GatherNodeTypeId { get; }
    }

    /// <summary>深只读初始化规格；不暴露可变 ISet/List 原件。</summary>
    public sealed class InitConfigView
    {
        readonly HashSet<int> _knownLookup;
        readonly int[] _knownIds;

        public InitConfigView(
            int initialPlotCount,
            IReadOnlyList<int> starterTools,
            IReadOnlyList<int> starterSeeds,
            IReadOnlyList<int> unlockGiftTools,
            int grantSchemaVersion,
            IReadOnlyList<MapPlotView> plots,
            IReadOnlyList<MapNodeView> nodes,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IEnumerable<int> knownItemIds,
            int stackDefault)
        {
            if (initialPlotCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialPlotCount));
            if (grantSchemaVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(grantSchemaVersion));
            if (plots == null || plots.Count == 0)
                throw new ArgumentException("plots required", nameof(plots));
            if (nodes == null)
                throw new ArgumentNullException(nameof(nodes));
            if (starterTools == null)
                throw new ArgumentNullException(nameof(starterTools));
            if (starterSeeds == null)
                throw new ArgumentNullException(nameof(starterSeeds));
            if (unlockGiftTools == null)
                throw new ArgumentNullException(nameof(unlockGiftTools));
            if (itemUnlockLevels == null)
                throw new ArgumentNullException(nameof(itemUnlockLevels));
            if (knownItemIds == null)
                throw new ArgumentNullException(nameof(knownItemIds));
            if (stackDefault <= 0)
                throw new ArgumentOutOfRangeException(nameof(stackDefault));
            if (initialPlotCount > plots.Count)
                throw new ArgumentException("initialPlotCount exceeds map plot instances");

            InitialPlotCount = initialPlotCount;
            GrantSchemaVersion = grantSchemaVersion;
            StackDefault = stackDefault;
            StarterTools = CopyInts(starterTools);
            StarterSeeds = CopyInts(starterSeeds);
            UnlockGiftTools = CopyInts(unlockGiftTools);
            Plots = CopyPlots(plots);
            Nodes = CopyNodes(nodes);
            ItemUnlockLevels = CopyIntMap(itemUnlockLevels);
            _knownLookup = new HashSet<int>(knownItemIds);
            _knownIds = new int[_knownLookup.Count];
            _knownLookup.CopyTo(_knownIds);
            Array.Sort(_knownIds);
            KnownItemIds = new FrozenIntIds(_knownIds, _knownLookup);
        }

        public int InitialPlotCount { get; }
        public IReadOnlyList<int> StarterTools { get; }
        public IReadOnlyList<int> StarterSeeds { get; }
        public IReadOnlyList<int> UnlockGiftTools { get; }
        public int GrantSchemaVersion { get; }
        public IReadOnlyList<MapPlotView> Plots { get; }
        public IReadOnlyList<MapNodeView> Nodes { get; }
        public IReadOnlyDictionary<int, int> ItemUnlockLevels { get; }
        /// <summary>只读 ID 集合；不可强转为可变 IList/IDictionary。</summary>
        public FrozenIntIds KnownItemIds { get; }
        public int StackDefault { get; }

        public bool IsKnownItem(int itemId) => _knownLookup.Contains(itemId);

        static IReadOnlyList<int> CopyInts(IReadOnlyList<int> source)
        {
            var arr = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<MapPlotView> CopyPlots(IReadOnlyList<MapPlotView> source)
        {
            var arr = new MapPlotView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<MapNodeView> CopyNodes(IReadOnlyList<MapNodeView> source)
        {
            var arr = new MapNodeView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyDictionary<int, int> CopyIntMap(IReadOnlyDictionary<int, int> source)
        {
            var map = new Dictionary<int, int>(source.Count);
            foreach (KeyValuePair<int, int> pair in source)
                map.Add(pair.Key, pair.Value);
            return new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(map);
        }
    }

    /// <summary>不可变 int 集合视图；Contains 可用；禁止当作可写集合。</summary>
    public sealed class FrozenIntIds : IReadOnlyList<int>
    {
        readonly int[] _ids;
        readonly HashSet<int> _lookup;

        internal FrozenIntIds(int[] ids, HashSet<int> lookup)
        {
            _ids = ids;
            _lookup = lookup;
        }

        public int Count => _ids.Length;
        public int this[int index] => _ids[index];
        public bool Contains(int id) => _lookup.Contains(id);
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)_ids).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _ids.GetEnumerator();
    }

    public readonly struct InitPlotCandidate
    {
        public InitPlotCandidate(int plotId, bool unlockedEmpty)
        {
            PlotId = plotId;
            UnlockedEmpty = unlockedEmpty;
        }

        public int PlotId { get; }
        public bool UnlockedEmpty { get; }
    }

    public readonly struct InitNodeCandidate
    {
        public InitNodeCandidate(int nodeId, int configNodeId)
        {
            NodeId = nodeId;
            ConfigNodeId = configNodeId;
        }

        public int NodeId { get; }
        public int ConfigNodeId { get; }
    }

    public readonly struct InitToolCandidate
    {
        public InitToolCandidate(int toolId, int level)
        {
            ToolId = toolId;
            Level = level;
        }

        public int ToolId { get; }
        public int Level { get; }
    }

    public sealed class InitGrantCandidate
    {
        public InitGrantCandidate(
            int targetGrantVersion,
            bool isNoOp,
            IReadOnlyList<InitPlotCandidate> plots,
            IReadOnlyList<InitNodeCandidate> nodes,
            IReadOnlyList<InitToolCandidate> toolsToEnsure,
            IReadOnlyList<string> diagnostics)
        {
            TargetGrantVersion = targetGrantVersion;
            IsNoOp = isNoOp;
            Plots = CopyPlots(plots);
            Nodes = CopyNodes(nodes);
            ToolsToEnsure = CopyTools(toolsToEnsure);
            Diagnostics = CopyStrings(diagnostics);
        }

        public int TargetGrantVersion { get; }
        public bool IsNoOp { get; }
        public IReadOnlyList<InitPlotCandidate> Plots { get; }
        public IReadOnlyList<InitNodeCandidate> Nodes { get; }
        public IReadOnlyList<InitToolCandidate> ToolsToEnsure { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        static IReadOnlyList<InitPlotCandidate> CopyPlots(IReadOnlyList<InitPlotCandidate> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<InitPlotCandidate>();
            var arr = new InitPlotCandidate[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<InitNodeCandidate> CopyNodes(IReadOnlyList<InitNodeCandidate> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<InitNodeCandidate>();
            var arr = new InitNodeCandidate[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<InitToolCandidate> CopyTools(IReadOnlyList<InitToolCandidate> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<InitToolCandidate>();
            var arr = new InitToolCandidate[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<string> CopyStrings(IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<string>();
            var arr = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }
}
