using System.Globalization;
using System.Text;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using CloudIsleLogContext = CloudIsle.Diagnostics.LogContext;

namespace CloudIsle.Server.Diagnostics;

/// <summary>
/// 服务端 Serilog 的持有者。控制台和滚动文件都经过脱敏。
/// 不写入静态 Serilog.Log.Logger。正式 Host 由 T60 组合；本类型只提供可配置入口。
/// </summary>
public sealed class LoggingSetup : IDisposable
{
    /// <summary>未指定大小时，单个文件大约 8 MiB 后滚动。</summary>
    public const long DefaultFileSizeLimitBytes = 8L * 1024 * 1024;

    /// <summary>允许的最小文件上限。再小就拒绝，避免无法滚动的配置。</summary>
    public const long MinFileSizeLimitBytes = 128;

    /// <summary>允许的最大文件上限，256 MiB。</summary>
    public const long MaxFileSizeLimitBytes = 256L * 1024 * 1024;

    /// <summary>未指定时连当前文件在内保留 8 份。</summary>
    public const int DefaultRetainedFileCount = 8;

    /// <summary>至少保留当前这一份。</summary>
    public const int MinRetainedFileCount = 1;

    /// <summary>连当前文件在内最多 32 份。</summary>
    public const int MaxRetainedFileCount = 32;

    const string OutputTemplate = "{Timestamp:O} [{Level:u3}] {Message:lj} {Properties}{NewLine}";

    readonly object _gate = new();
    Logger? _logger;

    /// <summary>用默认文件大小和保留份数配置控制台与滚动文件。</summary>
    /// <param name="logDirectory">日志目录。不存在会创建。空白、或路径已被文件占用时抛出异常。</param>
    /// <param name="minimumLevel">Verbose、Debug、Information/Info、Warning/Warn、Error、Fatal。无法识别时抛出异常。</param>
    public void Configure(string logDirectory, string minimumLevel)
    {
        Configure(logDirectory, minimumLevel, DefaultFileSizeLimitBytes, DefaultRetainedFileCount);
    }

