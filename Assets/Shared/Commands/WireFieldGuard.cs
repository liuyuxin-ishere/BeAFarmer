using System;
using Google.Protobuf;

namespace CloudIsle.Commands
{
    /// <summary>正式写命令禁止未知 wire 字段（含嵌套）。</summary>
    public static class WireFieldGuard
    {
        public static void RejectUnknownWireFields(IMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            byte[] original = message.ToByteArray();
            MessageParser parser = message.Descriptor.Parser.WithDiscardUnknownFields(true);
            IMessage stripped = parser.ParseFrom(original);
            byte[] knownOnly = stripped.ToByteArray();
            if (!ByteArraysEqual(original, knownOnly))
                throw new ArgumentException("unknown wire fields are not allowed on formal write commands");
        }

        public static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
