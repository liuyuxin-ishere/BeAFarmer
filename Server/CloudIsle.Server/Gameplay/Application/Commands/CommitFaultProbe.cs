using System.Threading;
using CloudIsle.Domain.Commands;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 测试专用提交相位。与 <see cref="CommitFaultPoint"/> 写点对齐，并扩展缓存发布/发送前相位。
/// 生产默认不挂探针。
/// </summary>
public enum CommitPhase
{
    None = 0,
    BeforePlayerUpdate = 1,
    AfterPlayerUpdate = 2,
    BeforeSnapshot = 3,
    AfterSnapshot = 4,
    BeforeReceipt = 5,
    AfterReceipt = 6,
    BeforeScopeAdvance = 7,
    AfterScopeAdvance = 8,
    BeforeCommit = 9,
    /// <summary>SQLite tx.Commit 已返回；DB 可见；尚未返回 CommitOutcome。</summary>
    AfterCommitVisible = 10,
    BeforeCachePublish = 11,
    /// <summary>Coordinator 已返回 Durable；WriteHandler 尚未调用 Publisher.Send。</summary>
    AfterCommitBeforeDeliver = 12,
    /// <summary>Publisher 即将发送 Batch/Resp。</summary>
    BeforeDeliverSend = 13
}

/// <summary>探针动作：默认无操作；Throw/Drop 抛错；BarrierThenHang 通知父测后挂起（供真实 Kill）；BarrierThenContinue 供同进程确定性并发门。</summary>
public enum CommitFaultAction
{
    None = 0,
    Throw = 1,
    /// <summary>抛 IO 异常模拟投递丢失；不得冒充进程强杀。</summary>
    Drop = 2,
    /// <summary>写证据、信号 barrier，再无限挂起直至进程被 Kill。禁止用 throw 冒充。</summary>
    BarrierThenHang = 3,
    /// <summary>写证据、信号 committed，再等待 continue 事件后返回（不 hang）。供 Save/顶号与 COMMIT 确定性交错。</summary>
    BarrierThenContinue = 4
}

/// <summary>
/// 仅夹具注入的提交故障探针。默认关闭；挂在真实 COMMIT/缓存/发送点。
/// 不改变 COMMIT 语义：BeforeCommit 失败仍回滚；AfterCommit* 时 DB 已落盘。
/// </summary>
public sealed class CommitFaultProbe
{
    CommitPhase _armed;
    CommitFaultAction _action;
    string? _barrierPrefix;
    string? _artifactDirectory;
    CommandKey? _matchKey;
    int _hitCount;
    CommitPhase _lastHit;
    int _commitVisible;
    readonly object _gate = new();

    public CommitPhase ArmedPhase
    {
        get { lock (_gate) return _armed; }
    }

    public CommitFaultAction ArmedAction
    {
        get { lock (_gate) return _action; }
    }

    public int HitCount => Volatile.Read(ref _hitCount);
    public CommitPhase LastHit => (CommitPhase)Volatile.Read(ref UnsafeLastHit);
    int UnsafeLastHit;
    public bool CommitWasVisible => Volatile.Read(ref _commitVisible) != 0;
    public string? BarrierPrefix
    {
        get { lock (_gate) return _barrierPrefix; }
    }

    public string? ArtifactDirectory
    {
        get { lock (_gate) return _artifactDirectory; }
    }

    /// <summary>可选精确命令键；Arm 后仅匹配该 key 才触发（登录/系统写不得误伤批量）。</summary>
    public CommandKey? MatchKey
    {
        get { lock (_gate) return _matchKey; }
    }

