using CloudIsle.Proto;
using CloudIsle.Server.Network;

namespace CloudIsle.Server.Account;

/// <summary>认证通过后的账号身份（不含客户端自报 playerId）。</summary>
public readonly struct VerifiedAccount
{
    public VerifiedAccount(long playerId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        PlayerId = playerId;
    }

    public long PlayerId { get; }
}

/// <summary>服务端连接目标；由 Host/Handler 从真实 ClientSession 构造，不接受客户端伪造。</summary>
public sealed class SessionTarget
{
    public SessionTarget(ClientSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        SessionId = session.SessionId;
    }

    public long SessionId { get; }
    public ClientSession Session { get; }
}

/// <summary>登录事务成功后的授权结果。Token 原文仅此处携带。新登录 NextOpId 恒为 1。</summary>
public sealed class LoginGrant
{
    public LoginGrant(
        string token,
        long operationScopeId,
        long nextOpId,
        OperationScopeState scopeState,
        long serverTimeMs,
        PlayerFullData data)
    {
        Token = token ?? throw new ArgumentNullException(nameof(token));
        if (operationScopeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(operationScopeId));
        if (nextOpId < 1)
            throw new ArgumentOutOfRangeException(nameof(nextOpId));
        OperationScopeId = operationScopeId;
        NextOpId = nextOpId;
        ScopeState = scopeState;
        ServerTimeMs = serverTimeMs;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public string Token { get; }
    public long OperationScopeId { get; }
    public long NextOpId { get; }
    public OperationScopeState ScopeState { get; }
    public long ServerTimeMs { get; }
    public PlayerFullData Data { get; }
}

/// <summary>重连恢复结果；不换发 Token；NextOpId 为持久存储实际值。</summary>
public sealed class ReconnectGrant
{
    public ReconnectGrant(
        long operationScopeId,
        long nextOpId,
        OperationScopeState scopeState,
        long serverTimeMs,
        PlayerFullData data)
    {
        if (operationScopeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(operationScopeId));
        if (nextOpId < 1)
            throw new ArgumentOutOfRangeException(nameof(nextOpId));
        OperationScopeId = operationScopeId;
        NextOpId = nextOpId;
        ScopeState = scopeState;
        ServerTimeMs = serverTimeMs;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public long OperationScopeId { get; }
    public long NextOpId { get; }
    public OperationScopeState ScopeState { get; }
    public long ServerTimeMs { get; }
    public PlayerFullData Data { get; }
}

/// <summary>入队前 Token 线索；lane 内必须重验。</summary>
public readonly struct ValidatedToken
{
    public ValidatedToken(string token, long playerId)
    {
        Token = token ?? "";
        PlayerId = playerId;
    }

    public string Token { get; }
    public long PlayerId { get; }
}
