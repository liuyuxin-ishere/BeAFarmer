using System;
using System.Collections.Generic;
using System.Numerics;
using CloudIsle.Domain.Inventory;
using CloudIsle.Net;

namespace CloudIsle.Domain.Reward
{
    /// <summary>
    /// D02 奖励策略：合并基础奖励与成长赠品；纯货币不占格；不递归成长、不 Save/Ntf/revision。
    /// 经验已由 ProgressionPolicy 消费；本层 <see cref="RewardBundle.Exp"/> 须为 0。
    /// Currency Item 不得入物理背包。
    /// </summary>
    public static class RewardPolicy
    {
        /// <summary>历史诊断常量保留；T021 后正经验走 Progression，不再用此拒正增益。</summary>
        public const string DiagProgressionUnsupported = "progression-unsupported-until-T021";
        public const string DiagExpUnsupported = "exp-reward-unsupported-until-T021";
        public const string DiagCurrencyAsBagItem = "currency-item-must-not-enter-bag";
        public const string DiagNegativeReward = "negative-reward-not-debit";
        public const string DiagResidualExp = "reward-exp-must-be-consumed-by-progression";

        /// <summary>
        /// 将奖励应用到 current（通常为已扣成本后的经济态）。失败时源指纹不变；返回集合不别名源。
        /// </summary>
        public static RewardResult Evaluate(
            EconomyState current,
            RewardBundle reward,
            ProgressionResult progression,
            RewardRules rules)
        {
            if (current == null)
                throw new ArgumentNullException(nameof(current));
            if (reward == null)
                throw new ArgumentNullException(nameof(reward));
            if (progression == null)
                throw new ArgumentNullException(nameof(progression));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();

            // Composer 须先把经验交给 Progression；此处残留非零 exp 显式失败（禁止默默丢）。
            if (reward.Exp != 0)
            {
                diagnostics.Add(DiagResidualExp);
                return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!progression.Success)
            {
                for (int i = 0; i < progression.Diagnostics.Count; i++)
                    diagnostics.Add(progression.Diagnostics[i]);
                return RewardResult.Fail(
                    progression.Code != 0 ? progression.Code : ErrorCodes.BadPacket,
                    diagnostics);
            }

            if (!TryValidateNonNegativeCurrency(reward.Gold, reward.Gem, reward.Ticket, reward.Shard, diagnostics))
                return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);

            // 负源货币不得保留或被奖励补平；四字段均须在加减前显式合法。
            if (!TryValidateNonNegativeCurrency(current.Gold, current.Gem, current.Ticket, current.Shard, diagnostics))
                return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);

            // 普通奖与等级赠品各自及合并后的原始条目数均受 MaxRewardEntries 约束；
            // 禁止同 ID 重复条目先聚合成 1 条再绕过 256 输入上限。
            int giftItemEntries = progression.LevelGifts != null ? progression.LevelGifts.Items.Count : 0;
            if (reward.Items.Count > rules.MaxRewardEntries
                || giftItemEntries > rules.MaxRewardEntries
                || reward.Items.Count + giftItemEntries > rules.MaxRewardEntries)
            {
                return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            BigInteger gold = reward.Gold;
            BigInteger gem = reward.Gem;
            BigInteger ticket = reward.Ticket;
            BigInteger shard = reward.Shard;
            var itemAgg = new Dictionary<int, BigInteger>();

            if (!TryAccumulateItems(reward.Items, itemAgg, rules, diagnostics, out int itemCode))
                return RewardResult.Fail(itemCode, diagnostics);

            // 合并成长赠品（普通奖 + 等级赠品 → 一次最终容量）；赠品自身不递归成长。
            if (progression.LevelGifts != null)
            {
                RewardBundle gifts = progression.LevelGifts;
                if (gifts.Exp != 0)
                {
                    diagnostics.Add(DiagResidualExp);
                    return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }
                if (gifts.Gold < 0 || gifts.Gem < 0 || gifts.Ticket < 0 || gifts.Shard < 0)
                {
                    diagnostics.Add(DiagNegativeReward);
                    return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }
                gold += gifts.Gold;
                gem += gifts.Gem;
                ticket += gifts.Ticket;
                shard += gifts.Shard;
                if (!TryAccumulateItems(gifts.Items, itemAgg, rules, diagnostics, out itemCode))
                    return RewardResult.Fail(itemCode, diagnostics);
            }

            if (!TryAddCurrencyChecked(current.Gold, gold, out long nextGold, diagnostics)
                || !TryAddCurrencyChecked(current.Gem, gem, out long nextGem, diagnostics)
                || !TryAddCurrencyChecked(current.Ticket, ticket, out long nextTicket, diagnostics)
                || !TryAddCurrencyChecked(current.Shard, shard, out long nextShard, diagnostics))
            {
                return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            var deltas = new List<ItemDelta>(itemAgg.Count);
            foreach (KeyValuePair<int, BigInteger> pair in itemAgg)
            {
                if (pair.Value.IsZero)
                    continue;
                if (pair.Value < BigInteger.Zero)
                {
                    diagnostics.Add(DiagNegativeReward);
                    return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }
                if (pair.Value > long.MaxValue)
                    return RewardResult.Fail(ErrorCodes.BadPacket, diagnostics);
                deltas.Add(new ItemDelta(pair.Key, (long)pair.Value));
            }

            InventoryResult inv = InventoryPolicy.TryApply(current.Inventory, deltas, rules.Inventory);
            if (!inv.Success)
            {
                for (int i = 0; i < inv.Diagnostics.Count; i++)
                    diagnostics.Add(inv.Diagnostics[i]);
                return RewardResult.Fail(inv.Code, diagnostics);
            }

            for (int i = 0; i < inv.Diagnostics.Count; i++)
                diagnostics.Add(inv.Diagnostics[i]);

            var candidate = new EconomyState(nextGold, nextGem, nextTicket, nextShard, inv.Candidate!);
            return RewardResult.Ok(candidate, inv.Diffs, diagnostics);
        }

        /// <summary>合并多来源奖励包（精确 BigInteger）；不含消耗。供 Composer 预聚合。</summary>
        public static bool TryMergeBundles(
            IReadOnlyList<RewardBundle> sources,
            RewardRules rules,
            out long gold,
            out long gem,
            out long ticket,
            out long shard,
            out IReadOnlyList<ItemAmount> items,
            out long exp,
            List<string> diagnostics,
            out int code)
        {
            gold = gem = ticket = shard = exp = 0;
            items = Array.AsReadOnly(Array.Empty<ItemAmount>());
            code = ErrorCodes.Ok;
            if (sources == null)
                throw new ArgumentNullException(nameof(sources));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            if (sources.Count > rules.MaxRewardEntries)
            {
                code = ErrorCodes.BadPacket;
                return false;
            }

            BigInteger g = BigInteger.Zero;
            BigInteger ge = BigInteger.Zero;
            BigInteger t = BigInteger.Zero;
            BigInteger s = BigInteger.Zero;
            BigInteger e = BigInteger.Zero;
            var itemAgg = new Dictionary<int, BigInteger>();
            int entryBudget = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                RewardBundle b = sources[i] ?? RewardBundle.Empty;
                // 负经验逐来源拒绝；禁止 +1/-1 抵消后静默发其他奖。
                if (b.Exp < 0)
                {
                    diagnostics.Add(CloudIsle.Domain.Progression.ProgressionPolicy.DiagNegativeExp);
                    code = ErrorCodes.BadPacket;
                    return false;
                }

                if (b.Gold < 0 || b.Gem < 0 || b.Ticket < 0 || b.Shard < 0)
                {
                    diagnostics.Add(DiagNegativeReward);
                    code = ErrorCodes.BadPacket;
                    return false;
                }

                g += b.Gold;
                ge += b.Gem;
                t += b.Ticket;
                s += b.Shard;
                e += b.Exp;
                entryBudget++;
                if (entryBudget > rules.MaxRewardEntries)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }

                for (int j = 0; j < b.Items.Count; j++)
                {
                    entryBudget++;
                    if (entryBudget > rules.MaxRewardEntries)
                    {
                        code = ErrorCodes.BadPacket;
                        return false;
                    }
                }

                if (!TryAccumulateItems(b.Items, itemAgg, rules, diagnostics, out code))
                    return false;
            }

