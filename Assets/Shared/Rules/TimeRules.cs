using System;

namespace CloudIsle.Rules
{
    /// <summary>
    /// PoC #5：前后端共享的纯 C# 规则示例（不依赖 UnityEngine）。
    /// </summary>
    public static class TimeRules
    {
        /// <summary>
        /// 成熟时刻（Unix 毫秒）= 种植时刻 + 生长秒数。
        /// </summary>
        public static long CalcReadyTimeMs(long plantTimeMs, int growSeconds)
        {
            if (growSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(growSeconds));
            return checked(plantTimeMs + growSeconds * 1000L);
        }

        /// <summary>是否已成熟（serverTimeMs &gt;= readyTimeMs）。</summary>
        public static bool IsReady(long serverTimeMs, long readyTimeMs)
        {
            return serverTimeMs >= readyTimeMs;
        }
    }
}
