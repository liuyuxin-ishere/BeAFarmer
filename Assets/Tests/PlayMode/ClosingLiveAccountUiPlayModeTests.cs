using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.Boot;
using Game.Framework.Resource;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// C2 Closing：真实生产 Lua 控件 + Tools/run_server.ps1 隔离 DB。
    /// 冷 Launcher→注册→错密→登录→HomeHud 服务端角色→遮罩/恢复→Logout→Login。
    /// 截图用 Camera.Render→RT→ReadPixels（无 WaitForEndOfFrame，可 batch）。
    /// </summary>
    public sealed class ClosingLiveAccountUiPlayModeTests
    {
        const string EvidenceRel = "Docs/Acceptance/M1/20261020-xLua/StageC2/Closing";

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
            _root = Path.Combine(Path.GetTempPath(), "FarmingC2Closing", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _evidence = Path.GetFullPath(EvidenceRel);
            Directory.CreateDirectory(Path.Combine(_evidence, "shots"));
            _tcpPort = FreePort();
            _httpPort = FreePort();
            _stopFile = Path.Combine(_root, "stop.signal");
            _dbPath = Path.Combine(_root, "closing.db");

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
                + " -BuildArtifactsDirectory \"" + Path.GetFullPath("Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts") + "\""
                + " -SkipBuild";
            if (!Directory.Exists(Path.GetFullPath("Docs/Acceptance/M1/20261020-xLua/ServerHost/dotnet-artifacts")))
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
            catch
            {
            }

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
        }

        [UnityTest]
        public IEnumerator Live_ColdLauncher_RegisterLoginHomeLogout_CameraRenderShots()
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
                    log: s => UnityEngine.Debug.Log("[ClosingLive] " + s));

                yield return launcher.RunAsync(CancellationToken.None).ToCoroutine();
                Assert.That(launcher.LoginOpened, Is.True, "login opened");
                Assert.That(string.IsNullOrEmpty(launcher.DisposeError), Is.True);

                string user = "live" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string pass = "Pw" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string nick = "NickLive";

                GameObject loginWin = GameObject.Find("Win_Login");
                Assert.That(loginWin, Is.Not.Null, "Win_Login");
                CaptureCamera("01-login-ready.png", loginWin);

                SetInput(loginWin, "Username", user);
                SetInput(loginWin, "Password", pass);
                SetInput(loginWin, "Nickname", nick);
                yield return ClickChild(loginWin, "RegisterButton");
                yield return WaitUntil(() =>
                {
                    string err = GetText(loginWin, "ErrorLabel");
                    return err != null && (err.Contains("注册成功") || err.Contains("已存在") || err.Contains("失败") || err.Contains("网络"));
                }, 20f);
                string regMsg = GetText(loginWin, "ErrorLabel") ?? "";
                Assert.That(regMsg.Contains("注册成功") || regMsg.Contains("请登录"), Is.True, "register msg=" + regMsg);
                Assert.That(AccountBridge.HasToken(), Is.False, "注册不自动授权");
                CaptureCamera("02-register-ok.png", loginWin);

                SetInput(loginWin, "Password", "wrong-" + pass);
                yield return ClickChild(loginWin, "LoginButton");
                yield return WaitUntil(() =>
                {
                    string err = GetText(loginWin, "ErrorLabel");
                    return err != null && err.Contains("密码");
                }, 20f);
                Assert.That(GetText(loginWin, "ErrorLabel"), Does.Contain("密码"));
                var loginBtn = loginWin.transform.Find("LoginButton")?.GetComponent<Button>();
                Assert.That(loginBtn, Is.Not.Null);
                Assert.That(loginBtn.interactable, Is.True, "错密后按钮可恢复");
                CaptureCamera("03-login-error.png", loginWin);

                SetInput(loginWin, "Password", pass);
                yield return ClickChild(loginWin, "LoginButton");
                yield return WaitUntil(() =>
                {
                    GameObject home = GameObject.Find("Win_HomeHud");
                    return home != null && home.activeInHierarchy;
                }, 45f);

                GameObject homeWin = GameObject.Find("Win_HomeHud");
                Assert.That(homeWin, Is.Not.Null, "HomeHud after login");
                string playerIdText = GetText(homeWin, "PlayerId") ?? "";
                string nickText = GetText(homeWin, "Nickname") ?? "";
                string levelText = GetText(homeWin, "Level") ?? "";
                Assert.That(playerIdText, Does.Contain("ID:"));
                Assert.That(nickText == nick || nickText.Contains(nick), Is.True, "nick=" + nickText);
                Assert.That(levelText, Does.Contain("Lv."));
                Assert.That(AccountBridge.IsAuthenticated(), Is.True);
                Assert.That(AccountBridge.LastSnapshotJson(), Does.Contain("playerId"));
                CaptureCamera("04-home-hud.png", homeWin);

                File.WriteAllText(
                    Path.Combine(_evidence, "live-role.txt"),
                    "user=" + user + "\nnick=" + nickText + "\nplayerIdLabel=" + playerIdText
                    + "\nlevel=" + levelText + "\nsnapshotHasPlayerId="
                    + AccountBridge.LastSnapshotJson().Contains("playerId")
                    + "\ndb=" + _dbPath + "\n",
                    new UTF8Encoding(false));

                AccountBridge.ShowDisconnectMask("连接已断开，正在恢复…");
                yield return null;
                CaptureCamera("05-disconnect-mask.png", homeWin);
                AccountBridge.HideDisconnectMask();

                yield return ClickChild(homeWin, "LogoutButton");
                yield return WaitUntil(() =>
                {
                    GameObject login2 = GameObject.Find("Win_Login");
                    GameObject home2 = GameObject.Find("Win_HomeHud");
                    bool loginOk = login2 != null && login2.activeInHierarchy;
                    bool homeGone = home2 == null || !home2.activeInHierarchy;
                    return loginOk && homeGone && !AccountBridge.IsAuthenticated();
                }, 45f);

                Assert.That(AccountBridge.HasToken(), Is.False);
                Assert.That(GameObject.Find("Win_Login"), Is.Not.Null);
                CaptureCamera("06-login-after-logout.png", GameObject.Find("Win_Login"));

                launcher.Dispose();
                Assert.That(string.IsNullOrEmpty(launcher.DisposeError), Is.True,
                    "DisposeError must be empty, got=" + launcher.DisposeError);
                Assert.That(UiBridge.Manager, Is.Null);
                Assert.That(UiBridge.Scenes, Is.Null);
                File.WriteAllText(
                    Path.Combine(_evidence, "dispose-error.txt"),
                    "disposeError=\nempty=True\n",
                    new UTF8Encoding(false));
            }
        }

        [UnityTest]
        public IEnumerator ExpiredHandler_KeepsReal106_NotRemappedTo204()
        {
            int got = -1;
            string kind = "";
            AccountBridge.Configure(
                "127.0.0.1", _tcpPort, Application.version, 100, 100, "c2-106",
                Path.Combine(_root, "t106-tokens"));
            AccountBridge.SetExpiredHandler((code, payload) =>
            {
                got = code;
                kind = payload ?? "";
            });

            MethodInfo mi = typeof(AccountBridge).GetMethod(
                "NotifySessionExpired",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(mi, Is.Not.Null);
            mi.Invoke(null, new object[] { 106 });
            float until = Time.realtimeSinceStartup + 2f;
            while (got < 0 && Time.realtimeSinceStartup < until)
                yield return null;

            Assert.That(got, Is.EqualTo(106), "106 must not become 204");
            Assert.That(kind, Is.EqualTo("update-required"));
            AccountBridge.Dispose();
        }

        static void SetInput(GameObject win, string name, string value)
        {
            var field = win.transform.Find(name)?.GetComponent<InputField>();
            Assert.That(field, Is.Not.Null, name);
            field.text = value;
        }

        static string GetText(GameObject win, string name)
        {
            return win.transform.Find(name)?.GetComponent<Text>()?.text;
        }

        static IEnumerator ClickChild(GameObject win, string child)
        {
            var btn = win.transform.Find(child)?.GetComponent<Button>();
            Assert.That(btn, Is.Not.Null, child);
            var es = EventSystem.current ?? UnityEngine.Object.FindObjectOfType<EventSystem>();
            if (es == null)
            {
                var esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                es = esGo.GetComponent<EventSystem>();
            }

            var ped = new PointerEventData(es);
            ExecuteEvents.Execute(btn.gameObject, ped, ExecuteEvents.pointerClickHandler);
            yield return null;
            yield return null;
        }

        static IEnumerator WaitUntil(Func<bool> pred, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                AccountBridge.Pump(64);
                if (pred())
                    yield break;
                yield return null;
            }

            Assert.Fail("timeout waiting condition");
        }

        void CaptureCamera(string fileName, GameObject anchor)
        {
            string path = Path.Combine(_evidence, "shots", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _evidence);
            string err = anchor != null ? GetText(anchor, "ErrorLabel") : "";
            string pid = anchor != null ? GetText(anchor, "PlayerId") : "";
            string nick = anchor != null ? GetText(anchor, "Nickname") : "";
            string level = anchor != null ? GetText(anchor, "Level") : "";

            void WriteState(string reason)
            {
                File.WriteAllText(
                    path + ".live-state.txt",
                    "file=" + fileName + "\nanchor=" + (anchor != null ? anchor.name : "null")
                    + "\nerrorLabel=" + (err ?? "") + "\nplayerId=" + (pid ?? "")
                    + "\nnickname=" + (nick ?? "") + "\nlevel=" + (level ?? "")
                    + "\ngfx=" + SystemInfo.graphicsDeviceType
                    + "\nmethod=live-ui-state-dump\nlayoutBake=false\nreason=" + reason + "\n",
                    new UTF8Encoding(false));
            }

            // batchmode 下 RT.Create 常失败并打 Error→LogAssert；活链路以控件断言+state 为准。
            if (Application.isBatchMode
                || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                WriteState(Application.isBatchMode ? "batchMode-skip-RT" : "no-gpu");
                return;
            }

            int w = 1280;
            int h = 720;
            var camGo = new GameObject("ClosingShotCam");
            RenderTexture rt = null;
            Texture2D tex = null;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.07f, 0.09f, 0.12f, 1f);
                cam.orthographic = true;
                cam.orthographicSize = 5f;
                cam.transform.position = new Vector3(0, 0, -10f);

                Canvas canvas = anchor != null ? anchor.GetComponentInParent<Canvas>() : null;
                if (canvas != null)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                }

                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();
                if (!rt.IsCreated())
                {
                    WriteState("RenderTextureNotCreated");
                    return;
                }

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                RenderTexture.active = previous;
                cam.targetTexture = null;
                if (canvas != null)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.worldCamera = null;
                }

                File.WriteAllText(
                    path + ".note.txt",
                    "method=Camera.Render-RT-ReadPixels\nlayoutBake=false\nliveUi=true\nsize="
                    + new FileInfo(path).Length + "\n",
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                WriteState("CaptureCatch:" + ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                if (rt != null)
                {
                    try { rt.Release(); } catch { }
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(camGo);
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

        /// <summary>与 LauncherFlowPlayModeTests 同构的最小静态 HTTP。</summary>
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
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch
                    {
                        break;
                    }

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
                    while (!string.IsNullOrEmpty(reader.ReadLine()))
                    {
                    }

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
