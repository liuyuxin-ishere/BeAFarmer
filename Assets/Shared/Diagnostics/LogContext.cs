namespace CloudIsle.Diagnostics
{
    /// <summary>
    /// 附在一条日志上的数字上下文。不放密码，也不放 Token。
    /// </summary>
    public readonly struct LogContext
    {
        /// <summary>玩家编号。没有玩家时为 0。</summary>
        public long PlayerId { get; }

        /// <summary>相关消息 ID。没有消息时为 0。</summary>
        public ushort MsgId { get; }

        /// <summary>请求序号。</summary>
        public uint Seq { get; }

        /// <summary>幂等操作编号。没有操作时为 0。</summary>
        public long OpId { get; }

        /// <summary>这步耗时，单位毫秒。</summary>
        public double ElapsedMs { get; }

        /// <summary>
        /// 记录一次日志要带上的数字。
        /// </summary>
        /// <param name="playerId">玩家编号。没有玩家时为 0。</param>
        /// <param name="msgId">相关消息 ID。没有消息时为 0。</param>
        /// <param name="seq">请求序号。</param>
        /// <param name="opId">幂等操作编号。没有操作时为 0。</param>
        /// <param name="elapsedMs">这步耗时，单位毫秒。</param>
        public LogContext(long playerId, ushort msgId, uint seq, long opId, double elapsedMs)
        {
            PlayerId = playerId;
            MsgId = msgId;
            Seq = seq;
            OpId = opId;
            ElapsedMs = elapsedMs;
        }
    }
}