    /// <summary>
    /// 配置一次故障；每次 Arm 重置当前尝试的提交可见性（HitCount 累计定义不变）。
    /// <paramref name="matchKey"/> 非空时仅命中该 (player,scope,op)。
    /// </summary>
    public void Arm(
        CommitPhase phase,
        CommitFaultAction action,
        string? barrierPrefix = null,
        string? artifactDirectory = null,
        CommandKey? matchKey = null)
    {
        if (phase == CommitPhase.None)
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (action == CommitFaultAction.None)
            throw new ArgumentOutOfRangeException(nameof(action));
        if ((action == CommitFaultAction.BarrierThenHang || action == CommitFaultAction.BarrierThenContinue)
            && string.IsNullOrWhiteSpace(barrierPrefix))
            throw new ArgumentException("Barrier actions require barrierPrefix", nameof(barrierPrefix));

        lock (_gate)
        {
            _armed = phase;
            _action = action;
            _barrierPrefix = barrierPrefix;
            _artifactDirectory = artifactDirectory;
            _matchKey = matchKey;
            // 新尝试不得继承上一轮 AfterCommit* 的可见性。
            Volatile.Write(ref _commitVisible, 0);
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _armed = CommitPhase.None;
            _action = CommitFaultAction.None;
            _barrierPrefix = null;
            _matchKey = null;
        }
    }

    /// <summary>生产路径零开销：未武装直接返回。</summary>
    public void Hit(CommitPhase phase, CommandKey? key = null)
    {
        CommitPhase armed;
        CommitFaultAction action;
        string? barrier;
        string? artifact;
        CommandKey? match;
        lock (_gate)
        {
            armed = _armed;
            action = _action;
            barrier = _barrierPrefix;
            artifact = _artifactDirectory;
            match = _matchKey;
        }

        if (armed == CommitPhase.None || armed != phase || action == CommitFaultAction.None)
            return;
        if (match is { } need)
        {
            if (key is not { } actual || !actual.Equals(need))
                return;
        }

        Interlocked.Increment(ref _hitCount);
        Volatile.Write(ref UnsafeLastHit, (int)phase);
        if (phase is CommitPhase.AfterCommitVisible
            or CommitPhase.BeforeCachePublish
            or CommitPhase.AfterCommitBeforeDeliver
            or CommitPhase.BeforeDeliverSend)
            Volatile.Write(ref _commitVisible, 1);

        bool visible = Volatile.Read(ref _commitVisible) != 0;
        WriteEvidence(artifact, phase, key, action, barrier, visible);

        switch (action)
        {
            case CommitFaultAction.Throw:
                Disarm();
                throw new InvalidOperationException("commit fault probe throw: " + phase);
            case CommitFaultAction.Drop:
                Disarm();
                throw new IOException("commit fault probe drop: " + phase);
            case CommitFaultAction.BarrierThenHang:
                SignalNamed(barrier!, "committed");
                // 确定性挂起：父测试持 Process 核验 PID 后 Kill。禁止 throw/sleep 猜时机。
                HangForever();
                break;
            case CommitFaultAction.BarrierThenContinue:
                SignalNamed(barrier!, "committed");
                WaitNamed(barrier!, "continue");
                Disarm();
                break;
        }
    }

    public static CommitPhase FromStorePoint(CommitFaultPoint point) => point switch
    {
        CommitFaultPoint.None => CommitPhase.None,
        CommitFaultPoint.BeforePlayerUpdate => CommitPhase.BeforePlayerUpdate,
        CommitFaultPoint.AfterPlayerUpdate => CommitPhase.AfterPlayerUpdate,
        CommitFaultPoint.BeforeSnapshot => CommitPhase.BeforeSnapshot,
        CommitFaultPoint.AfterSnapshot => CommitPhase.AfterSnapshot,
        CommitFaultPoint.BeforeReceipt => CommitPhase.BeforeReceipt,
        CommitFaultPoint.AfterReceipt => CommitPhase.AfterReceipt,
        CommitFaultPoint.BeforeScopeAdvance => CommitPhase.BeforeScopeAdvance,
        CommitFaultPoint.AfterScopeAdvance => CommitPhase.AfterScopeAdvance,
        CommitFaultPoint.BeforeCommit => CommitPhase.BeforeCommit,
        CommitFaultPoint.AfterCommit => CommitPhase.AfterCommitVisible,
        _ => CommitPhase.None
    };

    public static string BarrierName(string prefix, string suffix)
        => "Local\\m2fault-" + prefix + "-" + suffix;

