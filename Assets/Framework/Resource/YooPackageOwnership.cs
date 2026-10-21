using System;
using System.Collections.Generic;

namespace Game.Framework.Resource
{
    /// <summary>
    /// 同名 Yoo 包的初始化所有权登记。PatchService / ResMgr 共用，拒绝冲突 Mode/URL/缓存根静默复用。
    /// </summary>
    public static class YooPackageOwnership
    {
        static readonly object Gate = new object();
        static readonly Dictionary<string, Record> Records =
            new Dictionary<string, Record>(StringComparer.Ordinal);

        /// <summary>一次成功初始化的指纹。</summary>
        public sealed class Record
        {
            /// <summary>HostHttp / EditorSimulate 等逻辑模式名。</summary>
            public string Mode;

            /// <summary>远端 BaseUrl（Host）或模拟根（Editor）；末尾无斜杠。</summary>
            public string RemoteBase;

            /// <summary>沙箱缓存根；可空表示插件默认沙箱。</summary>
            public string CacheRoot;

            /// <summary>登记方：PatchService / ResMgr。</summary>
            public string Owner;
        }

        /// <summary>
        /// 若包尚未登记则写入；已登记则校验一致，冲突抛错。
        /// </summary>
        /// <param name="packageName">Yoo 包名。</param>
        /// <param name="mode">逻辑模式。</param>
        /// <param name="remoteBase">远端或模拟根。</param>
        /// <param name="cacheRoot">沙箱根；null/空=默认。</param>
        /// <param name="owner">登记方。</param>
        public static void ClaimOrValidate(
            string packageName,
            string mode,
            string remoteBase,
            string cacheRoot,
            string owner)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                throw new ArgumentException("packageName 不能为空", nameof(packageName));
            if (string.IsNullOrWhiteSpace(mode))
                throw new ArgumentException("mode 不能为空", nameof(mode));
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("owner 不能为空", nameof(owner));

            string baseNorm = NormalizeBase(remoteBase);
            string cacheNorm = string.IsNullOrWhiteSpace(cacheRoot) ? "" : cacheRoot.Trim();
            lock (Gate)
            {
                if (!Records.TryGetValue(packageName, out Record existing) || existing == null)
                {
                    Records[packageName] = new Record
                    {
                        Mode = mode,
                        RemoteBase = baseNorm,
                        CacheRoot = cacheNorm,
                        Owner = owner
                    };
                    return;
                }

                if (!string.Equals(existing.Mode, mode, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "包初始化模式冲突 package=" + packageName
                        + " existing=" + existing.Mode + " requested=" + mode
                        + " by=" + existing.Owner);
                }

                if (!string.Equals(existing.RemoteBase, baseNorm, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "包远端根冲突 package=" + packageName
                        + " existing=" + existing.RemoteBase + " requested=" + baseNorm
                        + " by=" + existing.Owner);
                }

                if (!string.Equals(existing.CacheRoot, cacheNorm, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "包缓存根冲突 package=" + packageName
                        + " existing=" + existing.CacheRoot + " requested=" + cacheNorm
                        + " by=" + existing.Owner);
                }
            }
        }

        /// <summary>读取已登记指纹；未登记返回 null。</summary>
        public static Record TryGet(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return null;
            lock (Gate)
            {
                return Records.TryGetValue(packageName, out Record r) ? r : null;
            }
        }

        /// <summary>包销毁后清除登记，允许下一轮重新初始化。</summary>
        public static void Clear(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return;
            lock (Gate)
            {
                Records.Remove(packageName);
            }
        }

        /// <summary>测试用：清空全部登记。</summary>
        public static void ClearAllForTests()
        {
            lock (Gate)
            {
                Records.Clear();
            }
        }

        static string NormalizeBase(string remoteBase)
        {
            if (string.IsNullOrWhiteSpace(remoteBase))
                return "";
            return remoteBase.Trim().TrimEnd('/');
        }
    }
}
