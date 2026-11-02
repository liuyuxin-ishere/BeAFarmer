using System;
using System.Collections.Generic;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Random;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// D08 确定性批量执行：按稳定顺序在本地候选上逐项 Policy+Composer；
    /// 失败不改候选；成功累积；无 DB/网络/revision。整批共用原 ctx。
    /// </summary>
    public static class FarmBatchExecutor
    {
        public const string DiagInvalidSourceBagSlots = "farm-batch-invalid-source-bag-slots";
        public const string DiagAggregateOverflow = "farm-batch-aggregate-overflow";
        public const string DiagHarvestRandomRequired = "farm-batch-harvest-random-required";

        public static BatchDomainResult Evaluate(
            PlayerAggregate source,
            NormalizedFarmBatch batch,
            CommandContext ctx)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (batch == null)
                throw new ArgumentNullException(nameof(batch));
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));
            if (ctx.Config == null)
                throw new ArgumentException("config required", nameof(ctx));
            if (batch.Items.Count < 1)
                throw new ArgumentException("normalized batch empty", nameof(batch));

            // 与单路由同源：非法容量不得异常逃出。
            if (source.BagSlots <= 0 || source.BagSlots > InventoryRules.DefaultMaxBagSlots)
            {
                return AllItemsFail(
                    batch,
                    ErrorCodes.BadPacket,
                    0,
                    new[] { DiagInvalidSourceBagSlots });
            }

            PlayerAggregate local = source;
            var itemResults = new List<FarmBatchItemResult>(batch.Items.Count);
            var gainMap = new SortedDictionary<int, long>();
            long aggExpLong = 0;
            int totalRng = 0;
            int successCount = 0;
            var diagnostics = new List<string>();

            CropRules? cropRules = null;
            if (batch.Kind == FarmBatchKind.Plant || batch.Kind == FarmBatchKind.Harvest)
                cropRules = CropRules.FromSnapshot(ctx.Config);

            IRandomSource? random = ctx.Random;
            if (batch.Kind == FarmBatchKind.Harvest && random == null)
            {
                return AllItemsFail(
                    batch,
                    ErrorCodes.BadPacket,
                    0,
                    new[] { DiagHarvestRandomRequired });
            }

            for (int i = 0; i < batch.Items.Count; i++)
            {
                PlotCommand cmd = batch.Items[i];
                if (batch.Kind == FarmBatchKind.Plant)
                {
                    DomainResult<PlayerAggregate> composed = EvaluatePlantItem(
                        local, cmd, cropRules!, ctx, out FarmBatchItemResult item);
                    totalRng = checked(totalRng + item.RngCalls);
                    itemResults.Add(item);
                    if (!composed.Success || item.Code != 0)
                    {
                        for (int d = 0; d < composed.Diagnostics.Count; d++)
                            diagnostics.Add(composed.Diagnostics[d]);
                        continue;
                    }

                    local = composed.Candidate!;
                    successCount++;
                }
                else
                {
                    DomainResult<PlayerAggregate> composed = EvaluateHarvestItem(
                        local, cmd, cropRules!, ctx, random!, out FarmBatchItemResult item);
                    totalRng = checked(totalRng + item.RngCalls);
                    itemResults.Add(item);
                    if (!composed.Success || item.Code != 0)
                    {
                        for (int d = 0; d < composed.Diagnostics.Count; d++)
                            diagnostics.Add(composed.Diagnostics[d]);
                        continue;
                    }

                    try
                    {
                        if (item.YieldItemId > 0 && item.YieldCount > 0)
                        {
                            if (!gainMap.TryGetValue(item.YieldItemId, out long cur))
                                cur = 0;
                            gainMap[item.YieldItemId] = checked(cur + item.YieldCount);
                        }

                        aggExpLong = checked(aggExpLong + item.ExpGained);
                    }
                    catch (OverflowException)
                    {
                        // 已有本地成功项时不得伪装全失败；溢出须在提交前显式暴露。
                        throw new InvalidOperationException(DiagAggregateOverflow);
                    }

                    local = composed.Candidate!;
                    successCount++;
                }
            }

            var aggGains = new List<ItemAmount>(gainMap.Count);
            foreach (KeyValuePair<int, long> kv in gainMap)
            {
                if (kv.Value > int.MaxValue || kv.Value <= 0)
                    throw new InvalidOperationException(DiagAggregateOverflow);
                aggGains.Add(new ItemAmount(kv.Key, (int)kv.Value));
            }

            if (aggExpLong > int.MaxValue)
                throw new InvalidOperationException(DiagAggregateOverflow);

            bool mutates = successCount > 0;
            // 防御：成功路径不得把源引用当成最终候选泄露给调用方可变共享。
            PlayerAggregate? finalCandidate = mutates ? local : null;
            if (mutates && ReferenceEquals(finalCandidate, source))
                throw new InvalidOperationException("batch success must not reuse source instance");

            return BatchDomainResult.Create(
                mutates,
                finalCandidate,
                itemResults,
                aggGains,
                (int)aggExpLong,
                totalRng,
                diagnostics);
        }

        static DomainResult<PlayerAggregate> EvaluatePlantItem(
            PlayerAggregate state,
            PlotCommand cmd,
            CropRules cropRules,
            CommandContext ctx,
            out FarmBatchItemResult item)
        {
            FarmInput farmInput = FarmInput.FromAggregate(state);
            var intent = new PlantIntent(cmd.PlotId, cmd.SeedId);
            FarmPlantResult plant = PlantPolicy.Evaluate(farmInput, intent, cropRules, ctx.NowMs);
            if (!plant.Success)
            {
                item = new FarmBatchItemResult(
                    cmd.PlotId,
                    MapPublishedPlantCode(plant),
                    plotAfter: null,
                    yieldItemId: 0,
                    yieldCount: 0,
                    expGained: 0,
                    rngCalls: 0);
                return DomainResult<PlayerAggregate>.Fail(plant.Code, plant.Diagnostics);
            }

            PlotStateView grown = plant.PlotAfter!.Value;
            AcceptedTaskState acceptedAfter = state.AcceptedTasks.WithHarvest(grown.PlotId, plant.AcceptedSpec!);
            var effects = new DomainEffects(
                itemCosts: new[] { new ItemAmount(plant.SeedId, 1) },
                basePatches: new AggregateModulePatch[] { new PlotReplacePatch(grown) },
                acceptedTasksAfter: acceptedAfter);
            DomainResult<PlayerAggregate> composed = GameplayEffectComposer.Compose(state, effects, ctx);
            if (!composed.Success)
            {
                int published = MapPublishedPlantDomain(composed);
                item = new FarmBatchItemResult(
                    cmd.PlotId, published, null, 0, 0, 0, 0);
                return composed;
            }

            PlotStateView after = FindPlot(composed.Candidate!, cmd.PlotId);
            item = new FarmBatchItemResult(cmd.PlotId, 0, after, 0, 0, 0, 0);
            return composed;
        }

        static DomainResult<PlayerAggregate> EvaluateHarvestItem(
            PlayerAggregate state,
            PlotCommand cmd,
            CropRules cropRules,
            CommandContext ctx,
            IRandomSource random,
            out FarmBatchItemResult item)
        {
            FarmInput farmInput = FarmInput.FromAggregate(state);
            var intent = new PlotIntent(cmd.PlotId);
            FarmHarvestResult harvest = HarvestPolicy.Evaluate(
                farmInput, intent, cropRules, ctx.NowMs, random);

            // 资格/抽签失败：保留实际 RngCalls，不改候选。
            if (!harvest.Success || !harvest.HasSettlement)
            {
                int published = MapPublishedHarvestCode(harvest);
                item = new FarmBatchItemResult(
                    cmd.PlotId, published, null, 0, 0, 0, harvest.RngCalls);
                var diagnostics = new List<string>();
                for (int i = 0; i < harvest.Diagnostics.Count; i++)
                    diagnostics.Add(harvest.Diagnostics[i]);
                if (harvest.Success && !harvest.HasSettlement)
                    diagnostics.Add(HarvestPolicy.DiagHarvestSettlementRequired);
                int code = harvest.Code != 0 ? harvest.Code : ErrorCodes.BadPacket;
                return DomainResult<PlayerAggregate>.Fail(code, diagnostics);
            }

            PlotStateView cleared = harvest.PlotAfter!.Value;
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
            DomainResult<PlayerAggregate> composed = GameplayEffectComposer.Compose(state, effects, ctx);
            if (!composed.Success)
            {
                // 抽后容量等失败：如实保留 RNG；作物/paid 未改（Composer 失败）。
                int published = MapPublishedHarvestDomain(composed);
                item = new FarmBatchItemResult(
                    cmd.PlotId, published, null, 0, 0, 0, harvest.RngCalls);
                return composed;
            }

            PlotStateView after = FindPlot(composed.Candidate!, cmd.PlotId);
            item = new FarmBatchItemResult(
                cmd.PlotId,
                0,
                after,
                harvest.YieldItemId,
                harvest.YieldCount,
                harvest.ExpGained,
                harvest.RngCalls);
            return composed;
        }

        static BatchDomainResult AllItemsFail(
            NormalizedFarmBatch batch,
            int itemCode,
            int totalRng,
            IReadOnlyList<string> diagnostics)
        {
            var items = new FarmBatchItemResult[batch.Items.Count];
            for (int i = 0; i < batch.Items.Count; i++)
            {
                items[i] = new FarmBatchItemResult(
                    batch.Items[i].PlotId, itemCode, null, 0, 0, 0, 0);
            }

            return BatchDomainResult.Create(
                mutatesAssets: false,
                candidate: null,
                items,
                Array.Empty<ItemAmount>(),
                aggregateExp: 0,
                totalRngCalls: totalRng,
                diagnostics);
        }

        static PlotStateView FindPlot(PlayerAggregate agg, int plotId)
        {
            for (int i = 0; i < agg.Plots.Count; i++)
            {
                if (agg.Plots[i].PlotId == plotId)
                    return agg.Plots[i];
            }

            throw new InvalidOperationException("composed candidate missing target plot");
        }

        /// <summary>与 PlantGameplayRule.MapPublishedPlantFailure 对齐的公开码映射（批量单项）。</summary>
        static int MapPublishedPlantCode(FarmPlantResult plant)
        {
            if (plant.Code != ErrorCodes.BadPacket)
                return plant.Code;
            IReadOnlyList<string> diagnostics = plant.Diagnostics;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                string d = diagnostics[i];
                if (string.Equals(d, PlantPolicy.DiagPlantBadIntent, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantNegativeClock, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantDeadlineOverflow, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantPlotUnknownInstance, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantPlotMissing, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantPlotUnusable, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantBagMalformed, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantStaleAcceptedConflict, StringComparison.Ordinal)
                    || string.Equals(d, PlantPolicy.DiagPlantMalformed, StringComparison.Ordinal))
                {
                    return 101;
                }
            }

            return plant.Code;
        }

        static int MapPublishedPlantDomain(DomainResult<PlayerAggregate> result)
        {
            if (result.Code != ErrorCodes.BadPacket)
                return result.Code;
            return 101;
        }

        static int MapPublishedHarvestCode(FarmHarvestResult harvest)
        {
            if (harvest.Code != ErrorCodes.BadPacket)
                return harvest.Code;
            IReadOnlyList<string> diagnostics = harvest.Diagnostics;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                string d = diagnostics[i];
                if (string.Equals(d, HarvestPolicy.DiagHarvestBadIntent, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestNegativeClock, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestPlotUnknownInstance, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestPlotMissing, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestPlotEmpty, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestPlotUnusable, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestMalformed, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestSpecMismatch, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestUnsupportedFormula, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestRandomRequired, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestRangeOverflow, StringComparison.Ordinal)
                    || string.Equals(d, HarvestPolicy.DiagHarvestSettlementRequired, StringComparison.Ordinal)
                    || string.Equals(d, FarmLegacyHarvestPolicy.DiagMissingAcceptedSpec, StringComparison.Ordinal))
                {
                    return 101;
                }
            }

            return harvest.Code;
        }

        static int MapPublishedHarvestDomain(DomainResult<PlayerAggregate> result)
        {
            if (result.Code != ErrorCodes.BadPacket)
                return result.Code;
            return 101;
        }
    }
}
