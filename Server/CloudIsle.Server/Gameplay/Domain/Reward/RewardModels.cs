using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;

namespace CloudIsle.Domain.Reward
{
    /// <summary>货币种类（对应 Item Currency 9001/9002/9003/9101 → 聚合字段）。</summary>
    public enum CurrencyKind
    {
        Gold = 0,
        Gem = 1,
        Ticket = 2,
        Shard = 3,
    }

    /// <summary>非负物品数量（奖励或消耗规格）；构造不接受负数。</summary>
    public readonly struct ItemAmount : IEquatable<ItemAmount>
    {
        public ItemAmount(int itemId, long amount)
        {
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId));
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "数量不能为负");
            ItemId = itemId;
            Amount = amount;
        }

        public int ItemId { get; }
        public long Amount { get; }

        public bool Equals(ItemAmount other) => ItemId == other.ItemId && Amount == other.Amount;
        public override bool Equals(object? obj) => obj is ItemAmount other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ItemId, Amount);
    }

    /// <summary>
    /// 单来源奖励包。货币/经验不占格；Exp 由 Composer 汇总后交 ProgressionPolicy（负值逐来源拒绝）。
    /// 货币须走 Gold/Gem/Ticket/Shard 字段；不得把 Currency Item 塞进 Items。
    /// </summary>
    public sealed class RewardBundle
    {
        public RewardBundle(
            long gold = 0,
            long gem = 0,
            long ticket = 0,
            long shard = 0,
            IReadOnlyList<ItemAmount>? items = null,
            long exp = 0)
        {
            Gold = gold;
            Gem = gem;
            Ticket = ticket;
            Shard = shard;
            Exp = exp;
            Items = FreezeItems(items);
        }

        public long Gold { get; }
        public long Gem { get; }
        public long Ticket { get; }
        public long Shard { get; }
        public long Exp { get; }
        public IReadOnlyList<ItemAmount> Items { get; }

        public static RewardBundle Empty { get; } = new();

        static IReadOnlyList<ItemAmount> FreezeItems(IReadOnlyList<ItemAmount>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<ItemAmount>());
            var arr = new ItemAmount[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>经济快照：货币 + 库存；不别名源集合。</summary>
    public sealed class EconomyState
    {
        public EconomyState(long gold, long gem, long ticket, long shard, InventoryState inventory)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            Gold = gold;
            Gem = gem;
            Ticket = ticket;
            Shard = shard;
            Inventory = inventory;
        }

        public long Gold { get; }
        public long Gem { get; }
        public long Ticket { get; }
        public long Shard { get; }
        public InventoryState Inventory { get; }

        public static EconomyState FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return new EconomyState(
                aggregate.Gold,
                aggregate.Gem,
                aggregate.Ticket,
                aggregate.Shard,
                InventoryState.FromAggregate(aggregate));
        }

        public long Fingerprint()
        {
            unchecked
            {
                long h = Gold;
                h = (h * 31) + Gem;
                h = (h * 31) + Ticket;
                h = (h * 31) + Shard;
                h = (h * 31) + Inventory.Fingerprint();
                return h;
            }
        }
    }

    /// <summary>
    /// D03 成长结果：success/code + detached level/exp + 排序解锁 + 一次性赠品/工具。
    /// RewardPolicy 仅合并 <see cref="LevelGifts"/>；等级/经验/工具由 Composer 写入候选。
    /// </summary>
    public sealed class ProgressionResult
    {
        ProgressionResult(
            bool success,
            int code,
            bool hasProgression,
            int levelAfter,
            int expAfter,
            RewardBundle? levelGifts,
            IReadOnlyList<int> newlyUnlockedItemIds,
            IReadOnlyList<CloudIsle.Domain.Init.ToolStateView> toolsToEnsure,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            HasProgression = hasProgression;
            LevelAfter = levelAfter;
            ExpAfter = expAfter;
            LevelGifts = levelGifts;
            NewlyUnlockedItemIds = newlyUnlockedItemIds
                ?? Array.AsReadOnly(Array.Empty<int>());
            ToolsToEnsure = toolsToEnsure
                ?? Array.AsReadOnly(Array.Empty<CloudIsle.Domain.Init.ToolStateView>());
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        /// <summary>无成长副作用（Reward 侧不合并赠品）。</summary>
        public static ProgressionResult None { get; } = new(
            true,
            0,
            false,
            0,
            0,
            null,
            Array.AsReadOnly(Array.Empty<int>()),
            Array.AsReadOnly(Array.Empty<CloudIsle.Domain.Init.ToolStateView>()),
            Array.AsReadOnly(Array.Empty<string>()));

        public bool Success { get; }
        public int Code { get; }
        public bool HasProgression { get; }
        public int LevelAfter { get; }
        public int ExpAfter { get; }
        public RewardBundle? LevelGifts { get; }
        public IReadOnlyList<int> NewlyUnlockedItemIds { get; }
        public IReadOnlyList<CloudIsle.Domain.Init.ToolStateView> ToolsToEnsure { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static ProgressionResult Ok(
            int levelAfter,
            int expAfter,
            IReadOnlyList<int> newlyUnlockedItemIds,
            RewardBundle? levelGifts,
            IReadOnlyList<CloudIsle.Domain.Init.ToolStateView> toolsToEnsure,
            bool hasProgression,
            IReadOnlyList<string>? diagnostics = null)
        {
            return new ProgressionResult(
                true,
                0,
                hasProgression,
                levelAfter,
                expAfter,
                levelGifts,
                FreezeInts(newlyUnlockedItemIds),
                FreezeTools(toolsToEnsure),
                FreezeDiagnostics(diagnostics));
        }

        public static ProgressionResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new ProgressionResult(
                false,
                code,
                false,
                0,
                0,
                null,
                Array.AsReadOnly(Array.Empty<int>()),
                Array.AsReadOnly(Array.Empty<CloudIsle.Domain.Init.ToolStateView>()),
                FreezeDiagnostics(diagnostics));
        }

        /// <summary>测试/Reward 合并路径工厂：标记有成长赠品。</summary>
        public static ProgressionResult CreateForT021(
            int levelAfter,
            int expAfter,
            RewardBundle? levelGifts)
        {
            return Ok(
                levelAfter,
                expAfter,
                Array.AsReadOnly(Array.Empty<int>()),
                levelGifts,
                Array.AsReadOnly(Array.Empty<CloudIsle.Domain.Init.ToolStateView>()),
                hasProgression: true);
        }

        static IReadOnlyList<int> FreezeInts(IReadOnlyList<int>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<int>());
            var arr = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<CloudIsle.Domain.Init.ToolStateView> FreezeTools(
            IReadOnlyList<CloudIsle.Domain.Init.ToolStateView>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<CloudIsle.Domain.Init.ToolStateView>());
            var arr = new CloudIsle.Domain.Init.ToolStateView[source.Count];
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

    /// <summary>
    /// 奖励规则：同份配置快照的子视图。Currency = Known 且无物理 stackMax；不回落 stack_default。
    /// </summary>
    public sealed class RewardRules
    {
        public const int DefaultMaxRewardEntries = 256;
        public const int DefaultMaxCostEntries = 256;

        readonly Dictionary<int, CurrencyKind> _currencyByItemId;
        readonly HashSet<int> _currencyIds;

        public RewardRules(
            InventoryRules inventory,
            IReadOnlyDictionary<int, CurrencyKind> currencyByItemId,
            int maxRewardEntries = DefaultMaxRewardEntries,
            int maxCostEntries = DefaultMaxCostEntries)
        {
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            if (currencyByItemId == null)
                throw new ArgumentNullException(nameof(currencyByItemId));
            if (maxRewardEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxRewardEntries));
            if (maxCostEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCostEntries));

            _currencyByItemId = new Dictionary<int, CurrencyKind>();
            _currencyIds = new HashSet<int>();
            foreach (KeyValuePair<int, CurrencyKind> pair in currencyByItemId)
            {
                if (pair.Key <= 0)
                    throw new ArgumentException("currency itemId 必须为正", nameof(currencyByItemId));
                if (_currencyByItemId.ContainsKey(pair.Key))
                    throw new ArgumentException("重复 currency itemId", nameof(currencyByItemId));
                _currencyByItemId.Add(pair.Key, pair.Value);
                _currencyIds.Add(pair.Key);
            }

            MaxRewardEntries = maxRewardEntries;
            MaxCostEntries = maxCostEntries;
            CurrencyItemIds = Array.AsReadOnly(SortedIds(_currencyIds));
        }

        public InventoryRules Inventory { get; }
        public int MaxRewardEntries { get; }
        public int MaxCostEntries { get; }
        public IReadOnlyList<int> CurrencyItemIds { get; }

        public bool IsCurrencyItem(int itemId) => _currencyIds.Contains(itemId);

        public bool TryGetCurrencyKind(int itemId, out CurrencyKind kind)
            => _currencyByItemId.TryGetValue(itemId, out kind);

        /// <summary>
        /// 从同份快照构建：物理 ItemStackMax 以外的 KnownItem 视为 Currency，并按 03 固定 ID 映射字段。
        /// </summary>
        public static RewardRules FromSnapshot(
            GameConfigSnapshot snapshot,
            int maxRewardEntries = DefaultMaxRewardEntries,
            int maxCostEntries = DefaultMaxCostEntries)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            InventoryRules inventory = InventoryRules.FromSnapshot(snapshot);
            var map = new Dictionary<int, CurrencyKind>();
            for (int i = 0; i < snapshot.KnownItemIds.Count; i++)
            {
                int id = snapshot.KnownItemIds[i];
                if (snapshot.TryGetItemStackMax(id, out _))
                    continue;
                if (!TryMapWellKnownCurrency(id, out CurrencyKind kind))
                {
                    throw new InvalidOperationException(
                        "未知 Currency itemId（T020 仅支持 9001/9002/9003/9101）: "
                        + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                map.Add(id, kind);
            }

            if (map.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot 缺少 Currency Item");

            return new RewardRules(inventory, map, maxRewardEntries, maxCostEntries);
        }

        public static bool TryMapWellKnownCurrency(int itemId, out CurrencyKind kind)
        {
            switch (itemId)
            {
                case 9001:
                    kind = CurrencyKind.Gold;
                    return true;
                case 9002:
                    kind = CurrencyKind.Gem;
                    return true;
                case 9003:
                    kind = CurrencyKind.Ticket;
                    return true;
                case 9101:
                    kind = CurrencyKind.Shard;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        static int[] SortedIds(HashSet<int> set)
        {
            var arr = new int[set.Count];
            set.CopyTo(arr);
            Array.Sort(arr);
            return arr;
        }
    }

    /// <summary>纯奖励候选：成功含 Economy Candidate；失败 Candidate=null，不别名源。</summary>
    public sealed class RewardResult
    {
        RewardResult(
            bool success,
            int code,
            EconomyState? candidate,
            IReadOnlyList<InventoryStackView> itemDiffs,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            Candidate = candidate;
            ItemDiffs = itemDiffs ?? Array.AsReadOnly(Array.Empty<InventoryStackView>());
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        public EconomyState? Candidate { get; }
        public IReadOnlyList<InventoryStackView> ItemDiffs { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static RewardResult Ok(
            EconomyState candidate,
            IReadOnlyList<InventoryStackView> itemDiffs,
            IReadOnlyList<string> diagnostics)
        {
            return new RewardResult(
                true,
                0,
                candidate,
                FreezeStacks(itemDiffs),
                FreezeDiagnostics(diagnostics));
        }

        public static RewardResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new RewardResult(
                false,
                code,
                null,
                Array.AsReadOnly(Array.Empty<InventoryStackView>()),
                FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<InventoryStackView> FreezeStacks(IReadOnlyList<InventoryStackView>? source)
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
