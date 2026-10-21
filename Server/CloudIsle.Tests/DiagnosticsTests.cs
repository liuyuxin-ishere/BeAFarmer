using System.Globalization;
using Google.Protobuf;
using System.Text;
using System.Text.RegularExpressions;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Diagnostics;
using CloudIsle.Server.Network;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CloudIsle.Tests;

/// <summary>服务端 Serilog、脱敏和真实路由上的请求关联字段。</summary>
[NonParallelizable]
public sealed class DiagnosticsTests
{
    const string BodySecret = "body-secret-xyz";
    const string UpperSecret = "UpperSecret";
    const string ColonSecret = "ColonSecret";
    const string SpacedSecret = "spacedSecret";
    const string JsonSecret = "jsonSecret";
    const string AlphaSecret = "AlphaSecret";
    const string BetaSecret = "BetaSecret";
    const string ContextSecret = "ctx-secret";
    const string PasswordSecret = "pw-secret";
    const string TokenSecret = "tk-secret";
    const string ExceptionSecret = "ex-secret";
    const string ExceptionToken = "ex-token";

    /// <summary>两个 sink 都留下关联字段，低于最低级别的事件不出现。</summary>
    [Test]
    public async Task Request_WritesCorrelationToConsoleAndFile_AndHonorsMinimumLevel()
    {
        using var holder = new LogHolder();
        using var logging = holder.Open("Information");
        var router = NewRouter();
        ushort msgId = MsgRegistry.GetId(typeof(HeartbeatReq));
        router.Register<HeartbeatReq>(msgId, (context, request, token) =>
        {
            logging.Logger.Debug("debug-marker");
            logging.Logger.Information("info-marker");
            return Task.CompletedTask;
        });
        var decorator = new RequestLogDecorator(router, logging);

        await decorator.DispatchAsync(
            new RequestContext(5, 42, msgId, 7),
            PacketOf(new HeartbeatReq { ClientTime = 1 }, msgId, 7),
            88,
            CancellationToken.None);

        string text = holder.ReadBoth();
        Assert.That(text, Does.Contain("info-marker"));
        Assert.That(text, Does.Not.Contain("debug-marker"));
        Assert.That(holder.ConsoleText, Does.Contain("info-marker"));
        Assert.That(holder.FileText, Does.Contain("info-marker"));
        AssertRequest(holder.ConsoleText, "ok", 42, msgId, 7, 88);
        AssertRequest(holder.FileText, "ok", 42, msgId, 7, 88);
        Assert.That(RequestAmbient.HasCurrent, Is.False);
        Assert.That(router.RequestCount, Is.EqualTo(1));
    }

    /// <summary>属性、赋值文本和异常消息在两个 sink 里都不再含有原秘密。</summary>
    [Test]
    public void Secrets_AreRedactedInStructuredPropertiesTemplatesAndExceptions()
    {
        using var holder = new LogHolder();
        using var logging = holder.Open("Verbose");
        logging.Logger.Information("keys {PASSWORD} {token}", AlphaSecret, BetaSecret);
        logging.Logger.Information(
            "PASSWORD={Upper} token:{Colon} password = {Spaced}",
            UpperSecret,
            ColonSecret,
            SpacedSecret);
        logging.Logger.Information("literal password=" + UpperSecret + " token:" + ColonSecret);
        logging.Logger.Information("json \"password\":\"" + JsonSecret + "\" \"token\":\"" + JsonSecret + "\"");
        logging.Logger.ForContext("Token", ContextSecret).Information("context-only");
        logging.Logger.Information("carrier {@Carrier}", new SecretCarrier
        {
            Password = PasswordSecret,
            Token = TokenSecret,
            Name = "visible-name"
        });
        logging.Logger.Error(
            new InvalidOperationException("boom password=" + ExceptionSecret + " token:" + ExceptionToken, new IOException("token:" + ExceptionToken)),
            "broken");

        string console = holder.ConsoleText;
        string file = holder.FileText;
        foreach (string text in new[] { console, file })
        {
            Assert.That(text, Does.Contain("visible-name"));
            Assert.That(text, Does.Contain("***"));
            Assert.That(text, Does.Contain("InvalidOperationException"));
            Assert.That(text, Does.Contain("IOException"));
            Assert.That(text, Does.Not.Contain("StackTrace"));
            Assert.That(text, Does.Not.Contain(".cs:line"));
            AssertNoSecrets(text);
        }
    }

