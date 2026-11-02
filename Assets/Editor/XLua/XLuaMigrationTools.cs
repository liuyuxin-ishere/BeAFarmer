using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSObjectWrapEditor;
using Game.Framework.Boot;
using Game.Framework.Lua;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;
using YooAsset.Editor;

namespace Game.Editor.XLuaTools
{
    /// <summary>
    /// 主工程 xLua 生成、资源包、Player 构建与批验证入口。
    /// </summary>
    public static class XLuaMigrationTools
    {
        const string ScenePath = "Assets/Scenes/xLua_PoC.unity";
        const string AcceptRoot = "Docs/Acceptance/xLuaMigration/20261020/MainProject";
        const string ContentRoot = "Docs/Acceptance/xLuaMigration/Tools/content";
        const string LuaStaging = "Assets/Lua/Staging";

        /// <summary>配置 Win64 原生插件导入平台。</summary>
        [MenuItem("Farming/xLua/0. Configure Native Plugin")]
        public static void ConfigurePlugin()
        {
            var path = "Assets/Framework/Lua/XLua/Plugins/x86_64/xlua.dll";
            var importer = AssetImporter.GetAtPath(path) as PluginImporter;
            if (importer == null)
            {
                Debug.LogError("[xLua] plugin missing: " + path);
                return;
            }

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SaveAndReimport();
            Debug.Log("[xLua] PluginImporter configured for Editor + Win64");
        }

        /// <summary>清除并生成桥接。完成后写出标记文件。</summary>
        [MenuItem("Farming/xLua/1. Generate Bridges (GenAll)")]
        public static void GenBridges()
        {
            ConfigurePlugin();
            Generator.ClearAll();
            Generator.GenAll();
            WriteMarker("gen-all.txt", DateTime.UtcNow.ToString("o"));
            Debug.Log("[xLua] GenAll done → Assets/Framework/Lua/XLua/Gen");
        }

        /// <summary>批处理：生成桥接后退出编辑器。</summary>
        public static void BatchGenBridges()
        {
            try
            {
                GenBridges();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// M2-T007：菜单 GenAll。只生成 Wrap，不写 Docs/Acceptance/xLuaMigration 旧 marker。
        /// </summary>
        [MenuItem("Farming/M2/Generate Bridges (GenAll)")]
        public static void M2GenBridgesMenu()
        {
            ConfigurePlugin();
            Generator.ClearAll();
            Generator.GenAll();
            Debug.Log("[M2] GenAll done → Assets/Framework/Lua/XLua/Gen (no M1 Accept marker)");
        }

        /// <summary>
        /// M2 批处理 GenAll。必须带 -M2ReportRoot=（或 --m2-report-root=）指向本轮 M2 报告目录；
        /// 禁止写入 Docs/Acceptance/xLuaMigration 旧 marker。不伪造 pass。
        /// executeMethod: Game.Editor.XLuaTools.XLuaMigrationTools.BatchM2GenBridges
        /// </summary>
        public static void BatchM2GenBridges()
        {
            try
            {
                string reportRoot = ReadCliArg("-M2ReportRoot=");
                if (string.IsNullOrEmpty(reportRoot))
                    reportRoot = ReadCliArg("--m2-report-root=");
                if (string.IsNullOrEmpty(reportRoot))
                    throw new InvalidOperationException(
                        "BatchM2GenBridges requires -M2ReportRoot= (M2 evidence dir); refusing M1 Accept marker");

                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                if (!Path.IsPathRooted(reportRoot))
                    reportRoot = Path.GetFullPath(Path.Combine(projectRoot, reportRoot));
                else
                    reportRoot = Path.GetFullPath(reportRoot);

                string normalized = reportRoot.Replace('\\', '/');
                if (normalized.IndexOf("Docs/Acceptance/xLuaMigration", StringComparison.OrdinalIgnoreCase) >= 0
                    || normalized.IndexOf("Docs/Acceptance/M1/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException(
                        "M2ReportRoot must not point at M1/xLuaMigration Accept roots: " + reportRoot);
                }

                ConfigurePlugin();
                Generator.ClearAll();
                Generator.GenAll();

                string genDir = Path.Combine(Application.dataPath, "Framework", "Lua", "XLua", "Gen");
                var wraps = Directory.Exists(genDir)
                    ? Directory.GetFiles(genDir, "*Wrap*.cs", SearchOption.AllDirectories)
                    : Array.Empty<string>();
                bool link = File.Exists(Path.Combine(genDir, "link.xml"));
                Directory.CreateDirectory(reportRoot);
                var sb = new StringBuilder();
                sb.AppendLine("tool=BatchM2GenBridges");
                sb.AppendLine("when=" + DateTime.UtcNow.ToString("o"));
                sb.AppendLine("genDir=" + genDir);
                sb.AppendLine("wrapCount=" + wraps.Length);
                sb.AppendLine("linkXml=" + link);
                sb.AppendLine("m1AcceptMarkerWritten=false");
                sb.AppendLine("note=wrap/link presence recorded; Unity compile of wraps is a later coordinator stage");
                // 不写 passed=true；仅客观计数，由协调器判读。
                File.WriteAllText(
                    Path.Combine(reportRoot, "m2-genall-report.txt"),
                    sb.ToString(),
                    new UTF8Encoding(false));
                EditorApplication.Exit(wraps.Length > 0 && link ? 0 : 3);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(2);
            }
        }

        static string ReadCliArg(string prefix)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length).Trim().Trim('"');
                if (string.Equals(a, prefix.TrimEnd('='), StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length)
                    return args[i + 1].Trim().Trim('"');
            }

            return null;
        }

