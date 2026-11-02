using System;
using System.Collections.Generic;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Random;
using CloudIsle.Net;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// D08/T033 纯单地块收获：成熟与存档 deadline；奖励仅来自原 AcceptedHarvestSpec；
    /// inclusive 产量抽选经 <see cref="IRandomSource"/>；不查当前 Crop、不改源集合。
    /// </summary>
    public static class HarvestPolicy
    {
        /// <summary>05 ERR_PLOT_LOCKED。</summary>
        public const int CodePlotLocked = 401;

        public const string DiagHarvestBadIntent = "harvest-bad-intent";
        public const string DiagHarvestNegativeClock = "harvest-negative-clock";
        public const string DiagHarvestPlotUnknownInstance = "harvest-plot-unknown-instance";
        public const string DiagHarvestPlotMissing = "harvest-plot-missing";
        public const string DiagHarvestPlotLocked = "harvest-plot-locked";
        public const string DiagHarvestPlotEmpty = "harvest-plot-empty";
        public const string DiagHarvestPlotUnusable = "harvest-plot-unusable";
        public const string DiagHarvestNotRipe = "harvest-crop-not-ripe";
        public const string DiagHarvestMalformed = "harvest-malformed-state";
        public const string DiagHarvestSpecMismatch = "harvest-accepted-spec-mismatch";
        public const string DiagHarvestUnsupportedFormula = "harvest-unsupported-formula";
        public const string DiagHarvestRandomRequired = "harvest-random-required";
        public const string DiagHarvestRangeOverflow = "harvest-yield-range-overflow";
        /// <summary>资格-only 成功不可直接 Compose；须走完整结算 overload。</summary>
        public const string DiagHarvestSettlementRequired = "harvest-settlement-required";

        /// <summary>
        /// 计划 D08 兼容签名：只做资格校验，成功时 <see cref="FarmHarvestResult.HasSettlement"/>=false 且不抽签。
        /// 正式结算须走带 <see cref="IRandomSource"/> 的 overload。
        /// </summary>
        public static FarmHarvestResult Evaluate(
            FarmInput state,
            PlotIntent intent,
            long nowMs)
        {
            return EvaluateCore(state, intent, stablePlotIds: null, nowMs, random: null, requireSettlement: false);
        }

        /// <summary>
        /// 完整结算：资格 + inclusive 抽选 + Empty 清地声明。stable 身份来自同份 CropRules（不取产量）。
        /// </summary>
        public static FarmHarvestResult Evaluate(
            FarmInput state,
            PlotIntent intent,
            CropRules rules,
            long nowMs,
            IRandomSource random)
        {
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            return EvaluateCore(state, intent, rules.StablePlotIds, nowMs, random, requireSettlement: true);
        }

        static FarmHarvestResult EvaluateCore(
            FarmInput state,
            PlotIntent intent,
            IReadOnlyList<int>? stablePlotIds,
            long nowMs,
            IRandomSource? random,
            bool requireSettlement)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            var diagnostics = new List<string>();
            if (nowMs < 0)
            {
                diagnostics.Add(DiagHarvestNegativeClock);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (intent.PlotId <= 0)
            {
                diagnostics.Add(DiagHarvestBadIntent);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (stablePlotIds != null && !ContainsStable(stablePlotIds, intent.PlotId))
            {
                diagnostics.Add(DiagHarvestPlotUnknownInstance);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryFindPlot(state, intent.PlotId, out PlotStateView plot, diagnostics))
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);

            PlotPresentation presentation = FarmStatePolicy.Project(plot, nowMs);
            switch (presentation)
            {
                case PlotPresentation.Ripe:
                    break;
                case PlotPresentation.Growing:
                    diagnostics.Add(DiagHarvestNotRipe);
                    return FarmHarvestResult.Fail(ErrorCodes.CropNotRipe, diagnostics);
                case PlotPresentation.Locked:
                    diagnostics.Add(DiagHarvestPlotLocked);
                    return FarmHarvestResult.Fail(CodePlotLocked, diagnostics);
                case PlotPresentation.Empty:
                    diagnostics.Add(DiagHarvestPlotEmpty);
                    return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
                default:
                    diagnostics.Add(DiagHarvestPlotUnusable);
                    return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 存态 Ripe 标签仍须合法生长元数据；无 care 零期限 / 逆序即使标 Ripe 也拒。
            if (!IsHarvestableCropMetadata(plot))
            {
                diagnostics.Add(DiagHarvestMalformed);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 以存档 deadline 为准；ready-1 → 403；exact ready（含 100% care）可收。
            if (nowMs < plot.ReadyTime)
            {
                diagnostics.Add(DiagHarvestNotRipe);
                return FarmHarvestResult.Fail(ErrorCodes.CropNotRipe, diagnostics);
            }

            if (!state.AcceptedTasks.TryGetHarvest(intent.PlotId, out AcceptedHarvestSpec? spec) || spec == null)
            {
                diagnostics.Add(FarmLegacyHarvestPolicy.DiagMissingAcceptedSpec);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (spec.CropId != plot.CropId)
            {
                diagnostics.Add(DiagHarvestSpecMismatch);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!string.Equals(spec.FormulaId, AcceptedHarvestSpec.FormulaCropYieldRangeV1, StringComparison.Ordinal))
            {
                diagnostics.Add(DiagHarvestUnsupportedFormula);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!requireSettlement)
                return FarmHarvestResult.Eligible(diagnostics);

            if (random == null)
            {
                diagnostics.Add(DiagHarvestRandomRequired);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TrySelectInclusiveYield(spec.YieldMin, spec.YieldMax, random, out int rolled, out int rngCalls, diagnostics))
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics, rngCalls);

            if (!spec.TryResolveSettlement(rolled, out int itemId, out int count, out int exp))
            {
                diagnostics.Add(DiagHarvestUnsupportedFormula);
                return FarmHarvestResult.Fail(ErrorCodes.BadPacket, diagnostics, rngCalls);
            }

            var cleared = new PlotStateView(
                plot.PlotId,
                DomainPlotState.Empty,
                cropId: 0,
                plantTime: 0,
                readyTime: 0,
                watered: false,
                fertilized: false);

            return FarmHarvestResult.Ok(
                cleared,
                spec.Clone(),
                itemId,
                count,
                exp,
                rngCalls,
                diagnostics);
        }

        /// <summary>
        /// inclusive [min,max] 抽选；退化区间不抽签；跨度用 long，避免 max==Int32.MaxValue 时 max+1 溢出。
        /// </summary>
        public static bool TrySelectInclusiveYield(
            int yieldMin,
            int yieldMax,
            IRandomSource random,
            out int selected,
            out int rngCalls,
            List<string>? diagnostics = null)
        {
            selected = 0;
            rngCalls = 0;
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            if (yieldMin <= 0 || yieldMax < yieldMin)
            {
                diagnostics?.Add(DiagHarvestRangeOverflow);
                return false;
            }

            if (yieldMin == yieldMax)
            {
                selected = yieldMin;
                rngCalls = 0;
                return true;
            }

            long span = (long)yieldMax - yieldMin + 1L;
            if (span <= 0 || span > int.MaxValue)
            {
                diagnostics?.Add(DiagHarvestRangeOverflow);
                return false;
            }

            // NextInt(0, span) → [0, span)；再加 min，覆盖 [min,max]。
            int offset = random.NextInt(0, (int)span);
            selected = yieldMin + offset;
            rngCalls = 1;
            if (selected < yieldMin || selected > yieldMax)
            {
                diagnostics?.Add(DiagHarvestRangeOverflow);
                return false;
            }

            return true;
        }

        /// <summary>可收获作物元数据：正 crop，且 ready&gt;plant 或 ready==plant 且已照料。</summary>
        public static bool IsHarvestableCropMetadata(PlotStateView plot)
        {
            if (plot.PlotId <= 0 || plot.CropId <= 0 || plot.PlantTime < 0 || plot.ReadyTime < 0)
                return false;
            return FarmStatePolicy.IsValidGrowingInvariant(
                new PlotStateView(
                    plot.PlotId,
                    DomainPlotState.Growing,
                    plot.CropId,
                    plot.PlantTime,
                    plot.ReadyTime,
                    plot.Watered,
                    plot.Fertilized));
        }

        static bool ContainsStable(IReadOnlyList<int> stablePlotIds, int plotId)
        {
            for (int i = 0; i < stablePlotIds.Count; i++)
            {
                if (stablePlotIds[i] == plotId)
                    return true;
            }

            return false;
        }

        static bool TryFindPlot(
            FarmInput state,
            int plotId,
            out PlotStateView plot,
            List<string> diagnostics)
        {
            // 与 Plant/Care 一致：存档 Plots 重复 plotId 在任何 RNG 前明确拒绝，不得静默取首项。
            plot = default;
            bool found = false;
            for (int i = 0; i < state.Plots.Count; i++)
            {
                PlotStateView row = state.Plots[i];
                if (row.PlotId != plotId)
                    continue;
                if (found)
                {
                    diagnostics.Add(DiagHarvestMalformed);
                    return false;
                }

                plot = row;
                found = true;
            }

            if (!found)
            {
                diagnostics.Add(DiagHarvestPlotMissing);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 纯收获结果：成功含选中产量/经验/Empty 地块与 RNG 次数；资格-only 成功无结算字段。
    /// </summary>
    public sealed class FarmHarvestResult
    {
        FarmHarvestResult(
            bool success,
            bool hasSettlement,
            int code,
            PlotStateView? plotAfter,
            AcceptedHarvestSpec? settledSpec,
            int yieldItemId,
            int yieldCount,
            int expGained,
            int rngCalls,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            HasSettlement = hasSettlement;
            Code = code;
            PlotAfter = plotAfter;
            SettledSpec = settledSpec;
            YieldItemId = yieldItemId;
            YieldCount = yieldCount;
            ExpGained = expGained;
            RngCalls = rngCalls;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        /// <summary>true：已抽选并声明清地/发奖字段；false：仅资格通过或失败。</summary>
        public bool HasSettlement { get; }
        public int Code { get; }
        public PlotStateView? PlotAfter { get; }
        public AcceptedHarvestSpec? SettledSpec { get; }
        public int YieldItemId { get; }
        public int YieldCount { get; }
        public int ExpGained { get; }
        public int RngCalls { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static FarmHarvestResult Eligible(IReadOnlyList<string>? diagnostics = null)
        {
            return new FarmHarvestResult(
                true, hasSettlement: false, 0, plotAfter: null, settledSpec: null,
                yieldItemId: 0, yieldCount: 0, expGained: 0, rngCalls: 0, FreezeDiagnostics(diagnostics));
        }

        public static FarmHarvestResult Ok(
            PlotStateView plotAfter,
            AcceptedHarvestSpec settledSpec,
            int yieldItemId,
            int yieldCount,
            int expGained,
            int rngCalls,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (settledSpec == null)
                throw new ArgumentNullException(nameof(settledSpec));
            if (yieldItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldItemId));
            if (yieldCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldCount));
            if (expGained < 0)
                throw new ArgumentOutOfRangeException(nameof(expGained));
            if (rngCalls < 0)
                throw new ArgumentOutOfRangeException(nameof(rngCalls));
            return new FarmHarvestResult(
                true, hasSettlement: true, 0, plotAfter, settledSpec.Clone(),
                yieldItemId, yieldCount, expGained, rngCalls, FreezeDiagnostics(diagnostics));
        }

        public static FarmHarvestResult Fail(
            int code,
            IReadOnlyList<string>? diagnostics = null,
            int rngCalls = 0)
        {
            if (rngCalls < 0)
                throw new ArgumentOutOfRangeException(nameof(rngCalls));
            return new FarmHarvestResult(
                false, hasSettlement: false, code, plotAfter: null, settledSpec: null,
                yieldItemId: 0, yieldCount: 0, expGained: 0, rngCalls, FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<string> FreezeDiagnostics(IReadOnlyList<string>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<string>());
            var arr = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i] ?? "";
            return Array.AsReadOnly(arr);
        }
    }
}
