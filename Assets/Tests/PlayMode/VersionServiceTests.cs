using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// VersionService：数值比较 + 真实 loopback HTTP 成功/失败/取消。
    /// HTTP 夹具用 TcpListener 手写最小响应，避免 HttpListener 在 batchmode 中卡死。
    /// </summary>
    public sealed class VersionServiceTests
    {
        [Test]
        public void RequiresAppUpdate_NumericSemantics()
        {
            var service = new VersionService();
            Assert.That(service.RequiresAppUpdate("0.1.9", "0.2.0"), Is.True);
            Assert.That(service.RequiresAppUpdate("0.2.0", "0.2.0"), Is.False);
            Assert.That(service.RequiresAppUpdate("0.2.1", "0.2.0"), Is.False);
            Assert.That(service.RequiresAppUpdate("0.1.10", "0.1.9"), Is.False);
            Assert.Throws<VersionManifestException>(() => service.RequiresAppUpdate("1.x", "0.2.0"));
        }

        [UnityTest]
        public IEnumerator FetchAsync_LocalHttp_Success()
        {
            string json = ValidJson();
            using (var http = MiniHttp.Start(new MiniHttp.Route("/", 404, ""), new MiniHttp.Route("/version.json", 200, json)))
            {
                var service = new VersionService();
                VersionManifest manifest = null;
                Exception error = null;
                yield return service.FetchAsync(new Uri(http.BaseUrl + "version.json"), CancellationToken.None)
                    .ToCoroutine(r => manifest = r, e => error = e);
                Assert.That(error, Is.Null, error != null ? error.ToString() : "");
                Assert.That(manifest, Is.Not.Null);
                Assert.That(manifest.MinAppVersion, Is.EqualTo("0.2.0"));
                Assert.That(manifest.ResVersion, Is.EqualTo(100));
                Assert.That(manifest.ProtoVersion, Is.EqualTo(1));
                Assert.That(manifest.ConfigVersion, Is.EqualTo(100));
                Assert.That(manifest.PackageVersion, Is.EqualTo("2"));
            }
        }

        [UnityTest]
        public IEnumerator FetchAsync_NotFound_Fails()
        {
            using (var http = MiniHttp.Start(new MiniHttp.Route("/version.json", 404, "")))
            {
                var service = new VersionService();
                Exception error = null;
                yield return service.FetchAsync(new Uri(http.BaseUrl + "version.json"), CancellationToken.None)
                    .ToCoroutine(_ => { }, e => error = e);
                Assert.That(error, Is.InstanceOf<VersionManifestException>());
            }
        }

        [UnityTest]
        public IEnumerator FetchAsync_BadJson_Fails()
        {
            using (var http = MiniHttp.Start(new MiniHttp.Route("/version.json", 200, "{")))
            {
                var service = new VersionService();
                Exception error = null;
                yield return service.FetchAsync(new Uri(http.BaseUrl + "version.json"), CancellationToken.None)
                    .ToCoroutine(_ => { }, e => error = e);
                Assert.That(error, Is.InstanceOf<VersionManifestException>());
            }
        }

        [UnityTest]
        public IEnumerator FetchAsync_Cancel_Throws()
        {
            string json = ValidJson();
            using (var http = MiniHttp.Start(new MiniHttp.Route("/version.json", 200, json)))
            {
                var service = new VersionService();
                var cts = new CancellationTokenSource();
                cts.Cancel();
                Exception error = null;
                yield return service.FetchAsync(new Uri(http.BaseUrl + "version.json"), cts.Token)
                    .ToCoroutine(_ => { }, e => error = e);
                Assert.That(error, Is.InstanceOf<OperationCanceledException>());
            }
        }

        [UnityTest]
        public IEnumerator FetchAsync_IllegalUri_Fails()
        {
            var service = new VersionService();
            Exception error = null;
            yield return service.FetchAsync(new Uri("ftp://127.0.0.1/version.json"), CancellationToken.None)
                .ToCoroutine(_ => { }, e => error = e);
            Assert.That(error, Is.InstanceOf<VersionManifestException>());
        }

        /// <summary>请求已发出且服务端迟迟不响应时，TimeoutSeconds 结束并抛 VersionManifestException。</summary>
        [UnityTest]
        public IEnumerator FetchAsync_Timeout_WhilePending_Fails()
        {
            string json = ValidJson();
            using (var http = MiniHttp.Start(new MiniHttp.Route("/version.json", 200, json, delayMs: 5000)))
            {
                var service = new VersionService { TimeoutSeconds = 1 };
                Exception error = null;
                yield return service.FetchAsync(new Uri(http.BaseUrl + "version.json"), CancellationToken.None)
                    .ToCoroutine(_ => { }, e => error = e);
                Assert.That(error, Is.InstanceOf<VersionManifestException>());
                Assert.That(error.Message, Does.Contain("超时").Or.Contain("失败").Or.Contain("timeout").IgnoreCase);
            }
        }

        /// <summary>请求已发出后主动取消；随后同一服务再 Fetch 可成功。</summary>
        [UnityTest]
        public IEnumerator FetchAsync_CancelWhilePending_ThenNextSucceeds()
        {
            string json = ValidJson();
            using (var http = MiniHttp.Start(new MiniHttp.Route("/version.json", 200, json, delayMs: 3000)))
            {
                var service = new VersionService { TimeoutSeconds = 30 };
                var cts = new CancellationTokenSource();
                Exception first = null;
                var task = service.FetchAsync(new Uri(http.BaseUrl + "version.json"), cts.Token);
                yield return null;
                yield return null;
                cts.Cancel();
                yield return task.ToCoroutine(_ => { }, e => first = e);
                Assert.That(first, Is.InstanceOf<OperationCanceledException>());
            }

            using (var http2 = MiniHttp.Start(new MiniHttp.Route("/version.json", 200, json)))
            {
                var service2 = new VersionService { TimeoutSeconds = 10 };
                VersionManifest manifest = null;
                Exception second = null;
                yield return service2.FetchAsync(new Uri(http2.BaseUrl + "version.json"), CancellationToken.None)
                    .ToCoroutine(r => manifest = r, e => second = e);
                Assert.That(second, Is.Null, second != null ? second.ToString() : "");
                Assert.That(manifest, Is.Not.Null);
                Assert.That(manifest.ResVersion, Is.EqualTo(100));
            }
        }

        static string ValidJson()
        {
            return
@"{
  ""MinAppVersion"": ""0.2.0"",
  ""ResVersion"": 100,
  ""ProtoVersion"": 1,
  ""ConfigVersion"": 100,
  ""PackageVersion"": ""2"",
  ""BaseUrl"": ""http://127.0.0.1:18080/100/""
}";
        }

        /// <summary>最小 HTTP/1.0 响应服务器。后台线程 Accept，不依赖 HttpListener。</summary>
        sealed class MiniHttp : IDisposable
        {
            public readonly struct Route
            {
                public readonly string Path;
                public readonly int Status;
                public readonly string Body;
                public readonly int DelayMs;

                public Route(string path, int status, string body, int delayMs = 0)
                {
                    Path = path;
                    Status = status;
                    Body = body ?? "";
                    DelayMs = delayMs;
                }
            }

            readonly TcpListener _listener;
            readonly Route[] _routes;
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly Task _loop;

            MiniHttp(TcpListener listener, Route[] routes)
            {
                _listener = listener;
                _routes = routes;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" + Port + "/";
                _loop = Task.Run(() => Loop(_cts.Token));
            }

            public int Port { get; }
            public string BaseUrl { get; }

            public static MiniHttp Start(params Route[] routes)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new MiniHttp(listener, routes);
            }

            void Loop(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    try
                    {
                        using (client)
                        using (NetworkStream stream = client.GetStream())
                        {
                            client.ReceiveTimeout = 2000;
                            client.SendTimeout = 2000;
                            var buffer = new byte[4096];
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

                            Route match = default;
                            bool found = false;
                            for (int i = 0; i < _routes.Length; i++)
                            {
                                if (string.Equals(_routes[i].Path, path, StringComparison.Ordinal))
                                {
                                    match = _routes[i];
                                    found = true;
                                    break;
                                }
                            }

                            int status = found ? match.Status : 404;
                            if (found && match.DelayMs > 0)
                            {
                                try
                                {
                                    Task.Delay(match.DelayMs, ct).Wait(ct);
                                }
                                catch (OperationCanceledException)
                                {
                                    continue;
                                }
                            }

                            byte[] body = Encoding.UTF8.GetBytes(found ? match.Body : "");
                            string reason = status == 200 ? "OK" : "Not Found";
                            string header =
                                "HTTP/1.0 " + status + " " + reason + "\r\n" +
                                "Content-Length: " + body.Length + "\r\n" +
                                "Content-Type: application/json\r\n" +
                                "Connection: close\r\n\r\n";
                            byte[] head = Encoding.ASCII.GetBytes(header);
                            stream.Write(head, 0, head.Length);
                            if (body.Length > 0)
                                stream.Write(body, 0, body.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("MiniHttp serve: " + ex.Message);
                    }
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); }
                catch { }
                try { _loop.Wait(1000); }
                catch { }
                _cts.Dispose();
            }
        }
    }
}
