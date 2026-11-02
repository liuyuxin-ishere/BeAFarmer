using System;
using System.Collections.Generic;
using CloudIsle.Domain.Trade;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 生产日切模块表与测试注入合并。生产始终注册 <see cref="ShopDailyResetPolicy"/>。
/// 测试同 ModuleKey 可显式覆盖；保留模块隔离与显式覆盖契约。
/// </summary>
public static class GameplayDailyComposition
{
    public static IReadOnlyList<DailyModuleRegistration> MergeProductionWithTestOverrides(
        IEnumerable<DailyModuleRegistration>? testModules)
    {
        var map = new Dictionary<string, DailyModuleRegistration>(StringComparer.Ordinal)
        {
            [ShopDailyResetPolicy.ModuleKeyName] = new DailyModuleRegistration(new ShopDailyResetPolicy())
        };

        if (testModules != null)
        {
            foreach (DailyModuleRegistration reg in testModules)
            {
                if (reg == null)
                    throw new ArgumentException("null daily registration", nameof(testModules));
                if (reg.Policy == null || string.IsNullOrWhiteSpace(reg.Policy.ModuleKey))
                    throw new ArgumentException("daily policy ModuleKey required", nameof(testModules));
                map[reg.Policy.ModuleKey] = reg;
            }
        }

        var list = new List<DailyModuleRegistration>(map.Count);
        foreach (KeyValuePair<string, DailyModuleRegistration> pair in map)
            list.Add(pair.Value);
        return list;
    }
}
