using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using CloudIsle.Net;
using CloudIsle.Server.Account;
using CloudIsle.Server.Config;
using CloudIsle.Server.Data;
using CloudIsle.Server.Diagnostics;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Hosting;

/// <summary>正式M1组合根的显式选项；不读取旧PoC数据库或使用旧PoC消息。</summary>
public sealed class M1ServerOptions
{
    /// <summary>本次实例的SQLite文件。目录由已有SqliteStore创建。</summary>
    public string DatabasePath { get; init; } = "";
    /// <summary>包含正式config_manifest.json的目录。</summary>
    public string ConfigDirectory { get; init; } = "";
    /// <summary>启动必须精确加载的配置版本。</summary>
    public int ConfigVersion { get; init; } = 100;
    /// <summary>静态HTTP内容根，包含version.json及版本目录。</summary>
    public string ContentRoot { get; init; } = "";
    /// <summary>HTTP监听前缀；本机Native客户端使用localhost避开127代理问题。</summary>
    public string HttpPrefix { get; init; } = "http://localhost:18080/";
    /// <summary>TCP绑定地址。端口0仅供隔离集成测试。</summary>
    public IPEndPoint TcpEndpoint { get; init; } = new(IPAddress.Loopback, 17780);
    /// <summary>本实例拥有的日志目录，不使用全局Serilog静态实例。</summary>
    public string LogDirectory { get; init; } = "";
    /// <summary>正式登录协议必须与此值相等。</summary>
    public int ProtoVersion { get; init; } = 1;
    /// <summary>允许登录的最低安装包版本。</summary>
    public string MinAppVersion { get; init; } = "0.3.0";
    /// <summary>登录最低资源版本；较新的兼容客户端允许通过。</summary>
    public int RequiredResVersion { get; init; } = 100;
    /// <summary>登录最低配置版本；与启动数据的精确ConfigVersion区分。</summary>
    public int RequiredConfigVersion { get; init; } = 100;
    /// <summary>令牌有效期，默认24小时；原文不入日志或数据库。</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(24);
    /// <summary>正常停机保存预算，耗尽会明确失败而不是假称已保存。</summary>
    public TimeSpan ShutdownSaveBudget { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// 复用正式TCP/账号/会话/令牌/保存/HTTP服务的组合根。每个实例只能Run一次。
/// 生产CLI由后续Program/T60脚本调用本类；集成测试也经同一真实Socket入口。
/// </summary>
public sealed class M1ServerHost
{
    readonly M1ServerOptions _options;
    readonly ConcurrentDictionary<long, ClientSession> _connections = new();
    readonly ConcurrentDictionary<long, Task> _connectionWatches = new();
    readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Action<long, string, string>? _connectionFaultLog;
    TcpServer? _tcp;
    SessionRegistry? _sessions;
    MessageRouter? _router;
    HeartbeatHandler? _heartbeat;
    int _runOnce;

    /// <summary>复制启动选项；路径只在Run时验证，便于CLI报告具体配置错误。</summary>
    /// <param name="options">DB/配置/监听/版本/日志的明确配置。</param>
    public M1ServerHost(M1ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.TcpEndpoint);
        _options = new M1ServerOptions
        {
            DatabasePath = options.DatabasePath, ConfigDirectory = options.ConfigDirectory,
            ConfigVersion = options.ConfigVersion, ContentRoot = options.ContentRoot,
            HttpPrefix = options.HttpPrefix, LogDirectory = options.LogDirectory,
            TcpEndpoint = new IPEndPoint(options.TcpEndpoint.Address, options.TcpEndpoint.Port),
            ProtoVersion = options.ProtoVersion, MinAppVersion = options.MinAppVersion,
            RequiredResVersion = options.RequiredResVersion,
            RequiredConfigVersion = options.RequiredConfigVersion,
            TokenLifetime = options.TokenLifetime, ShutdownSaveBudget = options.ShutdownSaveBudget
        };
    }

    /// <summary>TCP/HTTP均绑定后完成；启动失败则异常完成。</summary>
    public Task Started => _started.Task;
    /// <summary>实际TCP端点（测试端口0时读取系统分配值）。</summary>
    public IPEndPoint? TcpEndpoint => _tcp?.LocalEndpoint;
    /// <summary>实际TCP连接数，不把玩家缓存数充作在线人数。</summary>
    public int ConnectionCount => _tcp?.ConnectionCount ?? 0;
    /// <summary>真实已认证会话数量。</summary>
    public int AuthenticatedPlayerCount => _sessions?.AuthenticatedPlayerCount ?? 0;

    /// <summary>启动并运行全部生产组件；取消会等待关闭和保存，失败会清理后向调用者交付异常。</summary>
    /// <param name="ct">本实例的停止信号。不会影响其他实例或数据库。</param>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _runOnce, 1) != 0)
            throw new InvalidOperationException("M1ServerHost实例只能启动一次");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var logging = new LoggingSetup();
        SqliteStore? store = null;
        PlayerQueue? players = null;
        SaveService? saves = null;
        var tasks = new List<Task>();
        var failures = new List<Exception>();
        Exception? primary = null;
        bool loggingConfigured = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidateOptions();
            var config = new ServerConfigService();
            config.Load(Path.GetFullPath(_options.ConfigDirectory), _options.ConfigVersion);
            logging.Configure(Path.GetFullPath(_options.LogDirectory), "Information");
            loggingConfigured = true;
            _connectionFaultLog = (id, stage, errorType) => logging.Logger.Warning(
                "M1 session cleanup failed sessionId={SessionId} stage={Stage} errorType={ErrorType}",
                id, stage, errorType);
            Action<string> trace = text => logging.Logger.Information("M1 {Trace}", text);

            store = new SqliteStore();
            store.Initialize(Path.GetFullPath(_options.DatabasePath));
            trace($"database schema initialized version={SqliteStore.CurrentVersion}");
            players = new PlayerQueue();
            var playerStore = new PlayerStore(store);
            var cache = new PlayerCache(playerStore);
            _sessions = new SessionRegistry(players, SessionRegistry.DefaultKickBudget, trace);
            saves = new SaveService(players, cache, playerStore);
            _router = new MessageRouter(SendAsync, CloseAsync, new SystemRouterClock(), trace);
            _heartbeat = new HeartbeatHandler(SendAsync);
            _heartbeat.Register(_router);
            var loginOptions = new LoginSessionOptions(
                _options.ProtoVersion, _options.RequiredResVersion, _options.RequiredConfigVersion,
                _options.TokenLifetime, _options.MinAppVersion);
            var accounts = new AccountHandler(
                new AccountRepository(store, new PasswordHasher()), players, cache,
                new TokenService(store), _sessions, FindSession, loginOptions, saves, SendAsync);
            accounts.RegisterRoutes(_router);

            _tcp = new TcpServer(DispatchAsync);
            Task http = new PatchHttpService().RunAsync(
                _options.HttpPrefix, Path.GetFullPath(_options.ContentRoot), lifetime.Token);
            tasks.Add(http);
            // HttpListener绑定在第一个await之前执行；先观察同步启动失败，不留下半个TCP服务。
            if (http.IsCompleted)
                await http.ConfigureAwait(false);
            Task tcp = _tcp.RunAsync(_options.TcpEndpoint, lifetime.Token);
            tasks.Add(tcp);
            Task first = await Task.WhenAny(_tcp.Started, tcp, http).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!_tcp.Started.IsCompletedSuccessfully || http.IsCompleted)
                throw new InvalidOperationException("M1监听在启动阶段意外结束");
            saves.Start();
            tasks.Add(MetricsReporter.Create(logging, _tcp, _sessions, _router, players).RunAsync(lifetime.Token));
            _started.TrySetResult();
            trace($"server started tcp={_tcp.LocalEndpoint} http={_options.HttpPrefix} config={_options.ConfigVersion}");