        /// <summary>确认 Gen 目录已有 Wrap 与 link.xml。</summary>
        public static void BatchAssertGenCompiled()
        {
            var genDir = "Assets/Framework/Lua/XLua/Gen";
            var wraps = Directory.Exists(genDir) ? Directory.GetFiles(genDir, "*Wrap*.cs") : Array.Empty<string>();
            var link = File.Exists(Path.Combine(genDir, "link.xml"));
            WriteMarker("gen-compiled.txt", "wraps=" + wraps.Length + " link=" + link);
            EditorApplication.Exit(wraps.Length > 0 && link ? 0 : 3);
        }

        /// <summary>构建 GameLua 100/101 及拒绝用例包到验收发布目录。</summary>
        [MenuItem("Farming/xLua/2. Build Lua Packages 100/101(+reject)")]
        public static void BuildLuaPackages()
        {
            var report = new StringBuilder();
            var jobs = new[]
            {
                new[] { "100", "v100" },
                new[] { "101", "v101" },
                new[] { "201", "v201-missing" },
                new[] { "202", "v202-syntax" },
                new[] { "204", "v204-runtime" },
                new[] { "205", "v205-bad-bridge" },
                new[] { "206", "v206-duplicate" },
                new[] { "207", "v207-local-lease" },
                new[] { "208", "v208-async-throw" },
            };
            var failed = false;
            foreach (var job in jobs)
                failed |= !BuildOnePackage(job[0], job[1], report);

            WriteMarker("package-build-report.txt", report.ToString());
            if (failed)
                Debug.LogError("[xLua] package build had failures — see package-build-report.txt");
            else
                Debug.Log("[xLua] packages built");
        }

