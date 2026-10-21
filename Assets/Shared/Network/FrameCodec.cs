using System;
using System.Buffers.Binary;

namespace CloudIsle.Net
{
    /// <summary>
    /// 把 msgId、seq、body 装成一帧小端字节。
    /// 帧布局：len(4) + msgId(2) + seq(4) + body。
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>msgId 与 seq 的字节数。len = 该值 + body 长度。</summary>
        public const int HeaderSize = 6;

        /// <summary>body 允许的最大字节数，65536。</summary>
        public const int MaxBodyLength = 65536;

        /// <summary>len 的最大值，6 + 65536 = 65542。</summary>
        public const int MaxPayloadLength = HeaderSize + MaxBodyLength;

        /// <summary>整帧最大字节数，4 + 65542 = 65546。</summary>
        public const int MaxFrameLength = 4 + MaxPayloadLength;

        /// <summary>
        /// 校验 body 长度后写出完整帧。
        /// </summary>
        /// <param name="msgId">消息 ID，按小端写入 2 字节。</param>
        /// <param name="seq">请求序号；推送可以用 0。按小端写入 4 字节。</param>
        /// <param name="body">Protobuf 正文。长度 0 时整帧为 10 字节。空引用或超过 65536 时抛异常。</param>
        /// <returns>长度为 4 + 6 + body.Length 的新数组。</returns>
        public static byte[] Encode(ushort msgId, uint seq, byte[] body)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (body.Length > MaxBodyLength)
                throw new ArgumentOutOfRangeException(nameof(body), "body 超过 65536 字节");

            int payloadLength = HeaderSize + body.Length;
            byte[] frame = new byte[4 + payloadLength];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)payloadLength);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), msgId);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6, 4), seq);
            if (body.Length > 0)
                body.CopyTo(frame.AsSpan(10));
            return frame;
        }

        /// <summary>
        /// 从缓冲最前面尝试解出一帧。长度非法时返回 Invalid，数据不够时返回 NeedMoreData。
        /// </summary>
        /// <param name="buffer">当前已经收到的字节，可以含半包，也可以在一帧后面带着下一帧。</param>
        /// <param name="packet">Success 时为解出的包；其余状态为 default。</param>
        /// <param name="consumedBytes">Success 时为这一帧的字节数；NeedMoreData 和 Invalid 时为 0。</param>
        /// <returns>NeedMoreData、Success 或 Invalid。</returns>
        public static DecodeStatus TryDecode(ReadOnlySpan<byte> buffer, out Packet packet, out int consumedBytes)
        {
            packet = default;
            consumedBytes = 0;

            if (buffer.Length < 4)
                return DecodeStatus.NeedMoreData;

            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (payloadLength < HeaderSize || payloadLength > MaxPayloadLength)
                return DecodeStatus.Invalid;

            int frameLength = 4 + (int)payloadLength;
            if (buffer.Length < frameLength)
                return DecodeStatus.NeedMoreData;

            ushort msgId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(6, 4));
            int bodyLength = (int)payloadLength - HeaderSize;
            byte[] body = bodyLength == 0
                ? Array.Empty<byte>()
                : buffer.Slice(10, bodyLength).ToArray();

            packet = new Packet(msgId, seq, body);
            consumedBytes = frameLength;
            return DecodeStatus.Success;
        }
    }
}
