using YooAsset;

namespace Game.Framework.Resource
{
    /// <summary>
    /// 一份已加载场景的持有记录。由 ResMgr.LoadSceneAsync 创建，由 ResMgr.UnloadSceneAsync 卸载。
    /// </summary>
    public sealed class SceneLease
    {
        readonly SceneHandle _handle;

        /// <param name="handle">本次场景加载对应的 YooAsset 句柄。</param>
        /// <param name="address">加载时传入的地址。</param>
        internal SceneLease(SceneHandle handle, string address)
        {
            _handle = handle;
            Address = address;
            IsLoaded = true;
        }

        /// <summary>加载时传入的地址。</summary>
        public string Address { get; }

        /// <summary>场景是否仍由这份租约持有。</summary>
        public bool IsLoaded { get; private set; }

        internal SceneHandle Handle
        {
            get { return _handle; }
        }

        internal void MarkUnloaded()
        {
            IsLoaded = false;
        }
    }
}
