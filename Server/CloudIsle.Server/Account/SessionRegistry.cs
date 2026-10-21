using System.Diagnostics.CodeAnalysis;
using CloudIsle.Net;
using Google.Protobuf;
using CloudIsle.Proto;
using CloudIsle.Server.Network;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Account;

/// <summary>
/// 同一玩家只保留一条当前连接。新连接写入映射后，旧连接才收到正式踢线并关闭。
/// 不签发令牌，也不处理登录或登出。
/// </summary>
public sealed class SessionRegistry
{
    /// <summary>未另行指定时，踢线写入和等待旧连接关闭共用的上限。</summary>
    public static readonly TimeSpan DefaultKickBudget = TimeSpan.FromSeconds(2);

    readonly PlayerQueue _queue;
    readonly TimeSpan _kickBudget;
    readonly Action<string> _log;
    readonly object _gate = new();
    readonly Dictionary<long, ClientSession> _byPlayer = new();
    readonly Dictionary<long, long> _bySession = new();
    readonly Dictionary<long, Task> _watches = new();
    readonly HashSet<long> _retiring = new();

    /// <summary>使用共享玩家队列，踢线预算为 2 秒，日志写到控制台。</summary>
    /// <param name="playerQueue">同玩家串行的队列。本对象不释放它。</param>
    public SessionRegistry(PlayerQueue playerQueue)
        : this(playerQueue, DefaultKickBudget, null)
    {
    }

    /// <summary>使用共享玩家队列，并指定踢线预算和日志。</summary>
    /// <param name="playerQueue">同玩家串行的队列。绑定会进入这名玩家的队列；本对象不释放它。</param>
    /// <param name="kickBudget">旧连接通知和等待关闭的合计上限，必须为正。用尽后不再占住玩家队列。</param>
    /// <param name="log">诊断输出。为空时写到控制台。回调在字典锁外调用。</param>
    public SessionRegistry(PlayerQueue playerQueue, TimeSpan kickBudget, Action<string>? log)
    {
        if (playerQueue == null)
            throw new ArgumentNullException(nameof(playerQueue));
        if (kickBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(kickBudget), "踢线预算必须为正");

        _queue = playerQueue;
        _kickBudget = kickBudget;
        _log = log ?? (static message => Console.WriteLine(message));
    }

    /// <summary>当前绑定了会话的玩家数。这是在线认证会话，不是玩家缓存条数。</summary>
    public int AuthenticatedPlayerCount
    {
        get
        {
            lock (_gate)
                return _byPlayer.Count;
        }
    }

    /// <summary>当前仍登记为在线的玩家数。</summary>
    internal int MappedPlayerCount
    {
        get
        {
            lock (_gate)
                return _byPlayer.Count;
        }
    }

    /// <summary>会话到玩家的反向索引数量。</summary>
    internal int MappedSessionCount
    {
        get
        {
            lock (_gate)
                return _bySession.Count;
        }
    }

    /// <summary>尚未结束的会话观察数。结束后会从集合删除。</summary>
    internal int WatchCount
    {
        get
        {
            lock (_gate)
                return _watches.Count;
        }
    }

    /// <summary>已失去当前身份、关闭尚未观察结束的会话数。</summary>
    internal int RetiringCount
    {
        get
        {
            lock (_gate)
                return _retiring.Count;
        }
    }

    /// <summary>等待已经登记的观察任务结束。仍在运行的会话会使这个等待一直停着。</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                if (_watches.Count == 0)
                    return;
                pending = _watches.Values.ToArray();
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把连接登记为该玩家的唯一在线会话。同一会话重复绑定不踢自己。
    /// 已经处在该玩家队列动作中时直接执行，避免再次入队死锁。
    /// </summary>
    /// <param name="playerId">要占线的玩家。必须是正数。不从客户端报文读取。</param>
    /// <param name="session">服务端这条连接。已关闭或正在因顶号关闭时拒绝。</param>
    /// <param name="ct">只在改映射前生效。改映射之后取消不回滚新映射，旧连接仍按预算收尾。</param>
    public Task BindAsync(long playerId, ClientSession session, CancellationToken ct)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家编号必须为正数");

        ct.ThrowIfCancellationRequested();
        if (PlayerQueue.IsCurrentPlayer(playerId))
            return BindCoreAsync(playerId, session, ct);

