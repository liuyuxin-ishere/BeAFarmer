namespace CloudIsle.Bot;

public interface IBotOrchestrator
{
    Task<BotRunReport> RunAsync(BotOptions options, CancellationToken ct);
}

public sealed class BotOrchestrator : IBotOrchestrator
{
    public async Task<BotRunReport> RunAsync(BotOptions options, CancellationToken ct)
    {
        var workers = new BotWorker[options.Count];
        for (int i = 0; i < options.Count; i++)
            workers[i] = new BotWorker(i);

        var tasks = new List<Task>(options.Count);
        var connectDelay = TimeSpan.FromSeconds(1.0 / Math.Max(1, options.ConnectRate));

        Console.WriteLine(
            $"[Bot] start count={options.Count} duration={options.DurationSec}s " +
            $"rate={options.ConnectRate}/s moveHz={options.MoveHz} -> {options.Host}:{options.Port}");

        var overall = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < workers.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var w = workers[i];
            tasks.Add(Task.Run(() => w.RunAsync(options, ct), ct));
            if (i + 1 < workers.Length)
                await Task.Delay(connectDelay, ct);
        }

        await Task.WhenAll(tasks);
        overall.Stop();

        var report = BuildReport(workers, options, overall.Elapsed.TotalSeconds);
        Console.WriteLine(report.ToString());

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            string? dir = Path.GetDirectoryName(options.ReportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(options.ReportPath, report.ToString() + Environment.NewLine, ct);
            Console.WriteLine($"[Bot] report written {options.ReportPath}");
        }

        return report;
    }

    static BotRunReport BuildReport(BotWorker[] workers, BotOptions options, double elapsed)
    {
        var rtts = workers.SelectMany(w => w.RttSamples).OrderBy(x => x).ToList();
        int connected = workers.Count(w => w.ConnectedOk);
        int enter = workers.Count(w => w.EnterOk);
        int dropped = workers.Count(w => w.Dropped);
        double avg = rtts.Count == 0 ? 0 : rtts.Average();
        double p99 = rtts.Count == 0 ? 0 : rtts[Math.Min(rtts.Count - 1, (int)(rtts.Count * 0.99))];

        return new BotRunReport
        {
            Requested = options.Count,
            ConnectedOk = connected,
            EnterOk = enter,
            Dropped = dropped,
            DurationSec = elapsed,
            ConnectSuccessRate = options.Count == 0 ? 0 : (double)connected / options.Count,
            EnterSuccessRate = options.Count == 0 ? 0 : (double)enter / options.Count,
            DropRate = enter == 0 ? 0 : (double)dropped / enter,
            AvgRttMs = avg,
            P99RttMs = p99,
            SyncReceived = workers.Sum(w => w.SyncReceived),
            CorrectReceived = workers.Sum(w => w.CorrectReceived),
            Notes = "PoC#10"
        };
    }
}
