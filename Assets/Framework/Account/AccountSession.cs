using System;
using System.IO;
using System.Threading;
using CloudIsle.Net;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;
using Game.Framework.Gameplay;
using Game.Framework.Lua;
using Game.Framework.Network;
using Google.Protobuf;
using UnityEngine;

namespace Game.Framework.Account
{
    /// <summary>
    /// 正式账号会话：TCP + NetClient + Heartbeat + Reconnect + Token + Kick。
    /// 业务错误码与传输错误分开；Token 仅在 CommitPendingToken 后落盘。
    /// </summary>
    public sealed class AccountSession : IDisposable
    {
        public const int CodeOk = 0;
        public const int CodeCancel = -1;
        public const int CodeTransport = -2;

        readonly ServerClock _clock = new ServerClock();
        readonly object _gate = new object();
        TcpTransport _transport;
        NetClient _client;
        HeartbeatService _heartbeat;
        ReconnectService _reconnect;
        TokenStore _tokens;
        CancellationTokenSource _receiveCts;
        CancellationTokenSource _heartbeatCts;
        CancellationTokenSource _reconnectCts;
        CancellationTokenSource _opsCts;
        DisconnectOverlay _overlay;
        string _pendingToken = "";
        long _pendingScopeId;
        long _committedScopeId;
        long _pendingNextOpId;
        bool _pendingHasNextOp;
        OperationScopeState _pendingScopeState;
        long _pendingPlayerId;
        long _pendingRevision;
        long _committedNextOpId;
        bool _committedHasNextOp;
        long _committedPlayerId;
        long _committedRevision;
        string _lastSnapshotJson = "{}";
        long _lastAuthServerTimeMs;
        int _lastBizCode;
        string _lastErrorKind = "";
        bool _authenticated;
        bool _stopReconnect;
        bool _disposed;
        bool _reconnectRunning;
        bool _pumpRunning;
        int _heartbeatEpoch;
        /// <summary>登录/授权代次：取消、新登录、Dispose 时递增，阻止旧响应写新状态。</summary>
        int _authAttempt;
        int _pendingAttempt;

        public string TcpHost { get; set; } = "127.0.0.1";
        public int TcpPort { get; set; } = 17780;
        public string AppVersion { get; set; } = Application.version;
        /// <summary>M2 生产桥契约；与 BridgeApi.BridgeContractValue 对齐。</summary>
        public string BridgeContract { get; set; } = BridgeApi.BridgeContractValue;
        public int ResVersion { get; set; } = 200;
        public int ConfigVersion { get; set; } = 200;
        public string DeviceId { get; set; } = "m1-editor";

        public string LastSnapshotJson
        {
            get { lock (_gate) return _lastSnapshotJson; }
        }

        /// <summary>
        /// 最近一次成功 Login/Reconnect 响应的 server_time（Unix ms）。
        /// 玩法显示时钟在模型接受后读取；与 <see cref="ServerClock"/>（墙钟偏移）分离。
        /// </summary>
        public long LastAuthServerTimeMs
        {
            get { lock (_gate) return _lastAuthServerTimeMs; }
        }

        public int LastBizCode
        {
            get { lock (_gate) return _lastBizCode; }
        }

        public string LastErrorKind
        {
            get { lock (_gate) return _lastErrorKind; }
        }

        public bool IsAuthenticated
        {
            get { lock (_gate) return _authenticated; }
        }

        /// <summary>当前 NetClient 未完成请求数（无连接为 0）。</summary>
        public int PendingRequestCount
        {
            get
            {
                lock (_gate)
                    return _client?.PendingCount ?? 0;
            }
        }

        /// <summary>当前授权代次（测试可读）。</summary>
        public int AuthAttempt
        {
            get { lock (_gate) return _authAttempt; }
        }

        /// <summary>仅当前 attempt 的 pending scope；Commit 前可读，失败/取消不污染新 attempt。非 Lua 面。</summary>
        internal long PendingOperationScopeId
        {
            get { lock (_gate) return _pendingAttempt == _authAttempt ? _pendingScopeId : 0; }
        }

        /// <summary>已 Commit 的持久 operation scope；未认证为 0。非 Lua 面。</summary>
        internal long CommittedOperationScopeId
        {
            get { lock (_gate) return _authenticated ? _committedScopeId : 0; }
        }

        /// <summary>当前 attempt 的 pending next_op；无权威则 Has=false。非 Lua 面。</summary>
        internal bool PendingHasNextOpId
        {
            get { lock (_gate) return _pendingAttempt == _authAttempt && _pendingHasNextOp; }
        }

