using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Reward;

namespace CloudIsle.Domain.Progression
{
    /// <summary>
    /// 成长输入态：等级/相对当前级经验 + 已有工具/背包拥有（一次性赠品判定）。
    /// 不别名源集合。
    /// </summary>
    public sealed class LevelState
    {
        public LevelState(
            int level,
            int exp,
            IReadOnlyCollection<int>? ownedToolIds = null,
            IReadOnlyCollection<int>? bagOwnedItemIds = null)
        {
            Level = level;
            Exp = exp;
            OwnedToolIds = FreezeIds(ownedToolIds);
            BagOwnedItemIds = FreezeIds(bagOwnedItemIds);
        }

        public int Level { get; }
        /// <summary>相对当前等级的经验（非终身累计）。</summary>
        public int Exp { get; }
        public IReadOnlyList<int> OwnedToolIds { get; }
        public IReadOnlyList<int> BagOwnedItemIds { get; }

        public bool OwnsTool(int toolId)
        {
            for (int i = 0; i < OwnedToolIds.Count; i++)
            {
                if (OwnedToolIds[i] == toolId)
                    return true;
            }
            return false;
        }

        public bool OwnsBagItem(int itemId)
        {
            for (int i = 0; i < BagOwnedItemIds.Count; i++)
            {
                if (BagOwnedItemIds[i] == itemId)
                    return true;
            }
            return false;
        }

        /// <summary>工具记录或背包拥有均视为已领取/已拥有（禁止再送）。</summary>
        public bool OwnsToolOrBag(int toolItemId)
            => OwnsTool(toolItemId) || OwnsBagItem(toolItemId);

        public static LevelState FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return FromOwnershipViews(aggregate.Level, aggregate.Exp, aggregate.Tools, aggregate.Bag);
        }

        /// <summary>
        /// 成长拥有判定输入：等级/经验仍用源；工具取补丁后工作副本；背包取相关输入视图（成本前）。
        /// </summary>
        public static LevelState FromOwnershipViews(
            int level,
            int exp,
            IReadOnlyList<ToolStateView> tools,
            IReadOnlyList<BagItemView> bag)
        {
            if (tools == null)
                throw new ArgumentNullException(nameof(tools));
            if (bag == null)
                throw new ArgumentNullException(nameof(bag));

            var toolIds = new int[tools.Count];
            for (int i = 0; i < tools.Count; i++)
                toolIds[i] = tools[i].ToolId;

            var bagIds = new List<int>(bag.Count);
            for (int i = 0; i < bag.Count; i++)
            {
                BagItemView b = bag[i];
                if (b.ItemId > 0 && b.Count > 0)
                    bagIds.Add(b.ItemId);
            }

            return new LevelState(level, exp, toolIds, bagIds);
        }

        static IReadOnlyList<int> FreezeIds(IReadOnlyCollection<int>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<int>());
            var arr = new int[source.Count];
            int i = 0;
            foreach (int id in source)
                arr[i++] = id;
            Array.Sort(arr);
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的成长规则视图。
    /// 禁止在 Policy 内再 Capture/读文件；缺表/缺口/非正显式失败。
    /// </summary>
    public sealed class ProgressionRules
    {
        readonly Dictionary<int, int> _needExp;
        readonly Dictionary<int, int> _itemUnlockLevels;
        readonly int[] _unlockGiftTools;

