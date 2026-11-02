using System;
using CloudIsle.Domain.Daily;
using CloudIsle.Domain.Init;

namespace CloudIsle.Domain.Trade
{
    /// <summary>
    /// S06/D04 商店日切：只拥有当日购买计数重置；不碰订单/资产货币/背包。
    /// 初始化缺失模块一次；跨日清空计数到当前日；同日/回拨不恢复配额；跳日不补历史奖。
    /// CapabilityHash 不承载计数载荷。
    /// </summary>
    public sealed class ShopDailyResetPolicy : IDailyResetPolicy
    {
        public const string ModuleKeyName = "shop";
        public const string OwnedPurchaseCounts = "shop.purchaseCounts";
        public const int InitVersion = 1;

        public string ModuleKey => ModuleKeyName;
        public int RequiredInitVersion => InitVersion;

        public DailyPatch Evaluate(
            PlayerAggregate state,
            DailyModuleMarker previous,
            int toDay,
            DailyCommandContext ctx)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (previous == null)
                throw new ArgumentNullException(nameof(previous));
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));
            if (toDay < 0)
                throw new ArgumentOutOfRangeException(nameof(toDay));

            int nextInit = previous.InitVersion < RequiredInitVersion
                ? RequiredInitVersion
                : previous.InitVersion;

            // 跨日或首次初始化：清空商店计数。同日 Unavailable 重试不清空（无业务不可用态时等同 Available）。
            bool clearCounts = previous.LastResetDay < toDay
                               || previous.InitVersion < RequiredInitVersion;

            var nextMarker = new DailyModuleMarker(
                ModuleKeyName,
                nextInit,
                toDay,
                ModuleAvailability.Available,
                capabilityHash: "");

            return new DailyPatch(
                ModuleKeyName,
                nextMarker,
                new[] { OwnedPurchaseCounts },
                apply: clearCounts
                    ? (PlayerAggregate s) =>
                    {
                        if (s.ShopPurchases.IsEmpty)
                            return s;
                        return s.WithShopPurchases(ShopPurchaseCounts.Empty);
                    }
                    : null);
        }
    }
}