            if (!TryBigToNonNegativeLong(g, out gold)
                || !TryBigToNonNegativeLong(ge, out gem)
                || !TryBigToNonNegativeLong(t, out ticket)
                || !TryBigToNonNegativeLong(s, out shard)
                || !TryBigToNonNegativeLong(e, out exp))
            {
                code = ErrorCodes.BadPacket;
                return false;
            }

            var ids = new List<int>(itemAgg.Keys);
            ids.Sort();
            var list = new List<ItemAmount>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                BigInteger amt = itemAgg[id];
                if (amt.IsZero)
                    continue;
                if (amt < BigInteger.Zero || amt > long.MaxValue)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                list.Add(new ItemAmount(id, (long)amt));
            }

            items = Array.AsReadOnly(list.ToArray());
            return true;
        }

        static bool TryAccumulateItems(
            IReadOnlyList<ItemAmount> items,
            Dictionary<int, BigInteger> aggregated,
            RewardRules rules,
            List<string> diagnostics,
            out int code)
        {
            code = ErrorCodes.Ok;
            for (int i = 0; i < items.Count; i++)
            {
                ItemAmount a = items[i];
                if (a.ItemId <= 0)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                if (a.Amount < 0)
                {
                    diagnostics.Add(DiagNegativeReward);
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                if (a.Amount == 0)
                    continue;

                if (rules.IsCurrencyItem(a.ItemId))
                {
                    diagnostics.Add(DiagCurrencyAsBagItem);
                    code = ErrorCodes.ItemInvalid;
                    return false;
                }

                if (aggregated.TryGetValue(a.ItemId, out BigInteger sum))
                    aggregated[a.ItemId] = sum + a.Amount;
                else
                    aggregated[a.ItemId] = a.Amount;
            }
            return true;
        }

        static bool TryValidateNonNegativeCurrency(
            long gold, long gem, long ticket, long shard, List<string> diagnostics)
        {
            if (gold < 0 || gem < 0 || ticket < 0 || shard < 0)
            {
                diagnostics.Add(DiagNegativeReward);
                return false;
            }
            return true;
        }

        static bool TryAddCurrencyChecked(
            long current,
            BigInteger add,
            out long next,
            List<string> diagnostics)
        {
            next = current;
            // add==0 快捷路径也须拒绝负源；禁止负币保留或被后续奖励洗成合法。
            if (current < 0)
            {
                diagnostics.Add(DiagNegativeReward);
                return false;
            }
            if (add.IsZero)
                return true;
            if (add < BigInteger.Zero)
            {
                diagnostics.Add(DiagNegativeReward);
                return false;
            }
            BigInteger sum = (BigInteger)current + add;
            if (sum < BigInteger.Zero || sum > long.MaxValue)
                return false;
            next = (long)sum;
            return true;
        }

        static bool TryBigToNonNegativeLong(BigInteger value, out long result)
        {
            result = 0;
            if (value < BigInteger.Zero || value > long.MaxValue)
                return false;
            result = (long)value;
            return true;
        }
    }
}
