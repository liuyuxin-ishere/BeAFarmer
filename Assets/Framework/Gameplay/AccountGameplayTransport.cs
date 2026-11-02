using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using CloudIsle.Proto;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 复用 <see cref="AccountSession"/> 唯一 NetClient；不另建连接。
    /// </summary>
    public sealed class AccountGameplayTransport : IGameplayTransport, ICommandResultQueryPort
    {
        readonly AccountSession _session;
        readonly Func<long> _currentEpoch;
        readonly Func<long> _currentGeneration;
        readonly Func<int> _currentAttempt;

        public AccountGameplayTransport(
            AccountSession session,
            Func<long> currentEpoch,
            Func<long> currentGeneration,
            Func<int> currentAttempt)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _currentEpoch = currentEpoch ?? (() => 0);
            _currentGeneration = currentGeneration ?? (() => 0);
            _currentAttempt = currentAttempt ?? (() => 0);
        }

        public async UniTask<GameplayWireResponse> SendAsync(
            GameplayWireRequest request,
            TimeSpan timeout,
            CancellationToken transportCt)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            EnsureStampFresh(request.Epoch, request.ConnectionGeneration, request.AuthAttempt);

            if (!GameplayCommandAllowlist.TryGet(request.CommandKey, out CommandDescriptor desc))
                throw new InvalidOperationException("unknown-command");

            IMessage reqMsg = desc.RequestParser.ParseFrom(request.WireBytes);
            long expectedScope = ReadInt64(reqMsg, "operation_scope_id");
            long expectedOp = ReadInt64(reqMsg, "op_id");
            if (expectedScope <= 0 || expectedOp <= 0)
                throw new InvalidOperationException("request-meta-invalid");

            EnsureStampFresh(request.Epoch, request.ConnectionGeneration, request.AuthAttempt);
            IMessage raw = await _session
                .SendGameplayRequestAsync(reqMsg, desc.ResponseParser, timeout, transportCt);
            EnsureStampFresh(request.Epoch, request.ConnectionGeneration, request.AuthAttempt);
            return ProjectRespForCommand(raw, expectedScope, expectedOp, request.CommandKey);
        }

        public async UniTask<GameplayWireResponse> QueryAsync(long scopeId, long opId, CancellationToken ct)
        {
            if (scopeId <= 0 || opId <= 0)
                throw new ArgumentOutOfRangeException("scope/op must be positive");

            long epoch = _currentEpoch();
            long gen = _currentGeneration();
            int attempt = _currentAttempt();
            EnsureStampFresh(epoch, gen, attempt);

            var req = new CommandResultQueryReq
            {
                OperationScopeId = scopeId,
                OpId = opId
            };
            IMessage raw = await _session
                .SendGameplayRequestAsync(req, CommandResultQueryResp.Parser, TimeSpan.FromSeconds(8), ct);
            EnsureStampFresh(epoch, gen, attempt);
            return ProjectQuery(raw as CommandResultQueryResp);
        }

        public IDisposable SubscribeDelta(Action<byte[]> handler)
        {
            return _session.SubscribeGameplayDelta(handler);
        }

        /// <summary>真实 SyncStateReq；失败抛错，不伪造成功快照。</summary>
        public async UniTask<SyncStateResp> SyncStateAsync(CancellationToken ct)
        {
            long epoch = _currentEpoch();
            long gen = _currentGeneration();
            int attempt = _currentAttempt();
            EnsureStampFresh(epoch, gen, attempt);
            IMessage raw = await _session.SendGameplayRequestAsync(
                new SyncStateReq(), SyncStateResp.Parser, TimeSpan.FromSeconds(12), ct);
            EnsureStampFresh(epoch, gen, attempt);
            return raw as SyncStateResp
                   ?? SyncStateResp.Parser.ParseFrom(raw != null ? raw.ToByteArray() : Array.Empty<byte>());
        }

        // Preserve existing projection controls while production supplies its bound request key.
        static GameplayWireResponse ProjectResp(IMessage raw, long expectedScope, long expectedOp)
            => ProjectRespForCommand(raw, expectedScope, expectedOp,
                raw is PlantResp ? "Plant" : raw is HarvestResp ? "Harvest" : "");

        static GameplayWireResponse ProjectQuery(CommandResultQueryResp q)
            => ProjectQueryForCommand(q, null);

        void EnsureStampFresh(long epoch, long generation, int attempt)
        {
            if (epoch != _currentEpoch()
                || generation != _currentGeneration()
                || attempt != _currentAttempt())
            {
                throw new InvalidOperationException("stale-generation-or-attempt");
            }
        }

        /// <summary>写响应投影：缺 Ack / Unspecified → Unknown，绝不从 code 推断 Recorded。
        /// 门控通过后才投影 Plant/Harvest.Results；不得读不存在的 result_blob。</summary>
        static GameplayWireResponse ProjectRespForCommand(
            IMessage raw, long expectedScope, long expectedOp, string commandKey)
        {
            if (raw == null)
                return Unknown(-2);

            int code = ReadInt32(raw, "code");
            long committed = ReadInt64(raw, "committed_revision");
            int configVersion = ReadInt32(raw, "config_version");
            if (configVersion < 1)
                configVersion = 1;
            // PlantResp/HarvestResp 无 result_blob；保留空 blob，结果走 Results 投影
            byte[] blob = ReadBytes(raw, "result_blob");
            CommandAck ack = ReadAck(raw);
            if (ack == null || ack.Disposition == CommandDisposition.Unspecified)
                return GateFail(code, committed, configVersion, blob);

            if (ack.OperationScopeId != expectedScope || ack.OpId != expectedOp)
                return GateFail(code, committed, configVersion, blob);

            if (ack.OperationScopeId <= 0 || ack.OpId <= 0)
                return GateFail(code, committed, configVersion, blob);

            string disp = MapDispositionStrict(ack.Disposition);
            if (disp == null)
                return GateFail(code, committed, configVersion, blob);

            bool hasNext = ack.HasNextOpId;
            long next = 0;
            if (hasNext)
            {
                if (ack.NextOpId <= 0)
                    return GateFail(code, committed, configVersion, blob);
                next = ack.NextOpId;
            }

            bool open = ack.ScopeState == OperationScopeState.Open;
            if (ack.ScopeState != OperationScopeState.Open && ack.ScopeState != OperationScopeState.Closed)
                return GateFail(code, committed, configVersion, blob);

            FarmCommandDisplayResults display = FarmCommandDisplayResults.None;
            if (string.Equals(disp, "Recorded", StringComparison.OrdinalIgnoreCase))
                display = ProjectDirectFarmResults(raw, commandKey);

            return new GameplayWireResponse(
                code, disp, next, hasNext, open, committed, configVersion, blob,
                ack.OperationScopeId, ack.OpId, display);
        }

        /// <summary>查询投影：显式 ResultState；Recorded 允许 code0/rev0；Unspecified→Unknown。
        /// ResultBlob 为冻结 cmdresult 归档，不得当 PlantResp protobuf 解析。</summary>
        internal static GameplayWireResponse ProjectQueryForCommand(CommandResultQueryResp q, string expectedCommandKey)
        {
            if (q == null)
                return Unknown(-1);

            if (q.ResultState == CommandResultState.Unspecified)
                return Unknown(q.Code != 0 ? q.Code : 0);

            int configVersion = q.ConfigVersion > 0 ? q.ConfigVersion : 1;
            byte[] blob = q.ResultBlob != null ? q.ResultBlob.ToByteArray() : Array.Empty<byte>();

            if (q.ResultState == CommandResultState.Recorded)
            {
                if (q.Ack == null)
                    return Unknown(q.Code);
                int projectedCode = q.Code != 0 ? q.Code : q.OriginalCode;
                return ProjectQueryAck(
                    projectedCode,
                    "Recorded",
                    q.Ack,
                    q.CommittedRevision,
                    configVersion,
                    blob,
                    expectedCommandKey);
            }

            if (q.ResultState == CommandResultState.NotRecorded)
            {
                if (q.Ack == null)
                    return Unknown(q.Code);
                return ProjectQueryAck(
                    q.Code != 0 ? q.Code : 0,
                    "NotRecorded",
                    q.Ack,
                    q.CommittedRevision,
                    configVersion,
                    blob,
                    expectedCommandKey: null);
            }

            if (q.ResultState == CommandResultState.Expired)
                return new GameplayWireResponse(
                    q.Code != 0 ? q.Code : 111, "Expired", 0, false, false, q.CommittedRevision, configVersion, blob);

            return Unknown(q.Code);
        }

        static GameplayWireResponse ProjectQueryAck(
            int code,
            string disposition,
            CommandAck ack,
            long committed,
            int configVersion,
            byte[] blob,
            string expectedCommandKey)
        {
            if (ack.OperationScopeId <= 0 || ack.OpId <= 0)
                return Unknown(code);

            bool hasNext = ack.HasNextOpId;
            long next = 0;
            if (hasNext)
            {
                if (ack.NextOpId <= 0)
                    return Unknown(code);
                next = ack.NextOpId;
            }

            if (ack.ScopeState != OperationScopeState.Open && ack.ScopeState != OperationScopeState.Closed)
                return Unknown(code);

            bool open = ack.ScopeState == OperationScopeState.Open;
            FarmCommandDisplayResults display = FarmCommandDisplayResults.None;
            if (string.Equals(disposition, "Recorded", StringComparison.OrdinalIgnoreCase)
                && CmdResultArchiveDisplayCodec.IsFarmBatchCommandKey(expectedCommandKey))
            {
                display = CmdResultArchiveDisplayCodec.TryProjectQueryBlob(
                    blob, code, committed, configVersion, expectedCommandKey);
            }

            return new GameplayWireResponse(
                code, disposition, next, hasNext, open, committed, configVersion, blob,
                ack.OperationScopeId, ack.OpId, display);
        }

        static FarmCommandDisplayResults ProjectDirectFarmResults(IMessage raw, string commandKey)
        {
            if (!CmdResultArchiveDisplayCodec.IsFarmBatchCommandKey(commandKey))
                return FarmCommandDisplayResults.None;

            if (raw is PlantResp plant)
                return CmdResultArchiveDisplayCodec.TryProjectProtoResults("Plant", plant.Results);
            if (raw is HarvestResp harvest)
                return CmdResultArchiveDisplayCodec.TryProjectProtoResults("Harvest", harvest.Results);

            // 描述符名回退（测试反射/包装）
            string name = raw.Descriptor != null ? raw.Descriptor.Name : "";
            if (string.Equals(name, "PlantResp", StringComparison.Ordinal)
                || string.Equals(name, "HarvestResp", StringComparison.Ordinal))
            {
                FieldDescriptor f = raw.Descriptor.FindFieldByName("results");
                if (f == null || !f.IsRepeated)
                    return FarmCommandDisplayResults.UnavailableWithKind(commandKey);
                object listObj = f.Accessor.GetValue(raw);
                if (listObj is System.Collections.IList list)
                {
                    var tmp = new System.Collections.Generic.List<PlotResult>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is PlotResult pr)
                            tmp.Add(pr);
                        else
                            return FarmCommandDisplayResults.ErrorWithKind(commandKey);
                    }

                    return CmdResultArchiveDisplayCodec.TryProjectProtoResults(commandKey, tmp);
                }

                return FarmCommandDisplayResults.UnavailableWithKind(commandKey);
            }

            return FarmCommandDisplayResults.None;
        }

        static string MapDispositionStrict(CommandDisposition d)
        {
            if (d == CommandDisposition.NotAccepted) return "NotAccepted";
            if (d == CommandDisposition.Recorded) return "Recorded";
            if (d == CommandDisposition.Expired) return "Expired";
            return null;
        }

        static GameplayWireResponse GateFail(int code, long committed, int configVersion, byte[] blob)
            => new GameplayWireResponse(
                code, "Unknown", 0, false, true, committed, configVersion, blob,
                displayResults: FarmCommandDisplayResults.None);

        static GameplayWireResponse Unknown(int code)
            => new GameplayWireResponse(
                code, "Unknown", 0, false, true, 0, 1, Array.Empty<byte>(),
                displayResults: FarmCommandDisplayResults.None);

        static CommandAck ReadAck(IMessage raw)
        {
            FieldDescriptor f = raw.Descriptor.FindFieldByName("ack");
            if (f == null) return null;
            object v = f.Accessor.GetValue(raw);
            return v as CommandAck;
        }

        static int ReadInt32(IMessage raw, string protoName)
        {
            FieldDescriptor f = raw.Descriptor.FindFieldByName(protoName);
            if (f == null) return 0;
            object v = f.Accessor.GetValue(raw);
            if (v is int i) return i;
            return 0;
        }

        static long ReadInt64(IMessage raw, string protoName)
        {
            FieldDescriptor f = raw.Descriptor.FindFieldByName(protoName);
            if (f == null) return 0;
            object v = f.Accessor.GetValue(raw);
            if (v is long l) return l;
            return 0;
        }

        static byte[] ReadBytes(IMessage raw, string protoName)
        {
            FieldDescriptor f = raw.Descriptor.FindFieldByName(protoName);
            if (f == null) return Array.Empty<byte>();
            object v = f.Accessor.GetValue(raw);
            if (v is ByteString bs) return bs.ToByteArray();
            if (v is byte[] arr) return (byte[])arr.Clone();
            return Array.Empty<byte>();
        }
    }
}