    /// <summary>
    /// 配置本实例拥有的 Serilog。失败时不留下半套 logger，同一实例可以改正后再调用。
    /// 已经成功配置时必须先 Dispose。
    /// </summary>
    /// <param name="logDirectory">日志目录。</param>
    /// <param name="minimumLevel">最低级别。低于它的事件不会进入两个 sink。</param>
    /// <param name="fileSizeLimitBytes">单个文件的滚动上限，必须落在 128 字节到 256 MiB。</param>
    /// <param name="retainedFileCount">连当前文件在内保留的份数，必须是 1 到 32。</param>
    public void Configure(string logDirectory, string minimumLevel, long fileSizeLimitBytes, int retainedFileCount)
    {
        LogEventLevel level = ParseLevel(minimumLevel);
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("日志目录为空", nameof(logDirectory));
        if (fileSizeLimitBytes < MinFileSizeLimitBytes || fileSizeLimitBytes > MaxFileSizeLimitBytes)
            throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes), "文件上限必须在 128 字节到 256 MiB 之间");
        if (retainedFileCount < MinRetainedFileCount || retainedFileCount > MaxRetainedFileCount)
            throw new ArgumentOutOfRangeException(nameof(retainedFileCount), "保留份数必须在 1 到 32 之间");

        string directory = Path.GetFullPath(logDirectory);
        if (File.Exists(directory))
            throw new IOException("日志目录路径已被文件占用: " + directory);

        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, "server.log");
        var format = CultureInfo.InvariantCulture;

        ILogEventSink? console = null;
        ILogEventSink? file = null;
        Logger? created = null;
        try
        {
            console = LoggerSinkConfiguration.CreateSink(sink => sink.Console(
                outputTemplate: OutputTemplate,
                formatProvider: format));
            file = LoggerSinkConfiguration.CreateSink(sink => sink.File(
                path: filePath,
                restrictedToMinimumLevel: LogEventLevel.Verbose,
                outputTemplate: OutputTemplate,
                formatProvider: format,
                fileSizeLimitBytes: fileSizeLimitBytes,
                buffered: false,
                shared: false,
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCount,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
            created = new LoggerConfiguration()
                .MinimumLevel.Is(level)
                .Enrich.With(new AmbientLogContextEnricher())
                .AuditTo.Sink(new RedactingSink(new FanoutSink(console, file)))
                .CreateLogger();

            lock (_gate)
            {
                if (_logger != null)
                    throw new InvalidOperationException("日志已经配置。先 Dispose 才能再次 Configure");

                _logger = created;
                created = null;
                console = null;
                file = null;
            }
        }
        finally
        {
            if (created != null)
            {
                created.Dispose();
            }
            else
            {
                (file as IDisposable)?.Dispose();
                (console as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>本实例的 Serilog。尚未 Configure 或已经 Dispose 时抛出异常。</summary>
    public ILogger Logger
    {
        get
        {
            Logger? logger = _logger;
            if (logger == null)
                throw new InvalidOperationException("日志尚未 Configure");
            return logger;
        }
    }

    /// <summary>关闭 logger 并刷出文件。重复调用没有额外效果。</summary>
    public void Dispose()
    {
        Logger? logger;
        lock (_gate)
        {
            logger = _logger;
            _logger = null;
        }

        logger?.Dispose();
    }

    /// <summary>
    /// 测试用。和 Configure 一样先脱敏，再经 Audit 交给接收器。
    /// 接收器抛错会回到写日志的调用方。
    /// </summary>
    /// <param name="minimumLevel">与 Configure 相同的级别文字。</param>
    /// <param name="sink">观察或故意失败的接收器。</param>
    /// <returns>调用方负责 Dispose 的 logger。</returns>
    internal static Logger CreateAuditLogger(string minimumLevel, ILogEventSink sink)
    {
        if (sink == null)
            throw new ArgumentNullException(nameof(sink));

        return new LoggerConfiguration()
            .MinimumLevel.Is(ParseLevel(minimumLevel))
            .Enrich.With(new AmbientLogContextEnricher())
            .AuditTo.Sink(new RedactingSink(sink))
            .CreateLogger();
    }

    /// <summary>解析级别文字。Info 与 Warn 是两个简称，其余用 Serilog 的名字。</summary>
    /// <param name="minimumLevel">调用方给出的级别。</param>
    static LogEventLevel ParseLevel(string minimumLevel)
    {
        if (string.IsNullOrWhiteSpace(minimumLevel))
            throw new ArgumentException("日志级别为空", nameof(minimumLevel));

        switch (minimumLevel.Trim().ToLowerInvariant())
        {
            case "verbose":
                return LogEventLevel.Verbose;
            case "debug":
                return LogEventLevel.Debug;
            case "information":
            case "info":
                return LogEventLevel.Information;
            case "warning":
            case "warn":
                return LogEventLevel.Warning;
            case "error":
                return LogEventLevel.Error;
            case "fatal":
                return LogEventLevel.Fatal;
            default:
                throw new ArgumentException("无法识别的日志级别: " + minimumLevel, nameof(minimumLevel));
        }
    }

    /// <summary>把当前异步流上的 LogContext 五个字段补进事件。模板里已经有的同名属性不覆盖。</summary>
    sealed class AmbientLogContextEnricher : ILogEventEnricher
    {
        /// <summary>有环境请求时补上 PlayerId、MsgId、Seq、OpId、ElapsedMs。</summary>
        /// <param name="logEvent">即将写出的事件。</param>
        /// <param name="propertyFactory">创建属性的工厂。</param>
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            AmbientRequest? current = RequestAmbient.Current;
            if (current == null || logEvent == null || propertyFactory == null)
                return;

            CloudIsleLogContext context = current.ToLogContext();
            Add(logEvent, propertyFactory, nameof(CloudIsleLogContext.PlayerId), context.PlayerId);
            Add(logEvent, propertyFactory, nameof(CloudIsleLogContext.MsgId), context.MsgId);
            Add(logEvent, propertyFactory, nameof(CloudIsleLogContext.Seq), context.Seq);
            Add(logEvent, propertyFactory, nameof(CloudIsleLogContext.OpId), context.OpId);
            Add(logEvent, propertyFactory, nameof(CloudIsleLogContext.ElapsedMs), context.ElapsedMs);
        }

        /// <summary>同名属性已经存在时跳过。</summary>
        /// <param name="logEvent">事件。</param>
        /// <param name="propertyFactory">属性工厂。</param>
        /// <param name="name">LogContext 的字段名。</param>
        /// <param name="value">这次请求的数字。</param>
        static void Add(LogEvent logEvent, ILogEventPropertyFactory propertyFactory, string name, object value)
        {
            if (logEvent.Properties.ContainsKey(name))
                return;
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(name, value));
        }
    }
}
