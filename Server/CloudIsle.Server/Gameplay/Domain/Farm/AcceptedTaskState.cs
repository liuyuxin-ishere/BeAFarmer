using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// 已接受任务聚合（typed durable sidecar）：收获规格按 plotId 索引；
    /// 未知存量键值原样保留，不得静默丢弃。重复 plot 实例拒绝构造。
    /// Unknown 的公开视图对 value 做独立字节副本；内部存档与 Encode 只读共用一份，
    /// 避免每次 Encode 整表再拷，也避免调用者改返回/输入数组污染内部状态。
    /// </summary>
    public sealed class AcceptedTaskState : IEquatable<AcceptedTaskState>
    {
        public static AcceptedTaskState Empty { get; } = new(
            Array.Empty<KeyValuePair<int, AcceptedHarvestSpec>>(),
            Array.Empty<KeyValuePair<string, byte[]>>());

        readonly IReadOnlyDictionary<int, AcceptedHarvestSpec> _harvestByPlot;
        /// <summary>内部权威未知存量（构造时已拷贝）；不得直接暴露可变 byte[]。</summary>
        readonly Dictionary<string, byte[]> _unknown;

        public AcceptedTaskState(
            IEnumerable<KeyValuePair<int, AcceptedHarvestSpec>> harvestByPlot,
            IEnumerable<KeyValuePair<string, byte[]>>? unknownEntries = null)
        {
            if (harvestByPlot == null)
                throw new ArgumentNullException(nameof(harvestByPlot));

            var harvest = new Dictionary<int, AcceptedHarvestSpec>();
            foreach (KeyValuePair<int, AcceptedHarvestSpec> pair in harvestByPlot)
            {
                if (pair.Key <= 0)
                    throw new ArgumentException("plotId 必须为正", nameof(harvestByPlot));
                if (pair.Value == null)
                    throw new ArgumentException("harvest spec 不能为 null", nameof(harvestByPlot));
                if (!harvest.TryAdd(pair.Key, pair.Value.Clone()))
                    throw new ArgumentException("重复 plotId 不得静默覆盖", nameof(harvestByPlot));
            }

            var unknown = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (unknownEntries != null)
            {
                foreach (KeyValuePair<string, byte[]> pair in unknownEntries)
                {
                    if (string.IsNullOrEmpty(pair.Key))
                        throw new ArgumentException("unknown key 不能为空", nameof(unknownEntries));
                    if (pair.Value == null)
                        throw new ArgumentException("unknown value 不能为 null", nameof(unknownEntries));
                    byte[] copy = new byte[pair.Value.Length];
                    Buffer.BlockCopy(pair.Value, 0, copy, 0, pair.Value.Length);
                    if (!unknown.TryAdd(pair.Key, copy))
                        throw new ArgumentException("重复 unknown key", nameof(unknownEntries));
                }
            }

            _harvestByPlot = new ReadOnlyDictionary<int, AcceptedHarvestSpec>(harvest);
            _unknown = unknown;
        }

        public IReadOnlyDictionary<int, AcceptedHarvestSpec> HarvestByPlotId => _harvestByPlot;
        /// <summary>只读视图：每次取 value 均为独立副本；改返回数组或构造前原数组均不影响内部。</summary>
        public IReadOnlyDictionary<string, byte[]> UnknownEntries => new DefensiveUnknownMap(_unknown);
        public int HarvestCount => _harvestByPlot.Count;
        public int UnknownCount => _unknown.Count;
        public bool IsEmpty => _harvestByPlot.Count == 0 && _unknown.Count == 0;

        public bool TryGetHarvest(int plotId, out AcceptedHarvestSpec spec)
        {
            if (_harvestByPlot.TryGetValue(plotId, out AcceptedHarvestSpec? found) && found != null)
            {
                spec = found;
                return true;
            }

            spec = null!;
            return false;
        }

        /// <summary>绑定或替换单块已接受规格；返回新状态（深拷贝）。</summary>
        public AcceptedTaskState WithHarvest(int plotId, AcceptedHarvestSpec spec)
        {
            if (plotId <= 0)
                throw new ArgumentOutOfRangeException(nameof(plotId));
            if (spec == null)
                throw new ArgumentNullException(nameof(spec));

            var map = new Dictionary<int, AcceptedHarvestSpec>(_harvestByPlot.Count + 1);
            foreach (KeyValuePair<int, AcceptedHarvestSpec> kv in _harvestByPlot)
                map[kv.Key] = kv.Value;
            map[plotId] = spec.Clone();
            return new AcceptedTaskState(map, CloneUnknownPairs());
        }

        public AcceptedTaskState WithoutHarvest(int plotId)
        {
            if (!_harvestByPlot.ContainsKey(plotId))
                return Clone();
            var map = new Dictionary<int, AcceptedHarvestSpec>(_harvestByPlot.Count);
            foreach (KeyValuePair<int, AcceptedHarvestSpec> kv in _harvestByPlot)
            {
                if (kv.Key != plotId)
                    map[kv.Key] = kv.Value;
            }

            return new AcceptedTaskState(map, CloneUnknownPairs());
        }

        public AcceptedTaskState Clone()
        {
            if (IsEmpty)
                return Empty;
            var harvest = new List<KeyValuePair<int, AcceptedHarvestSpec>>(_harvestByPlot.Count);
            foreach (KeyValuePair<int, AcceptedHarvestSpec> kv in _harvestByPlot)
                harvest.Add(new KeyValuePair<int, AcceptedHarvestSpec>(kv.Key, kv.Value.Clone()));
            return new AcceptedTaskState(harvest, CloneUnknownPairs());
        }

        public bool Equals(AcceptedTaskState? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (_harvestByPlot.Count != other._harvestByPlot.Count
                || _unknown.Count != other._unknown.Count)
                return false;
            foreach (KeyValuePair<int, AcceptedHarvestSpec> kv in _harvestByPlot)
            {
                if (!other._harvestByPlot.TryGetValue(kv.Key, out AcceptedHarvestSpec? spec)
                    || !kv.Value.Equals(spec))
                    return false;
            }

            foreach (KeyValuePair<string, byte[]> kv in _unknown)
            {
                if (!other._unknown.TryGetValue(kv.Key, out byte[]? bytes)
                    || bytes == null
                    || bytes.Length != kv.Value.Length)
                    return false;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != kv.Value[i])
                        return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as AcceptedTaskState);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = _harvestByPlot.Count ^ (_unknown.Count * 397);
                if (_harvestByPlot.Count > 0)
                {
                    var keys = new int[_harvestByPlot.Count];
                    int i = 0;
                    foreach (int key in _harvestByPlot.Keys)
                        keys[i++] = key;
                    Array.Sort(keys);
                    for (int k = 0; k < keys.Length; k++)
                        h = (h * 31) + HashCode.Combine(keys[k], _harvestByPlot[keys[k]].GetHashCode());
                }

                if (_unknown.Count > 0)
                {
                    var ukeys = new string[_unknown.Count];
                    int i = 0;
                    foreach (string key in _unknown.Keys)
                        ukeys[i++] = key;
                    Array.Sort(ukeys, StringComparer.Ordinal);
                    for (int k = 0; k < ukeys.Length; k++)
                        h = (h * 31) + StringComparer.Ordinal.GetHashCode(ukeys[k]);
                }

                return h;
            }
        }

        public static bool Same(AcceptedTaskState? a, AcceptedTaskState? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            a ??= Empty;
            b ??= Empty;
            return a.Equals(b);
        }

        /// <summary>
        /// Codec 确定性写出未知存量：直接读内部只存档，不经公开视图、不整表再拷。
        /// 调用方仅允许读取传入的 value，不得保留或写入。
        /// </summary>
        internal void ForEachUnknownSorted(Action<string, byte[]> visitor)
        {
            if (visitor == null)
                throw new ArgumentNullException(nameof(visitor));
            if (_unknown.Count == 0)
                return;

            var keys = new string[_unknown.Count];
            int i = 0;
            foreach (string key in _unknown.Keys)
                keys[i++] = key;
            Array.Sort(keys, StringComparer.Ordinal);
            for (int k = 0; k < keys.Length; k++)
                visitor(keys[k], _unknown[keys[k]]);
        }

        IEnumerable<KeyValuePair<string, byte[]>> CloneUnknownPairs()
        {
            if (_unknown.Count == 0)
                return Array.Empty<KeyValuePair<string, byte[]>>();
            var list = new List<KeyValuePair<string, byte[]>>(_unknown.Count);
            foreach (KeyValuePair<string, byte[]> kv in _unknown)
            {
                byte[] copy = new byte[kv.Value.Length];
                Buffer.BlockCopy(kv.Value, 0, copy, 0, kv.Value.Length);
                list.Add(new KeyValuePair<string, byte[]>(kv.Key, copy));
            }

            return list;
        }

        /// <summary>
        /// 公开未知存量字典：Keys/Count 不拷贝；Indexer/Values/枚举的 value 均为独立字节副本。
        /// </summary>
        sealed class DefensiveUnknownMap : IReadOnlyDictionary<string, byte[]>
        {
            readonly Dictionary<string, byte[]> _inner;

            public DefensiveUnknownMap(Dictionary<string, byte[]> inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public int Count => _inner.Count;
            public IEnumerable<string> Keys => _inner.Keys;

            public IEnumerable<byte[]> Values
            {
                get
                {
                    foreach (byte[] value in _inner.Values)
                        yield return CloneBytes(value);
                }
            }

            public byte[] this[string key] => CloneBytes(_inner[key]);

            public bool ContainsKey(string key) => _inner.ContainsKey(key);

            public bool TryGetValue(string key, out byte[] value)
            {
                if (_inner.TryGetValue(key, out byte[]? found) && found != null)
                {
                    value = CloneBytes(found);
                    return true;
                }

                value = null!;
                return false;
            }

            public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator()
            {
                foreach (KeyValuePair<string, byte[]> kv in _inner)
                    yield return new KeyValuePair<string, byte[]>(kv.Key, CloneBytes(kv.Value));
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            static byte[] CloneBytes(byte[] source)
            {
                var copy = new byte[source.Length];
                Buffer.BlockCopy(source, 0, copy, 0, source.Length);
                return copy;
            }
        }
    }

    /// <summary>
    /// 无历史 accepted harvest 的生长作物：不得用当前 Crop 表捏造已付费产物。
    /// T033 收获须拒绝；T029 仅定义策略与仓储投影。
    /// </summary>
    public static class FarmLegacyHarvestPolicy
    {
        public const string DiagMissingAcceptedSpec = "legacy-growing-missing-accepted-harvest-spec";

        public static bool HasDurableSettlementSpec(int plotId, AcceptedTaskState? tasks)
        {
            if (plotId <= 0 || tasks == null)
                return false;
            return tasks.TryGetHarvest(plotId, out _);
        }
    }
}
