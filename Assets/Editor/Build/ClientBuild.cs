using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CSObjectWrapEditor;
using Game.Framework.Boot;
using Game.Framework.Lua;
using Game.Framework.Resource;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;
using YooAsset.Editor;

namespace Game.Editor.Build
{
    /// <summary>
    /// T58：正式 Win64 IL2CPP 基包。-executeMethod Game.Editor.Build.ClientBuild.BuildWindows
    /// CLI：--m1-output= --m1-app-version= --m1-res-version= --m1-phase=all|gen|yoo|player|boot
    /// </summary>
    public static class ClientBuild
    {
        const string OutputArg = "--m1-output=";
        const string AppVerArg = "--m1-app-version=";
        const string ResVerArg = "--m1-res-version=";
        const string PhaseArg = "--m1-phase=";
        const string BootScenePath = "Assets/Scenes/M1/M1_Boot.unity";

        /// <summary>批处理入口：按 phase 执行并 Exit 非 0 表示失败。</summary>
        public static void BuildWindows()
        {
            try
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string output = Arg(OutputArg, Path.Combine(projectRoot, "Docs/Acceptance/M1/20261020-xLua/StageD/Freeze100"));
                string appVer = Arg(AppVerArg, "0.3.0");
                int resVer = ArgInt(ResVerArg, 100);
                string phase = Arg(PhaseArg, "all").ToLowerInvariant();
                Directory.CreateDirectory(output);
                var report = new StringBuilder();
                report.AppendLine("phase=" + phase);
                report.AppendLine("output=" + output);
                report.AppendLine("app=" + appVer);
                report.AppendLine("res=" + resVer);
                report.AppendLine("when=" + DateTime.UtcNow.ToString("o"));

                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    Fail(output, report, "editor-busy", 3);
                if (EditorUtility.scriptCompilationFailed)
                    Fail(output, report, "scriptCompilationFailed", 4);

                PlayerSettings.bundleVersion = appVer;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);

                if (phase == "boot" || phase == "all")
                {
                    EnsureBootScene();
                    report.AppendLine("bootScene=" + BootScenePath);
                }

                if (phase == "gen" || phase == "all")
                {
                    StageFormalLua(resVer, appVer, report);
                    Generator.ClearAll();
                    Generator.GenAll();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    report.AppendLine("genAll=ok");
                    File.WriteAllText(Path.Combine(output, "clientbuild-report.txt"), report.ToString(), new UTF8Encoding(false));
                    if (phase == "gen")
                    {
                        EditorApplication.Exit(0);
                        return;
                    }
                }

                if (phase == "yoo" || phase == "all")
                {
                    string publish = Path.Combine(output, "Publish", resVer.ToString());
                    Directory.CreateDirectory(publish);
                    if (!BuildYooPackage(ResMgr.PackageName, resVer.ToString(), publish, report))
                        Fail(output, report, "yoo-default-failed", 5);
                    if (!BuildYooPackage("GameLua", resVer.ToString(), publish, report))
                        Fail(output, report, "yoo-lua-failed", 6);
                    WriteVersionJson(Path.Combine(output, "Publish"), resVer, appVer, publish);
                    ValidatePackageAddresses(publish, resVer, report);
                }

                if (phase == "player" || phase == "all")
                {
                    EnsureBootSceneInBuildSettings();
                    string playerDir = Path.Combine(output, "Player");
                    Directory.CreateDirectory(playerDir);
                    string exe = Path.Combine(playerDir, "Farming.exe");
                    var options = new BuildPlayerOptions
                    {
                        scenes = new[] { BootScenePath },
                        locationPathName = exe,
                        target = BuildTarget.StandaloneWindows64,
                        targetGroup = BuildTargetGroup.Standalone,
                        options = BuildOptions.None
                    };
                    UnityEditor.Build.Reporting.BuildReport br = BuildPipeline.BuildPlayer(options);
                    report.AppendLine("playerResult=" + br.summary.result);
                    report.AppendLine("playerErrors=" + br.summary.totalErrors);
                    report.AppendLine("playerPath=" + exe);
                    File.WriteAllText(Path.Combine(output, "player-build-report.txt"), report.ToString(), new UTF8Encoding(false));
                    if (br.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                        Fail(output, report, "player-build-failed", 7);
                    WriteBaselineManifest(output, appVer, resVer, exe, report);
                }

                File.WriteAllText(Path.Combine(output, "clientbuild-report.txt"), report.ToString(), new UTF8Encoding(false));
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(9);
            }
        }

