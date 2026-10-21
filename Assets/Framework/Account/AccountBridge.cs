using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Lua;
using Game.Framework.UI;
using UnityEngine;

namespace Game.Framework.Account
{
    /// <summary>
    /// Lua 可见的账号/网络固定桥。属 m1-bridge-0.3；不暴露 Protobuf 消息类型。
    /// 完成码：0=ok，&gt;0=业务码，-1=取消，-2=传输。
    /// </summary>
    public static class AccountBridge
    {
        static AccountSession _session;
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

        /// <summary>Lua applySnapshot 成功后调用，才写入 Token 并启心跳。失败可观察（不抛）。</summary>
        public static bool CommitLoginToken()
        {
            EnsureSession();
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
