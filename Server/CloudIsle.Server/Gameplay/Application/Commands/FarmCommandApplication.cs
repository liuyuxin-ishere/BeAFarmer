using System;
using System.Collections.Generic;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Random;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// D05/D06/D07/D08 农场应用边界：Unlock / 单地块 Plant / Water / Fertilize / Harvest → Composer 恰好一次；
/// 批量（T034）经 <see cref="EvaluateBatch"/> 直接返回 <see cref="FarmBatchExecutor"/> 已合成候选，不再二次合成。
/// 不独立事务/再入队/自增 revision。
/// </summary>
public static class FarmCommandApplication
{
    /// <summary>
    /// 稳定批量：委托 <see cref="FarmBatchExecutor.Evaluate"/>；调用方负责 Normalize。
    /// </summary>
    public static BatchDomainResult EvaluateBatch(
        PlayerAggregate state,
        NormalizedFarmBatch batch,
        CommandContext ctx)
        => FarmBatchExecutor.Evaluate(state, batch, ctx);

    /// <summary>
    /// 评估农场命令并合成一次候选。业务失败 Candidate=null；配置缺失显式抛出。
    /// </summary>
    public static DomainResult<PlayerAggregate> Evaluate(
        PlayerAggregate state,
        FarmCommand command,
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

        // 与 Composer/Trade 同源：非法容量不得异常逃出。
        if (state.BagSlots <= 0 || state.BagSlots > InventoryRules.DefaultMaxBagSlots)
        {
            return DomainResult<PlayerAggregate>.Fail(
                ErrorCodes.BadPacket,
                new[] { GameplayEffectComposer.DiagInvalidSourceBagSlots });
        }

        switch (command.Kind)
        {
            case FarmCommandKind.Unlock:
            {
                // 显式 Fail：负等级/金币等非法来源；禁止借 FarmState 构造异常逃出，无 catch-all。
                if (state.Level < 0 || state.Gold < 0)
                {
                    return DomainResult<PlayerAggregate>.Fail(
                        ErrorCodes.BadPacket,
                        new[] { FarmStatePolicy.DiagUnlockMalformed });
                }

                PlotRules plotRules = PlotRules.FromSnapshot(ctx.Config);
                FarmState farmState = FarmState.FromAggregate(state);
                FarmUnlockResult unlock = FarmStatePolicy.Unlock(
                    farmState, UnlockPlotIntent.Default, plotRules);
                if (!unlock.Success)
                    return DomainResult<PlayerAggregate>.Fail(unlock.Code, unlock.Diagnostics);

                var patches = new AggregateModulePatch[unlock.UnlockedPlots.Count];
                for (int i = 0; i < unlock.UnlockedPlots.Count; i++)
                    patches[i] = new PlotReplacePatch(unlock.UnlockedPlots[i]);

                var effects = new DomainEffects(
                    goldCost: unlock.GoldCost,
                    basePatches: patches);
                // ShopPurchasesAfter/AcceptedTasksAfter=null → Composer 保留源 typed sidecar，
                // 解锁不得清空购买计数或已接受收获规格。
                return GameplayEffectComposer.Compose(state, effects, ctx);
            }
            case FarmCommandKind.Plant:
            {
                if (command.PlantIntent == null)
                {
                    return DomainResult<PlayerAggregate>.Fail(
                        ErrorCodes.BadPacket,
                        new[] { PlantPolicy.DiagPlantBadIntent });
                }

                if (state.Level < 0)
                {
                    return DomainResult<PlayerAggregate>.Fail(
                        ErrorCodes.BadPacket,
                        new[] { PlantPolicy.DiagPlantMalformed });
                }

                CropRules cropRules = CropRules.FromSnapshot(ctx.Config);
                FarmInput farmInput = FarmInput.FromAggregate(state);
                FarmPlantResult plant = PlantPolicy.Evaluate(
                    farmInput, command.PlantIntent.Value, cropRules, ctx.NowMs);
                if (!plant.Success)
                    return DomainResult<PlayerAggregate>.Fail(plant.Code, plant.Diagnostics);

                // PlotAfter 为 Nullable<PlotStateView>：! 不拆包，成功后用 .Value（与 patch 同源）。
                PlotStateView grown = plant.PlotAfter.Value;
                AcceptedTaskState acceptedAfter = state.AcceptedTasks.WithHarvest(
                    grown.PlotId, plant.AcceptedSpec!);
                var effects = new DomainEffects(
                    itemCosts: new[] { new ItemAmount(plant.SeedId, 1) },
                    basePatches: new AggregateModulePatch[] { new PlotReplacePatch(grown) },
                    acceptedTasksAfter: acceptedAfter);
                // ShopPurchasesAfter=null → 保留商店配额；AcceptedTasksAfter 写入本块规格并保留 unknown。
                return GameplayEffectComposer.Compose(state, effects, ctx);
            }
            case FarmCommandKind.Water:
            {
                if (command.PlotIntent == null)
                {
                    return DomainResult<PlayerAggregate>.Fail(
                        ErrorCodes.BadPacket,
                        new[] { CropCarePolicy.DiagCareBadIntent });
                }

                CropCareRules careRules = CropCareRules.FromSnapshot(ctx.Config);
                FarmInput farmInput = FarmInput.FromAggregate(state);
                FarmCareResult care = CropCarePolicy.Water(
                    farmInput, command.PlotIntent.Value, careRules, ctx.NowMs);
                if (!care.Success)
                    return DomainResult<PlayerAggregate>.Fail(care.Code, care.Diagnostics);

                // PlotAfter 为 Nullable<PlotStateView>：成功后用 .Value（! 不拆包）。
                PlotStateView watered = care.PlotAfter.Value;
                var effects = new DomainEffects(
                    basePatches: new AggregateModulePatch[] { new PlotReplacePatch(watered) });
                // 无物品/货币成本；AcceptedTasksAfter/ShopPurchasesAfter=null → 保留源 sidecar。
                return GameplayEffectComposer.Compose(state, effects, ctx);
            }
            case FarmCommandKind.Fertilize:
            {
                if (command.PlotIntent == null)
                {
                    return DomainResult<PlayerAggregate>.Fail(
                        ErrorCodes.BadPacket,
                        new[] { CropCarePolicy.DiagCareBadIntent });
                }

                CropCareRules careRules = CropCareRules.FromSnapshot(ctx.Config);
                FarmInput farmInput = FarmInput.FromAggregate(state);
                FarmCareResult care = CropCarePolicy.Fertilize(
                    farmInput, command.PlotIntent.Value, careRules, ctx.NowMs);
                if (!care.Success)
                    return DomainResult<PlayerAggregate>.Fail(care.Code, care.Diagnostics);

                // PlotAfter / ConsumedFertilizerItemId：成功后用 .Value；与扣肥同一 Composer。
                PlotStateView fertilized = care.PlotAfter.Value;
                var effects = new DomainEffects(
                    itemCosts: new[] { new ItemAmount(care.ConsumedFertilizerItemId, 1) },
                    basePatches: new AggregateModulePatch[] { new PlotReplacePatch(fertilized) });
                // AcceptedTasksAfter/ShopPurchasesAfter=null → 保留源 sidecar（paid/opaque）。
                return GameplayEffectComposer.Compose(state, effects, ctx);
            }
            case FarmCommandKind.Harvest:
            {
                if (command.PlotIntent == null)
                {
                    return DomainResult<PlayerAggregate>.Fail(
                        ErrorCodes.BadPacket,
                        new[] { HarvestPolicy.DiagHarvestBadIntent });
                }

                IRandomSource random = ctx.Random
                    ?? throw new ArgumentException("harvest requires random", nameof(ctx));

                CropRules cropRules = CropRules.FromSnapshot(ctx.Config);
                FarmInput farmInput = FarmInput.FromAggregate(state);
                FarmHarvestResult harvest = HarvestPolicy.Evaluate(
                    farmInput, command.PlotIntent.Value, cropRules, ctx.NowMs, random);
                return ComposeHarvestSettlement(state, harvest, ctx);
            }
            default:
                return DomainResult<PlayerAggregate>.Fail(
                    ErrorCodes.BadPacket,
                    new[] { "unsupported-farm-kind" });
        }
    }