            // Only listener/background-service failures own the Host lifetime.
            // An individual peer's cleanup cannot terminate unrelated connections.
            Task ended = await Task.WhenAny(tasks).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                throw new InvalidOperationException("M1后台服务意外结束");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _started.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            primary = ex;
            _started.TrySetException(ex);
        }
        finally
        {
            lifetime.Cancel();
            // 即使一个组件失败，也继续等待其他组件退出和释放监听。
            foreach (Task task in tasks)
                await ObserveShutdownAsync(() => task, failures, true).ConfigureAwait(false);
            await ObserveShutdownAsync(() => Task.WhenAll(_connectionWatches.Values), failures).ConfigureAwait(false);
            _connectionFaultLog = null;
            if (_sessions != null)
                await ObserveShutdownAsync(_sessions.WhenIdleAsync, failures).ConfigureAwait(false);
            if (saves != null)
            {
                await ObserveShutdownAsync(saves.StopAsync, failures).ConfigureAwait(false);
                using var saveBudget = new CancellationTokenSource(_options.ShutdownSaveBudget);
                await ObserveShutdownAsync(() => saves.FlushDirtyAsync(saveBudget.Token), failures).ConfigureAwait(false);
                await ObserveShutdownAsync(() => saves.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            }
            try { players?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            try { store?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            _connections.Clear();
            if (loggingConfigured)
                logging.Logger.Information("M1 server stopped; cleanupFailures={Count}", failures.Count);
        }

        if (primary != null)
            ExceptionDispatchInfo.Capture(primary).Throw();
        if (failures.Count > 0)
            throw new AggregateException("M1停机未全部成功", failures);
    }

    /// <summary>在处理第一帧时登记实际连接，并观察关闭；所有业务仍交给已有正式路由。</summary>
    async Task DispatchAsync(ClientSession session, Packet packet, CancellationToken ct)
    {
        if (_connections.TryAdd(session.SessionId, session))
        {
            Task watch = WatchClosedAsync(session);
            _connectionWatches[session.SessionId] = watch;
            _ = watch.ContinueWith(done =>
            {
                _connectionWatches.TryRemove(session.SessionId, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        var context = _sessions!.CreateRequestContext(session, packet.MsgId, packet.Seq);
        await _router!.DispatchAsync(context, packet, ct).ConfigureAwait(false);
    }

    /// <summary>会话完全关闭后移除每连接状态；旧连接不能解绑取代它的新连接。</summary>
    async Task WatchClosedAsync(ClientSession session)
    {
        try { await session.Completion.ConfigureAwait(false); }
        catch (Exception ex) { ReportConnectionFailure(session.SessionId, "completion", ex); }
        finally
        {
            // Each release runs even when another release fails. Keep the failure local.
            CleanupConnectionPart(session.SessionId, "connection", () => _connections.TryRemove(session.SessionId, out _));
            CleanupConnectionPart(session.SessionId, "router", () => _router?.ReleaseSession(session.SessionId));
            CleanupConnectionPart(session.SessionId, "heartbeat", () => _heartbeat?.ReleaseSession(session.SessionId));
        }
        // SessionRegistry owns its identity-checked disconnect watch. Calling
        // UnbindIfCurrent here races that watch and violates its player-queue
        // precondition, turning an ordinary client exit into a Host failure.
    }

    void CleanupConnectionPart(long sessionId, string stage, Action release)
    {
        try { release(); }
        catch (Exception ex) { ReportConnectionFailure(sessionId, stage, ex); }
    }

    void ReportConnectionFailure(long sessionId, string stage, Exception error)
    {
        // Exception messages can contain peer data. Only type and numeric identity are logged.
        try { _connectionFaultLog?.Invoke(sessionId, stage, error.GetType().Name); }
        catch { /* A failed diagnostic sink must not promote a peer failure to a Host failure. */ }
    }

    /// <summary>按连接编号查真实会话；不复用已关闭连接。</summary>
    ClientSession? FindSession(long sessionId) =>
        _connections.TryGetValue(sessionId, out ClientSession? session) && !session.IsCloseRequested ? session : null;

    /// <summary>路由/账号/心跳共用唯一发送边界。</summary>
    Task SendAsync(RequestContext context, Packet packet, CancellationToken ct) =>
        (FindSession(context.SessionId) ?? throw new IOException("会话已关闭")).SendAsync(packet, ct);

    /// <summary>正式解析拒绝达到上限时仅关闭对应会话。</summary>
    Task CloseAsync(RequestContext context, string reason, CancellationToken ct) =>
        FindSession(context.SessionId)?.CloseAsync(reason, ct) ?? Task.CompletedTask;

    /// <summary>只忽略由本实例停止造成的取消；其他退出错误必须被报告。</summary>
    static async Task ObserveShutdownAsync(Func<Task> operation, List<Exception> failures, bool expectedCancellation = false)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (expectedCancellation) { }
        catch (Exception ex) { failures.Add(ex); }
    }

    /// <summary>拒绝空路径和非法监听/期限，不为错误配置创建伪成功目录。</summary>
    void ValidateOptions()
    {
        foreach (string path in new[] { _options.DatabasePath, _options.ConfigDirectory, _options.ContentRoot, _options.LogDirectory })
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("M1路径参数不能为空");
        if (!Directory.Exists(_options.ContentRoot)) throw new DirectoryNotFoundException(_options.ContentRoot);
        if (!Uri.TryCreate(_options.HttpPrefix, UriKind.Absolute, out Uri? prefix) || prefix.Scheme != "http" || prefix.Port <= 0)
            throw new ArgumentException("HTTP监听前缀非法");
        if (_options.ShutdownSaveBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(_options.ShutdownSaveBudget));
    }
}
