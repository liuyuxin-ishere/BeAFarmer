using System;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 玩法命令端口。T007 绑定面；T013 实现持久 CommandClient；T015 SyncState 委托 C06。
    /// </summary>
    public interface IGameplayCommandPort
    {
        /// <summary>
        /// 发送已校验意图。T013 负责落盘/编码；T007 假端口可直接编码下发测试。
        /// done 必须由调用方在主线程经 Registry 交付。
        /// </summary>
        void Send(CommandIntent intent, Action<int, string> done);

        /// <summary>全量同步；合并执行属 C06，done 只返回已应用 revision/错误。</summary>
        void SyncState(Action<int, string> done);

        /// <summary>订阅服务端增量（内部）。返回退订。生产由 DeltaInbox 独占传输订阅。</summary>
        Action SubscribeDelta(Action<string> onDeltaJson);
    }

    /// <summary>
    /// 模型写端口：唯一资产写者；C06/账号适配器注入；调用 Lua ModelStore。
    /// </summary>
    public interface IGameplayModelPort
    {
        /// <summary>清除已安装模型（失败/踢线/登出）。</summary>
        void Clear(SessionStamp stamp, string reason);

        /// <summary>全量替换；成功 Applied；陈旧 Duplicate；坏 DTO Invalid。</summary>
        ModelApplyResult ReplaceSnapshot(SnapshotEnvelope snapshot, SessionStamp stamp);

        /// <summary>增量应用；Applied/Duplicate/Gap/Invalid。</summary>
        ModelApplyResult ApplyBatch(DeltaBatchDto batch, SessionStamp stamp);

        /// <summary>
        /// 登录安装兼容：等价 ReplaceSnapshot。失败不得留下半安装状态。
        /// </summary>
        bool TryInstallSnapshot(SessionStamp stamp, string snapshotJson, out long revision, out string error);

        /// <summary>当前已安装 revision；未安装为 0。</summary>
        long GetRevision(SessionStamp stamp);
    }

    /// <summary>无 LuaEnv 时的空模型端口：安装失败，阻止 MarkReady。</summary>
    public sealed class NullGameplayModelPort : IGameplayModelPort
    {
        public void Clear(SessionStamp stamp, string reason)
        {
        }

        public ModelApplyResult ReplaceSnapshot(SnapshotEnvelope snapshot, SessionStamp stamp)
        {
            return ModelApplyResult.Invalid(0, "model-port-missing");
        }

        public ModelApplyResult ApplyBatch(DeltaBatchDto batch, SessionStamp stamp)
        {
            return ModelApplyResult.Invalid(0, "model-port-missing");
        }

        public bool TryInstallSnapshot(SessionStamp stamp, string snapshotJson, out long revision, out string error)
        {
            revision = 0;
            error = "model-port-missing";
            return false;
        }

        public long GetRevision(SessionStamp stamp) => 0;
    }
}