        internal long PendingNextOpId
        {
            get { lock (_gate) return _pendingAttempt == _authAttempt && _pendingHasNextOp ? _pendingNextOpId : 0; }
        }

        internal OperationScopeState PendingScopeState
        {
            get
            {
                lock (_gate)
                    return _pendingAttempt == _authAttempt ? _pendingScopeState : OperationScopeState.Unknown;
            }
        }

        internal long PendingPlayerId
        {
            get { lock (_gate) return _pendingAttempt == _authAttempt ? _pendingPlayerId : 0; }
        }

        internal long PendingRevision
        {
            get { lock (_gate) return _pendingAttempt == _authAttempt ? _pendingRevision : 0; }
        }

        /// <summary>已 Commit 的权威 next_op；未设置则不可开写。非 Lua 面。</summary>
        internal bool CommittedHasNextOpId
        {
            get { lock (_gate) return _authenticated && _committedHasNextOp; }
        }

        internal long CommittedNextOpId
        {
            get { lock (_gate) return _authenticated && _committedHasNextOp ? _committedNextOpId : 0; }
        }

        internal long CommittedPlayerId
        {
            get { lock (_gate) return _authenticated ? _committedPlayerId : 0; }
        }

        internal long CommittedRevision
        {
            get { lock (_gate) return _authenticated ? _committedRevision : 0; }
        }

        /// <summary>环境键（host:port），玩法 pending 隔离用。</summary>
        internal string EnvironmentKey => (TcpHost ?? "127.0.0.1") + ":" + TcpPort;

        /// <summary>账号键：已认证用 playerId 十进制；否则 pending。</summary>
        internal string AccountKey
        {
            get
            {
                lock (_gate)
                {
                    long id = _authenticated ? _committedPlayerId : _pendingPlayerId;
                    return id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "pending";
                }
            }
        }

        /// <summary>当前 NetClient 连接代次；无连接为 0。</summary>
        internal long ConnectionGeneration
        {
            get
            {
                lock (_gate)
                    return _client != null ? _client.ConnectionGeneration : 0;
            }
        }

        /// <summary>Ack 权威推进 next（仅正数）。</summary>
        internal void AdvanceCommittedNextOp(long nextOpId)
        {
            if (nextOpId <= 0) return;
            lock (_gate)
            {
                if (!_authenticated) return;
                if (_committedHasNextOp && nextOpId < _committedNextOpId) return;
                _committedNextOpId = nextOpId;
                _committedHasNextOp = true;
            }
        }

        /// <summary>模型 revision 观测后同步（不降级）。</summary>
        internal void ObserveCommittedRevision(long revision)
        {
            if (revision < 0) return;
            lock (_gate)
            {
                if (!_authenticated) return;
                if (revision > _committedRevision)
                    _committedRevision = revision;
            }
        }

        public ServerClock Clock => _clock;

        public void Configure(string tokenDirectory)
        {
            ThrowIfDisposed();
            _tokens = new TokenStore(tokenDirectory);
            if (_opsCts == null)
                _opsCts = new CancellationTokenSource();
        }

        public void Pump(int maxMessages = 64)
        {
            if (_disposed || _client == null)
                return;
            _client.DispatchPending(Math.Max(1, maxMessages));
        }

        public void CancelAllOps()
        {
            CancellationTokenSource old;
            lock (_gate)
            {
                _authAttempt++;
                _pendingToken = "";
                _pendingScopeId = 0;
                _pendingAttempt = 0;
                ClearPendingWriteMeta_NoLock();
                // 取消不清除已 Commit 的 scope；仅放弃未提交 pending
                old = _opsCts;
                _opsCts = new CancellationTokenSource();
            }

            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }
        }

        public async UniTask ConnectAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            EnsureTokens();
            using (CancellationTokenSource linked = LinkOps(ct))
            {
                linked.Token.ThrowIfCancellationRequested();
                await DisconnectCoreAsync(keepAuthFlag: _authenticated);
                linked.Token.ThrowIfCancellationRequested();
                _transport = new TcpTransport();
                _client = new NetClient(_transport, OnDispatch);
                await _transport.ConnectAsync(new ServerEndpoint(TcpHost, TcpPort), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                _receiveCts = new CancellationTokenSource();
                _client.BeginReceive(_receiveCts.Token);
                _stopReconnect = false;
                StartPumpLoop();
            }
        }

