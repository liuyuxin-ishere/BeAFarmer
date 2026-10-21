namespace Game.Framework.Resource
{
    /// <summary>
    /// 某一持有者在某一地址上尚未释放的租约汇总。只描述现状，不触发释放。
    /// </summary>
    public sealed class ResourceLeakInfo
    {
        /// <param name="address">资源地址。</param>
        /// <param name="owner">持有者。</param>
        /// <param name="refCount">该地址上此持有者尚未释放的租约份数。</param>
        /// <param name="createdAtMs">这组租约里最早一份的登记时间，UTC Unix 毫秒。</param>
        internal ResourceLeakInfo(string address, string owner, int refCount, long createdAtMs)
        {
            Address = address;
            Owner = owner;
            RefCount = refCount;
            CreatedAtMs = createdAtMs;
        }

        /// <summary>资源地址。</summary>
        public string Address { get; }

        /// <summary>持有者。</summary>
        public string Owner { get; }

        /// <summary>该地址上此持有者尚未释放的租约份数。</summary>
        public int RefCount { get; }

        /// <summary>这组租约里最早一份的登记时间，UTC Unix 毫秒。</summary>
        public long CreatedAtMs { get; }
    }
}
