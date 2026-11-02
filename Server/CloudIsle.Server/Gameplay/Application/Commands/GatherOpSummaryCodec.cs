using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// Gather 冻结业务摘要（cmdresult-v3 尾段）：原 node、gained、rareHit、exp、结算 nextReady。
/// 不含 Ack/seq/Token/nextOp；与网络 GatherResp 字段同源但独立编解码。
/// 不新增 cmdresult-v4；旧 v1/v2/v3 与 harvest/farm-batch 摘要并存兼容。
/// </summary>
public static class GatherOpSummaryCodec
{
    public const string FormatVersionV1 = "gather-summary-v1";

    public sealed class Summary
    {
        public Summary(
            int nodeInstanceId,
            bool rareHit,
            IReadOnlyList<ItemGain> gained,
            int expGained,
            long nextReadyTime)
        {
            if (nodeInstanceId <= 0)
                throw new ArgumentOutOfRangeException(nameof(nodeInstanceId));
            if (gained == null)
                throw new ArgumentNullException(nameof(gained));
            if (gained.Count == 0)
                throw new ArgumentException("成功摘要 gained 不可为空", nameof(gained));
            if (expGained < 0)
                throw new ArgumentOutOfRangeException(nameof(expGained));
            if (nextReadyTime < 0)
                throw new ArgumentOutOfRangeException(nameof(nextReadyTime));
            if (rareHit && gained.Count < 2)
                throw new ArgumentException("rareHit 须含普通+稀有 gained", nameof(gained));
            if (!rareHit && gained.Count != 1)
                throw new ArgumentException("非稀有成功摘要 gained 恰一项", nameof(gained));

            NodeInstanceId = nodeInstanceId;
            RareHit = rareHit;
            ExpGained = expGained;
            NextReadyTime = nextReadyTime;
            var arr = new ItemGain[gained.Count];
            for (int i = 0; i < gained.Count; i++)
                arr[i] = gained[i];
            Gained = Array.AsReadOnly(arr);
        }

        public int NodeInstanceId { get; }
        public bool RareHit { get; }
        public IReadOnlyList<ItemGain> Gained { get; }
        public int ExpGained { get; }
        public long NextReadyTime { get; }
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
        WriteI32(ms, summary.NodeInstanceId);
        WriteI32(ms, summary.RareHit ? 1 : 0);
        WriteI32(ms, summary.Gained.Count);
        for (int i = 0; i < summary.Gained.Count; i++)
        {
            WriteI32(ms, summary.Gained[i].ItemId);
            WriteI32(ms, summary.Gained[i].Count);
        }

        WriteI32(ms, summary.ExpGained);
        WriteI64(ms, summary.NextReadyTime);
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
            int nodeId = ReadI32(ms);
            int rareFlag = ReadI32(ms);
            if (rareFlag != 0 && rareFlag != 1)
                return false;
            bool rareHit = rareFlag == 1;
            int n = ReadI32(ms);
            if (nodeId <= 0 || n <= 0 || n > 64)
                return false;
            if (rareHit && n < 2)
                return false;
            if (!rareHit && n != 1)
                return false;
            var gained = new ItemGain[n];
            for (int i = 0; i < n; i++)
                gained[i] = new ItemGain(ReadI32(ms), ReadI32(ms));
            int exp = ReadI32(ms);
            long nextReady = ReadI64(ms);
            if (exp < 0 || nextReady < 0 || ms.Position != ms.Length)
                return false;
            summary = new Summary(nodeId, rareHit, gained, exp, nextReady);
            return true;
        }
        catch
        {
            summary = null;
            return false;
        }
    }

    /// <summary>成功结算摘要：正 node、非空 gained、非负 exp/nextReady、rare 与 gained 计数一致。</summary>
    public static bool IsSuccessfulSettlement(Summary? summary)
    {
        if (summary == null || summary.NodeInstanceId <= 0)
            return false;
        if (summary.ExpGained < 0 || summary.NextReadyTime < 0)
            return false;
        if (summary.Gained == null || summary.Gained.Count == 0)
            return false;
        if (summary.RareHit && summary.Gained.Count < 2)
            return false;
        if (!summary.RareHit && summary.Gained.Count != 1)
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

    static void WriteI64(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        buf[0] = (byte)((value >> 56) & 0xff);
        buf[1] = (byte)((value >> 48) & 0xff);
        buf[2] = (byte)((value >> 40) & 0xff);
        buf[3] = (byte)((value >> 32) & 0xff);
        buf[4] = (byte)((value >> 24) & 0xff);
        buf[5] = (byte)((value >> 16) & 0xff);
        buf[6] = (byte)((value >> 8) & 0xff);
        buf[7] = (byte)(value & 0xff);
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

    static long ReadI64(Stream s)
    {
        Span<byte> buf = stackalloc byte[8];
        int off = 0;
        while (off < 8)
        {
            int r = s.Read(buf.Slice(off));
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }

        long value = 0;
        for (int i = 0; i < 8; i++)
            value = (value << 8) | buf[i];
        return value;
    }
}
