using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Gameplay;
using Game.Framework.Lua;
using Game.Framework.UI;
using UnityEngine;
using XLua;

namespace Game.Framework.Account
{
    /// <summary>
    /// Lua 可见的账号/网络固定桥。属 m2-bridge-0.4（账号面兼容 m1）；不暴露 Protobuf 消息类型。
    /// 完成码：0=ok，&gt;0=业务码，-1=取消，-2=传输。
    /// </summary>
    public static class AccountBridge
    {
        static AccountSession _session;
        static GameplayRuntime _gameplay;
        static LuaCallback _kickHandler;
        static LuaCallback _restoredHandler;
        static LuaCallback _expiredHandler;
        static int _mainThreadId;
        static SynchronizationContext _mainCtx;
        static readonly object ActiveGate = new object();
        static readonly HashSet<CallbackLease> ActiveLeases = new HashSet<CallbackLease>();

        /// <summary>当前会话（测试可读）。</summary>
        public static AccountSession Session => _session;

        /// <summary>未完成桥操作租约数（测试）。</summary>
        public static int ActiveLeaseCount
        {
            get { lock (ActiveGate) return ActiveLeases.Count; }
        }

        /// <summary>
        /// 配置端点与 Token 目录。tokenDirectory 空则用 persistentDataPath/m1-account。
        /// </summary>
        public static void Configure(
            string tcpHost,
            int tcpPort,
            string appVersion,
            int resVersion,
            int configVersion,
            string deviceId,
            string tokenDirectory)
        {
            EnsureMain();
            // 先结束旧桥操作，再更换 Session，旧成功回调不能应用到新会话。
            CancelAll();
            DetachGameplayRuntime();
            DisposeSession();
            _session = new AccountSession
            {
                TcpHost = string.IsNullOrEmpty(tcpHost) ? "127.0.0.1" : tcpHost,
                TcpPort = tcpPort > 0 ? tcpPort : 17780,
                AppVersion = string.IsNullOrEmpty(appVersion) ? Application.version : appVersion,
                ResVersion = resVersion > 0 ? resVersion : 100,
                ConfigVersion = configVersion > 0 ? configVersion : 100,
                DeviceId = deviceId ?? "m1-editor"
            };
            string dir = string.IsNullOrWhiteSpace(tokenDirectory)
                ? Path.Combine(Application.persistentDataPath, "m1-account")
                : tokenDirectory;
            _session.Configure(dir);
        }

        /// <summary>
        /// Launcher：LuaEnv 就绪且 ConfigureHost 已完成后挂载玩法运行时。
        /// 替换时先 Dispose 旧 runtime（回调/订阅），再绑新 Session。
        /// </summary>
        internal static void AttachGameplayRuntime(
            IMainThreadDispatcher dispatcher,
            GameplaySessionContext context,
            LuaEnv luaEnv,
            string pendingRootDirectory)
        {
            EnsureMain();
            EnsureSession();
            DetachGameplayRuntime();
            _gameplay = new GameplayRuntime(
                dispatcher, context, luaEnv, pendingRootDirectory, _session);
        }

        internal static void DetachGameplayRuntime()
        {
            var g = _gameplay;
            _gameplay = null;
            try { g?.Dispose(); } catch { /* ignore */ }
        }

        /// <summary>Login/Restore 递增 attempt 后、RPC await 前。</summary>
        internal static void NotifyGameplayAuthStarting(
            AccountSession source, int authAttempt, long connectionGeneration, string environmentKey)
        {
            if (!ReferenceEquals(source, _session)) return;
            _ = environmentKey;
            _gameplay?.BeginAuthentication(authAttempt, connectionGeneration);
        }

        /// <summary>测试/诊断：是否已挂玩法运行时（非 Lua 导出）。</summary>
        internal static bool HasGameplayRuntime => _gameplay != null;

        internal static void NotifyGameplaySessionEnded(AccountSession source, SessionEndReason reason)
        {
            if (!ReferenceEquals(source, _session)) return;
            _gameplay?.OnSessionEnded(reason);
        }

        /// <summary>连接 TCP。done(code, kind)。</summary>
        public static CallbackLease Connect(LuaCallback done)
        {
            EnsureSession();
            var session = _session;
            var lease = new CallbackLease(done);
            Run(async ct =>
            {
                try
                {
                    await session.ConnectAsync(ct);
                    Complete(lease, 0, "ok");
                }
                catch (OperationCanceledException)
                {
                    Complete(lease, -1, "cancel");
                }
                catch (Exception ex)
                {
                    Complete(lease, -2, Safe(ex));
                }
            }, lease);
            return lease;
        }

