using System.Globalization;
using System.Net;

namespace CloudIsle.Server.Hosting;

/// <summary>正式M1命令行入口。所有参数显式验证，失败返回非0，不降级到旧PoC。</summary>
internal static class M1ServerCommand
{
    /// <summary>解析路径、监听与版本，等待生产Host退出；本方法不打印密码、Token或请求正文。</summary>
    /// <param name="args">含--m1开关，其余项为--name value成对参数。</param>
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            // Windows重定向时仍使用UTF-8，让脚本/验收日志中的中文不被系统代码页破坏。
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Dictionary<string, string> values = Parse(args);
            string repo = Path.GetFullPath(Value(values, "--repo-root", Environment.CurrentDirectory));
            string PathArg(string key, string fallback) =>
                Path.GetFullPath(Value(values, key, fallback), repo);
            int tcpPort = Positive(values, "--tcp-port", 17780, 65535);
            int httpPort = Positive(values, "--http-port", 18080, 65535);
            string hostText = Value(values, "--tcp-host", "127.0.0.1");
            if (!IPAddress.TryParse(hostText, out IPAddress? address))
                throw new ArgumentException("--tcp-host必须是IP地址");
            string? stopFile = values.ContainsKey("--stop-file") ? PathArg("--stop-file", "") : null;
            if (stopFile != null && File.Exists(stopFile))
                throw new ArgumentException("stop-file启动前已存在，请使用本次实例的新停止信号文件");
            var options = new M1ServerOptions
            {
                DatabasePath = PathArg("--db", "Server/Data/M1/m1.db"),
                ConfigDirectory = PathArg("--config-dir", "Server/ConfigLuban"),
                ConfigVersion = Positive(values, "--config-version", 200),
                ContentRoot = PathArg("--content-root", "Server/PatchContent/M1"),
                LogDirectory = PathArg("--log-dir", "Server/Logs/M1"),
                TcpEndpoint = new IPEndPoint(address, tcpPort),
                HttpPrefix = $"http://localhost:{httpPort}/",
                MinAppVersion = Value(values, "--min-app-version", "0.4.0"),
                RequiredBridgeContract = Value(values, "--bridge-contract", "m2-bridge-0.4"),
                ProtoVersion = Positive(values, "--proto-version", 1),
                RequiredResVersion = Positive(values, "--res-version", 200),
                RequiredConfigVersion = Positive(values, "--required-config-version", 200)
            };
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
            Task watch = stopFile == null ? Task.CompletedTask : WatchStopFileAsync(stopFile, stop);
            try
            {
                var server = new M1ServerHost(options);
                // Host将绑定失败交给Run；Started异常也被明确观察，避免悬挂/未观察异常。
                Task run = server.RunAsync(stop.Token);
                try { await server.Started.ConfigureAwait(false); }
                catch { await run.ConfigureAwait(false); throw; }
                await run.ConfigureAwait(false);
                return 0;
            }
            finally
            {
                stop.Cancel();
                try { await watch.ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                Console.CancelKeyPress -= onCancel;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[M1] 启动或停机失败: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>可选文件信号提供重定向/脚本环境的正常停机；只检测存在，不执行文件内容。</summary>
    /// <param name="path">此实例独占的新信号路径，启动前必须不存在。</param>
    /// <param name="stop">仅取消本次Host及此观察任务。</param>
    static async Task WatchStopFileAsync(string path, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (File.Exists(path)) { stop.Cancel(); return; }
            await Task.Delay(250, stop.Token).ConfigureAwait(false);
        }
    }

    /// <summary>拒绝未知、重复、空白或缺值参数；拼写错误不能静默落默认值。</summary>
    static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--repo-root", "--db", "--config-dir", "--config-version", "--content-root", "--log-dir",
            "--tcp-host", "--tcp-port", "--http-port", "--min-app-version", "--bridge-contract",
            "--proto-version", "--res-version", "--required-config-version", "--stop-file"
        };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool seenM1 = false;
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key.Equals("--m1", StringComparison.OrdinalIgnoreCase))
            {
                if (seenM1) throw new ArgumentException("重复--m1");
                seenM1 = true;
                continue;
            }
            if (!allowed.Contains(key)) throw new ArgumentException("未知M1参数: " + key);
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("参数缺值: " + key);
            if (!result.TryAdd(key, args[i])) throw new ArgumentException("重复M1参数: " + key);
        }
        return result;
    }

    /// <summary>读取已校验非空参数，缺项采用文档默认值。</summary>
    static string Value(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? value) ? value : fallback;

    /// <summary>解析正整数并拒绝溢出、0、负数及端口上界。</summary>
    static int Positive(Dictionary<string, string> values, string key, int fallback, int max = int.MaxValue)
    {
        if (!values.TryGetValue(key, out string? text)) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0 || value > max)
            throw new ArgumentOutOfRangeException(key, "必须是范围内的正整数");
        return value;
    }
}
