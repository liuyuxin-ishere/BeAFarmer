using System;
using System.Globalization;
using CloudIsle.Proto;
using Google.Protobuf;

namespace Game.Framework.Lua
{
    /// <summary>
    /// 正式 C# Protobuf 与 Lua 之间的 bytes / int64 桥。Lua 不改消息布局。
    /// 整数契约是 Lua 5.3 的 64 位整数，不用 double。
    /// </summary>
    public static class ProtoBridge
    {
        /// <summary>超过 double 精确整数范围的探测值，用来证明 int64 未被截断。</summary>
        public const long BigInt64 = 9007199254740993L;

        /// <summary>与活动索引一起写入的桥接契约名。</summary>
        public const string Contract = "lua53-integer";

        /// <summary>构造 MsgId 1001 HeartbeatReq，字段 1 client_time 使用大整数。</summary>
        public static byte[] BuildHeartbeatBytes()
        {
            var message = new HeartbeatReq { ClientTime = BigInt64 };
            return message.ToByteArray();
        }

        /// <summary>构造正式 AvatarData：字段 1 owned（repeated），字段 2 equipped（map）。</summary>
        public static byte[] BuildAvatarBytes()
        {
            var avatar = new AvatarData();
            avatar.Owned.Add(3);
            avatar.Owned.Add(5);
            avatar.Equipped[1] = 11;
            avatar.Equipped[4] = 22;
            return avatar.ToByteArray();
        }

        /// <summary>从心跳 bytes 读出字段 1。返回值经生成桥接进入 Lua。</summary>
        /// <param name="payload">HeartbeatReq 编码。</param>
        public static long ReadHeartbeatClientTime(byte[] payload)
        {
            var message = HeartbeatReq.Parser.ParseFrom(payload);
            return message.ClientTime;
        }

        /// <summary>把 Lua 侧整数原样送回。value 必须仍是完整 int64。</summary>
        public static long EchoInt64(long value)
        {
            return value;
        }

        /// <summary>原样回传 Protobuf bytes。</summary>
        /// <param name="payload">正式消息编码；null 当空数组。</param>
        public static byte[] EchoBytes(byte[] payload)
        {
            if (payload == null)
                return Array.Empty<byte>();

            var copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            return copy;
        }

        /// <summary>
        /// 校验回传结果。heartbeat/avatar 是 Lua 回传的原始 bytes，echoed 是跨 Lua 后的 int64。
        /// 成功返回 ok，否则返回原因。
        /// </summary>
        public static string VerifyAll(byte[] heartbeat, long echoed, byte[] avatar)
        {
            if (HeartbeatReq.ClientTimeFieldNumber != 1 || (int)MsgId.HeartbeatReq != 1001)
                return "heartbeat field or msgid changed";

            if (AvatarData.OwnedFieldNumber != 1 || AvatarData.EquippedFieldNumber != 2)
                return "avatar field numbers changed";

            if (echoed != BigInt64)
                return "int64 mismatch " + echoed.ToString(CultureInfo.InvariantCulture);

            var heartbeatMessage = HeartbeatReq.Parser.ParseFrom(heartbeat);
            if (heartbeatMessage.ClientTime != BigInt64)
                return "heartbeat bytes mismatch";

            if (!BytesEqual(BuildHeartbeatBytes(), heartbeat))
                return "heartbeat wire bytes changed";

            var avatarMessage = AvatarData.Parser.ParseFrom(avatar);
            if (avatarMessage.Owned.Count != 2 || avatarMessage.Owned[0] != 3 || avatarMessage.Owned[1] != 5)
                return "repeated owned mismatch";

            if (!avatarMessage.Equipped.ContainsKey(1) || avatarMessage.Equipped[1] != 11
                || !avatarMessage.Equipped.ContainsKey(4) || avatarMessage.Equipped[4] != 22)
                return "map equipped mismatch";

            if (!BytesEqual(BuildAvatarBytes(), avatar))
                return "avatar wire bytes changed";

            return "ok";
        }

        /// <summary>逐字节比较。任一为 null 或长度不同则 false。</summary>
        static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }
    }
}
