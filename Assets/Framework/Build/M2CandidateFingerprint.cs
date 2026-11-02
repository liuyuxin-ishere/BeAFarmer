using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Framework.Gameplay;

namespace Game.Framework.Build
{
    /// <summary>
    /// T027/V03：候选安装指纹（schema/profile 策略 + 源/配置/Lua/catalog + Native 三 SHA + baseline 双 SHA + 完整 provenance）。
    /// 固化运行时代码/构建输入变更后旧候选证据失效，须重建。
    /// </summary>
    public static class M2CandidateFingerprint
    {
        public const string Schema = "m2-candidate-fingerprint-v1";

        /// <summary>指纹 JSON 可大于包体上限；Load 显式放宽。</summary>
        public const int FingerprintJsonMaxLength = 8 * 1024 * 1024;

        public sealed class CaptureResult
        {
            public string SchemaVersion { get; set; }
            public string ProfilePath { get; set; }
            public string AppVersion { get; set; }
            public string BridgeContract { get; set; }
            public int ProtoVersion { get; set; }
            public int ResVersion { get; set; }
            public int ConfigVersion { get; set; }
            public string ContentPolicy { get; set; }
            public string OutputRoot { get; set; }
            public string ExeSha256 { get; set; }
            public string GameAssemblySha256 { get; set; }
            public string GlobalMetadataSha256 { get; set; }
            public string CatalogSha256 { get; set; }
            public string ConfigManifestSha256 { get; set; }
            public string BaselineManifestSha256 { get; set; }
            public string BaselineFingerprintSha256 { get; set; }
            public int ProvenanceFileCount { get; set; }
            public Dictionary<string, string> Provenance { get; set; }
        }

        /// <summary>
        /// 在已写出 baseline-manifest / baseline-fingerprint 的候选根上采集并落盘 candidate-fingerprint.json。
        /// <paramref name="baselineCollect"/> 应为 Editor <c>BaselineFingerprint.Collect</c> 的真实 Player 编译源/插件/schema/锁；
        /// 再并入正式 Lua/配置/prefab/profile/脚本/settings。不得把候选报告写回 provenance（避免循环指纹）。
        /// </summary>
        public static CaptureResult Capture(
            M2BuildProfile profile,
            string sourceRoot,
            string outputRoot,
            IDictionary<string, string> baselineCollect = null,
            IDictionary<string, string> extraProvenance = null)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(sourceRoot))
                throw new ArgumentException("sourceRoot required", nameof(sourceRoot));
            if (string.IsNullOrWhiteSpace(outputRoot))
                throw new ArgumentException("outputRoot required", nameof(outputRoot));