    /// <summary>模板赋值的短字符串和数字不能残留在结构化属性中，两种 sink 都要检查。</summary>
    [Test]
    public void ShortTemplateSecrets_AreRemovedFromMessageAndProperties()
    {
        using var holder = new LogHolder();
        using var logging = holder.Open("Information");
        logging.Logger.Information("short password={ShortValue}", "q");
        logging.Logger.Information("numeric token:{NumericValue}", 27);
        foreach (string text in new[] { holder.ConsoleText, holder.FileText })
        {
            string shortLine = LineContaining(text, "short password=");
            string numericLine = LineContaining(text, "numeric token:");
            Assert.That(shortLine, Does.Not.Contain("\"q\""));
            Assert.That(shortLine, Does.Not.Contain("password=q"));
            Assert.That(numericLine, Does.Not.Contain("NumericValue=27"));
            Assert.That(numericLine, Does.Not.Contain("token:27"));
            Assert.That(shortLine, Does.Contain("***"));
            Assert.That(numericLine, Does.Contain("***"));
        }
    }

    /// <summary>非法配置会失败，改正后仍可配置。Dispose 刷出文件，滚动不超过保留份数。</summary>
    [Test]
    public void InvalidConfig_FailsClearly_ThenRollsFlushesAndReleasesTheFile()
    {
        using var holder = new LogHolder();
        var logging = new LoggingSetup();
        Assert.Throws<ArgumentException>(() => logging.Configure(" ", "Information"));
        Assert.Throws<ArgumentException>(() => logging.Configure(holder.Directory, "nope"));
        Assert.Throws<ArgumentOutOfRangeException>(() => logging.Configure(holder.Directory, "Information", 127, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => logging.Configure(holder.Directory, "Information", LoggingSetup.MaxFileSizeLimitBytes + 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => logging.Configure(holder.Directory, "Information", 256, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => logging.Configure(holder.Directory, "Information", 256, LoggingSetup.MaxRetainedFileCount + 1));
        string blocked = Path.Combine(holder.Directory, "blocked");
        File.WriteAllText(blocked, "x");
        Assert.Throws<IOException>(() => logging.Configure(blocked, "Information"));

        logging.Configure(holder.Directory, "Warning", 512, 2);
        Assert.Throws<InvalidOperationException>(() => logging.Configure(holder.Directory, "Information"));
        logging.Logger.Information("info-hidden");
        logging.Logger.Warning("warn-visible");
        logging.Dispose();

        string flushed = holder.FileText;
        Assert.That(flushed, Does.Contain("warn-visible"));
        Assert.That(flushed, Does.Not.Contain("info-hidden"));
        using (var exclusive = new FileStream(Path.Combine(holder.Directory, "server.log"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.That(exclusive.Length, Is.GreaterThan(0));

        using var rolling = new LoggingSetup();
        rolling.Configure(holder.Directory, "Information", 512, 2);
        rolling.Logger.Information("OLD-MARKER " + new string('o', 300));
        for (int i = 0; i < 30; i++)
            rolling.Logger.Information("NEW-MARKER " + i.ToString(CultureInfo.InvariantCulture) + " " + new string('n', 300));
        rolling.Dispose();

        string[] files = Directory.GetFiles(holder.Directory, "server*.log");
        Assert.That(files.Length, Is.LessThanOrEqualTo(2));
        string rolled = holder.FileText;
        Assert.That(rolled, Does.Contain("NEW-MARKER"));
        Assert.That(rolled, Does.Not.Contain("OLD-MARKER"));
        Assert.That(rolled, Does.Not.Contain("warn-visible"));
    }

    /// <summary>失败请求仍有关联字段。两个并发请求不会串上下文，结束后环境为空。</summary>
    [Test]
    public async Task FailureAndConcurrentRequests_KeepIsolatedCorrelation()
    {
        using var holder = new LogHolder();
        using var logging = holder.Open("Information");
        var router = NewRouter();
        ushort msgId = MsgRegistry.GetId(typeof(HeartbeatReq));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        router.Register<HeartbeatReq>(msgId, (context, request, token) =>
        {
            logging.Logger.Information("handler-touch");
            if (Interlocked.Increment(ref entered) == 2)
                both.TrySetResult();
            return release.Task;
        });
        var decorator = new RequestLogDecorator(router, logging);
        Task first = decorator.DispatchAsync(new RequestContext(1, 101, msgId, 1001), PacketOf(new HeartbeatReq(), msgId, 1001), 501, CancellationToken.None);
        Task second = decorator.DispatchAsync(new RequestContext(2, 202, msgId, 2002), PacketOf(new HeartbeatReq(), msgId, 2002), 602, CancellationToken.None);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));

        AssertIsolated(holder.ConsoleText);
        release.TrySetResult();
        await Task.WhenAll(first, second);

        var failing = NewRouter();
        failing.Register<HeartbeatReq>(msgId, (context, request, token) =>
            throw new InvalidOperationException("password=" + ExceptionSecret + " token:" + ExceptionToken));
        var failingLog = new RequestLogDecorator(failing, logging);
        Assert.ThrowsAsync<InvalidOperationException>(() => failingLog.DispatchAsync(
            new RequestContext(3, 303, msgId, 3003),
            PacketOf(new HeartbeatReq(), msgId, 3003),
            703,
            CancellationToken.None));

        ushort registerId = MsgRegistry.GetId(typeof(RegisterReq));
        var bodyRouter = NewRouter();
        bodyRouter.Register<RegisterReq>(registerId, (context, request, token) => Task.CompletedTask);
        await new RequestLogDecorator(bodyRouter, logging).DispatchAsync(
            new RequestContext(4, 0, registerId, 4),
            PacketOf(new RegisterReq { Username = "u", Password = BodySecret, Nickname = "n" }, registerId, 4),
            0,
            CancellationToken.None);

        logging.Logger.Information("outside-marker");
        Assert.That(RequestAmbient.HasCurrent, Is.False);
        string text = holder.ReadBoth();
        AssertIsolated(text);
        AssertRequest(text, "fail", 303, msgId, 3003, 703);
        Assert.That(text, Does.Contain("InvalidOperationException"));
        Assert.That(text, Does.Not.Contain(ExceptionSecret));
        Assert.That(text, Does.Not.Contain(ExceptionToken));
        Assert.That(text, Does.Not.Contain(BodySecret));
        Assert.That(text, Does.Not.Contain(".cs:line"));
        string outside = LineContaining(text, "outside-marker");
        Assert.That(outside, Does.Not.Contain("PlayerId"));
        Assert.That(outside, Does.Not.Contain(" 101 "));
        Assert.That(outside, Does.Not.Contain(" 202 "));
        Assert.That(failing.RequestCount, Is.EqualTo(1));
    }

    /// <summary>脱敏发生在接收器之前，接收器失败会回到调用方。静态 Log 与本实例互不影响。</summary>
    [Test]
    public void AuditFailure_Propagates_AndStaticLogIsIndependent()
    {
        var seen = new CapturingSink();
        using (Logger audit = LoggingSetup.CreateAuditLogger("Information", seen))
        {
            Exception? error = null;
            try
            {
                audit.Error(
                    new InvalidOperationException("password=" + ExceptionSecret),
                    "probe {Token}",
                    ContextSecret);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Assert.That(error, Is.Not.Null);
            Assert.That(error!.ToString(), Does.Contain("sink-failed"));
        }

        Assert.That(seen.Rendered, Does.Not.Contain(ContextSecret));
        Assert.That(seen.Rendered, Does.Not.Contain(ExceptionSecret));
        Assert.That(seen.Rendered, Does.Contain("***"));
        Assert.That(seen.Rendered, Does.Contain("InvalidOperationException"));
        Assert.That(seen.HadExceptionObject, Is.False);

        using var holder = new LogHolder();
        var collected = new List<string>();
        using var probe = new LoggerConfiguration().WriteTo.Sink(new ListSink(collected)).CreateLogger();
        ILogger previous = Log.Logger;
        Log.Logger = probe;
        try
        {
            using var logging = holder.Open("Information");
            Assert.That(Log.Logger, Is.SameAs(probe));
            logging.Logger.Information("from-setup");
            Log.Information("from-static");
        }
        finally
        {
            Log.Logger = previous;
        }

        string files = holder.FileText;
        Assert.That(files, Does.Contain("from-setup"));
        Assert.That(files, Does.Not.Contain("from-static"));
        Assert.That(string.Join("\n", collected), Does.Contain("from-static"));
        Assert.That(string.Join("\n", collected), Does.Not.Contain("from-setup"));
    }

    /// <summary>新建一个不写追踪的路由。</summary>
    static MessageRouter NewRouter()
    {
        return new MessageRouter(
            (context, packet, token) => Task.CompletedTask,
            (context, reason, token) => Task.CompletedTask);
    }

    /// <summary>封一帧。测试只把正文交给路由，装饰器本身不记录它。</summary>
    /// <param name="message">请求消息。</param>
    /// <param name="msgId">消息 ID。</param>
    /// <param name="seq">序号。</param>
        static Packet PacketOf(IMessage message, ushort msgId, uint seq)
    {
        return new Packet(msgId, seq, message.ToByteArray());
    }

    /// <summary>结果行里能解析出这次的五个关联值，并且耗时不是负数。</summary>
    static void AssertRequest(string text, string outcome, long playerId, int msgId, uint seq, long opId)
    {
        var match = Regex.Match(
            text,
            "request " + outcome + " " + playerId.ToString(CultureInfo.InvariantCulture)
            + " " + msgId.ToString(CultureInfo.InvariantCulture)
            + " " + seq.ToString(CultureInfo.InvariantCulture)
            + " " + opId.ToString(CultureInfo.InvariantCulture)
            + " (?<elapsed>-?[0-9]+(?:\\.[0-9]+)?)");
        Assert.That(match.Success, Is.True, text);
        double elapsed = double.Parse(match.Groups["elapsed"].Value, CultureInfo.InvariantCulture);
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(0));
    }

    /// <summary>同一行里不能同时出现两个请求的序号。</summary>
    /// <param name="text">控制台或文件文本。</param>
    static void AssertIsolated(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!line.Contains("handler-touch", StringComparison.Ordinal)
                && !line.Contains(" 101 ", StringComparison.Ordinal)
                && !line.Contains(" 202 ", StringComparison.Ordinal))
                continue;
            bool first = line.Contains("101", StringComparison.Ordinal) && line.Contains("1001", StringComparison.Ordinal) && line.Contains("501", StringComparison.Ordinal);
            bool second = line.Contains("202", StringComparison.Ordinal) && line.Contains("2002", StringComparison.Ordinal) && line.Contains("602", StringComparison.Ordinal);
            Assert.That(first ^ second, Is.True, line);
            if (line.Contains("request ok ", StringComparison.Ordinal))
            {
                var elapsed = Regex.Match(line, "request ok \\d+ \\d+ \\d+ \\d+ (?<elapsed>[0-9]+(?:\\.[0-9]+)?)");
                Assert.That(elapsed.Success, Is.True, line);
                Assert.That(double.Parse(elapsed.Groups["elapsed"].Value, CultureInfo.InvariantCulture), Is.GreaterThan(0));
            }
        }
    }

    /// <summary>这些已知秘密都不能留下。</summary>
    /// <param name="text">一个 sink 的文本。</param>
    static void AssertNoSecrets(string text)
    {
        foreach (string secret in new[]
        {
            UpperSecret, ColonSecret, SpacedSecret, JsonSecret, AlphaSecret, BetaSecret,
            ContextSecret, PasswordSecret, TokenSecret, ExceptionSecret, ExceptionToken
        })
            Assert.That(text, Does.Not.Contain(secret));
    }

    /// <summary>找出包含标记的那一行。</summary>
    /// <param name="text">完整输出。</param>
    /// <param name="marker">行内标记。</param>
    static string LineContaining(string text, string marker)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                return line;
        }

        Assert.Fail("缺少 " + marker);
        return "";
    }

