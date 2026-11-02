using System;
using System.Collections.Generic;
using System.Numerics;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Progression;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Domain.Effects
{
    /// <summary>
    /// D02/D03 效果合成：独立消耗门 → 基础模块补丁 → 成长 → 容量效果（若有）先入工作态再参与成本/最终库存 → 普通+等级赠品一次候选。
    /// 失败全无：源对象不变、Candidate=null、不 Save/Ntf/自增 revision；不递归。
    /// </summary>
    public static class GameplayEffectComposer
    {
        public const string DiagNegativeCost = "negative-cost-not-credit";
        public const string DiagNegativeSourceCurrency = "negative-source-currency-rejected";
        public const string DiagExpUnsupported = RewardPolicy.DiagExpUnsupported;
        public const string DiagProgressionUnsupported = RewardPolicy.DiagProgressionUnsupported;
        public const string DiagCurrencyCostAsItem = "currency-item-must-use-currency-cost-fields";
        public const string DiagSelfFinanceBlocked = "consumption-gate-rewards-cannot-fund-costs";
        public const string DiagPatchBudgetExceeded = "module-patch-budget-exceeded";
        public const string DiagNegativeExpGain = ProgressionPolicy.DiagNegativeExp;
        public const string DiagExpSumOverflow = "exp-gain-sum-overflow";
        public const string DiagNegativeTargetBagSlots = "negative-target-bag-slots";
        public const string DiagInvalidSourceBagSlots = "invalid-source-bag-slots";

        /// <summary>与 MaxRewardEntries / MaxCostEntries / MaxDeltaEntries 对齐的有界补丁默认上限。</summary>
        public const int DefaultMaxPatchEntries = 256;

        /// <summary>
        /// 合成候选聚合。身份 / grant / 未触及模块 / revision 保留；level/exp/tools 可由成长更新。
        /// </summary>
        public static DomainResult<PlayerAggregate> Compose(
            PlayerAggregate source,
            DomainEffects effects,
            CommandContext ctx)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (effects == null)
                throw new ArgumentNullException(nameof(effects));
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));

            var diagnostics = new List<string>();
            RewardRules rules = RewardRules.FromSnapshot(ctx.Config);
            ProgressionRules progressionRules = ProgressionRules.FromSnapshot(ctx.Config);

            // DomainEffects.ExpGain 与各 RewardBundle.Exp 各计一次；负值逐来源拒绝。
            if (effects.ExpGain < 0)
            {
                diagnostics.Add(DiagNegativeExpGain);
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 消耗/奖励前拒绝任何原始负货币（四字段），不得洗成合法候选。
            if (source.Gold < 0 || source.Gem < 0 || source.Ticket < 0 || source.Shard < 0)
            {
                diagnostics.Add(DiagNegativeSourceCurrency);
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 原始容量须落在 (0, MaxBagSlots]；显式 Fail，禁止 InventoryState 构造抛出，也禁止目标容量洗白 0/负/513/intMax。
            if (source.BagSlots <= 0 || source.BagSlots > rules.Inventory.MaxBagSlots)
            {
                diagnostics.Add(DiagInvalidSourceBagSlots);
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryValidateNonNegativeCosts(effects, diagnostics, out int costValidateCode))
                return DomainResult<PlayerAggregate>.Fail(costValidateCode, diagnostics);

            if (effects.ItemCosts.Count > rules.MaxCostEntries)
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);

            if (effects.BasePatches.Count > DefaultMaxPatchEntries)
            {
                diagnostics.Add(DiagPatchBudgetExceeded);
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 多来源奖励先合并（精确算术）；重复消耗同样先聚合。
            if (!RewardPolicy.TryMergeBundles(
                    effects.RewardSources,
                    rules,
                    out long rewardGold,
                    out long rewardGem,
                    out long rewardTicket,
                    out long rewardShard,
                    out IReadOnlyList<ItemAmount> rewardItems,
                    out long rewardExp,
                    diagnostics,
                    out int mergeCode))
            {
                return DomainResult<PlayerAggregate>.Fail(mergeCode, diagnostics);
            }

            if (!TrySumExpGain(effects.ExpGain, rewardExp, out long totalExp, diagnostics))
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);

            if (!TryAggregateItemCosts(
                    effects.ItemCosts,
                    rules,
                    diagnostics,
                    out Dictionary<int, BigInteger> costByItem,
                    out int aggCostCode))
            {
                return DomainResult<PlayerAggregate>.Fail(aggCostCode, diagnostics);
            }

            // 独立消耗门：仅用原资产验能否付成本；禁止同笔奖励自我垫资。
            if (!TryConsumptionGate(
                    source,
                    effects.GoldCost,
                    effects.GemCost,
                    effects.TicketCost,
                    effects.ShardCost,
                    costByItem,
                    out int gateCode))
            {
                if (gateCode == ErrorCodes.ItemNotEnough
                    || gateCode == ErrorCodes.GoldNotEnough
                    || gateCode == ErrorCodes.GemNotEnough
                    || gateCode == ErrorCodes.TicketNotEnough
                    || gateCode == ErrorCodes.ShardNotEnough)
                {
                    diagnostics.Add(DiagSelfFinanceBlocked);
                }
                return DomainResult<PlayerAggregate>.Fail(gateCode, diagnostics);
            }

            // 基础模块补丁（工作副本）；后段失败时丢弃，源不变。
            var modules = new WorkingModules(source);
            for (int i = 0; i < effects.BasePatches.Count; i++)
            {
                if (!effects.BasePatches[i].TryApply(modules, diagnostics, out int patchCode))
                    return DomainResult<PlayerAggregate>.Fail(patchCode, diagnostics);
            }

            // D03：成长（不入包）；拥有判定见补丁后工具 + 成本前背包；等级赠品不递归再成长。
            LevelState levelState = LevelState.FromOwnershipViews(
                source.Level,
                source.Exp,
                modules.Tools,
                source.Bag);
            ProgressionResult progression = ProgressionPolicy.Evaluate(levelState, totalExp, progressionRules);
            if (!progression.Success)
            {
                for (int i = 0; i < progression.Diagnostics.Count; i++)
                    diagnostics.Add(progression.Diagnostics[i]);
                return DomainResult<PlayerAggregate>.Fail(progression.Code, diagnostics);
            }

            for (int i = 0; i < progression.Diagnostics.Count; i++)
                diagnostics.Add(progression.Diagnostics[i]);

            // 必要 Tool 记录并入工作副本（不降级已有）。
            if (!TryEnsureToolsNoDowngrade(modules, progression.ToolsToEnsure, diagnostics, out int toolCode))
                return DomainResult<PlayerAggregate>.Fail(toolCode, diagnostics);

            if (!TrySubtractCurrency(source.Gold, effects.GoldCost, out long goldAfterCost, diagnostics)
                || !TrySubtractCurrency(source.Gem, effects.GemCost, out long gemAfterCost, diagnostics)
                || !TrySubtractCurrency(source.Ticket, effects.TicketCost, out long ticketAfterCost, diagnostics)
                || !TrySubtractCurrency(source.Shard, effects.ShardCost, out long shardAfterCost, diagnostics))
            {
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 物品成本 delta；容量效果（若有）先在独立工作态验证并落地，再参与成本/最终库存校验。
            // 原始消耗门仍只用源资产；源容量已在入口校验；TrySetBagSlots 再拒非法目标/占格溢出。
            var costDeltas = new List<ItemDelta>(costByItem.Count);
            foreach (KeyValuePair<int, BigInteger> pair in costByItem)
            {
                if (pair.Value.IsZero)
                    continue;
                if (pair.Value < BigInteger.Zero || pair.Value > long.MaxValue)
                    return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
                costDeltas.Add(new ItemDelta(pair.Key, -(long)pair.Value));
            }

            if (effects.TargetBagSlots < 0)
            {
                diagnostics.Add(DiagNegativeTargetBagSlots);
                return DomainResult<PlayerAggregate>.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            InventoryState invWorking = InventoryState.FromAggregate(source);
            // 有界容量效果：与金币扣款同一候选；禁止 Composer 成功后再改格数。
            if (effects.TargetBagSlots > 0)
            {
                InventoryResult afterCap = InventoryPolicy.TrySetBagSlots(
                    invWorking, effects.TargetBagSlots, rules.Inventory);
                if (!afterCap.Success)
                {
                    for (int i = 0; i < afterCap.Diagnostics.Count; i++)
                        diagnostics.Add(afterCap.Diagnostics[i]);
                    return DomainResult<PlayerAggregate>.Fail(afterCap.Code, diagnostics);
                }

                for (int i = 0; i < afterCap.Diagnostics.Count; i++)
                    diagnostics.Add(afterCap.Diagnostics[i]);
                invWorking = afterCap.Candidate!;
            }

            InventoryResult afterCostInv = InventoryPolicy.TryApply(invWorking, costDeltas, rules.Inventory);
            if (!afterCostInv.Success)
            {
                for (int i = 0; i < afterCostInv.Diagnostics.Count; i++)
                    diagnostics.Add(afterCostInv.Diagnostics[i]);
                return DomainResult<PlayerAggregate>.Fail(afterCostInv.Code, diagnostics);
            }

            for (int i = 0; i < afterCostInv.Diagnostics.Count; i++)
                diagnostics.Add(afterCostInv.Diagnostics[i]);

            InventoryState inventoryAfterCost = afterCostInv.Candidate!;

            var economyAfterCost = new EconomyState(
                goldAfterCost,
                gemAfterCost,
                ticketAfterCost,
                shardAfterCost,
                inventoryAfterCost);

            // 经验已消费；合并包 exp=0。LevelGifts 经 ProgressionResult 并入最终容量。
            var mergedReward = new RewardBundle(
                rewardGold,
                rewardGem,
                rewardTicket,
                rewardShard,
                rewardItems,
                exp: 0);

            RewardResult rewardResult = RewardPolicy.Evaluate(
                economyAfterCost,
                mergedReward,
                progression,
                rules);
            if (!rewardResult.Success)
            {
                for (int i = 0; i < rewardResult.Diagnostics.Count; i++)
                    diagnostics.Add(rewardResult.Diagnostics[i]);
                // 容量失败等后段错误：补丁/扣费/成长均不提交（无 Candidate）。
                return DomainResult<PlayerAggregate>.Fail(rewardResult.Code, diagnostics);
            }

            for (int i = 0; i < rewardResult.Diagnostics.Count; i++)
                diagnostics.Add(rewardResult.Diagnostics[i]);

            PlayerAggregate candidate = BuildCandidate(
                source,
                modules,
                rewardResult.Candidate!,
                progression.LevelAfter,
                progression.ExpAfter,
                effects.ShopPurchasesAfter,
                effects.AcceptedTasksAfter);
            return DomainResult<PlayerAggregate>.Ok(candidate, diagnostics);
        }

        static bool TrySumExpGain(long effectsExp, long bundleExp, out long total, List<string> diagnostics)
        {
            total = 0;
            if (effectsExp < 0 || bundleExp < 0)
            {
                diagnostics.Add(DiagNegativeExpGain);
                return false;
            }
            try
            {
                total = checked(effectsExp + bundleExp);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagExpSumOverflow);
                return false;
            }
            return true;
        }

        static bool TryEnsureToolsNoDowngrade(
            WorkingModules modules,
            IReadOnlyList<ToolStateView> toolsToEnsure,
            List<string> diagnostics,
            out int code)
        {
            code = ErrorCodes.Ok;
            for (int i = 0; i < toolsToEnsure.Count; i++)
            {
                ToolStateView next = toolsToEnsure[i];
                if (!ToolReplacePatch.TryValidateStructure(next, diagnostics, out code))
                    return false;

                bool found = false;
                for (int t = 0; t < modules.Tools.Count; t++)
                {
                    if (modules.Tools[t].ToolId != next.ToolId)
                        continue;
                    found = true;
                    // 已有：永不降级；等级赠品不抬高已有高等级。
                    if (modules.Tools[t].Level < next.Level)
                        modules.Tools[t] = next;
                    break;
                }

                if (!found)
                    modules.Tools.Add(next);
            }
            return true;
        }

        static PlayerAggregate BuildCandidate(
            PlayerAggregate source,
            WorkingModules modules,
            EconomyState economy,
            int levelAfter,
            int expAfter,
            CloudIsle.Domain.Trade.ShopPurchaseCounts? shopPurchasesAfter,
            CloudIsle.Domain.Farm.AcceptedTaskState? acceptedTasksAfter)
        {
            var bag = new BagItemView[economy.Inventory.Stacks.Count];
            for (int i = 0; i < economy.Inventory.Stacks.Count; i++)
            {
                InventoryStackView s = economy.Inventory.Stacks[i];
                bag[i] = new BagItemView(s.ItemId, s.Count);
            }

            return new PlayerAggregate(
                source.PlayerId,
                source.Nickname,
                levelAfter,
                expAfter,
                economy.Gold,
                economy.Gem,
                economy.Ticket,
                economy.Shard,
                economy.Inventory.BagSlots,
                source.GuideStep,
                source.GrantVersion,
                source.Revision,
                modules.Plots,
                modules.Nodes,
                modules.Tools,
                modules.Facilities,
                bag,
                source.OrdersInitializedEmpty,
                shopPurchasesAfter ?? source.ShopPurchases,
                acceptedTasksAfter ?? source.AcceptedTasks);
        }

        static bool TryValidateNonNegativeCosts(DomainEffects effects, List<string> diagnostics, out int code)
        {
            code = ErrorCodes.Ok;
            if (effects.GoldCost < 0
                || effects.GemCost < 0
                || effects.TicketCost < 0
                || effects.ShardCost < 0)
            {
                diagnostics.Add(DiagNegativeCost);
                code = ErrorCodes.BadPacket;
                return false;
            }

            for (int i = 0; i < effects.ItemCosts.Count; i++)
            {
                if (effects.ItemCosts[i].Amount < 0)
                {
                    diagnostics.Add(DiagNegativeCost);
                    code = ErrorCodes.BadPacket;
                    return false;
                }
            }

            return true;
        }

        static bool TryAggregateItemCosts(
            IReadOnlyList<ItemAmount> costs,
            RewardRules rules,
            List<string> diagnostics,
            out Dictionary<int, BigInteger> costByItem,
            out int code)
        {
            costByItem = new Dictionary<int, BigInteger>();
            code = ErrorCodes.Ok;
            for (int i = 0; i < costs.Count; i++)
            {
                ItemAmount c = costs[i];
                if (c.ItemId <= 0)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                if (c.Amount == 0)
                    continue;
                if (rules.IsCurrencyItem(c.ItemId))
                {
                    diagnostics.Add(DiagCurrencyCostAsItem);
                    code = ErrorCodes.ItemInvalid;
                    return false;
                }
                // 未知新 ID / legacy：不可操作（与 InventoryPolicy 一致），不得靠奖励垫资绕过。
                if (!rules.Inventory.IsKnownItem(c.ItemId))
                {
                    code = ErrorCodes.ItemInvalid;
                    return false;
                }

                if (costByItem.TryGetValue(c.ItemId, out BigInteger sum))
                    costByItem[c.ItemId] = sum + c.Amount;
                else
                    costByItem[c.ItemId] = c.Amount;
            }

            foreach (KeyValuePair<int, BigInteger> pair in costByItem)
            {
                if (pair.Value < BigInteger.Zero || pair.Value > long.MaxValue)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
            }

            return true;
        }

        static bool TryConsumptionGate(
            PlayerAggregate source,
            long goldCost,
            long gemCost,
            long ticketCost,
            long shardCost,
            Dictionary<int, BigInteger> costByItem,
            out int code)
        {
            code = ErrorCodes.Ok;
            if (goldCost > 0 && source.Gold < goldCost)
            {
                code = ErrorCodes.GoldNotEnough;
                return false;
            }
            if (gemCost > 0 && source.Gem < gemCost)
            {
                code = ErrorCodes.GemNotEnough;
                return false;
            }
            if (ticketCost > 0 && source.Ticket < ticketCost)
            {
                code = ErrorCodes.TicketNotEnough;
                return false;
            }
            if (shardCost > 0 && source.Shard < shardCost)
            {
                code = ErrorCodes.ShardNotEnough;
                return false;
            }

            var have = new Dictionary<int, long>();
            for (int i = 0; i < source.Bag.Count; i++)
            {
                BagItemView b = source.Bag[i];
                if (b.ItemId <= 0 || b.Count <= 0)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                if (have.ContainsKey(b.ItemId))
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                have[b.ItemId] = b.Count;
            }

            foreach (KeyValuePair<int, BigInteger> pair in costByItem)
            {
                long need = (long)pair.Value;
                if (!have.TryGetValue(pair.Key, out long own) || own < need)
                {
                    code = ErrorCodes.ItemNotEnough;
                    return false;
                }
            }

            return true;
        }

        static bool TrySubtractCurrency(long current, long cost, out long next, List<string> diagnostics)
        {
            next = current;
            if (cost == 0)
                return true;
            if (cost < 0)
            {
                diagnostics.Add(DiagNegativeCost);
                return false;
            }
            try
            {
                next = checked(current - cost);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (next < 0)
                return false;
            return true;
        }
    }
}
