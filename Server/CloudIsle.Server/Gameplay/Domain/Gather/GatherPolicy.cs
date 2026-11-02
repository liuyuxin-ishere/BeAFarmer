using System;
using System.Collections.Generic;
using System.Globalization;
using CloudIsle.Config;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Random;
using CloudIsle.Net;

namespace CloudIsle.Domain.Gather
{
    /// <summary>
    /// D09/T038 纯采集：Authorize→解锁/工具/冷却→inclusive 产量与最终稀有概率抽样→冷却效果声明。
    /// 不改源集合；容量/升级赠品由 Composer 一次合成。不宣称 T039 回执强杀。
    /// </summary>
    public static class GatherPolicy
    {
        public const string DiagGatherBadIntent = "gather-bad-intent";
        public const string DiagGatherNegativeClock = "gather-negative-clock";
        public const string DiagGatherLevelLocked = "gather-level-locked";
        public const string DiagGatherToolMissing = "gather-tool-missing";
        public const string DiagGatherToolDuplicate = "gather-tool-duplicate";
        public const string DiagGatherToolLevelInvalid = "gather-tool-level-invalid";
        public const string DiagGatherToolRuleMissing = "gather-tool-rule-missing";
        public const string DiagGatherNodeRuleMissing = "gather-node-rule-missing";
        public const string DiagGatherCooldown = "gather-node-cooldown";
        public const string DiagGatherRandomRequired = "gather-random-required";
        public const string DiagGatherRangeOverflow = "gather-yield-range-overflow";
        public const string DiagGatherCooldownOverflow = "gather-cooldown-overflow";
        public const string DiagGatherRareChanceInvalid = "gather-rare-chance-invalid";
        public const string DiagGatherRareRollOutOfRange = "gather-rare-roll-out-of-range";
        public const string DiagGatherSettlementRequired = "gather-settlement-required";

