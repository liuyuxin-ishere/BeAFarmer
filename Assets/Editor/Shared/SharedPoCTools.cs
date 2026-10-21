using System.Diagnostics;
using System.IO;
using CloudIsle.Proto.Poc;
using CloudIsle.Rules;
using Google.Protobuf;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Editor.SharedTools
{
    /// <summary>
    /// PoC #5：触发服务端对 Game.Shared 同源源码的编译与运行验证。
    /// </summary>
    public static class SharedPoCTools
    {
        [MenuItem("Farming/Shared PoC/1. Verify Server Build (dotnet)")]
        public static void VerifyServerBuild()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string script = Path.Combine(projectRoot, "Tools", "verify_shared.ps1");
            if (!File.Exists(script))
            {
                Debug.LogError($"[Shared PoC] Missing {script}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
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
                    Debug.LogError($"[Shared PoC] verify FAILED ({p.ExitCode})\n{stderr}\n{stdout}");
                    return;
                }

                Debug.Log($"[Shared PoC] verify OK\n{stdout}");
            }
        }

        [MenuItem("Farming/Shared PoC/2. Log Client Shared Types")]
        public static void LogClientSharedTypes()
        {
            // 编辑器域直接引用 Game.Shared（与服务端相同源码）
            long ready = TimeRules.CalcReadyTimeMs(1_700_000_000_000L, 60);
            var req = new LoginReq { Username = "shared_poc_client" };
            int bytes = req.ToByteArray().Length;
            Debug.Log($"[Shared PoC] Client OK TimeRules.readyMs={ready} LoginReqBytes={bytes} asm=Game.Shared");
        }
    }
}
