namespace CloudIsle.Bot;

public enum BotState
{
    Idle = 0,
    Connecting = 1,
    LoggingIn = 2,
    InPlaza = 3,
    Moving = 4,
    Disconnected = 5,
    Failed = 6
}

public sealed class BotRunReport
{
    public int Requested;
    public int ConnectedOk;
    public int EnterOk;
    public int Dropped;
    public double DurationSec;
    public double ConnectSuccessRate;
    public double EnterSuccessRate;
    public double DropRate;
    public double AvgRttMs;
    public double P99RttMs;
    public long SyncReceived;
    public long CorrectReceived;
    public string Notes = "";

    public bool Passed =>
        EnterOk >= Math.Max(1, (int)Math.Floor(Requested * 0.99)) &&
        DropRate < 0.01;

    public override string ToString()
    {
        return
            $"[BotReport] requested={Requested} connected={ConnectedOk} enter={EnterOk} dropped={Dropped} " +
            $"dur={DurationSec:F1}s connectRate={ConnectSuccessRate:P1} enterRate={EnterSuccessRate:P1} " +
            $"dropRate={DropRate:P2} avgRtt={AvgRttMs:F1}ms p99Rtt={P99RttMs:F1}ms " +
            $"sync={SyncReceived} correct={CorrectReceived} pass={Passed} {Notes}";
    }
}
