using System.Diagnostics;
using System.IO;
using CloudIsle.LubanCfg;
using Luban.SimpleJSON;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Editor.LubanTools
{
    /// <summary>
    /// PoC #7b：Luban 导表与客户端读取。
    /// </summary>
    public static class LubanPoCTools
    {
        [MenuItem("Farming/Luban PoC/1. Generate (gen_luban.ps1)")]
        public static void Generate()
        {
            RunPs1("gen_luban.ps1");
            AssetDatabase.Refresh();
        }

        [MenuItem("Farming/Luban PoC/2. Verify (verify_luban.ps1)")]
        public static void Verify()
        {
            RunPs1("verify_luban.ps1");
            AssetDatabase.Refresh();
        }

        [MenuItem("Farming/Luban PoC/3. Load StreamingAssets ConfigLuban (Client)")]
        public static void LoadClient()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, "ConfigLuban");
            if (!File.Exists(Path.Combine(dir, "tbglobal.json")))
            {
                Debug.LogError($"[Luban PoC] missing {dir}/tbglobal.json — run menu 1 first");
                return;
            }

            var tables = new Tables(name =>
            {
                string text = File.ReadAllText(Path.Combine(dir, name + ".json"));
                return JSON.Parse(text);
            });

            Debug.Log(
                $"[Luban PoC] Client OK bag_init_slots={tables.TbGlobal.BagInitSlots} " +
                $"item1001={tables.TbItem.Get(1001).Name} crop1.growSec={tables.TbCrop.Get(1).GrowSec} " +
                $"tools={tables.TbTool.DataList.Count}");
        }

        static void RunPs1(string fileName)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string script = Path.Combine(projectRoot, "Tools", fileName);
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
                    Debug.LogError($"[Luban PoC] {fileName} FAILED ({p.ExitCode})\n{stderr}\n{stdout}");
                    return;
                }

                Debug.Log($"[Luban PoC] {fileName} OK\n{stdout}");
            }
        }
    }
}
