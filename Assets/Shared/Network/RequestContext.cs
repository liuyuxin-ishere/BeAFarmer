namespace CloudIsle.Net
{
    /// <summary>
    /// 服务端分发一条消息时带上的调用上下文。
    /// 未认证时 PlayerId 为 0。这里不放密码、Token 或正文。
    /// </summary>
    public sealed class RequestContext
    {
        /// <summary>
        /// 记录这一次分发属于哪条连接、哪个玩家、哪条消息。
        /// </summary>
        /// <param name="sessionId">连接会话编号。</param>
        /// <param name="playerId">已认证玩家编号。未认证传 0。</param>
        /// <param name="msgId">本帧消息 ID。</param>
        /// <param name="seq">本帧序号。</param>
        public RequestContext(long sessionId, long playerId, ushort msgId, uint seq)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            MsgId = msgId;
            Seq = seq;
        }

        /// <summary>连接会话编号。</summary>
        public long SessionId { get; }

        /// <summary>已认证玩家编号。未认证为 0。</summary>
        public long PlayerId { get; }

        /// <summary>本帧消息 ID。</summary>
        public ushort MsgId { get; }

        /// <summary>本帧序号。</summary>
        public uint Seq { get; }
    }
}
