using System;
using System.IO;

namespace Game.Framework.Config
{
    /// <summary>
    /// 配置 TextAsset 的 YooAsset 地址规则。
    /// configAddress 是清单资源地址，不是编辑器磁盘路径。
    /// 当前 DefaultPackage 对 Assets/ConfigLuban 使用 AddressByFileName（可 EditorSimulate 打包；
    /// StreamingAssets/ConfigLuban 保留作历史/直读副本），清单地址是 config_manifest，
    /// 表地址是去掉 .json 的文件名。
    /// 若以后地址带目录前缀，表地址仍替换清单地址的最后一段。
    /// </summary>
    public static class ConfigAddresses
    {
        /// <summary>M1 当前收集规则下的清单地址。</summary>
        public const string Manifest = "config_manifest";

        /// <summary>由清单地址和表文件名得到 YooAsset 地址。</summary>
        /// <param name="configAddress">清单 TextAsset 的地址，例如 config_manifest。</param>
        /// <param name="fileName">清单 files 里的文件名，例如 tbglobalkv.json。</param>
        /// <returns>同一前缀下、去掉扩展名的表地址。</returns>
        public static string Table(string configAddress, string fileName)
        {
            if (string.IsNullOrWhiteSpace(configAddress))
                throw new ArgumentException("清单地址不能为空白", nameof(configAddress));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("表文件名不能为空白", nameof(fileName));

            string stem = Path.GetFileNameWithoutExtension(fileName);
            int slash = configAddress.LastIndexOf('/');
            if (slash < 0)
                return stem;
            return configAddress.Substring(0, slash + 1) + stem;
        }
    }
}
