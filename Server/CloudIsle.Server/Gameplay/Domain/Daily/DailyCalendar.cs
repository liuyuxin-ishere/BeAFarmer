using System;

namespace CloudIsle.Domain.Daily
{
    /// <summary>
    /// 纯 UTC+8 日切日历。resetHour 由 Global.daily_reset_hour 提供且须在 0..23（checked）。
    /// day_index = floor((nowMs + 8h - resetHour) / 86400000)，不用向零截断。
    /// </summary>
    public static class DailyCalendar
    {
        const long HourMs = 3_600_000L;
        const long DayMs = 86_400_000L;
        const long UtcPlus8Ms = 8L * HourMs;

        /// <summary>
        /// 以 UTC+8 墙钟与 resetHour 计算 day_index（自 Unix epoch 起的完整重置周期数）。
        /// 边界：恰在重置时刻属新日；前 1ms 属旧日。
        /// </summary>
        public static int GetDayIndex(long nowMs, int resetHour)
        {
            if (nowMs < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMs));
            CheckResetHour(resetHour);

            long shifted = checked(nowMs + UtcPlus8Ms - (long)resetHour * HourMs);
            long day = FloorDiv(shifted, DayMs);
            if (day < int.MinValue || day > int.MaxValue)
                throw new OverflowException("day_index overflow");
            return checked((int)day);
        }

        /// <summary>下一重置时刻的 Unix 毫秒（UTC）；恰在边界上则返回下一周期。</summary>
        public static long GetNextResetMs(long nowMs, int resetHour)
        {
            if (nowMs < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMs));
            CheckResetHour(resetHour);

            int day = GetDayIndex(nowMs, resetHour);
            long nextDay = checked((long)day + 1L);
            return checked(nextDay * DayMs - UtcPlus8Ms + (long)resetHour * HourMs);
        }

        public static void CheckResetHour(int resetHour)
        {
            if (resetHour < 0 || resetHour > 23)
                throw new ArgumentOutOfRangeException(nameof(resetHour), "daily_reset_hour must be 0..23");
        }

        /// <summary>向负无穷的整数除法（floor）；C# `/` 对负数为向零截断。</summary>
        static long FloorDiv(long numerator, long denominator)
        {
            long q = numerator / denominator;
            long r = numerator % denominator;
            if (r != 0 && numerator < 0)
                q--;
            return q;
        }
    }
}
