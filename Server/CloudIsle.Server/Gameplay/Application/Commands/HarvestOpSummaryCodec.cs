using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// Harvest 冻结业务摘要（cmdresult-v3 尾段）：原目标 plot、实际 gained、原 exp。
/// 不含 Ack/seq/Token/nextOp；与网络 HarvestResp 字段同源但独立编解码。
/// </summary>
public static class HarvestOpSummaryCodec
{
    public const string FormatVersionV1 = "harvest-summary-v1";

    public sealed class Summary
    {
        public Summary(int plotId, int plotResultCode, IReadOnlyList<ItemGain> gained, int expGained)
        {
            if (plotId <= 0)
                throw new ArgumentOutOfRangeException(nameof(plotId));
            if (plotResultCode != 0)
                throw new ArgumentOutOfRangeException(nameof(plotResultCode), "成功摘要 plotResultCode 须为 0");
            if (gained == null)
                throw new ArgumentNullException(nameof(gained));
            if (gained.Count == 0)
                throw new ArgumentException("成功摘要 gained 不可为空", nameof(gained));
            if (expGained < 0)
                throw new ArgumentOutOfRangeException(nameof(expGained));

            PlotId = plotId;
            PlotResultCode = plotResultCode;
            ExpGained = expGained;
            var arr = new ItemGain[gained.Count];
            for (int i = 0; i < gained.Count; i++)
                arr[i] = gained[i];
            Gained = Array.AsReadOnly(arr);
        }

        public int PlotId { get; }
        public int PlotResultCode { get; }
        public IReadOnlyList<ItemGain> Gained { get; }
        public int ExpGained { get; }
    }

    public readonly struct ItemGain
    {
        public ItemGain(int itemId, int count)
        {
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId));
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
    }

    public static byte[] Encode(Summary summary)
    {
        if (summary == null)
            throw new ArgumentNullException(nameof(summary));
        using var ms = new MemoryStream(64);
        WriteLpUtf8(ms, FormatVersionV1);
        WriteI32(ms, summary.PlotId);
        WriteI32(ms, summary.PlotResultCode);
        WriteI32(ms, summary.Gained.Count);
        for (int i = 0; i < summary.Gained.Count; i++)
        {
            WriteI32(ms, summary.Gained[i].ItemId);
            WriteI32(ms, summary.Gained[i].Count);
        }

        WriteI32(ms, summary.ExpGained);
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
            int plotId = ReadI32(ms);
            int plotCode = ReadI32(ms);
            int n = ReadI32(ms);
            // 读盘恶坏输入：缺项/空 gained/非 0 plotResult 明确失败，不得靠 Encode 参数限制绕过。
            if (plotId <= 0 || plotCode != 0 || n <= 0 || n > 64)
                return false;
            var gained = new ItemGain[n];
            for (int i = 0; i < n; i++)
                gained[i] = new ItemGain(ReadI32(ms), ReadI32(ms));
            int exp = ReadI32(ms);
            if (exp < 0 || ms.Position != ms.Length)
                return false;
            summary = new Summary(plotId, plotCode, gained, exp);
            return true;
        }
        catch
        {
            summary = null;
            return false;
        }
    }

    /// <summary>成功结算摘要：正 plot、plotResultCode0、非空 gained、非负 exp。</summary>
    public static bool IsSuccessfulSettlement(Summary? summary)
    {
        if (summary == null || summary.PlotId <= 0 || summary.PlotResultCode != 0)
            return false;
        if (summary.ExpGained < 0 || summary.Gained == null || summary.Gained.Count == 0)
            return false;
        for (int i = 0; i < summary.Gained.Count; i++)
        {
            if (summary.Gained[i].ItemId <= 0 || summary.Gained[i].Count <= 0)
                return false;
        }

        return true;
    }

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