        /// <summary>注册。done(code, kind)。</summary>
        public static CallbackLease Register(string username, string password, string nickname, LuaCallback done)
        {
            EnsureSession();
            var session = _session;
            var lease = new CallbackLease(done);
            Run(async ct =>
            {
                var r = await session.RegisterAsync(username, password, nickname, ct);
                Complete(lease, r.code, r.kind);
            }, lease);
            return lease;
        }

        /// <summary>登录。成功时 payload=快照 JSON（尚未落 Token）。</summary>
        public static CallbackLease Login(string username, string password, LuaCallback done)
        {
            EnsureSession();
            // 捕获拥有者，Configure 更换会话后旧操作只清理旧会话。
            var session = _session;
            // 直接保有 Lua 委托，让租约正确 Retain/Release DelegateBridge。
            var lease = new CallbackLease(done);
            Run(async ct =>
            {
                var r = await session.LoginAsync(username, password, ct);
                // 响应已写入 pending 后，Cancel 须按本代次放弃（应用回调前）。
                lease.BindCancelSideEffect(() => session.ClearPendingAuthIfAttempt(r.attempt));
                // Yield：给 Cancel/Dispose 窗口（响应已到、Lua 应用前）。
                await UniTask.Yield(PlayerLoopTiming.Update);
                if (ct.IsCancellationRequested || lease.IsCompleted)
                {
                    session.ClearPendingAuthIfAttempt(r.attempt);
                    if (!lease.IsCompleted)
                        Complete(lease, -1, "cancel");
                    return;
                }

                Complete(lease, r.code, r.code == 0 ? r.snapshotJson : r.kind);
            }, lease);
            return lease;
        }

        /// <summary>
        /// 登录/恢复快照就绪后调用：有玩法运行时时经 ModelPort 安装 + Token + 观察 Recover；
        /// 无运行时仅 Token（M1 合同，不 MarkReady 玩法）。
        /// </summary>
        public static bool CommitLoginToken()
        {
            EnsureSession();
            if (_gameplay != null)
                return _gameplay.CommitLogin(_session.LastSnapshotJson);
            return _session.CommitPendingToken();
        }

        /// <summary>
        /// 丢弃当前会话的 pending Token（仅快照应用失败等本尝试路径）。
        /// 旧失败回调不得调用此方法清掉新尝试；请用 DiscardPendingTokenIfAttempt。
        /// </summary>
        public static void DiscardPendingToken()
        {
            _session?.ClearPendingToken();
        }

        /// <summary>仅当 pending 仍属指定登录代次时丢弃（旧回调安全）。</summary>
        public static void DiscardPendingTokenIfAttempt(int attempt)
        {
            _session?.ClearPendingAuthIfAttempt(attempt);
        }

        /// <summary>用已存 Token 恢复。payload=快照 JSON。</summary>
        public static CallbackLease Restore(LuaCallback done)
        {
            EnsureSession();
            var session = _session;
            var lease = new CallbackLease(done);
            Run(async ct =>
            {
                var r = await session.RestoreAsync(ct);
                Complete(lease, r.code, r.code == 0 ? r.snapshotJson : r.kind);
            }, lease);
            return lease;
        }

        /// <summary>登出：不期待响应；清 Token/连接。</summary>
        public static CallbackLease Logout(LuaCallback done)
        {
            EnsureSession();
            var session = _session;
            var lease = new CallbackLease(done);
            Run(async ct =>
            {
                try
                {
                    NotifyGameplaySessionEnded(session, SessionEndReason.Logout);
                    await session.LogoutAsync(ct);
                    Complete(lease, 0, "ok");
                }
                catch (OperationCanceledException)
                {
                    Complete(lease, -1, "cancel");
                }
                catch (Exception ex)
                {
                    Complete(lease, -2, Safe(ex));
                }
            }, lease);
            return lease;
        }

        /// <summary>取消全部拥有操作（会话 ops + 各租约 CTS）并收尾。</summary>
        public static void CancelAll()
        {
            _session?.CancelAllOps();
            CallbackLease[] copy;
            lock (ActiveGate)
            {
                copy = new CallbackLease[ActiveLeases.Count];
                ActiveLeases.CopyTo(copy);
            }

            for (int i = 0; i < copy.Length; i++)
            {
                try
                {
                    if (!copy[i].IsCompleted)
                        copy[i].Cancel("cancel-all");
                }
                catch
                {
                }
            }
        }

        public static void Pump(int maxMessages) => _session?.Pump(maxMessages);

        public static bool HasToken() => _session != null && _session.HasSavedToken();

        public static void ClearToken() => _session?.ClearToken();

        public static string LastSnapshotJson() => _session?.LastSnapshotJson ?? "{}";

        public static int LastBizCode() => _session?.LastBizCode ?? 0;

        public static string LastErrorKind() => _session?.LastErrorKind ?? "";

        public static bool IsAuthenticated() => _session != null && _session.IsAuthenticated;

        public static int PendingRequestCount() => _session?.PendingRequestCount ?? 0;