        /// <summary>主线程泵循环：把后台收包交给 NetClient 完成 Pending / Kick。</summary>
        void StartPumpLoop()
        {
            if (_pumpRunning)
                return;
            _pumpRunning = true;
            PumpLoopAsync().Forget();
        }

        async UniTaskVoid PumpLoopAsync()
        {
            try
            {
                while (!_disposed && _client != null)
                {
                    try
                    {
                        _client.DispatchPending(64);
                    }
                    catch
                    {
                    }

                    await UniTask.Yield(PlayerLoopTiming.Update);
                }
            }
            finally
            {
                _pumpRunning = false;
            }
        }

        public async UniTask<(int code, string kind, string payload)> RegisterAsync(
            string username, string password, string nickname, CancellationToken ct)
        {
            ThrowIfDisposed();
            using (CancellationTokenSource linked = LinkOps(ct))
            {
                try
                {
                    EnsureClient();
                    var req = new RegisterReq
                    {
                        Username = username ?? "",
                        Password = password ?? "",
                        Nickname = nickname ?? "",
                        ProtoVersion = BridgeApi.RequiredProtoVersion,
                        DeviceId = DeviceId ?? ""
                    };
                    RegisterResp resp = await _client.SendRequestAsync<RegisterResp>(
                        req, NetClient.DefaultTimeout, linked.Token);
                    SetResult(resp.Code, resp.Code == CodeOk ? "ok" : "biz");
                    return (resp.Code, LastErrorKind, "");
                }
                catch (OperationCanceledException)
                {
                    SetResult(0, "cancel");
                    return (CodeCancel, "cancel", "cancelled");
                }
                catch (Exception ex)
                {
                    SetResult(0, "transport");
                    return (CodeTransport, "transport", SafeMsg(ex));
                }
            }
        }

        public async UniTask<(int code, string kind, string snapshotJson, int attempt)> LoginAsync(
            string username, string password, CancellationToken ct)
        {
            ThrowIfDisposed();
            int attempt;
            long connectionGeneration;
            string environmentKey;
            lock (_gate)
            {
                _authAttempt++;
                attempt = _authAttempt;
                _pendingToken = "";
                _pendingScopeId = 0;
                _pendingAttempt = 0;
                ClearPendingWriteMeta_NoLock();
                // 新登录开始即收回已发布认证/committed，避免旧 scope 对新 attempt 可见
                _authenticated = false;
                _committedScopeId = 0;
                _committedHasNextOp = false;
                _committedNextOpId = 0;
                _committedPlayerId = 0;
                _committedRevision = 0;
                connectionGeneration = _client != null ? _client.ConnectionGeneration : 0;
                environmentKey = (TcpHost ?? "127.0.0.1") + ":" + TcpPort;
            }

            // 玩法 BeginAuth 必须在任何 await Login RPC 之前
            AccountBridge.NotifyGameplayAuthStarting(this, attempt, connectionGeneration, environmentKey);

            using (CancellationTokenSource linked = LinkOps(ct))
            {
                try
                {
                    EnsureClient();
                    var req = new LoginReq
                    {
                        Username = username ?? "",
                        Password = password ?? "",
                        AppVersion = string.IsNullOrEmpty(AppVersion) ? Application.version : AppVersion,
                        ResVersion = ResVersion,
                        ProtoVersion = BridgeApi.RequiredProtoVersion,
                        ConfigVersion = ConfigVersion,
                        DeviceId = DeviceId ?? "",
                        BridgeContract = string.IsNullOrEmpty(BridgeContract)
                            ? BridgeApi.BridgeContractValue
                            : BridgeContract
                    };
                    LoginResp resp = await _client.SendRequestAsync<LoginResp>(
                        req, NetClient.DefaultTimeout, linked.Token);

                    // RPC 已完成后：关闭/取消/新登录均不得再写或清新状态。
                    if (!IsCurrentAttempt(attempt) || linked.IsCancellationRequested || _disposed)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(0, "cancel");
                        return (CodeCancel, "cancel", "{}", attempt);
                    }

                    if (resp.Code != CodeOk)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(resp.Code, "biz");
                        return (resp.Code, "biz", "{}", attempt);
                    }

                    if (resp.Data == null)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(107, "biz");
                        return (107, "biz", "{}", attempt);
                    }

