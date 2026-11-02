using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Reward;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 批量冻结业务摘要（cmdresult-v3 尾段）：farm-batch-summary-v1。
/// 与 T033 harvest-summary-v1 并列；不含 Ack/seq/Token/nextOp。
/// </summary>
public static class FarmBatchSummaryCodec
{
    public const string FormatVersionV1 = "farm-batch-summary-v1";
    public const int KindPlant = 1;
    public const int KindHarvest = 2;

    public sealed class ItemGain
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

    public sealed class ItemResult
    {
        public ItemResult(int plotId, int code, IReadOnlyList<ItemGain> gained, int expGained)
        {
            if (plotId <= 0)
                throw new ArgumentOutOfRangeException(nameof(plotId));
            if (expGained < 0)
                throw new ArgumentOutOfRangeException(nameof(expGained));
            if (gained == null)
                throw new ArgumentNullException(nameof(gained));
            PlotId = plotId;
            Code = code;
            ExpGained = expGained;
            var arr = new ItemGain[gained.Count];
            for (int i = 0; i < gained.Count; i++)
                arr[i] = gained[i];
            Gained = Array.AsReadOnly(arr);
        }

        public int PlotId { get; }
        public int Code { get; }
        public IReadOnlyList<ItemGain> Gained { get; }
        public int ExpGained { get; }
    }

    public sealed class Summary
    {
        public Summary(
            int kind,
            IReadOnlyList<ItemResult> items,
            IReadOnlyList<ItemGain> aggregateGained,
            int aggregateExp)
        {
            if (kind != KindPlant && kind != KindHarvest)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (items == null || items.Count < 2 || items.Count > FarmBatchNormalizer.DefaultMaxItems)
                throw new ArgumentException("batch summary requires 2..24 items", nameof(items));
            if (aggregateGained == null)
                throw new ArgumentNullException(nameof(aggregateGained));
            if (aggregateExp < 0)
                throw new ArgumentOutOfRangeException(nameof(aggregateExp));

            Kind = kind;
            AggregateExp = aggregateExp;
            var itemArr = new ItemResult[items.Count];
            int prev = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemResult row = items[i] ?? throw new ArgumentException("null item", nameof(items));
                if (row.PlotId <= prev)
                    throw new ArgumentException("items must be strictly ascending plotId", nameof(items));
                if (kind == KindPlant)
                {
                    if (row.Gained.Count != 0 || row.ExpGained != 0)
                        throw new ArgumentException("plant item must have empty gains/exp", nameof(items));
                }
                else if (row.Code == 0)
                {
                    if (row.Gained.Count == 0)
                        throw new ArgumentException("harvest success item requires gains", nameof(items));
                }
                else if (row.Gained.Count != 0 || row.ExpGained != 0)
                {
                    throw new ArgumentException("harvest failure item must have empty gains/exp", nameof(items));
                }

                prev = row.PlotId;
                itemArr[i] = row;
            }

            var aggArr = new ItemGain[aggregateGained.Count];
            for (int i = 0; i < aggregateGained.Count; i++)
                aggArr[i] = aggregateGained[i];

            Items = Array.AsReadOnly(itemArr);
            AggregateGained = Array.AsReadOnly(aggArr);
        }

