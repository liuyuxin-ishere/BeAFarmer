using System;
using System.IO;
using System.Text;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 客户端只读解码：账本 <c>result_blob</c> 为 cmdresult-v* + 可选业务摘要。
    /// 与 Server <c>CommandResultArchive</c>/<c>FarmBatchSummaryCodec</c> 字节布局对齐；
    /// 不解析投影/delta protobuf，不做资产重算；不改变冻结格式。
    /// </summary>
    public static class CmdResultArchiveDisplayCodec
    {
        public const string FormatVersionV1 = "cmdresult-v1";
        public const string FormatVersionV2 = "cmdresult-v2";
        public const string FormatVersionV3 = "cmdresult-v3";
        public const string FarmBatchSummaryV1 = "farm-batch-summary-v1";
        public const string HarvestSummaryV1 = "harvest-summary-v1";

        public const int KindPlant = 1;
        public const int KindHarvest = 2;

        const int MaxLpBytes = 1024 * 1024;
        const int MaxSummaryLp = 64 * 1024;
        const int MaxVersionLp = 1024;

        /// <summary>
        /// 自 Query ResultBlob 恢复展示结果。
        /// 须由调用方先完成 Ack/scope/op 门控；本方法只读归档。
        /// </summary>
        /// <param name="blob">Query ResultBlob 原字节。</param>
        /// <param name="outerCode">Query 投影后的外层 code。</param>
        /// <param name="outerRevision">Query CommittedRevision。</param>
        /// <param name="outerConfig">Query ConfigVersion。</param>
        /// <param name="expectedCommandKey">Pending 命令键 Plant/Harvest；其它返回 None。</param>
        public static FarmCommandDisplayResults TryProjectQueryBlob(
            byte[] blob,
            int outerCode,
            long outerRevision,
            int outerConfig,
            string expectedCommandKey)
        {
            if (!IsFarmBatchCommandKey(expectedCommandKey))
                return FarmCommandDisplayResults.None;

            if (blob == null || blob.Length == 0)
                return FarmCommandDisplayResults.Unavailable;

            if (LooksLikeFormatVersion(blob, FormatVersionV1)
                || LooksLikeFormatVersion(blob, FormatVersionV2))
                return FarmCommandDisplayResults.UnavailableWithKind(expectedCommandKey);

            if (LooksLikeFormatVersion(blob, FormatVersionV3))
            {
                if (!TryDecodeV3Header(
                        blob,
                        out string kind,
                        out int archivedCode,
                        out long archivedRev,
                        out int archivedCfg,
                        out byte[] summary))
                {
                    return FarmCommandDisplayResults.ErrorWithKind(expectedCommandKey);
                }

                if (!string.Equals(kind, expectedCommandKey, StringComparison.Ordinal))
                    return FarmCommandDisplayResults.ErrorWithKind(kind ?? "");

                if (!HeaderConsistent(archivedCode, archivedRev, archivedCfg, outerCode, outerRevision, outerConfig))
                    return FarmCommandDisplayResults.ErrorWithKind(kind);

                if (summary == null || summary.Length == 0)
                    return FarmCommandDisplayResults.UnavailableWithKind(kind);

                string summaryVer = PeekLpUtf8Version(summary);
                if (string.Equals(summaryVer, HarvestSummaryV1, StringComparison.Ordinal))
                    return FarmCommandDisplayResults.UnavailableWithKind(kind);
                if (!string.Equals(summaryVer, FarmBatchSummaryV1, StringComparison.Ordinal))
                {
                    // 可识别为某摘要前缀失败或未知 → 若像 farm-batch 则 Error，否则 Unavailable
                    if (summaryVer != null && summaryVer.StartsWith("farm-batch", StringComparison.Ordinal))
                        return FarmCommandDisplayResults.ErrorWithKind(kind);
                    return FarmCommandDisplayResults.UnavailableWithKind(kind);
                }

                if (!FarmBatchSummaryDisplayCodec.TryDecode(
                        summary, out int summaryKind, out FarmPlotDisplayResult[] items))
                    return FarmCommandDisplayResults.ErrorWithKind(kind);

                if (!SummaryKindMatchesArchive(kind, summaryKind))
                    return FarmCommandDisplayResults.ErrorWithKind(kind);

                if (items == null || items.Length == 0)
                    return FarmCommandDisplayResults.UnavailableWithKind(kind);

                return FarmCommandDisplayResults.Present(kind, items);
            }

            // 无法识别前缀：不得当成功
            return FarmCommandDisplayResults.Unavailable;
        }

        /// <summary>直连 PlantResp/HarvestResp.Results → 展示信封（门控通过后调用）。</summary>
        public static FarmCommandDisplayResults TryProjectProtoResults(
            string commandKey,
            System.Collections.Generic.IList<CloudIsle.Proto.PlotResult> results)
        {
            if (!IsFarmBatchCommandKey(commandKey))
                return FarmCommandDisplayResults.None;
            if (results == null || results.Count == 0)
                return FarmCommandDisplayResults.UnavailableWithKind(commandKey);
            if (results.Count > FarmCommandDisplayResults.MaxItems)
                return FarmCommandDisplayResults.ErrorWithKind(commandKey);

            var items = new FarmPlotDisplayResult[results.Count];
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < results.Count; i++)
            {
                CloudIsle.Proto.PlotResult row = results[i];
                if (row == null || row.PlotId <= 0 || !seen.Add(row.PlotId))
                    return FarmCommandDisplayResults.ErrorWithKind(commandKey);
                // code 原样保留（含 0 与未知业务码）；不推断成功
                items[i] = new FarmPlotDisplayResult(row.PlotId, row.Code);
            }

            return FarmCommandDisplayResults.Present(commandKey, items);
        }

        public static bool IsFarmBatchCommandKey(string commandKey)
            => string.Equals(commandKey, "Plant", StringComparison.Ordinal)
               || string.Equals(commandKey, "Harvest", StringComparison.Ordinal);

        public static bool LooksLikeFormatVersion(byte[] blob, string formatVersion)
        {
            if (blob == null || blob.Length < 8 || string.IsNullOrEmpty(formatVersion))
                return false;
            try
            {
                using var ms = new MemoryStream(blob, writable: false);
                return string.Equals(ReadLpUtf8(ms, MaxVersionLp), formatVersion, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        static bool HeaderConsistent(
            int archivedCode,
            long archivedRev,
            int archivedCfg,
            int outerCode,
            long outerRevision,
            int outerConfig)
        {
            if (archivedCfg < 1 || outerConfig < 1)
                return false;
            if (archivedCode != outerCode)
                return false;
            if (archivedRev != outerRevision)
                return false;
            if (archivedCfg != outerConfig)
                return false;
            return true;
        }

        static bool SummaryKindMatchesArchive(string archiveKind, int summaryKind)
        {
            if (string.Equals(archiveKind, "Plant", StringComparison.Ordinal))
                return summaryKind == KindPlant;
            if (string.Equals(archiveKind, "Harvest", StringComparison.Ordinal))
                return summaryKind == KindHarvest;
            return false;
        }

        /// <summary>解码 v3：跳过投影/delta 字节，取出摘要；严格 EOF。</summary>
        public static bool TryDecodeV3Header(
            byte[] blob,
            out string kind,
            out int code,
            out long committedRevision,
            out int configVersion,
            out byte[] summary)
        {
            kind = "";
            code = 0;
            committedRevision = 0;
            configVersion = 0;
            summary = null;
            if (blob == null || blob.Length < 8)
                return false;
            try
            {
                using var ms = new MemoryStream(blob, writable: false);
                if (!string.Equals(ReadLpUtf8(ms, MaxVersionLp), FormatVersionV3, StringComparison.Ordinal))
                    return false;
                kind = ReadLpUtf8(ms, MaxVersionLp);
                code = ReadI32(ms);
                committedRevision = ReadI64(ms);
                configVersion = ReadI32(ms);
                if (string.IsNullOrEmpty(kind) || committedRevision < 0 || configVersion < 1)
                    return false;
                // 投影 / delta：只校验长度并跳过，不 ParseFrom
                SkipLpBytes(ms, MaxLpBytes);
                SkipLpBytes(ms, MaxLpBytes);
                byte[] sum = ReadLpBytes(ms, MaxSummaryLp);
                if (ms.Position != ms.Length)
                    return false;
                if (sum.Length > 0)
                {
                    summary = new byte[sum.Length];
                    Buffer.BlockCopy(sum, 0, summary, 0, sum.Length);
                }

                return true;
            }
            catch
            {
                kind = "";
                code = 0;
                committedRevision = 0;
                configVersion = 0;
                summary = null;
                return false;
            }
        }

        static string PeekLpUtf8Version(byte[] summary)
        {
            if (summary == null || summary.Length < 4)
                return null;
            try
            {
                using var ms = new MemoryStream(summary, writable: false);
                return ReadLpUtf8(ms, MaxVersionLp);
            }
            catch
            {
                return null;
            }
        }

        static void SkipLpBytes(Stream s, int maxLen)
        {
            int n = ReadI32(s);
            if (n < 0 || n > maxLen)
                throw new InvalidDataException("lp");
            if (n == 0)
                return;
            if (s.CanSeek)
            {
                long next = s.Position + n;
                if (next > s.Length)
                    throw new EndOfStreamException();
                s.Position = next;
                return;
            }

            var buf = new byte[Math.Min(n, 4096)];
            int left = n;
            while (left > 0)
            {
                int r = s.Read(buf, 0, Math.Min(buf.Length, left));
                if (r <= 0) throw new EndOfStreamException();
                left -= r;
            }
        }

        static byte[] ReadLpBytes(Stream s, int maxLen)
        {
            int n = ReadI32(s);
            if (n < 0 || n > maxLen)
                throw new InvalidDataException("lp length");
            var buf = new byte[n];
            int read = 0;
            while (read < n)
            {
                int r = s.Read(buf, read, n - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }

            return buf;
        }

        static string ReadLpUtf8(Stream s, int maxLen)
            => Encoding.UTF8.GetString(ReadLpBytes(s, maxLen));

        static int ReadI32(Stream s)
        {
            Span<byte> buf = stackalloc byte[4];
            ReadExact(s, buf);
            return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
        }

        static long ReadI64(Stream s)
        {
            Span<byte> buf = stackalloc byte[8];
            ReadExact(s, buf);
            long v = 0;
            for (int i = 0; i < 8; i++)
                v = (v << 8) | buf[i];
            return v;
        }

        static void ReadExact(Stream s, Span<byte> buf)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int r = s.Read(buf.Slice(off));
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
        }
    }

    /// <summary>
    /// farm-batch-summary-v1 只读展示解码：plotId+code；严格 EOF / 2..24 / 去重正 ID。
    /// </summary>
    public static class FarmBatchSummaryDisplayCodec
    {
        const int MaxGains = 64;
        const int MaxVersionLp = 1024;

        public static bool TryDecode(byte[] blob, out int kind, out FarmPlotDisplayResult[] items)
        {
            kind = 0;
            items = null;
            if (blob == null || blob.Length < 8)
                return false;
            try
            {
                using var ms = new MemoryStream(blob, writable: false);
                if (!string.Equals(
                        ReadLpUtf8(ms),
                        CmdResultArchiveDisplayCodec.FarmBatchSummaryV1,
                        StringComparison.Ordinal))
                    return false;
                kind = ReadI32(ms);
                int n = ReadI32(ms);
                if ((kind != CmdResultArchiveDisplayCodec.KindPlant
                     && kind != CmdResultArchiveDisplayCodec.KindHarvest)
                    || n < 2
                    || n > FarmCommandDisplayResults.MaxItems)
                    return false;

                var rows = new FarmPlotDisplayResult[n];
                int prev = 0;
                long aggExpCheck = 0;
                var aggMap = new System.Collections.Generic.SortedDictionary<int, long>();
                for (int i = 0; i < n; i++)
                {
                    int plotId = ReadI32(ms);
                    int code = ReadI32(ms);
                    int gainN = ReadI32(ms);
                    if (plotId <= 0 || plotId <= prev || gainN < 0 || gainN > MaxGains)
                        return false;
                    prev = plotId;
                    var gains = new System.Collections.Generic.List<(int id, int count)>(gainN);
                    for (int g = 0; g < gainN; g++)
                    {
                        int itemId = ReadI32(ms);
                        int count = ReadI32(ms);
                        if (itemId <= 0 || count <= 0)
                            return false;
                        gains.Add((itemId, count));
                    }

                    int exp = ReadI32(ms);
                    if (exp < 0)
                        return false;
                    if (kind == CmdResultArchiveDisplayCodec.KindPlant && (gainN != 0 || exp != 0))
                        return false;
                    if (kind == CmdResultArchiveDisplayCodec.KindHarvest)
                    {
                        if (code == 0 && gainN <= 0)
                            return false;
                        if (code != 0 && (gainN != 0 || exp != 0))
                            return false;
                    }

                    if (code == 0 && kind == CmdResultArchiveDisplayCodec.KindHarvest)
                    {
                        aggExpCheck = checked(aggExpCheck + exp);
                        for (int g = 0; g < gains.Count; g++)
                        {
                            if (!aggMap.TryGetValue(gains[g].id, out long cur))
                                cur = 0;
                            aggMap[gains[g].id] = checked(cur + gains[g].count);
                        }
                    }

                    rows[i] = new FarmPlotDisplayResult(plotId, code);
                }

                int aggN = ReadI32(ms);
                if (aggN < 0 || aggN > MaxGains)
                    return false;
                var agg = new (int id, int count)[aggN];
                for (int i = 0; i < aggN; i++)
                {
                    int itemId = ReadI32(ms);
                    int count = ReadI32(ms);
                    if (itemId <= 0 || count <= 0)
                        return false;
                    agg[i] = (itemId, count);
                }

                int aggExp = ReadI32(ms);
                if (aggExp < 0 || ms.Position != ms.Length)
                    return false;
                if (kind == CmdResultArchiveDisplayCodec.KindPlant && (aggN != 0 || aggExp != 0))
                    return false;
                if (kind == CmdResultArchiveDisplayCodec.KindHarvest)
                {
                    if (aggExpCheck != aggExp || aggExpCheck > int.MaxValue)
                        return false;
                    if (aggMap.Count != aggN)
                        return false;
                    int idx = 0;
                    foreach (var kv in aggMap)
                    {
                        if (idx >= aggN || agg[idx].id != kv.Key || agg[idx].count != kv.Value
                            || kv.Value > int.MaxValue)
                            return false;
                        idx++;
                    }
                }

                items = rows;
                return true;
            }
            catch
            {
                kind = 0;
                items = null;
                return false;
            }
        }

        static string ReadLpUtf8(Stream s)
        {
            int n = ReadI32(s);
            if (n < 0 || n > MaxVersionLp)
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
}
