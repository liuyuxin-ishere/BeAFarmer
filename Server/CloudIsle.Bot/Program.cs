namespace CloudIsle.Bot;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var opt = Parse(args);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var orch = new BotOrchestrator();
            var report = await orch.RunAsync(opt, cts.Token);

            // 退出码：0 通过；1 指标未达标；2 环境/连接基本失败
            if (report.ConnectedOk == 0)
                return 2;
            return report.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bot] FAILED: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    static BotOptions Parse(string[] args)
    {
        var o = new BotOptions();
        o.Host = GetArg(args, "--host") ?? o.Host;
        if (int.TryParse(GetArg(args, "--port"), out int port)) o.Port = port;
        if (int.TryParse(GetArg(args, "--count"), out int count)) o.Count = count;
        if (int.TryParse(GetArg(args, "--duration"), out int dur)) o.DurationSec = dur;
        if (int.TryParse(GetArg(args, "--connect-rate"), out int rate)) o.ConnectRate = rate;
        if (double.TryParse(GetArg(args, "--move-hz"), out double hz)) o.MoveHz = hz;
        if (int.TryParse(GetArg(args, "--map-size"), out int map)) o.MapSizeCm = map * 100; // meters → cm
        if (int.TryParse(GetArg(args, "--seed"), out int seed)) o.Seed = seed;
        o.EnableChat = HasFlag(args, "--enable-chat");
        o.EnableEmote = HasFlag(args, "--enable-emote");
        o.ReportPath = GetArg(args, "--report") ?? o.ReportPath;
        o.Password = GetArg(args, "--pass") ?? o.Password;
        return o;
    }

    static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
