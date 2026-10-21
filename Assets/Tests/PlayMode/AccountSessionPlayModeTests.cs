using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// 真实 TCP 账号桥：外部启动 Tools/run_server.ps1（正式 Host），不引用 Server 程序集。
    /// 不把密码/Token 写入断言消息。
    /// </summary>
    public sealed class AccountSessionPlayModeTests
    {
        string _root;
        int _tcpPort;
        int _httpPort;
        Process _server;
        AccountSession _session;
        string _stopFile;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FarmingC2Account", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _tcpPort = FreePort();
            _httpPort = FreePort();
            _stopFile = Path.Combine(_root, "stop.signal");
            if (File.Exists(_stopFile))
                File.Delete(_stopFile);

            string content = FindContentRoot();
            string script = Path.GetFullPath("Tools/run_server.ps1");
            Assert.That(File.Exists(script), Is.True, "缺 Tools/run_server.ps1");

            string args =
                "-NoProfile -File \"" + script + "\""
                + " -RepoRoot \"" + Path.GetFullPath(".") + "\""
                + " -DatabasePath \"" + Path.Combine(_root, "c2.db") + "\""
                + " -TcpPort " + _tcpPort
                + " -HttpPort " + _httpPort
                + " -ContentRoot \"" + content + "\""
                + " -LogDirectory \"" + Path.Combine(_root, "logs") + "\""
                + " -StopFile \"" + _stopFile + "\""
                + " -BuildArtifactsDirectory \"" + Path.GetFullPath("Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts") + "\""
                + " -SkipBuild";

            // 若尚无构建产物则允许首次 build（去掉 SkipBuild）
            string artifacts = Path.GetFullPath("Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts");
            if (!Directory.Exists(artifacts))
            {
                args = args.Replace(" -SkipBuild", "");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = args,
                WorkingDirectory = Path.GetFullPath("."),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _server = Process.Start(psi);
            Assert.That(_server, Is.Not.Null);

            float until = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < until)
            {
                if (_server.HasExited)
                    Assert.Fail("server exited early code=" + _server.ExitCode);
                if (PortOpen(_tcpPort) && PortOpen(_httpPort))
                    break;
                yield return null;
            }

            Assert.That(PortOpen(_tcpPort), Is.True, "TCP not listening");

            _session = new AccountSession
            {
                TcpHost = "127.0.0.1",
                TcpPort = _tcpPort,
                AppVersion = Application.version,
                ResVersion = 100,
                ConfigVersion = 100,
                DeviceId = "c2-playmode"
            };
            _session.Configure(Path.Combine(_root, "tokens"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            try
            {
                if (!string.IsNullOrEmpty(_stopFile))
                    File.WriteAllText(_stopFile, "stop");
            }
            catch
            {
            }

            float until = Time.realtimeSinceStartup + 30f;
            while (_server != null && !_server.HasExited && Time.realtimeSinceStartup < until)
                yield return null;
            try
            {
                if (_server != null && !_server.HasExited)
                    _server.Kill();
            }
            catch
            {
            }

            try { _server?.Dispose(); } catch { }
            _server = null;
            try
            {
                if (!string.IsNullOrEmpty(_root) && Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [UnityTest]
        public IEnumerator RegisterLogin_CommitToken_FailLoginKeepsNoToken_Logout()
        {
            string user = "u" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();

            var reg = default((int code, string kind, string payload));
            yield return _session.RegisterAsync(user, pass, "NickA", CancellationToken.None)
                .ToCoroutine(r => reg = r);
            Assert.That(reg.code, Is.EqualTo(0), "register kind=" + reg.kind + " payload=" + Sanitize(reg.payload));
            Assert.That(_session.HasSavedToken(), Is.False, "注册不写 Token");

            var login = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None)
                .ToCoroutine(r => login = r);
            Assert.That(login.code, Is.EqualTo(0), "login");
            Assert.That(_session.HasSavedToken(), Is.False, "未 Commit 前不落盘");
            Assert.That(login.snapshotJson, Does.Contain("playerId"));

            _session.CommitPendingToken();
            Assert.That(_session.HasSavedToken(), Is.True);
            Assert.That(_session.IsAuthenticated, Is.True);

            _session.ClearToken();
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            var bad = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, "wrong-" + pass, CancellationToken.None)
                .ToCoroutine(r => bad = r);
            Assert.That(bad.code, Is.EqualTo(202));
            Assert.That(_session.HasSavedToken(), Is.False);

            yield return _session.LoginAsync(user, pass, CancellationToken.None)
                .ToCoroutine(r => login = r);
            Assert.That(login.code, Is.EqualTo(0));
            _session.CommitPendingToken();
            yield return _session.LogoutAsync(CancellationToken.None).ToCoroutine();
            Assert.That(_session.HasSavedToken(), Is.False);
            Assert.That(_session.IsAuthenticated, Is.False);
        }

        [UnityTest]
        public IEnumerator CancelLogin_DoesNotLeaveCommittedToken()
        {
            string user = "c" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickC", CancellationToken.None).ToCoroutine();

            var cts = new CancellationTokenSource();
            var task = _session.LoginAsync(user, pass, cts.Token);
            cts.Cancel();
            var result = default((int code, string kind, string snapshotJson, int attempt));
            yield return task.ToCoroutine(r => result = r);
            _session.ClearPendingToken();
            Assert.That(_session.HasSavedToken(), Is.False);
        }

        [UnityTest]
        public IEnumerator LoginAttempt_StaleSuccess_DoesNotOverwriteNewerAttempt()
        {
            string user = "g" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickG", CancellationToken.None).ToCoroutine();

            var ctsOld = new CancellationTokenSource();
            var oldTask = _session.LoginAsync(user, pass, ctsOld.Token);
            // 等请求真正发出后再取消并启动新登录，覆盖「RPC 完成后应用前」窗口。
            float untilPend = Time.realtimeSinceStartup + 3f;
            while (_session.PendingRequestCount == 0 && Time.realtimeSinceStartup < untilPend)
                yield return null;
            Assert.That(_session.PendingRequestCount, Is.GreaterThan(0));
            ctsOld.Cancel();
            _session.CancelAllOps();
            // 取消可能打断传输；新尝试前重新连接，验证代次不污染新状态。
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();

            var fresh = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None).ToCoroutine(r => fresh = r);
            Assert.That(fresh.code, Is.EqualTo(0), "fresh login kind=" + fresh.kind);
            Assert.That(_session.CommitPendingToken(), Is.True);
            Assert.That(_session.HasSavedToken(), Is.True);

            var old = default((int code, string kind, string snapshotJson, int attempt));
            yield return oldTask.ToCoroutine(r => old = r);
            // 旧尝试不得清掉新 Token / 认证
            Assert.That(_session.HasSavedToken(), Is.True);
            Assert.That(_session.IsAuthenticated, Is.True);
            Assert.That(_session.PendingRequestCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Login_ResponseArrived_CancelBeforeApply_DoesNotLeavePendingAuth()
        {
            string user = "x" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickX", CancellationToken.None).ToCoroutine();

            // 真实响应已到且 pending 已写入、尚未 Commit：等价「应用前取消」。
            var arrived = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None)
                .ToCoroutine(r => arrived = r);
            Assert.That(arrived.code, Is.EqualTo(0), "response arrived");
            Assert.That(arrived.snapshotJson, Does.Contain("playerId"));
            Assert.That(_session.HasSavedToken(), Is.False, "not committed yet");

            // 桥租约 Cancel 副作用同路径：按代次放弃 pending。
            _session.ClearPendingAuthIfAttempt(arrived.attempt);
            Assert.That(_session.CommitPendingToken(), Is.False, "no pending after abandon");
            Assert.That(_session.IsAuthenticated, Is.False);

            // 桥路径：Login + Yield 窗口内 Cancel，BindCancelSideEffect 须清 pending。
            AccountBridge.Configure(
                "127.0.0.1", _tcpPort, Application.version, 100, 100, "c2-preapply",
                Path.Combine(_root, "preapply-tokens"));
            int connectCode = 99;
            CallbackLease connectLease = AccountBridge.Connect((code, _) => connectCode = code);
            float untilConn = Time.realtimeSinceStartup + 10f;
            while (!connectLease.IsCompleted && Time.realtimeSinceStartup < untilConn)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(connectCode, Is.EqualTo(0));
            int loginCode = 99;
            CallbackLease loginLease = AccountBridge.Login(user, pass, (code, _) => loginCode = code);
            float untilPend = Time.realtimeSinceStartup + 3f;
            while (AccountBridge.PendingRequestCount() == 0 && Time.realtimeSinceStartup < untilPend)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(AccountBridge.PendingRequestCount(), Is.GreaterThan(0));
            float untilResp = Time.realtimeSinceStartup + 8f;
            bool responseObservedBeforeApplication = false;
            while (!loginLease.IsCompleted && Time.realtimeSinceStartup < untilResp)
            {
                AccountBridge.Pump(64);
                if (AccountBridge.PendingRequestCount() == 0
                    && AccountBridge.LastSnapshotJson().Contains("playerId")
                    && !loginLease.IsCompleted)
                {
                    responseObservedBeforeApplication = true;
                    loginLease.Cancel("pre-apply-cancel");
                    break;
                }

                yield return null;
            }

            if (!loginLease.IsCompleted)
                loginLease.Cancel("pre-apply-cancel-fallback");

            float untilDone = Time.realtimeSinceStartup + 3f;
            while (!loginLease.IsCompleted && Time.realtimeSinceStartup < untilDone)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(responseObservedBeforeApplication, Is.True,
                "Must observe a successful response and cancel the real lease before delivery");
            Assert.That(loginCode, Is.EqualTo(-1), "Real lease cancellation must win before apply");
            Assert.That(AccountBridge.HasToken(), Is.False);
            Assert.That(AccountBridge.IsAuthenticated(), Is.False);
            Assert.That(AccountBridge.Session.CommitPendingToken(), Is.False, "cancel cleared pending");

            AccountBridge.Dispose();
        }

        [UnityTest]
        public IEnumerator Configure_ReplacesSession_CancelsOldCallback_NewPendingSurvives()
        {
            string user = "r" + Guid.NewGuid().ToString("N").Substring(0, 10);
            const string password = "root-scope-test-password";
            AccountBridge.Configure("127.0.0.1", _tcpPort, Application.version,
                100, 100, "scope-old", Path.Combine(_root, "scope-old"));
            yield return AccountBridge.Session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return AccountBridge.Session.RegisterAsync(user, password, "ScopeNew", CancellationToken.None).ToCoroutine();
            int oldCode = 99;
            int oldCalls = 0;
            CallbackLease oldLease = AccountBridge.Login(user, password, (code, _) =>
            {
                oldCode = code;
                oldCalls++;
            });
            // Reconfigure while the real old operation is still outstanding.
            Assert.That(oldLease.IsCompleted, Is.False);
            AccountBridge.Configure("127.0.0.1", _tcpPort, Application.version,
                100, 100, "scope-new", Path.Combine(_root, "scope-new"));
            Assert.That(oldCode, Is.EqualTo(-1), "Old callback must cancel before replacement");
            var freshSession = AccountBridge.Session;
            yield return freshSession.ConnectAsync(CancellationToken.None).ToCoroutine();
            var fresh = default((int code, string kind, string snapshotJson, int attempt));
            yield return freshSession.LoginAsync(user, password, CancellationToken.None).ToCoroutine(r => fresh = r);
            Assert.That(fresh.code, Is.EqualTo(0));
            oldLease.Cancel("late-old-cancel");
            oldLease.Dispose();
            for (int i = 0; i < 3; i++)
            {
                AccountBridge.Pump(64);
                yield return null;
            }
            Assert.That(oldCalls, Is.EqualTo(1));
            Assert.That(freshSession.CommitPendingToken(), Is.True, "Old cleanup cannot erase new pending");
            Assert.That(freshSession.IsAuthenticated, Is.True);
            Assert.That(AccountBridge.ActiveLeaseCount, Is.EqualTo(0));
            AccountBridge.Dispose();
        }

        [UnityTest]
        public IEnumerator StaleNonZeroCallback_DoesNotClearNewerPendingToken()
        {
            string user = "s" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickS", CancellationToken.None).ToCoroutine();

            var fresh = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None).ToCoroutine(r => fresh = r);
            Assert.That(fresh.code, Is.EqualTo(0));
            Assert.That(fresh.attempt, Is.GreaterThan(0));
            // 模拟旧失败回调误调无代次 Discard：不得清新 pending。
            int staleAttempt = fresh.attempt - 1;
            _session.ClearPendingAuthIfAttempt(staleAttempt);
            Assert.That(_session.CommitPendingToken(), Is.True, "new pending survives stale discard");
            Assert.That(_session.HasSavedToken(), Is.True);
        }

        [UnityTest]
        public IEnumerator BridgeCancelAll_AbortsPendingConnectOrLogin()
        {
            string user = "b" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            AccountBridge.Configure(
                "127.0.0.1", _tcpPort, Application.version, 100, 100, "c2-bridge",
                Path.Combine(_root, "bridge-tokens"));

            int connectCode = 99;
            CallbackLease connectLease = AccountBridge.Connect((code, _) => connectCode = code);
            float untilConn = Time.realtimeSinceStartup + 10f;
            while (!connectLease.IsCompleted && Time.realtimeSinceStartup < untilConn)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(connectCode, Is.EqualTo(0), "connect");
            var reg = default((int code, string kind, string payload));
            // 用底层 session 注册，避免桥回调嵌套
            yield return AccountBridge.Session.RegisterAsync(user, pass, "NickB", CancellationToken.None)
                .ToCoroutine(r => reg = r);
            Assert.That(reg.code, Is.EqualTo(0));

            int loginCode = 99;
            CallbackLease loginLease = AccountBridge.Login(user, pass, (code, _) => loginCode = code);
            float untilPend = Time.realtimeSinceStartup + 3f;
            while (AccountBridge.PendingRequestCount() == 0 && Time.realtimeSinceStartup < untilPend)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(AccountBridge.PendingRequestCount(), Is.GreaterThan(0), "login pending");
            Assert.That(AccountBridge.ActiveLeaseCount, Is.GreaterThan(0));
            AccountBridge.CancelAll();

            float untilDone = Time.realtimeSinceStartup + 5f;
            while (!loginLease.IsCompleted && Time.realtimeSinceStartup < untilDone)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(loginLease.IsCompleted, Is.True);
            Assert.That(loginCode, Is.EqualTo(-1));
            float untilIdle = Time.realtimeSinceStartup + 3f;
            while ((AccountBridge.PendingRequestCount() > 0 || AccountBridge.ActiveLeaseCount > 0)
                   && Time.realtimeSinceStartup < untilIdle)
            {
                AccountBridge.Pump(64);
                yield return null;
            }

            Assert.That(AccountBridge.PendingRequestCount(), Is.EqualTo(0));
            Assert.That(AccountBridge.ActiveLeaseCount, Is.EqualTo(0));
            Assert.That(AccountBridge.HasToken(), Is.False);
            AccountBridge.Dispose();
        }

        [UnityTest]
        public IEnumerator SecondLogin_KicksFirst_ClearsToken_StopsReconnect()
        {
            string user = "k" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            int kickReason = -1;
            AccountBridge.SetKickHandler((code, _) => kickReason = code);

            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickK", CancellationToken.None).ToCoroutine();
            var login = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None).ToCoroutine(r => login = r);
            Assert.That(login.code, Is.EqualTo(0));
            _session.CommitPendingToken();
            Assert.That(_session.HasSavedToken(), Is.True);

            var other = new AccountSession
            {
                TcpHost = "127.0.0.1",
                TcpPort = _tcpPort,
                AppVersion = Application.version,
                ResVersion = 100,
                ConfigVersion = 100,
                DeviceId = "c2-other"
            };
            other.Configure(Path.Combine(_root, "tokens-other"));
            try
            {
                yield return other.ConnectAsync(CancellationToken.None).ToCoroutine();
                yield return other.LoginAsync(user, pass, CancellationToken.None).ToCoroutine(r => login = r);
                Assert.That(login.code, Is.EqualTo(0), "second login");
                other.CommitPendingToken();

                float until = Time.realtimeSinceStartup + 8f;
                while (kickReason < 0 && Time.realtimeSinceStartup < until)
                {
                    _session.Pump(64);
                    yield return null;
                }

                Assert.That(kickReason, Is.GreaterThanOrEqualTo(0), "first session got Kick");
                Assert.That(_session.HasSavedToken(), Is.False, "kick clears token");
            }
            finally
            {
                try { other.Dispose(); } catch { }
                AccountBridge.SetKickHandler(null);
            }
        }

        [UnityTest]
        public IEnumerator Restore_AfterControlledDisconnect_ReappliesSnapshot()
        {
            string user = "r" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickR", CancellationToken.None).ToCoroutine();
            var login = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None).ToCoroutine(r => login = r);
            Assert.That(login.code, Is.EqualTo(0));
            Assert.That(_session.CommitPendingToken(), Is.True);
            Assert.That(_session.HasSavedToken(), Is.True);
            Assert.That(_session.IsAuthenticated, Is.True);

            // 受控断开：再次 ConnectAsync 会拆旧套接字但保留 Token 文件，再 Restore。
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            var restored = default((int code, string kind, string snapshotJson));
            yield return _session.RestoreAsync(CancellationToken.None).ToCoroutine(r => restored = r);
            Assert.That(restored.code, Is.EqualTo(0), "restore kind=" + restored.kind);
            Assert.That(restored.snapshotJson, Does.Contain("playerId"));
            // 与登录相同：成功仅写 pending，须 Commit 后才认证（模拟 Lua apply_and_commit）。
            Assert.That(_session.IsAuthenticated, Is.False, "pending before commit");
            Assert.That(_session.CommitPendingToken(), Is.True, "commit after restore");
            Assert.That(_session.IsAuthenticated, Is.True);
            Assert.That(_session.HasSavedToken(), Is.True);
        }

        /// <summary>
        /// T57：真实切断本会话 socket → BeginAutoReconnect → NotifyRestored 后须经 Commit 才认证/解遮罩。
        /// 不手调 Mask、不手发 Notify、不用手动 Restore 冒充自动恢复。
        /// </summary>
        [UnityTest]
        public IEnumerator AutoReconnect_AfterDropLiveSocket_RequiresCommitBeforeAuth()
        {
            string user = "a" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = "p" + Guid.NewGuid().ToString("N").Substring(0, 10);
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "NickA", CancellationToken.None).ToCoroutine();
            var login = default((int code, string kind, string snapshotJson, int attempt));
            yield return _session.LoginAsync(user, pass, CancellationToken.None).ToCoroutine(r => login = r);
            Assert.That(login.code, Is.EqualTo(0));
            Assert.That(_session.CommitPendingToken(), Is.True);
            Assert.That(_session.IsAuthenticated, Is.True);

            string restoredJson = null;
            int restoredCode = -99;
            bool sawUnauthedPending = false;
            // NotifyRestored 走静态 handler；Commit 必须打在本 Session（与 Login pending 事务同一实例）。
            AccountBridge.SetRestoredHandler((code, payload) =>
            {
                restoredCode = code;
                restoredJson = payload;
                if (!_session.IsAuthenticated)
                    sawUnauthedPending = true;
                Assert.That(_session.CommitPendingToken(), Is.True, "auto restore commit");
            });

            // 仅切断 socket；由心跳同代次收尾自动 BeginAutoReconnect（禁止手动 Begin）。
            _session.DropLiveSocketForTests();

            float until = Time.realtimeSinceStartup + 30f;
            bool sawMask = false;
            while (Time.realtimeSinceStartup < until && restoredCode < 0)
            {
                if (_session.IsOverlayVisible)
                    sawMask = true;
                _session.Pump(64);
                yield return null;
            }

            Assert.That(sawMask, Is.True, "mask visible during auto reconnect");

            Assert.That(restoredCode, Is.EqualTo(0), "auto restored code");
            Assert.That(restoredJson, Does.Contain("playerId"));
            Assert.That(sawUnauthedPending, Is.True, "pending before Lua/commit auth");
            Assert.That(_session.IsAuthenticated, Is.True, "authenticated after commit");
            Assert.That(_session.HasSavedToken(), Is.True);
            Assert.That(_session.IsOverlayVisible, Is.False, "mask hidden only after commit");
            AccountBridge.SetRestoredHandler(null);
        }

        /// <summary>Real ReconnectResp=204 must reach the terminal callback, not cancel its owner.</summary>
        [UnityTest]
        public IEnumerator AutoReconnect_InvalidStoredToken_Notifies204ExactlyOnce()
        {
            string user = "e" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string pass = Guid.NewGuid().ToString("N");
            yield return _session.ConnectAsync(CancellationToken.None).ToCoroutine();
            yield return _session.RegisterAsync(user, pass, "ExpiredTest", CancellationToken.None).ToCoroutine();
            yield return _session.LoginAsync(user, pass, CancellationToken.None).ToCoroutine();
            Assert.That(_session.CommitPendingToken(), Is.True);
            // Valid token syntax, absent from the isolated real server DB: a real 204 response.
            Assert.That(new TokenStore(Path.Combine(_root, "tokens")).Save(new string('a', 64)), Is.True);
            int terminalCode = 0;
            int calls = 0;
            AccountBridge.SetExpiredHandler((code, _) => { terminalCode = code; calls++; });
            try
            {
                _session.DropLiveSocketForTests();
                float until = Time.realtimeSinceStartup + 35f;
                while (terminalCode == 0 && Time.realtimeSinceStartup < until)
                {
                    _session.Pump(64);
                    yield return null;
                }
                Assert.That(terminalCode, Is.EqualTo(204));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(_session.HasSavedToken(), Is.False);
                Assert.That(_session.IsAuthenticated, Is.False);
                Assert.That(_session.IsReconnectRunning, Is.False);
                Assert.That(_session.IsOverlayVisible, Is.False);
            }
            finally { AccountBridge.SetExpiredHandler(null); }
        }

        static string FindContentRoot()
        {
            string[] candidates =
            {
                Path.GetFullPath("Server/PatchContent/M1"),
                Path.GetFullPath("Docs/Acceptance/M1/20261020-xLua/ServerHost/fixture-content"),
            };
            foreach (string c in candidates)
            {
                if (File.Exists(Path.Combine(c, "version.json")))
                    return c;
            }

            Assert.Fail("缺少正式 ContentRoot/version.json");
            return null;
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            // 断言消息禁密码/Token 原文；仅保留短原因。
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        static bool PortOpen(int port)
        {

            try
            {
                using (var client = new TcpClient())
                {
                    var ar = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(200);
                    if (!ok)
                        return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
