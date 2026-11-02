using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CloudIsle.Config;
using CloudIsle.Domain.Init;

namespace CloudIsle.Domain.Inventory
{
    /// <summary>单物品堆叠视图（最终数量；count=0 表示移除）。</summary>
    public readonly struct InventoryStackView : IEquatable<InventoryStackView>
    {
        public InventoryStackView(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }

        public bool Equals(InventoryStackView other) => ItemId == other.ItemId && Count == other.Count;

        public override bool Equals(object obj) => obj is InventoryStackView other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (ItemId * 397) ^ Count;
            }
        }
    }

    /// <summary>物品数量变化；同 ID 由 Policy 先聚合再验最终态。正数入包，负数花费。</summary>
    public readonly struct ItemDelta
    {
        public ItemDelta(int itemId, long delta)
        {
            ItemId = itemId;
            Delta = delta;
        }

        public int ItemId { get; }
        public long Delta { get; }
    }

    /// <summary>
    /// 只读库存快照。不别名调用方集合；不增 revision、不 Save、不发 Ntf。
    /// </summary>
    public sealed class InventoryState
    {
        public InventoryState(int bagSlots, IReadOnlyList<InventoryStackView> stacks)
        {
            if (bagSlots <= 0)
                throw new ArgumentOutOfRangeException(nameof(bagSlots));
            BagSlots = bagSlots;
            Stacks = FreezeStacks(stacks);
        }

        public int BagSlots { get; }

        /// <summary>按 itemId 升序；每 ID 至多一条；count&gt;0。</summary>
        public IReadOnlyList<InventoryStackView> Stacks { get; }

        public static InventoryState FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            var stacks = new InventoryStackView[aggregate.Bag.Count];
            for (int i = 0; i < aggregate.Bag.Count; i++)
            {
                BagItemView bag = aggregate.Bag[i];
                stacks[i] = new InventoryStackView(bag.ItemId, bag.Count);
            }
            return new InventoryState(aggregate.BagSlots, stacks);
        }

        /// <summary>全字段指纹：失败路径前后比对用。</summary>
        public long Fingerprint()
        {
            unchecked
            {
                long h = BagSlots * 397L;
                for (int i = 0; i < Stacks.Count; i++)
                {
                    InventoryStackView s = Stacks[i];
                    h = (h * 31) + s.ItemId;
                    h = (h * 31) + s.Count;
                }
                return h;
            }
        }

        static IReadOnlyList<InventoryStackView> FreezeStacks(IReadOnlyList<InventoryStackView> source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<InventoryStackView>());
            var arr = new InventoryStackView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>
    /// Item/stack 规则与输入规模上限。来自同份配置快照；构造后调用方改源集合不影响本实例。
    /// </summary>
    public sealed class InventoryRules
    {
        public const int DefaultMaxDeltaEntries = 256;
        public const int DefaultMaxBagSlots = 512;

        readonly HashSet<int> _known;
        readonly IReadOnlyDictionary<int, int> _stackMax;

        public InventoryRules(
            IReadOnlyDictionary<int, int> stackMaxByItemId,
            int stackDefault,
            int maxDeltaEntries = DefaultMaxDeltaEntries,
            int maxBagSlots = DefaultMaxBagSlots)
        {
            if (stackMaxByItemId == null)
                throw new ArgumentNullException(nameof(stackMaxByItemId));
            if (stackDefault <= 0)
                throw new ArgumentOutOfRangeException(nameof(stackDefault));
            if (maxDeltaEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDeltaEntries));
            if (maxBagSlots <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBagSlots));

            var copy = new Dictionary<int, int>(stackMaxByItemId.Count);
            _known = new HashSet<int>();
            foreach (KeyValuePair<int, int> pair in stackMaxByItemId)
            {
                if (pair.Key <= 0)
                    throw new ArgumentException("itemId 必须为正", nameof(stackMaxByItemId));
                if (pair.Value <= 0)
                    throw new ArgumentException("stackMax 必须为正", nameof(stackMaxByItemId));
                copy.Add(pair.Key, pair.Value);
                _known.Add(pair.Key);
            }

            _stackMax = new ReadOnlyDictionary<int, int>(copy);
            StackDefault = stackDefault;
            MaxDeltaEntries = maxDeltaEntries;
            MaxBagSlots = maxBagSlots;
            KnownItemIds = Array.AsReadOnly(SortedIds(_known));
        }

        public int StackDefault { get; }
        public int MaxDeltaEntries { get; }
        public int MaxBagSlots { get; }
        public IReadOnlyList<int> KnownItemIds { get; }
        public IReadOnlyDictionary<int, int> StackMaxByItemId { get { return _stackMax; } }

        public bool IsKnownItem(int itemId) => _known.Contains(itemId);

        public bool TryGetStackMax(int itemId, out int stackMax) => _stackMax.TryGetValue(itemId, out stackMax);

        public static InventoryRules FromSnapshot(
            GameConfigSnapshot snapshot,
            int maxDeltaEntries = DefaultMaxDeltaEntries,
            int maxBagSlots = DefaultMaxBagSlots)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            // ItemStackMax 仅为物理背包面（不含 Currency）；Currency Delta 经 IsKnownItem=false → 306
            if (snapshot.ItemStackMax.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing ItemStackMax; use LoadDirectory");
            return new InventoryRules(snapshot.ItemStackMax, snapshot.StackDefault, maxDeltaEntries, maxBagSlots);
        }

        static int[] SortedIds(HashSet<int> set)
        {
            var arr = new int[set.Count];
            set.CopyTo(arr);
            Array.Sort(arr);
            return arr;
        }
    }

    /// <summary>
    /// 纯候选结果：供 EffectComposer（T020）消费。成功含 Candidate/Diffs；失败 Candidate=null 且不别名源。
    /// </summary>
    public sealed class InventoryResult
    {
        InventoryResult(
            bool success,
            int code,
            InventoryState? candidate,
            IReadOnlyList<InventoryStackView> diffs,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            Candidate = candidate;
            Diffs = diffs ?? Array.AsReadOnly(Array.Empty<InventoryStackView>());
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        public InventoryState? Candidate { get; }
        /// <summary>变化后最终数量；count=0 表示移除。按 itemId 升序。</summary>
        public IReadOnlyList<InventoryStackView> Diffs { get; }
        /// <summary>legacy 保留等诊断；禁止静默丢弃未知旧条目。</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        public static InventoryResult Ok(
            InventoryState candidate,
            IReadOnlyList<InventoryStackView> diffs,
            IReadOnlyList<string> diagnostics)
        {
            return new InventoryResult(true, 0, candidate, FreezeDiffs(diffs), FreezeDiagnostics(diagnostics));
        }

        public static InventoryResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new InventoryResult(
                false,
                code,
                null,
                Array.AsReadOnly(Array.Empty<InventoryStackView>()),
                FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<InventoryStackView> FreezeDiffs(IReadOnlyList<InventoryStackView>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<InventoryStackView>());
            var arr = new InventoryStackView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
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
