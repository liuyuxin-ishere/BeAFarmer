using CloudIsle.Net;
using System.Runtime.ExceptionServices;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;
using Google.Protobuf;

namespace CloudIsle.Server.Account;

/// <summary>
/// 正式账号入口。两参数构造只处理注册，不绑定会话，不签发 Token。
/// 登录构造在此之外处理登录：先版本，再认证，再在玩家队列里换发令牌并绑定当前连接。
/// 带保存服务的构造再处理重连和显式登出。重连不换发 Token。登出不回包。
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

    /// <summary>只记下注册仓库和回包委托。这个构造不注册登录路由。</summary>
    /// <param name="accounts">已有的账号仓库。为空时抛出异常。</param>
    /// <param name="sendAsync">把响应帧交给当前会话。为空时抛出异常。</param>
    public AccountHandler(
        AccountRepository accounts,
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
    }

    /// <summary>
    /// 记下注册和登录所需的明确依赖。玩家队列必须与会话注册表是同一实例，绑定才能在队列内直接执行。
    /// </summary>
    /// <param name="accounts">认证和注册使用的仓库。为空时抛出异常。</param>
    /// <param name="players">登录成功后串行化该玩家状态变化的队列。为空时抛出异常。</param>
    /// <param name="cache">在该玩家队列中读取完整快照的缓存。为空时抛出异常。</param>
    /// <param name="tokens">提交新令牌并撤销旧令牌的服务。为空时抛出异常。</param>
    /// <param name="sessions">绑定当前连接的注册表，必须使用同一个玩家队列。为空时抛出异常。</param>
    /// <param name="resolveSession">用服务端会话编号取实际连接。为空时抛出异常。找不到时返回空。</param>
    /// <param name="options">协议、资源、配置、包体和令牌期限。为空时抛出异常。</param>
    /// <param name="sendAsync">把响应帧交给当前会话。为空时抛出异常。</param>
    public AccountHandler(
        AccountRepository accounts,
        PlayerQueue players,
        PlayerCache cache,
        TokenService tokens,
        SessionRegistry sessions,
        Func<long, ClientSession?> resolveSession,
        LoginSessionOptions options,
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync)
        : this(accounts, sendAsync)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _resolveSession = resolveSession ?? throw new ArgumentNullException(nameof(resolveSession));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 在登录依赖之外记下保存服务。这个构造才注册重连和登出。
    /// 保存服务必须使用同一个玩家队列和玩家缓存，登出才能在队列内直接等待落库。
    /// </summary>
    /// <param name="accounts">认证和注册使用的仓库。为空时抛出异常。</param>
    /// <param name="players">重连和登出要进入的玩家队列。为空时抛出异常。</param>
    /// <param name="cache">读取和移除快照的缓存。为空时抛出异常。</param>
    /// <param name="tokens">校验和撤销令牌的服务。为空时抛出异常。重连不调用发放。</param>
    /// <param name="sessions">绑定和按身份解绑的注册表，必须使用同一个玩家队列。为空时抛出异常。</param>
    /// <param name="resolveSession">用服务端会话编号取实际连接。为空时抛出异常。找不到时返回空。</param>
    /// <param name="options">协议门槛和当前 UTC 毫秒。为空时抛出异常。</param>
    /// <param name="saves">显式登出等待的保存服务。为空时抛出异常。</param>
    /// <param name="sendAsync">把重连响应交给当前会话。为空时抛出异常。登出不使用它发送业务响应。</param>
    public AccountHandler(
        AccountRepository accounts,
        PlayerQueue players,
        PlayerCache cache,
        TokenService tokens,
        SessionRegistry sessions,
        Func<long, ClientSession?> resolveSession,
        LoginSessionOptions options,
        SaveService saves,
        Func<RequestContext, Packet, CancellationToken, Task> sendAsync)
        : this(accounts, players, cache, tokens, sessions, resolveSession, options, sendAsync)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
    }

    /// <summary>
    /// 测试在重连首次校验已经返回、尚未入队时暂停。生产保持 null。
    /// 用来证明队列内必须重新校验，不能沿用入队前的结果。
    /// </summary>
    /// <remarks>参数是这一次重连的取消标记。回调异常会原样传出，并且不会入队。</remarks>
    internal Func<CancellationToken, Task>? BeforeReconnectEnqueueAsync { get; set; }

    /// <summary>
    /// 测试在登出已经取得上下文、尚未入队时暂停。生产保持 null。
    /// 用来证明队列内必须重新核对当前会话。
    /// </summary>
    /// <remarks>参数是这一次登出的取消标记。回调异常会原样传出，并且不会入队。</remarks>
    internal Func<CancellationToken, Task>? BeforeLogoutEnqueueAsync { get; set; }

    /// <summary>
    /// 向已有路由注册正式 RegisterReq。登录构造再注册 LoginReq。
    /// 带保存服务的构造再注册 ReconnectReq 和 LogoutReq。
    /// 重复注册由路由拒绝，本方法不覆盖旧处理函数。
    /// </summary>
    /// <param name="router">启动时持有的路由。为空时抛出异常。</param>
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

    /// <summary>
    /// 校验昵称后创建账号和玩家，并回送正式 RegisterResp。
    /// 取消或数据库错误原样抛出，不改写成成功或重名，也不发送 code 为 0 的响应。
    /// 成功后不绑定会话，不签发 Token。
    /// </summary>
    /// <param name="context">当前帧的会话和 seq。本方法不改玩家编号。</param>
    /// <param name="request">正式注册请求。只读取已有字段。</param>
    /// <param name="ct">取消校验之后的写库和回包。</param>
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

    /// <summary>
    /// 处理正式登录。版本不通过时回 106；认证失败回 202。
    /// 成功时在玩家队列内读取缓存快照、提交新令牌、绑定这条服务端连接，再回 1006。
    /// 取消或数据库错误不改成 202 或 0，也不发送成功响应。
    /// </summary>
    /// <param name="context">当前帧的服务端会话和 seq。其中的玩家编号不作为登录身份。</param>
    /// <param name="request">正式登录请求。只读取版本、用户名和密码。</param>
    /// <param name="ct">取消版本通过后的认证、换发、绑定和回包。</param>
    public async Task HandleLoginAsync(RequestContext context, LoginReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (_options == null || _players == null || _cache == null || _tokens == null || _sessions == null || _resolveSession == null)
            throw new InvalidOperationException("登录依赖未配置");

        ct.ThrowIfCancellationRequested();
        RequireOpenSession(context.SessionId);

        if (!_options.IsProtoCompatible(request.ProtoVersion)
            || !_options.IsResCompatible(request.ResVersion)
            || !_options.IsConfigCompatible(request.ConfigVersion)
            || !_options.IsAppCompatible(request.AppVersion))
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

        await _players.EnqueueAsync(playerId, token => CompleteLoginAsync(context, playerId, token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 在该玩家队列中完成快照、换发和绑定。绑定使用队列内直接执行，避免再次入队。
    /// </summary>
    /// <param name="context">要回包的原始帧。会话编号取自这里。</param>
    /// <param name="playerId">认证得到的玩家编号。不取自客户端上下文。</param>
    /// <param name="ct">取消尚未提交的换发，或取消绑定前的后续步骤。</param>
    async Task CompleteLoginAsync(RequestContext context, long playerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ClientSession session = RequireOpenSession(context.SessionId);
        PlayerState state = await _cache!.GetAsync(playerId, ct).ConfigureAwait(false);
        long now = _options!.UtcNowMs();
        long expiresAt = _options.ExpiresAtUtcMs(now);
        string token = await _tokens!.IssueAsync(playerId, expiresAt, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        session = RequireOpenSession(context.SessionId);
        await _sessions!.BindAsync(playerId, session, ct).ConfigureAwait(false);
        if (!_sessions.IsCurrent(playerId, session.SessionId))
            throw new InvalidOperationException("登录会话没有成为当前连接");

        await SendLoginAsync(context, Accepted(token, now, state.Data), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理正式重连。会话已关闭时拒绝。协议不兼容回 106，不改绑定。
    /// 入队前的校验只用来选择玩家队列。队列内用当时的 UTC 再校验同一个 Token。
    /// 有效时回 1008、服务器 UTC 毫秒和缓存中的完整快照，并绑定这条连接。不换发 Token。
    /// 无效回 204 且没有快照。取消或数据库错误不改成 204 或 0。
    /// </summary>
    /// <param name="context">当前帧的服务端会话和 seq。玩家编号不作为重连身份。</param>
    /// <param name="request">正式重连请求。只读取 Token 原文和 proto_version。</param>
    /// <param name="ct">取消入队前的校验，以及队列内尚未写出的响应。</param>
    public async Task HandleReconnectAsync(RequestContext context, ReconnectReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (_options == null || _players == null || _cache == null || _tokens == null || _sessions == null || _resolveSession == null)
            throw new InvalidOperationException("重连依赖未配置");

        ct.ThrowIfCancellationRequested();
        RequireOpenSession(context.SessionId);
        if (!_options.IsProtoCompatible(request.ProtoVersion))
        {
            await SendReconnectAsync(context, new ReconnectResp { Code = 106 }, ct).ConfigureAwait(false);
            return;
        }

        string token = request.Token ?? "";
        long previewNow = _options.UtcNowMs();
        TokenValidation preview = await _tokens.ValidateAsync(token, previewNow, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (preview.Code != TokenValidation.ValidCode || preview.PlayerId <= 0)
        {
            await SendReconnectAsync(context, new ReconnectResp { Code = 204 }, ct).ConfigureAwait(false);
            return;
        }

        Func<CancellationToken, Task>? beforeEnqueue = BeforeReconnectEnqueueAsync;
        if (beforeEnqueue != null)
            await beforeEnqueue(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        long playerId = preview.PlayerId;
        await _players.EnqueueAsync(playerId, queueToken => CompleteReconnectAsync(context, token, playerId, queueToken), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 在该玩家队列中重新校验 Token。通过后读取快照并绑定。失败只回 204。
    /// 不调用 IssueAsync，因此不会撤销当前有效令牌，也不会产生无法放进响应的新原文。
    /// </summary>
    /// <param name="context">要回包的原始帧。会话编号取自这里。</param>
    /// <param name="token">客户端这次带来的原文。队列内再次校验的就是这一串。</param>
    /// <param name="playerId">入队前校验得到的玩家。队列内结果必须仍然是这名玩家。</param>
    /// <param name="ct">取消尚未写出的响应。取消不改写成 204。</param>
    async Task CompleteReconnectAsync(RequestContext context, string token, long playerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ClientSession session = RequireOpenSession(context.SessionId);
        long now = _options!.UtcNowMs();
        TokenValidation again = await _tokens!.ValidateAsync(token, now, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (again.Code != TokenValidation.ValidCode || again.PlayerId != playerId)
        {
            await SendReconnectAsync(context, new ReconnectResp { Code = 204 }, ct).ConfigureAwait(false);
            return;
        }

        PlayerState state = await _cache!.GetAsync(playerId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        session = RequireOpenSession(context.SessionId);
        await _sessions!.BindAsync(playerId, session, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!_sessions.IsCurrent(playerId, session.SessionId))
            throw new InvalidOperationException("重连会话没有成为当前连接");

        await SendReconnectAsync(context, new ReconnectResp
        {
            Code = 0,
            ServerTime = now,
            Data = state.Data.Clone()
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 处理正式登出。不发送 LogoutResp，也不发送伪造成功的其他帧。
    /// 入队前的身份只决定进哪一条玩家队列。队列内必须重新确认这条会话仍是 current。
    /// 保存成功才丢缓存。保存失败仍尝试撤销和按身份关闭，并保留 Dirty。
    /// 撤销失败原样抛出。清理不读取已取消的请求标记来决定跳过。
    /// </summary>
    /// <param name="context">分发时由注册表生成的上下文。玩家编号和会话编号都取自这里。</param>
    /// <param name="request">正式登出请求。没有字段。</param>
    /// <param name="ct">取消入队前的等待，以及队列内的落库。已开始的撤销和关闭不使用它。</param>
    public async Task HandleLogoutAsync(RequestContext context, LogoutReq request, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (_players == null || _cache == null || _tokens == null || _sessions == null || _resolveSession == null || _saves == null)
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

    /// <summary>
    /// 在该玩家队列中完成保存、撤销和身份匹配关闭。
    /// 若这条会话已经不是 current，则不撤销、不解绑、不改缓存。
    /// </summary>
    /// <param name="session">入队前解析到的那条连接。关闭时只用这个对象，不改查当前连接。</param>
    /// <param name="playerId">入队前上下文中的玩家。队列内用它和会话编号重新核对。</param>
    /// <param name="ct">只交给落库。撤销和关闭使用不受这次请求取消影响的标记。</param>
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
            await _tokens!.RevokePlayerAsync(playerId, CancellationToken.None).ConfigureAwait(false);
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
                await CloseLogoutSessionAsync(session).ConfigureAwait(false);
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

    /// <summary>
    /// 保存已经成功且缓存不再脏、也没有持有时才移除。
    /// 仍脏或仍被持有时留下缓存，供后续重试。
    /// </summary>
    /// <param name="playerId">刚刚完成保存尝试的玩家。调用时必须仍在该玩家队列内。</param>
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

    /// <summary>
    /// 只发起登出对应连接的关闭，由会话观察任务处理完成清理。
    /// 不依赖玩家执行器继承的异步上下文判断是否身处收发循环，也不等待自身 Completion。
    /// </summary>
    /// <param name="session">身份匹配通过时的那条连接。</param>
    Task CloseLogoutSessionAsync(ClientSession session)
    {
        session.RequestClose("登出");
        return Task.CompletedTask;
    }

    /// <summary>按服务端会话编号取仍可登录的连接。已开始关闭或不在解析结果里时拒绝。</summary>
    /// <param name="sessionId">请求上下文中的服务端会话编号。</param>
    ClientSession RequireOpenSession(long sessionId)
    {
        ClientSession? session = _resolveSession!(sessionId);
        if (session == null || session.SessionId != sessionId || session.IsCloseRequested)
            throw new InvalidOperationException("会话不存在或已开始关闭");
        return session;
    }

    /// <summary>106 只带版本门槛，不带令牌和快照。</summary>
    LoginResp VersionRejected()
    {
        return new LoginResp
        {
            Code = 106,
            NeedResVersion = _options!.RequiredResVersion,
            NeedConfigVersion = _options.RequiredConfigVersion
        };
    }

    /// <summary>202 不带令牌、快照和版本门槛。</summary>
    static LoginResp AuthRejected()
    {
        return new LoginResp { Code = 202 };
    }

    /// <summary>成功响应带令牌、同一时刻的 UTC 毫秒、最低版本和缓存中的完整快照。</summary>
    /// <param name="token">已经提交的令牌原文。只放进这一次响应。</param>
    /// <param name="nowUnixMs">计算到期时间时使用的 UTC 毫秒。</param>
    /// <param name="data">缓存返回的完整玩家数据。这里再复制一份放进响应。</param>
    LoginResp Accepted(string token, long nowUnixMs, PlayerFullData data)
    {
        return new LoginResp
        {
            Code = 0,
            Token = token,
            ServerTime = nowUnixMs,
            NeedResVersion = _options!.RequiredResVersion,
            NeedConfigVersion = _options.RequiredConfigVersion,
            Data = data.Clone()
        };
    }

    /// <summary>
    /// 05 没有规定昵称长度和字符集。空串、纯空白和控制字符在这里视为非法。
    /// 不裁剪、不设上限，其余文本原样交给仓库。
    /// </summary>
    /// <param name="nickname">请求中的昵称。</param>
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

    /// <summary>用请求的 seq 发送只含 code 的正式注册响应。</summary>
    /// <param name="context">提供 seq 的当前帧上下文。</param>
    /// <param name="code">0、201 或 203。</param>
    /// <param name="ct">取消回包。</param>
    async Task SendRegisterAsync(RequestContext context, int code, CancellationToken ct)
    {
        var response = new RegisterResp { Code = code };
        ushort responseId = MsgRegistry.GetId(typeof(RegisterResp));
        await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
            .ConfigureAwait(false);
    }

    /// <summary>用请求的 seq 发送正式登录响应。消息编号来自 LoginResp。</summary>
    /// <param name="context">提供 seq 的当前帧上下文。</param>
    /// <param name="response">已经填好的登录响应。</param>
    /// <param name="ct">取消回包。</param>
    async Task SendLoginAsync(RequestContext context, LoginResp response, CancellationToken ct)
    {
        ushort responseId = MsgRegistry.GetId(typeof(LoginResp));
        await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
            .ConfigureAwait(false);
    }

    /// <summary>用请求的 seq 发送正式重连响应。消息编号来自 ReconnectResp。不附加 Token。</summary>
    /// <param name="context">提供 seq 的当前帧上下文。</param>
    /// <param name="response">已经填好的重连响应。204 和 106 不带快照。</param>
    /// <param name="ct">取消回包。</param>
    async Task SendReconnectAsync(RequestContext context, ReconnectResp response, CancellationToken ct)
    {
        ushort responseId = MsgRegistry.GetId(typeof(ReconnectResp));
        await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
            .ConfigureAwait(false);
    }
}
