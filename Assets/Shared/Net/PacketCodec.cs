using System;
using System.Buffers.Binary;

namespace CloudIsle.Net
{
    /// <summary>
    /// TCP 帧：len(4 LE) | msgId(2 LE) | seq(4 LE) | body
    /// len = msgId + seq + body 的字节数。
    /// </summary>
    public static class PacketCodec
    {
        public const int HeaderSize = 6; // msgId + seq
        public const int MaxBodySize = 64 * 1024;
        public const int MaxFramePayload = HeaderSize + MaxBodySize;

        public static byte[] Encode(ushort msgId, int seq, ReadOnlySpan<byte> body)
        {
            if (body.Length > MaxBodySize)
                throw new ArgumentOutOfRangeException(nameof(body), "body exceeds 64KB");

            int payloadLen = HeaderSize + body.Length;
            var buffer = new byte[4 + payloadLen];
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), payloadLen);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), msgId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6, 4), seq);
            if (body.Length > 0)
                body.CopyTo(buffer.AsSpan(10));
            return buffer;
        }

        /// <summary>
        /// 从缓冲中尝试拆出一帧。成功时 consumed 为消费字节数。
        /// </summary>
        public static bool TryDecode(
            ReadOnlySpan<byte> buffer,
            out ushort msgId,
            out int seq,
            out byte[] body,
            out int consumed)
        {
            msgId = 0;
            seq = 0;
            body = Array.Empty<byte>();
            consumed = 0;

            if (buffer.Length < 4)
                return false;

            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            if (payloadLen < HeaderSize || payloadLen > MaxFramePayload)
                throw new InvalidOperationException($"illegal frame len={payloadLen}");

            if (buffer.Length < 4 + payloadLen)
                return false;

            msgId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
            seq = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(6, 4));
            int bodyLen = payloadLen - HeaderSize;
            body = bodyLen == 0 ? Array.Empty<byte>() : buffer.Slice(10, bodyLen).ToArray();
            consumed = 4 + payloadLen;
            return true;
        }
    }
}