            string src = Path.GetFullPath(sourceRoot);
            string outRoot = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(outRoot))
                throw new DirectoryNotFoundException("candidate output missing: " + outRoot);

            string exe = Path.Combine(outRoot, "Player", "Farming.exe");
            string ga = Path.Combine(outRoot, "Player", "GameAssembly.dll");
            string meta = Path.Combine(outRoot, "Player", "Farming_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            string baseline = Path.Combine(outRoot, "baseline-manifest.json");
            string finger = Path.Combine(outRoot, "baseline-fingerprint.json");
            string catalog = Path.Combine(src, "Assets", "Lua", "Staging", "catalog.txt");
            string cfgManifest = Path.Combine(src, "Assets", "ConfigLuban", "config_manifest.json");

            if (!File.Exists(exe))
                throw new FileNotFoundException("Native Farming.exe missing", exe);
            if (!File.Exists(ga))
                throw new FileNotFoundException("GameAssembly.dll missing (IL2CPP)", ga);
            if (!File.Exists(meta))
                throw new FileNotFoundException("global-metadata.dat missing", meta);
            if (!File.Exists(baseline))
                throw new FileNotFoundException("baseline-manifest.json missing", baseline);
            if (!File.Exists(finger))
                throw new FileNotFoundException("baseline-fingerprint.json missing", finger);
            if (!File.Exists(catalog))
                throw new FileNotFoundException("formal lua catalog missing", catalog);
            if (!File.Exists(cfgManifest))
                throw new FileNotFoundException("config_manifest.json missing", cfgManifest);

            var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
            if (baselineCollect != null)
            {
                foreach (KeyValuePair<string, string> kv in baselineCollect)
                    MergeProvenanceEntry(provenance, kv.Key, kv.Value);
            }

            CollectSupplementalSources(src, provenance);
            AddFile(provenance, src, profile.SourcePath);

            if (extraProvenance != null)
            {
                foreach (KeyValuePair<string, string> kv in extraProvenance)
                    MergeProvenanceEntry(provenance, kv.Key, kv.Value);
            }

            // 禁止把本候选输出/后续报告纳入 provenance（循环指纹）。
            StripCandidateOutputEntries(provenance, src, outRoot);

            if (provenance.Count == 0)
                throw new InvalidOperationException("candidate provenance empty");

            var result = new CaptureResult
            {
                SchemaVersion = Schema,
                ProfilePath = profile.SourcePath,
                AppVersion = profile.AppVersion,
                BridgeContract = profile.BridgeContract,
                ProtoVersion = profile.ProtoVersion,
                ResVersion = profile.ResVersion,
                ConfigVersion = profile.ConfigVersion,
                ContentPolicy = profile.ContentPolicy,
                OutputRoot = outRoot,
                ExeSha256 = Sha256File(exe),
                GameAssemblySha256 = Sha256File(ga),
                GlobalMetadataSha256 = Sha256File(meta),
                CatalogSha256 = Sha256File(catalog),
                ConfigManifestSha256 = Sha256File(cfgManifest),
                BaselineManifestSha256 = Sha256File(baseline),
                BaselineFingerprintSha256 = Sha256File(finger),
                ProvenanceFileCount = provenance.Count,
                Provenance = provenance
            };
            RequireSha256(result.ExeSha256, "exeSha256");
            RequireSha256(result.GameAssemblySha256, "gameAssemblySha256");
            RequireSha256(result.GlobalMetadataSha256, "globalMetadataSha256");
            RequireSha256(result.CatalogSha256, "catalogSha256");
            RequireSha256(result.ConfigManifestSha256, "configManifestSha256");
            RequireSha256(result.BaselineManifestSha256, "baselineManifestSha256");
            RequireSha256(result.BaselineFingerprintSha256, "baselineFingerprintSha256");
            foreach (KeyValuePair<string, string> kv in provenance)
                RequireSha256(kv.Value, "provenance:" + kv.Key);

            WriteJson(Path.Combine(outRoot, "candidate-fingerprint.json"), result);
            return result;
        }

        /// <summary>
        /// 正式 Lua/配置/打包资源(Scenes/Res/Settings/Collector)/profile/构建脚本/关键 ProjectSettings —
        /// 与 BaselineFingerprint.Collect（真实 Player 编译源）互补。仅定向目录，不扫 Library/全仓。
        /// </summary>
        public static void CollectSupplementalSources(string sourceRoot, IDictionary<string, string> map)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot))
                throw new ArgumentException("sourceRoot required", nameof(sourceRoot));
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            string src = Path.GetFullPath(sourceRoot);
            AddTree(map, src, Path.Combine(src, "Assets", "Lua"), "*.txt");
            AddTree(map, src, Path.Combine(src, "Assets", "ConfigLuban"), "*");
            AddTree(map, src, Path.Combine(src, "Assets", "StreamingAssets", "ConfigLuban"), "*");
            // 被打包资源及其 .meta：新增/删除/修改须使旧 Native 证据失效（含 UI prefab）。
            AddTree(map, src, Path.Combine(src, "Assets", "Res"), "*");
            AddTree(map, src, Path.Combine(src, "Assets", "Res"), "*.meta");
            // Resources are included directly in Player;the farm material references a separate Shader source.
            // Bind both so a visual-resource-only edit invalidates prior Native evidence.
            AddTree(map, src, Path.Combine(src, "Assets", "Resources"), "*");
            AddTree(map, src, Path.Combine(src, "Assets", "Resources"), "*.meta");
            AddFile(map, src, Path.Combine(src, "Assets", "Resources.meta"));
            AddTree(map, src, Path.Combine(src, "Assets", "Framework", "Home", "Shaders"), "*");
            AddTree(map, src, Path.Combine(src, "Assets", "Framework", "Home", "Shaders"), "*.meta");
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Home", "Shaders.meta"));
            AddTree(map, src, Path.Combine(src, "Assets", "Scenes"), "*");
            AddTree(map, src, Path.Combine(src, "Assets", "Scenes"), "*.meta");
            AddTree(map, src, Path.Combine(src, "Assets", "Settings"), "*");
            AddTree(map, src, Path.Combine(src, "Assets", "Settings"), "*.meta");
            AddFile(map, src, Path.Combine(src, "Assets", "BundleCollectorSetting.asset"));
            AddFile(map, src, Path.Combine(src, "Assets", "BundleCollectorSetting.asset.meta"));
            AddTree(map, src, Path.Combine(src, "BuildProfiles"), "*");
            AddFile(map, src, Path.Combine(src, "Packages", "manifest.json"));
            AddFile(map, src, Path.Combine(src, "Packages", "packages-lock.json"));
            AddFile(map, src, Path.Combine(src, "ProjectSettings", "ProjectSettings.asset"));
            AddFile(map, src, Path.Combine(src, "ProjectSettings", "EditorBuildSettings.asset"));
            AddFile(map, src, Path.Combine(src, "ProjectSettings", "ProjectVersion.txt"));
            AddFile(map, src, Path.Combine(src, "Tools", "build_client_win.ps1"));
            AddFile(map, src, Path.Combine(src, "Tools", "gen_config.ps1"));
            AddFile(map, src, Path.Combine(src, "Tools", "gen_proto.ps1"));
            // 外部验收 harness（run_native_u01_gate / send_accept_cmd）不是 Player 编译输入；
            // as-run SHA 由 Gate 写入 harness-fingerprint.json，不得绑入候选 provenance。
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Boot", "AcceptLocalDriver.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Boot", "Launcher.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Boot", "LauncherBootBehaviour.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Build", "M2BuildProfile.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Build", "M2PatchContentPolicy.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Build", "M2CandidateFingerprint.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Lua", "BridgeApi.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "Lua", "LuaGenConfig.cs"));
            AddFile(map, src, Path.Combine(src, "Assets", "Framework", "UI", "M2U01WindowBuilder.cs"));
        }

        /// <summary>
        /// 用当前磁盘 Native 三文件 + 当前源 provenance 构造可比对快照（不落盘，供 ValidateCompatible）。
        /// </summary>
        public static CaptureResult BuildCurrentSnapshot(
            M2BuildProfile profile,
            string sourceRoot,
            string outputRoot,
            IDictionary<string, string> baselineCollect = null,
            IDictionary<string, string> extraProvenance = null)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            string src = Path.GetFullPath(sourceRoot);
            string outRoot = Path.GetFullPath(outputRoot);
            string exe = Path.Combine(outRoot, "Player", "Farming.exe");
            string ga = Path.Combine(outRoot, "Player", "GameAssembly.dll");
            string meta = Path.Combine(outRoot, "Player", "Farming_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            string baseline = Path.Combine(outRoot, "baseline-manifest.json");
            string finger = Path.Combine(outRoot, "baseline-fingerprint.json");
            string catalog = Path.Combine(src, "Assets", "Lua", "Staging", "catalog.txt");
            string cfgManifest = Path.Combine(src, "Assets", "ConfigLuban", "config_manifest.json");
            if (!File.Exists(exe) || !File.Exists(ga) || !File.Exists(meta))
                throw new FileNotFoundException("native binaries missing under " + outRoot);
            if (!File.Exists(baseline) || !File.Exists(finger))
                throw new FileNotFoundException("baseline files missing under " + outRoot);
            if (!File.Exists(catalog) || !File.Exists(cfgManifest))
                throw new FileNotFoundException("catalog/config missing under " + src);

            var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
            if (baselineCollect != null)
            {
                foreach (KeyValuePair<string, string> kv in baselineCollect)
                    MergeProvenanceEntry(provenance, kv.Key, kv.Value);
            }

            CollectSupplementalSources(src, provenance);
            AddFile(provenance, src, profile.SourcePath);
            if (extraProvenance != null)
            {
                foreach (KeyValuePair<string, string> kv in extraProvenance)
                    MergeProvenanceEntry(provenance, kv.Key, kv.Value);
            }

            StripCandidateOutputEntries(provenance, src, outRoot);
            if (provenance.Count == 0)
                throw new InvalidOperationException("candidate provenance empty");

            return new CaptureResult
            {
                SchemaVersion = Schema,
                ProfilePath = profile.SourcePath,
                AppVersion = profile.AppVersion,
                BridgeContract = profile.BridgeContract,
                ProtoVersion = profile.ProtoVersion,
                ResVersion = profile.ResVersion,
                ConfigVersion = profile.ConfigVersion,
                ContentPolicy = profile.ContentPolicy,
                OutputRoot = outRoot,
                ExeSha256 = Sha256File(exe),
                GameAssemblySha256 = Sha256File(ga),
                GlobalMetadataSha256 = Sha256File(meta),
                CatalogSha256 = Sha256File(catalog),
                ConfigManifestSha256 = Sha256File(cfgManifest),
                BaselineManifestSha256 = Sha256File(baseline),
                BaselineFingerprintSha256 = Sha256File(finger),
                ProvenanceFileCount = provenance.Count,
                Provenance = provenance
            };
        }

        /// <summary>
        /// 比对两次候选：schema、profile 策略、三 Native SHA、catalog/config、双 baseline SHA、完整 provenance（changed/added/removed）。
        /// </summary>
        public static void ValidateCompatible(CaptureResult baseline, CaptureResult current)
        {
            if (baseline == null)
                throw new ArgumentNullException(nameof(baseline));
            if (current == null)
                throw new ArgumentNullException(nameof(current));
            RequireEq(baseline.SchemaVersion, current.SchemaVersion, "schema");
            RequireEq(baseline.ContentPolicy, current.ContentPolicy, "contentPolicy");
            RequireEq(baseline.AppVersion, current.AppVersion, "appVersion");
            RequireEq(baseline.BridgeContract, current.BridgeContract, "bridgeContract");
            if (baseline.ProtoVersion != current.ProtoVersion)
                throw new InvalidOperationException("protoVersion mismatch");
            if (baseline.ResVersion != current.ResVersion)
                throw new InvalidOperationException("resVersion mismatch");
            if (baseline.ConfigVersion != current.ConfigVersion)
                throw new InvalidOperationException("configVersion mismatch");
            RequireEq(baseline.ExeSha256, current.ExeSha256, "exeSha256");
            RequireEq(baseline.GameAssemblySha256, current.GameAssemblySha256, "gameAssemblySha256");
            RequireEq(baseline.GlobalMetadataSha256, current.GlobalMetadataSha256, "globalMetadataSha256");
            RequireEq(baseline.CatalogSha256, current.CatalogSha256, "catalogSha256");
            RequireEq(baseline.ConfigManifestSha256, current.ConfigManifestSha256, "configManifestSha256");
            RequireEq(baseline.BaselineManifestSha256, current.BaselineManifestSha256, "baselineManifestSha256");
            RequireEq(baseline.BaselineFingerprintSha256, current.BaselineFingerprintSha256, "baselineFingerprintSha256");

            if (baseline.Provenance == null || current.Provenance == null)
                throw new InvalidOperationException("provenance missing");
            if (baseline.ProvenanceFileCount != baseline.Provenance.Count)
                throw new InvalidOperationException("baseline provenanceFileCount mismatch");
            if (current.ProvenanceFileCount != current.Provenance.Count)
                throw new InvalidOperationException("current provenanceFileCount mismatch");

            foreach (KeyValuePair<string, string> kv in baseline.Provenance)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new InvalidOperationException("baseline provenance empty path");
                if (!current.Provenance.TryGetValue(kv.Key, out string now))
                    throw new InvalidOperationException("provenance removed: " + kv.Key);
                if (!string.Equals(kv.Value, now, StringComparison.Ordinal))
                    throw new InvalidOperationException("provenance changed: " + kv.Key);
            }

            foreach (KeyValuePair<string, string> kv in current.Provenance)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new InvalidOperationException("current provenance empty path");
                if (!baseline.Provenance.ContainsKey(kv.Key))
                    throw new InvalidOperationException("provenance added: " + kv.Key);
            }
        }

        /// <summary>从已落盘 JSON 加载；缺字段/重复 path/非法 SHA/计数不符即失败。复用 GameplayStrictJson。</summary>
        public static CaptureResult Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path required", nameof(path));
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException("candidate-fingerprint missing", full);
            string text = File.ReadAllText(full, Encoding.UTF8);
            GameplayStrictJson.Node root = GameplayStrictJson.Parse(text, FingerprintJsonMaxLength);
            if (!(root is GameplayStrictJson.ObjectNode obj))
                throw new FormatException("candidate fingerprint root must be object");

            var r = new CaptureResult
            {
                SchemaVersion = RequireString(obj, "schema"),
                ProfilePath = RequireString(obj, "profilePath"),
                AppVersion = RequireString(obj, "appVersion"),
                BridgeContract = RequireString(obj, "bridgeContract"),
                ProtoVersion = RequireInt(obj, "protoVersion"),
                ResVersion = RequireInt(obj, "resVersion"),
                ConfigVersion = RequireInt(obj, "configVersion"),
                ContentPolicy = RequireString(obj, "contentPolicy"),
                OutputRoot = RequireString(obj, "outputRoot"),
                ExeSha256 = RequireString(obj, "exeSha256"),
                GameAssemblySha256 = RequireString(obj, "gameAssemblySha256"),
                GlobalMetadataSha256 = RequireString(obj, "globalMetadataSha256"),
                CatalogSha256 = RequireString(obj, "catalogSha256"),
                ConfigManifestSha256 = RequireString(obj, "configManifestSha256"),
                BaselineManifestSha256 = RequireString(obj, "baselineManifestSha256"),
                BaselineFingerprintSha256 = RequireString(obj, "baselineFingerprintSha256"),
                ProvenanceFileCount = RequireInt(obj, "provenanceFileCount"),
                Provenance = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            if (!string.Equals(r.SchemaVersion, Schema, StringComparison.Ordinal))
                throw new FormatException("unexpected candidate fingerprint schema: " + r.SchemaVersion);

            RequireSha256(r.ExeSha256, "exeSha256");
            RequireSha256(r.GameAssemblySha256, "gameAssemblySha256");
            RequireSha256(r.GlobalMetadataSha256, "globalMetadataSha256");
            RequireSha256(r.CatalogSha256, "catalogSha256");
            RequireSha256(r.ConfigManifestSha256, "configManifestSha256");
            RequireSha256(r.BaselineManifestSha256, "baselineManifestSha256");
            RequireSha256(r.BaselineFingerprintSha256, "baselineFingerprintSha256");

            if (!obj.Fields.TryGetValue("provenance", out GameplayStrictJson.Node provNode))
                throw new FormatException("missing field: provenance");
            if (!(provNode is GameplayStrictJson.ArrayNode provArr))
                throw new FormatException("provenance must be array");
            for (int i = 0; i < provArr.Items.Count; i++)
            {
                if (!(provArr.Items[i] is GameplayStrictJson.ObjectNode entry))
                    throw new FormatException("provenance[" + i + "] must be object");
                string p = RequireString(entry, "path");
                string sha = RequireString(entry, "sha256");
                if (string.IsNullOrEmpty(p))
                    throw new FormatException("provenance path empty");
                RequireSha256(sha, "provenance:" + p);
                if (r.Provenance.ContainsKey(p))
                    throw new FormatException("duplicate provenance path: " + p);
                r.Provenance.Add(p, sha);
            }

            if (r.ProvenanceFileCount != r.Provenance.Count)
                throw new FormatException("provenanceFileCount mismatch");
            if (r.Provenance.Count == 0)
                throw new FormatException("provenance empty");
            return r;
        }

        static void WriteJson(string path, CaptureResult r)
        {
            var keys = new List<string>(r.Provenance.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"schema\": \"" + Schema + "\",");
            sb.AppendLine("  \"profilePath\": \"" + JsonEscape(r.ProfilePath) + "\",");
            sb.AppendLine("  \"appVersion\": \"" + JsonEscape(r.AppVersion) + "\",");
            sb.AppendLine("  \"bridgeContract\": \"" + JsonEscape(r.BridgeContract) + "\",");
            sb.AppendLine("  \"protoVersion\": " + r.ProtoVersion.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"resVersion\": " + r.ResVersion.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"configVersion\": " + r.ConfigVersion.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"contentPolicy\": \"" + JsonEscape(r.ContentPolicy) + "\",");
            sb.AppendLine("  \"outputRoot\": \"" + JsonEscape(r.OutputRoot.Replace('\\', '/')) + "\",");
            sb.AppendLine("  \"exeSha256\": \"" + r.ExeSha256 + "\",");
            sb.AppendLine("  \"gameAssemblySha256\": \"" + r.GameAssemblySha256 + "\",");
            sb.AppendLine("  \"globalMetadataSha256\": \"" + r.GlobalMetadataSha256 + "\",");
            sb.AppendLine("  \"catalogSha256\": \"" + r.CatalogSha256 + "\",");
            sb.AppendLine("  \"configManifestSha256\": \"" + r.ConfigManifestSha256 + "\",");
            sb.AppendLine("  \"baselineManifestSha256\": \"" + r.BaselineManifestSha256 + "\",");
            sb.AppendLine("  \"baselineFingerprintSha256\": \"" + r.BaselineFingerprintSha256 + "\",");
            sb.AppendLine("  \"provenanceFileCount\": " + r.ProvenanceFileCount.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"provenance\": [");
            for (int i = 0; i < keys.Count; i++)
            {
                string k = keys[i];
                sb.Append("    {\"path\":\"").Append(JsonEscape(k)).Append("\",\"sha256\":\"")
                    .Append(r.Provenance[k]).Append("\"}");
                if (i + 1 < keys.Count)
                    sb.Append(',');
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static void StripCandidateOutputEntries(
            IDictionary<string, string> map, string sourceRoot, string outputRoot)
        {
            string outNorm = Path.GetFullPath(outputRoot).Replace('\\', '/').TrimEnd('/');
            var drop = new List<string>();
            foreach (string key in map.Keys)
            {
                string abs;
                if (Path.IsPathRooted(key))
                    abs = Path.GetFullPath(key).Replace('\\', '/');
                else
                    abs = Path.GetFullPath(Path.Combine(sourceRoot, key.Replace('/', Path.DirectorySeparatorChar)))
                        .Replace('\\', '/');
                if (abs.StartsWith(outNorm + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(abs, outNorm, StringComparison.OrdinalIgnoreCase))
                {
                    drop.Add(key);
                    continue;
                }

                string name = Path.GetFileName(key);
                if (string.Equals(name, "candidate-fingerprint.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "clientbuild-report.txt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "player-build-report.txt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "gate-report.txt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "gate-summary.json", StringComparison.OrdinalIgnoreCase))
                {
                    drop.Add(key);
                }
            }

            for (int i = 0; i < drop.Count; i++)
                map.Remove(drop[i]);
        }

        static void MergeProvenanceEntry(IDictionary<string, string> map, string path, string sha)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sha))
                return;
            string norm = path.Replace('\\', '/');
            if (map.TryGetValue(norm, out string existing)
                && !string.Equals(existing, sha, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("provenance conflict: " + norm);
            }

            map[norm] = sha;
        }

        static void AddFile(IDictionary<string, string> map, string sourceRoot, string absPath)
        {
            if (string.IsNullOrWhiteSpace(absPath) || !File.Exists(absPath))
                return;
            string norm = Path.GetFullPath(absPath).Replace('\\', '/');
            string rootNorm = Path.GetFullPath(sourceRoot).Replace('\\', '/');
            string rel = norm.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase)
                ? norm.Substring(rootNorm.Length).TrimStart('/')
                : Path.GetFileName(absPath);
            if (ShouldSkipRel(rel))
                return;
            MergeProvenanceEntry(map, rel, Sha256File(absPath));
        }

        static void AddTree(IDictionary<string, string> map, string sourceRoot, string absDir, string pattern)
        {
            if (!Directory.Exists(absDir))
                return;
            foreach (string file in Directory.GetFiles(absDir, pattern, SearchOption.AllDirectories))
            {
                string norm = file.Replace('\\', '/');
                if (norm.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (norm.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (norm.IndexOf("/Library/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (pattern != "*.meta" && norm.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddFile(map, sourceRoot, file);
            }
        }

        static bool ShouldSkipRel(string rel)
        {
            if (string.IsNullOrEmpty(rel))
                return true;
            string n = rel.Replace('\\', '/');
            if (n.IndexOf("Docs/Acceptance/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (n.IndexOf("Docs/LocalBackups/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (n.StartsWith("Builds/", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        static void RequireEq(string a, string b, string field)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
                throw new InvalidOperationException(field + " mismatch");
        }

        static string RequireString(GameplayStrictJson.ObjectNode obj, string key)
        {
            if (!obj.Fields.TryGetValue(key, out GameplayStrictJson.Node n))
                throw new FormatException("missing field: " + key);
            if (!(n is GameplayStrictJson.StringNode s))
                throw new FormatException("field not string: " + key);
            return s.Value ?? "";
        }

        static int RequireInt(GameplayStrictJson.ObjectNode obj, string key)
        {
            if (!obj.Fields.TryGetValue(key, out GameplayStrictJson.Node n))
                throw new FormatException("missing field: " + key);
            if (!(n is GameplayStrictJson.NumberNode num))
                throw new FormatException("field not int: " + key);
            if (!int.TryParse(num.Literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                throw new FormatException("field not int: " + key);
            return v;
        }

        static void RequireSha256(string sha, string field)
        {
            if (string.IsNullOrEmpty(sha) || sha.Length != 64)
                throw new FormatException("illegal sha256 length: " + field);
            for (int i = 0; i < sha.Length; i++)
            {
                char c = sha[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                    throw new FormatException("illegal sha256: " + field);
            }
        }

        static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string Sha256File(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
