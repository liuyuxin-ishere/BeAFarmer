using CloudIsle.Domain.Daily;
using CloudIsle.Proto;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 将 Domain 策略与可选 FullData 投影回调绑定。T016 允许空列表；T024/T054 再注册业务。
/// 权威资产来自 <see cref="DailyCandidate.Aggregate"/>；回调仅可映射已声明字段且须对齐候选。
/// </summary>
public sealed class DailyModuleRegistration
{
    public DailyModuleRegistration(
        IDailyResetPolicy policy,
        Action<PlayerFullData, DailyPatch, int>? applyFullData = null)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ApplyFullData = applyFullData;
    }

    public IDailyResetPolicy Policy { get; }
    /// <summary>
    /// 兼容投影回调；null=由协调器按声明字段从领域候选投影。
    /// 非 null 时结果须与候选+所有权一致，否则拒绝提交。
    /// </summary>
    public Action<PlayerFullData, DailyPatch, int>? ApplyFullData { get; }
}
