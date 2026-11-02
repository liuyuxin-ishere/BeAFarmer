using System;
using System.Collections.Generic;
using System.Globalization;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Reward;
using CloudIsle.Net;

namespace CloudIsle.Domain.Tool
{
    /// <summary>
    /// D10 工具升级资格与成本声明：一次自不可变 ToolRules 捕获当前/下一等级规格；
    /// 失败不扣费、不改工具、不推进节点冷却。不改已提交 Gather 冻结结果。
    /// </summary>
    public static class ToolUpgradePolicy
    {
        public const string DiagToolBadIntent = "tool-upgrade-bad-intent";
        public const string DiagToolMissing = "tool-upgrade-missing";
        public const string DiagToolDuplicate = "tool-upgrade-duplicate";
        public const string DiagToolLevelInvalid = "tool-upgrade-level-invalid";
        public const string DiagToolCurrentMissing = "tool-upgrade-current-spec-missing";
        public const string DiagToolAlreadyHigher = "tool-upgrade-already-higher";
        public const string DiagToolAtCap = "tool-upgrade-at-cap";
        public const string DiagToolLevelLocked = "tool-upgrade-level-locked";
        public const string DiagToolGoldShort = "tool-upgrade-gold-short";
        public const string DiagToolMaterialShort = "tool-upgrade-material-short";
        public const string DiagToolOverflow = "tool-upgrade-overflow";
        public const string DiagToolRulesMissing = "tool-upgrade-rules-missing";
        public const string DiagToolIncompleteCost = "tool-upgrade-incomplete-cost";