        /// <summary>
        /// 计划兼容：只做资格校验，成功时 <see cref="GatherResult.HasSettlement"/>=false 且不抽签。
        /// </summary>
        public static GatherResult Evaluate(
            GatherInput state,
            GatherIntent intent,
            GatherRules rules,
            long nowMs)
        {
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));
            return EvaluateCore(state, intent, rules, nowMs, random: null, requireSettlement: false);
        }

        /// <summary>完整结算：资格通过后抽样普通产量与稀有，并声明新冷却。</summary>
        public static GatherResult Evaluate(
            GatherInput state,
            GatherIntent intent,
            GatherRules rules,
            long nowMs,
            IRandomSource random)
        {
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            return EvaluateCore(state, intent, rules, nowMs, random, requireSettlement: true);
        }

        static GatherResult EvaluateCore(
            GatherInput state,
            GatherIntent intent,
            GatherRules rules,
            long nowMs,
            IRandomSource? random,
            bool requireSettlement)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            var diagnostics = new List<string>();
            if (nowMs < 0)
            {
                diagnostics.Add(DiagGatherNegativeClock);
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (intent.NodeInstanceId <= 0)
            {
                diagnostics.Add(DiagGatherBadIntent);
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 稳定身份：未知/跨 Kind/缺存档/重复/负冷却/类型冲突 → BadPacket，不创不修。
            NodeAuthorizeResult auth = GameplayInstanceRegistry.AuthorizeNodeUse(
                intent.NodeInstanceId, state.Nodes, rules.Snapshot);
            if (!auth.Success)
            {
                for (int i = 0; i < auth.Diagnostics.Count; i++)
                    diagnostics.Add(auth.Diagnostics[i]);
                return GatherResult.Fail(auth.Code != 0 ? auth.Code : ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.TryGetNode(auth.Spec.ConfigNodeId, out GatherNodeSpec node))
            {
                diagnostics.Add(
                    DiagGatherNodeRuleMissing
                    + ":"
                    + auth.Spec.ConfigNodeId.ToString(CultureInfo.InvariantCulture));
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 采集解锁看节点 unlockLevel；不用 Tool.needPlayerLevel。
            if (state.Level < node.UnlockLevel)
            {
                diagnostics.Add(
                    DiagGatherLevelLocked
                    + ":need="
                    + node.UnlockLevel.ToString(CultureInfo.InvariantCulture)
                    + ";have="
                    + state.Level.ToString(CultureInfo.InvariantCulture));
                return GatherResult.Fail(ErrorCodes.LevelLocked, diagnostics);
            }

            if (!TryResolveOwnedToolLevel(
                    state.Tools, node.ToolId, diagnostics, out int toolLevel, out int toolFailCode))
                return GatherResult.Fail(toolFailCode, diagnostics);

            if (!rules.TryGetToolLevel(node.ToolId, toolLevel, out ToolLevelSpec tool))
            {
                diagnostics.Add(
                    DiagGatherToolRuleMissing
                    + ":tool="
                    + node.ToolId.ToString(CultureInfo.InvariantCulture)
                    + ";level="
                    + toolLevel.ToString(CultureInfo.InvariantCulture));
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // now==nextReady 合法；now&lt;nextReady → 406。
            if (nowMs < auth.NextReadyTime)
            {
                diagnostics.Add(
                    DiagGatherCooldown
                    + ":now="
                    + nowMs.ToString(CultureInfo.InvariantCulture)
                    + ";nextReady="
                    + auth.NextReadyTime.ToString(CultureInfo.InvariantCulture));
                return GatherResult.Fail(ErrorCodes.NodeCooldown, diagnostics);
            }

            if (!requireSettlement)
                return GatherResult.Eligible(diagnostics);

            if (random == null)
            {
                diagnostics.Add(DiagGatherRandomRequired);
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryComputeEffectiveYieldMax(node.YieldMax, tool.YieldBonusMax, out int effectiveMax, diagnostics))
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics);

            int rngCalls = 0;
            if (!HarvestPolicy.TrySelectInclusiveYield(
                    node.YieldMin, effectiveMax, random, out int ordinaryCount, out int yieldCalls, diagnostics))
            {
                if (diagnostics.Count == 0 || diagnostics[diagnostics.Count - 1] != HarvestPolicy.DiagHarvestRangeOverflow)
                    diagnostics.Add(DiagGatherRangeOverflow);
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics, yieldCalls);
            }

            rngCalls += yieldCalls;

            if (!TryRollFinalRare(
                    tool.RareChancePpm, random, out bool rareHit, out int rareCalls, diagnostics))
            {
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics, rngCalls + rareCalls);
            }

            rngCalls += rareCalls;

            if (!TryComputeNextReady(nowMs, node.CooldownSec, out long nextReady, diagnostics))
                return GatherResult.Fail(ErrorCodes.BadPacket, diagnostics, rngCalls);

            var nodeAfter = new NodeStateView(
                auth.Spec.InstanceId, auth.Spec.ConfigNodeId, nextReady);

            int rareItemId = rareHit ? node.RareItemId : 0;
            int rareCount = rareHit ? 1 : 0;
            return GatherResult.Ok(
                nodeAfter,
                node.ItemId,
                ordinaryCount,
                rareItemId,
                rareCount,
                node.Exp,
                toolLevel,
                effectiveMax,
                rareHit,
                rngCalls,
                diagnostics);
        }

        /// <summary>
        /// 查找所需工具唯一等级：缺工具/等级&lt;1→407；同 toolId 多行→BadPacket（诊断 duplicate）。
        /// 不读取 needPlayerLevel，不降级已有高等级。
        /// </summary>
        static bool TryResolveOwnedToolLevel(
            IReadOnlyList<ToolStateView> tools,
            int requiredToolId,
            List<string> diagnostics,
            out int toolLevel,
            out int failCode)
        {
            toolLevel = 0;
            failCode = ErrorCodes.ToolMissing;
            bool found = false;
            for (int i = 0; i < tools.Count; i++)
            {
                ToolStateView row = tools[i];
                if (row.ToolId != requiredToolId)
                    continue;
                if (found)
                {
                    diagnostics.Add(
                        DiagGatherToolDuplicate
                        + ":"
                        + requiredToolId.ToString(CultureInfo.InvariantCulture));
                    toolLevel = 0;
                    failCode = ErrorCodes.BadPacket;
                    return false;
                }

                if (row.Level < 1)
                {
                    diagnostics.Add(
                        DiagGatherToolLevelInvalid
                        + ":"
                        + requiredToolId.ToString(CultureInfo.InvariantCulture)
                        + ";level="
                        + row.Level.ToString(CultureInfo.InvariantCulture));
                    failCode = ErrorCodes.ToolMissing;
                    return false;
                }

                toolLevel = row.Level;
                found = true;
            }

            if (!found)
            {
                diagnostics.Add(
                    DiagGatherToolMissing
                    + ":"
                    + requiredToolId.ToString(CultureInfo.InvariantCulture));
                failCode = ErrorCodes.ToolMissing;
                return false;
            }

            failCode = 0;
            return true;
        }

        /// <summary>effectiveMax = yieldMax + yieldBonusMax；溢出拒绝。</summary>
        public static bool TryComputeEffectiveYieldMax(
            int yieldMax,
            int yieldBonusMax,
            out int effectiveMax,
            List<string>? diagnostics = null)
        {
            effectiveMax = 0;
            if (yieldMax <= 0 || yieldBonusMax < 0)
            {
                diagnostics?.Add(DiagGatherRangeOverflow);
                return false;
            }

            long sum = (long)yieldMax + yieldBonusMax;
            if (sum > int.MaxValue)
            {
                diagnostics?.Add(DiagGatherRangeOverflow);
                return false;
            }

            effectiveMax = (int)sum;
            return true;
        }

        /// <summary>
        /// 最终稀有概率：ppm=0 永不命中（不抽签）；ppm=Scale 必中（不抽签）；
        /// 其余 NextInt(0,Scale)∈[0,Scale)，roll&lt;ppm 命中。越界返回拒绝。
        /// </summary>
        public static bool TryRollFinalRare(
            int rareChancePpm,
            IRandomSource random,
            out bool hit,
            out int rngCalls,
            List<string>? diagnostics = null)
        {
            hit = false;
            rngCalls = 0;
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            if (rareChancePpm < 0 || rareChancePpm > ToolLevelSpec.ChanceScalePpm)
            {
                diagnostics?.Add(DiagGatherRareChanceInvalid);
                return false;
            }

            if (rareChancePpm <= 0)
            {
                hit = false;
                return true;
            }

            if (rareChancePpm >= ToolLevelSpec.ChanceScalePpm)
            {
                hit = true;
                return true;
            }

            int roll = random.NextInt(0, ToolLevelSpec.ChanceScalePpm);
            rngCalls = 1;
            if (roll < 0 || roll >= ToolLevelSpec.ChanceScalePpm)
            {
                diagnostics?.Add(DiagGatherRareRollOutOfRange);
                return false;
            }

            hit = roll < rareChancePpm;
            return true;
        }

        /// <summary>nextReady = nowMs + cooldownSec*1000；checked 溢出拒绝，不回写时钟。</summary>
        public static bool TryComputeNextReady(
            long nowMs,
            int cooldownSec,
            out long nextReady,
            List<string>? diagnostics = null)
        {
            nextReady = 0;
            if (nowMs < 0 || cooldownSec <= 0)
            {
                diagnostics?.Add(DiagGatherCooldownOverflow);
                return false;
            }

            try
            {
                long delta = checked((long)cooldownSec * 1000L);
                nextReady = checked(nowMs + delta);
                if (nextReady < 0)
                {
                    diagnostics?.Add(DiagGatherCooldownOverflow);
                    return false;
                }

                return true;
            }
            catch (OverflowException)
            {
                diagnostics?.Add(DiagGatherCooldownOverflow);
                return false;
            }
        }
    }
}
