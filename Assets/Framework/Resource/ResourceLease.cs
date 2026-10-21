using System;
using YooAsset;

namespace Game.Framework.Resource
{
    /// <summary>
    /// 一份已加载资源的持有记录。由 ResMgr.LoadAsync 创建。
    /// 若调用方已经 Instantiate，先销毁实例，再 Dispose 这份 Prefab 租约。
    /// </summary>
    public sealed class ResourceLease<T> : IDisposable where T : UnityEngine.Object
    {
        readonly AssetHandle _handle;
        Action _onReleased;
        T _asset;

        /// <param name="handle">本次加载对应的 YooAsset 句柄。Dispose 时只释放这一份。</param>
        /// <param name="asset">加载到的资源。</param>
        /// <param name="address">加载时传入的地址。</param>
        /// <param name="owner">持有者名称。</param>
        /// <param name="onReleased">第一次 Dispose 时通知 ResMgr 从泄漏名单移除。只调用一次。</param>
        internal ResourceLease(AssetHandle handle, T asset, string address, string owner, Action onReleased)
        {
            _handle = handle;
            _asset = asset;
            _onReleased = onReleased;
            Address = address;
            Owner = owner;
        }

        /// <summary>加载到的资源。Dispose 之后为 null。</summary>
        public T Asset
        {
            get { return _asset; }
        }

        /// <summary>加载时传入的地址。</summary>
        public string Address { get; }

        /// <summary>持有者。泄漏报告用它区分是谁没释放。</summary>
        public string Owner { get; }

        /// <summary>是否已经释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>释放这一份句柄。重复调用直接返回。</summary>
        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            _asset = null;
            Action onReleased = _onReleased;
            _onReleased = null;
            try
            {
                if (onReleased != null)
                    onReleased();
            }
            finally
            {
                _handle.Release();
            }
        }
    }
}
