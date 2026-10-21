namespace CloudIsle.Net
{
    /// <summary>
    /// 一帧解出的消息。Body 在构造成功时拷贝为独立数组。
    /// </summary>
    public readonly struct Packet
    {
        /// <summary>消息 ID。</summary>
        public ushort MsgId { get; }

        /// <summary>请求序号。推送帧可以是 0。</summary>
        public uint Seq { get; }

        /// <summary>Protobuf 正文的独立副本。空正文是长度为 0 的数组。</summary>
        public byte[] Body { get; }

        /// <summary>
        /// 保存已经拷贝好的正文。
        /// </summary>
        /// <param name="msgId">从帧里读出的消息 ID。</param>
        /// <param name="seq">从帧里读出的序号。</param>
        /// <param name="body">调用方已经从缓冲拷出的正文。空引用会抛异常。</param>
        public Packet(ushort msgId, uint seq, byte[] body)
        {
            if (body == null)
                throw new System.ArgumentNullException(nameof(body));
            MsgId = msgId;
            Seq = seq;
            Body = body;
        }
    }
}
