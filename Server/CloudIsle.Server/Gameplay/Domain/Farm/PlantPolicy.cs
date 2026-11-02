using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Net;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// D06 纯单地块播种：同份 CropRules 解析种子→作物；校验正 ID/稳定地块/规范空地/
    /// 等级/库存/源状态；checked deadline；Capture 完整 AcceptedHarvestSpec。
    /// 不信任客户端 crop/readyTime/产量/经验；失败无效果、不清洗锁地/占用/未知。
    /// </summary>
    public static class PlantPolicy
    {
        /// <summary>05 ERR_PLOT_LOCKED; server domain constant, existing wire schema unchanged.</summary>
        public const int CodePlotLocked = 401;
        public const string DiagPlantBadIntent = "plant-bad-intent";
        public const string DiagPlantNegativeClock = "plant-negative-clock";
        public const string DiagPlantDeadlineOverflow = "plant-deadline-overflow";
        public const string DiagPlantPlotUnknownInstance = "plant-plot-unknown-instance";
        public const string DiagPlantPlotMissing = "plant-plot-missing";
        public const string DiagPlantPlotLocked = "plant-plot-locked";
        public const string DiagPlantPlotUnusable = "plant-plot-unusable";
        public const string DiagPlantPlotNotEmpty = "plant-plot-not-empty";
        public const string DiagPlantSeedUnknown = "plant-seed-unknown";
        public const string DiagPlantLevelLocked = "plant-level-locked";
        public const string DiagPlantNoSeed = "plant-no-seed";
        public const string DiagPlantBagMalformed = "plant-bag-malformed";
        public const string DiagPlantStaleAcceptedConflict = "plant-stale-accepted-conflict";
        public const string DiagPlantMalformed = "plant-malformed-state";

        /// <summary>
        /// 评估单块播种。成功效果：扣 1 粒正确种子、Growing+plant/ready、完整 AcceptedHarvestSpec。
        /// <paramref name="nowMs"/> 负值拒绝；deadline 用 checked(nowMs + growSec*1000)。
        /// </summary>
        public static FarmPlantResult Evaluate(
            FarmInput state,
            PlantIntent intent,
            CropRules rules,
            long nowMs)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            if (nowMs < 0)
            {
                diagnostics.Add(DiagPlantNegativeClock);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (intent.PlotId <= 0 || intent.SeedId <= 0)
            {
                diagnostics.Add(DiagPlantBadIntent);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Level < 0)
            {
                diagnostics.Add(DiagPlantMalformed);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.IsStablePlot(intent.PlotId))
            {
                diagnostics.Add(DiagPlantPlotUnknownInstance);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryFindPlot(state, intent.PlotId, out PlotStateView plot, diagnostics))
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);

            PlotPresentation presentation = FarmStatePolicy.Project(plot, nowMs);
            switch (presentation)
            {
                case PlotPresentation.Empty:
                    break;
                case PlotPresentation.Locked:
                    diagnostics.Add(DiagPlantPlotLocked);
                    return FarmPlantResult.Fail(CodePlotLocked, diagnostics);
                case PlotPresentation.Growing:
                case PlotPresentation.Ripe:
                    diagnostics.Add(DiagPlantPlotNotEmpty);
                    return FarmPlantResult.Fail(ErrorCodes.PlotNotEmpty, diagnostics);
                default:
                    diagnostics.Add(DiagPlantPlotUnusable);
                    return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // A valid paid Growing/Ripe crop is simply nonempty (402).
            // Only an Empty plot carrying an old paid specification is inconsistent.
            if (state.AcceptedTasks.TryGetHarvest(intent.PlotId, out _))
            {
                diagnostics.Add(DiagPlantStaleAcceptedConflict);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.TryGetBySeed(intent.SeedId, out CropSpec crop))
            {
                diagnostics.Add(DiagPlantSeedUnknown);
                return FarmPlantResult.Fail(ErrorCodes.ItemInvalid, diagnostics);
            }

            if (state.Level < crop.UnlockLevel)
            {
                diagnostics.Add(DiagPlantLevelLocked);
                return FarmPlantResult.Fail(ErrorCodes.LevelLocked, diagnostics);
            }

            if (!state.TryCountItem(intent.SeedId, out long seedCount, out bool malformed))
            {
                diagnostics.Add(malformed ? DiagPlantBagMalformed : DiagPlantNoSeed);
                return FarmPlantResult.Fail(
                    malformed ? ErrorCodes.BadPacket : ErrorCodes.ItemNotEnough, diagnostics);
            }

            if (seedCount < 1)
            {
                diagnostics.Add(DiagPlantNoSeed);
                return FarmPlantResult.Fail(ErrorCodes.ItemNotEnough, diagnostics);
            }

            long readyTime;
            try
            {
                long growMs = checked((long)crop.GrowSec * 1000L);
                readyTime = checked(nowMs + growMs);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagPlantDeadlineOverflow);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (readyTime <= nowMs)
            {
                diagnostics.Add(DiagPlantDeadlineOverflow);
                return FarmPlantResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            AcceptedHarvestSpec spec = AcceptedHarvestSpec.Capture(crop, rules.ConfigVersion);
            var nextPlot = new PlotStateView(
                intent.PlotId,
                DomainPlotState.Growing,
                cropId: crop.CropId,
                plantTime: nowMs,
                readyTime: readyTime,
                watered: false,
                fertilized: false);

            return FarmPlantResult.Ok(intent.SeedId, nextPlot, spec, diagnostics);
        }

        static bool TryFindPlot(
            FarmInput state,
            int plotId,
            out PlotStateView plot,
            List<string> diagnostics)
        {
            plot = default;
            bool found = false;
            for (int i = 0; i < state.Plots.Count; i++)
            {
                PlotStateView row = state.Plots[i];
                if (row.PlotId != plotId)
                    continue;
                if (found)
                {
                    diagnostics.Add(DiagPlantMalformed);
                    return false;
                }

                plot = row;
                found = true;
            }

            if (!found)
            {
                diagnostics.Add(DiagPlantPlotMissing);
                return false;
            }

            return true;
        }
    }
}
