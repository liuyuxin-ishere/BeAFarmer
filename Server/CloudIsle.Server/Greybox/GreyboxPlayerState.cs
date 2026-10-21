using CloudIsle.Net;

namespace CloudIsle.Server.Greybox;

public enum PlotStateKind
{
    Empty = 0,
    Growing = 1,
    Ripe = 2
}

public enum SlotStateKind
{
    Idle = 0,
    Working = 1,
    Done = 2
}

public sealed class GreyboxPlot
{
    public PlotStateKind State;
    public long ReadyTimeMs;
}

public sealed class GreyboxPlayerState
{
    public long PlayerId;
    public int SeedCount = GreyboxIds.StartingSeeds;
    public int WheatCount;
    public int FlourCount;
    public long Gold = 100;
    public int Exp;
    public bool OrderCompleted;
    public SlotStateKind SlotState;
    public long SlotFinishMs;
    public readonly GreyboxPlot[] Plots = new GreyboxPlot[GreyboxIds.PlotCount];
    public readonly Dictionary<long, byte[]> Idempotent = new();

    public GreyboxPlayerState(long playerId, long gold)
    {
        PlayerId = playerId;
        Gold = gold;
        for (int i = 0; i < Plots.Length; i++)
            Plots[i] = new GreyboxPlot();
    }

    public void RefreshRipe(long nowMs)
    {
        for (int i = 0; i < Plots.Length; i++)
        {
            if (Plots[i].State == PlotStateKind.Growing && nowMs >= Plots[i].ReadyTimeMs)
                Plots[i].State = PlotStateKind.Ripe;
        }
        if (SlotState == SlotStateKind.Working && nowMs >= SlotFinishMs)
            SlotState = SlotStateKind.Done;
    }
}

public sealed class GreyboxWorld
{
    readonly object _gate = new();
    readonly Dictionary<long, GreyboxPlayerState> _players = new();

    public GreyboxPlayerState GetOrCreate(long playerId, long goldFromDb)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(playerId, out var st))
            {
                st = new GreyboxPlayerState(playerId, goldFromDb);
                _players[playerId] = st;
            }
            return st;
        }
    }

    public bool TryGet(long playerId, out GreyboxPlayerState st)
    {
        lock (_gate) return _players.TryGetValue(playerId, out st!);
    }

    public void Reset(long playerId, long goldFromDb)
    {
        lock (_gate)
            _players[playerId] = new GreyboxPlayerState(playerId, goldFromDb);
    }
}
