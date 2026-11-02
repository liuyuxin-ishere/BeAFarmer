using System;
using System.Collections.Generic;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Gather;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Random;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// D09 采集应用边界：GatherPolicy → Composer 恰好一次；
/// 不独立事务/再入队/自增 revision。容量 301 时 Candidate=null，冷却不推进。
/// 正式网络注册/回执由 <see cref="GatherGameplayRule"/> 与既有 CommitCore/Publisher 承担。
/// </summary>
public static class GatherCommandApplication
{
    /// <summary>
    /// 评估采集命令并合成一次候选。业务失败 Candidate=null；配置缺失显式抛出。
    /// </summary>
    public static DomainResult<PlayerAggregate> Evaluate(
        PlayerAggregate state,
        GatherCommand command,
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
                new[] { GatherPolicy.DiagGatherBadIntent });
        }

        IRandomSource random = ctx.Random
            ?? throw new ArgumentException("gather requires random", nameof(ctx));

        GatherRules rules = GatherRules.FromSnapshot(ctx.Config);
        GatherInput input = GatherInput.FromAggregate(state);
        GatherResult gather = GatherPolicy.Evaluate(
            input, command.Intent, rules, ctx.NowMs, random);
        return ComposeGatherSettlement(state, gather, ctx);
    }

    /// <summary>
    /// 将已抽选的 <see cref="GatherResult"/> 合成一次候选（普通+稀有+经验+节点冷却）。
    /// 失败不改源；满包/赠品净容量失败同样不推进冷却。
    /// </summary>
    public static DomainResult<PlayerAggregate> ComposeGatherSettlement(
        PlayerAggregate state,
        GatherResult gather,
        CommandContext ctx)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (gather == null)
            throw new ArgumentNullException(nameof(gather));
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

        if (!gather.Success || !gather.HasSettlement)
        {
            var diagnostics = new List<string>();
            for (int i = 0; i < gather.Diagnostics.Count; i++)
                diagnostics.Add(gather.Diagnostics[i]);
            if (gather.Success && !gather.HasSettlement)
                diagnostics.Add(GatherPolicy.DiagGatherSettlementRequired);
            int code = gather.Code != 0 ? gather.Code : ErrorCodes.BadPacket;
            return DomainResult<PlayerAggregate>.Fail(code, diagnostics);
        }

        NodeStateView nodeAfter = gather.NodeAfter
            ?? throw new InvalidOperationException("settlement missing nodeAfter");

        var items = new List<ItemAmount>(2)
        {
            new ItemAmount(gather.OrdinaryItemId, gather.OrdinaryCount),
        };
        if (gather.RareHit && gather.RareCount > 0 && gather.RareItemId > 0)
            items.Add(new ItemAmount(gather.RareItemId, gather.RareCount));

        var effects = new DomainEffects(
            rewardSources: new[]
            {
                new RewardBundle(items: items, exp: gather.ExpGained),
            },
            basePatches: new AggregateModulePatch[] { new NodeReplacePatch(nodeAfter) });
        // AcceptedTasksAfter/ShopPurchasesAfter=null → 保留源 sidecar；不自增 revision。
        return GameplayEffectComposer.Compose(state, effects, ctx);
    }
}
