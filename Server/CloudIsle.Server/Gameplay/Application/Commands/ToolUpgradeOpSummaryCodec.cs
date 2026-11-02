using System;
using System.IO;
using System.Text;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// ToolUpgrade 冻结业务摘要（cmdresult-v3 尾段）：原 toolId、升级后 level。
/// 不含 Ack/seq/Token/nextOp；与网络 ToolUpgradeResp.Tool 同源但独立编解码。
/// 不新增 cmdresult-v4；与 harvest/gather/farm-batch 摘要并存。
/// </summary>
public static class ToolUpgradeOpSummaryCodec
{
    public const string FormatVersionV1 = "tool-upgrade-summary-v1";

    public sealed class Summary
    {
        public Summary(int toolId, int levelAfter)
        {
            if (toolId <= 0)
                throw new ArgumentOutOfRangeException(nameof(toolId));
            if (levelAfter <= 0)
                throw new ArgumentOutOfRangeException(nameof(levelAfter));
            ToolId = toolId;
            LevelAfter = levelAfter;
        }

        public int ToolId { get; }
        public int LevelAfter { get; }
    }

    public static byte[] Encode(Summary summary)
    {
        if (summary == null)
            throw new ArgumentNullException(nameof(summary));
        using var ms = new MemoryStream(32);
        WriteLpUtf8(ms, FormatVersionV1);
        WriteI32(ms, summary.ToolId);
        WriteI32(ms, summary.LevelAfter);
        return ms.ToArray();
    }

    public static bool TryDecode(byte[] blob, out Summary? summary)
    {
        summary = null;
        if (blob == null || blob.Length < 8)
            return false;
        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            if (!string.Equals(ReadLpUtf8(ms), FormatVersionV1, StringComparison.Ordinal))
                return false;
            int toolId = ReadI32(ms);
            int level = ReadI32(ms);
            if (toolId <= 0 || level <= 0 || ms.Position != ms.Length)
                return false;
            summary = new Summary(toolId, level);
            return true;
        }
        catch
        {
            summary = null;
            return false;
        }
    }

    /// <summary>成功结算摘要：正 toolId、正 levelAfter。</summary>
    public static bool IsSuccessfulSettlement(Summary? summary)
        => summary != null && summary.ToolId > 0 && summary.LevelAfter > 0;

    static void WriteLpUtf8(Stream s, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? "");
        WriteI32(s, utf8.Length);
        s.Write(utf8, 0, utf8.Length);
    }

    static void WriteI32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        buf[0] = (byte)((value >> 24) & 0xff);
        buf[1] = (byte)((value >> 16) & 0xff);
        buf[2] = (byte)((value >> 8) & 0xff);
        buf[3] = (byte)(value & 0xff);
        s.Write(buf);
    }

    static string ReadLpUtf8(Stream s)
    {
        int n = ReadI32(s);
        if (n < 0 || n > 1024)
            throw new InvalidDataException("lp");
        var buf = new byte[n];
        int read = 0;
        while (read < n)
        {
            int r = s.Read(buf, read, n - read);
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }

        return Encoding.UTF8.GetString(buf);
    }

    static int ReadI32(Stream s)
    {
        Span<byte> buf = stackalloc byte[4];
        int off = 0;
        while (off < 4)
        {
            int r = s.Read(buf.Slice(off));
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }

        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }
}
