using System.Diagnostics;
using System.IO;
using CloudIsle.Config;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Editor.ConfigTools
{
    /// <summary>
    /// PoC #7：导表与双端读取验证。
    /// </summary>
    public static class ConfigPoCTools
    {
        [MenuItem("Farming/Config PoC/1. Generate From Excel (python)")]
        public static void GenerateFromExcel()
        {
            RunScript("gen_config.py", "generate");
        }

        [MenuItem("Farming/Config PoC/2. Verify Export + Server Read")]
        public static void VerifyFull()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string script = Path.Combine(projectRoot, "Tools", "verify_config.ps1");
            if (!File.Exists(script))
            {
                Debug.LogError($"[Config PoC] Missing {script}");
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
                    Debug.LogError($"[Config PoC] verify FAILED ({p.ExitCode})\n{stderr}\n{stdout}");
                    return;
                }

                Debug.Log($"[Config PoC] verify OK\n{stdout}");
                AssetDatabase.Refresh();
            }
        }

        [MenuItem("Farming/Config PoC/3. Load StreamingAssets Config (Client)")]
        public static void LoadClientConfig()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, "Config");
            if (!ConfigPaths.IsConfigDir(dir))
            {
                Debug.LogError($"[Config PoC] missing config at {dir} — run menu 1 first");
                return;
            }

            var tables = ConfigTables.LoadFromDirectory(dir);
            int bag = tables.Global.GetInt("bag_init_slots");
            var wheat = tables.Item.Get(1001);
            var crop = tables.Crop.Get("C01");
            Debug.Log(
                $"[Config PoC] Client OK dir={dir} itemCount={tables.Item.Count} " +
                $"bag_init_slots={bag} item1001={wheat.Name}/{wheat.SellPrice} cropC01.growSec={crop.GrowSec}");
        }

        static void RunScript(string fileName, string label)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string script = Path.Combine(projectRoot, "Tools", fileName);
            if (!File.Exists(script))
            {
                Debug.LogError($"[Config PoC] Missing {script}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\"",
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
                    Debug.LogError($"[Config PoC] {label} FAILED\n{stderr}\n{stdout}");
                    return;
                }

                Debug.Log($"[Config PoC] {label} OK\n{stdout}");
                AssetDatabase.Refresh();
            }
        }
    }
}
