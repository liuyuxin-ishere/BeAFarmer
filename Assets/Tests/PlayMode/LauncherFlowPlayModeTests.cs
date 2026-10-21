using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.Boot;
using Game.Framework.Resource;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>正式 Launcher 冷启：入口前 EnsureManager、等 OpenLogin、索引字节事务。</summary>
    public sealed class LauncherFlowPlayModeTests
    {
        string _root;
        int _tcpPort;
        int _httpPort;
        Process _server;
        string _stopFile;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FarmingC2Launcher", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _tcpPort = FreePort();
            _httpPort = FreePort();
            _stopFile = Path.Combine(_root, "stop.signal");
            string content = FindContentRoot();
            string script = Path.GetFullPath("Tools/run_server.ps1");
            Assert.That(File.Exists(script), Is.True);

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

            Assert.That(PortOpen(_httpPort), Is.True, "HTTP");
            YooPackageOwnership.ClearAllForTests();
            var mgr = UiBridge.Manager;
            if (mgr != null)
            {
                mgr.Dispose();
                UiBridge.ClearManager(mgr);
            }
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
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [UnityTest]
        public IEnumerator Launcher_ColdStart_OpensLogin_ThenCommitsIndex()
        {
            string cdn100 = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Docs", "Acceptance", "M1", "20261020-xLua", "StageB", "cdn-isolated", "100"));
            if (!File.Exists(Path.Combine(cdn100, "GameLua.version")))
            {
                Assert.Ignore("StageB/cdn-isolated/100/GameLua.version missing");
                yield break;
            }

            // 单 HTTP 根：version.json + GameLua CDN 文件（DefaultPackage Host 留 T58；编辑器 ResMgr=Simulate）
            string httpRoot = Path.Combine(_root, "http");
            Directory.CreateDirectory(httpRoot);
            foreach (string file in Directory.GetFiles(cdn100))
                File.Copy(file, Path.Combine(httpRoot, Path.GetFileName(file)), true);

            string indexRoot = Path.Combine(_root, "index");
            string cacheRoot = Path.Combine(_root, "yoo");
            string tokenDir = Path.Combine(_root, "tokens");
            Directory.CreateDirectory(indexRoot);
            string oldIndex = "{\"resVersion\":1,\"configVersion\":1,\"protoVersion\":1,\"packageVersion\":\"old\",\"baseUrl\":\"http://localhost/\",\"packages\":[]}\n";
            byte[] oldBytes = Encoding.UTF8.GetBytes(oldIndex);
            string indexPath = Path.Combine(indexRoot, "active-version.json");
            File.WriteAllBytes(indexPath, oldBytes);
            string oldSha = Sha256Hex(oldBytes);

            var httpLog = new System.Collections.Generic.List<string>();
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
                    log: s => UnityEngine.Debug.Log("[C2LauncherTest] " + s));

                yield return launcher.RunAsync(CancellationToken.None).ToCoroutine();

                Assert.That(launcher.EntryReady, Is.True, "entryReady last=" + launcher.LastError);
                Assert.That(launcher.LoginOpened, Is.True, "loginOpened");
                Assert.That(launcher.Committed, Is.True, "committed");
                Assert.That(UiBridge.Manager, Is.Not.Null);
                Assert.That(GameObject.Find("Win_Login"), Is.Not.Null, "Login window present");

                byte[] after = File.ReadAllBytes(indexPath);
                Assert.That(Sha256Hex(after), Is.Not.EqualTo(oldSha), "index updated after login-ready");
                Assert.That(Encoding.UTF8.GetString(after), Does.Contain("resVersion"));

                // Live UI screenshots belong to ClosingLiveAccountUiPlayModeTests.
                // Keep this test focused on the real cold-start/index transaction;
                // WaitForEndOfFrame is never yielded by batch-mode tests.

                launcher.Dispose();
                Assert.That(string.IsNullOrEmpty(launcher.DisposeError), Is.True,
                    "DisposeError=" + launcher.DisposeError);
                Assert.That(UiBridge.Scenes, Is.Null, "Scenes unbound after Dispose");
            }
        }

        [UnityTest]
        public IEnumerator Launcher_CancelBeforeReady_KeepsOldIndexBytes()
        {
            string indexRoot = Path.Combine(_root, "index-cancel");
            Directory.CreateDirectory(indexRoot);
            string oldIndex = "{\"resVersion\":9,\"configVersion\":9,\"protoVersion\":1,\"packageVersion\":\"keep\",\"baseUrl\":\"http://localhost/\",\"packages\":[]}\n";
            byte[] oldBytes = Encoding.UTF8.GetBytes(oldIndex);
            string indexPath = Path.Combine(indexRoot, "active-version.json");
            File.WriteAllBytes(indexPath, oldBytes);
            string oldSha = Sha256Hex(oldBytes);

            var launcher = new Launcher(
                httpBase: "http://localhost:" + _httpPort + "/",
                cacheRoot: Path.Combine(_root, "yoo-cancel"),
                indexRoot: indexRoot,
                tcpHost: "127.0.0.1",
                tcpPort: _tcpPort,
                tokenDir: Path.Combine(_root, "tokens-cancel"));

            var cts = new CancellationTokenSource();
            bool finished = false;
            Exception caught = null;
            UniTask.Void(async () =>
            {
                try
                {
                    await launcher.RunAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                finally
                {
                    finished = true;
                }
            });

            yield return null;
            yield return null;
            cts.Cancel();
            float until = Time.realtimeSinceStartup + 60f;
            while (!finished && Time.realtimeSinceStartup < until)
                yield return null;

            Assert.That(finished, Is.True, "run finished");
            Assert.That(caught, Is.Not.Null, "cancel or fail observed");
            Assert.That(File.Exists(indexPath), Is.True);
            Assert.That(Sha256Hex(File.ReadAllBytes(indexPath)), Is.EqualTo(oldSha), "old index bytes kept");
            Assert.That(launcher.Committed, Is.False);
            Assert.That(launcher.LoginOpened, Is.False);
            launcher.Dispose();
        }

        static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
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

            Assert.Fail("缺少 ContentRoot/version.json");
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

        /// <summary>最小静态文件 HTTP（仅本夹具）。</summary>
        sealed class StaticFileHttp : IDisposable
        {
            readonly TcpListener _listener;
            readonly System.Collections.Generic.List<string> _log;
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly System.Threading.Tasks.Task _loop;
            readonly string _root;

            StaticFileHttp(TcpListener listener, string root, System.Collections.Generic.List<string> log)
            {
                _listener = listener;
                _root = root;
                _log = log;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" + Port + "/";
                _loop = System.Threading.Tasks.Task.Run(() => Loop(_cts.Token));
            }

            public int Port { get; }
            public string BaseUrl { get; }

            public static StaticFileHttp Start(string root, System.Collections.Generic.List<string> log)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new StaticFileHttp(listener, root, log);
            }

            void Loop(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { break; }

                    try
                    {
                        using (client)
                        using (NetworkStream stream = client.GetStream())
                        {
                            var buffer = new byte[8192];
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read <= 0)
                                continue;
                            string request = Encoding.ASCII.GetString(buffer, 0, read);
                            string path = "/";
                            int lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
                            if (lineEnd > 0)
                            {
                                string[] parts = request.Substring(0, lineEnd).Split(' ');
                                if (parts.Length >= 2)
                                    path = parts[1];
                            }

                            int q = path.IndexOf('?');
                            if (q >= 0)
                                path = path.Substring(0, q);
                            string rel = Uri.UnescapeDataString(path).TrimStart('/');
                            if (string.IsNullOrEmpty(rel))
                                rel = "version.json";
                            _log.Add(rel);
                            string full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(full))
                            {
                                byte[] notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                                stream.Write(notFound, 0, notFound.Length);
                                continue;
                            }

                            byte[] body = File.ReadAllBytes(full);
                            byte[] header = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length
                                + "\r\nConnection: close\r\n\r\n");
                            stream.Write(header, 0, header.Length);
                            stream.Write(body, 0, body.Length);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                try { _loop.Wait(1000); } catch { }
                _cts.Dispose();
            }
        }
    }
}
