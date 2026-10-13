using System.IO;
using HybridCLR;
using HybridCLR.Commands;
using HybridCLR.Installer;
using HybridCLR.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Editor.HybridCLRTools
{
    /// <summary>
    /// PoC #2 辅助菜单：配置、本地安装、Generate、拷贝 DLL。
    /// </summary>
    public static class HybridCLRPoCTools
    {
        const string HotfixAssemblyName = "Game.Hotfix";
        const string StreamingDllName = "Game.Hotfix.dll.bytes";
        const string LocalLibil2cppRelative = "HybridCLRData/Repos/il2cpp_plus/libil2cpp";

        [MenuItem("Farming/HybridCLR PoC/0. Run Full PoC Setup")]
        public static void RunFullSetup()
        {
            Configure();
            InstallFromLocalPrepared();
            GenerateAll();
            CompileAndCopy();
            Debug.Log("[PoC] Full setup finished. Next: Build Windows IL2CPP player and verify patch replace.");
        }

        [MenuItem("Farming/HybridCLR PoC/1. Configure Player + HotUpdate Assemblies")]
        public static void Configure()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);

            var settings = HybridCLRSettings.instance;
            settings.enable = true;
            settings.hotUpdateAssemblies = new[] { HotfixAssemblyName };
            settings.hotUpdateAssemblyDefinitions = System.Array.Empty<AssemblyDefinitionAsset>();
            HybridCLRSettings.Save();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            Debug.Log($"[PoC] IL2CPP + hotUpdateAssemblies=[{HotfixAssemblyName}]");
        }

        [MenuItem("Farming/HybridCLR PoC/2. Install From Local (skip git proxy)")]
        public static void InstallFromLocalPrepared()
        {
            string localLibil2cpp = Path.GetFullPath(Path.Combine(Application.dataPath, "..", LocalLibil2cppRelative));
            if (!Directory.Exists(Path.Combine(localLibil2cpp, "hybridclr")))
            {
                Debug.LogError($"[PoC] Prepared libil2cpp missing hybridclr folder:\n{localLibil2cpp}");
                return;
            }

            var controller = new InstallerController();
            if (controller.HasInstalledHybridCLR())
            {
                Debug.Log("[PoC] HybridCLR already installed.");
                return;
            }

            controller.InstallFromLocal(localLibil2cpp);
            Debug.Log(controller.HasInstalledHybridCLR()
                ? "[PoC] InstallFromLocal SUCCESS"
                : "[PoC] InstallFromLocal finished but HasInstalledHybridCLR=false — check Console");
        }

        [MenuItem("Farming/HybridCLR PoC/3. Generate All")]
        public static void GenerateAll()
        {
            PrebuildCommand.GenerateAll();
            Debug.Log("[PoC] PrebuildCommand.GenerateAll done.");
        }

        [MenuItem("Farming/HybridCLR PoC/4. Copy Hotfix DLL to StreamingAssets")]
        public static void CopyHotfixDll()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string src = Path.Combine(SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target), $"{HotfixAssemblyName}.dll");

            if (!File.Exists(src))
            {
                CompileDllCommand.CompileDll(target);
                if (!File.Exists(src))
                {
                    Debug.LogError($"[PoC] DLL not found: {src}");
                    return;
                }
            }

            string dstDir = Path.Combine(Application.dataPath, "StreamingAssets");
            Directory.CreateDirectory(dstDir);
            string dst = Path.Combine(dstDir, StreamingDllName);
            File.Copy(src, dst, true);
            AssetDatabase.Refresh();
            Debug.Log($"[PoC] Copied\n  {src}\n→ {dst}");
        }

        [MenuItem("Farming/HybridCLR PoC/5. CompileDll + Copy (patch workflow)")]
        public static void CompileAndCopy()
        {
            CompileDllCommand.CompileDllActiveBuildTarget();
            CopyHotfixDll();
            Debug.Log("[PoC] Patch ready. Replace player's StreamingAssets/Game.Hotfix.dll.bytes without rebuilding.");
        }
    }
}