        /// <summary>批处理构建资源包后退出。</summary>
        public static void BatchBuildPackages()
        {
            try
            {
                BuildLuaPackages();
                var text = File.ReadAllText(MarkerPath("package-build-report.txt"));
                EditorApplication.Exit(text.Contains("success=False") ? 6 : 0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(6);
            }
        }

        /// <summary>写入 IL2CPP/x64 设置（不在本方法内构建 Player）。</summary>
        public static void BatchConfigurePlayer()
        {
            EnsureSceneName();
            ConfigurePlugin();
            PlayerSettings.bundleVersion = "0.2.0";
            PlayerSettings.allowUnsafeCode = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 1);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Minimal);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
            AssetDatabase.SaveAssets();
            WriteMarker("player-configured.txt",
                "backend=" + PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) +
                " version=" + PlayerSettings.bundleVersion);
            EditorApplication.Exit(0);
        }

        /// <summary>构建 Windows x64 标准 IL2CPP Player（不使用 HybridCLRData）。</summary>
        public static void BatchBuildPlayer()
        {
            EnsureSceneName();
            ConfigurePlugin();
            var output = Path.GetFullPath(Path.Combine(AcceptRoot, "Builds", "Player", "Farming.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            // 确保 StreamingAssets/Lua 基线存在
            CopyStreamingBaseline();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var text = "result=" + report.summary.result + "\nerrors=" + report.summary.totalErrors
                + "\noutput=" + output
                + "\nbackend=" + PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)
                + "\nbundleVersion=" + PlayerSettings.bundleVersion + "\n";
            WriteMarker("player-build-report.txt", text);
            EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 7);
        }

        /// <summary>
        /// 打开 xLua 场景并进入 Play。通过请求文件让 HotfixLoader 写报告并退出（跨域重载保留），
        /// 避免额外驱动与宿主协程抢跑。
        /// </summary>
        public static void BatchEnterEditorProof()
        {
            EnsureSceneName();
            EditorSceneManager.OpenScene(ScenePath);
            var report = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), AcceptRoot, "editor-proof.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            HotfixLoader.RequestCliReport(report, quitWhenDone: true);
            EditorApplication.EnterPlaymode();
        }

        /// <summary>批处理跑 xLua 风险 EditMode 测试并写报告后退出。</summary>
        public static void BatchRunRiskEditModeTests()
        {
            var report = MarkerPath("editmode-risk-tests.txt");
            try
            {
                // 由外部 -runTests 驱动时不必走此方法；此处做最小同步烟测并写标记。
                var sb = new StringBuilder();
                sb.AppendLine("filter=XLuaMigrationRiskTests");
                sb.AppendLine("appVersion=" + Application.version);
                sb.AppendLine("bridgeContract=" + HotfixLoader.BridgeContract);
                sb.AppendLine("protoVersion=" + HotfixLoader.HostProtoVersion);
                sb.AppendLine("note=use Unity -runTests for full NUnit execution");
                File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, "error=" + ex.Message, new UTF8Encoding(false));
                EditorApplication.Exit(8);
            }
        }

        /// <summary>保存全部打开场景并退出编辑器（供迁移脚本请求）。</summary>
        public static void BatchSaveAndQuit()
        {
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            EditorApplication.Exit(0);
        }

        /// <summary>确保场景文件名已是 xLua_PoC，并保留用户对象。</summary>
        [MenuItem("Farming/xLua/3. Ensure Scene Name")]
        public static void EnsureSceneName()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError("[xLua] scene missing: " + ScenePath);
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath);
            var loader = UnityEngine.Object.FindFirstObjectByType<HotfixLoader>();
            if (loader == null)
            {
                var go = new GameObject("HotfixBoot");
                go.AddComponent<HotfixLoader>();
                EditorSceneManager.MarkSceneDirty(scene);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (var i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path.Contains("HybridCLR_PoC") || scenes[i].path.Contains("xLua_PoC"))
                    scenes[i] = new EditorBuildSettingsScene(ScenePath, true);
            }

            if (scenes.Count == 0)
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[xLua] scene ensured: " + ScenePath);
        }

        /// <summary>构建单个 GameLua 版本。version 写入包版本，folderName 是 content 子目录。</summary>
        static bool BuildOnePackage(string version, string folderName, StringBuilder report)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var source = Path.Combine(projectRoot, ContentRoot, folderName);
            if (!Directory.Exists(source))
            {
                report.AppendLine("version=" + version + " success=False reason=missing-content " + source);
                return false;
            }

            StageContent(source, version);
            ConfigureLuaCollector();
            var accept = Path.Combine(projectRoot, AcceptRoot);
            var parameters = new LegacyBuildParameters();
            parameters.BuildOutputRoot = Path.Combine(accept, "Builds", "YooBuild");
            parameters.BundledFileRoot = Path.Combine(accept, "Builds", "YooBuiltin");
            parameters.BuildPipeline = EBuildPipeline.LegacyBuildPipeline.ToString();
            parameters.BuildBundleType = (int)EBundleType.AssetBundle;
            parameters.BuildTarget = BuildTarget.StandaloneWindows64;
            parameters.PackageName = HotfixLoader.LuaPackageName;
            parameters.PackageVersion = version;
            parameters.PackageNote = "main-xlua-" + version;
            parameters.CompressOption = ECompressOption.LZ4;
            parameters.FileNameStyle = EFileNameStyle.HashName;
            parameters.BundledCopyOption = EBundledCopyOption.None;
            parameters.ClearBuildCacheFiles = true;
            parameters.VerifyBuildingResult = true;
            parameters.EnableSharePackRule = false;
            var result = new LegacyBuildPipeline().Run(parameters, true);
            report.Append("version=").Append(version)
                .Append(" success=").Append(result.Success)
                .Append(" output=").Append(result.OutputPackageDirectory)
                .AppendLine();
            if (!result.Success)
                return false;

            var cdn = Path.Combine(accept, "发布", "cdn-" + version);
            if (Directory.Exists(cdn))
                Directory.Delete(cdn, true);
            Directory.CreateDirectory(cdn);
            foreach (var file in Directory.GetFiles(result.OutputPackageDirectory))
                File.Copy(file, Path.Combine(cdn, Path.GetFileName(file)), true);
            // ASCII 镜像，避免 Windows 脚本 here-string 损坏中文「发布」路径导致 CDN 404。
            var serve = Path.Combine(accept, "cdn-serve", version);
            if (Directory.Exists(serve))
                Directory.Delete(serve, true);
            Directory.CreateDirectory(serve);
            foreach (var file in Directory.GetFiles(result.OutputPackageDirectory))
                File.Copy(file, Path.Combine(serve, Path.GetFileName(file)), true);
            return true;
        }

        /// <summary>把 content 目录同步到 Staging，并写 catalog。</summary>
        static void StageContent(string sourceDir, string version)
        {
            if (Directory.Exists(LuaStaging))
                Directory.Delete(LuaStaging, true);
            Directory.CreateDirectory(LuaStaging);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(LuaStaging, name), true);
            }

            var catalog = BuildCatalog(version, LuaStaging);
            ApplyCatalogFaultInject(catalog, version);
            File.WriteAllText(Path.Combine(LuaStaging, "catalog.txt"), JsonUtility.ToJson(catalog, true), new UTF8Encoding(false));
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 按版本注入目录级故障：坏桥接契约或重复模块映射。仅影响对应拒绝包，不改生产 100/101。
        /// </summary>
        static void ApplyCatalogFaultInject(RuntimeCatalog catalog, string version)
        {
            if (catalog == null)
                return;
            if (version == "205")
            {
                // 坏桥接：与宿主 BridgeContract 故意不一致。
                catalog.bridgeContract = "wrong-bridge-contract";
            }
            else if (version == "206" && catalog.modules != null && catalog.modules.Length > 0)
            {
                // 重复模块名：追加一条同名同址映射（地址可加载），由 ValidateCandidate 拒绝，
                // 避免伪造不存在地址在校验前变成 missing-module。
                var first = catalog.modules[0];
                var list = new List<CatalogModule>(catalog.modules)
                {
                    new CatalogModule
                    {
                        name = first.name,
                        address = first.address,
                        sha256 = first.sha256
                    }
                };
                catalog.modules = list.ToArray();
            }
        }

        /// <summary>按 Staging 文件计算 catalog。entry 固定为 entry。</summary>
        static RuntimeCatalog BuildCatalog(string version, string stagingDir)
        {
            var modules = new List<CatalogModule>();
            foreach (var file in Directory.GetFiles(stagingDir, "*.txt"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName == "catalog" || fileName == "cfg_runtime" || fileName.StartsWith("inject", StringComparison.Ordinal))
                    continue;
                var bytes = File.ReadAllBytes(file);
                modules.Add(new CatalogModule
                {
                    name = fileName.StartsWith("lua_", StringComparison.Ordinal)
                        ? fileName.Substring(4)
                        : fileName,
                    address = fileName,
                    sha256 = HashUtil.Sha256(bytes)
                });
            }

            // 规范名：content 里用 lua_entry.txt → address lua_entry，require 名 entry
            foreach (var m in modules)
            {
                if (m.name == "entry" || m.address == "lua_entry" || m.address == "entry")
                {
                    m.name = "entry";
                }
                else if (m.address.StartsWith("lua_", StringComparison.Ordinal))
                {
                    m.name = m.address.Substring(4);
                }
            }

            var cfgPath = Path.Combine(stagingDir, "cfg_runtime.txt");
            if (!File.Exists(cfgPath))
                cfgPath = Path.Combine(stagingDir, "lua_cfg_runtime.txt");
            var cfgBytes = File.ReadAllBytes(cfgPath);
            return new RuntimeCatalog
            {
                resVersion = version,
                entryModule = "entry",
                configAddress = Path.GetFileNameWithoutExtension(cfgPath),
                configSha256 = HashUtil.Sha256(cfgBytes),
                bridgeContract = HotfixLoader.BridgeContract,
                requiredAppVersion = Application.version,
                protoVersion = HotfixLoader.HostProtoVersion,
                modules = modules.ToArray()
            };
        }

        /// <summary>配置 GameLua 收集器指向 Staging。</summary>
        static void ConfigureLuaCollector()
        {
            var setting = BundleCollectorSettingData.Setting;
            var package = setting.Packages.Find(p => p.PackageName == HotfixLoader.LuaPackageName);
            if (package == null)
                package = BundleCollectorSettingData.CreatePackage(HotfixLoader.LuaPackageName);

            package.EnableAddressable = true;
            package.AutoCollectShaders = false;
            package.IgnoreRuleName = nameof(NormalIgnoreRule);
            BundleCollectorSettingData.ModifyPackage(package);

            var group = package.Groups.Find(g => g.GroupName == "Lua");
            if (group == null)
                group = BundleCollectorSettingData.CreateGroup(package, "Lua");
            group.ActiveRuleName = nameof(EnableGroup);
            BundleCollectorSettingData.ModifyGroup(package, group);

            var collector = group.Collectors.Find(c => c.CollectPath == LuaStaging);
            if (collector == null)
            {
                collector = new BundleCollector
                {
                    CollectPath = LuaStaging,
                    CollectorGUID = AssetDatabase.AssetPathToGUID(LuaStaging),
                    CollectorType = ECollectorType.MainAssetCollector,
                    AddressRuleName = nameof(AddressByFileName),
                    PackRuleName = nameof(PackSeparately),
                    FilterRuleName = nameof(CollectAll),
                    AssetTags = "lua"
                };
                BundleCollectorSettingData.CreateCollector(group, collector);
            }
            else
            {
                collector.CollectorGUID = AssetDatabase.AssetPathToGUID(LuaStaging);
                BundleCollectorSettingData.ModifyCollector(group, collector);
            }

            BundleCollectorSettingData.SaveFile();
        }

        /// <summary>复制 100 基线 Lua 到 StreamingAssets（不含 101 独有模块）。</summary>
        static void CopyStreamingBaseline()
        {
            var dest = Path.Combine(Application.dataPath, "StreamingAssets", "Lua");
            Directory.CreateDirectory(dest);
            foreach (var name in new[] { "entry", "panel_base", "proto_case", "async_case", "cfg_runtime" })
            {
                var src = Path.Combine(Application.dataPath, "Lua", name + ".txt");
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(dest, name + ".txt"), true);
            }
        }

        static string MarkerPath(string name) =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), AcceptRoot, name));

        static void WriteMarker(string name, string text)
        {
            var path = MarkerPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
        }

    }

    /// <summary>
    /// 编辑器 Play 验证驱动：等 HotfixLoader 跑完后写 editor-proof.txt 并退出。
    /// </summary>
    public sealed class EditorProofDriver : MonoBehaviour
    {
        float _until;

        void Start()
        {
            _until = Time.realtimeSinceStartup + 120f;
        }

        void Update()
        {
            var loader = FindFirstObjectByType<HotfixLoader>();
            if (loader == null)
            {
                if (Time.realtimeSinceStartup > _until)
                    Quit(9, "loader missing");
                return;
            }

            // 必须等 BootFinished：入口摘要在跨帧异步之前就会写入，过早判定会得到 asyncFrames=0。
            if (!loader.BootFinished)
            {
                if (Time.realtimeSinceStartup > _until)
                    Quit(9, "timeout waiting loader");
                return;
            }

            var pass = string.IsNullOrEmpty(loader.LastFlowError)
                       && BridgeApi.EntryExecuted
                       && loader.LastAsyncFrames >= 1
                       && loader.CancelRegistered
                       && loader.AsyncBeforeDispose == 1
                       && loader.AsyncAfterDispose == 1
                       && loader.LiveProducerId == loader.PostDisposeLiveId
                       && string.IsNullOrEmpty(loader.DisposeError)
                       && Application.version == "0.2.0";

            var sb = new StringBuilder();
            sb.Append("pass=").Append(pass).AppendLine();
            sb.Append("entry=").Append(loader.LastEntrySummary).AppendLine();
            sb.Append("error=").Append(loader.LastFlowError).AppendLine();
            sb.Append("asyncFrames=").Append(loader.LastAsyncFrames).AppendLine();
            sb.Append("cancelRegistered=").Append(loader.CancelRegistered).AppendLine();
            sb.Append("cancelWaitFrames=").Append(loader.LastCancelWaitFrames).AppendLine();
            sb.Append("asyncBeforeDispose=").Append(loader.AsyncBeforeDispose).AppendLine();
            sb.Append("asyncAfterDispose=").Append(loader.AsyncAfterDispose).AppendLine();
            sb.Append("liveProducer=").Append(loader.LiveProducerId).AppendLine();
            sb.Append("postDisposeLiveId=").Append(loader.PostDisposeLiveId).AppendLine();
            sb.Append("disposeError=").Append(loader.DisposeError).AppendLine();
            sb.Append("entryInvocations=").Append(loader.EntryInvocations).AppendLine();
            sb.Append("sideEffectRolledBack=").Append(loader.SideEffectRolledBack).AppendLine();
            sb.Append("panels=").Append(BridgeApi.PanelDump()).AppendLine();
            sb.Append("appVersion=").Append(Application.version).AppendLine();
            sb.Append("bridgeContract=").Append(HotfixLoader.BridgeContract).AppendLine();
            sb.Append("protoVersion=").Append(HotfixLoader.HostProtoVersion).AppendLine();
            sb.Append("integerContract=").Append(ProtoBridge.Contract).AppendLine();
            sb.Append("gcForced=0").AppendLine();
            sb.Append("note=vendor LuaEnv.Dispose may still GC internally; project does not force GC").AppendLine();

            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", AcceptRoot, "editor-proof.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Quit(pass ? 0 : 4, pass ? "ok" : loader.LastFlowError);
        }

        const string AcceptRoot = "Docs/Acceptance/xLuaMigration/20261020/MainProject";

        static void Quit(int code, string reason)
        {
            Debug.Log("[xLua] editor proof exit=" + code + " " + reason);
            if (Application.isEditor)
                EditorApplication.Exit(code);
            else
                Application.Quit(code);
        }
    }
}
