using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CloudIsle.Commands
{
    /// <summary>
    /// 规范命令摘要纯函数（cmdhash-v1）。netstandard2.1 / C#9：使用 SHA256.Create().ComputeHash。
    /// 不含 transport seq / Token / player / scope / op。
    /// </summary>
    public static class CmdHashV1
    {
        public const string FormatVersion = "cmdhash-v1";
        public const int HexLength = 64;

        /// <summary>返回 64 位小写 hex SHA-256。</summary>
        public static string ComputeHex(
            string kind,
            long expectedRevision,
            int configVersion,
            byte[] businessPayload)
        {
            if (string.IsNullOrEmpty(kind))
                throw new ArgumentException("kind required", nameof(kind));
            if (expectedRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedRevision));
            if (configVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(configVersion));
            byte[] business = businessPayload ?? Array.Empty<byte>();

            using (var ms = new MemoryStream(256 + business.Length))
            {
                WriteLpString(ms, FormatVersion);
                WriteLpString(ms, kind);
                WriteI64(ms, expectedRevision);
                WriteI32(ms, configVersion);
                WriteLpBytes(ms, business);
                byte[] digest = Hash(ms.ToArray());
                return ToLowerHex(digest);
            }
        }

        static byte[] Hash(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        static void WriteLpString(Stream s, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
            WriteLpBytes(s, utf8);
        }

        static void WriteLpBytes(Stream s, byte[] bytes)
        {
            WriteI32(s, bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }

        static void WriteI32(Stream s, int value)
        {
            byte[] buf = new byte[4];
            buf[0] = (byte)((value >> 24) & 0xff);
            buf[1] = (byte)((value >> 16) & 0xff);
            buf[2] = (byte)((value >> 8) & 0xff);
            buf[3] = (byte)(value & 0xff);
            s.Write(buf, 0, 4);
        }

        static void WriteI64(Stream s, long value)
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
            s.Write(buf, 0, 8);
        }

        static string ToLowerHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