        /// <summary>仅创建/刷新 Boot 场景。</summary>
        public static void EnsureBootSceneBatch()
        {
            try
            {
                EnsureBootScene();
                EnsureBootSceneInBuildSettings();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(2);
            }
        }

        static void EnsureBootScene()
        {
            Directory.CreateDirectory("Assets/Scenes/M1");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("M1LauncherBoot");
            go.AddComponent<LauncherBootBehaviour>();
            EditorSceneManager.SaveScene(scene, BootScenePath);
            AssetDatabase.Refresh();
        }

        static void EnsureBootSceneInBuildSettings()
        {
            var list = new List<EditorBuildSettingsScene>();
            list.Add(new EditorBuildSettingsScene(BootScenePath, true));
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (string.Equals(s.path, BootScenePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new EditorBuildSettingsScene(s.path, false));
            }

            EditorBuildSettings.scenes = list.ToArray();
        }

        /// <summary>同步正式 Lua 到 Staging 并写 catalog（扁平地址 + 逻辑模块名）。</summary>
        public static void StageFormalLua(int resVersion, string appVersion, StringBuilder report)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string luaRoot = Path.Combine(Application.dataPath, "Lua");
            string staging = Path.Combine(luaRoot, "Staging");
            // 清空 Staging 文本模块（保留 .meta 由 Unity 处理）
            if (Directory.Exists(staging))
            {
                foreach (string f in Directory.GetFiles(staging, "*.txt"))
                    File.Delete(f);
            }
            else
                Directory.CreateDirectory(staging);

            // name=require 键；file=Staging 扁平文件名（无 .txt）
            var map = new (string name, string srcRel, string file)[]
            {
                ("entry", "entry.txt", "entry"),
                ("AccountService", "AccountService.txt", "AccountService"),
                ("PlayerModel", "PlayerModel.txt", "PlayerModel"),
                ("Json", "Json.txt", "Json"),
                ("panel_base", "panel_base.txt", "panel_base"),
                ("UI/UIManager", "UI/UIManager.txt", "UIManager"),
                ("UI/UIWindow", "UI/UIWindow.txt", "UIWindow"),
                ("UI/DialogService", "UI/DialogService.txt", "DialogService"),
                ("UI/ToastService", "UI/ToastService.txt", "ToastService"),
                ("UI/Windows/LoginWindow", "UI/Windows/LoginWindow.txt", "LoginWindow"),
                ("UI/Windows/HomeHudWindow", "UI/Windows/HomeHudWindow.txt", "HomeHudWindow"),
            };

            // cfg_runtime 仅为 JSON 配置地址，不得进 modules（CompileOnly 会当 Lua 解析失败）。
            string cfgSrc = Path.Combine(luaRoot, "cfg_runtime.txt");
            if (!File.Exists(cfgSrc))
                throw new FileNotFoundException("cfg_runtime missing", cfgSrc);
            File.Copy(cfgSrc, Path.Combine(staging, "cfg_runtime.txt"), true);

