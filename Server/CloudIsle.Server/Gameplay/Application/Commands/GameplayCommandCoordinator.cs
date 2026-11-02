using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Clock;
using CloudIsle.Domain.Commands;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Gameplay.Infrastructure.Commands;
using CloudIsle.Server.Players;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 写命令统一入口：现有 PlayerQueue 入队一次；队内查账本/受理/规则/原子提交。
/// 不发网络；受理后 completion 不受 admissionCt 取消。
/// 规则求值传入 observed.DeepCopy()，权威 observed 保持只读。
/// 未注册 kind：Lookup 后对未记录新写 → 504 NotAccepted（不消费 op）；已记账仍重放。
/// 成功变更将冻结 Delta 写入 cmdresult-v2（Harvest 单地块 / 批量摘要用 v3），与 state/receipt 同事务；
/// 批量全失败亦可 v3 摘要且 revision 不变；重放零规则/RNG。
/// 新写/受理前同 lane 必要日切；Query/replay 不跑日切。有效 now 在进入同步 DB 前捕获一次。
/// </summary>
public sealed class GameplayCommandCoordinator
{
    /// <summary>未实现/未注册模块：受理前拒绝，不写账本、不推进 next。</summary>
    public const int CodeModuleUnavailable = 504;

    readonly PlayerQueue _queue;
    readonly SessionRegistry _sessions;
    readonly PlayerStore _players;
    readonly CommandReceiptRepository _receipts;
    readonly IGameplayRuleEvaluator _rules;
    readonly GameplayCommitCore _commit;
    readonly GameConfigSnapshot? _config;
    readonly Func<long> _utcNowMs;
    readonly IGameClock? _clock;
    readonly SystemWriteCoordinator? _systemWrites;
    readonly int _hostConfigVersion;

    public GameplayCommandCoordinator(
        PlayerQueue queue,
        SessionRegistry sessions,
        PlayerStore players,
        CommandReceiptRepository receipts,
        IGameplayRuleEvaluator rules,
        GameplayCommitCore commit,
        int hostConfigVersion,
        GameConfigSnapshot? config = null,
        Func<long>? utcNowMs = null,
        IGameClock? clock = null,
        SystemWriteCoordinator? systemWrites = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        if (hostConfigVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(hostConfigVersion));
        _hostConfigVersion = hostConfigVersion;
        _config = config;
        if (_config != null && _config.Version != hostConfigVersion)
            throw new ArgumentException("config snapshot version must match hostConfigVersion");
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _clock = clock;
        _systemWrites = systemWrites;
    }

    /// <summary>只入队一次；admissionCt 仅在入队/受理前生效。</summary>
    public async Task<CommandOutcome> ExecuteAsync(
        SessionIdentity caller,
        CommandEnvelope command,
        CancellationToken admissionCt)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (command.Key.PlayerId != caller.PlayerId)
            throw new ArgumentException("envelope playerId must match caller");

        admissionCt.ThrowIfCancellationRequested();
        CommandOutcome? outcome = null;
        await _queue.EnqueueAsync(caller.PlayerId, async laneCt =>
        {
            outcome = await ExecuteInLaneAsync(caller, command, admissionCt).ConfigureAwait(false);
            _ = laneCt;
        }, admissionCt).ConfigureAwait(false);

