using System.Globalization;
using CloudIsle.Config;
using CloudIsle.Domain.Daily;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>从已加载 GameConfigSnapshot 读取日切小时（checked 0..23）。</summary>
public static class DailyConfig
{
    public static int RequireResetHour(GameConfigSnapshot config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (!config.Globals.Globals.TryGetValue("daily_reset_hour", out string? raw)
            || string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Global.daily_reset_hour missing");
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour))
            throw new InvalidOperationException("Global.daily_reset_hour not an integer: " + raw);
        DailyCalendar.CheckResetHour(hour);
        return hour;
    }
}
