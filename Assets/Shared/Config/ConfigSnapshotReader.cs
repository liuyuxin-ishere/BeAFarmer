using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CloudIsle.Config
{
    /// <summary>
    /// 读取 T41 的 m1.config-manifest.v1 产物。
    /// 只把 Global 收成字符串快照。其他表只校验文件名、长度和哈希，不改已生成类型。
    /// </summary>
    public static class ConfigSnapshotReader
    {
        /// <summary>清单文件名。它本身不进入 files 哈希列表。</summary>
        public const string ManifestFileName = "config_manifest.json";

        /// <summary>清单 schema。</summary>
        public const string Schema = "m1.config-manifest.v1";

        /// <summary>101 之前禁止出现的补丁演示键。</summary>
        public const string PatchMessageKey = "m1_patch_message";

        /// <summary>100 基包必须具备的 Global 键。新增键可以多，这 22 个不能少。</summary>
        public static readonly string[] RequiredGlobalKeys =
        {
            "bag_init_slots",
            "bag_expand_step",
            "bag_expand_costs",
            "stack_default",
            "water_ratio",
            "fertilize_ratio",
            "speedup_sec_per_gem",
            "daily_reset_hour",
            "order_daily_count",
            "order_reroll_free",
            "order_reroll_costs",
            "order_ticket_at_3",
            "order_ticket_at_6",
            "order_all_done_gem",
            "rare_bonus_chance",
            "plaza_unlock_level",
            "gacha_unlock_level",
            "plaza_line_capacity",
            "chat_interval_sec",
            "new_player_gold",
            "new_player_gem",
            "new_player_ticket"
        };

        /// <summary>
        /// 从目录读取清单和登记表。任一校验失败都不返回快照。
        /// </summary>
        /// <param name="directory">含 config_manifest.json 的目录。只接受该目录内的登记文件。</param>
        /// <param name="expectedVersion">调用方要求的配置版本。</param>
        /// <returns>校验通过后的完整快照。</returns>
        public static ConfigSnapshot ReadDirectory(string directory, int expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ConfigLoadException("配置目录不能为空");

            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                throw new ConfigLoadException("配置目录不存在: " + directory);

            byte[] manifestBytes = ReadUnderRoot(root, ManifestFileName);
            return Read(manifestBytes, name => ReadUnderRoot(root, name), expectedVersion);
        }

        /// <summary>
        /// 用已经读入内存的清单和按文件名取内容的委托做完整校验。
        /// </summary>
        /// <param name="manifestBytes">清单原文的字节。不把清单自身算进 files 哈希。</param>
        /// <param name="readListedFile">按清单里的文件名读取表字节。只会收到已通过文件名检查的名字。</param>
        /// <param name="expectedVersion">调用方要求的配置版本。</param>
        /// <returns>校验通过后的完整快照。</returns>
        public static ConfigSnapshot Read(byte[] manifestBytes, Func<string, byte[]> readListedFile, int expectedVersion)
        {
            if (manifestBytes == null || manifestBytes.Length == 0)
                throw new ConfigLoadException("配置清单为空");
            if (readListedFile == null)
                throw new ArgumentNullException(nameof(readListedFile));
            if (expectedVersion < 1)
                throw new ConfigLoadException("要求的配置版本必须大于 0");

            SimpleJson.Obj manifest;
            try
            {
                manifest = SimpleJson.ParseObject(Encoding.UTF8.GetString(manifestBytes));
            }
            catch (FormatException ex)
            {
                throw new ConfigLoadException("配置清单不是合法 JSON: " + ex.Message);
            }

            if (manifest.RequireString("schema") != Schema)
                throw new ConfigLoadException("配置清单 schema 不匹配");

            long versionNumber = manifest.RequireLong("configVersion");
            if (versionNumber > int.MaxValue || versionNumber != expectedVersion)
                throw new ConfigLoadException("配置版本不匹配，要求 " + expectedVersion.ToString(CultureInfo.InvariantCulture));

            SimpleJson.Obj globals = manifest.GetObject("globals");
            if (globals.RequireString("format") != "luban-map")
                throw new ConfigLoadException("Global 格式必须是 luban-map");
            if (globals.RequireString("keyField") != "key" || globals.RequireString("valueField") != "value")
                throw new ConfigLoadException("Global 字段必须是 key 与 value");
            if (globals.RequireString("valueType") != "string")
                throw new ConfigLoadException("Global 值类型必须是 string");

            string globalFile = globals.RequireString("file");
            string typedGlobalFile = globals.RequireString("typedGlobalFile");
            if (!IsSafeFileName(globalFile) || !IsSafeFileName(typedGlobalFile))
                throw new ConfigLoadException("Global 文件名不在允许范围内");

            List<SimpleJson.Obj> files = manifest.GetObjectArray("files");
            if (files.Count == 0)
                throw new ConfigLoadException("配置清单没有登记文件");

            var listed = new Dictionary<string, FileRecord>(files.Count, StringComparer.Ordinal);
            for (int i = 0; i < files.Count; i++)
            {
                SimpleJson.Obj item = files[i];
                string name = item.RequireString("name");
                if (!IsSafeFileName(name))
                    throw new ConfigLoadException("非法配置文件名: " + name);
                if (listed.ContainsKey(name))
                    throw new ConfigLoadException("配置文件名重复: " + name);

                string sha = item.RequireString("sha256");
                if (sha.Length != 64)
                    throw new ConfigLoadException("配置哈希长度不正确: " + name);
                for (int c = 0; c < sha.Length; c++)
                {
                    char ch = sha[c];
                    bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
                    if (!hex)
                        throw new ConfigLoadException("配置哈希不是小写十六进制: " + name);
                }

                long length = item.RequireLong("length");
                if (length > int.MaxValue)
                    throw new ConfigLoadException("配置文件长度超出范围: " + name);
                listed.Add(name, new FileRecord(name, sha, (int)length));
            }

            if (!listed.ContainsKey(globalFile))
                throw new ConfigLoadException("清单未登记 Global 文件: " + globalFile);
            if (!listed.ContainsKey(typedGlobalFile))
                throw new ConfigLoadException("清单未登记强类型 Global 文件: " + typedGlobalFile);

            var payloads = new Dictionary<string, byte[]>(listed.Count, StringComparer.Ordinal);
            foreach (FileRecord record in listed.Values)
            {
                byte[] bytes;
                try
                {
                    bytes = readListedFile(record.Name);
                }
                catch (ConfigLoadException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ConfigLoadException("读取配置文件失败: " + record.Name + " " + ex.Message);
                }

                if (bytes == null)
                    throw new ConfigLoadException("缺少配置文件: " + record.Name);
                if (bytes.Length != record.Length)
                    throw new ConfigLoadException("配置文件长度不符: " + record.Name);
                if (!string.Equals(Sha256Hex(bytes), record.Sha256, StringComparison.Ordinal))
                    throw new ConfigLoadException("配置文件哈希不符: " + record.Name);
                payloads.Add(record.Name, bytes);
            }

            Dictionary<string, string> values = ParseGlobals(payloads[globalFile], (int)versionNumber);
            return new ConfigSnapshot((int)versionNumber, values);
        }

        /// <summary>判断文件名是不是单个允许的 json 名。</summary>
        /// <param name="name">清单里的 name 字段。</param>
        /// <returns>只含小写字母、数字、下划线和一个 .json 后缀时为 true。</returns>
        public static bool IsSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 6 || !name.EndsWith(".json", StringComparison.Ordinal))
                return false;
            if (name != Path.GetFileName(name))
                return false;

            for (int i = 0; i < name.Length - 5; i++)
            {
                char ch = name[i];
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_';
                if (!ok)
                    return false;
            }

            return true;
        }

        static Dictionary<string, string> ParseGlobals(byte[] bytes, int version)
        {
            List<SimpleJson.Obj> rows;
            try
            {
                rows = SimpleJson.ParseArray(Encoding.UTF8.GetString(bytes));
            }
            catch (FormatException ex)
            {
                throw new ConfigLoadException("Global 表不是合法 JSON 数组: " + ex.Message);
            }

            var values = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                string key = rows[i].RequireString("key");
                string value = rows[i].RequireString("value");
                if (key.Length == 0)
                    throw new ConfigLoadException("Global 键不能为空");
                if (version < 101 && key == PatchMessageKey)
                    throw new ConfigLoadException("配置版本小于 101，不能包含 " + PatchMessageKey);
                if (values.ContainsKey(key))
                    throw new ConfigLoadException("Global 键重复: " + key);
                values.Add(key, value);
            }

            for (int i = 0; i < RequiredGlobalKeys.Length; i++)
            {
                if (!values.ContainsKey(RequiredGlobalKeys[i]))
                    throw new ConfigLoadException("缺少必需 Global 键: " + RequiredGlobalKeys[i]);
            }

            return values;
        }

        static byte[] ReadUnderRoot(string root, string name)
        {
            if (!IsSafeFileName(name))
                throw new ConfigLoadException("非法配置文件名: " + name);

            string full = Path.GetFullPath(Path.Combine(root, name));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ConfigLoadException("配置文件越出目录: " + name);
            if (!File.Exists(full))
                throw new ConfigLoadException("缺少配置文件: " + name);
            return File.ReadAllBytes(full);
        }

        static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        readonly struct FileRecord
        {
            public FileRecord(string name, string sha256, int length)
            {
                Name = name;
                Sha256 = sha256;
                Length = length;
            }

            public string Name { get; }
            public string Sha256 { get; }
            public int Length { get; }
        }
    }
}
