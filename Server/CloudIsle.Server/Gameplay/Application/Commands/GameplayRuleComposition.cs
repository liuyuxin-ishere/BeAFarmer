using System;
using System.Collections.Generic;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 生产玩法规则表与测试注入合并。生产 BagSell/BagExpand/ShopBuy/PlotUnlock/Plant/Water/Fertilize/Harvest/Gather/ToolUpgrade 始终注册。
/// Plant/Harvest 规则内含 T034 稳定批量（2..24）。测试同 Kind 可显式覆盖（fault/kill GoldBump 夹具）。
/// 禁止将 FakeGoldBump 写入生产默认表。FacilityBuild 等仍未注册（504）。
/// </summary>
public static class GameplayRuleComposition
{
    /// <summary>
    /// 合并生产规则与可选测试注入。同 Kind 时测试规则覆盖生产（有意为之，供故障/强杀夹具）。
    /// <paramref name="testRules"/> 为 null 时为生产十种规则（含 Gather+ToolUpgrade）。
    /// </summary>
    public static IReadOnlyList<IGameplayRule> MergeProductionWithTestOverrides(
        IEnumerable<IGameplayRule>? testRules)
    {
        var map = new Dictionary<string, IGameplayRule>(StringComparer.Ordinal)
        {
            [BagSellGameplayRule.KindName] = new BagSellGameplayRule(),
            [BagExpandGameplayRule.KindName] = new BagExpandGameplayRule(),
            [ShopBuyGameplayRule.KindName] = new ShopBuyGameplayRule(),
            [PlotUnlockGameplayRule.KindName] = new PlotUnlockGameplayRule(),
            [PlantGameplayRule.KindName] = new PlantGameplayRule(),
            [WaterGameplayRule.KindName] = new WaterGameplayRule(),
            [FertilizeGameplayRule.KindName] = new FertilizeGameplayRule(),
            [HarvestGameplayRule.KindName] = new HarvestGameplayRule(),
            [GatherGameplayRule.KindName] = new GatherGameplayRule(),
            [ToolUpgradeGameplayRule.KindName] = new ToolUpgradeGameplayRule()
        };

        if (testRules != null)
        {
            foreach (IGameplayRule rule in testRules)
            {
                if (rule == null)
                    throw new ArgumentException("null test rule", nameof(testRules));
                if (string.IsNullOrWhiteSpace(rule.Kind))
                    throw new ArgumentException("test rule kind required", nameof(testRules));
                // 显式覆盖：同 Kind 不抛；文档约定测试覆盖为 fault/kill 注入路径。
                map[rule.Kind] = rule;
            }
        }

        var list = new List<IGameplayRule>(map.Count);
        foreach (KeyValuePair<string, IGameplayRule> pair in map)
            list.Add(pair.Value);
        return list;
    }
}
