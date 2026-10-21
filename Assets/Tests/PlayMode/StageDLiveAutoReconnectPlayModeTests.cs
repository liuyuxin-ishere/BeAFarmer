using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.Boot;
using Game.Framework.Lua;
using Game.Framework.Resource;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;
using Debug = UnityEngine.Debug;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// T57 完整链路（Editor 定位）：真实 Launcher + Lua entry 绑定 + UI 登录后
    /// DropLiveSocket（不手动 Begin/Restore/ShowMask），等待生产心跳收尾自动重连，
    /// Lua apply_and_commit 覆盖被污染的 PlayerModel，遮罩解除，DisposeError 空。
    /// </summary>
    public sealed class StageDLiveAutoReconnectPlayModeTests
    {
        const string EvidenceRel = "Docs/Acceptance/M1/20261020-xLua/StageD";

        string _root;
        int _tcpPort;
        int _httpPort;
        Process _server;
        string _stopFile;
        string _evidence;
        string _dbPath;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FarmingDLiveRc", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _evidence = Path.GetFullPath(EvidenceRel);
            Directory.CreateDirectory(_evidence);
            _tcpPort = FreePort();
            _httpPort = FreePort();
            _stopFile = Path.Combine(_root, "stop.signal");
            _dbPath = Path.Combine(_root, "d-live.db");

            string content = FindContentRoot();
            string script = Path.GetFullPath("Tools/run_server.ps1");
            Assert.That(File.Exists(script), Is.True);

            string args =
                "-NoProfile -File \"" + script + "\""
                + " -RepoRoot \"" + Path.GetFullPath(".") + "\""
                + " -DatabasePath \"" + _dbPath + "\""
                + " -TcpPort " + _tcpPort
                + " -HttpPort " + _httpPort
                + " -ContentRoot \"" + content + "\""
                + " -LogDirectory \"" + Path.Combine(_root, "logs") + "\""
                + " -StopFile \"" + _stopFile + "\""
                + " -SkipBuild";
            if (!Directory.Exists(Path.GetFullPath("Temp/M1ServerBuild")))
                args = args.Replace(" -SkipBuild", "");

            _server = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = args,
                WorkingDirectory = Path.GetFullPath("."),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.That(_server, Is.Not.Null);

            float until = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < until)
            {
                if (_server.HasExited)
                    Assert.Fail("server exited " + _server.ExitCode);
                if (PortOpen(_tcpPort) && PortOpen(_httpPort))
                    break;
                yield return null;
            }

            Assert.That(PortOpen(_tcpPort), Is.True, "TCP");
            YooPackageOwnership.ClearAllForTests();
            var mgr = UiBridge.Manager;
            if (mgr != null)
            {
                mgr.Dispose();
                UiBridge.ClearManager(mgr);
            }

            AccountFlow.Shutdown();
            try { AccountBridge.Dispose(); } catch { }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                var mgr = UiBridge.Manager;
                if (mgr != null)
                {
                    mgr.Dispose();
                    UiBridge.ClearManager(mgr);
                }
            }
            catch { }

            AccountFlow.Shutdown();
            try { AccountBridge.Dispose(); } catch { }

            if (YooAssets.IsInitialized)
            {
                foreach (string name in new[] { ResMgr.PackageName, HotfixLoader.LuaPackageName })
                {
                    if (YooAssets.TryGetPackage(name, out ResourcePackage pkg) && pkg != null)
                    {
                        DestroyPackageOperation destroy = pkg.DestroyPackageAsync();
                        yield return destroy;
                        YooAssets.RemovePackage(name);
                        YooPackageOwnership.Clear(name);
                    }
                }

                YooAssets.Destroy();
            }

            YooPackageOwnership.ClearAllForTests();
            try
            {
                if (!string.IsNullOrEmpty(_stopFile))
                    File.WriteAllText(_stopFile, "stop");
            }
            catch { }

            float until = Time.realtimeSinceStartup + 30f;
            while (_server != null && !_server.HasExited && Time.realtimeSinceStartup < until)
                yield return null;
            try
            {
                if (_server != null && !_server.HasExited)
                    _server.Kill();
            }
            catch { }

            try { _server?.Dispose(); } catch { }
            _server = null;
        }

        [UnityTest]
        public IEnumerator Live_LauncherLua_DropSocket_AutoRestore_CommitsAndHud()
        {
            string cdn100 = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Docs", "Acceptance", "M1", "20261020-xLua", "StageB", "cdn-isolated", "100"));
            if (!File.Exists(Path.Combine(cdn100, "GameLua.version")))
            {
                Assert.Ignore("StageB/cdn-isolated/100 missing");
                yield break;
            }

            string httpRoot = Path.Combine(_root, "http");
            Directory.CreateDirectory(httpRoot);
            foreach (string file in Directory.GetFiles(cdn100))
                File.Copy(file, Path.Combine(httpRoot, Path.GetFileName(file)), true);

            string indexRoot = Path.Combine(_root, "index");
            string cacheRoot = Path.Combine(_root, "yoo");
            string tokenDir = Path.Combine(_root, "tokens");
            Directory.CreateDirectory(indexRoot);

            var httpLog = new List<string>();
            using (var http = StaticFileHttp.Start(httpRoot, httpLog))
            {
                string versionJson =
                    "{\"MinAppVersion\":\"0.3.0\",\"ResVersion\":100,\"ProtoVersion\":1,\"ConfigVersion\":100,"
                    + "\"PackageVersion\":\"100\",\"BaseUrl\":\"" + http.BaseUrl.TrimEnd('/') + "/\"}";
                File.WriteAllText(Path.Combine(httpRoot, "version.json"), versionJson, new UTF8Encoding(false));

                var launcher = new Launcher(
                    httpBase: http.BaseUrl,
                    cacheRoot: cacheRoot,
                    indexRoot: indexRoot,
                    tcpHost: "127.0.0.1",
                    tcpPort: _tcpPort,
                    tokenDir: tokenDir,
                    log: s => Debug.Log("[DLiveRc] " + s));

                yield return launcher.RunAsync(CancellationToken.None).ToCoroutine();
                Assert.That(launcher.LoginOpened, Is.True);

                string user = "drc" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string pass = "Pw" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string nick = "NickDrc";

                GameObject loginWin = GameObject.Find("Win_Login");
                Assert.That(loginWin, Is.Not.Null);
                SetInput(loginWin, "Username", user);
                SetInput(loginWin, "Password", pass);
                SetInput(loginWin, "Nickname", nick);
                yield return ClickChild(loginWin, "RegisterButton");
                yield return WaitUntil(() =>
                {
                    string err = GetText(loginWin, "ErrorLabel");
                    return err != null && (err.Contains("注册成功") || err.Contains("请登录"));
                }, 20f);

                SetInput(loginWin, "Password", pass);
                yield return ClickChild(loginWin, "LoginButton");
                yield return WaitUntil(() =>
                {
                    GameObject home = GameObject.Find("Win_HomeHud");
                    return home != null && home.activeInHierarchy && AccountBridge.IsAuthenticated();
                }, 45f);

                GameObject homeWin = GameObject.Find("Win_HomeHud");
                Assert.That(homeWin, Is.Not.Null);
                string nickBefore = GetText(homeWin, "Nickname") ?? "";
                Assert.That(nickBefore == nick || nickBefore.Contains(nick), Is.True, "nick=" + nickBefore);

                // 主动污染本地模型：若自动恢复未走 Lua apply，HUD 会保持污染值。
                bool polluted = false;
                if (launcher.Lua?.Env != null)
                {
                    object[] r = launcher.Lua.Env.DoString(
                        "local M=require('PlayerModel'); M.nickname='POLLUTED_LOCAL'; M.level=999; return M.nickname",
                        "pollute");
                    polluted = r != null && r.Length > 0 && Convert.ToString(r[0]) == "POLLUTED_LOCAL";
                }

                Assert.That(polluted, Is.True, "PlayerModel polluted");
                string tokenBefore = ReadTokenFile(tokenDir);

                // 仅切断真实 socket；禁止 ShowMask / Restore / BeginAutoReconnect。
                AccountBridge.DropLiveSocketForAcceptance();

                bool sawMask = false;
                float until = Time.realtimeSinceStartup + 45f;
                while (Time.realtimeSinceStartup < until)
                {
                    if (AccountBridge.IsDisconnectMaskVisible())
                        sawMask = true;
                    // 恢复完成：已认证、遮罩关、昵称回到服务端值（非 POLLUTED）
                    homeWin = GameObject.Find("Win_HomeHud");
                    string nickNow = homeWin != null ? (GetText(homeWin, "Nickname") ?? "") : "";
                    if (sawMask
                        && AccountBridge.IsAuthenticated()
                        && !AccountBridge.IsDisconnectMaskVisible()
                        && nickNow.IndexOf("POLLUTED", StringComparison.Ordinal) < 0
                        && (nickNow.Contains(nick) || nickNow == nick))
                        break;
                    yield return null;
                }

                homeWin = GameObject.Find("Win_HomeHud");
                string nickAfter = homeWin != null ? (GetText(homeWin, "Nickname") ?? "") : "";
                string report =
                    "sawMask=" + sawMask
                    + "\nauth=" + AccountBridge.IsAuthenticated()
                    + "\nmaskFinal=" + AccountBridge.IsDisconnectMaskVisible()
                    + "\nnickBefore=" + nickBefore
                    + "\nnickAfter=" + nickAfter
                    + "\ntokenSame=" + (ReadTokenFile(tokenDir) == tokenBefore)
                    + "\nsnapshotHasPlayerId=" + (AccountBridge.LastSnapshotJson() ?? "").Contains("playerId")
                    + "\n";
                File.WriteAllText(Path.Combine(_evidence, "live-lua-auto-reconnect.txt"), report, new UTF8Encoding(false));

                Assert.That(sawMask, Is.True, "auto reconnect must show mask");
                Assert.That(AccountBridge.IsAuthenticated(), Is.True);
                Assert.That(AccountBridge.IsDisconnectMaskVisible(), Is.False, "mask hidden after Lua commit");
                Assert.That(nickAfter.IndexOf("POLLUTED", StringComparison.Ordinal), Is.LessThan(0), "lua snapshot overwrite");
                Assert.That(ReadTokenFile(tokenDir), Is.EqualTo(tokenBefore), "same token no password");
                Assert.That(AccountBridge.LastSnapshotJson(), Does.Contain("playerId"));

                yield return ClickChild(homeWin, "LogoutButton");
                yield return WaitUntil(() =>
                    GameObject.Find("Win_Login") != null && !AccountBridge.IsAuthenticated(), 45f);

                launcher.Dispose();
                Assert.That(string.IsNullOrEmpty(launcher.DisposeError), Is.True,
                    "DisposeError=" + launcher.DisposeError);
                File.WriteAllText(
                    Path.Combine(_evidence, "live-lua-dispose-error.txt"),
                    "empty=" + string.IsNullOrEmpty(launcher.DisposeError) + "\n",
                    new UTF8Encoding(false));
            }
        }

        static string ReadTokenFile(string dir)
        {
            if (!Directory.Exists(dir))
                return "";
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string t = File.ReadAllText(f).Trim();
                    if (!string.IsNullOrEmpty(t))
                        return t;
                }
                catch { }
            }

            return "";
        }

        static void SetInput(GameObject root, string child, string value)
        {
            var t = root.transform.Find(child)?.GetComponent<InputField>()
                    ?? root.transform.Find(child)?.GetComponentInChildren<InputField>();
            Assert.That(t, Is.Not.Null, child);
            t.text = value;
        }

        static string GetText(GameObject root, string child)
        {
            var tx = root.transform.Find(child)?.GetComponent<Text>()
                     ?? root.transform.Find(child)?.GetComponentInChildren<Text>();
            return tx != null ? tx.text : null;
        }

        static IEnumerator ClickChild(GameObject root, string child)
        {
            var btn = root.transform.Find(child)?.GetComponent<Button>()
                      ?? root.transform.Find(child)?.GetComponentInChildren<Button>();
            Assert.That(btn, Is.Not.Null, child);
            ExecuteEvents.Execute(btn.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
            yield return null;
        }

        static IEnumerator WaitUntil(Func<bool> pred, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                if (pred())
                    yield break;
                yield return null;
            }
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

            Assert.Fail("缺少 ContentRoot");
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

        sealed class StaticFileHttp : IDisposable
        {
            readonly TcpListener _listener;
            readonly List<string> _log;
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly System.Threading.Tasks.Task _loop;
            readonly string _root;

            StaticFileHttp(TcpListener listener, string root, List<string> log)
            {
                _listener = listener;
                _root = root;
                _log = log;
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUrl = "http://localhost:" + port + "/";
                _loop = System.Threading.Tasks.Task.Run(() => AcceptLoop());
            }

            public string BaseUrl { get; }

            public static StaticFileHttp Start(string root, List<string> log)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new StaticFileHttp(listener, root, log);
            }

            async System.Threading.Tasks.Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { break; }
                    _ = System.Threading.Tasks.Task.Run(() => Handle(client));
                }
            }

            void Handle(TcpClient client)
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line))
                        return;
                    string[] parts = line.Split(' ');
                    if (parts.Length < 2)
                        return;
                    string path = parts[1];
                    if (path.StartsWith("/"))
                        path = path.Substring(1);
                    if (path.Contains("?"))
                        path = path.Substring(0, path.IndexOf('?'));
                    while (!string.IsNullOrEmpty(reader.ReadLine())) { }

                    string full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
                    byte[] body;
                    string status;
                    if (File.Exists(full))
                    {
                        body = File.ReadAllBytes(full);
                        status = "200 OK";
                        _log.Add("200 " + path + " " + body.Length);
                    }
                    else
                    {
                        body = Encoding.UTF8.GetBytes("missing");
                        status = "404 Not Found";
                        _log.Add("404 " + path);
                    }

                    byte[] header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 " + status + "\r\nContent-Length: " + body.Length
                        + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    stream.Write(body, 0, body.Length);
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                try { _loop.Wait(1000); } catch { }
                _cts.Dispose();
            }
        }
    }
}
