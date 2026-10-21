using System;
using System.Collections.Generic;
using CloudIsle.Config;

namespace CloudIsle.Server.Config
{
    /// <summary>服务端读取 T41 导出目录，并在校验通过后整体替换快照。</summary>
    public sealed class ServerConfigService
    {
        readonly object _gate = new object();
        ConfigSnapshot? _snapshot;

        /// <summary>
        /// 从目录加载清单和全部登记表。失败时保留上一份快照，第一次失败则保持未加载。
        /// 并发调用在锁内串行，旧调用不能在新调用完成后再写回快照。
        /// </summary>
        /// <param name="directory">含 config_manifest.json 的目录，例如 Server/ConfigLuban。</param>
        /// <param name="expectedVersion">进程要求的配置版本。与清单不一致时失败。</param>
        public void Load(string directory, int expectedVersion)
        {
            if (directory == null)
                throw new ArgumentNullException(nameof(directory));

            lock (_gate)
            {
                ConfigSnapshot next = ConfigSnapshotReader.ReadDirectory(directory, expectedVersion);
                _snapshot = next;
            }
        }

        /// <summary>读取当前快照里的 Global 字符串。</summary>
        /// <param name="key">Global 键。未知键抛出 KeyNotFoundException，不返回空字符串。</param>
        /// <returns>该键对应的规范化字符串。</returns>
        public string GetGlobal(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("配置键不能为空", nameof(key));

            ConfigSnapshot? snapshot;
            lock (_gate)
            {
                snapshot = _snapshot;
            }

            if (snapshot == null)
                throw new InvalidOperationException("配置尚未加载");
            if (!snapshot.Globals.TryGetValue(key, out string? value) || value == null)
                throw new KeyNotFoundException("未知配置键: " + key);
            return value;
        }
    }
}
