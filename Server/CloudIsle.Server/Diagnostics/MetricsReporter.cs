using System.Diagnostics;
using System.Globalization;
using CloudIsle.Server.Account;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Serilog;

namespace CloudIsle.Server.Diagnostics;

/// <summary>指标循环使用的时钟和等待。测试可以推进时间，不必真的睡 10 秒。</summary>
public interface IMetricsSchedule
{
    /// <summary>从这份时刻表创建以来经过的时间。请求率用它做分母。</summary>
    TimeSpan Elapsed { get; }

    /// <summary>等待下一拍。取消时抛出 OperationCanceledException。</summary>
    /// <param name="interval">这一拍要等的时间。</param>
    /// <param name="ct">停止报告。</param>
    Task DelayAsync(TimeSpan interval, CancellationToken ct);
}

/// <summary>用 Stopwatch 和 Task.Delay 的时刻表。</summary>
public sealed class SystemMetricsSchedule : IMetricsSchedule
{
    readonly long _startedAt = Stopwatch.GetTimestamp();

    /// <summary>创建以来的单调时间。</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedAt);

    /// <summary>按系统时钟等待。已取消时立刻抛出。</summary>
    /// <param name="interval">等待时长。</param>
    /// <param name="ct">取消等待。</param>
    public Task DelayAsync(TimeSpan interval, CancellationToken ct) => Task.Delay(interval, ct);
}

/// <summary>
/// 按间隔输出连接数、已认证玩家数、请求率、队列长度和内存。
/// 默认 10 秒。队列长度含正在执行的动作。已认证玩家数来自会话绑定，不是缓存条数。
/// T60 再挂到正式 Host。本类型不监听端口。
/// </summary>
public sealed class MetricsReporter
{
    /// <summary>未另行指定时的输出间隔。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    readonly Func<int> _connectionCount;
    readonly Func<int> _authenticatedPlayerCount;
    readonly Func<long> _requestCount;
    readonly Func<int> _queueLength;
    readonly Func<long> _memoryBytes;
    readonly Action<string> _write;
    readonly IMetricsSchedule _schedule;

    /// <summary>用采样函数和时刻表创建报告器。采样函数应读取真实对象。</summary>
    /// <param name="connectionCount">当前 TCP 连接数。</param>
    /// <param name="authenticatedPlayerCount">当前绑定会话的玩家数，不要传缓存数量。</param>
    /// <param name="requestCount">已经进入路由分发的累计次数。</param>
    /// <param name="queueLength">玩家队列尚未结束的动作数，包含正在执行的一项。</param>
    /// <param name="memoryBytes">托管堆字节数。</param>
    /// <param name="write">写出一行指标。抛错会传给 RunAsync 的调用方。</param>
    /// <param name="schedule">间隔等待和经过时间。</param>
    public MetricsReporter(
        Func<int> connectionCount,
        Func<int> authenticatedPlayerCount,
        Func<long> requestCount,
        Func<int> queueLength,
        Func<long> memoryBytes,
        Action<string> write,
        IMetricsSchedule schedule)
    {
        _connectionCount = connectionCount ?? throw new ArgumentNullException(nameof(connectionCount));
        _authenticatedPlayerCount = authenticatedPlayerCount ?? throw new ArgumentNullException(nameof(authenticatedPlayerCount));
        _requestCount = requestCount ?? throw new ArgumentNullException(nameof(requestCount));
        _queueLength = queueLength ?? throw new ArgumentNullException(nameof(queueLength));
        _memoryBytes = memoryBytes ?? throw new ArgumentNullException(nameof(memoryBytes));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    }

    /// <summary>
    /// PoC 组合入口。把四个真实对象和 Serilog 接在一起，使用系统时钟。
    /// 不启动 TCP，也不表示服务已经上线。
    /// </summary>
    /// <param name="logging">已经 Configure 的日志。指标行走 Information。</param>
    /// <param name="server">提供连接数的监听器。</param>
    /// <param name="registry">提供已认证玩家数的会话表。</param>
    /// <param name="router">提供累计请求数的路由。</param>
    /// <param name="queue">提供队列长度的玩家队列。</param>
    public static MetricsReporter Create(
        LoggingSetup logging,
        TcpServer server,
        SessionRegistry registry,
        MessageRouter router,
        PlayerQueue queue)
    {
        if (logging == null)
            throw new ArgumentNullException(nameof(logging));
        if (server == null)
            throw new ArgumentNullException(nameof(server));
        if (registry == null)
            throw new ArgumentNullException(nameof(registry));
        if (router == null)
            throw new ArgumentNullException(nameof(router));
        if (queue == null)
            throw new ArgumentNullException(nameof(queue));

        ILogger logger = logging.Logger;
        return new MetricsReporter(
            () => server.ConnectionCount,
            () => registry.AuthenticatedPlayerCount,
            () => router.RequestCount,
            () => queue.QueueLength,
            () => GC.GetTotalMemory(forceFullCollection: false),
            line => logger.Information("metrics {MetricsText}", line),
            new SystemMetricsSchedule());
    }

    /// <summary>按默认 10 秒输出，直到取消。</summary>
    /// <param name="ct">取消后不再输出。</param>
    public Task RunAsync(CancellationToken ct) => RunAsync(DefaultInterval, ct);

    /// <summary>按给定间隔输出。间隔不是正数时拒绝。经过时间为 0 的那一拍请求率为 0。</summary>
    /// <param name="interval">两拍之间的等待。必须大于 0。</param>
    /// <param name="ct">取消当前等待并停止后续输出。</param>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "指标间隔必须为正");

        while (true)
        {
            try
            {
                await _schedule.DelayAsync(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            _write(FormatLine());
        }
    }

    /// <summary>累计请求除以实际经过的秒数。经过时间小于等于 0 时返回 0，不除零。</summary>
    /// <param name="requestCount">累计进入分发的次数。负数拒绝。</param>
    /// <param name="elapsed">从报告开始经过的时间。</param>
    public static double RequestRatePerSecond(long requestCount, TimeSpan elapsed)
    {
        if (requestCount < 0)
            throw new ArgumentOutOfRangeException(nameof(requestCount));
        if (elapsed <= TimeSpan.Zero)
            return 0;
        return requestCount / elapsed.TotalSeconds;
    }

    /// <summary>采样并格式化一行。内存单位是字节。queueIncludesExecuting 表示长度含正在执行的动作。</summary>
    string FormatLine()
    {
        int connections = _connectionCount();
        int players = _authenticatedPlayerCount();
        long requests = _requestCount();
        int queueLength = _queueLength();
        long memoryBytes = _memoryBytes();
        TimeSpan elapsed = _schedule.Elapsed;
        double rate = RequestRatePerSecond(requests, elapsed);
        return string.Format(
            CultureInfo.InvariantCulture,
            "connections={0} authenticatedPlayers={1} requests={2} elapsedSeconds={3:0.###} requestRatePerSecond={4:0.###} queueLength={5} queueIncludesExecuting=true memoryBytes={6}",
            connections,
            players,
            requests,
            elapsed.TotalSeconds,
            rate,
            queueLength,
            memoryBytes);
    }
}
