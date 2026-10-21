using System.Net;
using Google.Protobuf;
using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Diagnostics;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;

namespace CloudIsle.Tests;

/// <summary>指标间隔、请求率，以及连接、认证玩家和队列的真实计数。</summary>
[NonParallelizable]
public sealed class MetricsReporterTests
{
    /// <summary>经过时间为 0 或负数时速率是 0。默认间隔是 10 秒。</summary>
    [Test]
    public void RequestRate_IsZeroWhenElapsedIsNotPositive()
    {
        Assert.That(MetricsReporter.DefaultInterval, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(MetricsReporter.RequestRatePerSecond(5, TimeSpan.Zero), Is.EqualTo(0));
        Assert.That(MetricsReporter.RequestRatePerSecond(5, TimeSpan.FromTicks(-1)), Is.EqualTo(0));
        Assert.That(MetricsReporter.RequestRatePerSecond(0, TimeSpan.FromSeconds(10)), Is.EqualTo(0));
        Assert.That(MetricsReporter.RequestRatePerSecond(4, TimeSpan.FromSeconds(10)), Is.EqualTo(0.4).Within(0.000001));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricsReporter.RequestRatePerSecond(-1, TimeSpan.FromSeconds(1)));
    }

    /// <summary>第一拍按默认 10 秒等待。取消发生在输出之前时没有行，之后也不再等。</summary>
    [Test]
    public async Task DefaultInterval_CancelsBeforeFirstLine()
    {
        var schedule = new ScriptedSchedule();
        var lines = new List<string>();
        var reporter = Reporter(schedule, lines.Add, () => 0, () => 0, () => 0, () => 0);
        using var cts = new CancellationTokenSource();
        Task run = reporter.RunAsync(cts.Token);
        TaskCompletionSource first = await schedule.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(schedule.Delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(10) }));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(lines, Is.Empty);
        Assert.That(schedule.Delays, Has.Count.EqualTo(1));
        Assert.That(run.IsCompletedSuccessfully, Is.True);
        Assert.That(first.Task.IsCanceled, Is.True);
    }

    /// <summary>输出一拍之后取消，第二次等待不会再写出。</summary>
    [Test]
    public async Task Stop_AfterOneSample_WritesNothingMore()
    {
        var schedule = new ScriptedSchedule { Elapsed = TimeSpan.Zero };
        var lines = new List<string>();
        long requests = 3;
        var reporter = Reporter(schedule, lines.Add, () => 1, () => 2, () => requests, () => 4);
        using var cts = new CancellationTokenSource();
        Task run = reporter.RunAsync(TimeSpan.FromSeconds(10), cts.Token);
        TaskCompletionSource first = await schedule.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        first.TrySetResult();
        await WaitForLinesAsync(lines, 1);
        Assert.That(lines[0], Does.Contain("requestRatePerSecond=0"));
        Assert.That(lines[0], Does.Contain("elapsedSeconds=0"));
        Assert.That(lines[0], Does.Contain("connections=1"));
        Assert.That(lines[0], Does.Contain("authenticatedPlayers=2"));
        Assert.That(lines[0], Does.Contain("requests=3"));
        Assert.That(lines[0], Does.Contain("queueLength=4"));
        Assert.That(lines[0], Does.Contain("queueIncludesExecuting=true"));
        Assert.That(lines[0], Does.Contain("memoryBytes="));

        schedule.Elapsed = TimeSpan.FromSeconds(10);
        TaskCompletionSource second = await schedule.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(second.Task.IsCanceled, Is.True);
    }

    /// <summary>零间隔被拒绝。采样函数抛错不会被报告器吃掉。</summary>
    [Test]
    public async Task ZeroInterval_IsRejected_AndWriteErrorsPropagate()
    {
        var schedule = new ScriptedSchedule();
        var reporter = Reporter(schedule, _ => { }, () => 0, () => 0, () => 0, () => 0);
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reporter.RunAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.That(schedule.Delays, Is.Empty);

        var failing = Reporter(schedule, _ => throw new InvalidOperationException("write-failed"), () => 0, () => 0, () => 0, () => 0);
        Task run = failing.RunAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        TaskCompletionSource wait = await schedule.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        wait.TrySetResult();
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await run);
        Assert.That(error!.Message, Is.EqualTo("write-failed"));
    }

    /// <summary>连接、绑定玩家、路由计数和含执行中的队列长度来自真实对象。缓存不参与。</summary>
    [Test]
    public async Task Sample_UsesLiveServerRegistryRouterAndQueue()
    {
        ClientSession? session = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gate = new ListeningGate((seen, packet, token) =>
        {
            session = seen;
            ready.TrySetResult();
            return Task.CompletedTask;
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gate.Port);
        await client.GetStream().WriteAsync(FrameCodec.Encode(1, 1, new byte[] { 1 }));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(gate.Server.ConnectionCount, Is.EqualTo(1));

        using var queue = new PlayerQueue();
        var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), static _ => { });
        await registry.BindAsync(42, session!, CancellationToken.None);
        Assert.That(registry.AuthenticatedPlayerCount, Is.EqualTo(1));
        Assert.That(registry.AuthenticatedPlayerCount, Is.EqualTo(registry.MappedPlayerCount));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = queue.EnqueueAsync(42, async token =>
        {
            entered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(queue.QueueLength, Is.EqualTo(1));
        Task waiting = queue.EnqueueAsync(42, token => Task.CompletedTask, CancellationToken.None);
        Assert.That(queue.QueueLength, Is.EqualTo(2));

        var router = new MessageRouter(
            (context, packet, token) => Task.CompletedTask,
            (context, reason, token) => Task.CompletedTask);
        ushort msgId = MsgRegistry.GetId(typeof(HeartbeatReq));
        router.Register<HeartbeatReq>(msgId, (context, request, token) => Task.CompletedTask);
        for (uint seq = 1; seq <= 4; seq++)
        {
            await router.DispatchAsync(
                new RequestContext(9, 42, msgId, seq),
                new Packet(msgId, seq, new HeartbeatReq().ToByteArray()),
                CancellationToken.None);
        }

        Assert.That(router.RequestCount, Is.EqualTo(4));
        var schedule = new ScriptedSchedule { Elapsed = TimeSpan.FromSeconds(10) };
        var lines = new List<string>();
        var reporter = new MetricsReporter(
            () => gate.Server.ConnectionCount,
            () => registry.AuthenticatedPlayerCount,
            () => router.RequestCount,
            () => queue.QueueLength,
            () => 4096,
            lines.Add,
            schedule);
        using var cts = new CancellationTokenSource();
        Task report = reporter.RunAsync(MetricsReporter.DefaultInterval, cts.Token);
        TaskCompletionSource tick = await schedule.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(schedule.Delays[0], Is.EqualTo(TimeSpan.FromSeconds(10)));
        tick.TrySetResult();
        await WaitForLinesAsync(lines, 1);
        cts.Cancel();
        await report.WaitAsync(TimeSpan.FromSeconds(2));

        string line = lines[0];
        Assert.That(line, Does.Contain("connections=1"));
        Assert.That(line, Does.Contain("authenticatedPlayers=1"));
        Assert.That(line, Does.Contain("requests=4"));
        Assert.That(line, Does.Contain("elapsedSeconds=10"));
        Assert.That(line, Does.Contain("requestRatePerSecond=0.4"));
        Assert.That(line, Does.Contain("queueLength=2"));
        Assert.That(line, Does.Contain("queueIncludesExecuting=true"));
        Assert.That(line, Does.Contain("memoryBytes=4096"));

        release.TrySetResult();
        await Task.WhenAll(running, waiting);
        Assert.That(queue.QueueLength, Is.EqualTo(0));
        client.Close();
        await session!.CloseAsync("测试结束", CancellationToken.None);
        await WaitUntilAsync(() => gate.Server.ConnectionCount == 0);
        Assert.That(gate.Server.ConnectionCount, Is.EqualTo(0));
    }

    /// <summary>PoC 组合入口使用系统等待。取消在第一拍之前完成时，日志文件没有指标行。</summary>
    [Test]
    public async Task Create_StopsWithoutEmittingWhenCanceledImmediately()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CloudIsle.T40." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var logging = new LoggingSetup();
            logging.Configure(directory, "Information");
            using var queue = new PlayerQueue();
            var registry = new SessionRegistry(queue, TimeSpan.FromSeconds(2), static _ => { });
            var server = new TcpServer((session, packet, token) => Task.CompletedTask);
            var router = new MessageRouter(
                (context, packet, token) => Task.CompletedTask,
                (context, reason, token) => Task.CompletedTask);
            MetricsReporter reporter = MetricsReporter.Create(logging, server, registry, router, queue);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Task run = reporter.RunAsync(cts.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            logging.Dispose();
            string text = "";
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.GetFiles(directory))
                    text += File.ReadAllText(file);
            }

            Assert.That(text, Does.Not.Contain("connections="));
            Assert.That(server.ConnectionCount, Is.EqualTo(0));
            Assert.That(registry.AuthenticatedPlayerCount, Is.EqualTo(0));
            Assert.That(queue.QueueLength, Is.EqualTo(0));
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(full, true);
        }
    }

    /// <summary>组装一个只把数字交给测试的报告器。</summary>
    static MetricsReporter Reporter(
        IMetricsSchedule schedule,
        Action<string> write,
        Func<int> connections,
        Func<int> players,
        Func<long> requests,
        Func<int> queueLength)
    {
        return new MetricsReporter(connections, players, requests, queueLength, () => 2048, write, schedule);
    }

    /// <summary>等到行数达到预期。只用于同步信号之后的短等待。</summary>
    /// <param name="lines">已经写出的行。</param>
    /// <param name="count">需要的行数。</param>
    static async Task WaitForLinesAsync(List<string> lines, int count)
    {
        var timeout = TimeSpan.FromSeconds(2);
        var started = DateTime.UtcNow;
        while (lines.Count < count)
        {
            if (DateTime.UtcNow - started > timeout)
                Assert.Fail("指标没有写出");
            await Task.Yield();
        }
    }

    /// <summary>等到条件成立。</summary>
    /// <param name="condition">为真时返回。</param>
    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                Assert.Fail("条件没有成立");
            await Task.Delay(10);
        }
    }

    /// <summary>测试可控的等待。完成或取消都由测试决定。</summary>
    sealed class ScriptedSchedule : IMetricsSchedule
    {
        readonly object _gate = new();
        readonly Queue<TaskCompletionSource> _waits = new();
        TaskCompletionSource<TaskCompletionSource>? _notify;

        /// <summary>报告器读到的经过时间。</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>已经请求过的等待时长。</summary>
        public List<TimeSpan> Delays { get; } = new();

        /// <summary>记录这一拍并停住，直到测试完成或取消。</summary>
        /// <param name="interval">报告器传入的间隔。</param>
        /// <param name="ct">取消这一拍。</param>
        public Task DelayAsync(TimeSpan interval, CancellationToken ct)
        {
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<TaskCompletionSource>? notify = null;
            lock (_gate)
            {
                Delays.Add(interval);
                if (_notify != null)
                {
                    notify = _notify;
                    _notify = null;
                }
                else
                {
                    _waits.Enqueue(wait);
                }
            }

            if (ct.CanBeCanceled)
                ct.Register(() => wait.TrySetCanceled(ct));
            notify?.TrySetResult(wait);
            if (ct.IsCancellationRequested)
                wait.TrySetCanceled(ct);
            return wait.Task;
        }

        /// <summary>等到下一拍已经进入等待。</summary>
        public Task<TaskCompletionSource> NextAsync()
        {
            lock (_gate)
            {
                if (_waits.Count > 0)
                    return Task.FromResult(_waits.Dequeue());
                _notify = new TaskCompletionSource<TaskCompletionSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _notify.Task;
            }
        }
    }

    /// <summary>端口 0 的临时监听。</summary>
    sealed class ListeningGate : IAsyncDisposable
    {
        readonly CancellationTokenSource _cts = new();
        readonly Task _run;

        /// <summary>启动监听。</summary>
        /// <param name="onPacket">收到完整帧时调用。</param>
        public ListeningGate(Func<ClientSession, Packet, CancellationToken, Task> onPacket)
        {
            Server = new TcpServer(onPacket, 8);
            _run = Server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), _cts.Token);
        }

        /// <summary>被测监听器。</summary>
        public TcpServer Server { get; }

        /// <summary>系统分配的端口。</summary>
        public int Port
        {
            get
            {
                Server.Started.GetAwaiter().GetResult();
                return Server.LocalEndpoint!.Port;
            }
        }

        /// <summary>停止监听并等待结束。</summary>
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
            _cts.Dispose();
        }
    }
}
