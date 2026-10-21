using System.Collections.Generic;

namespace Game.Demo.AvatarMerge
{
    /// <summary>LRU 缓存，上限 64（与 04 §6.1 一致）。</summary>
    public sealed class AvatarMergeCache : IAvatarMergeCache
    {
        readonly int _capacity;
        readonly Dictionary<long, LinkedListNode<Entry>> _map;
        readonly LinkedList<Entry> _lru;

        public int Count => _map.Count;
        public int Capacity => _capacity;
        public int HitCount { get; private set; }
        public int MissCount { get; private set; }

        public AvatarMergeCache(int capacity = 64)
        {
            _capacity = capacity > 0 ? capacity : 64;
            _map = new Dictionary<long, LinkedListNode<Entry>>(_capacity);
            _lru = new LinkedList<Entry>();
        }

        public bool TryGet(long outfitHash, out AvatarMergeResult result)
        {
            if (_map.TryGetValue(outfitHash, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                result = node.Value.Result;
                HitCount++;
                return true;
            }

            result = null;
            MissCount++;
            return false;
        }

        public void Put(long outfitHash, AvatarMergeResult result)
        {
            if (result == null) return;

            if (_map.TryGetValue(outfitHash, out var existing))
            {
                var old = existing.Value.Result;
                if (!ReferenceEquals(old, result))
                {
                    old.OwnedByCache = false;
                    old.DestroyOwned();
                }
                existing.Value.Result = result;
                result.OwnedByCache = true;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            while (_map.Count >= _capacity && _lru.Last != null)
                Evict(_lru.Last);

            result.OwnedByCache = true;
            var entry = new Entry { Hash = outfitHash, Result = result };
            var node = _lru.AddFirst(entry);
            _map[outfitHash] = node;
        }

        public void Clear()
        {
            while (_lru.Last != null)
                Evict(_lru.Last);
            HitCount = 0;
            MissCount = 0;
        }

        void Evict(LinkedListNode<Entry> node)
        {
            _map.Remove(node.Value.Hash);
            var result = node.Value.Result;
            _lru.Remove(node);
            if (result != null)
            {
                result.OwnedByCache = false;
                result.DestroyOwned();
            }
        }

        sealed class Entry
        {
            public long Hash;
            public AvatarMergeResult Result;
        }
    }
}
