using CloudIsle.Config;
using CloudIsle.Domain.Clock;
using CloudIsle.Net;
using System.Runtime.ExceptionServices;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Application.Commands;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;

namespace CloudIsle.Server.Account;

/// <summary>
/// 正式账号入口。注册仍本地处理；登录/重连/登出编排委托 <see cref="AccountSessionService"/>。
/// 版本门槛与 Token/scope 变更分离：106 发生在任何持久身份变更之前。
/// </summary>
public sealed class AccountHandler
{
    readonly AccountRepository _accounts;
    readonly Func<RequestContext, Packet, CancellationToken, Task> _sendAsync;
    readonly PlayerQueue? _players;
    readonly PlayerCache? _cache;
    readonly TokenService? _tokens;
    readonly SessionRegistry? _sessions;
    readonly Func<long, ClientSession?>? _resolveSession;
    readonly LoginSessionOptions? _options;
    readonly SaveService? _saves;
    readonly AccountSessionService? _sessionService;
    readonly AccountSessionStore? _sessionStore;

    public AccountHandler(
        AccountRepository accounts,
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
    }

    public AccountHandler(
        AccountRepository accounts,
        PlayerQueue players,
        PlayerCache cache,
        TokenService tokens,
        SessionRegistry sessions,
        Func<long, ClientSession?> resolveSession,
        LoginSessionOptions options,
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync,
        GameConfigSnapshot? gameplayConfig = null,
        SystemWriteCoordinator? systemWrites = null,
        IGameClock? clock = null)
        : this(accounts, sendAsync)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _resolveSession = resolveSession ?? throw new ArgumentNullException(nameof(resolveSession));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionStore = new AccountSessionStore(tokens.Store);
        _sessionService = new AccountSessionService(
            players, cache, tokens, _sessionStore, sessions, options, options.ToRequirements(),
            gameplayConfig, systemWrites, clock);
    }

    public AccountHandler(
        AccountRepository accounts,
        PlayerQueue players,
        PlayerCache cache,
        TokenService tokens,
        SessionRegistry sessions,
        Func<long, ClientSession?> resolveSession,
        LoginSessionOptions options,
        SaveService saves,
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync,
        GameConfigSnapshot? gameplayConfig = null,
        SystemWriteCoordinator? systemWrites = null,
        IGameClock? clock = null)
        : this(accounts, players, cache, tokens, sessions, resolveSession, options, sendAsync,
            gameplayConfig, systemWrites, clock)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
    }

    /// <summary>测试可替换的会话服务钩子转发。</summary>
    internal Func<CancellationToken, Task>? BeforeReconnectEnqueueAsync
    {
        get => _sessionService?.BeforeReconnectEnqueueAsync;
        set
        {
            if (_sessionService != null)
                _sessionService.BeforeReconnectEnqueueAsync = value;
        }
    }

    internal Func<CancellationToken, Task>? BeforeLogoutEnqueueAsync { get; set; }

    /// <summary>测试访问 Store 故障注入点。</summary>
    internal AccountSessionStore? SessionStoreForTests => _sessionStore;

    public void RegisterRoutes(MessageRouter router)
    {
        if (router == null)
            throw new ArgumentNullException(nameof(router));
        router.Register<RegisterReq>(MsgRegistry.GetId(typeof(RegisterReq)), HandleRegisterAsync);
        if (_options != null)
            router.Register<LoginReq>(MsgRegistry.GetId(typeof(LoginReq)), HandleLoginAsync);
        if (_saves != null)
        {
            router.Register<ReconnectReq>(MsgRegistry.GetId(typeof(ReconnectReq)), HandleReconnectAsync);
            router.Register<LogoutReq>(MsgRegistry.GetId(typeof(LogoutReq)), HandleLogoutAsync);
        }
    }

    public async Task HandleRegisterAsync(RequestContext context, RegisterReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        ct.ThrowIfCancellationRequested();
        if (!IsNicknameAllowed(request.Nickname))
        {
            await SendRegisterAsync(context, 203, ct).ConfigureAwait(false);
            return;
        }

        int code = await _accounts.RegisterAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (code != 0 && code != 201)
            throw new InvalidOperationException("注册结果不是成功或用户名已存在");

        await SendRegisterAsync(context, code, ct).ConfigureAwait(false);
    }

    public async Task HandleLoginAsync(RequestContext context, LoginReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (_sessionService == null || _resolveSession == null || _options == null)
            throw new InvalidOperationException("登录依赖未配置");

        ct.ThrowIfCancellationRequested();
        ClientSession session = RequireOpenSession(context.SessionId);

        var build = new ClientBuildInfo(
            request.AppVersion,
            request.BridgeContract,
            request.ProtoVersion,
            request.ResVersion,
            request.ConfigVersion);
        CompatibilityResult gate = _sessionService.CheckBuild(build);
        if (!gate.Compatible)
        {
            await SendLoginAsync(context, VersionRejected(), ct).ConfigureAwait(false);
            return;
        }

        long playerId = await _accounts.AuthenticateAsync(request.Username ?? "", request.Password ?? "", ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (playerId <= 0)
        {
            await SendLoginAsync(context, AuthRejected(), ct).ConfigureAwait(false);
            return;
        }

        LoginGrant grant = await _sessionService.LoginAsync(
            new VerifiedAccount(playerId),
            build,
            new SessionTarget(session),
            ct).ConfigureAwait(false);

        await SendLoginAsync(context, Accepted(grant), ct).ConfigureAwait(false);
    }

    public async Task HandleReconnectAsync(RequestContext context, ReconnectReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (_sessionService == null || _resolveSession == null)
            throw new InvalidOperationException("重连依赖未配置");

        ct.ThrowIfCancellationRequested();
        ClientSession session = RequireOpenSession(context.SessionId);

        var build = new ClientBuildInfo(
            request.AppVersion,
            request.BridgeContract,
            request.ProtoVersion,
            request.ResVersion,
            request.ConfigVersion);

        (int code, ReconnectGrant? grant) = await _sessionService.ReconnectAsync(
            request.Token ?? "",
            build,
            new SessionTarget(session),
            ct).ConfigureAwait(false);

        if (code != 0 || grant == null)
        {
            await SendReconnectAsync(context, new ReconnectResp { Code = code }, ct).ConfigureAwait(false);
            return;
        }

        await SendReconnectAsync(context, new ReconnectResp
        {
            Code = 0,
            ServerTime = grant.ServerTimeMs,
            Data = grant.Data,
            OperationScopeId = grant.OperationScopeId,
            NextOpId = grant.NextOpId,
            ScopeState = grant.ScopeState
        }, ct).ConfigureAwait(false);
    }

    public async Task HandleLogoutAsync(RequestContext context, LogoutReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (_players == null || _cache == null || _tokens == null || _sessions == null
            || _resolveSession == null || _saves == null || _sessionStore == null || _options == null)
            throw new InvalidOperationException("登出依赖未配置");

        ct.ThrowIfCancellationRequested();
        ClientSession session = RequireOpenSession(context.SessionId);
        if (context.PlayerId <= 0)
            throw new InvalidOperationException("登出缺少服务端玩家身份");

        Func<CancellationToken, Task>? beforeEnqueue = BeforeLogoutEnqueueAsync;
        if (beforeEnqueue != null)
            await beforeEnqueue(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        long playerId = context.PlayerId;
        await _players.EnqueueAsync(playerId, queueToken => CompleteLogoutAsync(session, playerId, queueToken), ct)
            .ConfigureAwait(false);
    }

    async Task CompleteLogoutAsync(ClientSession session, long playerId, CancellationToken ct)
    {
        if (!_sessions!.IsCurrent(playerId, session.SessionId))
            return;

        Exception? saveError = null;
        bool saved = false;
        try
        {
            await _saves!.FlushPlayerAsync(playerId, ct).ConfigureAwait(false);
            saved = true;
        }
        catch (Exception ex)
        {
            saveError = ex;
        }

        Exception? revokeError = null;
        try
        {
            // Token 撤销与 scope 关闭同事务，避免撤 Token 后 scope 仍可新写
            long now = _options!.UtcNowMs();
            await _sessionStore!.RevokeAndCloseScopesAsync(playerId, now, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            revokeError = ex;
        }

        Exception? closeError = null;
        try
        {
            if (_sessions.IsCurrent(playerId, session.SessionId))
            {
                _sessions.UnbindIfCurrent(playerId, session.SessionId);
                if (saved)
                    DropCacheIfClean(playerId);
                session.RequestClose("登出");
            }
        }
        catch (Exception ex)
        {
            closeError = ex;
        }

        if (revokeError != null)
            ExceptionDispatchInfo.Capture(revokeError).Throw();
        if (saveError != null)
            ExceptionDispatchInfo.Capture(saveError).Throw();
        if (closeError != null)
            ExceptionDispatchInfo.Capture(closeError).Throw();
    }

    void DropCacheIfClean(long playerId)
    {
        if (!_cache!.IsCached(playerId))
            return;
        if (_cache.TryCopyDirty(playerId, out _))
            return;
        if (_cache.CopyHoldCount(playerId) > 0)
            return;
        _cache.Remove(playerId);
    }

    ClientSession RequireOpenSession(long sessionId)
    {
        ClientSession? session = _resolveSession!(sessionId);
        if (session == null || session.SessionId != sessionId || session.IsCloseRequested)
            throw new InvalidOperationException("会话不存在或已开始关闭");
        return session;
    }

    LoginResp VersionRejected()
    {
        return new LoginResp
        {
            Code = 106,
            NeedResVersion = _options!.RequiredResVersion,
            NeedConfigVersion = _options.RequiredConfigVersion
        };
    }

    static LoginResp AuthRejected() => new() { Code = 202 };

    LoginResp Accepted(LoginGrant grant)
    {
        return new LoginResp
        {
            Code = 0,
            Token = grant.Token,
            ServerTime = grant.ServerTimeMs,
            NeedResVersion = _options!.RequiredResVersion,
            NeedConfigVersion = _options.RequiredConfigVersion,
            Data = grant.Data,
            OperationScopeId = grant.OperationScopeId,
            NextOpId = grant.NextOpId,
            ScopeState = grant.ScopeState
        };
    }

    static bool IsNicknameAllowed(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            return false;
        foreach (char character in nickname)
        {
            if (char.IsControl(character))
                return false;
        }
        return true;
    }

    async Task SendRegisterAsync(RequestContext context, int code, CancellationToken ct)
    {
        var response = new RegisterResp { Code = code };
        ushort responseId = MsgRegistry.GetId(typeof(RegisterResp));
        await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
            .ConfigureAwait(false);
    }

    async Task SendLoginAsync(RequestContext context, LoginResp response, CancellationToken ct)
    {
        ushort responseId = MsgRegistry.GetId(typeof(LoginResp));
        await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
            .ConfigureAwait(false);
    }

    async Task SendReconnectAsync(RequestContext context, ReconnectResp response, CancellationToken ct)
    {
        ushort responseId = MsgRegistry.GetId(typeof(ReconnectResp));
        await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
            .ConfigureAwait(false);
    }

}