        public int Kind { get; }
        public IReadOnlyList<ItemResult> Items { get; }
        public IReadOnlyList<ItemGain> AggregateGained { get; }
        public int AggregateExp { get; }
    }

    public static byte[] Encode(Summary summary)
    {
        if (summary == null)
            throw new ArgumentNullException(nameof(summary));
        using var ms = new MemoryStream(128);
        WriteLpUtf8(ms, FormatVersionV1);
        WriteI32(ms, summary.Kind);
        WriteI32(ms, summary.Items.Count);
        for (int i = 0; i < summary.Items.Count; i++)
        {
            ItemResult row = summary.Items[i];
            WriteI32(ms, row.PlotId);
            WriteI32(ms, row.Code);
            WriteI32(ms, row.Gained.Count);
            for (int g = 0; g < row.Gained.Count; g++)
            {
                WriteI32(ms, row.Gained[g].ItemId);
                WriteI32(ms, row.Gained[g].Count);
            }

            WriteI32(ms, row.ExpGained);
        }

        WriteI32(ms, summary.AggregateGained.Count);
        for (int i = 0; i < summary.AggregateGained.Count; i++)
        {
            WriteI32(ms, summary.AggregateGained[i].ItemId);
            WriteI32(ms, summary.AggregateGained[i].Count);
        }

        WriteI32(ms, summary.AggregateExp);
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
            int kind = ReadI32(ms);
            int n = ReadI32(ms);
            if ((kind != KindPlant && kind != KindHarvest)
                || n < 2
                || n > FarmBatchNormalizer.DefaultMaxItems)
                return false;

            var items = new ItemResult[n];
            int prev = 0;
            for (int i = 0; i < n; i++)
            {
                int plotId = ReadI32(ms);
                int code = ReadI32(ms);
                int gainN = ReadI32(ms);
                if (plotId <= 0 || plotId <= prev || gainN < 0 || gainN > 64)
                    return false;
                prev = plotId;
                var gains = new ItemGain[gainN];
                for (int g = 0; g < gainN; g++)
                    gains[g] = new ItemGain(ReadI32(ms), ReadI32(ms));
                int exp = ReadI32(ms);
                if (exp < 0)
                    return false;
                if (kind == KindPlant && (gainN != 0 || exp != 0))
                    return false;
                if (kind == KindHarvest)
                {
                    if (code == 0 && gainN <= 0)
                        return false;
                    if (code != 0 && (gainN != 0 || exp != 0))
                        return false;
                }

                items[i] = new ItemResult(plotId, code, gains, exp);
            }

            int aggN = ReadI32(ms);
            if (aggN < 0 || aggN > 64)
                return false;
            var agg = new ItemGain[aggN];
            for (int i = 0; i < aggN; i++)
                agg[i] = new ItemGain(ReadI32(ms), ReadI32(ms));
            int aggExp = ReadI32(ms);
            if (aggExp < 0 || ms.Position != ms.Length)
                return false;
            if (kind == KindPlant && (aggN != 0 || aggExp != 0))
                return false;
            if (!AggregateMatchesSuccessItems(kind, items, agg, aggExp))
                return false;

            summary = new Summary(kind, items, agg, aggExp);
            return true;
        }
        catch
        {
            summary = null;
            return false;
        }
    }

    public static bool IsValidBatchSummary(Summary? summary)
        => summary != null
           && (summary.Kind == KindPlant || summary.Kind == KindHarvest)
           && summary.Items.Count >= 2
           && summary.Items.Count <= FarmBatchNormalizer.DefaultMaxItems;

    /// <summary>自 BatchDomainResult 编码；Plant 聚合空；Harvest 用已 checked 聚合。</summary>
    public static byte[] EncodeFromBatch(FarmBatchKind kind, BatchDomainResult batch)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));
        if (batch.Items.Count < 2 || batch.Items.Count > FarmBatchNormalizer.DefaultMaxItems)
            throw new ArgumentException("batch summary requires 2..24 items", nameof(batch));

        int wireKind = kind == FarmBatchKind.Plant ? KindPlant : KindHarvest;
        var items = new ItemResult[batch.Items.Count];
        for (int i = 0; i < batch.Items.Count; i++)
        {
            FarmBatchItemResult row = batch.Items[i];
            ItemGain[] gains;
            if (kind == FarmBatchKind.Harvest && row.Code == 0 && row.YieldItemId > 0 && row.YieldCount > 0)
                gains = new[] { new ItemGain(row.YieldItemId, row.YieldCount) };
            else
                gains = Array.Empty<ItemGain>();
            items[i] = new ItemResult(row.PlotId, row.Code, gains, kind == FarmBatchKind.Harvest && row.Code == 0 ? row.ExpGained : 0);
        }

        ItemGain[] agg;
        if (kind == FarmBatchKind.Harvest)
        {
            agg = new ItemGain[batch.AggregateGained.Count];
            for (int i = 0; i < batch.AggregateGained.Count; i++)
            {
                ItemAmount a = batch.AggregateGained[i];
                agg[i] = new ItemGain(a.ItemId, checked((int)a.Amount));
            }
        }
        else
            agg = Array.Empty<ItemGain>();

        int aggExp = kind == FarmBatchKind.Harvest ? batch.AggregateExp : 0;
        return Encode(new Summary(wireKind, items, agg, aggExp));
    }

    static bool AggregateMatchesSuccessItems(
        int kind,
        ItemResult[] items,
        ItemGain[] aggregate,
        int aggregateExp)
    {
        if (kind == KindPlant)
            return aggregate.Length == 0 && aggregateExp == 0;

        var map = new SortedDictionary<int, long>();
        long exp = 0;
        for (int i = 0; i < items.Length; i++)
        {
            ItemResult row = items[i];
            if (row.Code != 0)
                continue;
            try
            {
                exp = checked(exp + row.ExpGained);
                for (int g = 0; g < row.Gained.Count; g++)
                {
                    ItemGain gain = row.Gained[g];
                    if (!map.TryGetValue(gain.ItemId, out long cur))
                        cur = 0;
                    map[gain.ItemId] = checked(cur + gain.Count);
                }
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (exp != aggregateExp || exp > int.MaxValue)
            return false;
        if (map.Count != aggregate.Length)
            return false;
        int idx = 0;
        foreach (KeyValuePair<int, long> kv in map)
        {
            if (idx >= aggregate.Length)
                return false;
            if (aggregate[idx].ItemId != kv.Key || aggregate[idx].Count != kv.Value || kv.Value > int.MaxValue)
                return false;
            idx++;
        }

        return idx == aggregate.Length;
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
