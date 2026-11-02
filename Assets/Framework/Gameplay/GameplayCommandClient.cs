using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 单写待确认客户端：落盘后才发；同环境账号一槽；重试复用 wire/hash。
    /// Dispose 观察者不取消已持久命令。
    /// </summary>
    public sealed class GameplayCommandClient : IGameplayCommandPort
    {
        readonly PendingCommandStore _store;
        readonly IGameplayTransport _transport;
        readonly ICommandResultQueryPort _query;
        readonly Func<SessionStamp> _captureStamp;
        readonly Func<bool> _isReady;
        readonly Func<long> _modelRevision;
        readonly Func<long> _utcNowMs;
        readonly Func<WriteAuthority, bool> _tryAllocateWrite;
        readonly Action<GameplayWireResponse> _onAuthoritativeAck;
        readonly Action<SyncReason, Action<SyncAppliedResult>> _requestFullSync;
        readonly Action<int, SessionStamp> _onTerminalSession;
        readonly object _gate = new object();

        PendingCommand _active;
        Action<CommandClientOutcome> _observer;
        bool _busy;
        bool _slotReserved;
        string _lockReason;
        string _recoveryPhase;
        string _recoveryError;
        long _runId;
        long _slotGeneration;
        /// <summary>槽位私有冻结展示结果；新/陈旧/重入槽不得借用。</summary>
        FarmCommandDisplayResults _slotDisplayResults;
        long _slotDisplayResultsGeneration;
        UniTask _inflight = UniTask.CompletedTask;

        public GameplayCommandClient(
            PendingCommandStore store,
            IGameplayTransport transport,
            ICommandResultQueryPort query,
            Func<SessionStamp> captureStamp,
            Func<bool> isReady,
            Func<long> modelRevision,
            Func<long> utcNowMs = null,
            Func<WriteAuthority, bool> tryAllocateWrite = null,
            Action<GameplayWireResponse> onAuthoritativeAck = null,
            Action<SyncReason, Action<SyncAppliedResult>> requestFullSync = null,
            Action<int, SessionStamp> onTerminalSession = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _captureStamp = captureStamp ?? throw new ArgumentNullException(nameof(captureStamp));
            _isReady = isReady ?? throw new ArgumentNullException(nameof(isReady));
            _modelRevision = modelRevision ?? (() => 0);
            _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _tryAllocateWrite = tryAllocateWrite;
            _onAuthoritativeAck = onAuthoritativeAck;
            _requestFullSync = requestFullSync;
            _onTerminalSession = onTerminalSession;
        }

        public bool IsBusy
        {
            get { lock (_gate) return _busy || _active != null || _slotReserved; }
        }

        public string LockReason
        {
            get { lock (_gate) return _lockReason; }
        }

        public PendingCommand ActiveSnapshot
        {
            get { lock (_gate) return _active; }
        }

        /// <summary>Read-only acceptance diagnostics. No account, request body or exception message.</summary>
        public string RecoveryPhase { get { lock (_gate) return _recoveryPhase ?? ""; } }
        public string RecoveryError { get { lock (_gate) return _recoveryError ?? ""; } }

        /// <summary>
        /// 生产 Send：由会话权威分配 scope/op/revision/config，落盘后经唯一传输发送。
        /// </summary>
        public void Send(CommandIntent intent, Action<int, string> done)
        {
            if (done == null) throw new ArgumentNullException(nameof(done));
            if (intent == null)
            {
                done(GameplayBridgeBinding.CodeInvalidDto, GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeInvalidDto, "intent-null"));
                return;
            }

            if (!_isReady())
            {
                done(GameplayBridgeBinding.CodeNotReady, GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeNotReady, "not-ready"));
                return;
            }

            if (_tryAllocateWrite == null)
            {
                done(GameplayBridgeBinding.CodeNotReady, GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeNotReady, "write-authority-missing"));
                return;
            }

            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_lockReason)
                    && (_lockReason.StartsWith("pending-locked", StringComparison.Ordinal)
                        || _lockReason.StartsWith("payload-conflict", StringComparison.Ordinal)))
                {
                    done(GameplayBridgeBinding.CodeNotReady, GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeNotReady, _lockReason));
                    return;
                }

                if (_busy || _active != null || _slotReserved)
                {
                    done(GameplayBridgeBinding.CodeNotReady, GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeNotReady, "busy"));
                    return;
                }
            }

            var authority = new WriteAuthority();
            if (!_tryAllocateWrite(authority)
                || authority.OpId <= 0
                || authority.OperationScopeId <= 0
                || authority.ConfigVersion < 1
                || string.IsNullOrEmpty(authority.EnvironmentKey)
                || string.IsNullOrEmpty(authority.AccountKey)
                || string.IsNullOrEmpty(authority.PlayerId))
            {
                done(GameplayBridgeBinding.CodeNotReady, GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeNotReady, "no-authoritative-next"));
                return;
            }

            try
            {
                BeginWrite(
                    intent,
                    authority.OpId,
                    authority.OperationScopeId,
                    authority.ExpectedRevision,
                    authority.ConfigVersion,
                    authority.EnvironmentKey,
                    authority.AccountKey,
                    authority.PlayerId,
                    authority.AuthAttempt,
                    authority.Epoch,
                    authority.ConnectionGeneration,
                    outcome =>
                    {
                        if (outcome == null)
                        {
                            done(-2, GameplayCompletionFormatter.FormatLocal(-2, "null-outcome"));
                            return;
                        }

                        if (outcome.Cleared || !outcome.AwaitsModel)
                        {
                            // 结构化完成回调；204/106 终止由 HandleAck/SyncState 携带原请求 stamp 委托，
                            // 不得在用户回调后再 Capture 当前 stamp。
                            done(outcome.Code, GameplayCompletionFormatter.Format(outcome));
                        }
                        // RecordedAwaitModel是中间状态；模型确认后的Completed才交付完成回调。
                    });
            }
            catch (Exception ex)
            {
                done(-2, GameplayCompletionFormatter.FormatLocal(-2, ex.GetType().Name));
            }
        }

        public void SyncState(Action<int, string> done)
        {
            if (done == null) throw new ArgumentNullException(nameof(done));
            if (_requestFullSync == null)
            {
                done(GameplayBridgeBinding.CodeNotReady,
                    GameplayCompletionFormatter.FormatLocal(GameplayBridgeBinding.CodeNotReady, "sync-coordinator-not-installed"));
                return;
            }

            // 捕获本请求 stamp；用户完成回调可开启新认证，终止仍用原 stamp
            SessionStamp requestStamp = _captureStamp();
            _requestFullSync(SyncReason.Explicit, result =>
            {
                if (result == null)
                {
                    done(-2, GameplayCompletionFormatter.FormatLocal(-2, "sync-null-result"));
                    return;
                }

                if (result.Code != 0)
                {
                    try
                    {
                        done(result.Code, GameplayCompletionFormatter.Format(
                            result.Code, "Local", result.Message ?? "sync-failed"));
                    }
                    finally
                    {
                        MaybeNotifyTerminal(
                            new CommandClientOutcome(
                                result.Code, result.Message ?? "sync-terminal", 0, false, false,
                                disposition: "Terminal"),
                            requestStamp);
                    }

                    return;
                }

                done(0, GameplayCompletionFormatter.Format(
                    0, "SyncApplied", GameplayInt64.Format(result.AppliedRevision), result.AppliedRevision, !IsBusy));
            });
        }

        public Action SubscribeDelta(Action<string> onDeltaJson)
        {
            IDisposable sub = _transport.SubscribeDelta(bytes =>
            {
                if (GameplayDtoCodec.TryDecodeDelta(bytes, out string json, out _))
                    onDeltaJson?.Invoke(json);
            });
            return () => sub?.Dispose();
        }

        /// <summary>仅注销观察者；不取消已持久命令、不清槽。</summary>
        public void ClearObserver()
        {
            lock (_gate)
            {
                _observer = null;
            }
        }

        /// <summary>兼容旧签名：attempt 取自已持久 pending。</summary>
        public UniTask RecoverAsync(
            SessionStamp stamp,
            string environmentKey,
            string accountKey,
            bool previousScopeClosed,
            CancellationToken ct)
            => RecoverAsync(stamp, environmentKey, accountKey, authAttempt: 0, previousScopeClosed, ct);

        /// <summary>
        /// 恢复路径：校验身份后仅重绑传输 stamp（scope/op/bytes/hash 不变）；Unknown 先 Query。
        /// </summary>
        public async UniTask RecoverAsync(
            SessionStamp stamp,
            string environmentKey,
            string accountKey,
            int authAttempt,
            bool previousScopeClosed,
            CancellationToken ct)
        {
            _ = previousScopeClosed;
            lock (_gate) { _recoveryPhase = "loading"; _recoveryError = ""; }
            PendingLoadResult load = _store.TryLoad(environmentKey, accountKey);
            if (load.Kind == PendingLoadKind.CorruptLocked || load.Kind == PendingLoadKind.MissingPayloadLocked)
            {
                lock (_gate)
                {
                    _busy = true;
                    _lockReason = "pending-locked:" + load.Message;
                }
                return;
            }

            if (load.Kind != PendingLoadKind.Ok || load.Command == null)
                return;

            PendingCommand pending = load.Command;
            if (!string.Equals(pending.EnvironmentKey, environmentKey, StringComparison.Ordinal)
                || !string.Equals(pending.AccountKey, accountKey, StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _busy = true;
                    _lockReason = "pending-locked:identity-mismatch";
                }
                return;
            }

            if (!string.IsNullOrEmpty(stamp.PlayerId)
                && !string.Equals(pending.PlayerId, stamp.PlayerId, StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _busy = true;
                    _lockReason = "pending-locked:player-mismatch";
                }
                return;
            }

            PendingCommand rebound = pending.WithTransportStamp(
                authAttempt > 0 ? authAttempt : pending.AuthAttempt,
                stamp.Epoch,
                stamp.ConnectionGeneration);
            try
            {
                _store.SaveAtomic(rebound);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _busy = true;
                    _lockReason = "rebind-save-failed";
                }
                return;
            }

            long slotGen;
            lock (_gate)
            {
                _active = rebound;
                _busy = true;
                _slotReserved = true;
                slotGen = ++_slotGeneration;
            }

            PendingCommand capture = rebound;
            long gen = slotGen;
            await RunObservedAsync(() => DrivePendingAsync(capture, stamp, gen, ct));
        }

        public void ObserveModelRevision(SessionStamp stamp, long revision)
        {
            PendingCommand active;
            Action<CommandClientOutcome> obs;
            long slotGen;
            lock (_gate)
            {
                active = _active;
                obs = _observer;
                slotGen = _slotGeneration;
                if (active == null || active.State != PendingCommandState.RecordedAwaitModel)
                    return;
                if (!active.HasConfirmedRevision)
                    return;
                if (stamp.Epoch != active.Epoch || stamp.ConnectionGeneration != active.ConnectionGeneration)
                    return;
            }

            if (revision < active.ConfirmedRevision)
                return;

            if (!TryMutateDisk(active, stamp, slotGen, () =>
            {
                _store.Complete(active.EnvironmentKey, active.AccountKey, active.OperationScopeId, active.OpId);
            }))
            {
                return;
            }

            FarmCommandDisplayResults frozenDisplay = FarmCommandDisplayResults.None;
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return;
                if (_active == null || _active.OpId != active.OpId || _active.Epoch != active.Epoch)
                    return;
                if (_slotDisplayResultsGeneration == slotGen && _slotDisplayResults != null)
                    frozenDisplay = _slotDisplayResults.Clone();
                _slotDisplayResults = null;
                _slotDisplayResultsGeneration = 0;
                _active = null;
                _busy = false;
                _slotReserved = false;
                _recoveryPhase = "recorded-cleared";
                _observer = null;
            }
            obs?.Invoke(new CommandClientOutcome(
                0, "completed", active.ConfirmedRevision,
                awaitsModel: false, cleared: true, disposition: "Recorded",
                displayResults: frozenDisplay));
        }

        public void BeginWrite(
            CommandIntent intent,
            long opId,
            long scopeId,
            long expectedRevision,
            int configVersion,
            string environmentKey,
            string accountKey,
            string playerId,
            int authAttempt,
            long epoch,
            long connectionGeneration,
            Action<CommandClientOutcome> observer)
        {
            long slotGen;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_lockReason) && _lockReason.StartsWith("pending-locked", StringComparison.Ordinal))
                    throw new InvalidOperationException(_lockReason);
                if (_busy || _active != null || _slotReserved)
                    throw new InvalidOperationException("busy");
                _slotReserved = true;
                _busy = true;
                slotGen = ++_slotGeneration;
                // 新槽不得借用旧槽冻结结果
                _slotDisplayResults = null;
                _slotDisplayResultsGeneration = 0;
            }

            PendingCommand pending;
            try
            {
                pending = new PendingCommand(intent, opId, scopeId, expectedRevision, configVersion)
                    .WithIdentity(environmentKey, accountKey, playerId, authAttempt, epoch, connectionGeneration, _utcNowMs());
                GameplayDtoCodec.BuildWireRequest(pending);
                pending.EnsureDurableHash();
                _store.SaveAtomic(pending.WithState(PendingCommandState.Prepared));
            }
            catch
            {
                lock (_gate)
                {
                    if (_slotGeneration == slotGen)
                    {
                        _slotReserved = false;
                        _busy = false;
                        _active = null;
                        _observer = null;
                    }
                }
                throw;
            }

            PendingCommand inFlight = pending.WithState(PendingCommandState.InFlight);
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return;
                _active = inFlight;
                _observer = observer;
            }

            SessionStamp stamp = new SessionStamp(environmentKey, playerId, scopeId.ToString(), epoch, connectionGeneration);
            PendingCommand capture = inFlight;
            long gen = slotGen;
            _inflight = RunObservedAsync(() => DrivePendingAsync(capture, stamp, gen, CancellationToken.None));
        }

        async UniTask RunObservedAsync(Func<UniTask> work)
        {
            long id;
            lock (_gate) { id = ++_runId; }
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                ObserveException(ex.GetType().Name);
                lock (_gate)
                {
                    if (id == _runId)
                    {
                        _busy = true;
                        _recoveryPhase = "drive-failed";
                        _recoveryError = ex.GetType().Name;
                        if (string.IsNullOrEmpty(_lockReason))
                            _lockReason = "drive-failed:" + ex.GetType().Name;
                    }
                }
            }
        }

        async UniTask DrivePendingAsync(
            PendingCommand seed, SessionStamp stamp, long slotGen, CancellationToken ct)
        {
            PendingCommand pending = seed;
            if (pending == null)
                return;
            if (!LiveSlot(pending, stamp, slotGen))
                return;

            bool needsQuery = pending.State == PendingCommandState.Unknown
                              || pending.State == PendingCommandState.Prepared
                              || pending.State == PendingCommandState.RecordedAwaitModel;

            if (needsQuery)
            {
                GameplayWireResponse q = await QueryBoundAsync(pending, stamp, slotGen, ct);
                if (!LiveSlot(pending, stamp, slotGen))
                    return;
                q = BindFarmQueryDisplay(pending, q);

                if (q.Code == 106 || q.Code == 204 || q.Code == 112)
                {
                    await HandleAckAsync(pending, q, stamp, slotGen);
                    return;
                }

                if (string.Equals(q.Disposition, "Unknown", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(q.Disposition))
                {
                    // 无权威：不得盲发/清槽；不得携带提交结果
                    ClearSlotDisplayResults(slotGen);
                    lock (_gate)
                    {
                        _recoveryPhase = "query-unknown-retain";
                        if (string.IsNullOrEmpty(_lockReason)) _lockReason = "unknown-retain";
                    }
                    return;
                }

                if (string.Equals(q.Disposition, "Recorded", StringComparison.OrdinalIgnoreCase))
                {
                    if (q.Code != 0)
                    {
                        await CompleteRecordedRefusalAsync(pending, q, stamp, slotGen);
                        return;
                    }

                    await EnterRecordedAsync(pending, q, stamp, slotGen);
                    return;
                }

                if (string.Equals(q.Disposition, "Expired", StringComparison.OrdinalIgnoreCase) || q.Code == 111)
                {
                    await ArchiveDurableAsync(pending, "expired-query", 111, "expired-archived", stamp, slotGen);
                    return;
                }

                if (string.Equals(q.Disposition, "NotRecorded", StringComparison.OrdinalIgnoreCase))
                {
                    if (!q.ScopeOpen)
                    {
                        await ArchiveDurableAsync(
                            pending, "closed-scope-archive", 0,
                            "archived-closed-scope-notice-required", stamp, slotGen);
                        return;
                    }

                    if (pending.State == PendingCommandState.RecordedAwaitModel)
                        return;
                    // 开 scope NotRecorded → 下落重发完全相同 payload
                }
                else if (string.Equals(q.Disposition, "NotAccepted", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleAckAsync(pending, q, stamp, slotGen);
                    return;
                }
                else
                {
                    // 未识别查询结果：停住
                    return;
                }
            }

            if (pending.State == PendingCommandState.RecordedAwaitModel)
                return;

            await SendExactAsync(pending, stamp, slotGen, ct);
        }

        async UniTask SendExactAsync(
            PendingCommand pending, SessionStamp stamp, long slotGen, CancellationToken ct)
        {
            if (pending == null || !pending.HasWireBytes)
                return;
            if (!LiveSlot(pending, stamp, slotGen))
                return;

            var req = new GameplayWireRequest(
                pending.Intent.CommandKey,
                pending.WireBytes,
                pending.Epoch,
                pending.ConnectionGeneration,
                pending.AuthAttempt);

            GameplayWireResponse resp;
            try
            {
                if (!LiveSlot(pending, stamp, slotGen))
                    return;
                resp = await _transport.SendAsync(req, TimeSpan.FromSeconds(8), ct);
                if (!LiveSlot(pending, stamp, slotGen))
                    return;
            }
            catch (OperationCanceledException)
            {
                if (!LiveSlot(pending, stamp, slotGen))
                    return;
                await MarkUnknownAsync(pending, stamp, slotGen);
                return;
            }
            catch (TimeoutException)
            {
                if (!LiveSlot(pending, stamp, slotGen))
                    return;
                await MarkUnknownAsync(pending, stamp, slotGen);
                return;
            }
            catch (Exception)
            {
                if (!LiveSlot(pending, stamp, slotGen))
                    return;
                await MarkUnknownAsync(pending, stamp, slotGen);
                return;
            }

            await HandleAckAsync(pending, resp, stamp, slotGen);
        }

        async UniTask<GameplayWireResponse> QueryBoundAsync(
            PendingCommand pending, SessionStamp stamp, long slotGen, CancellationToken ct)
        {
            if (!LiveSlot(pending, stamp, slotGen))
                return new GameplayWireResponse(-1, "Unknown", 0, false, true, 0, 1, Array.Empty<byte>());
            lock (_gate) { _recoveryPhase = "query-bound"; }
            GameplayWireResponse q = await _query.QueryAsync(pending.OperationScopeId, pending.OpId, ct);
            if (!LiveSlot(pending, stamp, slotGen))
                return new GameplayWireResponse(-1, "Unknown", 0, false, true, 0, 1, Array.Empty<byte>());
            // An identified Query reply must belong to this exact durable operation.
            // Legacy test/port responses may omit identities;they cannot carry display results below.
            if (q != null
                && ((q.OperationScopeId > 0 && q.OperationScopeId != pending.OperationScopeId)
                    || (q.OpId > 0 && q.OpId != pending.OpId)))
            {
                lock (_gate) { _recoveryPhase = "query-foreign-ack"; }
                return new GameplayWireResponse(-1, "Unknown", 0, false, true, 0, 1, Array.Empty<byte>());
            }
            lock (_gate) { _recoveryPhase = "query-result:" + (q?.Disposition ?? "Unknown"); }
            return q ?? new GameplayWireResponse(-1, "Unknown", 0, false, true, 0, 1, Array.Empty<byte>());
        }

        async UniTask MarkUnknownAsync(PendingCommand pending, SessionStamp stamp, long slotGen, int code = 0)
        {
            if (!LiveSlot(pending, stamp, slotGen))
                return;

            lock (_gate)
            {
                // 禁止把已升级的 AwaitModel 目标回退成 Unknown
                if (_active != null
                    && _active.OpId == pending.OpId
                    && _active.State == PendingCommandState.RecordedAwaitModel
                    && pending.State != PendingCommandState.RecordedAwaitModel)
                {
                    return;
                }
            }

            var next = pending.WithState(PendingCommandState.Unknown);
            if (!TryMutateDisk(pending, stamp, slotGen, () => _store.SaveAtomic(next)))
                return;

            Action<CommandClientOutcome> observer;
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return;
                _busy = true;
                if (string.IsNullOrEmpty(_lockReason))
                    _lockReason = "unknown-retain";
                observer = _observer;
                if (_active != null
                    && _active.OpId == pending.OpId
                    && _active.Epoch == pending.Epoch
                    && _active.ConnectionGeneration == pending.ConnectionGeneration
                    && _active.State != PendingCommandState.RecordedAwaitModel)
                {
                    _active = next;
                }
            }

            try
            {
                observer?.Invoke(new CommandClientOutcome(
                    code, "unknown-retain", 0, false, false, disposition: "Unknown"));
            }
            catch { /* UI观察者不能改变全局持久恢复状态 */ }

            await UniTask.CompletedTask;
        }

        async UniTask HandleAckAsync(
            PendingCommand pending, GameplayWireResponse resp, SessionStamp stamp, long slotGen)
        {
            if (!LiveSlot(pending, stamp, slotGen))
                return;

            Action<CommandClientOutcome> obs;
            lock (_gate) { obs = _observer; }

            string disp = resp?.Disposition ?? "";
            int code = resp?.Code ?? 0;

            // 112 / 冲突：停在任何清槽/Recorded 之前；不得带提交结果
            if (code == 112 || string.Equals(disp, "PayloadConflict", StringComparison.OrdinalIgnoreCase))
            {
                ClearSlotDisplayResults(slotGen);
                lock (_gate)
                {
                    if (_slotGeneration == slotGen)
                    {
                        _busy = true;
                        _lockReason = "payload-conflict-112";
                    }
                }
                obs?.Invoke(new CommandClientOutcome(112, "payload-conflict", 0, false, false, disposition: "PayloadConflict"));
                return;
            }

            // 110 / 外域 / 会话权威错误：保留 pending，不 Cleared，不猜 next
            // 106/204：保留真实码与 durable pending，用本请求 stamp 委托终止（非回调后再 Capture）
            if (code == 110 || code == 106 || code == 204)
            {
                ClearSlotDisplayResults(slotGen);
                lock (_gate)
                {
                    if (_slotGeneration == slotGen)
                    {
                        _busy = true;
                        if (string.IsNullOrEmpty(_lockReason))
                            _lockReason = code == 110 ? "not-accepted-retain" : "terminal-retain";
                    }
                }

                string terminalDisp = code == 110 ? "NotAccepted" : "Terminal";
                var terminalOutcome = new CommandClientOutcome(
                    code, "foreign-or-auth-retain", 0, false, false, disposition: terminalDisp);
                try { obs?.Invoke(terminalOutcome); }
                finally { MaybeNotifyTerminal(terminalOutcome, stamp); }
                return;
            }

            if (string.IsNullOrEmpty(disp)
                || string.Equals(disp, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                ClearSlotDisplayResults(slotGen);
                await MarkUnknownAsync(pending, stamp, slotGen, code);
                return;
            }

            if (string.Equals(disp, "NotAccepted", StringComparison.OrdinalIgnoreCase))
            {
                // 单次传输 NotAccepted（101/104/105…）不能证明此前尝试永久未执行：
                // 保留 durable pending、挡新写、不猜 next、不自动换 op。
                ClearSlotDisplayResults(slotGen);
                lock (_gate)
                {
                    if (_slotGeneration == slotGen)
                    {
                        _busy = true;
                        _lockReason = "not-accepted-retain";
                    }
                }

                string msg = resp.HasNextOp && resp.NextOpId > 0
                    ? "not-accepted;next=" + resp.NextOpId
                    : "not-accepted;next-absent";
                obs?.Invoke(new CommandClientOutcome(code, msg, 0, false, false, disposition: "NotAccepted"));
                return;
            }

            if (string.Equals(disp, "Expired", StringComparison.OrdinalIgnoreCase) || code == 111)
            {
                ClearSlotDisplayResults(slotGen);
                await ArchiveDurableAsync(pending, "expired", 111, "expired-archived", stamp, slotGen);
                return;
            }

            if (string.Equals(disp, "Recorded", StringComparison.OrdinalIgnoreCase))
            {
                if (code != 0)
                {
                    await CompleteRecordedRefusalAsync(pending, resp, stamp, slotGen);
                    return;
                }

                await EnterRecordedAsync(pending, resp, stamp, slotGen);
                return;
            }

            await MarkUnknownAsync(pending, stamp, slotGen);
        }

        async UniTask CompleteRecordedRefusalAsync(
            PendingCommand pending, GameplayWireResponse resp, SessionStamp stamp, long slotGen)
        {
            if (!TryMutateDisk(pending, stamp, slotGen, () =>
                    _store.Complete(
                        pending.EnvironmentKey, pending.AccountKey,
                        pending.OperationScopeId, pending.OpId)))
            {
                return;
            }

            ClearSlotDisplayResults(slotGen);
            Action<CommandClientOutcome> obs;
            lock (_gate) { obs = _observer; }
            ClearActiveIfSame(pending, slotGen);
            if (resp.HasNextOp && resp.NextOpId > 0)
                _onAuthoritativeAck?.Invoke(resp);
            // 拒绝路径不携带批量逐项成功发明
            obs?.Invoke(new CommandClientOutcome(
                resp.Code, "recorded-refusal", resp.CommittedRevision, false, true, disposition: "Recorded"));
            await UniTask.CompletedTask;
        }

        async UniTask EnterRecordedAsync(
            PendingCommand pending, GameplayWireResponse resp, SessionStamp stamp, long slotGen)
        {
            if (!LiveSlot(pending, stamp, slotGen))
                return;

            long committed = resp.CommittedRevision;
            if (committed < 0)
                committed = 0;

            var awaitModel = pending
                .WithConfirmedRevision(committed)
                .WithState(PendingCommandState.RecordedAwaitModel);
            if (!TryMutateDisk(pending, stamp, slotGen, () => _store.SaveAtomic(awaitModel)))
                return;

            FarmCommandDisplayResults capture = CaptureSlotDisplay(pending, resp);
            Action<CommandClientOutcome> obs;
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return;
                if (_active == null
                    || _active.OpId != pending.OpId
                    || _active.Epoch != pending.Epoch
                    || _active.ConnectionGeneration != pending.ConnectionGeneration)
                {
                    return;
                }

                _active = awaitModel;
                _recoveryPhase = "recorded-await-model";
                // 槽位私有冻结；延迟模型到达后最终回调仍用此列表
                _slotDisplayResults = capture.Clone();
                _slotDisplayResultsGeneration = slotGen;
                obs = _observer;
            }

            if (resp.HasNextOp && resp.NextOpId > 0)
                _onAuthoritativeAck?.Invoke(resp);

            // 中间态：不向 Lua 交付（Send 过滤 AwaitsModel）；不把 results 放进该 outcome
            obs?.Invoke(new CommandClientOutcome(0, "recorded-await-model", committed, true, false, disposition: "Recorded"));

            long modelRev = _modelRevision();
            if (modelRev >= committed)
            {
                ObserveModelRevision(stamp, modelRev);
            }
            else if (_requestFullSync != null)
            {
                // Recorded 成功且模型未到确认 revision（含无 delta 的 legacy 回执）→ 合并 FullSync；
                // 完成仍只在 ObserveModelRevision 达标后。
                SessionStamp syncStamp = stamp;
                long need = committed;
                _requestFullSync(SyncReason.RecordedAwaitModel, result =>
                {
                    if (result == null || result.Code != 0)
                        return;
                    long live = _modelRevision();
                    if (live < need && result.AppliedRevision > live)
                        live = result.AppliedRevision;
                    if (live >= need)
                        ObserveModelRevision(syncStamp, live);
                });
            }

            await UniTask.CompletedTask;
        }

        async UniTask ArchiveDurableAsync(
            PendingCommand pending, string reason, int code, string message,
            SessionStamp stamp, long slotGen)
        {
            if (!TryMutateDisk(pending, stamp, slotGen, () =>
                    _store.Archive(pending.EnvironmentKey, pending.AccountKey, reason)))
            {
                return;
            }

            Action<CommandClientOutcome> obs;
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                {
                    obs = null;
                }
                else if (_active != null
                         && _active.OpId == pending.OpId
                         && _active.Epoch == pending.Epoch)
                {
                    _active = null;
                    _busy = false;
                    _slotReserved = false;
                    _lockReason = message;
                    obs = _observer;
                    _observer = null;
                }
                else
                {
                    obs = null;
                    _lockReason = message;
                }
            }

            obs?.Invoke(new CommandClientOutcome(code, message, 0, false, true, disposition: "Expired"));
            await UniTask.CompletedTask;
        }

        void MaybeNotifyTerminal(CommandClientOutcome outcome, SessionStamp requestStamp)
        {
            if (outcome == null || _onTerminalSession == null)
                return;
            if (outcome.Code != 204 && outcome.Code != 106)
                return;
            try
            {
                _onTerminalSession(outcome.Code, requestStamp);
            }
            catch
            {
                /* 终止委托失败不得抛回命令路径 */
            }
        }

        void ClearActiveIfSame(PendingCommand pending, long slotGen)
        {
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return;
                if (_active != null
                    && _active.OpId == pending.OpId
                    && _active.Epoch == pending.Epoch
                    && _active.ConnectionGeneration == pending.ConnectionGeneration)
                {
                    _active = null;
                    _busy = false;
                    _slotReserved = false;
                    _observer = null;
                    if (_slotDisplayResultsGeneration == slotGen)
                    {
                        _slotDisplayResults = null;
                        _slotDisplayResultsGeneration = 0;
                    }
                }
            }
        }

        void ClearSlotDisplayResults(long slotGen)
        {
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return;
                if (_slotDisplayResultsGeneration == slotGen)
                {
                    _slotDisplayResults = null;
                    _slotDisplayResultsGeneration = 0;
                }
            }
        }

        /// <summary>
        /// Query ResultBlob → 冻结 farm-batch-summary-v1 展示；非 protobuf Resp。
        /// </summary>
        static GameplayWireResponse BindFarmQueryDisplay(PendingCommand pending, GameplayWireResponse q)
        {
            if (q == null || pending?.Intent == null)
                return q;
            string key = pending.Intent.CommandKey;
            if (!CmdResultArchiveDisplayCodec.IsFarmBatchCommandKey(key))
                return q;
            if (q.OperationScopeId != pending.OperationScopeId || q.OpId != pending.OpId)
            {
                return new GameplayWireResponse(
                    q.Code, q.Disposition, q.NextOpId, q.HasNextOp, q.ScopeOpen,
                    q.CommittedRevision, q.ConfigVersion, q.ResultBlob,
                    q.OperationScopeId, q.OpId, FarmCommandDisplayResults.ErrorWithKind(key));
            }
            if (!string.Equals(q.Disposition, "Recorded", StringComparison.OrdinalIgnoreCase))
            {
                return new GameplayWireResponse(
                    q.Code, q.Disposition, q.NextOpId, q.HasNextOp, q.ScopeOpen,
                    q.CommittedRevision, q.ConfigVersion, q.ResultBlob,
                    q.OperationScopeId, q.OpId, FarmCommandDisplayResults.None);
            }

            FarmCommandDisplayResults display = CmdResultArchiveDisplayCodec.TryProjectQueryBlob(
                q.ResultBlob, q.Code, q.CommittedRevision, q.ConfigVersion, key);
            return new GameplayWireResponse(
                q.Code, q.Disposition, q.NextOpId, q.HasNextOp, q.ScopeOpen,
                q.CommittedRevision, q.ConfigVersion, q.ResultBlob,
                q.OperationScopeId, q.OpId, display);
        }

        /// <summary>直连 Resp 或 Query 恢复的展示信封；命令键边界校验。</summary>
        static FarmCommandDisplayResults CaptureSlotDisplay(PendingCommand pending, GameplayWireResponse resp)
        {
            if (resp == null || pending?.Intent == null)
                return FarmCommandDisplayResults.None;
            string key = pending.Intent.CommandKey;
            if (!CmdResultArchiveDisplayCodec.IsFarmBatchCommandKey(key))
                return FarmCommandDisplayResults.None;

            FarmCommandDisplayResults d = resp.DisplayResults;
            if (d == null || d.Availability == FarmResultsAvailability.None)
                return FarmCommandDisplayResults.None;

            if (d.Availability == FarmResultsAvailability.Present
                && !string.IsNullOrEmpty(d.ArchiveKind)
                && !string.Equals(d.ArchiveKind, key, StringComparison.Ordinal))
            {
                return FarmCommandDisplayResults.ErrorWithKind(d.ArchiveKind);
            }

            return d.Clone();
        }

        bool TryMutateDisk(PendingCommand pending, SessionStamp stamp, long slotGen, Action mutate)
        {
            if (!LiveSlot(pending, stamp, slotGen))
                return false;
            try
            {
                mutate();
                return LiveSlot(pending, stamp, slotGen);
            }
            catch (Exception)
            {
                LockIo("disk-mutate-failed");
                return false;
            }
        }

        bool LiveSlot(PendingCommand pending, SessionStamp stamp, long slotGen)
        {
            if (pending == null) return false;
            // 同时比对当前会话，不能只比较两个同样已经过期的捕获值。
            if (!stamp.Equals(_captureStamp())) return false;
            lock (_gate)
            {
                if (_slotGeneration != slotGen)
                    return false;
            }

            return stamp.Epoch == pending.Epoch
                   && stamp.ConnectionGeneration == pending.ConnectionGeneration;
        }

        void LockIo(string reason)
        {
            lock (_gate)
            {
                _busy = true;
                _lockReason = reason;
            }
            ObserveException(reason);
        }

        static void ObserveException(string tag)
        {
            _ = tag;
        }
    }

    /// <summary>写权威分配结果（由 GameplayRuntime 填充；非 Lua 面）。</summary>
    public sealed class WriteAuthority
    {
        public long OpId;
        public long OperationScopeId;
        public long ExpectedRevision;
        public int ConfigVersion;
        public string EnvironmentKey;
        public string AccountKey;
        public string PlayerId;
        public int AuthAttempt;
        public long Epoch;
        public long ConnectionGeneration;
    }
}
