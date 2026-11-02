using System;
using System.Globalization;

namespace Game.Framework.Gameplay
{
    /// <summary>int64 ↔ 十进制字符串；禁止经 double。</summary>
    public static class GameplayInt64
    {
        public const long BigBeyondDouble = 9007199254740993L;

        public static bool TryParse(string text, out long value)
        {
            return TryParse(text, out value, out _);
        }

        /// <summary>计划别名：规范十进制解析。</summary>
        public static bool TryParseDecimal(string text, out long value) => TryParse(text, out value);

        public static bool TryParse(string text, out long value, out string error)
        {
            value = 0;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "int64 empty";
                return false;
            }

            string t = text;
            if (t.IndexOf('.') >= 0 || t.IndexOf('e') >= 0 || t.IndexOf('E') >= 0)
            {
                error = "int64 must be decimal integer: " + t;
                return false;
            }

            if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                error = "int64 parse failed: " + t;
                return false;
            }

            if (!string.Equals(t, Format(value), StringComparison.Ordinal))
            {
                error = "int64 must use canonical decimal representation";
                value = 0;
                return false;
            }

            return true;
        }

        public static string Format(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static int CompareDecimal(string a, string b)
        {
            if (!TryParse(a, out long left, out string errA))
                throw new FormatException(errA);
            if (!TryParse(b, out long right, out string errB))
                throw new FormatException(errB);
            return left.CompareTo(right);
        }
    }
}
