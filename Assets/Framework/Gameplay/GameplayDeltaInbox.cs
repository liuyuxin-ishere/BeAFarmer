using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;
using Google.Protobuf;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// C06：唯一 Batch 订阅者与 FullSync 拥有者。不直接改资产；结果回传到模型端口。
    /// 每趟 Sync 有不可变 runId；旧 run 的 finally/catch 不得影响新会话。
    /// 缓冲溢出记录 dropped revision 地板，写锁保持到快照覆盖后再解冻，禁止自动同步风暴。
    /// </summary>
    internal sealed class GameplayDeltaInbox : IDisposable
    {
        public const int MaxBufferedBatches = 64;
        public const int MaxBufferedBytes = 256 * 1024;

        readonly object _gate = new object();
        readonly IGameplayModelPort _model;
        readonly IMainThreadDispatcher _dispatcher;
        readonly Func<SessionStamp> _captureStamp;
        readonly Func<long> _connectionGeneration;
        readonly Func<int> _authAttempt;
        readonly Func<CancellationToken, UniTask<SyncStateResp>> _requestSync;
        readonly Action<SessionStamp, string> _freezeWrites;
        readonly Action<SessionStamp> _clearWriteFreeze;
        readonly Action<long, SessionStamp> _onServerTimeAccepted;
        readonly Action<SessionStamp, long> _onRevisionApplied;

        readonly SortedDictionary<long, DeltaBatchDto> _buffer = new SortedDictionary<long, DeltaBatchDto>();
        int _bufferedBytes;
        IDisposable _deltaSub;
        bool _disposed;

        bool _syncInFlight;
        long _syncRunId;
        long _nextSyncRunId = 1;
        readonly List<Action<SyncAppliedResult>> _syncObservers = new List<Action<SyncAppliedResult>>();
        long _syncEpoch;
        long _syncGeneration;
        int _syncAttempt;
        SyncReason _syncReason;
        bool _writesFrozen;
        string _freezeReason = "";
        /// <summary>溢出丢弃后的最低必达 revision；0 表示无地板。空缓冲不等于已连续。</summary>
        long _droppedRevisionFloor;

        public GameplayDeltaInbox(
            IGameplayModelPort model,
            IMainThreadDispatcher dispatcher,
            IGameplayTransport transport,
            Func<SessionStamp> captureStamp,
            Func<long> connectionGeneration,
            Func<int> authAttempt,
            Func<CancellationToken, UniTask<SyncStateResp>> requestSync,
            Action<SessionStamp, string> freezeWrites,
            Action<SessionStamp> clearWriteFreeze,
            Action<SessionStamp, long> onRevisionApplied,
            Action<long, SessionStamp> onServerTimeAccepted = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            _captureStamp = captureStamp ?? throw new ArgumentNullException(nameof(captureStamp));
            _connectionGeneration = connectionGeneration ?? (() => 0);
            _authAttempt = authAttempt ?? (() => 0);
            _requestSync = requestSync ?? throw new ArgumentNullException(nameof(requestSync));
            _freezeWrites = freezeWrites ?? ((_, __) => { });
            _clearWriteFreeze = clearWriteFreeze ?? (_ => { });
            _onRevisionApplied = onRevisionApplied;
            _onServerTimeAccepted = onServerTimeAccepted;

            _deltaSub = transport.SubscribeDelta(OnTransportDelta);
        }

        public bool IsSyncInFlight
        {
            get { lock (_gate) return _syncInFlight; }
        }

        public bool IsWriteFrozen
        {
            get { lock (_gate) return _writesFrozen; }
        }

        public string FreezeReason
        {
            get { lock (_gate) return _freezeReason; }
        }

        public int BufferedBatchCount
        {
            get { lock (_gate) return _buffer.Count; }
        }

        public int BufferedBytes
        {
            get { lock (_gate) return _bufferedBytes; }
        }

        public long DroppedRevisionFloor
        {
            get { lock (_gate) return _droppedRevisionFloor; }
        }

        void OnTransportDelta(byte[] payload)
        {
            if (_disposed) return;
            long epoch = _captureStamp().Epoch;
            long gen = _connectionGeneration();
            var envelope = new GameplayDeltaEnvelope(payload, epoch, gen);
            PostMain(() => Accept(envelope, _captureStamp()));
        }

        public void Accept(GameplayDeltaEnvelope batch, SessionStamp stamp)
        {
            if (_disposed || batch == null) return;
            if (!StampLive(stamp, batch.Epoch, batch.ConnectionGeneration))
                return;

            if (!TryDecodeBatch(batch, out DeltaBatchDto dto, out string decodeError))
            {
                FreezeAndSync(stamp, SyncReason.Invalid, "delta-decode:" + decodeError);
                return;
            }

            if (!StampLive(stamp, dto.Epoch, dto.ConnectionGeneration))
                return;

            bool syncing;
            lock (_gate) syncing = _syncInFlight;
            if (syncing)
            {
                if (!TryBuffer(dto, out string overflow))
                {
                    FreezeAndSync(stamp, SyncReason.BufferOverflow, overflow);
                }
                return;
            }

            ApplyOrReact(dto, stamp);
        }

        public void AcceptSnapshot(SnapshotEnvelope snapshot, SessionStamp stamp)
        {
            if (_disposed || snapshot == null) return;
            if (!StampLive(stamp, snapshot.Epoch, snapshot.ConnectionGeneration))
                return;

            long current = _model.GetRevision(stamp);
            if (snapshot.Revision < current)
            {
                // 陈旧快照不得回退
                return;
            }

            ModelApplyResult result = _model.ReplaceSnapshot(snapshot, stamp);
            HandleModelResult(result, stamp, fromSync: false);
            if (result.Kind == ModelApplyKind.Applied)
                MaybeUnfreezeAfterProgress(stamp);
        }

        /// <summary>合并为单一在途 SyncState；观察者在主线程收到已应用 revision/错误。</summary>
        public void RequestFullSync(SyncReason reason, Action<SyncAppliedResult> observer)
        {
            if (_disposed)
            {
                observer?.Invoke(new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "inbox-disposed"));
                return;
            }

            SessionStamp stamp = _captureStamp();
            bool start;
            long runId = 0;
            lock (_gate)
            {
                if (observer != null)
                    _syncObservers.Add(observer);
                if (_syncInFlight)
                {
                    start = false;
                }
                else
                {
                    _syncInFlight = true;
                    runId = _nextSyncRunId++;
                    _syncRunId = runId;
                    _syncEpoch = stamp.Epoch;
                    _syncGeneration = _connectionGeneration();
                    _syncAttempt = _authAttempt();
                    _syncReason = reason;
                    start = true;
                }
            }

            if (reason == SyncReason.Gap
                || reason == SyncReason.Invalid
                || reason == SyncReason.BufferOverflow)
            {
                FreezeWrites(stamp, reason.ToString());
            }

            if (start)
                RunSyncAsync(stamp, runId);
        }

        public void Reset(SessionStamp stamp)
        {
            lock (_gate)
            {
                _buffer.Clear();
                _bufferedBytes = 0;
                _droppedRevisionFloor = 0;
                // 使旧 run 失效：抬高 runId，清空观察者，关闭 inflight
                _syncRunId = 0;
                _nextSyncRunId++;
                CancelSyncObservers_NoLock(new SyncAppliedResult(
                    GameplayBridgeBinding.CodeNotReady, 0, "inbox-reset"));
                _syncInFlight = false;
                _writesFrozen = false;
                _freezeReason = "";
            }

            try { _model.Clear(stamp, "inbox-reset"); } catch { /* ignore */ }
            try { _clearWriteFreeze(stamp); } catch { /* ignore */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _deltaSub?.Dispose(); } catch { /* ignore */ }
            _deltaSub = null;
            lock (_gate)
            {
                _buffer.Clear();
                _bufferedBytes = 0;
                _droppedRevisionFloor = 0;
                _syncRunId = 0;
                _nextSyncRunId++;
                CancelSyncObservers_NoLock(new SyncAppliedResult(
                    GameplayBridgeBinding.CodeNotReady, 0, "inbox-disposed"));
                _syncInFlight = false;
            }
        }

        async UniTaskVoid RunSyncAsync(SessionStamp startStamp, long runId)
        {
            SyncAppliedResult terminal = null;
            bool ownsTerminal = false;
            try
            {
                long epoch;
                long gen;
                int attempt;
                lock (_gate)
                {
                    if (_syncRunId != runId || !_syncInFlight)
                    {
                        terminal = new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "stale-sync-run");
                        return;
                    }

                    epoch = _syncEpoch;
                    gen = _syncGeneration;
                    attempt = _syncAttempt;
                }

                if (_captureStamp().Epoch != epoch
                    || _connectionGeneration() != gen
                    || _authAttempt() != attempt)
                {
                    terminal = new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "stale-sync-generation");
                    ownsTerminal = true;
                    return;
                }

                SyncStateResp resp = await _requestSync(CancellationToken.None);

                if (!StillOwnsRun(runId, epoch, gen, attempt))
                {
                    terminal = new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "stale-sync-callback");
                    return;
                }

                SessionStamp live = _captureStamp();
                if (resp == null || resp.Code != 0 || resp.Data == null)
                {
                    int code = resp != null ? resp.Code : -2;
                    terminal = new SyncAppliedResult(
                        code != 0 ? code : -2,
                        _model.GetRevision(live),
                        "sync-state-failed");
                    FreezeWrites(live, "sync-failed");
                    ownsTerminal = true;
                    return;
                }

                if (resp.OperationScopeId > 0
                    && !string.IsNullOrEmpty(live.ScopeId)
                    && !string.Equals(
                        resp.OperationScopeId.ToString(CultureInfo.InvariantCulture),
                        live.ScopeId,
                        StringComparison.Ordinal))
                {
                    terminal = new SyncAppliedResult(-2, _model.GetRevision(live), "sync-scope-mismatch");
                    FreezeWrites(live, "sync-scope-mismatch");
                    ownsTerminal = true;
                    return;
                }

                string json = JsonFormatter.Default.Format(resp.Data);
                long revision = resp.Data.Revision;
                var envelope = new SnapshotEnvelope(json, revision, epoch, gen);
                long current = _model.GetRevision(live);
                if (revision < current)
                {
                    // 陈旧全量：不回退；若缓冲可续则尝试重放，否则保持冻写
                    ReplayBuffer(live);
                    long after = _model.GetRevision(live);
                    terminal = new SyncAppliedResult(0, after, "stale-snapshot-ignored");
                    MaybeUnfreezeAfterProgress(live);
                    ownsTerminal = true;
                    return;
                }

                ModelApplyResult applied = default;
                bool installed = false;
                await RunOnMainAsync(() =>
                {
                    if (!StillOwnsRun(runId, epoch, gen, attempt))
                        return;
                    applied = _model.ReplaceSnapshot(envelope, live);
                    installed = true;
                });

                if (!StillOwnsRun(runId, epoch, gen, attempt))
                {
                    terminal = new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "stale-sync-install");
                    return;
                }

                if (!installed)
                {
                    terminal = new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "stale-sync-install");
                    return;
                }

                if (applied.Kind == ModelApplyKind.Invalid || applied.Kind == ModelApplyKind.Gap)
                {
                    FreezeWrites(live, applied.Error);
                    terminal = new SyncAppliedResult(-2, applied.AppliedRevision, applied.Error);
                    ownsTerminal = true;
                    return;
                }

                NotifyRevision(live, applied.AppliedRevision);
                NoteFloorCovered(live, applied.AppliedRevision);
                if (resp.ServerTime > 0)
                    NotifyServerTime(resp.ServerTime, live);
                ReplayBuffer(live);
                MaybeUnfreezeAfterProgress(live);

                terminal = new SyncAppliedResult(0, _model.GetRevision(live), _syncReason.ToString());
                ownsTerminal = true;
            }
            catch (Exception ex)
            {
                if (StillOwnsRun(runId, -1, -1, -1, ignoreStamp: true)
                    && _captureStamp().Equals(startStamp)
                    && _connectionGeneration() == startStamp.ConnectionGeneration)
                {
                    SessionStamp live = _captureStamp();
                    FreezeWrites(live, "sync-exception");
                    terminal = new SyncAppliedResult(-2, _model.GetRevision(live), ex.GetType().Name);
                    ownsTerminal = true;
                }
                else
                {
                    terminal = new SyncAppliedResult(GameplayBridgeBinding.CodeNotReady, 0, "stale-sync-exception");
                }
            }
            finally
            {
                List<Action<SyncAppliedResult>> observers = null;
                lock (_gate)
                {
                    if (_syncRunId == runId)
                    {
                        _syncInFlight = false;
                        _syncRunId = 0;
                        observers = new List<Action<SyncAppliedResult>>(_syncObservers);
                        _syncObservers.Clear();
                    }
                }

                // 仅本 run 仍拥有观察者列表时通知；旧 run 不得完成新会话观察者
                if (observers != null && observers.Count > 0)
                {
                    SyncAppliedResult result = (ownsTerminal ? terminal : null)
                        ?? new SyncAppliedResult(-2, 0, "sync-incomplete");
                    PostMain(() =>
                    {
                        if (_disposed || !_captureStamp().Equals(startStamp)
                            || _connectionGeneration() != startStamp.ConnectionGeneration)
                            return;
                        for (int i = 0; i < observers.Count; i++)
                        {
                            try { observers[i]?.Invoke(result); }
                            catch { /* isolate */ }
                        }
                    });
                }
            }
        }

        bool StillOwnsRun(long runId, long epoch, long gen, int attempt, bool ignoreStamp = false)
        {
            lock (_gate)
            {
                if (_disposed || _syncRunId != runId || !_syncInFlight)
                    return false;
                if (ignoreStamp)
                    return true;
                if (_syncEpoch != epoch || _syncGeneration != gen || _syncAttempt != attempt)
                    return false;
            }

            if (ignoreStamp)
                return true;

            return _captureStamp().Epoch == epoch
                   && _connectionGeneration() == gen
                   && _authAttempt() == attempt;
        }

        void ApplyOrReact(DeltaBatchDto dto, SessionStamp stamp)
        {
            long current = _model.GetRevision(stamp);
            if (dto.NewRevision <= current && dto.BaseRevision < current)
            {
                _ = ModelApplyResult.Duplicate(current);
                return;
            }

            if (dto.BaseRevision > current)
            {
                if (!TryBuffer(dto, out string overflow))
                {
                    FreezeAndSync(stamp, SyncReason.BufferOverflow, overflow);
                    return;
                }

                FreezeAndSync(stamp, SyncReason.Gap, "gap-base=" + dto.BaseRevision + ";have=" + current);
                return;
            }

            if (dto.BaseRevision < current)
            {
                return;
            }

            ModelApplyResult result = _model.ApplyBatch(dto, stamp);
            HandleModelResult(result, stamp, fromSync: false);
            if (result.Kind == ModelApplyKind.Applied)
            {
                NoteFloorCovered(stamp, result.AppliedRevision);
                if (dto.ServerTimeMs > 0)
                    NotifyServerTime(dto.ServerTimeMs, stamp);
                TryDrainBuffer(stamp);
                MaybeUnfreezeAfterProgress(stamp);
            }
        }

        void NotifyServerTime(long serverTimeMs, SessionStamp stamp)
        {
            if (serverTimeMs <= 0 || _onServerTimeAccepted == null)
                return;
            try { _onServerTimeAccepted(serverTimeMs, stamp); }
            catch { /* 显示时钟不得破坏模型路径 */ }
        }

        void HandleModelResult(ModelApplyResult result, SessionStamp stamp, bool fromSync)
        {
            if (result.Kind == ModelApplyKind.Applied)
            {
                NotifyRevision(stamp, result.AppliedRevision);
                return;
            }

            if (result.Kind == ModelApplyKind.Duplicate)
                return;

            if (result.Kind == ModelApplyKind.Gap)
            {
                FreezeAndSync(stamp, SyncReason.Gap, result.Error);
                return;
            }

            if (result.Kind == ModelApplyKind.Invalid)
            {
                FreezeAndSync(stamp, SyncReason.Invalid, result.Error);
            }
        }

        void FreezeAndSync(SessionStamp stamp, SyncReason reason, string detail)
        {
            FreezeWrites(stamp, string.IsNullOrEmpty(detail) ? reason.ToString() : detail);
            RequestFullSync(reason, null);
        }

        void FreezeWrites(SessionStamp stamp, string reason)
        {
            lock (_gate)
            {
                _writesFrozen = true;
                _freezeReason = reason ?? "";
            }
            try { _freezeWrites(stamp, reason); } catch { /* ignore */ }
        }

        void Unfreeze(SessionStamp stamp)
        {
            lock (_gate)
            {
                _writesFrozen = false;
                _freezeReason = "";
            }
            try { _clearWriteFreeze(stamp); } catch { /* ignore */ }
        }

        void NoteFloorCovered(SessionStamp stamp, long appliedRevision)
        {
            lock (_gate)
            {
                if (_droppedRevisionFloor > 0 && appliedRevision >= _droppedRevisionFloor)
                    _droppedRevisionFloor = 0;
            }
        }

        void MaybeUnfreezeAfterProgress(SessionStamp stamp)
        {
            lock (_gate)
            {
                if (_droppedRevisionFloor > 0
                    && _model.GetRevision(stamp) < _droppedRevisionFloor)
                {
                    // 空缓冲只说明溢出丢失，不是已连续追平
                    return;
                }

                if (!ContiguousOk_NoLock(stamp))
                    return;
            }

            Unfreeze(stamp);
        }

        bool ContiguousOk_NoLock(SessionStamp stamp)
        {
            if (_droppedRevisionFloor > 0
                && _model.GetRevision(stamp) < _droppedRevisionFloor)
                return false;

            if (_buffer.Count == 0) return true;
            long current = _model.GetRevision(stamp);
            foreach (var kv in _buffer)
            {
                if (kv.Value.BaseRevision > current)
                    return false;
            }
            return true;
        }

        void ReplayBuffer(SessionStamp stamp)
        {
            TryDrainBuffer(stamp);
        }

        void TryDrainBuffer(SessionStamp stamp)
        {
            while (true)
            {
                DeltaBatchDto next;
                lock (_gate)
                {
                    long current = _model.GetRevision(stamp);
                    if (!_buffer.TryGetValue(current, out next))
                        return;
                    _buffer.Remove(current);
                    _bufferedBytes -= next.EncodedBytes;
                    if (_bufferedBytes < 0) _bufferedBytes = 0;
                }

                ModelApplyResult result = _model.ApplyBatch(next, stamp);
                if (result.Kind == ModelApplyKind.Applied)
                {
                    NotifyRevision(stamp, result.AppliedRevision);
                    NoteFloorCovered(stamp, result.AppliedRevision);
                    continue;
                }

                if (result.Kind == ModelApplyKind.Duplicate)
                    continue;

                HandleModelResult(result, stamp, fromSync: true);
                return;
            }
        }

        bool TryBuffer(DeltaBatchDto dto, out string error)
        {
            error = null;
            lock (_gate)
            {
                if (_buffer.ContainsKey(dto.BaseRevision))
                {
                    DeltaBatchDto existing = _buffer[dto.BaseRevision];
                    if (dto.NewRevision <= existing.NewRevision)
                        return true;
                    _bufferedBytes -= existing.EncodedBytes;
                    _buffer[dto.BaseRevision] = dto;
                    _bufferedBytes += dto.EncodedBytes;
                }
                else
                {
                    if (_buffer.Count >= MaxBufferedBatches
                        || _bufferedBytes + dto.EncodedBytes > MaxBufferedBytes)
                    {
                        long floor = dto.NewRevision;
                        foreach (var kv in _buffer)
                        {
                            if (kv.Value.NewRevision > floor)
                                floor = kv.Value.NewRevision;
                        }

                        error = "buffer-overflow;count=" + _buffer.Count + ";bytes=" + _bufferedBytes
                                + ";floor=" + floor;
                        _buffer.Clear();
                        _bufferedBytes = 0;
                        if (floor > _droppedRevisionFloor)
                            _droppedRevisionFloor = floor;
                        return false;
                    }

                    _buffer[dto.BaseRevision] = dto;
                    _bufferedBytes += dto.EncodedBytes;
                }

                return true;
            }
        }

        void NotifyRevision(SessionStamp stamp, long revision)
        {
            try { _onRevisionApplied?.Invoke(stamp, revision); }
            catch { /* isolate */ }
        }

        void CancelSyncObservers_NoLock(SyncAppliedResult result)
        {
            for (int i = 0; i < _syncObservers.Count; i++)
            {
                Action<SyncAppliedResult> obs = _syncObservers[i];
                PostMain(() =>
                {
                    try { obs?.Invoke(result); } catch { /* ignore */ }
                });
            }
            _syncObservers.Clear();
        }

        bool StampLive(SessionStamp stamp, long epoch, long generation)
        {
            return _captureStamp().Equals(stamp)
                   && _connectionGeneration() == generation
                   && stamp.Epoch == epoch && stamp.ConnectionGeneration == generation;
        }

        static bool TryDecodeBatch(GameplayDeltaEnvelope envelope, out DeltaBatchDto dto, out string error)
        {
            dto = null;
            error = null;
            byte[] payload = envelope.Payload;
            try
            {
                var msg = PlayerDeltaBatchNtf.Parser.ParseFrom(payload ?? Array.Empty<byte>());
                if (msg.NewRevision < 0 || msg.BaseRevision < 0)
                {
                    error = "negative-revision";
                    return false;
                }

                if (msg.NewRevision <= msg.BaseRevision)
                {
                    error = "non-advancing-revision";
                    return false;
                }

                string json = JsonFormatter.Default.Format(msg);
                dto = new DeltaBatchDto(
                    json,
                    msg.BaseRevision,
                    msg.NewRevision,
                    envelope.Epoch,
                    envelope.ConnectionGeneration,
                    envelope.EncodedBytes,
                    msg.ServerTime);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                return false;
            }
        }

        void PostMain(Action action)
        {
            if (action == null) return;
            if (_dispatcher.IsMainThread)
            {
                action();
                return;
            }

            _dispatcher.Post(action);
        }

        UniTask RunOnMainAsync(Action action)
        {
            if (_dispatcher.IsMainThread)
            {
                action();
                return UniTask.CompletedTask;
            }

            var utcs = new UniTaskCompletionSource();
            _dispatcher.Post(() =>
            {
                try
                {
                    action();
                    utcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    utcs.TrySetException(ex);
                }
            });
            return utcs.Task;
        }
    }
}
