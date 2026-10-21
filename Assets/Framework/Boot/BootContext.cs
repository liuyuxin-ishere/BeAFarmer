using CloudIsle.Contracts;
using Game.Framework.Core;
using Game.Framework.Network;
using Game.Framework.Resource;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 传给生产热更入口的上下文。只含基包类型，不含热更窗口。
    /// </summary>
    public sealed class BootContext
    {
        /// <summary>资源管理器。InitializeAsync 已在 T13 实现，加载留在 T14。</summary>
        public ResMgr Resources;

        /// <summary>网络客户端。热更用它登录和重连。T18 之前为空壳。</summary>
        public NetClient Network;

        /// <summary>事件总线。Subscribe 与 Publish 已在 T09 实现。</summary>
        public EventBus Events;

        /// <summary>本次启动的版本。StartAsync 要求它非空，用来打日志并在后续任务里校验配置。</summary>
        public VersionManifest Versions;
    }
}