            var modules = new List<string>();
            var catalogMods = new StringBuilder();
            catalogMods.AppendLine("  \"modules\": [");
            for (int i = 0; i < map.Length; i++)
            {
                var m = map[i];
                string src = Path.Combine(luaRoot, m.srcRel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src))
                    throw new FileNotFoundException("formal lua missing", src);
                string dst = Path.Combine(staging, m.file + ".txt");
                File.Copy(src, dst, true);
                string sha = Sha256File(dst);
                modules.Add(m.name + "=" + m.file + ":" + sha);
                if (i > 0)
                    catalogMods.AppendLine(",");
                catalogMods.Append("    {\"name\":\"").Append(m.name).Append("\",\"address\":\"")
                    .Append(m.file).Append("\",\"sha256\":\"").Append(sha).Append("\"}");
            }

            catalogMods.AppendLine();
            catalogMods.AppendLine("  ]");

            string cfgSha = Sha256File(Path.Combine(staging, "cfg_runtime.txt"));
            var catalog = new StringBuilder();
            catalog.AppendLine("{");
            catalog.AppendLine("  \"resVersion\": \"" + resVersion + "\",");
            catalog.AppendLine("  \"entryModule\": \"entry\",");
            catalog.AppendLine("  \"configAddress\": \"cfg_runtime\",");
            catalog.AppendLine("  \"configSha256\": \"" + cfgSha + "\",");
            catalog.AppendLine("  \"bridgeContract\": \"" + BridgeApi.BridgeContractValue + "\",");
            catalog.AppendLine("  \"requiredAppVersion\": \"" + appVersion + "\",");
            catalog.AppendLine("  \"protoVersion\": " + BridgeApi.RequiredProtoVersion + ",");
            catalog.Append(catalogMods);
            catalog.AppendLine("}");
            File.WriteAllText(Path.Combine(staging, "catalog.txt"), catalog.ToString(), new UTF8Encoding(false));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            report?.AppendLine("luaModules=" + modules.Count);
            report?.AppendLine("luaCatalog=Staging/catalog.txt");
            // 禁止 100 含补丁演示模块
            if (File.Exists(Path.Combine(staging, "m1_patch_message.txt")))
                File.Delete(Path.Combine(staging, "m1_patch_message.txt"));
            if (File.Exists(Path.Combine(staging, "PatchDemoWindow.txt")))
                File.Delete(Path.Combine(staging, "PatchDemoWindow.txt"));
        }

        /// <summary>101：在已 Stage 的正式 Lua 上追加 PatchDemo 模块并重写 catalog。</summary>
        public static void AppendPatchDemoToStaging(int resVersion, string appVersion, StringBuilder report)
        {
            string luaRoot = Path.Combine(Application.dataPath, "Lua");
            string staging = Path.Combine(luaRoot, "Staging");
            string src = Path.Combine(luaRoot, "UI", "Windows", "PatchDemoWindow.txt");
            if (!File.Exists(src))
                throw new FileNotFoundException("PatchDemoWindow missing", src);
            File.Copy(src, Path.Combine(staging, "PatchDemoWindow.txt"), true);
            // 重跑 Stage 逻辑含 PatchDemo：读现有 catalog 追加
            string catalogPath = Path.Combine(staging, "catalog.txt");
            string sha = Sha256File(Path.Combine(staging, "PatchDemoWindow.txt"));
            // StageFormalLua uses AppendLine (CRLF on Windows). Normalize before
            // the LF-delimited insertion so the 101 module is actually indexed.
            string catalog = File.ReadAllText(catalogPath).Replace("\r\n", "\n");
            if (!catalog.Contains("\"name\":\"UI/Windows/PatchDemoWindow\""))
            {
                // 在 modules 数组最后一项后插入（兼容 CRLF→LF 后的 ]\n}）
                int modsEnd = catalog.LastIndexOf("\n  ]\n}", StringComparison.Ordinal);
                if (modsEnd < 0)
                    modsEnd = catalog.LastIndexOf("\n  ]}", StringComparison.Ordinal);
                if (modsEnd < 0)
                    throw new InvalidOperationException("catalog-modules-end-not-found");
                string insert =
                    ",\n    {\"name\":\"UI/Windows/PatchDemoWindow\",\"address\":\"PatchDemoWindow\",\"sha256\":\""
                    + sha + "\"}";
                catalog = catalog.Substring(0, modsEnd) + insert + catalog.Substring(modsEnd);
                catalog = System.Text.RegularExpressions.Regex.Replace(
                    catalog, "\"resVersion\":\\s*\"\\d+\"", "\"resVersion\": \"" + resVersion + "\"");
                File.WriteAllText(catalogPath, catalog, new UTF8Encoding(false));
            }

            EnsurePatchDemoPrefab();
            File.Copy(Path.Combine(luaRoot, "entry.txt"), Path.Combine(staging, "entry.txt"), true);
            string entrySha = Sha256File(Path.Combine(staging, "entry.txt"));
            catalog = File.ReadAllText(catalogPath).Replace("\r\n", "\n");
            catalog = System.Text.RegularExpressions.Regex.Replace(
                catalog,
                "\"name\":\"entry\",\"address\":\"entry\",\"sha256\":\"[a-f0-9]+\"",
                "\"name\":\"entry\",\"address\":\"entry\",\"sha256\":\"" + entrySha + "\"");
            File.WriteAllText(catalogPath, catalog, new UTF8Encoding(false));
            catalog = File.ReadAllText(catalogPath);
            if (!catalog.Contains("\"name\":\"UI/Windows/PatchDemoWindow\""))
                throw new InvalidOperationException("patchdemo-not-in-catalog-modules");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            report?.AppendLine("patchDemo=ok sha=" + sha);
        }

        static void EnsurePatchDemoPrefab()
        {
            const string path = "Assets/Res/UI_Patch101/PatchDemoWindow.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                return;
            Directory.CreateDirectory("Assets/Res/UI_Patch101");
            var root = new GameObject("PatchDemoWindow", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var rt = (RectTransform)root.transform;
            rt.sizeDelta = new Vector2(640, 280);
            root.GetComponent<UnityEngine.UI.Image>().color = new Color(0.08f, 0.18f, 0.14f, 0.96f);
            CreateUiText(root.transform, "Title", "热更新验证成功", new Vector2(0.05f, 0.55f), new Vector2(0.95f, 0.92f), 32);
            CreateUiText(root.transform, "Message", "热更新验证成功", new Vector2(0.08f, 0.15f), new Vector2(0.92f, 0.55f), 26);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void CreateUiText(Transform parent, string name, string value, Vector2 amin, Vector2 amax, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<UnityEngine.UI.Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                        ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = value;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            var tr = (RectTransform)go.transform;
            tr.anchorMin = amin;
            tr.anchorMax = amax;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
        }

        static bool BuildYooPackage(string packageName, string version, string publishDir, StringBuilder report)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var uniqueBundleName = BundleCollectorSettingData.Setting.UniqueBundleName;
            var packRuleResult = DefaultBundlePackRule.CreateShadersPackRuleResult();
            string builtinShaderBundleName = packRuleResult.GetBundleName(packageName, uniqueBundleName);

            var buildParameters = new ScriptableBuildParameters
            {
                BuildOutputRoot = Path.Combine(publishDir, "_yoo_build"),
                BundledFileRoot = Path.Combine(publishDir, "_yoo_builtin"),
                BuildPipeline = EBuildPipeline.ScriptableBuildPipeline.ToString(),
                BuildBundleType = (int)EBundleType.AssetBundle,
                BuildTarget = BuildTarget.StandaloneWindows64,
                PackageName = packageName,
                PackageVersion = version,
                EnableSharePackRule = packageName == ResMgr.PackageName,
                VerifyBuildingResult = true,
                FileNameStyle = EFileNameStyle.HashName,
                BundledCopyOption = EBundledCopyOption.None,
                CompressOption = ECompressOption.LZ4,
                ClearBuildCacheFiles = true,
                UseAssetDependencyDB = true,
                BuiltinShadersBundleName = builtinShaderBundleName
            };

            var pipeline = new ScriptableBuildPipeline();
            var result = pipeline.Run(buildParameters, true);
            report.AppendLine("yoo." + packageName + ".success=" + result.Success);
            report.AppendLine("yoo." + packageName + ".out=" + result.OutputPackageDirectory);
            if (!result.Success)
            {
                report.AppendLine("yoo." + packageName + ".err=" + result.ErrorInfo);
                return false;
            }

            string dest = Path.Combine(publishDir, packageName);
            if (Directory.Exists(dest))
                Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(result.OutputPackageDirectory))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            // HostHttp 扁平：同时把包文件拷到版本根（version.json BaseUrl/.../）
            foreach (string file in Directory.GetFiles(result.OutputPackageDirectory))
            {
                string name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(publishDir, name), true);
            }

            return true;
        }

        static void WriteVersionJson(string publishRoot, int resVer, string appVer, string versionDir)
        {
            Directory.CreateDirectory(publishRoot);
            // BaseUrl 由 run_server / 验收时按实际 HTTP 端口覆写；占位 localhost。
            string json =
                "{\n"
                + "  \"MinAppVersion\": \"" + appVer + "\",\n"
                + "  \"ResVersion\": " + resVer + ",\n"
                + "  \"ProtoVersion\": " + BridgeApi.RequiredProtoVersion + ",\n"
                + "  \"ConfigVersion\": " + resVer + ",\n"
                + "  \"PackageVersion\": \"" + resVer + "\",\n"
                + "  \"BaseUrl\": \"http://localhost:18080/" + resVer + "/\"\n"
                + "}\n";
            File.WriteAllText(Path.Combine(versionDir, "version.json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(publishRoot, "version.json"), json, new UTF8Encoding(false));
        }

        static void ValidatePackageAddresses(string publishDir, int resVer, StringBuilder report)
        {
            string[] mustExist =
            {
                Path.Combine(Application.dataPath, "Res/UI/LoginWindow.prefab"),
                Path.Combine(Application.dataPath, "Res/UI/HomeHudWindow.prefab"),
                Path.Combine(Application.dataPath, "Scenes/M1/M1_Login.unity"),
                Path.Combine(Application.dataPath, "Scenes/M1/M1_Home.unity"),
                Path.Combine(Application.dataPath, "Lua/Staging/catalog.txt"),
                Path.Combine(Application.dataPath, "Lua/Staging/entry.txt"),
            };
            foreach (string p in mustExist)
            {
                if (!File.Exists(p))
                    throw new FileNotFoundException("address-validate-missing", p);
            }

            string catalog = File.ReadAllText(Path.Combine(Application.dataPath, "Lua/Staging/catalog.txt"));
            if (catalog.Contains("m1_patch_message"))
                throw new InvalidOperationException("100-catalog-must-not-contain-m1_patch_message");
            if (catalog.Contains("PatchDemoWindow") || catalog.Contains("UI/Windows/PatchDemoWindow"))
                throw new InvalidOperationException("100-catalog-must-not-contain-PatchDemo");
            if (!catalog.Contains("\"resVersion\": \"" + resVer + "\""))
                throw new InvalidOperationException("catalog-resVersion-mismatch");
            if (File.Exists(Path.Combine(Application.dataPath, "Res/UI/PatchDemoWindow.prefab")))
                throw new InvalidOperationException("PatchDemo-still-under-Assets/Res/UI");
            string flat = Directory.Exists(publishDir)
                ? string.Join("\n", Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories))
                : "";
            if (flat.IndexOf("PatchDemoWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("100-publish-contains-PatchDemoWindow");
            report.AppendLine("addressValidate=ok");
        }

        static void WriteBaselineManifest(string output, string appVer, int resVer, string exePath, StringBuilder report)
        {
            string ga = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "GameAssembly.dll");
            string meta = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "Farming_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"appVersion\": \"" + appVer + "\",");
            sb.AppendLine("  \"resVersion\": " + resVer + ",");
            sb.AppendLine("  \"protoVersion\": " + BridgeApi.RequiredProtoVersion + ",");
            sb.AppendLine("  \"bridgeContract\": \"" + BridgeApi.BridgeContractValue + "\",");
            sb.AppendLine("  \"exeSha256\": \"" + Sha256File(exePath) + "\",");
            sb.AppendLine("  \"gameAssemblySha256\": \"" + (File.Exists(ga) ? Sha256File(ga) : "") + "\",");
            sb.AppendLine("  \"globalMetadataSha256\": \"" + (File.Exists(meta) ? Sha256File(meta) : "") + "\",");
            sb.AppendLine("  \"bridgeApiCs\": \"" + Sha256File(Path.Combine(Application.dataPath, "Framework/Lua/BridgeApi.cs")) + "\",");
            sb.AppendLine("  \"luaGenConfigCs\": \"" + Sha256File(Path.Combine(Application.dataPath, "Framework/Lua/LuaGenConfig.cs")) + "\",");
            sb.AppendLine("  \"accountBridgeCs\": \"" + Sha256File(Path.Combine(Application.dataPath, "Framework/Account/AccountBridge.cs")) + "\",");
            sb.AppendLine("  \"accountSessionCs\": \"" + Sha256File(Path.Combine(Application.dataPath, "Framework/Account/AccountSession.cs")) + "\",");
            sb.AppendLine("  \"accountProto\": \"" + Sha256File(Path.Combine(Application.dataPath, "..", "Shared", "Proto", "account.proto")) + "\",");
            sb.AppendLine("  \"linkXml\": \"" + Sha256File(Path.Combine(Application.dataPath, "link.xml")) + "\",");
            sb.AppendLine("  \"catalogSha256\": \"" + Sha256File(Path.Combine(Application.dataPath, "Lua/Staging/catalog.txt")) + "\",");
            sb.AppendLine("  \"unityVersion\": \"" + Application.unityVersion + "\",");
            sb.AppendLine("  \"scriptingBackend\": \"IL2CPP\",");
            sb.AppendLine("  \"when\": \"" + DateTime.UtcNow.ToString("o") + "\"");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(output, "baseline-manifest.json"), sb.ToString(), new UTF8Encoding(false));
            WriteBaselineFingerprint(output, report);
            report.AppendLine("baseline=ok");
        }

        /// <summary>委托公共收集器（CompilationPipeline Player 源 + 插件 + lock + schema）。</summary>
        public static void WriteBaselineFingerprint(string output, StringBuilder report)
        {
            BaselineFingerprint.Write(output, report);
        }

        static void Fail(string output, StringBuilder report, string reason, int code)
        {
            report.AppendLine("FAIL=" + reason);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "clientbuild-report.txt"), report.ToString(), new UTF8Encoding(false));
            Debug.LogError("[ClientBuild] " + reason);
            EditorApplication.Exit(code);
            throw new InvalidOperationException("ClientBuild.Fail:" + reason);
        }

        static string Arg(string prefix, string fallback)
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string v = a.Substring(prefix.Length).Trim().Trim('"');
                if (!string.IsNullOrEmpty(v))
                    return Path.IsPathRooted(v) || prefix != OutputArg
                        ? v
                        : Path.GetFullPath(Path.Combine(Path.Combine(Application.dataPath, ".."), v));
            }

            return fallback;
        }

        static int ArgInt(string prefix, int fallback)
        {
            string v = Arg(prefix, null);
            return int.TryParse(v, out int n) ? n : fallback;
        }

        static string Sha256File(string path)
        {
            if (!File.Exists(path))
                return "";
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
