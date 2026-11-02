using System;
using XLua;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 兼容保留：委托 <see cref="LuaModelStorePort"/>。新代码请直接使用 ModelStore 端口。
    /// </summary>
    internal sealed class LuaPlayerModelPort : IGameplayModelPort
    {
        readonly LuaModelStorePort _inner;

        public LuaPlayerModelPort(LuaEnv env, IMainThreadDispatcher dispatcher)
        {
            _inner = new LuaModelStorePort(env, dispatcher);
        }

        public void Clear(SessionStamp stamp, string reason) => _inner.Clear(stamp, reason);

        public ModelApplyResult ReplaceSnapshot(SnapshotEnvelope snapshot, SessionStamp stamp)
            => _inner.ReplaceSnapshot(snapshot, stamp);

        public ModelApplyResult ApplyBatch(DeltaBatchDto batch, SessionStamp stamp)
            => _inner.ApplyBatch(batch, stamp);

        public bool TryInstallSnapshot(SessionStamp stamp, string snapshotJson, out long revision, out string error)
            => _inner.TryInstallSnapshot(stamp, snapshotJson, out revision, out error);

        public long GetRevision(SessionStamp stamp) => _inner.GetRevision(stamp);
    }
}
