using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Hosting;
using Google.Protobuf;

namespace CloudIsle.Tests;

/// <summary>生产组合根经真实TCP/HTTP/SQLite验证；不替换账号处理器，不记录密码或Token。</summary>
public sealed class M1ServerHostTests
{
    /// <summary>两个监听就绪后注册/登录/心跳/登出；登出只有关闭，没有伪造响应。</summary>
    [Test]
    public async Task FormalHost_HttpAndAccountAndHeartbeatAndLogout()
    {
        await using var world = new World();
        await world.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.That(await http.GetStringAsync(world.Prefix + "version.json"), Does.Contain("0.3.0"));
        using var missing = await http.GetAsync(world.Prefix + "missing.bin");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using TcpClient client = await world.ConnectAsync();
        await RegisterAsync(client, 1);
        LoginResp login = await LoginAsync(client, 2);
        Assert.That(login.Code, Is.Zero);
        Assert.That(login.Data.Info.Nickname, Is.EqualTo("Host验证"));
        Assert.That(login.Token.Length, Is.EqualTo(64));
        Assert.That(world.Host!.AuthenticatedPlayerCount, Is.EqualTo(1));
        await SendAsync(client, new HeartbeatReq { ClientTime = 123 }, 3);
        Packet heartbeat = await ReadAsync(client);
        Assert.That(heartbeat.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(HeartbeatResp))));
        Assert.That(heartbeat.Seq, Is.EqualTo(3u));
        Assert.That(HeartbeatResp.Parser.ParseFrom(heartbeat.Body).ServerTime, Is.GreaterThan(123));
        await SendAsync(client, new LogoutReq(), 4);
        await AssertClosedAsync(client);
        await world.StopAsync();
        Assert.That(world.Host.AuthenticatedPlayerCount, Is.Zero);
        Assert.That(world.Host.ConnectionCount, Is.Zero);
        string logs = string.Join('\n', Directory.GetFiles(world.Logs).Select(File.ReadAllText));
        Assert.That(logs.Contains(login.Token), Is.False, "Token不可进入日志");
        Assert.That(logs.Contains(World.Password), Is.False, "密码不可进入日志");
        // 同一端口/文件能立刻被新生产实例使用，证明正常停机释放资源。
        await world.StartAsync();
        await world.StopAsync();
    }

    /// <summary>不同账号同时在线；一个对端RST退出不影响另一个玩家、HTTP和后续登录。</summary>
    [Test]
    public async Task OnePeerAbruptExit_KeepsOtherAuthenticatedPeerAndListenersAlive()
    {
        await using var world = new World();
        await world.StartAsync();
        using TcpClient survivor = await world.ConnectAsync();
        await RegisterAsync(survivor, 1, "surviving-account");
        Assert.That((await LoginAsync(survivor, 2, "surviving-account")).Code, Is.Zero);
        using (TcpClient exiting = await world.ConnectAsync())
        {
            await RegisterAsync(exiting, 1);
            Assert.That((await LoginAsync(exiting, 2)).Code, Is.Zero);
            Assert.That(world.Host!.AuthenticatedPlayerCount, Is.EqualTo(2));
            exiting.Client.LingerState = new LingerOption(true, 0); // Real abrupt TCP reset, no LogoutReq.
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (world.Host!.ConnectionCount != 1 || world.Host.AuthenticatedPlayerCount != 1)
            await Task.Delay(10, timeout.Token);
        Assert.That(world.IsRunning, Is.True);
        await SendAsync(survivor, new HeartbeatReq { ClientTime = 456 }, 3);
        Packet reply = await ReadAsync(survivor);
        Assert.That(reply.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(HeartbeatResp))));
        Assert.That(reply.Seq, Is.EqualTo(3u));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.That(await http.GetStringAsync(world.Prefix + "version.json"), Does.Contain("0.3.0"));
        using TcpClient newcomer = await world.ConnectAsync();
        Assert.That((await LoginAsync(newcomer, 1)).Code, Is.Zero);
        Assert.That(world.Host.AuthenticatedPlayerCount, Is.EqualTo(2));
        Assert.That(world.IsRunning, Is.True);
        await world.StopAsync();
    }

    /// <summary>已认证对端自然退出后Host保持运行，连续12轮新连接均能登录。</summary>
    [Test]
    public async Task AuthenticatedPeerExit_KeepsHostListeningAndAcceptsNextLogin()
    {
        await using var world = new World();
        await world.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        for (int i = 0; i < 12; i++)
        {
            using (TcpClient client = await world.ConnectAsync())
            {
                if (i == 0) await RegisterAsync(client, 1);
                Assert.That((await LoginAsync(client, 2)).Code, Is.Zero);
                // No LogoutReq: an actual authenticated peer closes naturally.
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (world.Host!.ConnectionCount != 0 || world.Host.AuthenticatedPlayerCount != 0)
                await Task.Delay(10, timeout.Token);
            Assert.That(await http.GetStringAsync(world.Prefix + "version.json"), Does.Contain("0.3.0"));
        }
        await world.StopAsync();
    }

    /// <summary>停机保留原DB与未撤销Token；新实例真实重连恢复快照，登出后Token失效。</summary>
    [Test]
    public async Task RestartSameDatabase_ReconnectsThenLogoutRevokesToken()
    {
        await using var world = new World();
        await world.StartAsync();
        using TcpClient first = await world.ConnectAsync();
        await RegisterAsync(first, 1);
        LoginResp login = await LoginAsync(first, 2);
        long playerId = login.Data.Info.PlayerId;
        await world.StopAsync();
        await world.StartAsync();
        using TcpClient resumed = await world.ConnectAsync();
        await SendAsync(resumed, new ReconnectReq { Token = login.Token, ProtoVersion = 1 }, 5);
        Packet frame = await ReadAsync(resumed);
        Assert.That(frame.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(ReconnectResp))));
        ReconnectResp response = ReconnectResp.Parser.ParseFrom(frame.Body);
        Assert.That(response.Code, Is.Zero);
        Assert.That(response.Data.Info.PlayerId, Is.EqualTo(playerId));
        Assert.That(response.Data, Is.EqualTo(login.Data));
        await SendAsync(resumed, new LogoutReq(), 6);
        await AssertClosedAsync(resumed);
        using TcpClient rejected = await world.ConnectAsync();
        await SendAsync(rejected, new ReconnectReq { Token = login.Token, ProtoVersion = 1 }, 7);
        Assert.That(ReconnectResp.Parser.ParseFrom((await ReadAsync(rejected)).Body).Code, Is.EqualTo(204));
    }

    /// <summary>生产Host发真实踢线且撤销旧Token，协议拒绝不抢占当前登录。</summary>
    [Test]
    public async Task ReplacementLogin_KicksOldAndRejectsStaleTokenAndProto()
    {
        await using var world = new World();
        await world.StartAsync();
        using TcpClient old = await world.ConnectAsync();
        await RegisterAsync(old, 1);
        LoginResp first = await LoginAsync(old, 2);
        using TcpClient current = await world.ConnectAsync();
        LoginResp second = await LoginAsync(current, 3);
        Packet kicked = await ReadAsync(old);
        Assert.That(kicked.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(KickNtf))));
        Assert.That(kicked.Seq, Is.Zero);
        Assert.That(KickNtf.Parser.ParseFrom(kicked.Body).Reason, Is.EqualTo(KickReason.KickElsewhere));
        await AssertClosedAsync(old);
        Assert.That(second.Code, Is.Zero);
        using TcpClient stale = await world.ConnectAsync();
        await SendAsync(stale, new ReconnectReq { Token = first.Token, ProtoVersion = 1 }, 4);
        Assert.That(ReconnectResp.Parser.ParseFrom((await ReadAsync(stale)).Body).Code, Is.EqualTo(204));
        await SendAsync(stale, new ReconnectReq { Token = second.Token, ProtoVersion = 2 }, 5);
        Assert.That(ReconnectResp.Parser.ParseFrom((await ReadAsync(stale)).Body).Code, Is.EqualTo(106));
        Assert.That(world.Host!.AuthenticatedPlayerCount, Is.EqualTo(1));
    }

    /// <summary>TCP端口占用使启动异常并关闭先启动的HTTP；释放后原前缀可以重新启动。</summary>
    [Test]
    public async Task OccupiedTcp_FailsStartedAndRunAndReleasesHttp()
    {
        await using var world = new World();
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            world.TcpPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            Assert.ThrowsAsync<SocketException>(async () => await world.StartAsync());
        }
        finally { occupied.Stop(); }
        await world.StartAsync();
        await world.StopAsync();
    }

    /// <summary>配置版本错误在监听前被拒绝，修正后相同地址仍可成功启动。</summary>
    [Test]
    public async Task WrongConfigVersion_FailsBeforeListeningAndCanRetryNewHost()
    {
        await using var world = new World();
        Assert.CatchAsync<Exception>(async () => await world.StartAsync(999));
        await world.StartAsync();
        await world.StopAsync();
    }

    /// <summary>测试注册正文，生产RegisterResp只含结果码。</summary>
    static async Task RegisterAsync(TcpClient client, uint seq, string username = "host-account")
    {
        await SendAsync(client, new RegisterReq
        {
            Username = username, Password = World.Password,
            Nickname = "Host验证", ProtoVersion = 1, DeviceId = "host-test"
        }, seq);
        Packet frame = await ReadAsync(client);
        Assert.That(frame.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(RegisterResp))));
        Assert.That(frame.Seq, Is.EqualTo(seq));
        Assert.That(RegisterResp.Parser.ParseFrom(frame.Body).Code, Is.Zero);
    }

    /// <summary>只通过真实Socket进行正式1005登录，不提供测试捷径。</summary>
    static async Task<LoginResp> LoginAsync(TcpClient client, uint seq, string username = "host-account")
    {
        await SendAsync(client, new LoginReq
        {
            Username = username, Password = World.Password, AppVersion = "0.3.0",
            ProtoVersion = 1, ResVersion = 100, ConfigVersion = 100, DeviceId = "host-test"
        }, seq);
        Packet frame = await ReadAsync(client);
        Assert.That(frame.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(LoginResp))));
        Assert.That(frame.Seq, Is.EqualTo(seq));
        return LoginResp.Parser.ParseFrom(frame.Body);
    }

    /// <summary>正式帧编码，seq由测试给定，5秒内必须发送完成。</summary>
    static async Task SendAsync(TcpClient client, IMessage message, uint seq)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.GetStream().WriteAsync(FrameCodec.Encode(
            MsgRegistry.GetId(message.GetType()), seq, message.ToByteArray()), timeout.Token);
    }

    /// <summary>真实网络帧完整读取，长度/消费量均核对；不向断言打印敏感正文。</summary>
    static async Task<Packet> ReadAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] prefix = new byte[4];
        await client.GetStream().ReadExactlyAsync(prefix, timeout.Token);
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix));
        Assert.That(length, Is.InRange(FrameCodec.HeaderSize, FrameCodec.MaxPayloadLength));
        byte[] bytes = new byte[4 + length];
        prefix.CopyTo(bytes, 0);
        await client.GetStream().ReadExactlyAsync(bytes.AsMemory(4), timeout.Token);
        Assert.That(FrameCodec.TryDecode(bytes, out Packet packet, out int consumed), Is.EqualTo(DecodeStatus.Success));
        Assert.That(consumed, Is.EqualTo(bytes.Length));
        return packet;
    }

    /// <summary>预期登出/踢线只结束连接；若有任何响应字节则失败。</summary>
    static async Task AssertClosedAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { Assert.That(await client.GetStream().ReadAsync(new byte[1], timeout.Token), Is.Zero); }
        catch (Exception ex) when (ex is IOException or SocketException) { }
    }

    /// <summary>每测试独占目录、DB与日志，重启保留该DB；不触及正式/旧PoC数据。</summary>
    sealed class World : IAsyncDisposable
    {
        internal const string Password = "host-test-password";
        readonly string _root;
        readonly string _config;
        CancellationTokenSource? _stop;
        Task? _run;
        internal bool IsRunning => _run != null && !_run.IsCompleted;
        internal M1ServerHost? Host { get; private set; }
        internal int TcpPort { get; set; }
        internal string Prefix { get; }
        internal string Logs => Path.Combine(_root, "logs");

        internal World()
        {
            DirectoryInfo? repo = new(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "Server", "ConfigLuban", "config_manifest.json")))
                repo = repo.Parent;
            if (repo == null) throw new DirectoryNotFoundException("找不到正式配置源");
            _config = Path.Combine(repo.FullName, "Server", "ConfigLuban");
            _root = Path.Combine(repo.FullName, "Temp", "m1-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "content"));
            File.WriteAllText(Path.Combine(_root, "content", "version.json"), "{\"MinAppVersion\":\"0.3.0\"}");
            Prefix = $"http://localhost:{FreePort()}/";
        }

        internal async Task StartAsync(int configVersion = 100)
        {
            _stop = new CancellationTokenSource();
            Host = new M1ServerHost(new M1ServerOptions
            {
                DatabasePath = Path.Combine(_root, "game.db"), ConfigDirectory = _config,
                ConfigVersion = configVersion, ContentRoot = Path.Combine(_root, "content"),
                HttpPrefix = Prefix, TcpEndpoint = new IPEndPoint(IPAddress.Loopback, TcpPort),
                LogDirectory = Logs
            });
            _run = Host.RunAsync(_stop.Token);
            try
            {
                await Host.Started.WaitAsync(TimeSpan.FromSeconds(10));
                TcpPort = Host.TcpEndpoint!.Port;
            }
            catch { await StopAsync(); throw; }
        }

        internal async Task<TcpClient> ConnectAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, TcpPort).WaitAsync(TimeSpan.FromSeconds(5));
            return client;
        }

        internal async Task StopAsync()
        {
            Task? run = _run;
            _run = null;
            _stop?.Cancel();
            try { if (run != null) await run.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { _stop?.Dispose(); _stop = null; }
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
    }
}
