using System;

namespace Game.Framework.Gameplay
{
    /// <summary>模型应用结果种类（C06/C07）。</summary>
    public enum ModelApplyKind
    {
        Applied = 0,
        Duplicate = 1,
        Gap = 2,
        Invalid = 3,
    }

    /// <summary>模型端口返回：含已应用 revision；失败不半更新。</summary>
    public readonly struct ModelApplyResult
    {
        public ModelApplyResult(ModelApplyKind kind, long appliedRevision, string error = null)
        {
            Kind = kind;
            AppliedRevision = appliedRevision;
            Error = error ?? "";
        }

        public ModelApplyKind Kind { get; }
        public long AppliedRevision { get; }
        public string Error { get; }

        public bool IsOk => Kind == ModelApplyKind.Applied || Kind == ModelApplyKind.Duplicate;

        public static ModelApplyResult Applied(long revision) =>
            new ModelApplyResult(ModelApplyKind.Applied, revision);

        public static ModelApplyResult Duplicate(long revision) =>
            new ModelApplyResult(ModelApplyKind.Duplicate, revision);

        public static ModelApplyResult Gap(long currentRevision, string error) =>
            new ModelApplyResult(ModelApplyKind.Gap, currentRevision, error);

        public static ModelApplyResult Invalid(long currentRevision, string error) =>
            new ModelApplyResult(ModelApplyKind.Invalid, currentRevision, error);
    }

    /// <summary>全量快照封套（JSON 已含规范 int64 字符串）。</summary>
    public sealed class SnapshotEnvelope
    {
        readonly byte[] _rawBytes;

        public SnapshotEnvelope(string snapshotJson, long revision, long epoch, long connectionGeneration, int encodedBytes = 0)
        {
            SnapshotJson = snapshotJson ?? "";
            Revision = revision;
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
            EncodedBytes = encodedBytes > 0
                ? encodedBytes
                : (SnapshotJson.Length > 0 ? System.Text.Encoding.UTF8.GetByteCount(SnapshotJson) : 0);
            _rawBytes = Array.Empty<byte>();
        }

        public string SnapshotJson { get; }
        public long Revision { get; }
        public long Epoch { get; }
        public long ConnectionGeneration { get; }
        public int EncodedBytes { get; }
    }

    /// <summary>增量批次 DTO（JSON 来自正式 PlayerDeltaBatchNtf）。</summary>
    public sealed class DeltaBatchDto
    {
        public DeltaBatchDto(
            string batchJson,
            long baseRevision,
            long newRevision,
            long epoch,
            long connectionGeneration,
            int encodedBytes,
            long serverTimeMs = 0)
        {
            BatchJson = batchJson ?? "";
            BaseRevision = baseRevision;
            NewRevision = newRevision;
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
            EncodedBytes = encodedBytes < 0 ? 0 : encodedBytes;
            ServerTimeMs = serverTimeMs < 0 ? 0 : serverTimeMs;
        }

        public string BatchJson { get; }
        public long BaseRevision { get; }
        public long NewRevision { get; }
        public long Epoch { get; }
        public long ConnectionGeneration { get; }
        public int EncodedBytes { get; }
        /// <summary>已校验 Batch.server_time；0 表示缺省/未接线。</summary>
        public long ServerTimeMs { get; }
    }

    /// <summary>传输层增量封套（原始帧）。</summary>
    public sealed class GameplayDeltaEnvelope
    {
        readonly byte[] _payload;

        public GameplayDeltaEnvelope(byte[] payload, long epoch, long connectionGeneration)
        {
            _payload = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
        }

        public byte[] Payload => (byte[])_payload.Clone();
        public int EncodedBytes => _payload.Length;
        public long Epoch { get; }
        public long ConnectionGeneration { get; }
    }

    public enum SyncReason
    {
        Explicit = 0,
        Gap = 1,
        Invalid = 2,
        BufferOverflow = 3,
        RecordedAwaitModel = 4,
        Reset = 5,
    }

    public sealed class SyncAppliedResult
    {
        public SyncAppliedResult(int code, long appliedRevision, string message = null)
        {
            Code = code;
            AppliedRevision = appliedRevision;
            Message = message ?? "";
        }

        public int Code { get; }
        public long AppliedRevision { get; }
        public string Message { get; }
    }
}
