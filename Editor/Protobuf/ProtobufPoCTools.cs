using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Game.Editor.ProtobufTools
{
    /// <summary>
    /// PoC #4：重新生成 proto、确保场景、触发热更 DLL 拷贝。
    /// </summary>
    public static class ProtobufPoCTools
    {
        const string ProtoFile = "Shared/proto/poc.proto";
        const string CSharpOut = "Assets/Shared/Proto";
        const string ScenePath = "Assets/Scenes/HybridCLR_PoC.unity";

        [MenuItem("Farming/Protobuf PoC/0. Regenerate Proto C#")]
        public static void RegenerateProto()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string protoPath = Path.Combine(projectRoot, ProtoFile);
            if (!File.Exists(protoPath))
            {
                Debug.LogError($"[Protobuf PoC] Missing {protoPath}");
                return;
            }

            Directory.CreateDirectory(Path.Combine(projectRoot, CSharpOut));

            var psi = new ProcessStartInfo
            {
                FileName = "protoc",
                Arguments = $"--csharp_out=\"{CSharpOut}\" --proto_path=\"Shared/proto\" \"{ProtoFile.Replace('\\', '/')}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Debug.LogError($"[Protobuf PoC] protoc failed ({p.ExitCode}): {stderr}\n{stdout}");
                    return;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Protobuf PoC] Regenerated C# from {ProtoFile} → {CSharpOut}");
        }

        [MenuItem("Farming/Protobuf PoC/1. Ensure HybridCLR PoC Scene Has Loader")]
        public static void EnsureScene()
        {
            var scene = File.Exists(Path.Combine(Application.dataPath, "Scenes/HybridCLR_PoC.unity"))
                ? EditorSceneManager.OpenScene(ScenePath)
                : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var loader = Object.FindFirstObjectByType<Game.Framework.Boot.HotUpdateLoader>();
            if (loader == null)
            {
                var go = new GameObject("HotUpdateLoader");
                go.AddComponent<Game.Framework.Boot.HotUpdateLoader>();
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[Protobuf PoC] Scene ready: {ScenePath}");
        }

        [MenuItem("Farming/Protobuf PoC/2. Compile HotUpdate DLL + Copy StreamingAssets")]
        public static void CompileAndCopy()
        {
            bool ok = EditorApplication.ExecuteMenuItem("Farming/HybridCLR PoC/5. CompileDll + Copy (patch workflow)");
            if (!ok)
                ok = EditorApplication.ExecuteMenuItem("Farming/HybridCLR PoC/5. CompileDll + Copy");
            Debug.Log(ok
                ? "[Protobuf PoC] CompileDll + Copy requested"
                : "[Protobuf PoC] HybridCLR compile menu missing — open HybridCLR PoC tools first");
        }

        [MenuItem("Farming/Protobuf PoC/3. Run Protobuf Test In Editor (no Play)")]
        public static void RunTestInEditor()
        {
            var asm = System.AppDomain.CurrentDomain.GetAssemblies();
            System.Type entry = null;
            foreach (var a in asm)
            {
                if (a.GetName().Name != "Game.HotUpdate")
                    continue;
                entry = a.GetType("Game.HotUpdate.Entry");
                break;
            }

            if (entry == null)
            {
                Debug.LogError("[Protobuf PoC] Game.HotUpdate.Entry not found — wait for compile");
                return;
            }

            var method = entry.GetMethod("RunProtobufPoC", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            string result = method.Invoke(null, null) as string;
            Debug.Log($"[Protobuf PoC] Editor invoke => {result}");
        }
    }
}