    /// <summary>带 Password 和 Token 的解构对象。</summary>
    sealed class SecretCarrier
    {
        /// <summary>应被整值替换的口令。</summary>
        public string Password { get; set; } = "";

        /// <summary>应被整值替换的令牌。</summary>
        public string Token { get; set; } = "";

        /// <summary>普通字段，应原样保留。</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>先记下已脱敏事件，再抛出 IO 异常。</summary>
    sealed class CapturingSink : ILogEventSink
    {
        /// <summary>渲染后的文本。</summary>
        public string Rendered { get; private set; } = "";

        /// <summary>交给这个接收器的事件是否还带着异常对象。</summary>
        public bool HadExceptionObject { get; private set; }

        /// <summary>记录事件后失败。</summary>
        /// <param name="logEvent">已经过脱敏的事件。</param>
        public void Emit(LogEvent logEvent)
        {
            HadExceptionObject = logEvent.Exception != null;
            var builder = new StringBuilder();
            builder.Append(logEvent.RenderMessage(CultureInfo.InvariantCulture));
            foreach (var pair in logEvent.Properties)
                builder.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            Rendered = builder.ToString();
            throw new IOException("sink-failed");
        }
    }

    /// <summary>把静态 logger 的消息收进列表。</summary>
    sealed class ListSink : ILogEventSink
    {
        readonly List<string> _lines;

