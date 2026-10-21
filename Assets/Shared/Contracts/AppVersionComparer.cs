using System;
using System.Globalization;

namespace CloudIsle.Contracts
{
    /// <summary>
    /// 数值语义比较 AppVersion（主.次.修订…）。不能按字符串字典序，否则 0.1.10 会小于 0.1.9。
    /// </summary>
    public static class AppVersionComparer
    {
        /// <summary>
        /// 比较两个已规范化的版本号。
        /// </summary>
        /// <param name="left">左侧版本，例如 0.1.10。</param>
        /// <param name="right">右侧版本，例如 0.1.9。</param>
        /// <returns>left&lt;right 为负，相等为 0，left&gt;right 为正。</returns>
        public static int Compare(string left, string right)
        {
            int[] a = ParseParts(left, nameof(left));
            int[] b = ParseParts(right, nameof(right));
            int n = a.Length > b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
            {
                int av = i < a.Length ? a[i] : 0;
                int bv = i < b.Length ? b[i] : 0;
                if (av != bv)
                    return av.CompareTo(bv);
            }

            return 0;
        }

        /// <summary>left 是否严格大于 right。</summary>
        public static bool IsGreater(string left, string right)
        {
            return Compare(left, right) > 0;
        }

        /// <summary>
        /// 解析并校验版本各段。空串、负号、非数字、前导空段均失败。
        /// </summary>
        /// <param name="version">要解析的版本文本。</param>
        /// <param name="paramName">参数名，用于异常信息。</param>
        /// <returns>非空整数段数组。</returns>
        public static int[] ParseParts(string version, string paramName)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new VersionManifestException("版本号不能为空: " + paramName);

            string text = version.Trim();
            string[] pieces = text.Split('.');
            if (pieces.Length == 0)
                throw new VersionManifestException("版本号格式非法: " + text);

            var parts = new int[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                string piece = pieces[i];
                if (piece.Length == 0)
                    throw new VersionManifestException("版本号含空段: " + text);
                for (int c = 0; c < piece.Length; c++)
                {
                    if (piece[c] < '0' || piece[c] > '9')
                        throw new VersionManifestException("版本号含非数字: " + text);
                }

                if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    throw new VersionManifestException("版本号段过大或非法: " + text);
                parts[i] = value;
            }

            return parts;
        }
    }
}
