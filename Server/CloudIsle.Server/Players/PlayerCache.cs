using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Players;

/// <summary>
/// 玩家内存缓存。只有 PlayerQueue 正在执行的同一玩家动作可以访问或修改。
/// 不负责定时保存。保存成功后要由调用方在该玩家队列里 ConfirmSaved。
/// </summary>
public sealed class PlayerCache
{
    readonly object _gate = new();
    readonly Dictionary<long, Entry> _entries = new();
    readonly PlayerStore _store;

    /// <summary>使用指定的玩家存储。缓存不自己打开数据库连接。</summary>
    /// <param name="store">加载和保存都走它的 SQLite 队列。</param>
    public PlayerCache(PlayerStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>当前缓存中的玩家数量，供测试观察移除和失败不入缓存。</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>这名玩家是否已经在缓存里。不访问数据库，也不要求调用方处在玩家队列中。</summary>
    /// <param name="playerId">要查看的玩家主键。</param>
    /// <returns>缓存里有该玩家时为 true。没有时为 false，不会因此去加载。</returns>
    internal bool IsCached(long playerId)
    {
        lock (_gate)
            return _entries.ContainsKey(playerId);
    }

    /// <summary>复制当前缓存里仍脏的玩家主键。不修改状态，不访问数据库，也不要求调用方处在玩家队列中。</summary>
    /// <returns>Revision 与 SavedRevision 不同的玩家主键，按主键升序。</returns>
    internal long[] CopyDirtyPlayerIds()
    {
        lock (_gate)
        {
            var ids = new List<long>();
            foreach (KeyValuePair<long, Entry> pair in _entries)
            {
                if (pair.Value.Revision != pair.Value.SavedRevision)
                    ids.Add(pair.Key);
            }

            ids.Sort();
            return ids.ToArray();
        }
    }

    /// <summary>若该玩家已在缓存且仍脏，复制一份独立快照。未缓存或已干净时不访问数据库。</summary>
    /// <param name="playerId">要复制的玩家。当前异步流必须正在执行这名玩家的队列动作。</param>
    /// <param name="snapshot">成功时为数据和两个版本的深拷贝；失败时为 null。</param>
    /// <returns>拿到一份脏快照时为 true。</returns>
    internal bool TryCopyDirty(long playerId, out PlayerState? snapshot)
    {
        RequireCurrent(playerId);
        lock (_gate)
        {
            if (!_entries.TryGetValue(playerId, out Entry? entry) || entry.Revision == entry.SavedRevision)
            {
                snapshot = null;
                return false;
            }

            snapshot = entry.Capture(playerId);
            return true;
        }
    }

    /// <summary>取得玩家快照。第一次查数据库，之后复用缓存。返回值是独立深拷贝。</summary>
    /// <param name="playerId">要读取的玩家。当前异步流必须正在执行这名玩家的队列动作。</param>
    /// <param name="ct">传给数据库加载。取消时不放入缓存。</param>
    public async Task<PlayerState> GetAsync(long playerId, CancellationToken ct)
    {
        RequireCurrent(playerId);
        lock (_gate)
        {
            if (_entries.TryGetValue(playerId, out Entry? cached))
                return cached.Capture(playerId);
        }

        PlayerState loaded = await _store.LoadAsync(playerId, ct).ConfigureAwait(false);
        RequireCurrent(playerId);
        lock (_gate)
        {
            if (!_entries.TryGetValue(playerId, out Entry? cached))
            {
                cached = new Entry(loaded.Data, loaded.Revision, loaded.SavedRevision);
                _entries.Add(playerId, cached);
            }

            return cached.Capture(playerId);
        }
    }

    /// <summary>只把版本加一，不改玩家数据。已经载入的玩家才会被标脏。</summary>
    /// <param name="playerId">要标脏的玩家。必须是当前队列动作的玩家。</param>
    public void MarkDirty(long playerId)
    {
        RequireCurrent(playerId);
        lock (_gate)
            Bump(Required(playerId));
    }

    /// <summary>从缓存移除已经保存且没有活跃持有的玩家。脏数据或仍被持有时拒绝。</summary>
    /// <param name="playerId">要移除的玩家。必须是当前队列动作的玩家，且已经在缓存中。</param>
    public void Remove(long playerId)
    {
        RequireCurrent(playerId);
        lock (_gate)
        {
            Entry entry = Required(playerId);
            if (entry.Revision != entry.SavedRevision)
                throw new InvalidOperationException("玩家仍有未保存修改，拒绝移除");
            if (entry.Holds > 0)
                throw new InvalidOperationException("玩家仍被活跃持有，拒绝移除");
            _entries.Remove(playerId);
        }
    }

    /// <summary>确认某个版本已经成功写入。不大于当前版本时才前移 SavedRevision；期间又有修改则仍算脏。</summary>
    /// <param name="playerId">要确认的玩家。必须是当前队列动作的玩家。</param>
    /// <param name="savedRevision">已经成功写入数据库的那个版本。</param>
    public void ConfirmSaved(long playerId, long savedRevision)
    {
        RequireCurrent(playerId);
        if (savedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(savedRevision), "已保存版本不能为负");

        lock (_gate)
        {
            Entry entry = Required(playerId);
            if (savedRevision > entry.Revision)
                throw new InvalidOperationException("确认的保存版本超出当前版本");
            if (savedRevision < entry.SavedRevision)
                throw new InvalidOperationException("拒绝把已保存版本回退");
            entry.SavedRevision = savedRevision;
        }
    }

    /// <summary>只读尚未释放的持有次数。不要求处在玩家队列，不改数据和版本。</summary>
    /// <param name="playerId">要观察的玩家主键。</param>
    /// <returns>未缓存时为 0；已缓存时为当前持有计数。</returns>
    internal int CopyHoldCount(long playerId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(playerId, out Entry? entry))
                return 0;
            return entry.Holds;
        }
    }

    /// <summary>登记一次活跃持有。后续保存服务可在落库期间持有，本项不启动定时器。</summary>
    /// <param name="playerId">要持有的玩家。必须是当前队列动作的玩家，且已经载入。</param>
    /// <returns>在同一玩家队列里释放的句柄。重复释放不会把计数减成负数。</returns>
    public IDisposable AcquireHold(long playerId)
    {
        RequireCurrent(playerId);
        lock (_gate)
        {
            Required(playerId).Holds++;
            return new Hold(this, playerId);
        }
    }

    /// <summary>测试夹具用来修改缓存中的昵称，并让版本加一。不是生产玩法接口。</summary>
    /// <param name="playerId">要修改的玩家。必须是当前队列动作的玩家，且已经载入。</param>
    /// <param name="nickname">新的昵称。写入缓存里的 Info，不直接写数据库。</param>
    internal void ReplaceNickname(long playerId, string nickname)
    {
        RequireCurrent(playerId);
        if (nickname == null)
            throw new ArgumentNullException(nameof(nickname));

        lock (_gate)
        {
            Entry entry = Required(playerId);
            // 版本到达上限时必须在改变任何业务字段之前拒绝。
            Bump(entry);
            if (entry.Data.Info == null)
                entry.Data.Info = new PlayerInfo { PlayerId = playerId };
            entry.Data.Info.Nickname = nickname;
        }
    }

    /// <summary>
    /// 释放本缓存发出的保存持有。不要求处在玩家队列，因此保存结束时不必再入队。
    /// 只减少持有计数，不改 Data、Revision、SavedRevision。
    /// </summary>
    /// <param name="hold">本缓存 AcquireHold 返回的句柄。其他对象会被拒绝。</param>
    internal void ReleaseSaveHold(IDisposable hold)
    {
        if (hold is not Hold typed || !ReferenceEquals(typed.Cache, this))
            throw new InvalidOperationException("只能释放本缓存发出的保存持有");
        typed.ReleaseMetadata();
    }

    /// <summary>减少一次持有。必须仍处在该玩家的队列动作里。</summary>
    /// <param name="playerId">要释放的玩家。</param>
    void ReleaseHold(long playerId)
    {
        RequireCurrent(playerId);
        ReleaseHoldMetadata(playerId);
    }

    /// <summary>在缓存锁内把持有计数减一。不改数据和两个版本。</summary>
    /// <param name="playerId">要释放的玩家。必须已经在缓存中，且持有计数大于 0。</param>
    void ReleaseHoldMetadata(long playerId)
    {
        lock (_gate)
        {
            Entry entry = Required(playerId);
            if (entry.Holds <= 0)
                throw new InvalidOperationException("玩家没有可释放的持有");
            entry.Holds--;
        }
    }

    /// <summary>核对当前异步流就是这名玩家的队列动作。</summary>
    /// <param name="playerId">调用方声称要访问的玩家。</param>
    static void RequireCurrent(long playerId)
    {
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException($"玩家 {playerId} 的状态只能由该玩家队列中的动作访问或修改");
    }

    /// <summary>取出已经载入的条目。</summary>
    /// <param name="playerId">玩家主键。</param>
    Entry Required(long playerId)
    {
        if (!_entries.TryGetValue(playerId, out Entry? entry))
            throw new InvalidOperationException($"玩家 {playerId} 尚未载入缓存");
        return entry;
    }

    /// <summary>把版本加一。已经到达上限时拒绝，避免回绕。</summary>
    /// <param name="entry">要递增的缓存条目。</param>
    static void Bump(Entry entry)
    {
        if (entry.Revision == long.MaxValue)
            throw new InvalidOperationException("玩家版本已到上限");
        entry.Revision++;
    }

    /// <summary>一名玩家的缓存条目。对外只交出深拷贝。</summary>
    sealed class Entry
    {
        /// <summary>记下加载得到的那一份数据。调用方拿到的是它的拷贝。</summary>
        /// <param name="data">缓存持有的玩家数据。</param>
        /// <param name="revision">当前内存版本。</param>
        /// <param name="savedRevision">已确认保存的版本。</param>
        public Entry(PlayerFullData data, long revision, long savedRevision)
        {
            Data = data;
            Revision = revision;
            SavedRevision = savedRevision;
        }

        /// <summary>缓存持有的玩家数据。</summary>
        public PlayerFullData Data { get; }

        /// <summary>当前内存版本。</summary>
        public long Revision { get; set; }

        /// <summary>已确认保存的版本。</summary>
        public long SavedRevision { get; set; }

        /// <summary>尚未释放的活跃持有次数。</summary>
        public int Holds { get; set; }

        /// <summary>同时拷贝数据和两个版本。</summary>
        /// <param name="playerId">缓存键上的玩家主键。不从可变的 Info 里重新取值。</param>
        public PlayerState Capture(long playerId)
        {
            return new PlayerState(playerId, Data.Clone(), Revision, SavedRevision);
        }
    }

    /// <summary>一次持有。释放时回到缓存计数。</summary>
    sealed class Hold : IDisposable
    {
        readonly PlayerCache _cache;
        readonly long _playerId;
        int _released;

        /// <summary>记下要释放的缓存和玩家。</summary>
        /// <param name="cache">发出这次持有的缓存。</param>
        /// <param name="playerId">被持有的玩家。</param>
        public Hold(PlayerCache cache, long playerId)
        {
            _cache = cache;
            _playerId = playerId;
        }

        /// <summary>发出这次持有的缓存。保存服务用来拒绝其他对象。</summary>
        internal PlayerCache Cache => _cache;

        /// <summary>在该玩家队列中把持有计数减一。重复调用不再减少。错误上下文不会消耗句柄。</summary>
        public void Dispose()
        {
            lock (this)
            {
                if (_released != 0)
                    return;
                // 验证与释放成功后才消耗句柄，错误异步上下文调用不能泄漏持有计数。
                _cache.ReleaseHold(_playerId);
                _released = 1;
            }
        }

        /// <summary>保存路径直接减计数。不检查玩家队列。已经释放过则不再减少。</summary>
        internal void ReleaseMetadata()
        {
            lock (this)
            {
                if (_released != 0)
                    return;
                _cache.ReleaseHoldMetadata(_playerId);
                _released = 1;
            }
        }
    }
}