        /// <summary>收进调用方的列表。</summary>
        /// <param name="lines">输出目标。</param>
        public ListSink(List<string> lines) => _lines = lines;

        /// <summary>保存渲染后的消息。</summary>
        /// <param name="logEvent">静态 logger 的事件。</param>
        public void Emit(LogEvent logEvent) => _lines.Add(logEvent.RenderMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>临时目录和可恢复的控制台。</summary>
    sealed class LogHolder : IDisposable
    {
        readonly TextWriter _previousOut;
        readonly StringWriter _console = new StringWriter();
        LoggingSetup? _logging;

        /// <summary>创建临时目录并接走控制台。</summary>
        public LogHolder()
        {
            string path = Path.Combine(Path.GetTempPath(), "CloudIsle.T40." + Guid.NewGuid().ToString("N"));
            Directory = System.IO.Directory.CreateDirectory(path).FullName;
            _previousOut = Console.Out;
            Console.SetOut(_console);
        }

        /// <summary>这次测试的日志目录。</summary>
        public string Directory { get; }

        /// <summary>目前捕获到的控制台文本。</summary>
        public string ConsoleText => _console.ToString();

        /// <summary>目录里全部日志文件的文本。</summary>
        public string FileText
        {
            get
            {
                var builder = new StringBuilder();
                if (!System.IO.Directory.Exists(Directory))
                    return "";
                foreach (string file in System.IO.Directory.GetFiles(Directory))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    builder.AppendLine(reader.ReadToEnd());
                }

                return builder.ToString();
            }
        }

        /// <summary>按 Information 打开日志。</summary>
        /// <param name="level">最低级别。</param>
        public LoggingSetup Open(string level)
        {
            _logging = new LoggingSetup();
            _logging.Configure(Directory, level);
            return _logging;
        }

        /// <summary>先刷出文件，再合并两个 sink 的文本。</summary>
        public string ReadBoth()
        {
            _logging?.Dispose();
            _logging = null;
            return ConsoleText + "\n" + FileText;
        }

        /// <summary>恢复控制台并删除临时目录。</summary>
        public void Dispose()
        {
            _logging?.Dispose();
            Console.SetOut(_previousOut);
            string full = Path.GetFullPath(Directory);
            string temp = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && full.Contains("CloudIsle.T40.", StringComparison.Ordinal))
                System.IO.Directory.Delete(full, true);
        }
    }
}
