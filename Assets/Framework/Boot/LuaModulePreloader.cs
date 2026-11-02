using System;
using System.Collections.Generic;
using System.IO;
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
        /// 正式 catalog + 全部精确模块。
        /// Editor：可读工程 Staging（UNITY_EDITOR）；Player：仅 StreamingAssets/Resources 已打包路径，
        /// 禁止 Application.dataPath/Lua 工程回落；失败明确返回 false，不旁路依赖/不重算受信 hash。
        /// </summary>
        public static bool TryLoadFormalFromDisk(
            out LuaIndex index,
            out Dictionary<string, byte[]> modules,
            out byte[] configBytes,
            out RuntimeCatalog catalog)
        {
            index = null;
            modules = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            configBytes = null;
            catalog = null;

            if (!TryReadFormalCatalogJson(out string catalogJson, out string stagingBase))
                return false;

            catalog = ParseRuntimeCatalog(catalogJson);
            if (catalog == null || catalog.modules == null || catalog.modules.Length == 0)
                return false;
            if (string.IsNullOrEmpty(catalog.configAddress))
                return false;
            if (!string.Equals(catalog.bridgeContract, BridgeApi.BridgeContractValue, StringComparison.Ordinal))
                return false;
            if (catalog.protoVersion != BridgeApi.RequiredProtoVersion)
                return false;
            if (string.IsNullOrEmpty(catalog.requiredAppVersion))
                return false;

            var names = new HashSet<string>(StringComparer.Ordinal);
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < catalog.modules.Length; i++)
            {
                CatalogModule module = catalog.modules[i];
                if (module == null || string.IsNullOrEmpty(module.name) || string.IsNullOrEmpty(module.address))
                    return false;
                if (!names.Add(module.name) || !addresses.Add(module.address))
                    return false;
                if (!IsSafeRelativeAddress(module.address))
                    return false;
                byte[] bytes = ReadFormalModuleBytesUnderBase(stagingBase, module.name, module.address);
                if (bytes == null)
                    return false;
                // 仅校验声明 SHA；失败不改写 catalog / 不发明受信 hash。
                if (string.IsNullOrEmpty(module.sha256)
                    || !string.Equals(HashUtil.Sha256(bytes), module.sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                modules[module.name] = bytes;
            }

            if (!IsSafeRelativeAddress(catalog.configAddress))
                return false;
            configBytes = ReadFormalModuleBytesUnderBase(stagingBase, catalog.configAddress, catalog.configAddress);
            if (configBytes == null)
                return false;
            if (string.IsNullOrEmpty(catalog.configSha256)
                || !string.Equals(HashUtil.Sha256(configBytes), catalog.configSha256, StringComparison.OrdinalIgnoreCase))
                return false;

            index = LuaIndex.FromCatalog(catalog);
            string reject = index.Validate(
                modules, configBytes, BridgeApi.BridgeContractValue, BridgeApi.RequiredProtoVersion, Application.version);
            if (reject != null)
            {
                index = null;
                modules.Clear();
                configBytes = null;
                catalog = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Editor=Assets/Lua/Staging；Player=StreamingAssets 或 Resources（无工程 dataPath 旁路）。
        /// </summary>
        static bool TryReadFormalCatalogJson(out string catalogJson, out string stagingBase)
        {
            catalogJson = null;
            stagingBase = null;
#if UNITY_EDITOR
            string editorCatalog = Path.Combine(Application.dataPath, "Lua", "Staging", "catalog.txt");
            if (File.Exists(editorCatalog))
            {
                catalogJson = File.ReadAllText(editorCatalog, Encoding.UTF8);
                stagingBase = Path.GetFullPath(Path.Combine(Application.dataPath, "Lua", "Staging"));
                return true;
            }
#endif
            string streamingCatalog = Path.Combine(
                Application.streamingAssetsPath, "Lua", "Staging", "catalog.txt");
            if (File.Exists(streamingCatalog))
            {
                catalogJson = File.ReadAllText(streamingCatalog, Encoding.UTF8);
                stagingBase = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Lua", "Staging"));
                return true;
            }

            TextAsset resource = Resources.Load<TextAsset>("Lua/Staging/catalog");
            if (resource != null)
            {
                catalogJson = resource.text;
                stagingBase = null; // Resources 无磁盘基路径；模块须同包 Resources 提供
                return true;
            }

            return false;
        }

        /// <summary>禁止绝对路径与 .. 逃逸；地址须为相对安全段。</summary>
        static bool IsSafeRelativeAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                return false;
            string norm = address.Replace('\\', '/');
            if (Path.IsPathRooted(address) || Path.IsPathRooted(norm))
                return false;
            if (norm.StartsWith("/", StringComparison.Ordinal) || norm.StartsWith("../", StringComparison.Ordinal)
                || norm.Contains("/../") || norm.EndsWith("/..", StringComparison.Ordinal)
                || string.Equals(norm, "..", StringComparison.Ordinal))
                return false;
            return true;
        }

        static byte[] ReadFormalModuleBytesUnderBase(string stagingBase, string moduleName, string address)
        {
            string addr = string.IsNullOrEmpty(address) ? moduleName : address;
            if (!IsSafeRelativeAddress(addr))
                return null;

            if (!string.IsNullOrEmpty(stagingBase))
            {
                string baseFull = Path.GetFullPath(stagingBase);
                string candidate = Path.GetFullPath(Path.Combine(baseFull, addr.Replace('/', Path.DirectorySeparatorChar) + ".txt"));
                string prefix = baseFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? baseFull
                    : baseFull + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);

                // Staging 扁平：address 可能是 LoginWindow，文件在 Staging/LoginWindow.txt
                string flat = Path.GetFullPath(Path.Combine(baseFull, Path.GetFileName(addr.Replace('\\', '/')) + ".txt"));
                if (flat.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(flat))
                    return File.ReadAllBytes(flat);
            }

            // 打包 Resources（Player 或 Editor 无磁盘 Staging 时）
            TextAsset resource = Resources.Load<TextAsset>("Lua/Staging/" + Path.GetFileName(addr.Replace('\\', '/')));
            if (resource != null)
                return CopyBytes(resource.bytes);
            resource = Resources.Load<TextAsset>("Lua/" + addr.Replace('\\', '/'));
            if (resource != null)
                return CopyBytes(resource.bytes);
            return null;
        }

        /// <summary>
        /// 旧字面量 4 模块（entry/panel_base/proto_case/async_case）+ cfg_runtime。
        /// 仅供显式合成/负例或历史探测；正式 entry 集成须用 <see cref="TryLoadFormalFromDisk"/>。
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

        /// <summary>
        /// 正式模块字节。Editor 可经 Staging 基路径；Player 不走工程 dataPath。
        /// 保留公开 API；新路径请用 TryLoadFormalFromDisk（含逃逸校验）。
        /// </summary>
        public static byte[] ReadFormalModuleBytes(string moduleName, string address)
        {
            string stagingBase = null;
#if UNITY_EDITOR
            stagingBase = Path.GetFullPath(Path.Combine(Application.dataPath, "Lua", "Staging"));
#else
            string streamingStaging = Path.Combine(Application.streamingAssetsPath, "Lua", "Staging");
            if (Directory.Exists(streamingStaging))
                stagingBase = Path.GetFullPath(streamingStaging);
#endif
            string addr = string.IsNullOrEmpty(address) ? moduleName : address;
            if (!IsSafeRelativeAddress(addr))
                return null;
            return ReadFormalModuleBytesUnderBase(stagingBase, moduleName, addr);
        }

        /// <summary>读取内置 TextAsset 字节；与 HotfixLoader 同源回退链（旧字面量路径）。</summary>
        public static byte[] ReadBuiltinBytes(string address)
        {
#if UNITY_EDITOR
            string editorPath = Path.Combine(Application.dataPath, "Lua", address + ".txt");
            if (File.Exists(editorPath))
                return File.ReadAllBytes(editorPath);
#endif
            string streaming = Path.Combine(Application.streamingAssetsPath, "Lua", address + ".txt");
            if (File.Exists(streaming))
                return File.ReadAllBytes(streaming);

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
