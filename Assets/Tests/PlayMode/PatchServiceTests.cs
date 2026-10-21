using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Boot;
using Game.Framework.Resource;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// PatchService：活动索引契约、真实 TCP HTTP + Yoo GameLua 100→101 差异下载。
    /// CDN 取自 StageB/cdn-isolated（脚本复制，不改迁移归档）。
    /// </summary>
    public sealed class PatchServiceTests
    {
        static string StageBRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Docs", "Acceptance", "M1", "20261020-xLua", "StageB"));

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (YooAssets.IsInitialized)
            {
                if (YooAssets.TryGetPackage(HotfixLoader.LuaPackageName, out ResourcePackage lua) && lua != null)
                {
                    DestroyPackageOperation destroy = lua.DestroyPackageAsync();
                    yield return destroy;
                    YooAssets.RemovePackage(HotfixLoader.LuaPackageName);
                    YooPackageOwnership.Clear(HotfixLoader.LuaPackageName);
                }

                YooAssets.Destroy();
            }

            YooPackageOwnership.ClearAllForTests();
        }

        [UnityTest]
        public IEnumerator CommitVersion_WithoutPrepare_Throws_AndNoIndex()
        {
            string root = Path.Combine(StageBRoot, "cache-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var service = new PatchService(root, Path.Combine(root, "yoo"), new[] { HotfixLoader.LuaPackageName });
                Assert.That(service.HasActiveIndex, Is.False);
                Assert.That(service.TryReadActiveIndex(), Is.Null);
                var manifest = new VersionManifest
                {
                    ResVersion = 100,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "100",
                    BaseUrl = "http://127.0.0.1:9/"
                };
                Assert.Throws<InvalidOperationException>(() => service.CommitVersion(manifest));
                Assert.That(service.HasActiveIndex, Is.False);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Prepare_RealHttp_100Then101_DiffOnly_AndCancelDoesNotCommit()
        {
            string cdn100 = Path.Combine(StageBRoot, "cdn-isolated", "100");
            string cdn101 = Path.Combine(StageBRoot, "cdn-isolated", "101");
            if (!File.Exists(Path.Combine(cdn100, "GameLua.version")) ||
                !File.Exists(Path.Combine(cdn101, "GameLua.version")))
            {
                Assert.Ignore("StageB cdn-isolated/100|101 未准备；先跑 StageB/scripts/Prepare-CdnIsolated.ps1");
                yield break;
            }

            string work = Path.Combine(StageBRoot, "cache-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var httpLog = new List<string>();
            using (var http = FileHttp.Start(cdn100, httpLog))
            {
                var progressLog = new List<PatchProgress>();
                var progress = new Progress<PatchProgress>(p => progressLog.Add(p));
                var service = new PatchService(
                    Path.Combine(work, "index"),
                    Path.Combine(work, "yoo"),
                    new[] { HotfixLoader.LuaPackageName });

                var manifest100 = new VersionManifest
                {
                    MinAppVersion = "0.2.0",
                    ResVersion = 100,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "100",
                    BaseUrl = http.BaseUrl.TrimEnd('/')
                };

                Exception error100 = null;
                yield return service.PrepareAsync(manifest100, progress, CancellationToken.None)
                    .ToCoroutine(e => error100 = e);
                Assert.That(error100, Is.Null, error100 != null ? error100.ToString() : "");
                Assert.That(service.Candidate, Is.Not.Null);
                int files100 = service.Candidate.TotalDownloadFiles;
                long bytes100 = service.Candidate.TotalDownloadBytes;
                service.CommitVersion(manifest100);
                Assert.That(service.HasActiveIndex, Is.True);
                Assert.That(service.TryReadActiveIndex(), Does.Contain("\"resVersion\":100"));
                int requestsAfter100 = httpLog.Count;

                http.SwapRoot(cdn101);
                httpLog.Clear();

                var manifest101 = new VersionManifest
                {
                    MinAppVersion = "0.2.0",
                    ResVersion = 101,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "101",
                    BaseUrl = http.BaseUrl.TrimEnd('/')
                };

                if (YooAssets.TryGetPackage(HotfixLoader.LuaPackageName, out ResourcePackage oldPkg) && oldPkg != null)
                {
                    DestroyPackageOperation destroy = oldPkg.DestroyPackageAsync();
                    yield return destroy;
                    YooAssets.RemovePackage(HotfixLoader.LuaPackageName);
                }

                var service2 = new PatchService(
                    Path.Combine(work, "index"),
                    Path.Combine(work, "yoo"),
                    new[] { HotfixLoader.LuaPackageName });
                Exception error101 = null;
                yield return service2.PrepareAsync(manifest101, progress, CancellationToken.None)
                    .ToCoroutine(e => error101 = e);
                Assert.That(error101, Is.Null, error101 != null ? error101.ToString() : "");
                Assert.That(service2.Candidate, Is.Not.Null);

                int files101 = service2.Candidate.TotalDownloadFiles;
                long bytes101 = service2.Candidate.TotalDownloadBytes;
                Directory.CreateDirectory(StageBRoot);
                File.WriteAllText(
                    Path.Combine(StageBRoot, "patch-diff-counts.txt"),
                    "files100=" + files100 + " bytes100=" + bytes100
                    + " files101=" + files101 + " bytes101=" + bytes101
                    + " httpAfter100=" + requestsAfter100
                    + " http101=" + httpLog.Count + "\n"
                    + "progressStages=" + progressLog.Count + "\n");

                Assert.That(files101, Is.GreaterThanOrEqualTo(0));
                if (files100 > 0)
                    Assert.That(files101, Is.LessThanOrEqualTo(files100));

                service2.CommitVersion(manifest101);
                Assert.That(service2.TryReadActiveIndex(), Does.Contain("\"resVersion\":101"));

                if (YooAssets.TryGetPackage(HotfixLoader.LuaPackageName, out ResourcePackage pkg3) && pkg3 != null)
                {
                    DestroyPackageOperation d3 = pkg3.DestroyPackageAsync();
                    yield return d3;
                    YooAssets.RemovePackage(HotfixLoader.LuaPackageName);
                }

                var service3 = new PatchService(
                    Path.Combine(work, "index"),
                    Path.Combine(work, "yoo"),
                    new[] { HotfixLoader.LuaPackageName });
                var cts = new CancellationTokenSource();
                cts.Cancel();
                Exception cancelError = null;
                yield return service3.PrepareAsync(manifest101, progress, cts.Token)
                    .ToCoroutine(e => cancelError = e);
                Assert.That(cancelError, Is.InstanceOf<OperationCanceledException>());
                Assert.That(service3.Candidate, Is.Null);
                Assert.That(File.ReadAllText(service3.ActiveIndexPath), Does.Contain("\"resVersion\":101"));
            }

            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
        }

        [UnityTest]
        public IEnumerator Commit_RejectsMutatedManifest_AfterPrepare()
        {
            string cdn100 = Path.Combine(StageBRoot, "cdn-isolated", "100");
            if (!File.Exists(Path.Combine(cdn100, "GameLua.version")))
            {
                Assert.Ignore("cdn-isolated/100 missing");
                yield break;
            }

            string work = Path.Combine(StageBRoot, "cache-snap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var httpLog = new List<string>();
            using (var http = FileHttp.Start(cdn100, httpLog))
            {
                var service = new PatchService(
                    Path.Combine(work, "index"),
                    Path.Combine(work, "yoo"),
                    new[] { HotfixLoader.LuaPackageName });
                var manifest = new VersionManifest
                {
                    MinAppVersion = "0.2.0",
                    ResVersion = 100,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "100",
                    BaseUrl = http.BaseUrl.TrimEnd('/')
                };
                Exception err = null;
                yield return service.PrepareAsync(manifest, null, CancellationToken.None)
                    .ToCoroutine(e => err = e);
                Assert.That(err, Is.Null, err != null ? err.ToString() : "");
                // 修改公开视图必须不能改动服务内部的待提交快照。
                PatchService.CandidateState view = service.Candidate;
                view.Manifest.ConfigVersion = 888;
                view.PackageNames[0] = "changed-public-view";
                Assert.That(service.Candidate.Manifest.ConfigVersion, Is.EqualTo(100));
                Assert.That(service.Candidate.PackageNames[0], Is.EqualTo(HotfixLoader.LuaPackageName));
                manifest.ConfigVersion = 999;
                Assert.Throws<InvalidOperationException>(() => service.CommitVersion(manifest));
                Assert.That(service.HasActiveIndex, Is.False);
                manifest.ConfigVersion = 100;
                Assert.DoesNotThrow(() => service.CommitVersion(manifest));
            }

            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        [UnityTest]
        public IEnumerator Prepare_RejectsConcurrent_AndOwnershipUrlConflict()
        {
            string cdn100 = Path.Combine(StageBRoot, "cdn-isolated", "100");
            if (!File.Exists(Path.Combine(cdn100, "GameLua.version")))
            {
                Assert.Ignore("cdn-isolated/100 missing");
                yield break;
            }

            string work = Path.Combine(StageBRoot, "cache-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var httpLog = new List<string>();
            using (var http = FileHttp.Start(cdn100, httpLog))
            {
                var service = new PatchService(
                    Path.Combine(work, "index"),
                    Path.Combine(work, "yoo"),
                    new[] { HotfixLoader.LuaPackageName });
                var manifest = new VersionManifest
                {
                    MinAppVersion = "0.2.0",
                    ResVersion = 100,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "100",
                    BaseUrl = http.BaseUrl.TrimEnd('/')
                };

                Exception first = null;
                var running = service.PrepareAsync(manifest, null, CancellationToken.None)
                    .ToCoroutine(e => first = e);
                Exception concurrent = null;
                bool threw = false;
                try
                {
                    // 同步抢门闸：第二路应立即拒绝
                    service.PrepareAsync(manifest, null, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                catch (Exception ex)
                {
                    concurrent = ex;
                }

                yield return running;
                Assert.That(first, Is.Null, first != null ? first.ToString() : "");
                Assert.That(threw || concurrent is InvalidOperationException, Is.True);

                // 同包改 URL：所有权冲突
                var bad = new VersionManifest
                {
                    MinAppVersion = "0.2.0",
                    ResVersion = 100,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "100",
                    BaseUrl = "http://127.0.0.1:1"
                };
                Exception conflict = null;
                yield return service.PrepareAsync(bad, null, CancellationToken.None)
                    .ToCoroutine(e => conflict = e);
                Assert.That(conflict, Is.Not.Null);
                Assert.That(conflict.Message, Does.Contain("冲突").Or.Contain("conflict").IgnoreCase);
            }

            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        [UnityTest]
        public IEnumerator Prepare_PendingCancel_CallsCancelDownload_ThenRetryOk()
        {
            string cdn100 = Path.Combine(StageBRoot, "cdn-isolated", "100");
            if (!File.Exists(Path.Combine(cdn100, "GameLua.version")))
            {
                Assert.Ignore("cdn-isolated/100 missing");
                yield break;
            }

            string work = Path.Combine(StageBRoot, "cache-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var httpLog = new List<string>();
            using (var http = FileHttp.Start(cdn100, httpLog))
            {
                http.HoldBundleResponses = true;
                var progressLog = new List<PatchProgress>();
                var progress = new Progress<PatchProgress>(p => progressLog.Add(p));
                var service = new PatchService(
                    Path.Combine(work, "index"),
                    Path.Combine(work, "yoo"),
                    new[] { HotfixLoader.LuaPackageName });
                var manifest = new VersionManifest
                {
                    MinAppVersion = "0.2.0",
                    ResVersion = 100,
                    ConfigVersion = 100,
                    ProtoVersion = 1,
                    PackageVersion = "100",
                    BaseUrl = http.BaseUrl.TrimEnd('/')
                };

                var cts = new CancellationTokenSource();
                Exception cancelErr = null;
                var prepare = service.PrepareAsync(manifest, progress, cts.Token)
                    .ToCoroutine(e => cancelErr = e);

                float until = UnityEngine.Time.realtimeSinceStartup + 15f;
                while (UnityEngine.Time.realtimeSinceStartup < until)
                {
                    // 必须已有真正的 bundle 请求在等待 body；版本/清单请求不算下载中取消。
                    if (http.HeldBundleRequests > 0)
                    {
                        cts.Cancel();
                        break;
                    }

                    yield return null;
                }

                bool reachedBundle = http.HeldBundleRequests > 0;
                cts.Cancel();

                yield return prepare;
                Assert.That(reachedBundle, Is.True, "未到达真实 bundle body 等待，不能以预取消冒充");
                Assert.That(cancelErr, Is.InstanceOf<OperationCanceledException>());
                Assert.That(service.Candidate, Is.Null);
                Assert.That(service.IsPrepareInFlight, Is.False);
                Assert.That(service.HasActiveIndex, Is.False);

                // 在任何 DestroyPackage/服务端停机前，独立观察客户端确已断开被暂停的下载。
                float closeUntil = Time.realtimeSinceStartup + 3f;
                while (http.PeerClosedWhileHeld == 0 && Time.realtimeSinceStartup < closeUntil)
                    yield return null;
                Assert.That(http.PeerClosedWhileHeld, Is.GreaterThan(0), "取消等待后，底层HTTP仍未终止");

                // 同一个服务/包重试，不靠销毁包来掩盖残留下载。
                http.HoldBundleResponses = false;
                Exception retryErr = null;
                yield return service.PrepareAsync(manifest, null, CancellationToken.None)
                    .ToCoroutine(e => retryErr = e);
                Assert.That(retryErr, Is.Null, retryErr != null ? retryErr.ToString() : "");
                Assert.That(service.Candidate, Is.Not.Null);
                cts.Dispose();
            }

            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        /// <summary>TcpListener 静态文件服务（避免 batchmode HttpListener 卡死）。</summary>
        sealed class FileHttp : IDisposable
        {
            readonly TcpListener _listener;
            readonly List<string> _log;
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly Task _loop;
            string _root;
            int _responseDelayMs;
            volatile bool _holdBundleResponses;
            int _heldBundleRequests;
            int _peerClosedWhileHeld;

            /// <summary>只暂停真实bundle body；版本和清单正常返回，确保测到下载阶段。</summary>
            public bool HoldBundleResponses { get => _holdBundleResponses; set => _holdBundleResponses = value; }
            public int HeldBundleRequests => Volatile.Read(ref _heldBundleRequests);
            public int PeerClosedWhileHeld => Volatile.Read(ref _peerClosedWhileHeld);

            FileHttp(TcpListener listener, string root, List<string> log, int responseDelayMs)
            {
                _listener = listener;
                _root = root;
                _log = log;
                _responseDelayMs = responseDelayMs;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" + Port + "/";
                _loop = Task.Run(() => Loop(_cts.Token));
            }

            public int Port { get; }
            public string BaseUrl { get; }

            public static FileHttp Start(string root, List<string> log, int responseDelayMs = 0)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new FileHttp(listener, root, log, responseDelayMs);
            }

            public void SwapRoot(string root) => _root = root;

            public void SetDelayMs(int ms) => _responseDelayMs = ms < 0 ? 0 : ms;

            void Loop(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        using (client)
                        using (NetworkStream stream = client.GetStream())
                        {
                            client.ReceiveTimeout = 10000;
                            client.SendTimeout = 10000;
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
                            _log.Add(rel);
                            if (_holdBundleResponses && rel.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                            {
                                Interlocked.Increment(ref _heldBundleRequests);
                                bool peerClosed = false;
                                while (_holdBundleResponses && !ct.IsCancellationRequested)
                                {
                                    // 无后续请求数据且socket可读=客户端FIN/RST；与服务自身计数独立。
                                    if (client.Client.Poll(1000, SelectMode.SelectRead) && client.Available == 0)
                                    {
                                        Interlocked.Increment(ref _peerClosedWhileHeld);
                                        peerClosed = true;
                                        break;
                                    }
                                    Thread.Sleep(5);
                                }
                                if (peerClosed || ct.IsCancellationRequested)
                                    continue;
                            }
                            int delay = _responseDelayMs;
                            if (delay > 0)
                                Thread.Sleep(delay);
                            string filePath = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
                            string rootFull = Path.GetFullPath(_root);
                            string rootSep = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                                ? rootFull
                                : rootFull + Path.DirectorySeparatorChar;
                            byte[] body = Array.Empty<byte>();
                            int status = 404;
                            if (filePath.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                            {
                                body = File.ReadAllBytes(filePath);
                                status = 200;
                            }

                            string reason = status == 200 ? "OK" : "Not Found";
                            string header =
                                "HTTP/1.0 " + status + " " + reason + "\r\n" +
                                "Content-Length: " + body.Length + "\r\n" +
                                "Connection: close\r\n\r\n";
                            byte[] head = Encoding.ASCII.GetBytes(header);
                            stream.Write(head, 0, head.Length);
                            if (body.Length > 0)
                                stream.Write(body, 0, body.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("FileHttp: " + ex.Message);
                    }
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                try { _loop.Wait(2000); } catch { }
                _cts.Dispose();
            }
        }
    }
}
