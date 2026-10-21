using System;
using System.Globalization;
using System.Text;
using CloudIsle.Config;

namespace CloudIsle.Contracts
{
    /// <summary>
    /// 解析并校验 version.json 指针。供 HTTP 样例校验与客户端 VersionService 共用。
    /// </summary>
    public static class VersionManifestParser
    {
        /// <summary>version.json 最大字节数。超过即拒绝，防止误把大文件当清单。</summary>
        public const int MaxBytes = 8192;

        /// <summary>当前工程宿主要求的协议版本。不兼容则不能进入热更。</summary>
        public const int ExpectedProtoVersion = 1;

        /// <summary>
        /// 从 UTF-8 字节解析清单。任一字段非法都不返回半份对象。
        /// </summary>
        /// <param name="bytes">HTTP 或磁盘读到的原文。</param>
        /// <param name="expectedProtoVersion">宿主要求的 ProtoVersion；不匹配则失败。</param>
        /// <returns>字段齐全且通过语义校验的清单。</returns>
        public static VersionManifest Parse(byte[] bytes, int expectedProtoVersion)
        {
            if (bytes == null || bytes.Length == 0)
                throw new VersionManifestException("版本清单为空");
            if (bytes.Length > MaxBytes)
                throw new VersionManifestException(
                    "版本清单超过大小限制 " + MaxBytes.ToString(CultureInfo.InvariantCulture));

            SimpleJson.Obj root;
            try
            {
                root = SimpleJson.ParseObject(Encoding.UTF8.GetString(bytes));
            }
            catch (FormatException ex)
            {
                throw new VersionManifestException("版本清单不是合法 JSON: " + ex.Message);
            }

            string minApp = RequireNonEmpty(root, "MinAppVersion");
            AppVersionComparer.ParseParts(minApp, "MinAppVersion");

            int resVersion = RequirePositiveInt(root, "ResVersion");
            int protoVersion = RequirePositiveInt(root, "ProtoVersion");
            int configVersion = RequirePositiveInt(root, "ConfigVersion");
            string packageVersion = RequireNonEmpty(root, "PackageVersion");
            string baseUrl = RequireNonEmpty(root, "BaseUrl");

            if (protoVersion != expectedProtoVersion)
            {
                throw new VersionManifestException(
                    "协议版本不兼容，清单 " +
                    protoVersion.ToString(CultureInfo.InvariantCulture) +
                    "，要求 " +
                    expectedProtoVersion.ToString(CultureInfo.InvariantCulture));
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new VersionManifestException("BaseUrl 不是合法绝对 HTTP(S) 地址");
            }

            return new VersionManifest
            {
                MinAppVersion = minApp,
                ResVersion = resVersion,
                ProtoVersion = protoVersion,
                ConfigVersion = configVersion,
                PackageVersion = packageVersion,
                BaseUrl = baseUrl
            };
        }

        /// <summary>使用默认宿主 ProtoVersion=1 解析。</summary>
        /// <param name="bytes">清单原文。</param>
        /// <returns>已验证清单。</returns>
        public static VersionManifest Parse(byte[] bytes)
        {
            return Parse(bytes, ExpectedProtoVersion);
        }

        static string RequireNonEmpty(SimpleJson.Obj root, string key)
        {
            string value;
            try
            {
                value = root.RequireString(key);
            }
            catch (FormatException ex)
            {
                throw new VersionManifestException("版本清单缺少字段 " + key + ": " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(value))
                throw new VersionManifestException("版本清单字段为空: " + key);
            return value.Trim();
        }

        static int RequirePositiveInt(SimpleJson.Obj root, string key)
        {
            long value;
            try
            {
                value = root.RequireLong(key);
            }
            catch (FormatException ex)
            {
                throw new VersionManifestException("版本清单缺少或非法字段 " + key + ": " + ex.Message);
            }

            if (value < 1 || value > int.MaxValue)
                throw new VersionManifestException("版本清单字段必须为正整数: " + key);
            return (int)value;
        }
    }
}
