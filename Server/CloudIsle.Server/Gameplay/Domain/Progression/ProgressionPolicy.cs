using System;
using System.Collections.Generic;
using System.Numerics;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Domain.Progression
{
    /// <summary>
    /// D03 纯成长策略：连续升级、配置解锁集合、Lv3 工具 5102 一次性效果。
    /// 不入包、不 Save/Ntf/revision、不递归调用 Reward/Inventory。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一次性赠品 marker（诚实派生，无新增持久 flag、不滥用 GrantVersion）：
    /// 仅当 <c>current.Level &lt; unlockLv</c> 且升级后 <c>levelAfter &gt;= unlockLv</c> 时视为首次跨越；
    /// 已持久化的 <c>level &gt;= unlockLv</c> 本身即表示跨越已发生（不再追补）。
    /// 另：已有 Tool 记录或背包拥有该工具物品 → 视为已领取，不得再送/降级。
    /// </para>
    /// </remarks>
    public static class ProgressionPolicy
    {
        public const string DiagNegativeExp = "negative-exp-gain-rejected";
        public const string DiagInvalidLevelState = "invalid-level-or-exp-state";
        public const string DiagInvalidConfig = "invalid-progression-config";
        public const string DiagExpOverflow = "exp-gain-overflow";

        /// <summary>
        /// 评估成长。成功返回 detached next level/exp、排序解锁 ID、合格一次性赠品；失败 Candidate 语义由调用方视为无。
        /// </summary>
        public static ProgressionResult Evaluate(LevelState current, long expGain, ProgressionRules rules)
        {
            if (current == null)
                throw new ArgumentNullException(nameof(current));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();

            if (expGain < 0)
            {
                diagnostics.Add(DiagNegativeExp);
                return ProgressionResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (rules.MaxLevel <= 1)
            {
                diagnostics.Add(DiagInvalidConfig);
                return ProgressionResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (current.Level < 1
                || current.Level > rules.MaxLevel
                || current.Exp < 0)
            {
                diagnostics.Add(DiagInvalidLevelState);
                return ProgressionResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 满级：exp 恒 0；额外增益丢弃；显式规范化遗留正残余。
            if (current.Level == rules.MaxLevel)
            {
                return BuildSuccess(
                    current,
                    rules.MaxLevel,
                    expAfter: 0,
                    rules,
                    diagnostics);
            }

            // 相对当前级：先并入本次增益，再按 needExp(current) 连续消耗。
            BigInteger expPool = current.Exp;
            if (expGain > 0)
            {
                expPool += expGain;
                if (expPool > long.MaxValue)
                {
                    // 超大正增益允许有限步饱和到满级，不得 int 回绕；池用 BigInteger 继续。
                }
            }

            int level = current.Level;
            // 最多 MaxLevel-1 次升级；有界，无用溢出循环。
            int guard = rules.MaxLevel;
            while (level < rules.MaxLevel && guard-- > 0)
            {
                if (!rules.TryGetNeedExp(level, out int need) || need <= 0)
                {
                    diagnostics.Add(DiagInvalidConfig);
                    return ProgressionResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                if (expPool < need)
                    break;

                expPool -= need;
                level++;
                if (level == rules.MaxLevel)
                {
                    expPool = BigInteger.Zero; // D08：升至 20 清零并丢弃多余
                    break;
                }
            }

            if (level < 1 || level > rules.MaxLevel)
            {
                diagnostics.Add(DiagInvalidLevelState);
                return ProgressionResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (expPool < BigInteger.Zero || expPool > int.MaxValue)
            {
                diagnostics.Add(DiagExpOverflow);
                return ProgressionResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            int expAfter = level == rules.MaxLevel ? 0 : (int)expPool;
            return BuildSuccess(current, level, expAfter, rules, diagnostics);
        }

        static ProgressionResult BuildSuccess(
            LevelState current,
            int levelAfter,
            int expAfter,
            ProgressionRules rules,
            List<string> diagnostics)
        {
            IReadOnlyList<int> unlocked = CollectNewlyUnlocked(current.Level, levelAfter, rules);
            TryBuildLevelGifts(
                current,
                levelAfter,
                rules,
                out RewardBundle? gifts,
                out IReadOnlyList<ToolStateView> toolsToEnsure);

            bool changed = levelAfter != current.Level
                || expAfter != current.Exp
                || (gifts != null && (gifts.Items.Count > 0 || gifts.Gold != 0 || gifts.Gem != 0
                    || gifts.Ticket != 0 || gifts.Shard != 0 || gifts.Exp != 0))
                || toolsToEnsure.Count > 0
                || unlocked.Count > 0;

            return ProgressionResult.Ok(
                levelAfter,
                expAfter,
                unlocked,
                gifts,
                toolsToEnsure,
                hasProgression: changed,
                diagnostics);
        }

        static IReadOnlyList<int> CollectNewlyUnlocked(int levelBefore, int levelAfter, ProgressionRules rules)
        {
            if (levelAfter <= levelBefore)
                return Array.AsReadOnly(Array.Empty<int>());

            var list = new List<int>();
            foreach (KeyValuePair<int, int> pair in rules.ItemUnlockLevels)
            {
                // 跨越区间 (before, after]：旧级未达、新级已达。
                if (pair.Value > levelBefore && pair.Value <= levelAfter)
                    list.Add(pair.Key);
            }
            list.Sort();
            return Array.AsReadOnly(list.ToArray());
        }

        /// <summary>
        /// 仅配置 UnlockGiftTools（M2=5102×1）；无虚构每级礼包。
        /// 跨越 unlockLevel 且未拥有/领取才入选；已有 Tool 不降级。
        /// </summary>
        static void TryBuildLevelGifts(
            LevelState current,
            int levelAfter,
            ProgressionRules rules,
            out RewardBundle? gifts,
            out IReadOnlyList<ToolStateView> toolsToEnsure)
        {
            gifts = null;
            var giftItems = new List<ItemAmount>();
            var tools = new List<ToolStateView>();

            for (int i = 0; i < rules.UnlockGiftTools.Count; i++)
            {
                int toolId = rules.UnlockGiftTools[i];
                if (!rules.TryGetItemUnlockLevel(toolId, out int unlockLv) || unlockLv <= 0)
                    continue;

                // 持久 level 已达门槛 ⇒ 非本次跨越，不追补。
                bool crossed = current.Level < unlockLv && levelAfter >= unlockLv;
                if (!crossed)
                    continue;

                if (current.OwnsToolOrBag(toolId))
                    continue;

                giftItems.Add(new ItemAmount(toolId, 1));
                // 必要 Tool 记录一起候选；level=1；若已有则上方已跳过。
                tools.Add(new ToolStateView(toolId, 1));
            }

            if (giftItems.Count == 0)
            {
                toolsToEnsure = Array.AsReadOnly(Array.Empty<ToolStateView>());
                return;
            }

            gifts = new RewardBundle(items: giftItems);
            toolsToEnsure = Array.AsReadOnly(tools.ToArray());
        }
    }
}