                    // M2 认证必须携带 scope>0；否则不进入可 Commit 状态
                    if (resp.OperationScopeId <= 0 || string.IsNullOrEmpty(resp.Token))
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(107, "biz");
                        return (107, "biz", "{}", attempt);
                    }

                    // 缺权威 next 不得开写；仍可 Commit 认证（空号），但玩法 Ready 不开写
                    string json = SnapshotCodec.ToLuaJson(resp.Data);
                    long playerId = resp.Data.Info != null ? resp.Data.Info.PlayerId : 0;
                    long revision = resp.Data.Revision;
                    lock (_gate)
                    {
                        if (_authAttempt != attempt || _disposed)
                        {
                            _lastBizCode = 0;
                            _lastErrorKind = "cancel";
                            return (CodeCancel, "cancel", "{}", attempt);
                        }

                        _pendingToken = resp.Token ?? "";
                        _pendingScopeId = resp.OperationScopeId;
                        _pendingAttempt = attempt;
                        _pendingHasNextOp = resp.HasNextOpId && resp.NextOpId > 0;
                        _pendingNextOpId = _pendingHasNextOp ? resp.NextOpId : 0;
                        _pendingScopeState = resp.ScopeState;
                        _pendingPlayerId = playerId;
                        _pendingRevision = revision >= 0 ? revision : 0;
                        _lastSnapshotJson = json;
                        _lastAuthServerTimeMs = resp.ServerTime;
                        _lastBizCode = 0;
                        _lastErrorKind = "ok";
                    }

                    // 写入 pending 后再查一次取消：响应已到、应用前取消不得留下可 Commit 的认证。
                    if (linked.IsCancellationRequested || !IsCurrentAttempt(attempt) || _disposed)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(0, "cancel");
                        return (CodeCancel, "cancel", "{}", attempt);
                    }

                    _clock.Synchronize(resp.ServerTime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    return (0, "ok", json, attempt);
                }
                catch (OperationCanceledException)
                {
                    ClearPendingTokenIfAttempt(attempt);
                    SetResult(0, "cancel");
                    return (CodeCancel, "cancel", "{}", attempt);
                }
                catch (Exception ex)
                {
                    ClearPendingTokenIfAttempt(attempt);
                    SetResult(0, "transport");
                    return (CodeTransport, "transport", SafeMsg(ex), attempt);
                }
            }
        }

        /// <summary>
        /// Lua applySnapshot 成功后：落盘 Token、标已认证、启心跳、解除断线遮罩。
        /// Save 失败或不匹配代次时不标已认证、不隐藏遮罩。
        /// </summary>
        public bool CommitPendingToken()
        {
            ThrowIfDisposed();
            EnsureTokens();
            bool ok;
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_pendingToken)
                    || _pendingAttempt != _authAttempt
                    || _pendingScopeId <= 0)
                    return false;

                string token = _pendingToken;
                int attempt = _pendingAttempt;
                long scopeId = _pendingScopeId;
                bool hasNext = _pendingHasNextOp;
                long nextOp = _pendingNextOpId;
                long playerId = _pendingPlayerId;
                long revision = _pendingRevision;
                // Save 与代次校验同锁：避免 Save 后、发布前被 Cancel/新 attempt 插队覆写文件却仍标认证
                if (!_tokens.Save(token))
                {
                    ok = false;
                }
                else if (_authAttempt != attempt || _pendingAttempt != attempt)
                {
                    return false;
                }
                else
                {
                    _committedScopeId = scopeId;
                    _committedHasNextOp = hasNext;
                    _committedNextOpId = nextOp;
                    _committedPlayerId = playerId;
                    _committedRevision = revision;
                    _pendingToken = "";
                    _pendingScopeId = 0;
                    _pendingAttempt = 0;
                    ClearPendingWriteMeta_NoLock();
                    _authenticated = true;
                    _stopReconnect = false;
                    ok = true;
                }
            }

            if (!ok)
            {
                SetResult(0, "token-save-failed");
                return false;
            }

            // StartHeartbeat / Hide 不得持锁
            StartHeartbeat();
            HideOverlay();
            return true;
        }

        public void ClearPendingToken()
        {
            lock (_gate)
            {
                _pendingToken = "";
                _pendingScopeId = 0;
                _pendingAttempt = 0;
                ClearPendingWriteMeta_NoLock();
            }
        }

        void ClearPendingWriteMeta_NoLock()
        {
            _pendingHasNextOp = false;
            _pendingNextOpId = 0;
            _pendingScopeState = OperationScopeState.Unknown;
            _pendingPlayerId = 0;
            _pendingRevision = 0;
        }

        /// <summary>仅清除仍属指定登录代次的 pending（旧回调/旧租约安全）。</summary>
        public void ClearPendingAuthIfAttempt(int attempt) => ClearPendingTokenIfAttempt(attempt);

        public bool HasSavedToken()
        {
            EnsureTokens();
            return _tokens.HasToken();
        }

        public void ClearToken()
        {
            EnsureTokens();
            ClearPendingToken();
            _tokens.Clear();
            lock (_gate)
            {
                _authenticated = false;
                _committedScopeId = 0;
                _committedHasNextOp = false;
                _committedNextOpId = 0;
                _committedPlayerId = 0;
                _committedRevision = 0;
                _lastSnapshotJson = "{}";
            }
        }

        /// <summary>用已存 Token 发 ReconnectReq。需要已连接。</summary>
        public async UniTask<(int code, string kind, string snapshotJson)> RestoreAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            EnsureTokens();
            string token = _tokens.Read();
            if (string.IsNullOrEmpty(token))
            {
                SetResult(204, "biz");
                return (204, "biz", "{}");
            }

            if (_client == null)
                await ConnectAsync(ct);

            return await ReconnectWithTokenAsync(token, ct);
        }

        public async UniTask LogoutAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            StopReconnectLoop();
            StopHeartbeat();
            HideOverlay();
            try
            {
                if (_transport != null && _authenticated)
                    await SendLogoutFireAndForgetAsync(ct);
            }
            finally
            {
                ClearToken();
                await DisconnectCoreAsync(keepAuthFlag: false);
            }
        }

        public void StopReconnectLoop()
        {
            _stopReconnect = true;
            try { _reconnectCts?.Cancel(); } catch { }
            _reconnectRunning = false;
        }

        public void ShowOverlay(string message)
        {
            _overlay = DisconnectOverlay.Ensure();
            _overlay.Show(message);
        }

        public void HideOverlay() => _overlay?.Hide();

        /// <summary>断线遮罩当前是否可见（验收/测试）。</summary>
        public bool IsOverlayVisible => _overlay != null && _overlay.IsVisible;

        /// <summary>自动重连循环是否在跑（单一循环）。</summary>
        public bool IsReconnectRunning => _reconnectRunning;

        /// <summary>当前/最近重连尝试序号（来自 ReconnectService）。</summary>
        public int ReconnectAttempt => _reconnect != null ? _reconnect.LastAttempt : 0;

        /// <summary>测试/注入：标记已认证并准备在断线时自动恢复。</summary>
        public void MarkAuthenticatedForTests()
        {
            lock (_gate)
                _authenticated = true;
            _stopReconnect = false;
        }

        /// <summary>
        /// 受控验收：切断本会话真实套接字，保留 Token/认证意图。
        /// 不调用 StopHeartbeat（会抬 epoch）；仅取消当前心跳 CTS，
        /// 由 <see cref="RunHeartbeatAsync"/> 在同代次收尾后自动 <see cref="BeginAutoReconnect"/>。
        /// 禁止手调遮罩/手发 Notify/手动 Restore 冒充此路径。
        /// </summary>
        public void DropLiveSocketForTests()
        {
            ThrowIfDisposed();
            try { _receiveCts?.Cancel(); } catch { }
            try { _client?.Disconnect("test-drop"); } catch { }
            try { _transport?.Close(); } catch { }
            // 同 epoch 取消：RunHeartbeatAsync 的 cancel 分支触发自动重连（非 StopHeartbeat）。
            try { _heartbeatCts?.Cancel(); } catch { }
        }

        /// <summary>主动触发断线恢复循环（socket 已断或即将断）。</summary>
        public void BeginAutoReconnect()
        {
            if (_disposed || _stopReconnect)
                return;
            bool authed;
            lock (_gate)
                authed = _authenticated;
            if (!authed || _reconnectRunning)
                return;

            _reconnectRunning = true;
            ShowOverlay("网络断开，正在重连…");
            StopHeartbeat();
            if (_transport == null)
                _transport = new TcpTransport();
            if (_client == null)
                _client = new NetClient(_transport, OnDispatch);
            _reconnect = new ReconnectService(_client, _transport);
            _reconnectCts = new CancellationTokenSource();
            CancellationToken ct = _reconnectCts.Token;
            RunReconnectAsync(ct).Forget();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelAllOps();
            StopReconnectLoop();
            StopHeartbeat();
            HideOverlay();
            _overlay?.Dispose();
            _overlay = null;
            try { _receiveCts?.Cancel(); } catch { }
            try { _client?.Disconnect("dispose"); } catch { }
            try { _transport?.Close(); } catch { }
            _client = null;
            _transport = null;
            ClearPendingToken();
        }

        Action<byte[]> _gameplayDeltaHandler;

        /// <summary>玩法受限发送：复用本会话唯一 NetClient，不另开连接。</summary>
        public async UniTask<IMessage> SendGameplayRequestAsync(
            IMessage request,
            MessageParser responseParser,
            TimeSpan timeout,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (responseParser == null) throw new ArgumentNullException(nameof(responseParser));
            NetClient client;
            lock (_gate) client = _client;
            if (client == null)
                throw new InvalidOperationException("not-connected");
            using (CancellationTokenSource linked = LinkOps(ct))
            {
                IMessage raw = await client
                    .SendRequestMessageAsync(request, timeout, linked.Token);
                // 已由 NetClient 按 MsgRegistry 解析为具体类型；再经 parser 防御拷贝
                return responseParser.ParseFrom(raw.ToByteArray());
            }
        }

        /// <summary>订阅 PlayerDeltaBatchNtf 原始字节；Kick 仍由本类独占。</summary>
        public IDisposable SubscribeGameplayDelta(Action<byte[]> handler)
        {
            _gameplayDeltaHandler = handler;
            return new DeltaUnsub(this);
        }

        void OnDispatch(Packet packet)
        {
            if (packet.MsgId == MsgRegistry.GetId(typeof(PlayerDeltaBatchNtf)))
            {
                try { _gameplayDeltaHandler?.Invoke(packet.Body ?? Array.Empty<byte>()); }
                catch { /* 隔离玩法回调 */ }
                return;
            }

            if (packet.MsgId != MsgRegistry.GetId(typeof(KickNtf)))
                return;
            KickNtf ntf;
            try
            {
                ntf = KickNtf.Parser.ParseFrom(packet.Body ?? Array.Empty<byte>());
            }
            catch
            {
                ntf = new KickNtf();
            }

            HandleKick(ntf);
        }

        sealed class DeltaUnsub : IDisposable
        {
            AccountSession _owner;
            public DeltaUnsub(AccountSession owner) { _owner = owner; }
            public void Dispose()
            {
                AccountSession o = Interlocked.Exchange(ref _owner, null);
                if (o != null)
                    o._gameplayDeltaHandler = null;
            }
        }

        void HandleKick(KickNtf ntf)
        {
            StopReconnectLoop();
            StopHeartbeat();
            ClearToken();
            HideOverlay();
            try { _client?.Disconnect("kick"); } catch { }
            AccountBridge.NotifyGameplaySessionEnded(this, SessionEndReason.Kick);
            AccountBridge.NotifyKick((int)(ntf?.Reason ?? KickReason.KickElsewhere));
        }

        /// <summary>
        /// 用已存 Token 发 ReconnectReq。成功时写入与登录相同的 pending 认证事务
        /// （同 Token + 代次），不立即标认证/启心跳/隐藏遮罩；须 Lua 应用快照后 Commit。
        /// 204/106：停重试、清 Token、解除遮罩，由上层回 Login。
        /// </summary>
        async UniTask<(int code, string kind, string snapshotJson)> ReconnectWithTokenAsync(
            string token, CancellationToken ct)
        {
            int attempt;
            long connectionGeneration;
            string environmentKey;
            lock (_gate)
            {
                _authAttempt++;
                attempt = _authAttempt;
                _pendingToken = "";
                _pendingScopeId = 0;
                _pendingAttempt = 0;
                ClearPendingWriteMeta_NoLock();
                // Token 重连新 attempt：收回已发布认证，旧 committed 对新 attempt 不可见
                _authenticated = false;
                _committedScopeId = 0;
                _committedHasNextOp = false;
                _committedNextOpId = 0;
                _committedPlayerId = 0;
                _committedRevision = 0;
                connectionGeneration = _client != null ? _client.ConnectionGeneration : 0;
                environmentKey = (TcpHost ?? "127.0.0.1") + ":" + TcpPort;
            }

            AccountBridge.NotifyGameplayAuthStarting(this, attempt, connectionGeneration, environmentKey);

            using (CancellationTokenSource linked = LinkOps(ct))
            {
                try
                {
                    EnsureClient();
                    var req = new ReconnectReq
                    {
                        Token = token,
                        ProtoVersion = BridgeApi.RequiredProtoVersion,
                        AppVersion = string.IsNullOrEmpty(AppVersion) ? Application.version : AppVersion,
                        ResVersion = ResVersion,
                        ConfigVersion = ConfigVersion,
                        BridgeContract = string.IsNullOrEmpty(BridgeContract)
                            ? BridgeApi.BridgeContractValue
                            : BridgeContract
                    };
                    ReconnectResp resp = await _client.SendRequestAsync<ReconnectResp>(
                        req, NetClient.DefaultTimeout, linked.Token);

                    if (!IsCurrentAttempt(attempt) || linked.IsCancellationRequested || _disposed)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(0, "cancel");
                        return (CodeCancel, "cancel", "{}");
                    }

                    if (resp.Code == 204 || resp.Code == 106)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        // Return terminal status to the owning loop. Cancelling its CTS here
                        // aborts RecoverAsync before it can deliver the Lua expiry/update event.
                        ClearToken();
                        HideOverlay();
                        SetResult(resp.Code, "biz");
                        return (resp.Code, "biz", "{}");
                    }

                    if (resp.Code != CodeOk || resp.Data == null)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(resp.Code == CodeOk ? 107 : resp.Code, "biz");
                        return (resp.Code == CodeOk ? 107 : resp.Code, "biz", "{}");
                    }

                    if (resp.OperationScopeId <= 0)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(107, "biz");
                        return (107, "biz", "{}");
                    }

                    string json = SnapshotCodec.ToLuaJson(resp.Data);
                    long playerId = resp.Data.Info != null ? resp.Data.Info.PlayerId : 0;
                    long revision = resp.Data.Revision;
                    lock (_gate)
                    {
                        if (_authAttempt != attempt || _disposed)
                        {
                            _lastBizCode = 0;
                            _lastErrorKind = "cancel";
                            return (CodeCancel, "cancel", "{}");
                        }

                        // 与 Login 相同：pending=已存同 Token + 本 attempt 的 scope；认证/心跳/遮罩等 Commit。
                        _pendingToken = token ?? "";
                        _pendingScopeId = resp.OperationScopeId;
                        _pendingAttempt = attempt;
                        _pendingHasNextOp = resp.HasNextOpId && resp.NextOpId > 0;
                        _pendingNextOpId = _pendingHasNextOp ? resp.NextOpId : 0;
                        _pendingScopeState = resp.ScopeState;
                        _pendingPlayerId = playerId;
                        _pendingRevision = revision >= 0 ? revision : 0;
                        _lastSnapshotJson = json;
                        _lastAuthServerTimeMs = resp.ServerTime;
                        _lastBizCode = 0;
                        _lastErrorKind = "ok";
                    }

                    if (linked.IsCancellationRequested || !IsCurrentAttempt(attempt) || _disposed)
                    {
                        ClearPendingTokenIfAttempt(attempt);
                        SetResult(0, "cancel");
                        return (CodeCancel, "cancel", "{}");
                    }

                    _clock.Synchronize(resp.ServerTime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    return (0, "ok", json);
                }
                catch (OperationCanceledException)
                {
                    ClearPendingTokenIfAttempt(attempt);
                    SetResult(0, "cancel");
                    return (CodeCancel, "cancel", "{}");
                }
                catch (Exception ex)
                {
                    ClearPendingTokenIfAttempt(attempt);
                    SetResult(0, "transport");
                    return (CodeTransport, "transport", SafeMsg(ex));
                }
            }
        }

        async UniTaskVoid RunReconnectAsync(CancellationToken ct)
        {
            try
            {
                EnsureTokens();
                string token = _tokens.Read();
                if (string.IsNullOrEmpty(token))
                {
                    ClearToken();
                    HideOverlay();
                    AccountBridge.NotifySessionExpired(204);
                    return;
                }

                int terminalCode = 204;
                bool ok = await _reconnect.RecoverAsync(
                    new ServerEndpoint(TcpHost, TcpPort),
                    async restoreCt =>
                    {
                        var result = await ReconnectWithTokenAsync(token, restoreCt);
                        if (result.code == 204 || result.code == 106)
                        {
                            // 保留真实终止码：106=版本更新停重试，不得重标 204。
                            terminalCode = result.code;
                            return false;
                        }

                        if (result.code == 0)
                        {
                            AccountBridge.NotifyRestored(result.snapshotJson);
                            return true;
                        }

                        // 传输错误继续退避；勿伪造认证成功。
                        throw new IOException(result.snapshotJson ?? "restore-transport");
                    },
                    ct);

                if (!ok)
                {
                    ClearToken();
                    HideOverlay();
                    AccountBridge.NotifySessionExpired(terminalCode);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                if (!_stopReconnect)
                {
                    ClearToken();
                    HideOverlay();
                    int code = LastBizCode > 0 ? LastBizCode : 204;
                    AccountBridge.NotifySessionExpired(code);
                }
            }
            finally
            {
                _reconnectRunning = false;
            }
        }

        void StartHeartbeat()
        {
            StopHeartbeat();
            if (_client == null)
                return;
            _heartbeat = new HeartbeatService(_client, _clock);
            _heartbeatCts = new CancellationTokenSource();
            int epoch = Interlocked.Increment(ref _heartbeatEpoch);
            CancellationToken ct = _heartbeatCts.Token;
            RunHeartbeatAsync(epoch, ct).Forget();
        }

        async UniTaskVoid RunHeartbeatAsync(int epoch, CancellationToken ct)
        {
            try
            {
                await _heartbeat.RunAsync(
                    HeartbeatService.DefaultInterval,
                    HeartbeatService.DefaultSilenceTimeout,
                    ct);
                // 静默断线等正常收尾：仍同代次且未主动停重连 → 自动恢复。
                if (epoch == Volatile.Read(ref _heartbeatEpoch) && !_stopReconnect)
                    BeginAutoReconnect();
            }
            catch (OperationCanceledException)
            {
                // StopHeartbeat 会先抬 epoch；同代次取消（如 DropLiveSocket）视为断线。
                if (epoch == Volatile.Read(ref _heartbeatEpoch) && !_stopReconnect)
                    BeginAutoReconnect();
            }
            catch (Exception)
            {
                if (epoch == Volatile.Read(ref _heartbeatEpoch) && !_stopReconnect)
                    BeginAutoReconnect();
            }
        }

        void StopHeartbeat()
        {
            Interlocked.Increment(ref _heartbeatEpoch);
            try { _heartbeatCts?.Cancel(); } catch { }
            try { _heartbeatCts?.Dispose(); } catch { }
            _heartbeatCts = null;
            _heartbeat = null;
        }

        async UniTask SendLogoutFireAndForgetAsync(CancellationToken ct)
        {
            if (_transport == null)
                return;
            try
            {
                byte[] body = new LogoutReq().ToByteArray();
                ushort msgId = MsgRegistry.GetId(typeof(LogoutReq));
                byte[] frame = FrameCodec.Encode(msgId, 0, body);
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(500);
                    await _transport.SendAsync(frame, cts.Token);
                }
            }
            catch
            {
            }
        }

        async UniTask DisconnectCoreAsync(bool keepAuthFlag)
        {
            StopReconnectLoop();
            StopHeartbeat();
            try { _receiveCts?.Cancel(); } catch { }
            try { _receiveCts?.Dispose(); } catch { }
            _receiveCts = null;
            try { _client?.Disconnect("disconnect"); } catch { }
            try { _transport?.Close(); } catch { }
            _client = null;
            _transport = null;
            if (!keepAuthFlag)
            {
                lock (_gate)
                    _authenticated = false;
            }

            await UniTask.Yield();
        }

        void EnsureClient()
        {
            if (_client == null)
                throw new InvalidOperationException("not-connected");
        }

        void EnsureTokens()
        {
            if (_tokens == null)
                _tokens = new TokenStore(Path.Combine(Application.persistentDataPath, "m1-account"));
        }

        CancellationTokenSource LinkOps(CancellationToken ct)
        {
            CancellationTokenSource ops;
            lock (_gate)
            {
                if (_opsCts == null)
                    _opsCts = new CancellationTokenSource();
                ops = _opsCts;
            }

            return CancellationTokenSource.CreateLinkedTokenSource(ops.Token, ct);
        }

        void SetResult(int code, string kind)
        {
            lock (_gate)
            {
                _lastBizCode = code;
                _lastErrorKind = kind ?? "";
            }
        }

        bool IsCurrentAttempt(int attempt)
        {
            lock (_gate)
                return !_disposed && _authAttempt == attempt;
        }

        void ClearPendingTokenIfAttempt(int attempt)
        {
            lock (_gate)
            {
                if (_authAttempt != attempt)
                    return;
                if (_pendingAttempt != 0 && _pendingAttempt != attempt)
                    return;
                _pendingToken = "";
                _pendingScopeId = 0;
                _pendingAttempt = 0;
                ClearPendingWriteMeta_NoLock();
            }
        }

        static string SafeMsg(Exception ex)
        {
            string msg = ex.GetBaseException().Message ?? "";
            // 不把可能含 token/password 的细节写入公开回调；仅短原因。
            if (msg.Length > 120)
                msg = msg.Substring(0, 120);
            return msg;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AccountSession));
        }
    }
}
