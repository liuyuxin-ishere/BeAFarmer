using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Game.Framework.Lua
{
    /// <summary>
    /// 正式 Lua 目录契约：唯一模块/地址映射、入口、SHA、App/Proto/bridgeContract。
    /// 由迁移 RuntimeCatalog 提炼；可从 catalog JSON 构建，不可假称历史已有本类型。
    /// </summary>
    public sealed class LuaIndex
    {
        /// <summary>资源版本字符串，例如 100 / 101。</summary>
        public string ResVersion { get; set; }

        /// <summary>require 入口模块名。</summary>
        public string EntryModule { get; set; }

        /// <summary>运行时配置 TextAsset 地址。</summary>
        public string ConfigAddress { get; set; }

        /// <summary>配置内容 SHA256（小写十六进制）。</summary>
        public string ConfigSha256 { get; set; }

        /// <summary>固定 C# 桥接契约名，须等于 BridgeApi.BridgeContractValue。</summary>
        public string BridgeContract { get; set; }

        /// <summary>要求的 Player AppVersion（Application.version）。</summary>
        public string RequiredAppVersion { get; set; }

        /// <summary>协议版本，须等于 BridgeApi.RequiredProtoVersion。</summary>
        public int ProtoVersion { get; set; }

        /// <summary>模块映射表。</summary>
        public LuaIndexModule[] Modules { get; set; }

        /// <summary>
        /// 从迁移 RuntimeCatalog 构建正式索引（字段拷贝，不共享可变数组引用）。
        /// </summary>
        /// <param name="catalog">迁移目录对象。</param>
        /// <returns>新 LuaIndex 实例。</returns>
        public static LuaIndex FromCatalog(RuntimeCatalog catalog)
        {
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));
            var index = new LuaIndex
            {
                ResVersion = catalog.resVersion,
                EntryModule = catalog.entryModule,
                ConfigAddress = catalog.configAddress,
                ConfigSha256 = catalog.configSha256,
                BridgeContract = catalog.bridgeContract,
                RequiredAppVersion = catalog.requiredAppVersion,
                ProtoVersion = catalog.protoVersion
            };
            if (catalog.modules == null)
            {
                index.Modules = Array.Empty<LuaIndexModule>();
                return index;
            }

            index.Modules = new LuaIndexModule[catalog.modules.Length];
            for (int i = 0; i < catalog.modules.Length; i++)
            {
                CatalogModule src = catalog.modules[i];
                index.Modules[i] = src == null
                    ? null
                    : new LuaIndexModule
                    {
                        Name = src.name,
                        Address = src.address,
                        Sha256 = src.sha256
                    };
            }

            return index;
        }

        /// <summary>
        /// 转回 RuntimeCatalog，供仍依赖迁移结构的适配层使用。
        /// </summary>
        public RuntimeCatalog ToCatalog()
        {
            var catalog = new RuntimeCatalog
            {
                resVersion = ResVersion,
                entryModule = EntryModule,
                configAddress = ConfigAddress,
                configSha256 = ConfigSha256,
                bridgeContract = BridgeContract,
                requiredAppVersion = RequiredAppVersion,
                protoVersion = ProtoVersion
            };
            if (Modules == null)
            {
                catalog.modules = Array.Empty<CatalogModule>();
                return catalog;
            }

            catalog.modules = new CatalogModule[Modules.Length];
            for (int i = 0; i < Modules.Length; i++)
            {
                LuaIndexModule src = Modules[i];
                catalog.modules[i] = src == null
                    ? null
                    : new CatalogModule
                    {
                        name = src.Name,
                        address = src.Address,
                        sha256 = src.Sha256
                    };
            }

            return catalog;
        }

        /// <summary>
        /// 校验索引自身与已加载模块字节。返回 null 表示通过；否则为拒绝原因码。
        /// </summary>
        /// <param name="loaded">已预加载的模块名→字节。</param>
        /// <param name="configBytes">配置原文。</param>
        /// <param name="hostBridgeContract">宿主桥接契约。</param>
        /// <param name="hostProtoVersion">宿主协议版本。</param>
        /// <param name="hostAppVersion">Application.version。</param>
        public string Validate(
            IReadOnlyDictionary<string, byte[]> loaded,
            byte[] configBytes,
            string hostBridgeContract,
            int hostProtoVersion,
            string hostAppVersion)
        {
            if (string.IsNullOrEmpty(ResVersion))
                return "catalog-resVersion-empty";
            if (string.IsNullOrEmpty(BridgeContract))
                return "catalog-bridgeContract-empty";
            if (!string.Equals(BridgeContract, hostBridgeContract, StringComparison.Ordinal))
                return "catalog-bridgeContract-mismatch want=" + hostBridgeContract + " got=" + BridgeContract;
            if (string.IsNullOrEmpty(RequiredAppVersion))
                return "catalog-requiredAppVersion-empty";
            if (!string.Equals(RequiredAppVersion, hostAppVersion, StringComparison.Ordinal))
                return "catalog-requiredAppVersion-mismatch want=" + hostAppVersion + " got=" + RequiredAppVersion;
            if (ProtoVersion != hostProtoVersion)
                return "catalog-protoVersion-mismatch want=" + hostProtoVersion + " got=" + ProtoVersion;
            if (configBytes == null || configBytes.Length == 0)
                return "config-empty";
            if (string.IsNullOrEmpty(ConfigAddress))
                return "config-address-empty";
            if (string.IsNullOrEmpty(ConfigSha256) || !Regex.IsMatch(ConfigSha256, "^[a-fA-F0-9]{64}$"))
                return "config-sha-invalid";
            // 内容 SHA 比对在 LuaLoader 预加载后执行；此处只校验证书格式，保持与迁移 ValidateCandidate 一致。

            string entry = EntryModule ?? "";
            if (!Regex.IsMatch(entry, "^[A-Za-z_][A-Za-z0-9_]*$"))
                return "invalid-entry name";

            if (Modules == null || Modules.Length == 0)
                return "catalog-modules-empty";

            var names = new HashSet<string>(StringComparer.Ordinal);
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Modules.Length; i++)
            {
                LuaIndexModule module = Modules[i];
                if (module == null)
                    return "catalog-module-null";
                // 允许 UI/Windows/LoginWindow 这类 require 路径；禁止空段与首尾斜杠。
                if (string.IsNullOrEmpty(module.Name)
                    || !Regex.IsMatch(module.Name, "^[A-Za-z_][A-Za-z0-9_]*(?:/[A-Za-z_][A-Za-z0-9_]*)*$"))
                    return "invalid-module-name " + module.Name;
                if (string.IsNullOrEmpty(module.Address))
                    return "invalid-module-address-empty";
                if (string.IsNullOrEmpty(module.Sha256) || !Regex.IsMatch(module.Sha256, "^[a-fA-F0-9]{64}$"))
                    return "invalid-module-sha " + module.Name;
                if (!names.Add(module.Name))
                    return "duplicate-module-name " + module.Name;
                if (!addresses.Add(module.Address))
                    return "duplicate-module-address " + module.Address;
            }

            if (loaded == null)
                return "loaded-null";
            if (!loaded.ContainsKey(entry))
                return "invalid-entry missing " + entry;

            return null;
        }

        /// <summary>
        /// 校验已加载 bytes 与索引声明 SHA 一致（预加载后调用）。
        /// </summary>
        /// <param name="loaded">模块名→字节。</param>
        /// <param name="configBytes">配置原文。</param>
        /// <returns>null 通过，否则拒绝原因。</returns>
        public string ValidateContentHashes(IReadOnlyDictionary<string, byte[]> loaded, byte[] configBytes)
        {
            if (configBytes == null || configBytes.Length == 0)
                return "config-empty";
            if (!string.Equals(ConfigSha256, HashUtil.Sha256(configBytes), StringComparison.OrdinalIgnoreCase))
                return "config hash mismatch";
            if (Modules == null || loaded == null)
                return "loaded-null";
            for (int i = 0; i < Modules.Length; i++)
            {
                LuaIndexModule module = Modules[i];
                if (module == null)
                    return "catalog-module-null";
                if (!loaded.TryGetValue(module.Name, out byte[] bytes) || bytes == null)
                    return "missing module " + module.Name;
                if (!string.Equals(HashUtil.Sha256(bytes), module.Sha256, StringComparison.OrdinalIgnoreCase))
                    return "hash mismatch " + module.Name;
            }

            return null;
        }
    }

    /// <summary>LuaIndex 中的单个模块映射。</summary>
    public sealed class LuaIndexModule
    {
        /// <summary>require 名。</summary>
        public string Name;

        /// <summary>Yoo / 内置地址。</summary>
        public string Address;

        /// <summary>内容 SHA256。</summary>
        public string Sha256;
    }
}
