using System;
using CloudIsle.Commands;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 已校验意图：冻结业务 body 字节；getter 返回解析克隆，不泄露可变原件。
    /// </summary>
    public sealed class CommandIntent
    {
        readonly byte[] _businessBytes;
        readonly MessageParser _parser;

        public CommandIntent(string commandKey, IMessage businessBody)
        {
            if (string.IsNullOrEmpty(commandKey))
                throw new ArgumentException("commandKey required", nameof(commandKey));
            if (businessBody == null)
                throw new ArgumentNullException(nameof(businessBody));
            if (!GameplayCommandAllowlist.TryGet(commandKey, out CommandDescriptor desc))
                throw new ArgumentException("unknown-command:" + commandKey, nameof(commandKey));
            if (!ReferenceEquals(businessBody.Descriptor, desc.RequestParser.ParseFrom(Array.Empty<byte>()).Descriptor))
                throw new ArgumentException("request-type-does-not-match-command", nameof(businessBody));

            CommandKey = commandKey;
            _parser = desc.RequestParser;
            _businessBytes = businessBody.ToByteArray();
        }

        internal CommandIntent(string commandKey, MessageParser parser, byte[] businessBytes)
        {
            CommandKey = commandKey;
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _businessBytes = (byte[])(businessBytes ?? Array.Empty<byte>()).Clone();
        }

        public string CommandKey { get; }

        /// <summary>每次返回新解析实例，修改不影响冻结快照。</summary>
        public IMessage BusinessBody => _parser.ParseFrom(_businessBytes);

        /// <summary>业务字节拷贝。</summary>
        public byte[] CopyBusinessBytes() => (byte[])_businessBytes.Clone();

        internal byte[] PeekBusinessBytes() => _businessBytes;

        internal MessageParser Parser => _parser;

        /// <summary>与服务器同源的规范业务字节（不可变拷贝）。</summary>
        public byte[] CopyCanonicalBusinessBytes()
        {
            return CanonicalBusinessEncoder.Encode(CommandKey, BusinessBody);
        }
    }

    /// <summary>
    /// 不可变待确认命令：key/wire/hash/meta 在首次发送前冻结；重试复用同一 wire。
    /// </summary>
    public sealed class PendingCommand
    {
        byte[] _wireBytes;
        readonly byte[] _businessBytes;

        /// <summary>T007 兼容构造：尚未落盘/哈希；调用方随后 BuildWire。</summary>
        public PendingCommand(
            CommandIntent intent,
            long opId,
            long operationScopeId,
            long expectedRevision,
            int configVersion)
            : this(
                intent,
                environmentKey: "",
                accountKey: "",
                playerId: "",
                opId,
                operationScopeId,
                expectedRevision,
                configVersion,
                payloadHash: null,
                wireBytes: null,
                state: PendingCommandState.Prepared,
                createdAtMs: 0,
                authAttempt: 0,
                epoch: 0,
                connectionGeneration: 0,
                hasConfirmedRevision: false,
                confirmedRevision: 0)
        {
        }

        public PendingCommand(
            CommandIntent intent,
            string environmentKey,
            string accountKey,
            string playerId,
            long opId,
            long operationScopeId,
            long expectedRevision,
            int configVersion,
            string payloadHash,
            byte[] wireBytes,
            PendingCommandState state,
            long createdAtMs,
            int authAttempt,
            long epoch,
            long connectionGeneration)
            : this(
                intent, environmentKey, accountKey, playerId,
                opId, operationScopeId, expectedRevision, configVersion,
                payloadHash, wireBytes, state, createdAtMs, authAttempt, epoch, connectionGeneration,
                hasConfirmedRevision: false, confirmedRevision: 0)
        {
        }

        public PendingCommand(
            CommandIntent intent,
            string environmentKey,
            string accountKey,
            string playerId,
            long opId,
            long operationScopeId,
            long expectedRevision,
            int configVersion,
            string payloadHash,
            byte[] wireBytes,
            PendingCommandState state,
            long createdAtMs,
            int authAttempt,
            long epoch,
            long connectionGeneration,
            bool hasConfirmedRevision,
            long confirmedRevision)
        {
            Intent = intent ?? throw new ArgumentNullException(nameof(intent));
            if (opId <= 0)
                throw new ArgumentOutOfRangeException(nameof(opId));
            if (operationScopeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(operationScopeId));
            if (expectedRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedRevision));
            if (configVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(configVersion), "configVersion must be >= 1");
            if (!Enum.IsDefined(typeof(PendingCommandState), state))
                throw new ArgumentOutOfRangeException(nameof(state));
            if (createdAtMs < 0)
                throw new ArgumentOutOfRangeException(nameof(createdAtMs));
            if (authAttempt < 0)
                throw new ArgumentOutOfRangeException(nameof(authAttempt));
            if (hasConfirmedRevision && confirmedRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(confirmedRevision));

            EnvironmentKey = environmentKey ?? "";
            AccountKey = accountKey ?? "";
            PlayerId = playerId ?? "";
            OpId = opId;
            OperationScopeId = operationScopeId;
            ExpectedRevision = expectedRevision;
            ConfigVersion = configVersion;
            State = state;
            CreatedAtMs = createdAtMs;
            AuthAttempt = authAttempt;
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
            HasConfirmedRevision = hasConfirmedRevision;
            ConfirmedRevision = confirmedRevision;
            _businessBytes = intent.CopyBusinessBytes();

            if (wireBytes != null && wireBytes.Length > 0)
            {
                ValidateWireAgainstIdentity(wireBytes);
                _wireBytes = (byte[])wireBytes.Clone();
                HasWireBytes = true;
                string computed = ComputeCanonicalHash();
                if (!string.IsNullOrEmpty(payloadHash) &&
                    !string.Equals(payloadHash, computed, StringComparison.Ordinal))
                {
                    throw new ArgumentException("payload-hash-mismatch", nameof(payloadHash));
                }

                PayloadHash = computed;
            }
            else
            {
                _wireBytes = null;
                HasWireBytes = false;
                PayloadHash = payloadHash;
            }
        }

        public CommandIntent Intent { get; }
        public string EnvironmentKey { get; }
        public string AccountKey { get; }
        public string PlayerId { get; }
        public long OpId { get; }
        public long OperationScopeId { get; }
        public long ExpectedRevision { get; }
        public int ConfigVersion { get; }
        public PendingCommandState State { get; }
        public long CreatedAtMs { get; }
        public int AuthAttempt { get; }
        public long Epoch { get; }
        public long ConnectionGeneration { get; }

        /// <summary>Recorded 后持久化的权威 committed revision；未设置时不可用 Expected+1 猜测。</summary>
        public bool HasConfirmedRevision { get; }
        public long ConfirmedRevision { get; }

        public bool HasWireBytes { get; private set; }

        /// <summary>cmdhash-v1；T007 仅 BuildWire 未落盘时可为 null。</summary>
        public string PayloadHash { get; private set; }

        /// <summary>返回拷贝；修改不影响内部重试字节。</summary>
        public byte[] WireBytes
        {
            get
            {
                if (!HasWireBytes || _wireBytes == null)
                    return null;
                return (byte[])_wireBytes.Clone();
            }
        }

        internal byte[] PeekWireBytes() => _wireBytes;
        internal byte[] PeekBusinessBytes() => _businessBytes;

        /// <summary>T007：编码一次；不写 hash（正式落盘前由 EnsureDurableHash）。</summary>
        internal void SetWireOnce(byte[] bytes)
        {
            if (HasWireBytes)
                return;
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("wire required", nameof(bytes));
            ValidateWireAgainstIdentity(bytes);
            _wireBytes = (byte[])bytes.Clone();
            HasWireBytes = true;
        }

        /// <summary>
        /// 落盘前用规范业务字节计算并冻结 cmdhash-v1。
        /// 已有 hash 必须与重算一致，禁止构造参数绕过。
        /// </summary>
        public void EnsureDurableHash()
        {
            if (!HasWireBytes)
                throw new InvalidOperationException("wire required before hash");
            ValidateWireAgainstIdentity(_wireBytes);
            string computed = ComputeCanonicalHash();
            if (!string.IsNullOrEmpty(PayloadHash) &&
                !string.Equals(PayloadHash, computed, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("payload-hash-mismatch");
            }

            PayloadHash = computed;
        }

        /// <summary>编码一次后冻结 wire+hash；已冻结则返回自身。wire 元数据必须与身份一致。</summary>
        public PendingCommand WithFrozenWire(byte[] wireBytes)
        {
            if (HasWireBytes && !string.IsNullOrEmpty(PayloadHash))
                return this;
            if (wireBytes == null || wireBytes.Length == 0)
                throw new ArgumentException("wire required", nameof(wireBytes));
            ValidateWireAgainstIdentity(wireBytes);
            string hash = ComputeCanonicalHashFromBusiness();
            return new PendingCommand(
                Intent, EnvironmentKey, AccountKey, PlayerId,
                OpId, OperationScopeId, ExpectedRevision, ConfigVersion,
                hash, wireBytes, State, CreatedAtMs, AuthAttempt, Epoch, ConnectionGeneration,
                HasConfirmedRevision, ConfirmedRevision);
        }

        public PendingCommand WithState(PendingCommandState state)
        {
            return new PendingCommand(
                Intent, EnvironmentKey, AccountKey, PlayerId,
                OpId, OperationScopeId, ExpectedRevision, ConfigVersion,
                PayloadHash, _wireBytes, state, CreatedAtMs, AuthAttempt, Epoch, ConnectionGeneration,
                HasConfirmedRevision, ConfirmedRevision);
        }

        public PendingCommand WithConfirmedRevision(long committedRevision)
        {
            if (committedRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(committedRevision));
            return new PendingCommand(
                Intent, EnvironmentKey, AccountKey, PlayerId,
                OpId, OperationScopeId, ExpectedRevision, ConfigVersion,
                PayloadHash, _wireBytes, State, CreatedAtMs, AuthAttempt, Epoch, ConnectionGeneration,
                hasConfirmedRevision: true, confirmedRevision: committedRevision);
        }

        public PendingCommand WithIdentity(
            string environmentKey,
            string accountKey,
            string playerId,
            int authAttempt,
            long epoch,
            long connectionGeneration,
            long createdAtMs)
        {
            return new PendingCommand(
                Intent,
                environmentKey,
                accountKey,
                playerId,
                OpId,
                OperationScopeId,
                ExpectedRevision,
                ConfigVersion,
                PayloadHash,
                _wireBytes,
                State,
                createdAtMs,
                authAttempt,
                epoch,
                connectionGeneration,
                HasConfirmedRevision,
                ConfirmedRevision);
        }

        /// <summary>
        /// 重认证后仅重绑传输 stamp（attempt/epoch/generation）；
        /// 不改 scope/op/hash/wire/业务字节。
        /// </summary>
        public PendingCommand WithTransportStamp(int authAttempt, long epoch, long connectionGeneration)
        {
            if (authAttempt < 0)
                throw new ArgumentOutOfRangeException(nameof(authAttempt));
            return new PendingCommand(
                Intent, EnvironmentKey, AccountKey, PlayerId,
                OpId, OperationScopeId, ExpectedRevision, ConfigVersion,
                PayloadHash, _wireBytes, State, CreatedAtMs,
                authAttempt, epoch, connectionGeneration,
                HasConfirmedRevision, ConfirmedRevision);
        }

        string ComputeCanonicalHash() => ComputeCanonicalHashFromBusiness();

        string ComputeCanonicalHashFromBusiness()
        {
            byte[] canonical = CanonicalBusinessEncoder.Encode(Intent.CommandKey, Intent.BusinessBody);
            return CmdHashV1.ComputeHex(Intent.CommandKey, ExpectedRevision, ConfigVersion, canonical);
        }

        void ValidateWireAgainstIdentity(byte[] wireBytes)
        {
            if (wireBytes == null || wireBytes.Length == 0)
                throw new ArgumentException("wire required");
            if (wireBytes.Length > PendingCommandStore.MaxWireBytes)
                throw new ArgumentException("wire-too-large");

            IMessage msg = Intent.Parser.ParseFrom(wireBytes);
            WireFieldGuard.RejectUnknownWireFields(msg);
            long wireOp = ReadInt64Field(msg, "op_id");
            long wireScope = ReadInt64Field(msg, "operation_scope_id");
            long wireRev = ReadInt64Field(msg, "expected_revision");
            int wireCfg = ReadInt32Field(msg, "config_version");
            if (wireOp != OpId || wireScope != OperationScopeId
                || wireRev != ExpectedRevision || wireCfg != ConfigVersion)
            {
                throw new ArgumentException("wire-meta-mismatch");
            }

            // wire 业务字段必须与冻结 Intent 规范字节一致（meta 已对齐）
            ClearMetaFields(msg);
            byte[] fromWire = CanonicalBusinessEncoder.Encode(Intent.CommandKey, msg);
            byte[] fromIntent = Intent.CopyCanonicalBusinessBytes();
            if (!WireFieldGuard.ByteArraysEqual(fromWire, fromIntent))
                throw new ArgumentException("wire-business-mismatch");
        }

        static void ClearMetaFields(IMessage msg)
        {
            SetInt64Field(msg, "op_id", 0);
            SetInt64Field(msg, "operation_scope_id", 0);
            SetInt64Field(msg, "expected_revision", 0);
            SetInt32Field(msg, "config_version", 0);
        }

        static void SetInt64Field(IMessage msg, string protoName, long value)
        {
            FieldDescriptor f = msg.Descriptor.FindFieldByName(protoName);
            if (f != null) f.Accessor.SetValue(msg, value);
        }

        static void SetInt32Field(IMessage msg, string protoName, int value)
        {
            FieldDescriptor f = msg.Descriptor.FindFieldByName(protoName);
            if (f != null) f.Accessor.SetValue(msg, value);
        }

        static long ReadInt64Field(IMessage msg, string protoName)
        {
            FieldDescriptor f = msg.Descriptor.FindFieldByName(protoName);
            if (f == null) return 0;
            object v = f.Accessor.GetValue(msg);
            if (v is long l) return l;
            if (v is ulong u) return unchecked((long)u);
            return 0;
        }

        static int ReadInt32Field(IMessage msg, string protoName)
        {
            FieldDescriptor f = msg.Descriptor.FindFieldByName(protoName);
            if (f == null) return 0;
            object v = f.Accessor.GetValue(msg);
            if (v is int i) return i;
            return 0;
        }
    }
}
