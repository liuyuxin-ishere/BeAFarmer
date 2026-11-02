using System;
using System.Collections.Generic;
using System.Numerics;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Domain.Trade
{
    /// <summary>
    /// D04 纯交易策略（Sell + Expand + Buy）：失败源字段不变；不 Save/Ntf/revision。
    /// 正式 BagSell 重复身份整包无效（与 Canonical 一致），不合并同 ID（对比 InventoryPolicy deltas）。
    /// Expand：规范档位/费用取自 ExpandRules（同份 Global），不 floor 廉价档、不静默重置旧资产。
    /// Buy：价格/币种/等级/限购取自同份 ShopRules；dayIndex 须非负（日历权威由调用方提供）。
    /// </summary>
    public static class TradePolicy
    {
        public const string DiagEmptyIntent = "sell-intent-empty";
        public const string DiagOversizedIntent = "sell-intent-oversized";
        public const string DiagDuplicateIdentity = "sell-duplicate-item-id";
        public const string DiagNonPositiveLine = "sell-nonpositive-id-or-count";
        public const string DiagUnsellable = "sell-item-unsellable";
        public const string DiagGoldOverflow = "sell-gold-overflow";

        public const string DiagExpandNonCanonical = "expand-noncanonical-slots";
        public const string DiagExpandAtCap = "expand-at-cap";
        public const string DiagExpandOverflow = "expand-slots-overflow";
        public const string DiagExpandNegativeSlots = "expand-negative-or-zero-slots";

        public const string DiagBuyNonPositiveCount = "buy-nonpositive-count";
        public const string DiagBuyUnknownShop = "buy-unknown-shop";
        public const string DiagBuyInvalidCategory = "buy-invalid-category";
        public const string DiagBuyInvalidReward = "buy-invalid-reward-item";
        public const string DiagBuyLevelLocked = "buy-level-locked";
        public const string DiagBuyLimitReached = "buy-limit-reached";
        public const string DiagBuyCostOverflow = "buy-cost-overflow";
        public const string DiagBuyDayIndex = "buy-day-index-invalid";
        public const string DiagBuyCurrencyMetadata = "buy-currency-metadata";

        /// <summary>
        /// 评估出售。成功：ItemCosts + GoldReward（供 Composer）；失败：不改 state，无效果。
        /// 顺序无关：按 itemId 稳定排序后计价。
        /// </summary>
        public static TradeResult Sell(TradeState state, SellIntent intent, ItemRules rules)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (intent == null)
                throw new ArgumentNullException(nameof(intent));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            IReadOnlyList<SellLine> lines = intent.Lines;

            if (lines.Count == 0)
            {
                diagnostics.Add(DiagEmptyIntent);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (lines.Count > rules.MaxSellEntries)
            {
                diagnostics.Add(DiagOversizedIntent);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            var seen = new HashSet<int>();
            var normalized = new List<(int ItemId, int Count)>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                SellLine line = lines[i];
                if (line.ItemId <= 0 || line.Count <= 0)
                {
                    diagnostics.Add(DiagNonPositiveLine);
                    return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                if (!seen.Add(line.ItemId))
                {
                    diagnostics.Add(DiagDuplicateIdentity);
                    return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                normalized.Add((line.ItemId, line.Count));
            }

            normalized.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));

            // 源库存索引（只读）；legacy/未知保留条目可在 bag 中，但不可售。
            var stock = new Dictionary<int, int>();
            for (int i = 0; i < state.Inventory.Stacks.Count; i++)
            {
                InventoryStackView s = state.Inventory.Stacks[i];
                if (s.ItemId <= 0 || s.Count <= 0)
                    return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
                if (stock.ContainsKey(s.ItemId))
                    return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
                stock[s.ItemId] = s.Count;
            }

            if (state.Gold < 0)
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);

            BigInteger totalGold = BigInteger.Zero;
            var costs = new List<ItemAmount>(normalized.Count);

            for (int i = 0; i < normalized.Count; i++)
            {
                int itemId = normalized[i].ItemId;
                int count = normalized[i].Count;

                // Currency / 未知 / 非物理 / sellPrice≤0 / 缺售价 → 306（不可操作）
                if (!rules.IsSellable(itemId) || !rules.TryGetSellPrice(itemId, out int price) || price <= 0)
                {
                    diagnostics.Add(DiagUnsellable);
                    return TradeResult.Fail(ErrorCodes.ItemInvalid, diagnostics);
                }

                if (!stock.TryGetValue(itemId, out int have) || have < count)
                    return TradeResult.Fail(ErrorCodes.ItemNotEnough, diagnostics);

                BigInteger lineGold = (BigInteger)price * count;
                totalGold += lineGold;
                if (totalGold > long.MaxValue)
                {
                    diagnostics.Add(DiagGoldOverflow);
                    return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                costs.Add(new ItemAmount(itemId, count));
            }

            BigInteger finalGold = (BigInteger)state.Gold + totalGold;
            if (finalGold > long.MaxValue)
            {
                diagnostics.Add(DiagGoldOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long goldReward = (long)totalGold;
            return TradeResult.Ok(costs, goldReward, diagnostics);
        }

        /// <summary>
        /// 评估扩容。成功：GoldCost + BagSlotsAfter（供 Composer）；失败：不改 state。
        /// 仅规范档 init+k·step（k=0..costs.Length-1）可扩；达 MaxSlots 拒；非规范/负数/越界显式失败。
        /// </summary>
        public static TradeResult Expand(TradeState state, ExpandRules rules)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            int slots = state.Inventory.BagSlots;
            if (slots <= 0)
            {
                diagnostics.Add(DiagExpandNegativeSlots);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Gold < 0)
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);

            if (slots > rules.MaxSlots)
            {
                diagnostics.Add(DiagExpandOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (slots < rules.InitSlots)
            {
                diagnostics.Add(DiagExpandNonCanonical);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            int delta = slots - rules.InitSlots;
            if (delta % rules.Step != 0)
            {
                diagnostics.Add(DiagExpandNonCanonical);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            int tier = delta / rules.Step;
            if (tier == rules.Costs.Count)
            {
                // 已达上限（如 110）：不再扩。
                diagnostics.Add(DiagExpandAtCap);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (tier < 0 || tier > rules.Costs.Count)
            {
                diagnostics.Add(DiagExpandNonCanonical);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long cost = rules.Costs[tier];
            int nextSlots;
            try
            {
                nextSlots = checked(slots + rules.Step);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagExpandOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (nextSlots > rules.MaxSlots)
            {
                diagnostics.Add(DiagExpandOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Gold < cost)
                return TradeResult.Fail(ErrorCodes.GoldNotEnough, diagnostics);

            return TradeResult.OkExpand(cost, nextSlots, diagnostics);
        }

        /// <summary>
        /// 评估购买。成功：货币成本 + 物品奖励 + 限购计数（供 Composer 一次合成）；
        /// 失败：不改 state。价格仅来自 rules；不信任客户端价。
        /// </summary>
        public static TradeResult Buy(
            TradeState state,
            BuyIntent intent,
            ShopRules rules,
            int dayIndex)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            if (dayIndex < 0)
            {
                diagnostics.Add(DiagBuyDayIndex);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (intent.ShopId <= 0 || intent.Count <= 0)
            {
                diagnostics.Add(DiagBuyNonPositiveCount);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Level < 0 || state.Gold < 0 || state.Gem < 0 || state.Ticket < 0 || state.Shard < 0)
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);

            if (!rules.TryGetOffer(intent.ShopId, out ShopOffer offer))
            {
                diagnostics.Add(DiagBuyUnknownShop);
                return TradeResult.Fail(ErrorCodes.ShopItemInvalid, diagnostics);
            }

            // 正式 M2 仅 Seed/Consumable；其余视为无效商品。
            if (!string.Equals(offer.Category, "Seed", StringComparison.Ordinal)
                && !string.Equals(offer.Category, "Consumable", StringComparison.Ordinal))
            {
                diagnostics.Add(DiagBuyInvalidCategory);
                return TradeResult.Fail(ErrorCodes.ShopItemInvalid, diagnostics);
            }

            if (!rules.IsPhysicalRewardItem(offer.ItemId) || offer.UnitPrice < 0)
            {
                diagnostics.Add(DiagBuyInvalidReward);
                return TradeResult.Fail(ErrorCodes.ShopItemInvalid, diagnostics);
            }

            if (state.Level < offer.UnlockLevel)
            {
                diagnostics.Add(DiagBuyLevelLocked);
                return TradeResult.Fail(ErrorCodes.LevelLocked, diagnostics);
            }

            // limitCount=0 → 不限；否则按数量累计（非次数），exact/+1。
            if (offer.LimitCount > 0)
            {
                int already = state.ShopPurchases.GetCount(offer.ShopId);
                long after;
                try
                {
                    after = checked((long)already + intent.Count);
                }
                catch (OverflowException)
                {
                    diagnostics.Add(DiagBuyLimitReached);
                    return TradeResult.Fail(ErrorCodes.ShopItemInvalid, diagnostics);
                }

                if (after > offer.LimitCount)
                {
                    diagnostics.Add(DiagBuyLimitReached);
                    return TradeResult.Fail(ErrorCodes.ShopItemInvalid, diagnostics);
                }
            }

            BigInteger totalCost;
            try
            {
                totalCost = checked((BigInteger)offer.UnitPrice * intent.Count);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagBuyCostOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (totalCost > long.MaxValue)
            {
                diagnostics.Add(DiagBuyCostOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long cost = (long)totalCost;
            if (cost <= 0)
            {
                diagnostics.Add(DiagBuyCostOverflow);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 计价币种必须经不可变 currency 视图解析，并与 offer 声明一致；禁止只信 Kind、禁止回落 Gold。
            if (!rules.TryGetCurrencyKind(offer.PriceCurrencyItemId, out CurrencyKind mapped)
                || mapped != offer.PriceCurrencyKind
                || !ShopRules.IsKnownCurrencyKind(mapped))
            {
                diagnostics.Add(DiagBuyCurrencyMetadata);
                return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long goldCost = 0, gemCost = 0, ticketCost = 0, shardCost = 0;
            switch (mapped)
            {
                case CurrencyKind.Gold:
                    goldCost = cost;
                    if (state.Gold < goldCost)
                        return TradeResult.Fail(ErrorCodes.GoldNotEnough, diagnostics);
                    break;
                case CurrencyKind.Gem:
                    gemCost = cost;
                    if (state.Gem < gemCost)
                        return TradeResult.Fail(ErrorCodes.GemNotEnough, diagnostics);
                    break;
                case CurrencyKind.Ticket:
                    ticketCost = cost;
                    if (state.Ticket < ticketCost)
                        return TradeResult.Fail(ErrorCodes.TicketNotEnough, diagnostics);
                    break;
                case CurrencyKind.Shard:
                    shardCost = cost;
                    if (state.Shard < shardCost)
                        return TradeResult.Fail(ErrorCodes.ShardNotEnough, diagnostics);
                    break;
                default:
                    diagnostics.Add(DiagBuyCurrencyMetadata);
                    return TradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            ShopPurchaseCounts nextCounts;
            try
            {
                nextCounts = offer.LimitCount > 0
                    ? state.ShopPurchases.WithAdded(offer.ShopId, intent.Count)
                    : state.ShopPurchases.Clone();
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagBuyLimitReached);
                return TradeResult.Fail(ErrorCodes.ShopItemInvalid, diagnostics);
            }

            var rewards = new[] { new ItemAmount(offer.ItemId, intent.Count) };
            return TradeResult.OkBuy(
                goldCost, gemCost, ticketCost, shardCost, rewards, nextCounts, diagnostics);
        }
    }
}
