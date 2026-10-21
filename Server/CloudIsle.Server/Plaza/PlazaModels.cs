using CloudIsle.Proto;

namespace CloudIsle.Server.Plaza;

public interface IPlazaOutbound
{
    long PlayerId { get; }
    ValueTask SendAsync(ushort msgId, byte[] body);
}

public sealed class PlazaPlayer
{
    public long PlayerId { get; init; }
    public string Nickname { get; init; } = "";
    public int Level { get; init; } = 1;
    public int[] AvatarIds { get; init; } = DefaultAvatars;
    public IPlazaOutbound Outbound { get; init; } = null!;

    public int X;
    public int Y;
    public int Dir;
    public int State;
    public long LastMoveUnixMs;
    public bool Dirty;

    public static readonly int[] DefaultAvatars = { 7101, 7201, 7301, 7401, 7501, 7601 };

    public PlazaEntity ToEntity()
    {
        var e = new PlazaEntity
        {
            PlayerId = PlayerId,
            Nickname = Nickname,
            Level = Level,
            X = X,
            Y = Y,
            Dir = Dir,
            State = State
        };
        e.AvatarIds.AddRange(AvatarIds);
        return e;
    }
}

public readonly struct PlazaMoveInput
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Dir { get; init; }
    public int State { get; init; }
    public long ClientTime { get; init; }
}

public sealed class PlazaEnterResult
{
    public int Code;
    public int LineId;
    public PlazaEntity? Self;
    public List<PlazaEntity> Entities = new();
}

public sealed class PlazaLineMetrics
{
    public int LineId;
    public int Online;
    public double LastTickMs;
    public double P50TickMs;
    public double P99TickMs;
    public long MoveMsgPerSec;
    public long SyncMsgPerSec;
    public long CorrectCount;
}
