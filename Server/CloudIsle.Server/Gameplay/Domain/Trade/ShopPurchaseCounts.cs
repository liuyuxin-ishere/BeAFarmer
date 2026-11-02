using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CloudIsle.Domain.Trade
{
    /// <summary>
    /// 当日商店购买计数（typed durable sidecar；不进 PlayerFullData / marker CapabilityHash）。
    /// 日切由 <c>ShopDailyResetPolicy</c> 清空；买卖扩容/登录无关命令不得清掉。
    /// </summary>
    public sealed class ShopPurchaseCounts : IEquatable<ShopPurchaseCounts>
    {
        public static ShopPurchaseCounts Empty { get; } = new(Array.Empty<KeyValuePair<int, int>>());

        readonly IReadOnlyDictionary<int, int> _counts;

        public ShopPurchaseCounts(IEnumerable<KeyValuePair<int, int>> entries)
        {
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));
            var map = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> pair in entries)
            {
                if (pair.Key <= 0)
                    throw new ArgumentException("shopItemId 必须为正", nameof(entries));
                if (pair.Value <= 0)
                    throw new ArgumentException("count 必须为正（零行省略）", nameof(entries));
                if (!map.TryAdd(pair.Key, pair.Value))
                    throw new ArgumentException("重复 shopItemId", nameof(entries));
            }

            _counts = new ReadOnlyDictionary<int, int>(map);
        }

        public IReadOnlyDictionary<int, int> Counts => _counts;
        public int Count => _counts.Count;
        public bool IsEmpty => _counts.Count == 0;

        public int GetCount(int shopItemId)
        {
            if (shopItemId <= 0)
                return 0;
            return _counts.TryGetValue(shopItemId, out int n) ? n : 0;
        }

        /// <summary>返回写入后的新账本；checked 累加；countDelta 必须为正。</summary>
        public ShopPurchaseCounts WithAdded(int shopItemId, int countDelta)
        {
            if (shopItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(shopItemId));
            if (countDelta <= 0)
                throw new ArgumentOutOfRangeException(nameof(countDelta));

            var map = new Dictionary<int, int>(_counts.Count + 1);
            foreach (KeyValuePair<int, int> kv in _counts)
                map[kv.Key] = kv.Value;

            int next;
            try
            {
                next = checked(GetCount(shopItemId) + countDelta);
            }
            catch (OverflowException)
            {
                throw new OverflowException("shop purchase count overflow");
            }

            map[shopItemId] = next;
            return new ShopPurchaseCounts(map);
        }

        public ShopPurchaseCounts Clone()
        {
            if (IsEmpty)
                return Empty;
            return new ShopPurchaseCounts(_counts);
        }

        public bool Equals(ShopPurchaseCounts? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (_counts.Count != other._counts.Count)
                return false;
            foreach (KeyValuePair<int, int> kv in _counts)
            {
                if (!other._counts.TryGetValue(kv.Key, out int n) || n != kv.Value)
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ShopPurchaseCounts);

        public override int GetHashCode()
        {
            // 与插入顺序无关：按 shopItemId 排序后混合，满足相等对象同哈希。
            unchecked
            {
                int h = _counts.Count;
                if (_counts.Count == 0)
                    return h;
                var keys = new int[_counts.Count];
                int i = 0;
                foreach (int key in _counts.Keys)
                    keys[i++] = key;
                Array.Sort(keys);
                for (int k = 0; k < keys.Length; k++)
                    h = (h * 31) + HashCode.Combine(keys[k], _counts[keys[k]]);
                return h;
            }
        }

        public static bool Same(ShopPurchaseCounts? a, ShopPurchaseCounts? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            a ??= Empty;
            b ??= Empty;
            return a.Equals(b);
        }
    }
}
