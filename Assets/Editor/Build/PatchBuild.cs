using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Framework.Boot;
using Game.Framework.Lua;
using Game.Framework.Resource;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace Game.Editor.Build
{
    /// <summary>
    /// T59：仅资源补丁。结构化完整指纹不一致则拒绝。
    /// 已发布版本目录不可变；仅完整 staging 成功后原子切指针。
    /// -executeMethod Game.Editor.Build.PatchBuild.BuildPatch
    /// --m1-output= --m1-res-version=101 --m1-config-version=101 --m1-base-manifest=
    /// </summary>
    public static class PatchBuild
    {
        const string OutputArg = "--m1-output=";
        const string ResVerArg = "--m1-res-version=";
        const string CfgVerArg = "--m1-config-version=";
        const string BaseArg = "--m1-base-manifest=";
        const string AppVerArg = "--m1-app-version=";
        const string LuaPackageName = "GameLua";

        public static void BuildPatch()
        {
            try
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string output = Arg(OutputArg, Path.Combine(projectRoot, "Docs/Acceptance/M1/20261020-xLua/StageD/Publish"));
                int resVer = ArgInt(ResVerArg, 101);
                int cfgVer = ArgInt(CfgVerArg, resVer);
                string appVer = Arg(AppVerArg, "0.3.0");
                string baseManifest = Arg(BaseArg, "");
                var report = new StringBuilder();
                report.AppendLine("res=" + resVer + " config=" + cfgVer);
                report.AppendLine("output=" + output);
                report.AppendLine("base=" + baseManifest);

                output = Path.GetFullPath(output);
                if (string.IsNullOrEmpty(baseManifest) || !File.Exists(baseManifest))
                    Fail(output, report, "missing-base-manifest", 2);

                baseManifest = Path.GetFullPath(baseManifest);
                string baseDir = Path.GetDirectoryName(baseManifest) ?? "";
                string playerDir = Path.GetFullPath(Path.Combine(baseDir, "Player"));
                // 任何写盘前：Output 不得与 baseline Player 相交。
                GuardPlayerUntouched(output, playerDir, report);
                Directory.CreateDirectory(output);
                AssertOwnedPath(output, output, "output-root");

                string err = ValidateBaseline(baseManifest);
                if (!string.IsNullOrEmpty(err))
                    Fail(output, report, "aot-incompatible:" + err, 3);

                string versionDir = Path.Combine(output, resVer.ToString());
                AssertOwnedPath(output, versionDir, "versionDir");
                string stagingDir = Path.Combine(output, "_staging_" + resVer + "_" + Guid.NewGuid().ToString("N"));
                AssertOwnedPath(output, stagingDir, "stagingDir");
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);

                // 输入兼容：配置/Res 须存在；101 须有 PatchDemo 源与 Config 版本化输入提示。
                if (!Directory.Exists(Path.Combine(Application.dataPath, "ConfigLuban")))
                    Fail(output, report, "missing-ConfigLuban", 11);
                if (resVer >= 101)
                {
                    string demoLua = Path.Combine(Application.dataPath, "Lua/UI/Windows/PatchDemoWindow.txt");
                    if (!File.Exists(demoLua))
                        Fail(output, report, "missing-PatchDemoWindow-lua", 12);
                }

                ClientBuild.StageFormalLua(resVer, appVer, report);
                if (resVer >= 101)
                {
                    ClientBuild.AppendPatchDemoToStaging(resVer, appVer, report);
                    string catPath = Path.Combine(Application.dataPath, "Lua/Staging/catalog.txt");
                    string cat = File.ReadAllText(catPath);
                    if (!cat.Contains("\"name\":\"UI/Windows/PatchDemoWindow\""))
                        Fail(output, report, "101-catalog-missing-PatchDemo-module", 17);
                }

                // 101 Prefab：主副本在 UI_Patch101；构建期短暂放入 Res/UI 供 DefaultPackage 收集，结束后移除。
                string demoMaster = Path.Combine(Application.dataPath, "Res/UI_Patch101/PatchDemoWindow.prefab");
                string demoLive = Path.Combine(Application.dataPath, "Res/UI/PatchDemoWindow.prefab");
                bool stagedDemo = false;
                try
                {
                    if (resVer >= 101)
                    {
                        if (!File.Exists(demoMaster))
                            Fail(output, report, "missing-PatchDemo-prefab-master", 13);
                        File.Copy(demoMaster, demoLive, true);
                        string meta = demoMaster + ".meta";
                        if (File.Exists(meta))
                            File.Copy(meta, demoLive + ".meta", true);
                        stagedDemo = true;
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    }

                    if (!BuildYoo(ResMgr.PackageName, resVer.ToString(), stagingDir, report))
                        Fail(output, report, "yoo-default-failed", 5);
                    if (!BuildYoo(LuaPackageName, resVer.ToString(), stagingDir, report))
                        Fail(output, report, "yoo-lua-failed", 6);
                }
                finally
                {
                    if (stagedDemo)
                    {
                        try
                        {
                            if (File.Exists(demoLive))
                                File.Delete(demoLive);
                            if (File.Exists(demoLive + ".meta"))
                                File.Delete(demoLive + ".meta");
                            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        }
                        catch (Exception cleanupEx)
                        {
                            report.AppendLine("demo-cleanup-warn=" + cleanupEx.Message);
                        }
                    }
                }

                if (resVer >= 101)
                {
                    string flat = string.Join("\n", Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories));
                    if (flat.IndexOf("PatchDemoWindow", StringComparison.OrdinalIgnoreCase) < 0)
                        Fail(output, report, "101-missing-PatchDemoWindow-in-package", 14);
                }

                string json =
                    "{\n"
                    + "  \"MinAppVersion\": \"" + appVer + "\",\n"
                    + "  \"ResVersion\": " + resVer + ",\n"
                    + "  \"ProtoVersion\": " + BridgeApi.RequiredProtoVersion + ",\n"
                    + "  \"ConfigVersion\": " + cfgVer + ",\n"
                    + "  \"PackageVersion\": \"" + resVer + "\",\n"
                    + "  \"BaseUrl\": \"http://localhost:18080/" + resVer + "/\"\n"
                    + "}\n";
                File.WriteAllText(Path.Combine(stagingDir, "version.json"), json, new UTF8Encoding(false));

                // 已发布版本不可变：存在则仅允许内容完全一致的 no-op，否则拒绝。
                if (Directory.Exists(versionDir))
                {
                    if (DirectoryContentEqual(versionDir, stagingDir))
                    {
                        report.AppendLine("immutable-noop=true");
                        try { Directory.Delete(stagingDir, true); } catch { }
                        WritePointerAtomic(output, json, report);
                        report.AppendLine("ok=true");
                        File.WriteAllText(Path.Combine(output, "patchbuild-report.txt"), report.ToString(), new UTF8Encoding(false));
                        EditorApplication.Exit(0);
                        return;
                    }

                    try { Directory.Delete(stagingDir, true); } catch { }
                    Fail(output, report, "published-version-immutable:" + resVer, 15);
                }

                // 首次发布：Move staging → versionDir（目标不存在），再原子切指针。
                AssertOwnedPath(output, stagingDir, "staging-before-move");
                Directory.Move(stagingDir, versionDir);
                WritePointerAtomic(output, json, report);

                GuardPlayerUntouched(output, playerDir, report);
                report.AppendLine("ok=true");
                File.WriteAllText(Path.Combine(output, "patchbuild-report.txt"), report.ToString(), new UTF8Encoding(false));
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(9);
            }
        }

        /// <summary>仅校验基包兼容（可单独 -executeMethod 测失败码）。</summary>
        public static void ValidateBaselineOnly()
        {
            string baseManifest = Arg(BaseArg, "");
            string output = Arg(OutputArg, Path.GetTempPath());
            var report = new StringBuilder();
            if (string.IsNullOrEmpty(baseManifest) || !File.Exists(baseManifest))
                Fail(output, report, "missing-base-manifest", 2);
            string err = ValidateBaseline(baseManifest);
            if (!string.IsNullOrEmpty(err))
                Fail(output, report, "aot-incompatible:" + err, 3);
            report.AppendLine("validate=ok");
            File.WriteAllText(Path.Combine(output, "validate-baseline.txt"), report.ToString(), new UTF8Encoding(false));
            EditorApplication.Exit(0);
        }

        /// <summary>必须存在完整 baseline-fingerprint.json；与 Collect 同一收集器比较。无旧六文件 fallback。</summary>
        public static string ValidateBaseline(string baseBuildManifest)
        {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(baseBuildManifest)) ?? "";
            string fpPath = Path.Combine(baseDir, "baseline-fingerprint.json");
            if (!File.Exists(fpPath))
                return "fingerprint-required";
            string err = BaselineFingerprint.ValidateAgainstFile(fpPath);
            if (!string.IsNullOrEmpty(err))
                return err;
            // 轻量契约字段仍核对照基包 manifest
            string json = File.ReadAllText(baseBuildManifest);
            if (!json.Contains("\"bridgeContract\": \"" + BridgeApi.BridgeContractValue + "\""))
                return "bridgeContract-mismatch";
            if (!json.Contains("\"protoVersion\": " + BridgeApi.RequiredProtoVersion))
                return "proto-mismatch";
            return "";
        }

        static void WritePointerAtomic(string output, string json, StringBuilder report)
        {
            string active = Path.Combine(output, "version.json");
            string tmp = Path.Combine(output, "version.json." + Guid.NewGuid().ToString("N") + ".tmp");
            AssertOwnedPath(output, tmp, "pointer-tmp");
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            string bak = active + ".bak";
            if (File.Exists(active))
            {
                File.Replace(tmp, active, bak);
            }
            else
            {
                File.Move(tmp, active);
            }

            report.AppendLine("pointer=atomic");
        }

        static void GuardPlayerUntouched(string output, string playerDir, StringBuilder report)
        {
            string outFull = Path.GetFullPath(output).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string playerFull = Path.GetFullPath(playerDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (outFull.StartsWith(playerFull, StringComparison.OrdinalIgnoreCase)
                || playerFull.StartsWith(outFull, StringComparison.OrdinalIgnoreCase))
            {
                Fail(output, report, "output-intersects-player-dir", 16);
            }

            string exe = Path.Combine(playerDir, "Farming.exe");
            string ga = Path.Combine(playerDir, "GameAssembly.dll");
            string meta = Path.Combine(playerDir, "Farming_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            report.AppendLine("playerDirUntouched=" + playerDir);
            report.AppendLine("playerExeSha=" + (File.Exists(exe) ? Sha(exe) : "missing"));
            report.AppendLine("playerGaSha=" + (File.Exists(ga) ? Sha(ga) : "missing"));
            report.AppendLine("playerMetaSha=" + (File.Exists(meta) ? Sha(meta) : "missing"));
        }

        static void AssertOwnedPath(string outputRoot, string target, string tag)
        {
            string root = Path.GetFullPath(outputRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(target);
            string fullCmp = full.EndsWith("\\") || full.EndsWith("/")
                ? full
                : full + (Directory.Exists(full) || !File.Exists(full) ? Path.DirectorySeparatorChar.ToString() : "");
            // 允许文件或目录：前缀匹配
            string check = Directory.Exists(full) || !Path.HasExtension(full)
                ? full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar
                : Path.GetDirectoryName(full)?.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (check == null || !check.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("path-not-under-output:" + tag + ":" + full);
        }

        static bool DirectoryContentEqual(string a, string b)
        {
            var fa = ListFileHashes(a);
            var fb = ListFileHashes(b);
            if (fa.Count != fb.Count)
                return false;
            foreach (var kv in fa)
            {
                if (!fb.TryGetValue(kv.Key, out string hb) || !string.Equals(hb, kv.Value, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        static Dictionary<string, string> ListFileHashes(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string rootFull = Path.GetFullPath(root);
            foreach (string file in Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(rootFull.Length).TrimStart('\\', '/').Replace('\\', '/');
                map[rel] = Sha(file);
            }

            return map;
        }

        static bool BuildYoo(string packageName, string version, string publishDir, StringBuilder report)
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
            var result = new ScriptableBuildPipeline().Run(buildParameters, true);
            report.AppendLine("yoo." + packageName + "=" + result.Success);
            if (!result.Success)
                return false;
            foreach (string file in Directory.GetFiles(result.OutputPackageDirectory))
                File.Copy(file, Path.Combine(publishDir, Path.GetFileName(file)), true);
            return true;
        }

        static void Fail(string output, StringBuilder report, string reason, int code)
        {
            report.AppendLine("FAIL=" + reason);
            // 禁止把失败报告写进 baseline Player；相交则只打日志。
            try
            {
                string outFull = Path.GetFullPath(output).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                bool intersectsPlayer = outFull.IndexOf(
                    Path.DirectorySeparatorChar + "Player" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0
                    && outFull.IndexOf("Freeze", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!intersectsPlayer)
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "patchbuild-report.txt"), report.ToString(), new UTF8Encoding(false));
                }
                else
                {
                    Debug.LogError("[PatchBuild] refuse-write-into-player-dir report=" + reason);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[PatchBuild] fail-report-write " + ex.Message);
            }

            Debug.LogError("[PatchBuild] " + reason);
            EditorApplication.Exit(code);
            throw new InvalidOperationException("PatchBuild.Fail:" + reason);
        }

        static string Arg(string prefix, string fallback)
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string v = a.Substring(prefix.Length).Trim().Trim('"');
                if (!string.IsNullOrEmpty(v))
                    return v;
            }

            return fallback;
        }

        static int ArgInt(string prefix, int fallback)
        {
            string v = Arg(prefix, null);
            return int.TryParse(v, out int n) ? n : fallback;
        }

        static string Sha(string path)
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
