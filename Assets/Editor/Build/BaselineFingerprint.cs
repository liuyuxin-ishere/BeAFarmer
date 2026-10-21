using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Editor.Build
{
    /// <summary>
    /// 写入与 Validate 共用的完整冻结指纹收集器（Player 编译源/插件/锁/schema）。
    /// </summary>
    public static class BaselineFingerprint
    {
        public const string Schema = "m1-baseline-fingerprint-v2";

        public static Dictionary<string, string> Collect()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void AddFile(string absOrRel)
            {
                string abs = Path.IsPathRooted(absOrRel)
                    ? Path.GetFullPath(absOrRel)
                    : Path.GetFullPath(Path.Combine(projectRoot, absOrRel));
                if (!File.Exists(abs))
                    return;
                string norm = abs.Replace('\\', '/');
                string rootNorm = projectRoot.Replace('\\', '/');
                if (norm.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;
                if (norm.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;
                string rel = norm.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase)
                    ? norm.Substring(rootNorm.Length).TrimStart('/')
                    : Path.GetFileName(abs);
                map[rel] = Sha(abs);
            }

            foreach (UnityEditor.Compilation.Assembly asm in CompilationPipeline.GetAssemblies(AssembliesType.Player))
            {
                if (asm == null || asm.sourceFiles == null)
                    continue;
                string name = asm.name ?? "";
                if (name.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (name.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                foreach (string src in asm.sourceFiles)
                    AddFile(src);
            }

            // Proto / schema / Defines（影响生成与兼容）
            AddTree(projectRoot, "Shared/Proto", "*.proto", map);
            AddTree(projectRoot, "Shared/config/Defines", "*", map);
            string lubanConf = Path.Combine(projectRoot, "Shared", "config", "luban.conf");
            AddFile(lubanConf);

            // asmdef / asmref（非 Editor/Tests）
            AddTree(projectRoot, "Assets", "*.asmdef", map);
            AddTree(projectRoot, "Assets", "*.asmref", map);

            string link = Path.Combine(Application.dataPath, "link.xml");
            AddFile(link);

            AddFile(Path.Combine(projectRoot, "Packages", "manifest.json"));
            AddFile(Path.Combine(projectRoot, "Packages", "packages-lock.json"));

            // Win64 兼容原生/托管插件 + meta
            CollectWin64Plugins(projectRoot, map);

            return map;
        }

        public static void Write(string output, StringBuilder report)
        {
            var map = Collect();
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            var fsb = new StringBuilder();
            fsb.AppendLine("{");
            fsb.AppendLine("  \"schema\": \"" + Schema + "\",");
            fsb.AppendLine("  \"count\": " + keys.Count + ",");
            fsb.AppendLine("  \"files\": [");
            for (int i = 0; i < keys.Count; i++)
            {
                string p = keys[i];
                fsb.Append("    {\"path\":\"").Append(p).Append("\",\"sha256\":\"").Append(map[p]).Append("\"}");
                if (i + 1 < keys.Count)
                    fsb.Append(',');
                fsb.AppendLine();
            }

            fsb.AppendLine("  ]");
            fsb.AppendLine("}");
            File.WriteAllText(Path.Combine(output, "baseline-fingerprint.json"), fsb.ToString(), new UTF8Encoding(false));
            report?.AppendLine("fingerprintCount=" + keys.Count);
            report?.AppendLine("fingerprintSchema=" + Schema);
        }

        /// <summary>结构化比较；missing/added/changed 均返回原因。</summary>
        public static string ValidateAgainstFile(string fingerprintPath)
        {
            if (string.IsNullOrEmpty(fingerprintPath) || !File.Exists(fingerprintPath))
                return "fingerprint-required";
            string text = File.ReadAllText(fingerprintPath);
            if (!text.Contains("\"schema\": \"" + Schema + "\""))
                return "fingerprint-schema";

            var expected = Parse(text);
            if (expected.Count == 0)
                return "fingerprint-empty";
            var actual = Collect();

            foreach (var kv in expected)
            {
                if (!actual.TryGetValue(kv.Key, out string now))
                    return "fingerprint-missing:" + kv.Key;
                if (!string.Equals(now, kv.Value, StringComparison.OrdinalIgnoreCase))
                    return "fingerprint-changed:" + kv.Key;
            }

            foreach (var kv in actual)
            {
                if (!expected.ContainsKey(kv.Key))
                    return "fingerprint-added:" + kv.Key;
            }

            return "";
        }

        static Dictionary<string, string> Parse(string text)
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            int idx = 0;
            while (true)
            {
                int p = text.IndexOf("\"path\":\"", idx, StringComparison.Ordinal);
                if (p < 0)
                    break;
                p += 8;
                int pe = text.IndexOf('"', p);
                if (pe < 0)
                    break;
                string path = text.Substring(p, pe - p);
                int s = text.IndexOf("\"sha256\":\"", pe, StringComparison.Ordinal);
                if (s < 0)
                    break;
                s += 10;
                int se = text.IndexOf('"', s);
                if (se < 0)
                    break;
                expected[path] = text.Substring(s, se - s);
                idx = se + 1;
            }

            return expected;
        }

        static void CollectWin64Plugins(string projectRoot, Dictionary<string, string> map)
        {
            string[] roots =
            {
                Path.Combine(projectRoot, "Assets"),
                Path.Combine(projectRoot, "Packages")
            };
            string[] patterns = { "*.dll", "*.so", "*.bundle", "*.dylib" };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string pattern in patterns)
                {
                    foreach (string file in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
                    {
                        string norm = file.Replace('\\', '/');
                        if (norm.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        if (norm.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        // Library/PackageCache 只读散列允许：Packages 源路径已覆盖；跳过 Library
                        if (norm.IndexOf("/Library/", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        string assetPath;
                        string rootNorm = projectRoot.Replace('\\', '/');
                        if (norm.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase))
                            assetPath = norm.Substring(rootNorm.Length).TrimStart('/');
                        else
                            continue;

                        // Unity asset path uses Assets/... or Packages/...
                        string unityPath = assetPath;
                        var importer = AssetImporter.GetAtPath(unityPath) as PluginImporter;
                        if (importer == null)
                        {
                            // 非导入器管理的二进制仍纳入（如预置 xLua）
                            map[assetPath] = Sha(file);
                            string meta = file + ".meta";
                            if (File.Exists(meta))
                                map[assetPath + ".meta"] = Sha(meta);
                            continue;
                        }

                        bool win = importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)
                                   || importer.GetCompatibleWithAnyPlatform();
                        if (!win)
                            continue;
                        map[assetPath] = Sha(file);
                        string meta2 = file + ".meta";
                        if (File.Exists(meta2))
                            map[assetPath + ".meta"] = Sha(meta2);
                    }
                }
            }
        }

        static void AddTree(string projectRoot, string relRoot, string pattern, Dictionary<string, string> map)
        {
            string abs = Path.Combine(projectRoot, relRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs))
                return;
            string rootNorm = projectRoot.Replace('\\', '/');
            foreach (string file in Directory.GetFiles(abs, pattern, SearchOption.AllDirectories))
            {
                string norm = file.Replace('\\', '/');
                if (norm.IndexOf("/Tests/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (norm.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (norm.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && pattern != "*.meta")
                {
                    // schema/Defines：保留非 meta；meta 仅插件路径单独加
                    continue;
                }

                string rel = norm.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase)
                    ? norm.Substring(rootNorm.Length).TrimStart('/')
                    : Path.GetFileName(file);
                map[rel] = Sha(file);
            }
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
