using System;
using System.Globalization;
using System.IO;
using System.Threading;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using UnityEngine;
using XLua;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 每 AccountSession/环境一套玩法运行时：Store + Transport + Inbox + Client + Adapter + ModelPort。
    /// internal，不导出 Lua。由 Launcher 在 LuaEnv 就绪后挂到 AccountBridge。
    /// </summary>
    internal sealed class GameplayRuntime : IDisposable
    {
        readonly object _gate = new object();
        readonly IMainThreadDispatcher _dispatcher;
        readonly GameplaySessionContext _context;
        readonly PendingCommandStore _store;
        readonly AccountGameplayTransport _transport;
        readonly GameplayCommandClient _client;
        readonly AccountGameplayAdapter _adapter;
        readonly IGameplayModelPort _model;
        readonly GameplayDeltaInbox _inbox;
        readonly LuaEnv _luaEnv;
        readonly string _pendingRoot;
        readonly ClientGameplayClock _displayClock;
        readonly IMonotonicClock _monotonic;
        readonly GameplayTerminalGate _terminalGate = new GameplayTerminalGate();

        AccountSession _session;
        bool _disposed;
        bool _recoverInFlight;
        long _boundConnectionGeneration = -1;

        public GameplayRuntime(
            IMainThreadDispatcher dispatcher,
            GameplaySessionContext context,
            LuaEnv luaEnv,
            string pendingRootDirectory,
            AccountSession session,
            IMonotonicClock monotonicClock = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (luaEnv == null) throw new ArgumentNullException(nameof(luaEnv));
            _luaEnv = luaEnv;
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _displayClock = new ClientGameplayClock();
            _monotonic = monotonicClock ?? new StopwatchMonotonicClock();

            _pendingRoot = string.IsNullOrWhiteSpace(pendingRootDirectory)
                ? Path.Combine(Application.persistentDataPath, "m2-pending")
                : pendingRootDirectory;
            Directory.CreateDirectory(_pendingRoot);

            _model = new LuaModelStorePort(luaEnv, dispatcher);
            _store = new PendingCommandStore(_pendingRoot);
            _transport = new AccountGameplayTransport(
                session,
                () => _context.Capture().Epoch,
                () => session.ConnectionGeneration,
                () => session.AuthAttempt);
            _client = new GameplayCommandClient(
                _store,
                _transport,
                _transport,
                () => _context.Capture(),
                () => _context.IsReady,
                () => _model.GetRevision(_context.Capture()),
                utcNowMs: null,
                tryAllocateWrite: TryAllocateWrite,
                onAuthoritativeAck: OnAuthoritativeAck,
                requestFullSync: RequestFullSyncFromClient,
                onTerminalSession: OnTerminalSessionCode);
            _adapter = new AccountGameplayAdapter(_context, _client, _model, session.EnvironmentKey);

            // Inbox 独占 Batch 订阅（早于 BindPort，且 BindPort 不再订阅传输增量）
            _inbox = new GameplayDeltaInbox(
                _model,
                _dispatcher,
                _transport,
                () => _context.Capture(),
                () => (_session ?? session).ConnectionGeneration,
                () => (_session ?? session).AuthAttempt,
                RequestSyncStateAsync,
                (stamp, reason) => _context.FreezeWrites(stamp, reason),
                stamp => _context.ClearWriteFreeze(stamp),
                OnModelRevisionApplied,
                OnServerTimeAccepted);

            GameplayBridgeBinding.BindPort(_client, _model, subscribeTransportDelta: false);
            GameplayBridgeBinding.AttachRuntimeSurfaces(
                BuildStatusJson,
                ProbeWritable,
                _displayClock,
                _monotonic);
            _luaEnv.Global.Set("__m2_auth_model_managed", true);
        }

        public ClientGameplayClock DisplayClock => _displayClock;
        public IMonotonicClock MonotonicClock => _monotonic;

        public GameplayCommandClient Client => _client;
        public AccountGameplayAdapter Adapter => _adapter;
        public IGameplayModelPort ModelPort => _model;
        public GameplayDeltaInbox Inbox => _inbox;
        public string PendingRoot => _pendingRoot;

        /// <summary>
        /// 只读：持久 store 当前槽 pending（无则 null）。供验收诊断，不暴露写面。
        /// </summary>
        public PendingCommand PeekDurablePendingOrNull()
        {
            ThrowIfDisposed();
            AccountSession session = _session;
            if (session == null)
                return null;
            PendingLoadResult load = _store.TryLoad(session.EnvironmentKey, session.AccountKey);
            return load.Kind == PendingLoadKind.Ok ? load.Command : null;
        }

        public AuthenticationStamp BeginAuthentication(int authAttempt, long connectionGeneration)
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                _boundConnectionGeneration = connectionGeneration;
                _recoverInFlight = false;
            }
            _displayClock.Reset();
            SessionStamp prior = _context.Capture();
            AuthenticationStamp auth = _adapter.BeginAuthentication(authAttempt, connectionGeneration);
            try { _inbox.Reset(prior); } catch { /* 新认证清理旧收件箱 */ }
            return auth;
        }

        public bool CommitLogin(string snapshotJson)
        {
            ThrowIfDisposed();
            AccountSession session = _session ?? throw new InvalidOperationException("session-missing");

            long playerId = session.PendingPlayerId;
            long scopeId = session.PendingOperationScopeId;
            int attempt = session.AuthAttempt;
            if (playerId <= 0 || scopeId <= 0)
                return false;

            string playerKey = playerId.ToString(CultureInfo.InvariantCulture);
            string scopeKey = scopeId.ToString(CultureInfo.InvariantCulture);

            if (!_adapter.AttachAndInstallCandidate(playerKey, scopeKey, snapshotJson ?? session.LastSnapshotJson))
                return false;

            if (!session.CommitPendingToken())
            {
                _adapter.OnSessionEnded(SessionEndReason.AuthFailed);
                _model.Clear(_context.Capture(), "token-commit-failed");
                return false;
            }

            _adapter.OnAuthenticationCommitted(attempt);
            // 模型已接受后再校显示时钟；不用 AccountSession.ServerClock（DateTime UTC）
            if (session.LastAuthServerTimeMs > 0)
                OnServerTimeAccepted(session.LastAuthServerTimeMs, _context.Capture());
            StartObservedRecover(session);
            return true;
        }

        void StartObservedRecover(AccountSession session)
        {
            lock (_gate)
            {
                if (_recoverInFlight) return;
                _recoverInFlight = true;
            }

            string env = session.EnvironmentKey;
            string account = session.AccountKey;
            int attempt = session.AuthAttempt;
            SessionStamp recoveryStamp = _context.Capture();
            CancellationToken ct = CancellationToken.None;

            UniTask.Void(async () =>
            {
                try
                {
                    await _adapter.CompleteReadyAsync(account, attempt, previousScopeClosed: false, ct);
                }
                catch (Exception)
                {
                    if (ReferenceEquals(_session, session) && session.AuthAttempt == attempt
                        && _context.Matches(recoveryStamp))
                        OnSessionEnded(SessionEndReason.AuthFailed);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_session, session) && session.AuthAttempt == attempt
                            && _context.Matches(recoveryStamp))
                            _recoverInFlight = false;
                    }
                }
            });
        }

        public void OnSessionEnded(SessionEndReason reason)
        {
            if (_disposed) return;
            lock (_gate) { _recoverInFlight = false; }
            SessionStamp stamp = _context.Capture();
            try { _inbox.Reset(stamp); } catch { /* ignore */ }
            _adapter.OnSessionEnded(reason);
            try { _model.Clear(_context.Capture(), reason.ToString()); } catch { /* ignore */ }
            _displayClock.Reset();
            _terminalGate.Reset();
        }

        void OnServerTimeAccepted(long serverTimeMs, SessionStamp stamp)
        {
            if (_disposed || serverTimeMs <= 0)
                return;
            if (!_context.Matches(stamp))
                return;
            void Apply()
            {
                if (_disposed || !_context.Matches(stamp))
                    return;
                _displayClock.Synchronize(
                    GameplayInt64.Format(serverTimeMs),
                    _monotonic.ElapsedMilliseconds);
            }

            if (_dispatcher.IsMainThread)
                Apply();
            else
                _dispatcher.Post(Apply);
        }

        void OnTerminalSessionCode(int code, SessionStamp stamp)
        {
            if (_disposed || !_context.Matches(stamp))
                return;
            long live = _context.Epoch;
            if (!_terminalGate.TryClaim(stamp.Epoch, live, code))
                return;
            _context.FreezeWrites(stamp, "terminal-session");
            // 延后到主线程队列：保证 Facade 已 Post 的完成回调先跑完再 Invalidate
            int terminalCode = code;
            _dispatcher.Post(() =>
            {
                if (_disposed)
                    return;
                if (!_context.Matches(stamp))
                    return;
                try { AccountBridge.NotifySessionExpired(terminalCode); }
                catch { /* ignore */ }
            });
        }

        string BuildStatusJson()
        {
            bool ready = _context.IsReady;
            bool frozen = _context.IsWriteLocked || _inbox.IsWriteFrozen;
            string freeze = !string.IsNullOrEmpty(_context.WriteFreezeReason)
                ? _context.WriteFreezeReason
                : (_inbox.IsWriteFrozen ? _inbox.FreezeReason : "");
            bool busy = _client.IsBusy;
            string busyReason = _client.LockReason ?? "";
            string reason;
            if (!ready)
                reason = "not-ready";
            else if (!string.IsNullOrEmpty(busyReason)
                     && (busyReason.StartsWith("pending-locked", StringComparison.Ordinal)
                         || busyReason.StartsWith("payload-conflict", StringComparison.Ordinal)
                         || busyReason.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0))
                reason = busyReason;
            else if (busy)
                reason = string.IsNullOrEmpty(busyReason) ? "busy" : busyReason;
            else if (frozen)
                reason = string.IsNullOrEmpty(freeze) ? "write-frozen" : freeze;
            else
                reason = "ok";
            bool writable = ready && !frozen && !busy
                            && (string.IsNullOrEmpty(busyReason)
                                || (!busyReason.StartsWith("pending-locked", StringComparison.Ordinal)
                                    && !busyReason.StartsWith("payload-conflict", StringComparison.Ordinal)));
            return "{\"authReady\":" + (ready ? "true" : "false")
                   + ",\"writeFrozen\":" + (frozen ? "true" : "false")
                   + ",\"freezeReason\":\"" + EscapeJson(freeze) + "\""
                   + ",\"busy\":" + (busy ? "true" : "false")
                   + ",\"busyReason\":\"" + EscapeJson(busyReason) + "\""
                   + ",\"writable\":" + (writable ? "true" : "false")
                   + ",\"reason\":\"" + EscapeJson(reason) + "\""
                   + ",\"clockSynced\":" + (_displayClock.IsSynchronized ? "true" : "false")
                   + "}";
        }

        (bool writable, string reason) ProbeWritable()
        {
            bool ready = _context.IsReady;
            bool frozen = _context.IsWriteLocked || _inbox.IsWriteFrozen;
            bool busy = _client.IsBusy;
            string busyReason = _client.LockReason ?? "";
            string freeze = !string.IsNullOrEmpty(_context.WriteFreezeReason)
                ? _context.WriteFreezeReason
                : (_inbox.IsWriteFrozen ? _inbox.FreezeReason : "");
            string reason;
            if (!ready)
                reason = "not-ready";
            else if (!string.IsNullOrEmpty(busyReason)
                     && (busyReason.StartsWith("pending-locked", StringComparison.Ordinal)
                         || busyReason.StartsWith("payload-conflict", StringComparison.Ordinal)
                         || busyReason.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0))
                reason = busyReason;
            else if (busy)
                reason = string.IsNullOrEmpty(busyReason) ? "busy" : busyReason;
            else if (frozen)
                reason = string.IsNullOrEmpty(freeze) ? "write-frozen" : freeze;
            else
                reason = "ok";
            bool writable = ready && !frozen && !busy
                            && (string.IsNullOrEmpty(busyReason)
                                || (!busyReason.StartsWith("pending-locked", StringComparison.Ordinal)
                                    && !busyReason.StartsWith("payload-conflict", StringComparison.Ordinal)));
            return (writable, reason);
        }

        static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void RebindSession(AccountSession session)
        {
            ThrowIfDisposed();
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        bool TryAllocateWrite(WriteAuthority authority)
        {
            AccountSession session = _session;
            if (session == null || !session.IsAuthenticated)
                return false;
            if (!_context.IsReady || _context.IsWriteLocked)
                return false;
            if (_inbox.IsWriteFrozen)
                return false;
            if (!session.CommittedHasNextOpId || session.CommittedNextOpId <= 0)
                return false;
            if (session.CommittedOperationScopeId <= 0 || session.CommittedPlayerId <= 0)
                return false;

            SessionStamp stamp = _context.Capture();
            authority.OpId = session.CommittedNextOpId;
            authority.OperationScopeId = session.CommittedOperationScopeId;
            authority.ExpectedRevision = _model.GetRevision(stamp);
            authority.ConfigVersion = session.ConfigVersion > 0 ? session.ConfigVersion : 200;
            authority.EnvironmentKey = session.EnvironmentKey;
            authority.AccountKey = session.AccountKey;
            authority.PlayerId = session.CommittedPlayerId
                .ToString(CultureInfo.InvariantCulture);
            authority.AuthAttempt = session.AuthAttempt;
            authority.Epoch = stamp.Epoch;
            authority.ConnectionGeneration = session.ConnectionGeneration;
            return true;
        }

        void OnAuthoritativeAck(GameplayWireResponse resp)
        {
            if (resp == null || !resp.HasNextOp || resp.NextOpId <= 0)
                return;
            if (_session == null || resp.OperationScopeId != _session.CommittedOperationScopeId)
                return;
            _session?.AdvanceCommittedNextOp(resp.NextOpId);
        }

        void OnModelRevisionApplied(SessionStamp stamp, long revision)
        {
            _ = stamp;
            _adapter.ObserveModelRevision(revision);
        }

        void RequestFullSyncFromClient(SyncReason reason, Action<SyncAppliedResult> observer)
        {
            _inbox.RequestFullSync(reason, observer);
        }

        async UniTask<SyncStateResp> RequestSyncStateAsync(CancellationToken ct)
        {
            AccountSession session = _session;
            if (session == null)
                throw new InvalidOperationException("session-missing");
            SessionStamp requestStamp = _context.Capture();
            int attempt = session.AuthAttempt;
            SyncStateResp result = await _transport.SyncStateAsync(ct);
            // 自动Gap/Invalid恢复也必须通知账号拥有者；不能依赖窗口同步观察者。
            if (result != null && (result.Code == 106 || result.Code == 204)
                && ReferenceEquals(session, _session) && session.AuthAttempt == attempt
                && _context.Matches(requestStamp))
                OnTerminalSessionCode(result.Code, requestStamp);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _displayClock.Reset();
            _terminalGate.Reset();
            try { _inbox.Dispose(); } catch { /* ignore */ }
            try { _adapter.OnSessionEnded(SessionEndReason.Shutdown); } catch { /* ignore */ }
            try { _model.Clear(default, "dispose"); } catch { /* ignore */ }
            try { _luaEnv.Global.Set("__m2_auth_model_managed", false); } catch { /* VM正在退出 */ }
            try { GameplayBridgeBinding.UnbindPort(); } catch { /* ignore */ }
            _session = null;
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GameplayRuntime));
        }
    }
}
