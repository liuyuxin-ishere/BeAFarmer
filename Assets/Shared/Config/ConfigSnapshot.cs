using System;
using System.Collections;
using System.Collections.Generic;

namespace CloudIsle.Config
{
    /// <summary>
    /// 一份已经校验通过的配置快照。
    /// Global 保存为自己的字符串副本。其余配置仍由已生成的 Luban 类型读取，不放进这个字典。
    /// </summary>
    public sealed class ConfigSnapshot
    {
        /// <summary>复制调用方给出的键值，并包成不可写视图。</summary>
        /// <param name="version">清单里的 configVersion，必须大于 0。</param>
        /// <param name="globals">已规范化的 Global 字符串。本构造会复制，之后修改原字典不影响快照。</param>
        public ConfigSnapshot(int version, IReadOnlyDictionary<string, string> globals)
        {
            if (globals == null)
                throw new ArgumentNullException(nameof(globals));
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version));

            Version = version;
            Globals = new OwnedGlobals(globals);
        }

        /// <summary>这份快照对应的配置版本。</summary>
        public int Version { get; }

        /// <summary>Global 键值。只实现只读接口，转成可写字典会失败。</summary>
        public IReadOnlyDictionary<string, string> Globals { get; }

        sealed class OwnedGlobals : IReadOnlyDictionary<string, string>
        {
            readonly Dictionary<string, string> _items;

            /// <summary>把来源逐项复制到新的字典。</summary>
            /// <param name="source">调用方持有的键值。允许是可写字典。</param>
            public OwnedGlobals(IReadOnlyDictionary<string, string> source)
            {
                _items = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> pair in source)
                {
                    if (pair.Key == null)
                        throw new ArgumentException("配置键不能为 null", nameof(source));
                    if (pair.Value == null)
                        throw new ArgumentException("配置值不能为 null", nameof(source));
                    _items.Add(pair.Key, pair.Value);
                }
            }

            /// <summary>键的数量。</summary>
            public int Count
            {
                get { return _items.Count; }
            }

            /// <summary>按键取出已经复制的字符串。</summary>
            /// <param name="key">Global 键。</param>
            public string this[string key]
            {
                get { return _items[key]; }
            }

            /// <summary>全部键。枚举的是副本里的键。</summary>
            public IEnumerable<string> Keys
            {
                get { return _items.Keys; }
            }

            /// <summary>全部值。枚举的是副本里的值。</summary>
            public IEnumerable<string> Values
            {
                get { return _items.Values; }
            }

            /// <summary>副本里是否有这个键。</summary>
            /// <param name="key">要查的键。</param>
            /// <returns>有这个键时为 true。</returns>
            public bool ContainsKey(string key)
            {
                return _items.ContainsKey(key);
            }

            /// <summary>尝试取出副本里的值。</summary>
            /// <param name="key">要查的键。</param>
            /// <param name="value">找到时写入副本中的值。</param>
            /// <returns>找到时为 true。</returns>
            public bool TryGetValue(string key, out string value)
            {
                return _items.TryGetValue(key, out value);
            }

            /// <summary>枚举副本中的键值对。</summary>
            /// <returns>键值枚举器。</returns>
            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                return _items.GetEnumerator();
            }

            /// <summary>非泛型枚举入口。</summary>
            /// <returns>键值枚举器。</returns>
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