    /// <summary>父夹具/外部 harness：等待 worker 已进入已武装相位（命名事件，禁止 sleep 猜）。</summary>
    public static bool TryWaitCommitted(string barrierPrefix, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(barrierPrefix))
            throw new ArgumentException("barrierPrefix required", nameof(barrierPrefix));
        using var handle = new EventWaitHandle(
            false, EventResetMode.ManualReset, BarrierName(barrierPrefix, "committed"));
        return handle.WaitOne(timeout);
    }

    /// <summary>父夹具：释放 BarrierThenContinue 继续点。</summary>
    public static void ReleaseContinue(string barrierPrefix)
    {
        if (string.IsNullOrWhiteSpace(barrierPrefix))
            throw new ArgumentException("barrierPrefix required", nameof(barrierPrefix));
        using var handle = new EventWaitHandle(
            false, EventResetMode.ManualReset, BarrierName(barrierPrefix, "continue"));
        handle.Set();
    }

    /// <summary>父夹具：等待 worker ready 命名事件。</summary>
    public static bool TryWaitReady(string barrierPrefix, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(barrierPrefix))
            throw new ArgumentException("barrierPrefix required", nameof(barrierPrefix));
        using var handle = new EventWaitHandle(
            false, EventResetMode.ManualReset, BarrierName(barrierPrefix, "ready"));
        return handle.WaitOne(timeout);
    }

    static void SignalNamed(string barrierPrefix, string suffix)
    {
        using var handle = new EventWaitHandle(
            false, EventResetMode.ManualReset, BarrierName(barrierPrefix, suffix));
        handle.Set();
    }

    static void WaitNamed(string barrierPrefix, string suffix)
    {
        using var handle = new EventWaitHandle(
            false, EventResetMode.ManualReset, BarrierName(barrierPrefix, suffix));
        if (!handle.WaitOne(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("fault fixture did not release continuation barrier");
    }

    static void HangForever()
    {
        using var never = new ManualResetEventSlim(false);
        never.Wait();
    }

    static void WriteEvidence(
        string? artifactDirectory,
        CommitPhase phase,
        CommandKey? key,
        CommitFaultAction action,
        string? barrier,
        bool commitVisible)
    {
        if (string.IsNullOrWhiteSpace(artifactDirectory))
            return;
        try
        {
            Directory.CreateDirectory(artifactDirectory);
            string path = Path.Combine(artifactDirectory, "barrier-hit.txt");
            string keyLine = key is { } k
                ? $"player={k.PlayerId}\nscope={k.ScopeId}\nop={k.OpId}\ncommandKey={k.PlayerId}:{k.ScopeId}:{k.OpId}\n"
                : "player=\nscope=\nop=\ncommandKey=\n";
            // matched 供外部 harness 布尔核对：须自有 Host PID + 武装相位 + commitVisible + 非空 commandKey。
            // 登录/系统写不得进入 AfterCommitBeforeDeliver；无 key 时 matched=0，禁止仅凭文件存在放行。
            int matched = commitVisible
                          && key is { } ck
                          && ck.PlayerId > 0 && ck.ScopeId > 0 && ck.OpId > 0
                ? 1 : 0;
            string line =
                $"utc={DateTimeOffset.UtcNow:O}\npid={Environment.ProcessId}\nphase={phase}\naction={action}\n" +
                $"barrier={barrier}\ncommitVisible={(commitVisible ? 1 : 0)}\nmatched={matched}\n" +
                keyLine;
            File.WriteAllText(path, line);
            // 可读相位状态（外部 harness 轮询；不替代命名事件 barrier）。
            File.WriteAllText(
                Path.Combine(artifactDirectory, "phase-status.txt"),
                $"phase={phase}\naction={action}\ncommitVisible={(commitVisible ? 1 : 0)}\nmatched={matched}\n" +
                $"pid={Environment.ProcessId}\nutc={DateTimeOffset.UtcNow:O}\n" + keyLine);
        }
        catch
        {
            // 证据写失败不得掩盖真实故障相位
        }
    }
}
