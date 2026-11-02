using CloudIsle.Config;
using CloudIsle.Domain.Clock;
using CloudIsle.Domain.Init;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Application.Commands;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Account;

/// <summary>
/// 账号认证后的会话编排：版本门槛 → 玩家 lane → Store 单事务 → 提交后绑定/顶号。
/// 不在 lane 内 await 依赖本 lane 的旧连接 Flush。
/// Token+scope 轮换（T009）不变；可选在轮换前 composition 一次初始化（T012）。
/// </summary>
public sealed class AccountSessionService
{
    readonly PlayerQueue _players;
    readonly PlayerCache _cache;
    readonly TokenService _tokens;
    readonly AccountSessionStore _sessionStore;
    readonly SessionRegistry _sessions;
    readonly LoginSessionOptions _options;
    readonly ServerRequirements _requirements;
    readonly GameConfigSnapshot? _configSnapshot;
    readonly SystemWriteCoordinator? _systemWrites;
    readonly IGameClock? _clock;

    /// <summary>测试：重连入队前暂停（与 AccountHandler 原 hook 等价）。</summary>
    internal Func<CancellationToken, Task>? BeforeReconnectEnqueueAsync { get; set; }

    public AccountSessionService(
        PlayerQueue players,
        PlayerCache cache,
        TokenService tokens,
        AccountSessionStore sessionStore,
        SessionRegistry sessions,
        LoginSessionOptions options,
        ServerRequirements requirements,
        GameConfigSnapshot? configSnapshot = null,
        SystemWriteCoordinator? systemWrites = null,
        IGameClock? clock = null)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        _configSnapshot = configSnapshot;
        _systemWrites = systemWrites;
        _clock = clock;
        if (_configSnapshot != null && _requirements.RequiredConfigVersion != _configSnapshot.Version)
        {
            throw new ArgumentException(
                $"RequiredConfigVersion {_requirements.RequiredConfigVersion} must equal GameConfigSnapshot.Version {_configSnapshot.Version}",
                nameof(requirements));
        }
    }

    public ServerRequirements Requirements => _requirements;
    public LoginSessionOptions Options => _options;
    public AccountSessionStore Store => _sessionStore;
    public TokenService Tokens => _tokens;
    /// <summary>Host 注入的已校验配置快照；测试夹具可为 null。</summary>
    public GameConfigSnapshot? ConfigSnapshot => _configSnapshot;

    /// <summary>版本门槛（Login/Reconnect 共用）。对照注入快照的 Config 门槛，不另读盘。</summary>
    public CompatibilityResult CheckBuild(ClientBuildInfo build)
    {
        if (_configSnapshot != null && _requirements.RequiredConfigVersion != _configSnapshot.Version)
            throw new InvalidOperationException("requirements/config snapshot desync");
        return ClientCompatibilityPolicy.Check(build, _requirements);
    }

    /// <summary>
    /// 已通过版本与账号认证后：入队轮换 Token+scope，提交后绑定。
    /// admissionCt 可在事务开始前取消；事务开始后由持久完成范围拥有。
    /// </summary>
    public async Task<LoginGrant> LoginAsync(
        VerifiedAccount account,
        ClientBuildInfo build,
        SessionTarget target,
        CancellationToken admissionCt)
    {
        CompatibilityResult gate = CheckBuild(build);
        if (!gate.Compatible)
            throw new InvalidOperationException("compatibility must be checked before LoginAsync");

        admissionCt.ThrowIfCancellationRequested();
        RequireOpen(target);

        LoginGrant? grant = null;
        await _players.EnqueueAsync(account.PlayerId, async laneCt =>
        {
            // 受理后使用不受客户端退出影响的完成标记完成 DB；laneCt 仅用于缓存读可取消点
            CancellationToken completionCt = CancellationToken.None;
            RequireOpen(target);
            PlayerState state = await _cache.GetAsync(account.PlayerId, laneCt).ConfigureAwait(false);
            long now = await CaptureNowAsync(completionCt).ConfigureAwait(false);

            // T012：Token 轮换前一次初始化；失败则不发 LoginGrant（无 ready 假快照）
            if (_systemWrites != null && _configSnapshot != null)
            {
                PlayerOrigin origin = SystemWriteCoordinator.ClassifyOrigin(state);
                InitLaneResult init = await _systemWrites
                    .InitializeInLaneAsync(account.PlayerId, origin, _configSnapshot, now, completionCt)
                    .ConfigureAwait(false);
                state = init.State;
                DailyLaneResult day = await _systemWrites
                    .ReconcileDailyInLaneAsync(account.PlayerId, now, completionCt)
                    .ConfigureAwait(false);
                state = day.State;
            }

            long expires = _options.ExpiresAtUtcMs(now);
            (string token, long scopeId) = await _sessionStore
                .RotateLoginAsync(account.PlayerId, expires, now, completionCt)
                .ConfigureAwait(false);

            try
            {
                RequireOpen(target);
                await _sessions.BindAsync(account.PlayerId, target.Session, completionCt).ConfigureAwait(false);
                if (!_sessions.IsCurrent(account.PlayerId, target.SessionId))
                    throw new InvalidOperationException("登录会话没有成为当前连接");
            }
            catch
            {
                // 持久身份已提交：旧连接不得继续权威；不 await Flush。
                _sessions.RetireCurrentIfAny(account.PlayerId);
                throw;
            }

            // 新登录关闭旧 scope 后新建：权威 next_op 恒为 1，scope OPEN
            grant = new LoginGrant(
                token, scopeId, nextOpId: 1, OperationScopeState.Open, now, state.Data.Clone());
        }, admissionCt).ConfigureAwait(false);

        return grant ?? throw new InvalidOperationException("登录未产生授权");
    }

    /// <summary>
    /// 先版本门槛；入队前 Validate 只选 lane；队首取 now 重验并 RestoreScope。
    /// </summary>
    public async Task<(int Code, ReconnectGrant? Grant)> ReconnectAsync(
        string token,
        ClientBuildInfo build,
        SessionTarget target,
        CancellationToken admissionCt)
    {
        CompatibilityResult gate = CheckBuild(build);
        if (!gate.Compatible)
            return (106, null);

        admissionCt.ThrowIfCancellationRequested();
        RequireOpen(target);

        long previewNow = await CaptureNowAsync(admissionCt).ConfigureAwait(false);
        TokenValidation preview = await _tokens.ValidateAsync(token ?? "", previewNow, admissionCt)
            .ConfigureAwait(false);
        if (preview.Code != TokenValidation.ValidCode || preview.PlayerId <= 0)
            return (204, null);

        Func<CancellationToken, Task>? before = BeforeReconnectEnqueueAsync;
        if (before != null)
            await before(admissionCt).ConfigureAwait(false);
        admissionCt.ThrowIfCancellationRequested();

        long playerId = preview.PlayerId;
        int code = 204;
        ReconnectGrant? grant = null;
        await _players.EnqueueAsync(playerId, async laneCt =>
        {
            RequireOpen(target);
            long now = await CaptureNowAsync(CancellationToken.None).ConfigureAwait(false); // 队首更新的 now
            ScopeRestoreResult restored = await _sessionStore
                .RestoreScopeDetailedAsync(token ?? "", playerId, now, CancellationToken.None)
                .ConfigureAwait(false);
            if (restored.ScopeId <= 0 || restored.NextOpId < 1)
            {
                code = 204;
                return;
            }

            PlayerState state = await _cache.GetAsync(playerId, laneCt).ConfigureAwait(false);
            if (_systemWrites != null && _configSnapshot != null)
            {
                PlayerOrigin origin = SystemWriteCoordinator.ClassifyOrigin(state);
                InitLaneResult init = await _systemWrites
                    .InitializeInLaneAsync(playerId, origin, _configSnapshot, now, CancellationToken.None)
                    .ConfigureAwait(false);
                state = init.State;
                DailyLaneResult day = await _systemWrites
                    .ReconcileDailyInLaneAsync(playerId, now, CancellationToken.None)
                    .ConfigureAwait(false);
                state = day.State;
            }

            try
            {
                RequireOpen(target);
                await _sessions.BindAsync(playerId, target.Session, CancellationToken.None).ConfigureAwait(false);
                if (!_sessions.IsCurrent(playerId, target.SessionId))
                    throw new InvalidOperationException("重连会话没有成为当前连接");
            }
            catch
            {
                _sessions.RetireCurrentIfAny(playerId);
                throw;
            }

            code = 0;
            // 权威 next 为存储实际值，不得恒写 1
            grant = new ReconnectGrant(
                restored.ScopeId,
                restored.NextOpId,
                OperationScopeState.Open,
                now,
                state.Data.Clone());
        }, admissionCt).ConfigureAwait(false);

        return (code, grant);
    }

    async Task<long> CaptureNowAsync(CancellationToken ct)
    {
        if (_clock != null)
            return await _clock.GetEffectiveNowMsAsync(ct).ConfigureAwait(false);
        return _options.UtcNowMs();
    }

    static void RequireOpen(SessionTarget target)
    {
        ClientSession session = target.Session;
        if (session.SessionId != target.SessionId || session.IsCloseRequested)
            throw new InvalidOperationException("会话不存在或已开始关闭");
    }
}