        /// <summary>
        /// T028 验收只读：GameplayCommandClient.ActiveSnapshot / 持久 store 槽位数（0|1）。
        /// 禁止用账号传输 PendingRequestCount 冒充玩法 pending。
        /// </summary>
        public static int GameplayPendingCount()
        {
            if (_gameplay == null)
                return 0;
            if (_gameplay.Client.ActiveSnapshot != null)
                return 1;
            return _gameplay.PeekDurablePendingOrNull() != null ? 1 : 0;
        }

        /// <summary>
        /// 写入 accept-state 玩法 pending 只读元数据（key/状态/scope/op/消息 kind/请求字节 SHA）。
        /// 不输出 token/password；不提供写 Model / 篡改 pending 的入口。
        /// 触及固定运行时 C# → 旧 Native 失效，须 GenAll + 新 IL2CPP 包。
        /// </summary>
        public static void AppendGameplayPendingAcceptanceFields(StringBuilder sb)
        {
            if (sb == null)
                throw new ArgumentNullException(nameof(sb));

            PendingCommand active = _gameplay?.Client?.ActiveSnapshot;
            PendingCommand durable = null;
            try { durable = _gameplay?.PeekDurablePendingOrNull(); }
            catch { /* 持久锁/损坏：仍报 count，细节留空 */ }

            PendingCommand src = active ?? durable;
            int count = src != null ? 1 : 0;
            sb.Append("pendingCount=").Append(count).Append('\n');
            sb.Append("pendingSource=").Append(
                active != null ? "active" : (durable != null ? "durable" : "")).Append('\n');

            // 最小只读恢复诊断：busy/lockReason/recoveryPhase/异常类型；禁止 token/password/account/body。
            bool busy = false;
            string lockReason = "";
            string recoveryPhase = "";
            string recoveryError = "";
            try
            {
                if (_gameplay?.Client != null)
                {
                    busy = _gameplay.Client.IsBusy;
                    lockReason = _gameplay.Client.LockReason ?? "";
                    recoveryPhase = _gameplay.Client.RecoveryPhase ?? "";
                    recoveryError = _gameplay.Client.RecoveryError ?? "";
                }
            }
            catch { /* 只读失败留空 */ }

            sb.Append("pendingBusy=").Append(busy ? "true" : "false").Append('\n');
            sb.Append("pendingLockReason=").Append(SanitizeAccept(lockReason)).Append('\n');
            sb.Append("pendingRecoveryPhase=").Append(SanitizeAccept(recoveryPhase)).Append('\n');
            sb.Append("pendingRecoveryError=").Append(SanitizeAccept(recoveryError)).Append('\n');

            // 写锁/可写：供故障恢复后核对 pending 解除与写锁释放；不输出 token/password。
            bool writable = false;
            string writeReason = "";
            try { writable = LuaGameplayFacade.IsWritable(out writeReason); }
            catch
            {
                writable = false;
                writeReason = "probe-failed";
            }

            sb.Append("gameplayWritable=").Append(writable ? "true" : "false").Append('\n');
            sb.Append("gameplayWriteReason=").Append(SanitizeAccept(writeReason ?? "")).Append('\n');

            if (src == null)
            {
                sb.Append("pendingState=\n");
                sb.Append("pendingCommandKey=\n");
                sb.Append("pendingScope=\n");
                sb.Append("pendingOp=\n");
                sb.Append("pendingMsgKind=\n");
                sb.Append("pendingRequestSha256=\n");
                sb.Append("pendingPayloadHash=\n");
                return;
            }

            sb.Append("pendingState=").Append(src.State.ToString()).Append('\n');
            string kind = src.Intent != null ? (src.Intent.CommandKey ?? "") : "";
            sb.Append("pendingCommandKey=").Append(SanitizeAccept(kind)).Append('\n');
            sb.Append("pendingScope=").Append(src.OperationScopeId.ToString()).Append('\n');
            sb.Append("pendingOp=").Append(src.OpId.ToString()).Append('\n');
            sb.Append("pendingMsgKind=").Append(SanitizeAccept(kind)).Append('\n');
            sb.Append("pendingRequestSha256=").Append(Sha256Hex(src.PeekWireBytes())).Append('\n');
            sb.Append("pendingPayloadHash=").Append(SanitizeAccept(src.PayloadHash ?? "")).Append('\n');
        }

        static string SanitizeAccept(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n' || c == '\0')
                    continue;
                sb.Append(c);
            }

            return sb.ToString();
        }

        static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        public static void ShowDisconnectMask(string message) => _session?.ShowOverlay(message);

        public static void HideDisconnectMask() => _session?.HideOverlay();

        public static void StopReconnect() => _session?.StopReconnectLoop();