        return _queue.EnqueueAsync(playerId, token => BindCoreAsync(playerId, session, token), ct);
    }

    /// <summary>这名玩家的当前连接是不是这条会话。</summary>
    /// <param name="playerId">要核对的玩家。没有在线会话时返回 false。</param>
    /// <param name="sessionId">服务端会话编号。</param>
    public bool IsCurrent(long playerId, long sessionId)
    {
        lock (_gate)
        {
            return _byPlayer.TryGetValue(playerId, out ClientSession? current)
                && current.SessionId == sessionId
                && _bySession.TryGetValue(sessionId, out long mapped)
                && mapped == playerId;
        }
    }

    /// <summary>按玩家取当前连接。</summary>
    /// <param name="playerId">要查询的玩家。</param>
    /// <param name="session">找到时是当前连接；否则为 null。</param>
    /// <returns>该玩家有当前连接时为 true。</returns>
    public bool TryGetCurrentSession(long playerId, [NotNullWhen(true)] out ClientSession? session)
    {
        lock (_gate)
        {
            if (_byPlayer.TryGetValue(playerId, out ClientSession? current))
            {
                session = current;
                return true;
            }
        }

        session = null;
        return false;
    }

    /// <summary>按服务端会话编号反查玩家。</summary>
    /// <param name="sessionId">服务端会话编号。不采用客户端自行声明的编号。</param>
    /// <param name="playerId">找到时为注册表里的玩家；否则为 0。</param>
    /// <returns>该会话仍是某玩家的当前连接时为 true。</returns>
    public bool TryGetPlayerId(long sessionId, out long playerId)
    {
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out long mapped)
                && _byPlayer.TryGetValue(mapped, out ClientSession? current)
                && current.SessionId == sessionId)
            {
                playerId = mapped;
                return true;
            }
        }

        playerId = 0;
        return false;
    }

    /// <summary>
    /// 用这条服务端连接构造请求上下文。玩家编号只取注册表，未绑定为 0。
    /// </summary>
    /// <param name="session">正在处理的服务端连接。会话编号取自该对象。</param>
    /// <param name="msgId">本帧的正式消息编号。不从正文推断玩家。</param>
    /// <param name="seq">本帧序号。</param>
    public RequestContext CreateRequestContext(ClientSession session, ushort msgId, uint seq)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));

        long playerId = 0;
        if (TryGetPlayerId(session.SessionId, out long mapped))
            playerId = mapped;

        return new RequestContext(session.SessionId, playerId, msgId, seq);
    }

    /// <summary>在玩家队列内完成一次绑定。调用方保证同一玩家不会并发进入这里。</summary>
    /// <param name="playerId">本次占线的玩家。</param>
    /// <param name="session">要成为当前连接的会话。</param>
    /// <param name="ct">改映射前取消则原样抛出，并且不改字典。</param>
    async Task BindCoreAsync(long playerId, ClientSession session, CancellationToken ct)
    {
        ClientSession? retiring = null;
        TaskCompletionSource? watch = null;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (session.IsCloseRequested || session.Completion.IsCompleted)
            {
                UnmapIfMatch(session.SessionId);
                throw new InvalidOperationException("已关闭的会话不能绑定");
            }

            if (_retiring.Contains(session.SessionId))
                throw new InvalidOperationException("正在关闭的会话不能重新绑定");

            bool sameCurrent = _byPlayer.TryGetValue(playerId, out ClientSession? current)
                && current.SessionId == session.SessionId;
            if (!sameCurrent)
            {
                if (_bySession.TryGetValue(session.SessionId, out long previousPlayer) && previousPlayer != playerId)
                {
                    if (_byPlayer.TryGetValue(previousPlayer, out ClientSession? previous)
                        && previous.SessionId == session.SessionId)
                    {
                        _byPlayer.Remove(previousPlayer);
                    }

                    _bySession.Remove(session.SessionId);
                }

                if (_byPlayer.TryGetValue(playerId, out ClientSession? old) && old.SessionId != session.SessionId)
                {
                    _byPlayer.Remove(playerId);
                    _bySession.Remove(old.SessionId);
                    _retiring.Add(old.SessionId);
                    retiring = old;
                }

                _byPlayer[playerId] = session;
                _bySession[session.SessionId] = playerId;
            }

            if (!_watches.ContainsKey(session.SessionId))
            {
                watch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _watches[session.SessionId] = watch.Task;
            }
        }

        if (watch != null)
            Observe(RunWatchAsync(session, watch));

        if (retiring != null)
            await RetireAsync(retiring, playerId).ConfigureAwait(false);
    }

    /// <summary>等会话结束后，仅当反向索引仍是它时才删除。然后丢掉观察项。</summary>
    /// <param name="session">被观察的连接。完成任务是它自己的 Completion。</param>
    /// <param name="done">交给 WhenIdleAsync 的完成源。清理后再完成，避免漏等。</param>
    async Task RunWatchAsync(ClientSession session, TaskCompletionSource done)
    {
        try
        {
            await Task.Yield();
            await session.Completion.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                lock (_gate)
                {
                    UnmapIfMatch(session.SessionId);
                    _retiring.Remove(session.SessionId);
                    if (_watches.TryGetValue(session.SessionId, out Task? task) && ReferenceEquals(task, done.Task))
                        _watches.Remove(session.SessionId);
                }
            }
            finally
            {
                done.TrySetResult();
            }
        }
    }

    /// <summary>锁外发送正式踢线并关闭。失败或超时只记日志，不把新映射撤掉。</summary>
    /// <param name="oldSession">已经从当前映射移除的旧连接。</param>
    /// <param name="playerId">这次绑定的玩家，只写入日志。</param>
    async Task RetireAsync(ClientSession oldSession, long playerId)
    {
        Log($"玩家 {playerId} 的旧会话 {oldSession.SessionId} 已失去当前身份。KickNtf 与关闭等待预算 {_kickBudget.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} 毫秒。超时或失败不保证通知送达，仍会关闭旧连接。");
        using var budget = new CancellationTokenSource(_kickBudget);
        try
        {
            await oldSession.SendAsync(CreateKickPacket(), budget.Token).ConfigureAwait(false);
            Log($"玩家 {playerId} 的旧会话 {oldSession.SessionId} 已写入 KickNtf（KickElsewhere，seq=0）。写入完成不代表对端已经处理。");
        }
        catch (Exception ex)
        {
            Log($"玩家 {playerId} 的旧会话 {oldSession.SessionId} 的 KickNtf 未能确认送达（{ex.GetType().Name}）。不保证对方收到，仍关闭旧连接。");
        }

        try
        {
            await oldSession.CloseAsync("顶号", budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"玩家 {playerId} 的旧会话 {oldSession.SessionId} 在预算内没有完成关闭（{ex.GetType().Name}）。关闭已经发起，玩家队列不再等待。");
        }
    }

    /// <summary>
    /// 仅当该玩家的当前映射仍是这条会话时，同时删除玩家索引和会话索引。
    /// 已经换成其他会话时不改任何映射。必须在该玩家的队列动作里调用。
    /// </summary>
    /// <param name="playerId">登出上下文里的玩家。必须是正数。</param>
    /// <param name="sessionId">要解除的服务端会话编号。只删除仍指向它的映射。</param>
    public void UnbindIfCurrent(long playerId, long sessionId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家编号必须为正数");
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException("只能在该玩家的队列内按身份解绑");

        lock (_gate)
        {
            if (!_byPlayer.TryGetValue(playerId, out ClientSession? current) || current.SessionId != sessionId)
                return;
            if (!_bySession.TryGetValue(sessionId, out long mapped) || mapped != playerId)
                return;

            _byPlayer.Remove(playerId);
            _bySession.Remove(sessionId);
            _retiring.Add(sessionId);
        }
    }

    /// <summary>调用方必须已经持有字典锁。只删除仍然指向这个会话编号的索引。</summary>
    /// <param name="sessionId">完成或被拒绝的服务端会话编号。</param>
    void UnmapIfMatch(long sessionId)
    {
        if (!_bySession.TryGetValue(sessionId, out long playerId))
            return;

        _bySession.Remove(sessionId);
        if (_byPlayer.TryGetValue(playerId, out ClientSession? current) && current.SessionId == sessionId)
            _byPlayer.Remove(playerId);
    }

    /// <summary>组装正式踢线。原因使用生成枚举的顶号值，序号固定为 0。</summary>
    static Packet CreateKickPacket()
    {
        var notification = new KickNtf { Reason = KickReason.KickElsewhere };
        return new Packet(MsgRegistry.GetId(typeof(KickNtf)), 0, notification.ToByteArray());
    }

    /// <summary>观察后台任务，避免未观察异常。</summary>
    /// <param name="task">会话观察任务。</param>
    static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>写诊断。回调异常不影响关闭。</summary>
    /// <param name="message">已经写好的说明，不含令牌或密码。</param>
    void Log(string message)
    {
        try
        {
            _log("[SessionRegistry] " + message);
        }
        catch (Exception)
        {
        }
    }
}
