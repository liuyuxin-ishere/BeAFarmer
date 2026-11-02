using System;
using System.Collections.Generic;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Tool;
using CloudIsle.Net;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// D10 工具升级应用边界：ToolUpgradePolicy → Composer 恰好一次；
/// 金币/材料/工具 patch 与 snapshot/receipt/nextOp 统一提交。
/// 失败 Candidate=null；不推进节点冷却；不另开事务。
/// </summary>
public static class ToolCommandApplication
{
    /// <summary>
    /// 评估升级命令并合成一次候选。业务失败 Candidate=null；配置缺失显式抛出。
    /// </summary>
    public static DomainResult<PlayerAggregate> Evaluate(
        PlayerAggregate state,
        ToolCommand command,
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

        if (state.BagSlots <= 0 || state.BagSlots > InventoryRules.DefaultMaxBagSlots)
        {
            return DomainResult<PlayerAggregate>.Fail(
                ErrorCodes.BadPacket,
                new[] { GameplayEffectComposer.DiagInvalidSourceBagSlots });
        }

        if (state.Level < 0)
        {
            return DomainResult<PlayerAggregate>.Fail(
                ErrorCodes.BadPacket,
                new[] { ToolUpgradePolicy.DiagToolBadIntent });
        }

        ToolRules rules = ToolRules.FromSnapshot(ctx.Config);
        ToolInput input = ToolInput.FromAggregate(state);
        ToolUpgradeResult upgrade = ToolUpgradePolicy.Evaluate(input, command.Intent, rules);
        return ComposeToolUpgradeSettlement(state, upgrade, ctx);
    }

    /// <summary>
    /// 将已声明的 <see cref="ToolUpgradeResult"/> 合成一次候选（扣金币/材料 + 工具等级 patch）。
    /// 失败不改源；不触碰 Nodes。
    /// </summary>
    public static DomainResult<PlayerAggregate> ComposeToolUpgradeSettlement(
        PlayerAggregate state,
        ToolUpgradeResult upgrade,
        CommandContext ctx)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (upgrade == null)
            throw new ArgumentNullException(nameof(upgrade));
        if (ctx == null)
            throw new ArgumentNullException(nameof(ctx));
        if (ctx.Config == null)
            throw new ArgumentException("config required", nameof(ctx));

        if (state.BagSlots <= 0 || state.BagSlots > InventoryRules.DefaultMaxBagSlots)
        {
            return DomainResult<PlayerAggregate>.Fail(
                ErrorCodes.BadPacket,
                new[] { GameplayEffectComposer.DiagInvalidSourceBagSlots });
        }

        if (!upgrade.Success || !upgrade.HasSettlement)
        {
            var diagnostics = new List<string>();
            for (int i = 0; i < upgrade.Diagnostics.Count; i++)
                diagnostics.Add(upgrade.Diagnostics[i]);
            int code = upgrade.Code != 0 ? upgrade.Code : ErrorCodes.BadPacket;
            return DomainResult<PlayerAggregate>.Fail(code, diagnostics);
        }

        ToolStateView toolAfter = upgrade.ToolAfter
            ?? throw new InvalidOperationException("settlement missing toolAfter");

        var effects = new DomainEffects(
            goldCost: upgrade.GoldCost,
            itemCosts: upgrade.ItemCosts,
            basePatches: new AggregateModulePatch[] { new ToolReplacePatch(toolAfter) });
        // AcceptedTasksAfter/ShopPurchasesAfter=null → 保留源 sidecar；不自增 revision；不改 Nodes。
        return GameplayEffectComposer.Compose(state, effects, ctx);
    }
}
