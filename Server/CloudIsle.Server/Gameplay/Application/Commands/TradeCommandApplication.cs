using System;
using CloudIsle.Domain.Daily;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Reward;
using CloudIsle.Domain.Trade;
using CloudIsle.Net;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// D04 交易应用边界：Sell/Expand/Buy 规则效果交 Composer 恰好一次；不独立事务、不再入队、不自增 revision。
/// </summary>
public static class TradeCommandApplication
{
    /// <summary>
    /// 评估交易命令并合成一次候选。业务失败返回 Fail（Candidate=null）；配置缺失显式抛出。
    /// Buy 的 dayIndex 由 UTC+8 日历从 <paramref name="ctx"/>.NowMs + Global.resetHour 算出。
    /// </summary>
    public static DomainResult<PlayerAggregate> Evaluate(
        PlayerAggregate state,
        TradeCommand command,
        CommandContext ctx)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (ctx == null)
            throw new ArgumentNullException(nameof(ctx));
        if (ctx.Config == null)
            throw new ArgumentException("config required", nameof(ctx));

        // TradeState.FromAggregate → InventoryState 对 <=0 会抛；与 Composer 同源边界，契约失败不得异常逃出。
        if (state.BagSlots <= 0 || state.BagSlots > InventoryRules.DefaultMaxBagSlots)
        {
            return DomainResult<PlayerAggregate>.Fail(
                ErrorCodes.BadPacket,
                new[] { GameplayEffectComposer.DiagInvalidSourceBagSlots });
        }

        TradeState tradeState = TradeState.FromAggregate(state);
        TradeResult trade;
        DomainEffects effects;
        switch (command.Kind)
        {
            case TradeCommandKind.Sell:
            {
                if (command.SellIntent == null)
                    throw new ArgumentException("SellIntent required", nameof(command));
                ItemRules itemRules = ItemRules.FromSnapshot(ctx.Config);
                trade = TradePolicy.Sell(tradeState, command.SellIntent, itemRules);
                if (!trade.Success)
                    return DomainResult<PlayerAggregate>.Fail(trade.Code, trade.Diagnostics);

                effects = new DomainEffects(
                    itemCosts: trade.ItemCosts,
                    rewardSources: new[] { new RewardBundle(gold: trade.GoldReward) });
                break;
            }
            case TradeCommandKind.Expand:
            {
                ExpandRules expandRules = ExpandRules.FromSnapshot(ctx.Config);
                trade = TradePolicy.Expand(tradeState, expandRules);
                if (!trade.Success)
                    return DomainResult<PlayerAggregate>.Fail(trade.Code, trade.Diagnostics);

                effects = new DomainEffects(
                    goldCost: trade.GoldCost,
                    targetBagSlots: trade.BagSlotsAfter);
                break;
            }
            case TradeCommandKind.Buy:
            {
                if (command.BuyIntent == null)
                    throw new ArgumentException("BuyIntent required", nameof(command));
                ShopRules shopRules = ShopRules.FromSnapshot(ctx.Config);
                int resetHour = DailyConfig.RequireResetHour(ctx.Config);
                int day = DailyCalendar.GetDayIndex(ctx.NowMs, resetHour);
                trade = TradePolicy.Buy(tradeState, command.BuyIntent.Value, shopRules, day);
                if (!trade.Success)
                    return DomainResult<PlayerAggregate>.Fail(trade.Code, trade.Diagnostics);

                effects = new DomainEffects(
                    goldCost: trade.GoldCost,
                    gemCost: trade.GemCost,
                    ticketCost: trade.TicketCost,
                    shardCost: trade.ShardCost,
                    rewardSources: new[] { new RewardBundle(items: trade.ItemRewards) },
                    shopPurchasesAfter: trade.ShopPurchasesAfter);
                break;
            }
            default:
                return DomainResult<PlayerAggregate>.Fail(
                    ErrorCodes.BadPacket,
                    new[] { "unsupported-trade-kind" });
        }

        return GameplayEffectComposer.Compose(state, effects, ctx);
    }
}
