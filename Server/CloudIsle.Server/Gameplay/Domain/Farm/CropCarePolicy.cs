using System;
using System.Collections.Generic;
using System.Globalization;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Net;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// D07 纯浇水/施肥：同份 CropCareRules 的整数比例；规范 Growing + 已存 accepted 规格；
    /// Water 仅改 watered/readyTime（无扣费）；Fertilize 改 fertilized/readyTime 并声明扣肥 1；
    /// 不查当前 Crop、不改 accepted/plantTime/crop/对方标志。
    /// </summary>
    public static class CropCarePolicy
    {
        /// <summary>05 ERR_PLOT_LOCKED。</summary>
        public const int CodePlotLocked = 401;
        /// <summary>05 ERR_ALREADY_BOOSTED。</summary>
        public const int CodeAlreadyBoosted = 405;

        public const string DiagCareBadIntent = "care-bad-intent";
        public const string DiagCareNegativeClock = "care-negative-clock";
        public const string DiagCarePlotUnknownInstance = "care-plot-unknown-instance";
        public const string DiagCarePlotMissing = "care-plot-missing";
        public const string DiagCarePlotLocked = "care-plot-locked";
        public const string DiagCarePlotEmpty = "care-plot-empty";
        public const string DiagCarePlotUnusable = "care-plot-unusable";
        public const string DiagCareAlreadyWatered = "care-already-watered";
        public const string DiagCareAlreadyFertilized = "care-already-fertilized";
        public const string DiagCareNoFertilizer = "care-no-fertilizer";
        public const string DiagCareBagMalformed = "care-bag-malformed";
        public const string DiagCareFertilizerItemInvalid = "care-fertilizer-item-invalid";
        /// <summary>
        /// 已成熟（at/after ready）拒绝照料。05 无独立成熟 care 码；域内用 BadPacket，
        /// 正式面映射 101。禁止借用 403 ERR_CROP_NOT_RIPE（含义相反）。见 ADR T031/T032。
        /// </summary>
        public const string DiagCareAlreadyMature = "care-already-mature";
        public const string DiagCareMissingAccepted = "care-missing-accepted-spec";
        public const string DiagCareSpecMismatch = "care-accepted-spec-mismatch";
        public const string DiagCareMalformed = "care-malformed-state";
        public const string DiagCareRatioInvalid = "care-ratio-invalid";
        public const string DiagCareReductionOverflow = "care-reduction-overflow";

        /// <summary>
        /// 评估一次浇水。成功：watered=true、readyTime 按剩余比例整数缩短；失败无效果、不改源。
        /// </summary>
        public static FarmCareResult Water(
            FarmInput state,
            PlotIntent intent,
            CropCareRules rules,
            long nowMs)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            if (nowMs < 0)
            {
                diagnostics.Add(DiagCareNegativeClock);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (intent.PlotId <= 0)
            {
                diagnostics.Add(DiagCareBadIntent);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.IsRatioValid)
            {
                diagnostics.Add(DiagCareRatioInvalid);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.IsStablePlot(intent.PlotId))
            {
                diagnostics.Add(DiagCarePlotUnknownInstance);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryFindPlot(state, intent.PlotId, out PlotStateView plot, diagnostics))
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);

            PlotPresentation presentation = FarmStatePolicy.Project(plot, nowMs);
            switch (presentation)
            {
                case PlotPresentation.Growing:
                    break;
                case PlotPresentation.Locked:
                    diagnostics.Add(DiagCarePlotLocked);
                    return FarmCareResult.Fail(CodePlotLocked, diagnostics);
                case PlotPresentation.Empty:
                    diagnostics.Add(DiagCarePlotEmpty);
                    return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
                case PlotPresentation.Ripe:
                    diagnostics.Add(DiagCareAlreadyMature);
                    return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
                default:
                    diagnostics.Add(DiagCarePlotUnusable);
                    return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // Growing 投影已保证 now < ready；仍显式拒绝 at/after，避免把 deadline 改回未来。
            if (nowMs >= plot.ReadyTime)
            {
                diagnostics.Add(DiagCareAlreadyMature);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (plot.Watered)
            {
                diagnostics.Add(DiagCareAlreadyWatered);
                return FarmCareResult.Fail(CodeAlreadyBoosted, diagnostics);
            }

            if (!state.AcceptedTasks.TryGetHarvest(intent.PlotId, out AcceptedHarvestSpec? spec) || spec == null)
            {
                diagnostics.Add(DiagCareMissingAccepted);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (spec.CropId != plot.CropId)
            {
                diagnostics.Add(DiagCareSpecMismatch);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long remainingMs;
            try
            {
                remainingMs = checked(plot.ReadyTime - nowMs);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagCareMalformed);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (remainingMs <= 0)
            {
                diagnostics.Add(DiagCareAlreadyMature);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long reduction;
            try
            {
                reduction = FloorMulDivNonNeg(
                    remainingMs, rules.WaterRatioNumerator, CropCareRules.RatioDenominator);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagCareReductionOverflow);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long newReady;
            try
            {
                newReady = checked(plot.ReadyTime - reduction);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagCareReductionOverflow);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 守恒：newReady ∈ [nowMs, oldReady]；1ms 剩余且 10% → reduction0，deadline 不变但 watered=true。
            if (newReady > plot.ReadyTime || newReady < nowMs)
            {
                diagnostics.Add(DiagCareReductionOverflow);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            var nextPlot = new PlotStateView(
                plot.PlotId,
                DomainPlotState.Growing,
                cropId: plot.CropId,
                plantTime: plot.PlantTime,
                readyTime: newReady,
                watered: true,
                fertilized: plot.Fertilized);

            return FarmCareResult.Ok(nextPlot, diagnostics);
        }

        /// <summary>
        /// 评估一次施肥。成功：fertilized=true、readyTime 按当前剩余×fertilize 比例整数缩短、
        /// 声明扣权威肥料 1；失败无效果、不改源。成熟优先于重复（与 Water 同序，见 ADR T032）。
        /// </summary>
        public static FarmCareResult Fertilize(
            FarmInput state,
            PlotIntent intent,
            CropCareRules rules,
            long nowMs)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            if (nowMs < 0)
            {
                diagnostics.Add(DiagCareNegativeClock);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (intent.PlotId <= 0)
            {
                diagnostics.Add(DiagCareBadIntent);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.IsFertilizeConfigured || !rules.IsFertilizeRatioValid)
            {
                diagnostics.Add(DiagCareRatioInvalid);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (rules.FertilizerItemId <= 0)
            {
                diagnostics.Add(DiagCareFertilizerItemInvalid);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.IsStablePlot(intent.PlotId))
            {
                diagnostics.Add(DiagCarePlotUnknownInstance);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryFindPlot(state, intent.PlotId, out PlotStateView plot, diagnostics))
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);

            PlotPresentation presentation = FarmStatePolicy.Project(plot, nowMs);
            switch (presentation)
            {
                case PlotPresentation.Growing:
                    break;
                case PlotPresentation.Locked:
                    diagnostics.Add(DiagCarePlotLocked);
                    return FarmCareResult.Fail(CodePlotLocked, diagnostics);
                case PlotPresentation.Empty:
                    diagnostics.Add(DiagCarePlotEmpty);
                    return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
                case PlotPresentation.Ripe:
                    diagnostics.Add(DiagCareAlreadyMature);
                    return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
                default:
                    diagnostics.Add(DiagCarePlotUnusable);
                    return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // Growing 投影已保证 now < ready；仍显式拒绝 at/after，避免把 deadline 改回未来。
            // 成熟优先于已施肥：与 T031 Water 同序（见 ADR T032）。
            if (nowMs >= plot.ReadyTime)
            {
                diagnostics.Add(DiagCareAlreadyMature);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (plot.Fertilized)
            {
                diagnostics.Add(DiagCareAlreadyFertilized);
                return FarmCareResult.Fail(CodeAlreadyBoosted, diagnostics);
            }

            if (!state.AcceptedTasks.TryGetHarvest(intent.PlotId, out AcceptedHarvestSpec? spec) || spec == null)
            {
                diagnostics.Add(DiagCareMissingAccepted);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (spec.CropId != plot.CropId)
            {
                diagnostics.Add(DiagCareSpecMismatch);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!state.TryCountItem(rules.FertilizerItemId, out long fertCount, out bool bagMalformed))
            {
                diagnostics.Add(bagMalformed ? DiagCareBagMalformed : DiagCareNoFertilizer);
                return FarmCareResult.Fail(
                    bagMalformed ? ErrorCodes.BadPacket : ErrorCodes.ItemNotEnough, diagnostics);
            }

            if (fertCount < 1)
            {
                diagnostics.Add(DiagCareNoFertilizer);
                return FarmCareResult.Fail(ErrorCodes.ItemNotEnough, diagnostics);
            }

            long remainingMs;
            try
            {
                remainingMs = checked(plot.ReadyTime - nowMs);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagCareMalformed);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (remainingMs <= 0)
            {
                diagnostics.Add(DiagCareAlreadyMature);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long reduction;
            try
            {
                reduction = FloorMulDivNonNeg(
                    remainingMs, rules.FertilizeRatioNumerator, CropCareRules.RatioDenominator);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagCareReductionOverflow);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            long newReady;
            try
            {
                newReady = checked(plot.ReadyTime - reduction);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagCareReductionOverflow);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 守恒：newReady ∈ [nowMs, oldReady]；1ms 剩余且 30% → reduction0，deadline 不变但 fertilized=true 且仍扣肥。
            if (newReady > plot.ReadyTime || newReady < nowMs)
            {
                diagnostics.Add(DiagCareReductionOverflow);
                return FarmCareResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            var nextPlot = new PlotStateView(
                plot.PlotId,
                DomainPlotState.Growing,
                cropId: plot.CropId,
                plantTime: plot.PlantTime,
                readyTime: newReady,
                watered: plot.Watered,
                fertilized: true);

            return FarmCareResult.Ok(nextPlot, rules.FertilizerItemId, diagnostics);
        }

        /// <summary>
        /// floor(remaining * numerator / denominator)，用商余避免 Int64 乘溢出；不用 float/double。
        /// </summary>
        public static long FloorMulDivNonNeg(long remaining, int numerator, int denominator)
        {
            if (remaining < 0)
                throw new ArgumentOutOfRangeException(nameof(remaining));
            if (denominator <= 0)
                throw new ArgumentOutOfRangeException(nameof(denominator));
            if (numerator < 0 || numerator > denominator)
                throw new ArgumentOutOfRangeException(nameof(numerator));
            if (numerator == 0)
                return 0;
            if (numerator == denominator)
                return remaining;

            long q = remaining / denominator;
            long r = remaining % denominator;
            long high = checked(q * numerator);
            // r < denominator ≤ 10000，numerator ≤ 10000 → r*numerator ≤ 1e8，可安全乘。
            long low = (r * numerator) / denominator;
            return checked(high + low);
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
                    diagnostics.Add(DiagCareMalformed);
                    return false;
                }

                plot = row;
                found = true;
            }

            if (!found)
            {
                diagnostics.Add(DiagCarePlotMissing);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的浇水/施肥比例、权威肥料 ID 与稳定地块身份。
    /// 比例自 Globals 配置字符串精确解析为万分比分子；不查当前 Crop 表。
    /// 既有 Water 双参构造保持兼容；施肥须完整四参或 FromSnapshot。
    /// </summary>
    public sealed class CropCareRules
    {
        public const int RatioDenominator = 10000;
        /// <summary>权威肥料 itemId（03/Config200）；须在快照物品表存在，禁止冒造。</summary>
        public const int AuthoritativeFertilizerItemId = 5001;

        /// <summary>仅浇水规则（兼容既有测试/调用）；不可用于 Fertilize。</summary>
        public CropCareRules(int waterRatioNumerator, IReadOnlyCollection<int> stablePlotIds)
            : this(
                waterRatioNumerator,
                fertilizeRatioNumerator: 0,
                fertilizerItemId: 0,
                stablePlotIds,
                fertilizeConfigured: false)
        {
        }

        /// <summary>浇水+施肥完整规则。</summary>
        public CropCareRules(
            int waterRatioNumerator,
            int fertilizeRatioNumerator,
            int fertilizerItemId,
            IReadOnlyCollection<int> stablePlotIds)
            : this(
                waterRatioNumerator,
                fertilizeRatioNumerator,
                fertilizerItemId,
                stablePlotIds,
                fertilizeConfigured: true)
        {
        }

        CropCareRules(
            int waterRatioNumerator,
            int fertilizeRatioNumerator,
            int fertilizerItemId,
            IReadOnlyCollection<int> stablePlotIds,
            bool fertilizeConfigured)
        {
            if (waterRatioNumerator < 0 || waterRatioNumerator > RatioDenominator)
                throw new ArgumentOutOfRangeException(nameof(waterRatioNumerator));
            if (stablePlotIds == null || stablePlotIds.Count == 0)
                throw new ArgumentException("stablePlotIds required", nameof(stablePlotIds));

            if (fertilizeConfigured)
            {
                if (fertilizeRatioNumerator < 0 || fertilizeRatioNumerator > RatioDenominator)
                    throw new ArgumentOutOfRangeException(nameof(fertilizeRatioNumerator));
                if (fertilizerItemId <= 0)
                    throw new ArgumentOutOfRangeException(nameof(fertilizerItemId));
            }

            var idList = new List<int>();
            var seen = new HashSet<int>();
            foreach (int id in stablePlotIds)
            {
                if (id <= 0)
                    throw new ArgumentException("stablePlotIds 必须为正", nameof(stablePlotIds));
                if (!seen.Add(id))
                    throw new ArgumentException("stablePlotIds 重复", nameof(stablePlotIds));
                idList.Add(id);
            }

            idList.Sort();
            WaterRatioNumerator = waterRatioNumerator;
            FertilizeRatioNumerator = fertilizeConfigured ? fertilizeRatioNumerator : 0;
            FertilizerItemId = fertilizeConfigured ? fertilizerItemId : 0;
            IsFertilizeConfigured = fertilizeConfigured;
            IsFertilizeRatioValid = fertilizeConfigured;
            StablePlotIds = Array.AsReadOnly(idList.ToArray());
            _stablePlotSet = seen;
            IsRatioValid = true;
        }

        readonly HashSet<int> _stablePlotSet;

        /// <summary>万分比分子；正式 Config200 water_ratio=0.10 → 1000。</summary>
        public int WaterRatioNumerator { get; }
        /// <summary>万分比分子；正式 Config200 fertilize_ratio=0.3 → 3000。</summary>
        public int FertilizeRatioNumerator { get; }
        /// <summary>权威肥料 itemId；未配置施肥时为 0。</summary>
        public int FertilizerItemId { get; }
        public IReadOnlyList<int> StablePlotIds { get; }
        public bool IsRatioValid { get; }
        /// <summary>是否装载了施肥比例与肥料身份（双参 Water 构造为 false）。</summary>
        public bool IsFertilizeConfigured { get; }
        public bool IsFertilizeRatioValid { get; }

        public static CropCareRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Globals == null)
                throw new InvalidOperationException("GameConfigSnapshot missing Globals");

            if (!snapshot.Globals.Globals.TryGetValue("water_ratio", out string? rawWater)
                || string.IsNullOrEmpty(rawWater))
            {
                throw new InvalidOperationException("Globals missing water_ratio");
            }

            if (!TryParseRatioNumerator(rawWater, out int waterNumerator, out string waterDiag))
            {
                throw new InvalidOperationException(
                    "invalid water_ratio: " + waterDiag + " raw=" + rawWater);
            }

            if (!snapshot.Globals.Globals.TryGetValue("fertilize_ratio", out string? rawFert)
                || string.IsNullOrEmpty(rawFert))
            {
                throw new InvalidOperationException("Globals missing fertilize_ratio");
            }

            if (!TryParseRatioNumerator(rawFert, out int fertNumerator, out string fertDiag))
            {
                throw new InvalidOperationException(
                    "invalid fertilize_ratio: " + fertDiag + " raw=" + rawFert);
            }

            // 权威 5001 须在当前快照物品表存在；缺则失败，不冒造。
            if (!snapshot.TryGetItemStackMax(AuthoritativeFertilizerItemId, out _))
            {
                throw new InvalidOperationException(
                    "snapshot missing authoritative fertilizer item "
                    + AuthoritativeFertilizerItemId.ToString(CultureInfo.InvariantCulture));
            }

            var plotIds = new List<int>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in snapshot.MapInstances)
            {
                if (string.Equals(pair.Value.Kind, "Plot", StringComparison.Ordinal))
                    plotIds.Add(pair.Key);
            }

            if (plotIds.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing Plot map instances");
            return new CropCareRules(
                waterNumerator, fertNumerator, AuthoritativeFertilizerItemId, plotIds);
        }

        public bool IsStablePlot(int plotId) => plotId > 0 && _stablePlotSet.Contains(plotId);

        /// <summary>
        /// 精确解析比例字符串为万分比分子（denominator=10000）。
        /// 禁止 float/double；超过 4 位小数或不在 [0,1] → 失败（不默默舍入）。
        /// 接受 "0.1"/"0.10"/"0.1000" → 1000；"0"→0；"1"/"1.0"→10000。
        /// </summary>
        public static bool TryParseRatioNumerator(string text, out int numerator, out string diagnostic)
        {
            numerator = 0;
            diagnostic = "";
            if (text == null)
            {
                diagnostic = "null";
                return false;
            }

            string s = text.Trim();
            if (s.Length == 0)
            {
                diagnostic = "empty";
                return false;
            }

            // 仅允许 [0-9]+(.[0-9]+)?；Invariant；无符号/指数。
            int dot = -1;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '.')
                {
                    if (dot >= 0)
                    {
                        diagnostic = "multi-dot";
                        return false;
                    }
                    dot = i;
                    continue;
                }

                if (ch < '0' || ch > '9')
                {
                    diagnostic = "non-digit";
                    return false;
                }
            }

            string intPart;
            string fracPart;
            if (dot < 0)
            {
                intPart = s;
                fracPart = "";
            }
            else
            {
                intPart = s.Substring(0, dot);
                fracPart = s.Substring(dot + 1);
                if (intPart.Length == 0 && fracPart.Length == 0)
                {
                    diagnostic = "bare-dot";
                    return false;
                }
            }

            if (intPart.Length == 0)
                intPart = "0";
            if (fracPart.Length > 4)
            {
                diagnostic = "frac-too-long";
                return false;
            }

            while (fracPart.Length < 4)
                fracPart += "0";

            if (!int.TryParse(intPart, NumberStyles.None, CultureInfo.InvariantCulture, out int whole)
                || whole < 0)
            {
                diagnostic = "bad-int";
                return false;
            }

            if (!int.TryParse(fracPart, NumberStyles.None, CultureInfo.InvariantCulture, out int frac)
                || frac < 0 || frac >= RatioDenominator)
            {
                diagnostic = "bad-frac";
                return false;
            }

            try
            {
                numerator = checked(whole * RatioDenominator + frac);
            }
            catch (OverflowException)
            {
                diagnostic = "overflow";
                return false;
            }

            if (numerator < 0 || numerator > RatioDenominator)
            {
                diagnostic = "out-of-range";
                numerator = 0;
                return false;
            }

            return true;
        }
    }
}
