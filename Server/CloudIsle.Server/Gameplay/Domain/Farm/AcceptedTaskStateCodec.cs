using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// AcceptedTaskState 显式 envelope codec（格式版本独立于 SQLite schema）。
    /// 确定性字节序；重复实例/非法值/高格式/损坏拒绝，不得静默降为空。
    /// </summary>
    public static class AcceptedTaskStateCodec
    {
        public const int CurrentFormatVersion = 1;
        public const int MaxHarvestInstances = 64;
        public const int MaxUnknownEntries = 32;
        public const int MaxBodyBytes = 65536;
        public const int MaxFormulaChars = 64;
        public const int MaxUnknownKeyChars = 64;
        public const int MaxUnknownValueBytes = 4096;

        static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static byte[] Encode(AcceptedTaskState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (state.HarvestCount > MaxHarvestInstances)
                throw new InvalidDataException("accepted harvest instance count exceeds cap");
            if (state.UnknownCount > MaxUnknownEntries)
                throw new InvalidDataException("accepted unknown entry count exceeds cap");

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Utf8, leaveOpen: true))
            {
                w.Write(CurrentFormatVersion);
                WriteHarvests(w, state);
                WriteUnknowns(w, state);
            }

            byte[] body = ms.ToArray();
            if (body.Length > MaxBodyBytes)
                throw new InvalidDataException("accepted task body exceeds MaxBodyBytes");
            return body;
        }

        /// <summary>
        /// <paramref name="formatVersion"/> 为 sidecar 列权威版本；须与 body 头一致且 ≤ Current。
        /// </summary>
        public static AcceptedTaskState Decode(byte[] bytes, int formatVersion)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (formatVersion <= 0)
                throw new InvalidDataException("accepted format_version must be positive");
            if (formatVersion > CurrentFormatVersion)
                throw new InvalidDataException(
                    "accepted task format_version " + formatVersion
                    + " higher than supported " + CurrentFormatVersion);
            if (bytes.Length > MaxBodyBytes)
                throw new InvalidDataException("accepted task body exceeds MaxBodyBytes");

            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms, Utf8, leaveOpen: false);
            int header = r.ReadInt32();
            if (header != formatVersion)
                throw new InvalidDataException("accepted body format header mismatch column");
            if (header != CurrentFormatVersion)
                throw new InvalidDataException("unsupported accepted format header=" + header);

            var harvest = ReadHarvests(r);
            var unknown = ReadUnknowns(r);
            if (ms.Position != ms.Length)
                throw new InvalidDataException("accepted task body has trailing garbage");

            return new AcceptedTaskState(harvest, unknown);
        }

        static void WriteHarvests(BinaryWriter w, AcceptedTaskState state)
        {
            var keys = new int[state.HarvestCount];
            int i = 0;
            foreach (int key in state.HarvestByPlotId.Keys)
                keys[i++] = key;
            Array.Sort(keys);
            w.Write(keys.Length);
            for (int k = 0; k < keys.Length; k++)
            {
                AcceptedHarvestSpec spec = state.HarvestByPlotId[keys[k]];
                w.Write(keys[k]);
                w.Write(spec.CropId);
                w.Write(spec.SeedId);
                w.Write(spec.YieldItemId);
                w.Write(spec.YieldMin);
                w.Write(spec.YieldMax);
                w.Write(spec.Exp);
                w.Write(spec.GrowSec);
                w.Write(spec.UnlockLevel);
                w.Write(spec.ConfigVersion);
                WriteString(w, spec.FormulaId, MaxFormulaChars);
            }
        }

        static List<KeyValuePair<int, AcceptedHarvestSpec>> ReadHarvests(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > MaxHarvestInstances)
                throw new InvalidDataException("invalid harvest count");
            var list = new List<KeyValuePair<int, AcceptedHarvestSpec>>(count);
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int plotId = r.ReadInt32();
                if (plotId <= 0 || !seen.Add(plotId))
                    throw new InvalidDataException("duplicate or invalid harvest plotId");
                int cropId = r.ReadInt32();
                int seedId = r.ReadInt32();
                int yieldItemId = r.ReadInt32();
                int yieldMin = r.ReadInt32();
                int yieldMax = r.ReadInt32();
                int exp = r.ReadInt32();
                int growSec = r.ReadInt32();
                int unlockLevel = r.ReadInt32();
                int configVersion = r.ReadInt32();
                string formula = ReadString(r, MaxFormulaChars);
                var spec = new AcceptedHarvestSpec(
                    cropId, seedId, yieldItemId, yieldMin, yieldMax, exp,
                    growSec, unlockLevel, configVersion, formula);
                list.Add(new KeyValuePair<int, AcceptedHarvestSpec>(plotId, spec));
            }

            return list;
        }

        static void WriteUnknowns(BinaryWriter w, AcceptedTaskState state)
        {
            // 先写计数再顺序写出；走内部只读存档，避免公开视图多次整表拷贝。
            w.Write(state.UnknownCount);
            state.ForEachUnknownSorted((key, value) =>
            {
                if (value.Length > MaxUnknownValueBytes)
                    throw new InvalidDataException("unknown value exceeds cap");
                WriteString(w, key, MaxUnknownKeyChars);
                w.Write(value.Length);
                w.Write(value);
            });
        }

        static List<KeyValuePair<string, byte[]>> ReadUnknowns(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > MaxUnknownEntries)
                throw new InvalidDataException("invalid unknown count");
            var list = new List<KeyValuePair<string, byte[]>>(count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = ReadString(r, MaxUnknownKeyChars);
                if (!seen.Add(key))
                    throw new InvalidDataException("duplicate unknown key");
                int len = r.ReadInt32();
                if (len < 0 || len > MaxUnknownValueBytes)
                    throw new InvalidDataException("invalid unknown value length");
                byte[] value = r.ReadBytes(len);
                if (value.Length != len)
                    throw new EndOfStreamException("truncated accepted unknown value");
                list.Add(new KeyValuePair<string, byte[]>(key, value));
            }

            return list;
        }

        static void WriteString(BinaryWriter w, string value, int maxChars)
        {
            if (value.Length > maxChars)
                throw new InvalidDataException("string exceeds cap");
            byte[] bytes = Utf8.GetBytes(value);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        static string ReadString(BinaryReader r, int maxChars)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > maxChars * 4)
                throw new InvalidDataException("invalid string length");
            byte[] bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("truncated string");
            string s = Utf8.GetString(bytes);
            if (s.Length > maxChars)
                throw new InvalidDataException("string exceeds char cap");
            return s;
        }
    }
}
