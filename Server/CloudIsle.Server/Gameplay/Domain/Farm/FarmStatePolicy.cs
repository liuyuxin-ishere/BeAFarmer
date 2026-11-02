using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Net;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// D05 纯地块投影与解锁：读取不落库；解锁只改本阶段 Locked→Empty；
    /// Growing/元数据/未知旧实例原样保留；非规范 legacy 明确失败且不收费。
    /// 成熟仅为 nowMs 相对 ReadyTime 的投影（ready-1 Growing / exact Ripe）；
    /// 不在读时写库、不依赖客户端时钟。存态仅 Locked/Empty/Growing（及兼容存量 Ripe/Unknown）。
    /// </summary>
    public static class FarmStatePolicy
    {
        public const string DiagUnlockAtCap = "unlock-at-cap";
        public const string DiagUnlockNonCanonical = "unlock-noncanonical-legacy";
        public const string DiagUnlockLevelLocked = "unlock-level-locked";
        public const string DiagUnlockMissingPlot = "unlock-missing-canonical-plot";
        public const string DiagUnlockTargetNotLocked = "unlock-target-not-locked";
        public const string DiagUnlockGoldOverflow = "unlock-gold-overflow";
        public const string DiagUnlockMalformed = "unlock-malformed-state";
        /// <summary>ADR O06：稳定表外旧实例保留；诊断串须含其实例 ID。</summary>
        public const string DiagUnlockRetainedUnknownLegacy = "unlock-retained-unknown-legacy";

        /// <summary>
        /// 只读呈现：合法 Growing 须正 ID/正 crop、plant≥0，且 ready&gt;plant，
        /// 或 ready==plant 且已 watered/fertilized（100% 照料后的合法立即成熟，见 ADR T032）；
        /// ready-1 仍 Growing；nowMs ≥ ready → Ripe（含 ready==plant==now）。
        /// 无 care 的 ready==plant 或 ready&lt;plant 为畸形 → Unknown。
        /// Locked/Empty 须正 plotId 且无活作物/时间/水肥元数据，否则 Unknown（源字段原样保留，不清洗）。
        /// 不修改输入、不增 revision、不发奖；不把 deadline 夹到未来。
        /// </summary>
        public static PlotPresentation Project(PlotStateView plot, long nowMs)
        {
            if (nowMs < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMs));

            switch (plot.State)
            {
                case DomainPlotState.Locked:
                    if (plot.PlotId <= 0 || HasIdlePlotMetadata(plot))
                        return PlotPresentation.Unknown;
                    return PlotPresentation.Locked;
                case DomainPlotState.Empty:
                    if (plot.PlotId <= 0 || HasIdlePlotMetadata(plot))
                        return PlotPresentation.Unknown;
                    return PlotPresentation.Empty;
                case DomainPlotState.Growing:
                    if (!IsValidGrowingInvariant(plot))
                        return PlotPresentation.Unknown;
                    // ready-1 → Growing；nowMs >= ReadyTime → Ripe（含合法立即成熟）
                    return nowMs >= plot.ReadyTime ? PlotPresentation.Ripe : PlotPresentation.Growing;
                case DomainPlotState.Ripe:
                    return PlotPresentation.Ripe;
                case DomainPlotState.Unknown:
                default:
                    return PlotPresentation.Unknown;
            }
        }

        /// <summary>
        /// 评估下一阶段解锁。费用为阶段一次价（不按地块乘）；
        /// 稳定 ID 取自 rules.StablePlotIds 前缀扩容；失败源不变。
        /// </summary>
        public static FarmUnlockResult Unlock(
            FarmState state,
            UnlockPlotIntent intent,
            PlotRules rules)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));
            _ = intent;

            var diagnostics = new List<string>();
            if (state.Level < 0 || state.Gold < 0)
            {
                diagnostics.Add(DiagUnlockMalformed);
                return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 兼容规则：已解锁规范地块必须等于 StablePlotIds 的前缀，且前缀长度=某阶段 totalPlots。
            diagnostics.Add(PlotRules.CompatCanonicalPrefixStageTotal);

            var byId = new Dictionary<int, PlotStateView>(state.Plots.Count);
            var stableIdSet = new HashSet<int>(rules.StablePlotIds.Count);
            for (int i = 0; i < rules.StablePlotIds.Count; i++)
                stableIdSet.Add(rules.StablePlotIds[i]);

            for (int i = 0; i < state.Plots.Count; i++)
            {
                PlotStateView p = state.Plots[i];
                if (p.PlotId <= 0)
                {
                    diagnostics.Add(DiagUnlockMalformed);
                    return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }
                if (byId.ContainsKey(p.PlotId))
                {
                    diagnostics.Add(DiagUnlockMalformed);
                    return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }
                byId.Add(p.PlotId, p);
                if (!stableIdSet.Contains(p.PlotId))
                {
                    // O06：未知旧实例 ID 保留不删/不重键；诊断须含该 ID，不阻塞合法下一阶段。
                    diagnostics.Add(DiagUnlockRetainedUnknownLegacy + ":" + p.PlotId);
                }
            }

            int prefixUnlocked = 0;
            bool stillPrefix = true;
            for (int i = 0; i < rules.StablePlotIds.Count; i++)
            {
                int id = rules.StablePlotIds[i];
                if (!byId.TryGetValue(id, out PlotStateView plot))
                {
                    diagnostics.Add(DiagUnlockMissingPlot);
                    return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                // 仅 Empty/Growing/Ripe 计入已拥有；Unknown/非法态不得算解锁。
                bool isOwned = CountsAsOwnedStageMember(plot.State);
                if (stillPrefix)
                {
                    if (isOwned)
                        prefixUnlocked++;
                    else
                        stillPrefix = false;
                }
                else if (isOwned)
                {
                    // 前缀断裂后又出现已解锁 → 非规范，不得猜廉价档。
                    diagnostics.Add(DiagUnlockNonCanonical);
                    return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }
            }

            if (!rules.TryGetStageByTotal(prefixUnlocked, out PlotStageRow currentStage))
            {
                diagnostics.Add(DiagUnlockNonCanonical);
                return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.TryGetNextStage(prefixUnlocked, out PlotStageRow nextStage))
            {
                diagnostics.Add(DiagUnlockAtCap);
                return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Level < nextStage.NeedPlayerLevel)
            {
                diagnostics.Add(DiagUnlockLevelLocked);
                return FarmUnlockResult.Fail(ErrorCodes.LevelLocked, diagnostics);
            }

            long cost = nextStage.UnlockGold;
            if (cost <= 0)
            {
                diagnostics.Add(DiagUnlockGoldOverflow);
                return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Gold < cost)
                return FarmUnlockResult.Fail(ErrorCodes.GoldNotEnough, diagnostics);

            var unlocked = new List<PlotStateView>(nextStage.TotalPlots - prefixUnlocked);
            for (int i = prefixUnlocked; i < nextStage.TotalPlots; i++)
            {
                int id = rules.StablePlotIds[i];
                PlotStateView plot = byId[id];
                if (plot.State != DomainPlotState.Locked)
                {
                    diagnostics.Add(DiagUnlockTargetNotLocked);
                    return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                // 锁地若带作物/时间/水肥，不得静默清洗后收费；诊断失败、源不变。
                if (HasIdlePlotMetadata(plot))
                {
                    diagnostics.Add(DiagUnlockMalformed);
                    return FarmUnlockResult.Fail(ErrorCodes.BadPacket, diagnostics);
                }

                // 仅解锁 Locked→Empty（干净锁地无元数据可清零）。
                unlocked.Add(new PlotStateView(
                    id,
                    DomainPlotState.Empty,
                    cropId: 0,
                    plantTime: 0,
                    readyTime: 0,
                    watered: false,
                    fertilized: false));
            }

            _ = currentStage;
            return FarmUnlockResult.Ok(cost, unlocked, diagnostics);
        }

        /// <summary>
        /// 合法 Growing：正 plotId、正 crop、plant≥0；
        /// ready&gt;plant，或 ready==plant 且 watered|fertilized（照料后立即成熟）。
        /// ready&lt;plant 或无 care 的零期限（ready==plant 且未水未肥）非法。
        /// Project / PlotReplacePatch / Care 产出须共用此不变量（ADR T032）。
        /// </summary>
        public static bool IsValidGrowingInvariant(PlotStateView plot)
        {
            if (plot.PlotId <= 0 || plot.CropId <= 0 || plot.PlantTime < 0)
                return false;
            if (plot.ReadyTime < plot.PlantTime)
                return false;
            if (plot.ReadyTime > plot.PlantTime)
                return true;
            // ready == plant：仅已照料的合法立即成熟；无 care 零期限仍畸形。
            return plot.Watered || plot.Fertilized;
        }

        /// <summary>阶段已拥有成员：Empty/Growing/Ripe；Locked/Unknown/其它不算。</summary>
        public static bool CountsAsOwnedStageMember(DomainPlotState state)
        {
            return state == DomainPlotState.Empty
                   || state == DomainPlotState.Growing
                   || state == DomainPlotState.Ripe;
        }

        /// <summary>
        /// Locked/Empty 不应携带的活元数据。投影为 Unknown；解锁失败。不修改源字段。
        /// </summary>
        static bool HasIdlePlotMetadata(PlotStateView plot)
        {
            return plot.CropId != 0
                   || plot.PlantTime != 0
                   || plot.ReadyTime != 0
                   || plot.Watered
                   || plot.Fertilized;
        }
    }
}