        public ProgressionRules(
            int maxLevel,
            IReadOnlyDictionary<int, int> needExpByLevel,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyList<int> unlockGiftTools)
        {
            if (maxLevel <= 1)
                throw new ArgumentOutOfRangeException(nameof(maxLevel));
            if (needExpByLevel == null)
                throw new ArgumentNullException(nameof(needExpByLevel));
            if (itemUnlockLevels == null)
                throw new ArgumentNullException(nameof(itemUnlockLevels));
            if (unlockGiftTools == null)
                throw new ArgumentNullException(nameof(unlockGiftTools));

            MaxLevel = maxLevel;

            // 必须先校验原始键集合：禁止只复制 1..maxLevel-1 后静默丢弃 0/满级/范围外行。
            foreach (KeyValuePair<int, int> pair in needExpByLevel)
            {
                if (pair.Key < 1 || pair.Key >= maxLevel)
                {
                    throw new ArgumentException(
                        "Level.needExp 含范围外阈值行: level="
                        + pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        nameof(needExpByLevel));
                }
                if (pair.Value <= 0)
                {
                    throw new ArgumentException(
                        "Level.needExp 缺失或非正: level="
                        + pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        nameof(needExpByLevel));
                }
            }

            if (needExpByLevel.Count != maxLevel - 1)
            {
                throw new ArgumentException(
                    "Level.needExp 行数须恰为 maxLevel-1（缺行或额外行）",
                    nameof(needExpByLevel));
            }

            _needExp = new Dictionary<int, int>(maxLevel - 1);
            for (int lv = 1; lv < maxLevel; lv++)
            {
                if (!needExpByLevel.TryGetValue(lv, out int need) || need <= 0)
                {
                    throw new ArgumentException(
                        "Level.needExp 缺失或非正: level=" + lv.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        nameof(needExpByLevel));
                }
                _needExp[lv] = need;
            }

            _itemUnlockLevels = new Dictionary<int, int>(itemUnlockLevels.Count);
            foreach (KeyValuePair<int, int> pair in itemUnlockLevels)
            {
                if (pair.Key <= 0)
                    throw new ArgumentException("item unlock id 必须为正", nameof(itemUnlockLevels));
                if (pair.Value < 0)
                    throw new ArgumentException("unlockLevel 不能为负", nameof(itemUnlockLevels));
                _itemUnlockLevels[pair.Key] = pair.Value;
            }

            var gifts = new int[unlockGiftTools.Count];
            var seen = new HashSet<int>();
            for (int i = 0; i < unlockGiftTools.Count; i++)
            {
                int id = unlockGiftTools[i];
                if (id <= 0)
                    throw new ArgumentException("unlockGiftTools id 必须为正", nameof(unlockGiftTools));
                if (!seen.Add(id))
                    throw new ArgumentException("unlockGiftTools 重复 id", nameof(unlockGiftTools));
                if (!_itemUnlockLevels.TryGetValue(id, out int unlockLv) || unlockLv <= 0)
                {
                    throw new ArgumentException(
                        "unlockGiftTools 缺正 unlockLevel: " + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        nameof(unlockGiftTools));
                }
                gifts[i] = id;
            }
            Array.Sort(gifts);
            _unlockGiftTools = gifts;

            ItemUnlockLevels = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(
                new Dictionary<int, int>(_itemUnlockLevels));
            UnlockGiftTools = Array.AsReadOnly((int[])gifts.Clone());
            NeedExpByLevel = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(
                new Dictionary<int, int>(_needExp));
        }

        public int MaxLevel { get; }
        public IReadOnlyDictionary<int, int> NeedExpByLevel { get; }
        public IReadOnlyDictionary<int, int> ItemUnlockLevels { get; }
        public IReadOnlyList<int> UnlockGiftTools { get; }

        public bool TryGetNeedExp(int level, out int needExp)
            => _needExp.TryGetValue(level, out needExp);

        public bool TryGetItemUnlockLevel(int itemId, out int unlockLevel)
            => _itemUnlockLevels.TryGetValue(itemId, out unlockLevel);

        /// <summary>从同份不可变快照构建；不重新读盘/Capture。</summary>
        public static ProgressionRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.MaxLevel <= 1 || snapshot.LevelNeedExp.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot 缺少 Level 表");

            return new ProgressionRules(
                snapshot.MaxLevel,
                snapshot.LevelNeedExp,
                snapshot.ItemUnlockLevels,
                snapshot.PlayerInit.UnlockGiftTools);
        }
    }
}
