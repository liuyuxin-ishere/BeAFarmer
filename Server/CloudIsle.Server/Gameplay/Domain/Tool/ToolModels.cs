using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Reward;

namespace CloudIsle.Domain.Tool
{
    /// <summary>工具升级意图：仅稳定 toolId；下一等级/成本/门槛由服务端规则给出。</summary>
    public readonly struct ToolUpgradeIntent
    {
        public ToolUpgradeIntent(int toolId)
        {
            ToolId = toolId;
        }

        public int ToolId { get; }
    }

    /// <summary>
    /// 升级规则输入：玩家等级/金币/背包/工具视图；不持有可变聚合引用。
    /// </summary>
    public sealed class ToolInput
    {
        public ToolInput(
            int level,
            long gold,
            IReadOnlyList<BagItemView> bag,
            IReadOnlyList<ToolStateView> tools)
        {
            if (level < 0)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (gold < 0)
                throw new ArgumentOutOfRangeException(nameof(gold));
            if (bag == null)
                throw new ArgumentNullException(nameof(bag));
            if (tools == null)
                throw new ArgumentNullException(nameof(tools));

            Level = level;
            Gold = gold;
            Bag = FreezeBag(bag);
            Tools = FreezeTools(tools);
        }

        public int Level { get; }
        public long Gold { get; }
        public IReadOnlyList<BagItemView> Bag { get; }
        public IReadOnlyList<ToolStateView> Tools { get; }

        public static ToolInput FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return new ToolInput(aggregate.Level, aggregate.Gold, aggregate.Bag, aggregate.Tools);
        }

        public long Fingerprint()
        {
            unchecked
            {
                long h = Level;
                h = (h * 397) ^ Gold;
                h = (h * 397) ^ Bag.Count;
                for (int i = 0; i < Bag.Count; i++)
                {
                    BagItemView b = Bag[i];
                    h = (h * 397) ^ b.ItemId;
                    h = (h * 397) ^ b.Count;
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

        static IReadOnlyList<BagItemView> FreezeBag(IReadOnlyList<BagItemView> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<BagItemView>());
            var arr = new BagItemView[source.Count];
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
    /// 同份 <see cref="GameConfigSnapshot"/> 的工具等级视图；
    /// 升级门槛/金币/材料与采集共用同一快照，禁止命令内重读磁盘。
    /// </summary>
    public sealed class ToolRules
    {
        ToolRules(GameConfigSnapshot snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            ConfigVersion = snapshot.Version;
            ToolLevels = snapshot.ToolLevels;
        }

        public GameConfigSnapshot Snapshot { get; }
        public int ConfigVersion { get; }
        public IReadOnlyDictionary<long, ToolLevelSpec> ToolLevels { get; }

        public static ToolRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.ToolLevels.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing ToolLevels; use LoadDirectory");
            return new ToolRules(snapshot);
        }

        public bool TryGetToolLevel(int toolId, int level, out ToolLevelSpec tool)
            => Snapshot.TryGetToolLevel(toolId, level, out tool);
    }

    /// <summary>工具升级命令载体（应用层入口）。</summary>
    public sealed class ToolCommand
    {
        public ToolCommand(ToolUpgradeIntent intent)
        {
            Intent = intent;
        }

        public ToolUpgradeIntent Intent { get; }
    }

    /// <summary>
    /// 纯升级结果：成功含金币/材料成本与工具等级 patch；失败无效果、不改节点冷却。
    /// </summary>
    public sealed class ToolUpgradeResult
    {
        ToolUpgradeResult(
            bool success,
            bool hasSettlement,
            int code,
            long goldCost,
            IReadOnlyList<ItemAmount> itemCosts,
            ToolStateView? toolAfter,
            int fromLevel,
            int toLevel,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            HasSettlement = hasSettlement;
            Code = code;
            GoldCost = goldCost;
            ItemCosts = itemCosts ?? Array.AsReadOnly(Array.Empty<ItemAmount>());
            ToolAfter = toolAfter;
            FromLevel = fromLevel;
            ToLevel = toLevel;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public bool HasSettlement { get; }
        public int Code { get; }
        public long GoldCost { get; }
        public IReadOnlyList<ItemAmount> ItemCosts { get; }
        public ToolStateView? ToolAfter { get; }
        public int FromLevel { get; }
        public int ToLevel { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static ToolUpgradeResult Ok(
            long goldCost,
            IReadOnlyList<ItemAmount> itemCosts,
            ToolStateView toolAfter,
            int fromLevel,
            int toLevel,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (goldCost < 0)
                throw new ArgumentOutOfRangeException(nameof(goldCost));
            if (itemCosts == null)
                throw new ArgumentNullException(nameof(itemCosts));
            if (toolAfter.ToolId <= 0 || toolAfter.Level <= 0)
                throw new ArgumentOutOfRangeException(nameof(toolAfter));
            if (fromLevel <= 0 || toLevel <= fromLevel)
                throw new ArgumentOutOfRangeException(nameof(toLevel));
            return new ToolUpgradeResult(
                true, hasSettlement: true, 0, goldCost, FreezeCosts(itemCosts), toolAfter,
                fromLevel, toLevel, FreezeDiagnostics(diagnostics));
        }

        public static ToolUpgradeResult Fail(
            int code,
            IReadOnlyList<string>? diagnostics = null)
        {
            return new ToolUpgradeResult(
                false, hasSettlement: false, code, goldCost: 0,
                Array.AsReadOnly(Array.Empty<ItemAmount>()), toolAfter: null,
                fromLevel: 0, toLevel: 0, FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<ItemAmount> FreezeCosts(IReadOnlyList<ItemAmount> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<ItemAmount>());
            var arr = new ItemAmount[source.Count];
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
