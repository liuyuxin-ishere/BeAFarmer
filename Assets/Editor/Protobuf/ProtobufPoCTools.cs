using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Framework.Lua;
using Debug = UnityEngine.Debug;

namespace Game.Editor.ProtobufTools
{
    /// <summary>
    /// 协议生成与往返验证。已去掉 HybridCLR 元数据拷贝与反射旧 Entry。
    /// </summary>
    public static class ProtobufPoCTools
    {
        const string ProtoFile = "Shared/proto/poc.proto";
        const string CSharpOut = "Assets/Shared/Proto";
        const string ScenePath = "Assets/Scenes/xLua_PoC.unity";

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
                Arguments = $"--csharp_out=\"{CSharpOut}\" --proto_path=\"Shared/proto\" poc.proto",
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

        [MenuItem("Farming/Protobuf PoC/1. Ensure xLua PoC Scene Has Loader")]
        public static void EnsureScene()
        {
            var scene = File.Exists(Path.Combine(Application.dataPath, "Scenes/xLua_PoC.unity"))
                ? EditorSceneManager.OpenScene(ScenePath)
                : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var loader = Object.FindFirstObjectByType<Game.Framework.Boot.HotfixLoader>();
            if (loader == null)
            {
                var go = new GameObject("HotfixBoot");
                go.AddComponent<Game.Framework.Boot.HotfixLoader>();
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[Protobuf PoC] Scene ready: {ScenePath}");
        }

        [MenuItem("Farming/Protobuf PoC/2. Run ProtoBridge Verify In Editor (no Play)")]
        public static void RunTestInEditor()
        {
            try
            {
                byte[] heartbeat = ProtoBridge.BuildHeartbeatBytes();
                long clientTime = ProtoBridge.ReadHeartbeatClientTime(heartbeat);
                long echoed = ProtoBridge.EchoInt64(clientTime);
                byte[] avatar = ProtoBridge.BuildAvatarBytes();
                string result = ProtoBridge.VerifyAll(
                    ProtoBridge.EchoBytes(heartbeat),
                    echoed,
                    ProtoBridge.EchoBytes(avatar));
                Debug.Log($"[Protobuf PoC] ProtoBridge.VerifyAll => {result}");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        [MenuItem("Farming/Protobuf PoC/3. Run Full Setup (Proto + Scene + Editor Test)")]
        public static void RunFullSetup()
        {
            RegenerateProto();
            EnsureScene();
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isCompiling)
                    RunTestInEditor();
            };
            Debug.Log("[Protobuf PoC] Full setup requested. If compiling, re-run menu 2 after idle.");
        }
    }
}
