using System;
using System.Collections.Generic;

namespace CloudIsle.Config
{
    /// <summary>
    /// 工具等级只读投影（T038/P02 + T040）：(toolId,level)→门槛/金币/材料/产量上限加成/最终稀有概率。
    /// <see cref="NeedPlayerLevel"/>/<see cref="GoldCost"/>/<see cref="Materials"/> 仅升级使用；
    /// 采集只读 <see cref="YieldBonusMax"/>/<see cref="RareChancePpm"/>，不得用门槛降级已有高等级工具。
    /// <see cref="HasCompleteUpgradeCost"/>：装载期捕获的完整升级成本面；旧六参数及 materials:null 为 false，
    /// 显式空列表仍可表示合法金币-only 成本；Freeze/Snapshot 复制必须保留标志，
    /// 禁止把不完整投影洗成“空材料免费升级”，也不允许用完整标志绕过 null。
    /// </summary>
    public sealed class ToolLevelSpec
    {
        /// <summary>稀有概率定点分母：1_000_000 ppm ≡ 100%。</summary>
        public const int ChanceScalePpm = 1_000_000;

        static readonly IReadOnlyList<ItemStackSpec> EmptyMaterials =
            Array.AsReadOnly(Array.Empty<ItemStackSpec>());

        /// <summary>
        /// 兼容构造：无材料列表 → 空材料且 <see cref="HasCompleteUpgradeCost"/>=false。
        /// 旧 Gather 仍可读产量/稀有；升级规则必须拒绝以此作为下一等级成本。
        /// </summary>
        public ToolLevelSpec(
            int toolId,
            int level,
            int needPlayerLevel,
            int goldCost,
            int yieldBonusMax,
            int rareChancePpm)
            : this(
                toolId,
                level,
                needPlayerLevel,
                goldCost,
                yieldBonusMax,
                rareChancePpm,
                materials: null,
                upgradeCostComplete: false)
        {
        }

        /// <summary>
        /// 七参数：仅当 <paramref name="materials"/> 非 null（含空列表=无材料/金币-only）时标完整；
        /// null 表示缺失信息 → <see cref="HasCompleteUpgradeCost"/>=false，不得当免费升级。
        /// </summary>
        public ToolLevelSpec(
            int toolId,
            int level,
            int needPlayerLevel,
            int goldCost,
            int yieldBonusMax,
            int rareChancePpm,
            IReadOnlyList<ItemStackSpec>? materials)
            : this(
                toolId,
                level,
                needPlayerLevel,
                goldCost,
                yieldBonusMax,
                rareChancePpm,
                materials,
                upgradeCostComplete: materials != null)
        {
        }

        /// <summary>
        /// 装载/Freeze 用：显式保留成本完整性标志，避免复制时把不完整投影洗成完整。
        /// <paramref name="materials"/> 为 null 时强制不完整，忽略 <paramref name="upgradeCostComplete"/>=true 绕过。
        /// </summary>
        public ToolLevelSpec(
            int toolId,
            int level,
            int needPlayerLevel,
            int goldCost,
            int yieldBonusMax,
            int rareChancePpm,
            IReadOnlyList<ItemStackSpec>? materials,
            bool upgradeCostComplete)
        {
            if (toolId <= 0)
                throw new ArgumentOutOfRangeException(nameof(toolId));
            if (level <= 0)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (needPlayerLevel < 0)
                throw new ArgumentOutOfRangeException(nameof(needPlayerLevel));
            if (goldCost < 0)
                throw new ArgumentOutOfRangeException(nameof(goldCost));
            if (yieldBonusMax < 0)
                throw new ArgumentOutOfRangeException(nameof(yieldBonusMax));
            if (rareChancePpm < 0 || rareChancePpm > ChanceScalePpm)
                throw new ArgumentOutOfRangeException(nameof(rareChancePpm));

            ToolId = toolId;
            Level = level;
            NeedPlayerLevel = needPlayerLevel;
            GoldCost = goldCost;
            YieldBonusMax = yieldBonusMax;
            RareChancePpm = rareChancePpm;
            Materials = FreezeMaterials(materials);
            // null=缺失信息：不得借完整标志洗成空材料免费升级；显式空列表才可完整。
            HasCompleteUpgradeCost = upgradeCostComplete && materials != null;
        }

        public int ToolId { get; }
        public int Level { get; }
        /// <summary>升级所需玩家等级；采集资格不读取。</summary>
        public int NeedPlayerLevel { get; }
        public int GoldCost { get; }
        /// <summary>
        /// 升至本等级所需材料（一次装载捕获；显式空且完整=无材料/金币-only；
        /// materials 曾为 null 时 Materials 仍为空列表但 HasCompleteUpgradeCost=false，不得当免费）。
        /// </summary>
        public IReadOnlyList<ItemStackSpec> Materials { get; }
        /// <summary>
        /// 是否已捕获完整升级成本面（含显式空材料）。旧六参数 / materials:null 为 false。
        /// </summary>
        public bool HasCompleteUpgradeCost { get; }
        /// <summary>扩大普通产量 inclusive 上限（yieldMax+本值），不是每次产量固定加数。</summary>
        public int YieldBonusMax { get; }
        /// <summary>当前等级最终稀有命中 ppm（0=永不，Scale=必中）；非节点基础率叠加。</summary>
        public int RareChancePpm { get; }

        public static long PackKey(int toolId, int level)
            => ((long)toolId << 32) | (uint)level;

        static IReadOnlyList<ItemStackSpec> FreezeMaterials(IReadOnlyList<ItemStackSpec>? source)
        {
            if (source == null || source.Count == 0)
                return EmptyMaterials;
            var arr = new ItemStackSpec[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }
}
