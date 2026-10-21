using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Lua;
using UnityEngine;
using YooAsset;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 从已准备的 Yoo 包异步预加载 Lua TextAsset：字节拷贝、SHA 校验、句柄释放。
    /// 供正式 LuaLoader / Launcher 复用；CustomLoader 只读返回的内存副本。
    /// </summary>
    public static class LuaModulePreloader
    {
        /// <summary>预加载结果：索引 + 模块 bytes + 配置 bytes。</summary>
        public sealed class Result
        {
            /// <summary>由 catalog 构建的正式索引。</summary>
            public LuaIndex Index;

            /// <summary>模块名 → UTF-8 源码拷贝。</summary>
            public Dictionary<string, byte[]> Modules;

            /// <summary>cfg_runtime 原文拷贝。</summary>
            public byte[] ConfigBytes;
        }

        static readonly Regex ModuleEntryRegex = new Regex(
            "\\{\\s*\"name\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"address\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"sha256\"\\s*:\\s*\"([^\"]+)\"\\s*\\}",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// 从指定已初始化包加载 catalog 与全部模块/配置。取消时释放已打开句柄，不改活动索引。
        /// </summary>
        /// <param name="package">已 Initialize + LoadManifest 的包（通常 GameLua）。</param>
        /// <param name="ct">取消。</param>
        public static async UniTask<Result> LoadFromPackageAsync(ResourcePackage package, CancellationToken ct)
        {
            if (package == null)
                throw new ArgumentNullException(nameof(package));
            ct.ThrowIfCancellationRequested();

            AssetHandle catalogHandle = package.LoadAssetAsync<TextAsset>("catalog");
            try
            {
                await catalogHandle.ToUniTask(cancellationToken: ct);
                if (catalogHandle.Status != EOperationStatus.Succeeded || catalogHandle.AssetObject == null)
                    throw new InvalidOperationException("catalog load failed: " + catalogHandle.Error);

                byte[] catalogBytes = CopyBytes(catalogHandle.GetAssetObject<TextAsset>().bytes);
                catalogHandle.Release();
                catalogHandle = null;

                string catalogJson = Encoding.UTF8.GetString(catalogBytes);
                RuntimeCatalog catalog = ParseRuntimeCatalog(catalogJson);
                if (catalog == null || catalog.modules == null || catalog.modules.Length == 0)
                    throw new InvalidOperationException("catalog json invalid");
                if (string.IsNullOrEmpty(catalog.configAddress))
                    throw new InvalidOperationException("catalog configAddress empty");

                // configAddress 绝不能当 Lua 模块（JSON 会被 CompileOnly 当源码解析失败）。
                catalog.modules = FilterConfigFromModules(catalog.modules, catalog.configAddress);
                if (catalog.modules.Length == 0)
                    throw new InvalidOperationException("catalog modules empty after config filter");

                LuaIndex index = LuaIndex.FromCatalog(catalog);
                var modules = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                for (int i = 0; i < catalog.modules.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    CatalogModule module = catalog.modules[i];
                    if (module == null || string.IsNullOrEmpty(module.name))
                        throw new InvalidOperationException("module entry invalid");
                    if (string.Equals(module.name, catalog.configAddress, StringComparison.Ordinal)
                        || string.Equals(module.address, catalog.configAddress, StringComparison.Ordinal))
                        continue;

                    AssetHandle handle = package.LoadAssetAsync<TextAsset>(module.address);
                    try
                    {
                        await handle.ToUniTask(cancellationToken: ct);
                        if (handle.Status != EOperationStatus.Succeeded || handle.AssetObject == null)
                            throw new InvalidOperationException("missing module " + module.name);

                        byte[] bytes = CopyBytes(handle.GetAssetObject<TextAsset>().bytes);
                        handle.Release();
                        handle = null;
                        string hash = HashUtil.Sha256(bytes);
                        if (!string.Equals(hash, module.sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "hash mismatch " + module.name + " actual " + hash);
                        }

                        modules[module.name] = bytes;
                    }
                    finally
                    {
                        if (handle != null && handle.IsValid)
                            handle.Release();
                    }
                }

                if (modules.ContainsKey(catalog.configAddress))
                    modules.Remove(catalog.configAddress);

                ct.ThrowIfCancellationRequested();
                AssetHandle configHandle = package.LoadAssetAsync<TextAsset>(catalog.configAddress);
                try
                {
                    await configHandle.ToUniTask(cancellationToken: ct);
                    if (configHandle.Status != EOperationStatus.Succeeded || configHandle.AssetObject == null)
                        throw new InvalidOperationException("config load failed");

                    byte[] configBytes = CopyBytes(configHandle.GetAssetObject<TextAsset>().bytes);
                    configHandle.Release();
                    configHandle = null;
                    if (!string.Equals(HashUtil.Sha256(configBytes), catalog.configSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("config hash mismatch");

                    return new Result
                    {
                        Index = index,
                        Modules = modules,
                        ConfigBytes = configBytes
                    };
                }
                finally
                {
                    if (configHandle != null && configHandle.IsValid)
                        configHandle.Release();
                }
            }
            finally
            {
                if (catalogHandle != null && catalogHandle.IsValid)
                    catalogHandle.Release();
            }
        }

        /// <summary>
        /// 解析 catalog：标量走 JsonUtility；modules 用正则抽取，避免 JsonUtility 数组/脏数据把 config 混进模块表。
        /// </summary>
        public static RuntimeCatalog ParseRuntimeCatalog(string catalogJson)
        {
            if (string.IsNullOrWhiteSpace(catalogJson))
                return null;

            RuntimeCatalog catalog = JsonUtility.FromJson<RuntimeCatalog>(catalogJson);
            if (catalog == null)
                return null;

            CatalogModule[] parsed = ParseModulesFromJson(catalogJson);
            if (parsed != null && parsed.Length > 0)
                catalog.modules = parsed;
            else if (catalog.modules == null)
                catalog.modules = Array.Empty<CatalogModule>();

            catalog.modules = FilterConfigFromModules(catalog.modules, catalog.configAddress);
            return catalog;
        }

        static CatalogModule[] ParseModulesFromJson(string catalogJson)
        {
            var list = new List<CatalogModule>();
            MatchCollection matches = ModuleEntryRegex.Matches(catalogJson);
            for (int i = 0; i < matches.Count; i++)
            {
                Match m = matches[i];
                list.Add(new CatalogModule
                {
                    name = m.Groups[1].Value,
                    address = m.Groups[2].Value,
                    sha256 = m.Groups[3].Value
                });
            }

            return list.ToArray();
        }

        static CatalogModule[] FilterConfigFromModules(CatalogModule[] modules, string configAddress)
        {
            if (modules == null || modules.Length == 0)
                return Array.Empty<CatalogModule>();
            if (string.IsNullOrEmpty(configAddress))
                return modules;

            var filtered = new List<CatalogModule>(modules.Length);
            for (int i = 0; i < modules.Length; i++)
            {
                CatalogModule m = modules[i];
                if (m == null)
                    continue;
                if (string.Equals(m.name, configAddress, StringComparison.Ordinal))
                    continue;
                if (string.Equals(m.address, configAddress, StringComparison.Ordinal))
                    continue;
                filtered.Add(m);
            }

            return filtered.ToArray();
        }

        /// <summary>
        /// 从内置 Lua 构建索引。Editor 优先 Assets/Lua；Player 读 StreamingAssets/Lua 或 Resources/Lua。
        /// </summary>
        public static bool TryLoadBuiltin(
            out LuaIndex index,
            out Dictionary<string, byte[]> modules,
            out byte[] configBytes)
        {
            index = null;
            modules = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            configBytes = null;
            string[] names = { "entry", "panel_base", "proto_case", "async_case" };
            var list = new List<LuaIndexModule>();
            foreach (string name in names)
            {
                byte[] bytes = ReadBuiltinBytes(name);
                if (bytes == null)
                    return false;
                modules[name] = bytes;
                list.Add(new LuaIndexModule
                {
                    Name = name,
                    Address = name,
                    Sha256 = HashUtil.Sha256(bytes)
                });
            }

            configBytes = ReadBuiltinBytes("cfg_runtime");
            if (configBytes == null)
                return false;
            index = new LuaIndex
            {
                ResVersion = "100",
                EntryModule = "entry",
                ConfigAddress = "cfg_runtime",
                ConfigSha256 = HashUtil.Sha256(configBytes),
                BridgeContract = BridgeApi.BridgeContractValue,
                RequiredAppVersion = Application.version,
                ProtoVersion = BridgeApi.RequiredProtoVersion,
                Modules = list.ToArray()
            };
            return true;
        }

        /// <summary>读取内置 TextAsset 字节；与 HotfixLoader 同源回退链。</summary>
        public static byte[] ReadBuiltinBytes(string address)
        {
#if UNITY_EDITOR
            string editorPath = System.IO.Path.Combine(Application.dataPath, "Lua", address + ".txt");
            if (System.IO.File.Exists(editorPath))
                return System.IO.File.ReadAllBytes(editorPath);
#endif
            string streaming = System.IO.Path.Combine(Application.streamingAssetsPath, "Lua", address + ".txt");
            if (System.IO.File.Exists(streaming))
                return System.IO.File.ReadAllBytes(streaming);

            TextAsset resource = Resources.Load<TextAsset>("Lua/" + address);
            if (resource != null)
                return CopyBytes(resource.bytes);
            return null;
        }

        static byte[] CopyBytes(byte[] source)
        {
            if (source == null)
                return null;
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}
