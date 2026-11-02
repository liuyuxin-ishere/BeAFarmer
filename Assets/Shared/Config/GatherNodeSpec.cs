using System;

namespace CloudIsle.Config
{
    /// <summary>
    /// 采集点只读投影（T038/P02）：类型 id→工具/解锁/产量/冷却/稀有物/经验；
    /// 不暴露 Luban 可变行。节点表 <c>rareChance</c> 仅作配置投影；正式抽样用工具等级最终概率。
    /// </summary>
    public sealed class GatherNodeSpec
    {
        public GatherNodeSpec(
            int nodeId,
            int toolId,
            int unlockLevel,
            int count,
            int itemId,
            int yieldMin,
            int yieldMax,
            int cooldownSec,
            int rareItemId,
            int rareChancePpm,
            int exp)
        {
            if (nodeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(nodeId));
            if (toolId <= 0)
                throw new ArgumentOutOfRangeException(nameof(toolId));
            if (unlockLevel <= 0)
                throw new ArgumentOutOfRangeException(nameof(unlockLevel));
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId));
            if (yieldMin <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldMin));
            if (yieldMax < yieldMin)
                throw new ArgumentOutOfRangeException(nameof(yieldMax));
            if (cooldownSec <= 0)
                throw new ArgumentOutOfRangeException(nameof(cooldownSec));
            if (rareItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(rareItemId));
            if (rareChancePpm < 0 || rareChancePpm > ToolLevelSpec.ChanceScalePpm)
                throw new ArgumentOutOfRangeException(nameof(rareChancePpm));
            if (exp < 0)
                throw new ArgumentOutOfRangeException(nameof(exp));

            NodeId = nodeId;
            ToolId = toolId;
            UnlockLevel = unlockLevel;
            Count = count;
            ItemId = itemId;
            YieldMin = yieldMin;
            YieldMax = yieldMax;
            CooldownSec = cooldownSec;
            RareItemId = rareItemId;
            RareChancePpm = rareChancePpm;
            Exp = exp;
        }

        public int NodeId { get; }
        public int ToolId { get; }
        public int UnlockLevel { get; }
        public int Count { get; }
        public int ItemId { get; }
        public int YieldMin { get; }
        public int YieldMax { get; }
        public int CooldownSec { get; }
        public int RareItemId { get; }
        /// <summary>节点表投影的 ppm（0..1e6）；采集抽样不使用本字段。</summary>
        public int RareChancePpm { get; }
        public int Exp { get; }
    }
}
