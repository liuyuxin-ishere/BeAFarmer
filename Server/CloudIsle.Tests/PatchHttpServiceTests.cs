using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Contracts;
using CloudIsle.Server.Hosting;

namespace CloudIsle.Tests
{
    /// <summary>真实 HttpListener 集成：200/404/穿越/取消/端口占用。</summary>
    public sealed class PatchHttpServiceTests
    {
        /// <summary>GET version.json 与样例文件返回原文；解析字段齐全。</summary>
        [Test]
        public async Task RunAsync_ServesVersionAndSample_200()
        {
            using (var workspace = new HttpWorkspace())
            {
                int port = workspace.Port;
                string baseUrl = "http://127.0.0.1:" + port + "/";
                using (var cts = new CancellationTokenSource())
                {
                    Task server = Task.Run(
                        () => new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts.Token),
                        CancellationToken.None);

                    await WaitListeningAsync(port).ConfigureAwait(false);

                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        string versionBody = await client.GetStringAsync(baseUrl + "version.json")
                            .ConfigureAwait(false);
                        Assert.That(versionBody, Does.Contain("\"MinAppVersion\""));
                        VersionManifest manifest = VersionManifestParser.Parse(
                            Encoding.UTF8.GetBytes(versionBody));
                        Assert.That(manifest.MinAppVersion, Is.EqualTo("0.2.0"));
                        Assert.That(manifest.ResVersion, Is.EqualTo(100));
                        Assert.That(manifest.ProtoVersion, Is.EqualTo(1));
                        Assert.That(manifest.ConfigVersion, Is.EqualTo(100));
                        Assert.That(manifest.PackageVersion, Is.EqualTo("2"));

                        string sample = await client.GetStringAsync(baseUrl + "100/sample.txt")
                            .ConfigureAwait(false);
                        Assert.That(sample.Trim(), Is.EqualTo("m1-patch-sample-100"));
                    }

                    cts.Cancel();
                    try
                    {
                        await server.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }

        /// <summary>缺失文件与目录路径返回 404，且响应体长度为 0。</summary>
        [Test]
        public async Task RunAsync_MissingOrDirectory_Returns404Empty()
        {
            using (var workspace = new HttpWorkspace())
            {
                int port = workspace.Port;
                string baseUrl = "http://127.0.0.1:" + port + "/";
                using (var cts = new CancellationTokenSource())
                {
                    Task server = Task.Run(
                        () => new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts.Token),
                        CancellationToken.None);
                    await WaitListeningAsync(port).ConfigureAwait(false);

                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        HttpResponseMessage missing = await client.GetAsync(baseUrl + "no-such.bin")
                            .ConfigureAwait(false);
                        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                        Assert.That((await missing.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length,
                            Is.EqualTo(0));

                        HttpResponseMessage dir = await client.GetAsync(baseUrl + "100/")
                            .ConfigureAwait(false);
                        Assert.That(dir.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                        Assert.That((await dir.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length,
                            Is.EqualTo(0));
                    }

                    cts.Cancel();
                    try { await server.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        /// <summary>
        /// 非法路径（含 %00）返回明确 4xx 并关闭响应；随后合法请求仍可服务（无悬挂）。
        /// </summary>
        [Test]
        public async Task RunAsync_NullBytePath_Returns4xx_ThenNextRequestOk()
        {
            using (var workspace = new HttpWorkspace())
            {
                int port = workspace.Port;
                string baseUrl = "http://127.0.0.1:" + port + "/";
                using (var cts = new CancellationTokenSource())
                {
                    Task server = Task.Run(
                        () => new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts.Token),
                        CancellationToken.None);
                    await WaitListeningAsync(port).ConfigureAwait(false);

                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        // 含 %00 的路径在 Uri 层可能被拒绝；同时覆盖服务端 Path.GetFullPath 防护。
                        HttpResponseMessage bad;
                        try
                        {
                            bad = await client.GetAsync(baseUrl + "100/sample%00.txt").ConfigureAwait(false);
                        }
                        catch (HttpRequestException)
                        {
                            // 部分运行时在客户端拒绝 null 字节 URI；仍用穿越路径触发服务端 4xx 分支。
                            bad = await client.GetAsync(baseUrl + "100/%00sample.txt").ConfigureAwait(false);
                        }

                        Assert.That((int)bad.StatusCode, Is.GreaterThanOrEqualTo(400));
                        Assert.That((int)bad.StatusCode, Is.LessThan(500));

                        string sample = await client.GetStringAsync(baseUrl + "100/sample.txt")
                            .ConfigureAwait(false);
                        Assert.That(sample.Trim(), Is.EqualTo("m1-patch-sample-100"));
                    }

                    cts.Cancel();
                    try { await server.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        /// <summary>路径穿越与编码穿越不能读到根外文件。</summary>
        [Test]
        public async Task RunAsync_PathTraversal_Rejected()
        {
            using (var workspace = new HttpWorkspace())
            {
                string secret = Path.Combine(Path.GetDirectoryName(workspace.Root)!, "secret-outside.txt");
                File.WriteAllText(secret, "LEAK", Encoding.UTF8);
                try
                {
                    int port = workspace.Port;
                    string baseUrl = "http://127.0.0.1:" + port + "/";
                    using (var cts = new CancellationTokenSource())
                    {
                        Task server = Task.Run(
                            () => new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts.Token),
                            CancellationToken.None);
                        await WaitListeningAsync(port).ConfigureAwait(false);

                        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                        {
                            HttpResponseMessage slashDot = await client
                                .GetAsync(baseUrl + "../secret-outside.txt").ConfigureAwait(false);
                            Assert.That(slashDot.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

                            HttpResponseMessage encoded = await client
                                .GetAsync(baseUrl + "%2e%2e/secret-outside.txt").ConfigureAwait(false);
                            Assert.That(encoded.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                        }

                        cts.Cancel();
                        try { await server.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
                finally
                {
                    if (File.Exists(secret))
                        File.Delete(secret);
                }
            }
        }

        /// <summary>取消后 RunAsync 结束；同端口可再次绑定。</summary>
        [Test]
        public async Task RunAsync_Cancel_StopsAndReleasesPort()
        {
            using (var workspace = new HttpWorkspace())
            {
                int port = workspace.Port;
                string baseUrl = "http://127.0.0.1:" + port + "/";
                using (var cts = new CancellationTokenSource())
                {
                    Task server = Task.Run(
                        () => new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts.Token),
                        CancellationToken.None);
                    await WaitListeningAsync(port).ConfigureAwait(false);
                    cts.Cancel();
                    try { await server.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    Task again = new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts2.Token);
                    await WaitListeningAsync(port).ConfigureAwait(false);
                    cts2.Cancel();
                    try { await again.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        /// <summary>端口已被占用时启动失败，不静默成功。</summary>
        [Test]
        public async Task RunAsync_PortInUse_Fails()
        {
            using (var workspace = new HttpWorkspace())
            {
                int port = workspace.Port;
                string baseUrl = "http://127.0.0.1:" + port + "/";
                var blocker = new HttpListener();
                blocker.Prefixes.Add(baseUrl);
                blocker.Start();
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    {
                        InvalidOperationException? error = null;
                        try
                        {
                            await new PatchHttpService().RunAsync(baseUrl, workspace.Root, cts.Token)
                                .ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            error = ex;
                        }

                        Assert.That(error, Is.Not.Null);
                        Assert.That(error!.Message, Does.Contain("无法绑定补丁 HTTP 端口"));
                    }
                }
                finally
                {
                    blocker.Stop();
                    blocker.Close();
                }
            }
        }

        static async Task WaitListeningAsync(int port)
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                        return;
                    }
                }
                catch (SocketException)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            throw new TimeoutException("HTTP 端口未在时限内开始监听: " + port);
        }

        sealed class HttpWorkspace : IDisposable
        {
            readonly TcpListener _reserve;

            public HttpWorkspace()
            {
                _reserve = new TcpListener(IPAddress.Loopback, 0);
                _reserve.Start();
                Port = ((IPEndPoint)_reserve.LocalEndpoint).Port;
                _reserve.Stop();

                Root = Path.Combine(FindRepo(), "Temp", "t43-http-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(Root, "100"));
                string official = Path.Combine(FindRepo(), "Server", "PatchContent", "M1");
                File.Copy(
                    Path.Combine(official, "version.json"),
                    Path.Combine(Root, "version.json"));
                // BaseUrl 仍指向约定 18080；本测试只校验字段存在与样例文件内容。
                File.WriteAllText(
                    Path.Combine(Root, "100", "sample.txt"),
                    "m1-patch-sample-100\n",
                    new UTF8Encoding(false));
            }

            public int Port { get; }
            public string Root { get; }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }

            static string FindRepo()
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Server", "CloudIsle.sln")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Assets")))
                        return dir.FullName;
                    dir = dir.Parent;
                }

                throw new InvalidOperationException("找不到工程根目录");
            }
        }
    }
}