        return outcome ?? throw new InvalidOperationException("command produced no outcome");
    }

    /// <summary>断言已在 lane；供同 lane 系统路径、发布器或测试直接调用。</summary>
    public Task<CommandOutcome> ExecuteInLaneAsync(
        SessionIdentity caller,
        CommandEnvelope command,
        CancellationToken admissionCt = default)
    {
        if (!PlayerQueue.IsCurrentPlayer(caller.PlayerId))
            throw new InvalidOperationException("ExecuteInLaneAsync requires player lane ownership");
        return ExecuteInLaneCoreAsync(caller, command, admissionCt);
    }

    async Task<CommandOutcome> ExecuteInLaneCoreAsync(
        SessionIdentity caller,
        CommandEnvelope command,
        CancellationToken admissionCt)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (command.Key.PlayerId != caller.PlayerId)
            throw new ArgumentException("envelope playerId must match caller");

        if (!_sessions.IsCurrent(caller.PlayerId, caller.SessionId))
            return NotAccepted(102, "caller session is not current", hasNext: false, 0, false);

        // 先捕获有效 now（可能 await DB 抬 floor）；此后同步事务不再 await 时钟。
        long now = await CaptureNowAsync().ConfigureAwait(false);

        // 先查账本：已记账（含规则后来下线）必须可重放；504 只挡未记录的新写。不跑日切。
        CommandKey key = command.Key;
        CommandHash hash = CanonicalCommandHasher.Compute(command);

        ReceiptLookup lookup = await _receipts
            .LookupAsync(key, hash, now, CancellationToken.None)
            .ConfigureAwait(false);

        switch (lookup.Kind)
        {
            case ReceiptLookupKind.RecordedMatched:
                return Replay(lookup, now);
            case ReceiptLookupKind.PayloadConflict:
                return Conflict112(lookup);
            case ReceiptLookupKind.Expired:
                return Expired111(lookup);
            case ReceiptLookupKind.ScopeInvalid:
                return Foreign110();
            case ReceiptLookupKind.OpGap:
                return NotAccepted(101, "op gap", hasNext: true, lookup.NextOpId, lookup.ScopeOpen);
            case ReceiptLookupKind.NotRecorded:
                break;
            default:
                throw new InvalidOperationException("unknown lookup kind");
        }

        if (!lookup.ScopeOpen)
            return NotAccepted(110, "closed scope cannot admit new write", hasNext: true, lookup.NextOpId, false);
        if (key.OpId != lookup.NextOpId)
            return NotAccepted(101, "op is not next", hasNext: true, lookup.NextOpId, lookup.ScopeOpen);

        // 未实现模块：仅拒绝新写受理；不消费 op、无回执。保留未知规则 504 契约。
        if (!_rules.IsRegistered(command.Kind))
            return NotAccepted(CodeModuleUnavailable, "module not implemented: " + command.Kind,
                hasNext: false, 0, false);

        if (admissionCt.IsCancellationRequested)
            return NotAccepted(105, "admission cancelled", hasNext: true, lookup.NextOpId, lookup.ScopeOpen);

        if (key.OpId == long.MaxValue)
            return NotAccepted(101, "op overflow", hasNext: true, lookup.NextOpId, lookup.ScopeOpen);

        // 同 lane 必要日切（在外部命令前）；系统 Batch 元数据先行。
        IReadOnlyList<PlayerDeltaBatchNtf> preceding = Array.Empty<PlayerDeltaBatchNtf>();
        if (_systemWrites != null)
        {
            DailyLaneResult day = await _systemWrites
                .ReconcileDailyInLaneAsync(caller.PlayerId, now, CancellationToken.None)
                .ConfigureAwait(false);
            if (day.Applied && day.SystemDelta != null)
                preceding = new[] { day.SystemDelta };
        }

        PlayerState observed = await _players.LoadAsync(caller.PlayerId, CancellationToken.None)
            .ConfigureAwait(false);

        if (command.ExpectedRevision != observed.Revision)
        {
            return await CommitRecordedRefusalAsync(
                caller, command, hash, observed, code: 109, now, lookup.NextOpId, "revision conflict",
                precedingSystemDeltas: preceding)
                .ConfigureAwait(false);
        }

        if (command.ConfigVersion != _hostConfigVersion)
        {
            return await CommitRecordedRefusalAsync(
                caller, command, hash, observed, code: 109, now, lookup.NextOpId, "config conflict",
                precedingSystemDeltas: preceding)
                .ConfigureAwait(false);
        }

        var evalCtx = new CommandEvalContext(_hostConfigVersion, now, _config);
        RuleEvaluation eval;
        try
        {
            eval = _rules.Evaluate(observed.DeepCopy(), command, evalCtx);
        }
        catch
        {
            throw;
        }

        int ruleInvocations = 1;
        int rng = eval.RngCalls;

        if (eval.Code != 0 && eval.MutatesAssets)
            throw new InvalidOperationException("rule returned nonzero code with MutatesAssets");

        if (!eval.MutatesAssets)
        {
            return await CommitRecordedRefusalAsync(
                    caller, command, hash, observed, eval.Code, now, lookup.NextOpId, eval.Message,
                    ruleInvocations, rng, preceding, eval.FrozenOperationSummary)
                .ConfigureAwait(false);
        }

        PlayerState candidate = eval.Candidate!.DeepCopy();
        if (candidate.PlayerId != caller.PlayerId)
            throw new InvalidOperationException("rule candidate playerId mismatch");

        long newRev;
        try { newRev = checked(observed.Revision + 1); }
        catch (OverflowException)
        {
            return NotAccepted(101, "revision overflow", hasNext: true, lookup.NextOpId, lookup.ScopeOpen);
        }

        PlayerFullData data = candidate.Data.Clone();
        data.Revision = newRev;
        // 必须带上规则候选完整 ShopPurchases；漏传默认 Empty 会清空 Buy 已算好的限购 sidecar，
        // 亦使后续 Sell/Expand 提交把已持久计数冲成空（不得靠规则 SQL 补写/二次 Compose）。
        candidate = new PlayerState(
            candidate.PlayerId, data, newRev, observed.SavedRevision,
            candidate.ShopPurchases, candidate.AcceptedTasks);

        PlayerDeltaBatchNtf delta = GameplayProjectionMapper.ToDelta(
            observed, candidate, key.ScopeId, key.OpId, now, command.ConfigVersion)
            ?? throw new InvalidOperationException("mutating commit must produce a delta");
        PlayerFullData projection = GameplayProjectionMapper.ToFullClone(candidate);
        byte[]? opSummary = eval.FrozenOperationSummary;
        byte[] blob = opSummary != null && opSummary.Length > 0
            ? CommandResultArchive.EncodeV3(
                command.Kind, eval.Code, newRev, command.ConfigVersion, projection, delta, opSummary)
            : CommandResultArchive.EncodeV2(
                command.Kind, eval.Code, newRev, command.ConfigVersion, projection, delta);
        var receipt = new ReceiptWriteSpec(eval.Code, newRev, command.ConfigVersion, blob, now);
        var advance = new ScopeAdvanceSpec(key.ScopeId, key.OpId, hash);
        var request = new CommitRequest(
            caller.PlayerId, observed.Revision, candidate, advance, receipt, systemGuard: null, now);

        CommitOutcome committed = await _commit.CommitAsync(request, CancellationToken.None)
            .ConfigureAwait(false);

        return new CommandOutcome(
            CommandDisposition.Recorded,
            transportCode: eval.Code,
            nextOpId: committed.NextOpId,
            scopeOpen: committed.ScopeOpen,
            hasAuthoritativeNext: true,
            details: committed.ReceiptDetails,
            isReplay: false,
            ruleInvocations: ruleInvocations,
            rngCalls: rng,
            message: committed.PostCommitFault ? "post-commit fault; durable" : eval.Message,
            frozenDelta: delta,
            precedingSystemDeltas: preceding);
    }

    async Task<long> CaptureNowAsync()
    {
        if (_clock != null)
            return await _clock.GetEffectiveNowMsAsync(CancellationToken.None).ConfigureAwait(false);
        return _utcNowMs();
    }

    async Task<CommandOutcome> CommitRecordedRefusalAsync(
        SessionIdentity caller,
        CommandEnvelope command,
        CommandHash hash,
        PlayerState observed,
        int code,
        long now,
        long currentNext,
        string message,
        int ruleInvocations = 0,
        int rng = 0,
        IReadOnlyList<PlayerDeltaBatchNtf>? precedingSystemDeltas = null,
        byte[]? frozenOperationSummary = null)
    {
        PlayerState candidate = observed.DeepCopy();
        // T034 全失败批量：code0 + farm-batch-summary-v1，revision/资产不变；单项拒绝仍 EncodeV2。
        byte[] blob = frozenOperationSummary != null && frozenOperationSummary.Length > 0
            ? CommandResultArchive.EncodeV3(
                command.Kind, code, observed.Revision, command.ConfigVersion,
                projection: null, frozenDelta: null, operationSummary: frozenOperationSummary)
            : CommandResultArchive.EncodeV2(
                command.Kind, code, observed.Revision, command.ConfigVersion, projection: null, frozenDelta: null);
        var receipt = new ReceiptWriteSpec(code, observed.Revision, command.ConfigVersion, blob, now);
        var advance = new ScopeAdvanceSpec(command.Key.ScopeId, command.Key.OpId, hash);
        var request = new CommitRequest(
            caller.PlayerId, observed.Revision, candidate, advance, receipt, null, now);

        CommitOutcome committed = await _commit.CommitAsync(request, CancellationToken.None)
            .ConfigureAwait(false);

        return new CommandOutcome(
            CommandDisposition.Recorded,
            transportCode: code,
            nextOpId: committed.NextOpId,
            scopeOpen: committed.ScopeOpen,
            hasAuthoritativeNext: true,
            details: committed.ReceiptDetails,
            isReplay: false,
            ruleInvocations: ruleInvocations,
            rngCalls: rng,
            message: message,
            frozenDelta: null,
            precedingSystemDeltas: precedingSystemDeltas);
    }

    static CommandOutcome Replay(ReceiptLookup lookup, long nowMs)
    {
        ReceiptDetails details = lookup.Details
            ?? throw new InvalidOperationException("RecordedMatched missing details");
        PlayerDeltaBatchNtf? delta = TryLoadReplayDelta(details, nowMs, out bool needsResync);
        string? msg = needsResync
            ? "legacy archive lacks durable delta; client must SyncState"
            : null;
        return new CommandOutcome(
            CommandDisposition.Recorded,
            transportCode: details.Code,
            nextOpId: lookup.NextOpId,
            scopeOpen: lookup.ScopeOpen,
            hasAuthoritativeNext: true,
            details: details,
            isReplay: true,
            ruleInvocations: 0,
            rngCalls: 0,
            message: msg,
            frozenDelta: delta,
            requiresClientResync: needsResync);
    }

    static PlayerDeltaBatchNtf? TryLoadReplayDelta(ReceiptDetails details, long nowMs, out bool requiresClientResync)
    {
        requiresClientResync = false;
        if (details.Code != 0)
            return null;

        if (!CommandResultArchive.TryGetReplayableDelta(
                details.ResultBlob, out PlayerDeltaBatchNtf? archived, out requiresClientResync))
            return null;

        if (archived == null)
            return null;

        PlayerDeltaBatchNtf replay = archived.Clone();
        replay.ServerTime = nowMs;
        return replay;
    }

    static CommandOutcome Conflict112(ReceiptLookup lookup)
        => new(CommandDisposition.Recorded, 112, lookup.NextOpId, lookup.ScopeOpen, true, null, false, 0, 0, "payload conflict");

    static CommandOutcome Expired111(ReceiptLookup lookup)
        => new(CommandDisposition.Expired, 111, lookup.NextOpId, lookup.ScopeOpen, true, null, false, 0, 0, "expired");

    static CommandOutcome Foreign110()
        => new(CommandDisposition.NotAccepted, 110, 0, false, false, null, false, 0, 0, "foreign scope");

    static CommandOutcome NotAccepted(int code, string msg, bool hasNext, long next, bool open)
        => new(CommandDisposition.NotAccepted, code, next, open, hasNext, null, false, 0, 0, msg);
}