    /// <summary>
    /// 将已抽选的 <see cref="FarmHarvestResult"/> 合成一次候选（奖+经验+Empty+去目标 paid）。
    /// 供正式 Evaluate(Harvest) 与规则在持有结算快照后复用；失败不改源。
    /// </summary>
    public static DomainResult<PlayerAggregate> ComposeHarvestSettlement(
        PlayerAggregate state,
        FarmHarvestResult harvest,
        CommandContext ctx)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (harvest == null)
            throw new ArgumentNullException(nameof(harvest));
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

        // 资格-only（Success && !HasSettlement）或失败：不得用 Code0 冒充业务拒绝。
        if (!harvest.Success || !harvest.HasSettlement)
        {
            var diagnostics = new List<string>();
            for (int i = 0; i < harvest.Diagnostics.Count; i++)
                diagnostics.Add(harvest.Diagnostics[i]);
            if (harvest.Success && !harvest.HasSettlement)
                diagnostics.Add(HarvestPolicy.DiagHarvestSettlementRequired);
            int code = harvest.Code != 0 ? harvest.Code : ErrorCodes.BadPacket;
            return DomainResult<PlayerAggregate>.Fail(code, diagnostics);
        }

        // PlotAfter 为 Nullable：成功结算后用 .Value。
        PlotStateView cleared = harvest.PlotAfter.Value;
        AcceptedTaskState acceptedAfter = state.AcceptedTasks.WithoutHarvest(cleared.PlotId);
        var effects = new DomainEffects(
            rewardSources: new[]
            {
                new RewardBundle(
                    items: new[] { new ItemAmount(harvest.YieldItemId, harvest.YieldCount) },
                    exp: harvest.ExpGained)
            },
            basePatches: new AggregateModulePatch[] { new PlotReplacePatch(cleared) },
            acceptedTasksAfter: acceptedAfter);
        // ShopPurchasesAfter=null → 保留商店；AcceptedTasksAfter 仅去掉目标 paid，保留他块/unknown。
        return GameplayEffectComposer.Compose(state, effects, ctx);
    }
}
