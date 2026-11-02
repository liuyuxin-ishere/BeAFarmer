using System;
using System.IO;
using System.Text;

namespace CloudIsle.Commands
{
    /// <summary>长度定界的规范业务字段写入器（大端）；C#9 / netstandard2.1。</summary>
    public sealed class CanonicalBusinessWriter
    {
        readonly MemoryStream _ms = new MemoryStream(256);

        public void WriteTag(string name)
        {
            WriteLpUtf8(name);
        }

        public void WriteI32(int value)
        {
            byte[] buf = new byte[4];
            buf[0] = (byte)((value >> 24) & 0xff);
            buf[1] = (byte)((value >> 16) & 0xff);
            buf[2] = (byte)((value >> 8) & 0xff);
            buf[3] = (byte)(value & 0xff);
            _ms.Write(buf, 0, 4);
        }

        public void WriteI64(long value)
        {
            byte[] buf = new byte[8];
            buf[0] = (byte)((value >> 56) & 0xff);
            buf[1] = (byte)((value >> 48) & 0xff);
            buf[2] = (byte)((value >> 40) & 0xff);
            buf[3] = (byte)((value >> 32) & 0xff);
            buf[4] = (byte)((value >> 24) & 0xff);
            buf[5] = (byte)((value >> 16) & 0xff);
            buf[6] = (byte)((value >> 8) & 0xff);
            buf[7] = (byte)(value & 0xff);
            _ms.Write(buf, 0, 8);
        }

        public void WriteBool(bool value) => _ms.WriteByte(value ? (byte)1 : (byte)0);

        public void WriteLpUtf8(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
            WriteI32(utf8.Length);
            _ms.Write(utf8, 0, utf8.Length);
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