        /// <summary>
        /// 评估升级。成功：GoldCost + ItemCosts + ToolAfter（供 Composer）；失败：不改 state。
        /// </summary>
        public static ToolUpgradeResult Evaluate(
            ToolInput state,
            ToolUpgradeIntent intent,
            ToolRules rules)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>(4);
            if (intent.ToolId <= 0)
            {
                diagnostics.Add(DiagToolBadIntent);
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (rules.ToolLevels.Count == 0)
            {
                diagnostics.Add(DiagToolRulesMissing);
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!TryResolveOwnedToolLevel(
                    state.Tools, intent.ToolId, diagnostics,
                    out int currentLevel, out int resolveCode))
            {
                return ToolUpgradeResult.Fail(resolveCode, diagnostics);
            }

            if (!rules.TryGetToolLevel(intent.ToolId, currentLevel, out ToolLevelSpec currentSpec))
            {
                // 存档等级高于配置最大档，或当前档缺失 → 已有高等级/非法存档。
                diagnostics.Add(
                    DiagToolAlreadyHigher
                    + ":"
                    + intent.ToolId.ToString(CultureInfo.InvariantCulture)
                    + ";level="
                    + currentLevel.ToString(CultureInfo.InvariantCulture));
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            int nextLevel;
            try
            {
                nextLevel = checked(currentLevel + 1);
            }
            catch (OverflowException)
            {
                diagnostics.Add(DiagToolOverflow + ":next-level");
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (!rules.TryGetToolLevel(intent.ToolId, nextLevel, out ToolLevelSpec nextSpec))
            {
                diagnostics.Add(
                    DiagToolAtCap
                    + ":"
                    + intent.ToolId.ToString(CultureInfo.InvariantCulture)
                    + ";level="
                    + currentLevel.ToString(CultureInfo.InvariantCulture));
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            // 一次不可变捕获：门槛/金币/材料全部取自 nextSpec（装载期已冻结）。
            _ = currentSpec;
            if (!nextSpec.HasCompleteUpgradeCost)
            {
                // 旧六参数等不完整投影：Materials 可能为空，不得默认免费升级漏扣。
                diagnostics.Add(
                    DiagToolIncompleteCost
                    + ":"
                    + intent.ToolId.ToString(CultureInfo.InvariantCulture)
                    + ";level="
                    + nextLevel.ToString(CultureInfo.InvariantCulture));
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            int needPlayerLevel = nextSpec.NeedPlayerLevel;
            long goldCost = nextSpec.GoldCost;
            if (goldCost < 0)
            {
                diagnostics.Add(DiagToolOverflow + ":gold-cost-negative");
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);
            }

            if (state.Level < needPlayerLevel)
            {
                diagnostics.Add(
                    DiagToolLevelLocked
                    + ":need="
                    + needPlayerLevel.ToString(CultureInfo.InvariantCulture)
                    + ";have="
                    + state.Level.ToString(CultureInfo.InvariantCulture));
                return ToolUpgradeResult.Fail(ErrorCodes.LevelLocked, diagnostics);
            }

            if (!TryCaptureItemCosts(nextSpec.Materials, diagnostics, out IReadOnlyList<ItemAmount> itemCosts))
                return ToolUpgradeResult.Fail(ErrorCodes.BadPacket, diagnostics);

            if (goldCost > 0 && state.Gold < goldCost)
            {
                diagnostics.Add(DiagToolGoldShort);
                return ToolUpgradeResult.Fail(ErrorCodes.GoldNotEnough, diagnostics);
            }

            if (!TryPrecheckMaterials(state.Bag, itemCosts, diagnostics))
                return ToolUpgradeResult.Fail(ErrorCodes.ItemNotEnough, diagnostics);

            var toolAfter = new ToolStateView(intent.ToolId, nextLevel);
            return ToolUpgradeResult.Ok(
                goldCost, itemCosts, toolAfter, currentLevel, nextLevel, diagnostics);
        }

        /// <summary>
        /// 查找唯一工具行：缺/level&lt;1→407；同 toolId 多行→BadPacket。
        /// </summary>
        static bool TryResolveOwnedToolLevel(
            IReadOnlyList<ToolStateView> tools,
            int toolId,
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
                if (row.ToolId != toolId)
                    continue;
                if (found)
                {
                    diagnostics.Add(
                        DiagToolDuplicate
                        + ":"
                        + toolId.ToString(CultureInfo.InvariantCulture));
                    toolLevel = 0;
                    failCode = ErrorCodes.BadPacket;
                    return false;
                }

                if (row.Level < 1)
                {
                    diagnostics.Add(
                        DiagToolLevelInvalid
                        + ":"
                        + toolId.ToString(CultureInfo.InvariantCulture)
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
                    DiagToolMissing
                    + ":"
                    + toolId.ToString(CultureInfo.InvariantCulture));
                failCode = ErrorCodes.ToolMissing;
                return false;
            }

            return true;
        }

        static bool TryCaptureItemCosts(
            IReadOnlyList<ItemStackSpec> materials,
            List<string> diagnostics,
            out IReadOnlyList<ItemAmount> costs)
        {
            costs = Array.AsReadOnly(Array.Empty<ItemAmount>());
            if (materials == null || materials.Count == 0)
                return true;

            var list = new List<ItemAmount>(materials.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < materials.Count; i++)
            {
                ItemStackSpec stack = materials[i];
                if (stack.ItemId <= 0 || stack.Count <= 0)
                {
                    diagnostics.Add(DiagToolOverflow + ":material-entry");
                    return false;
                }

                if (!seen.Add(stack.ItemId))
                {
                    diagnostics.Add(
                        DiagToolOverflow
                        + ":material-dup:"
                        + stack.ItemId.ToString(CultureInfo.InvariantCulture));
                    return false;
                }

                list.Add(new ItemAmount(stack.ItemId, stack.Count));
            }

            costs = Array.AsReadOnly(list.ToArray());
            return true;
        }

        static bool TryPrecheckMaterials(
            IReadOnlyList<BagItemView> bag,
            IReadOnlyList<ItemAmount> costs,
            List<string> diagnostics)
        {
            if (costs.Count == 0)
                return true;

            var have = new Dictionary<int, long>();
            for (int i = 0; i < bag.Count; i++)
            {
                BagItemView b = bag[i];
                if (b.ItemId <= 0 || b.Count <= 0)
                {
                    diagnostics.Add(DiagToolOverflow + ":bag-malformed");
                    return false;
                }

                if (have.ContainsKey(b.ItemId))
                {
                    diagnostics.Add(DiagToolOverflow + ":bag-dup");
                    return false;
                }

                have[b.ItemId] = b.Count;
            }

            for (int i = 0; i < costs.Count; i++)
            {
                ItemAmount need = costs[i];
                if (!have.TryGetValue(need.ItemId, out long own) || own < need.Amount)
                {
                    diagnostics.Add(
                        DiagToolMaterialShort
                        + ":"
                        + need.ItemId.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
            }

            return true;
        }
    }
}
