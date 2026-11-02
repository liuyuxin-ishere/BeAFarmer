namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>已认证写命令调用者。PlayerId/SessionId 均须为正，来自 SessionRegistry，不采客户端自报。</summary>
public readonly struct SessionIdentity
{
    public SessionIdentity(long playerId, long sessionId)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (sessionId <= 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        PlayerId = playerId;
        SessionId = sessionId;
    }

    public long PlayerId { get; }
    public long SessionId { get; }
}