        /// <summary>自动重连是否进行中（验收 state）。</summary>
        public static bool IsReconnectRunning() => _session != null && _session.IsReconnectRunning;

        /// <summary>当前重连尝试序号；未重连为 0。</summary>
        public static int ReconnectAttempt => _session != null ? _session.ReconnectAttempt : 0;

        /// <summary>验收：切断真实 socket，由心跳收尾自动重连（非手调遮罩）。</summary>
        public static void DropLiveSocketForAcceptance() => _session?.DropLiveSocketForTests();

        /// <summary>断线遮罩是否可见（验收可观察）。</summary>
        public static bool IsDisconnectMaskVisible() => _session != null && _session.IsOverlayVisible;

        /// <summary>Kick 回调：done(reason, \"kick\")。替换时释放旧桥。</summary>
        public static void SetKickHandler(LuaCallback handler) =>
            ReplaceHandler(ref _kickHandler, handler);

        /// <summary>自动恢复成功：done(0, snapshotJson)。</summary>
        public static void SetRestoredHandler(LuaCallback handler) =>
            ReplaceHandler(ref _restoredHandler, handler);

        /// <summary>Token 失效等不可恢复：done(code, \"expired\")。</summary>
        public static void SetExpiredHandler(LuaCallback handler) =>
            ReplaceHandler(ref _expiredHandler, handler);

        /// <summary>关闭会话与桥引用；释放事件回调（Dispose 异常上抛）。</summary>
        public static void Dispose()
        {
            CancelAll();
            DetachGameplayRuntime();
            DisposeSession();
            ReleaseHandler(ref _kickHandler, swallow: false);
            ReleaseHandler(ref _restoredHandler, swallow: false);
            ReleaseHandler(ref _expiredHandler, swallow: false);
        }

        internal static void NotifyKick(int reason)
        {
            Post(() =>
            {
                var h = _kickHandler;
                h?.Invoke(reason, "kick");
            });
        }

        internal static void NotifyRestored(string snapshotJson)
        {
            Post(() =>
            {
                var h = _restoredHandler;
                h?.Invoke(0, snapshotJson ?? "{}");
            });
        }

        /// <summary>
        /// 不可恢复终止：保留真实业务码。204=会话失效，106=需更新（停止重试）。
        /// 不得把 106 重标为 204。
        /// </summary>
        internal static void NotifySessionExpired(int code = 204)
        {
            int biz = code > 0 ? code : 204;
            string kind = biz == 106 ? "update-required" : "expired";
            NotifyGameplaySessionEnded(_session,
                biz == 106 ? SessionEndReason.VersionRejected : SessionEndReason.AuthFailed);
            Post(() =>
            {
                var h = _expiredHandler;
                h?.Invoke(biz, kind);
            });
        }

        static void Run(Func<CancellationToken, UniTask> work, CallbackLease lease)
        {
            var cts = new CancellationTokenSource();
            lease.BindOperationCts(cts);
            lock (ActiveGate)
                ActiveLeases.Add(lease);

            UniTask.Void(async () =>
            {
                try
                {
                    await work(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!lease.IsCompleted)
                        lease.Cancel("cancelled");
                }
                catch (Exception ex)
                {
                    if (!lease.IsCompleted)
                        lease.Complete(-2, Safe(ex));
                }
                finally
                {
                    lock (ActiveGate)
                        ActiveLeases.Remove(lease);
                }
            });
        }

        static void Complete(CallbackLease lease, int code, string payload)
        {
            Post(() =>
            {
                if (!lease.IsCompleted)
                    lease.Complete(code, payload ?? "");
            });
        }

        static void Post(Action action)
        {
            if (action == null)
                return;
            if (_mainCtx != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                _mainCtx.Post(_ => action(), null);
            else
                action();
        }

        static void EnsureMain()
        {
            if (_mainThreadId != 0)
                return;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _mainCtx = SynchronizationContext.Current;
        }

        static void EnsureSession()
        {
            EnsureMain();
            if (_session == null)
                Configure("127.0.0.1", 17780, Application.version, 100, 100, "m1-editor", null);
        }

        static void DisposeSession()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
        }

        static void ReplaceHandler(ref LuaCallback field, LuaCallback next)
        {
            var old = field;
            if (next != null)
                CallbackLease.RetainLuaCallback(next);
            field = next;
            if (old != null)
                CallbackLease.ReleaseLuaCallback(old, swallowDisposeErrors: false);
        }

        static void ReleaseHandler(ref LuaCallback field, bool swallow)
        {
            var old = field;
            field = null;
            if (old != null)
                CallbackLease.ReleaseLuaCallback(old, swallowDisposeErrors: swallow);
        }

        static string Safe(Exception ex)
        {
            string m = ex.GetBaseException().Message ?? "error";
            return m.Length > 120 ? m.Substring(0, 120) : m;
        }
    }
}
