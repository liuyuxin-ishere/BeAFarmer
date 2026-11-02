using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Gameplay
{
    /// <summary>玩法传输请求（含会话门控元数据）。</summary>
    public sealed class GameplayWireRequest
    {
        readonly byte[] _wireBytes;

        public GameplayWireRequest(
            string commandKey,
            byte[] wireBytes,
            long epoch,
            long connectionGeneration,
            int authAttempt)
        {
            CommandKey = commandKey ?? "";
            _wireBytes = wireBytes == null ? Array.Empty<byte>() : (byte[])wireBytes.Clone();
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
            AuthAttempt = authAttempt;
        }

        public string CommandKey { get; }

        /// <summary>每次返回新拷贝，防止调用方改写内部缓冲。</summary>
        public byte[] WireBytes => (byte[])_wireBytes.Clone();

        public long Epoch { get; }
        public long ConnectionGeneration { get; }
        public int AuthAttempt { get; }
    }

    /// <summary>传输层响应投影（Ack 字段 + 可选农场展示结果）。</summary>
    public sealed class GameplayWireResponse
    {
        readonly byte[] _resultBlob;
        readonly FarmCommandDisplayResults _displayResults;

        public GameplayWireResponse(
            int code,
            string disposition,
            long nextOpId,
            bool hasNextOp,
            bool scopeOpen,
            long committedRevision,
            int configVersion,
            byte[] resultBlob,
            long operationScopeId = 0,
            long opId = 0,
            FarmCommandDisplayResults displayResults = null)
        {
            Code = code;
            Disposition = disposition ?? "";
            NextOpId = nextOpId;
            HasNextOp = hasNextOp;
            ScopeOpen = scopeOpen;
            CommittedRevision = committedRevision;
            ConfigVersion = configVersion;
            OperationScopeId = operationScopeId;
            OpId = opId;
            _resultBlob = resultBlob == null ? Array.Empty<byte>() : (byte[])resultBlob.Clone();
            _displayResults = displayResults == null
                ? FarmCommandDisplayResults.None
                : displayResults.Clone();
        }

        public int Code { get; }
        /// <summary>NotAccepted / Recorded / Expired / Unknown / NotRecorded。</summary>
        public string Disposition { get; }
        public long NextOpId { get; }
        public bool HasNextOp { get; }
        public bool ScopeOpen { get; }
        public long CommittedRevision { get; }
        public int ConfigVersion { get; }
        public long OperationScopeId { get; }
        public long OpId { get; }

        /// <summary>每次返回新拷贝。</summary>
        public byte[] ResultBlob => (byte[])_resultBlob.Clone();

        /// <summary>槽位私有展示信封克隆；Unknown/未门控路径为 None。</summary>
        public FarmCommandDisplayResults DisplayResults => _displayResults.Clone();
    }

    /// <summary>复用账号唯一连接的受限传输。</summary>
    public interface IGameplayTransport
    {
        UniTask<GameplayWireResponse> SendAsync(
            GameplayWireRequest request,
            TimeSpan timeout,
            CancellationToken transportCt);

        IDisposable SubscribeDelta(Action<byte[]> handler);
    }

    /// <summary>权威账本查询（客户端恢复用）。</summary>
    public interface ICommandResultQueryPort
    {
        UniTask<GameplayWireResponse> QueryAsync(
            long scopeId,
            long opId,
            CancellationToken ct);
    }
}
