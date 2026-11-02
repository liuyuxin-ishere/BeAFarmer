using System;
using CloudIsle.Config;
using CloudIsle.Domain.Commands;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>规则求值上下文：注入配置版本与时钟；无网络/SQL。</summary>
public sealed class CommandEvalContext
{
    public CommandEvalContext(int configVersion, long nowMs, GameConfigSnapshot? config)
    {
        if (configVersion < 1) throw new ArgumentOutOfRangeException(nameof(configVersion));
        if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs));
        ConfigVersion = configVersion;
        NowMs = nowMs;
        Config = config;
    }

    public int ConfigVersion { get; }
    public long NowMs { get; }
    public GameConfigSnapshot? Config { get; }
}

/// <summary>
/// 规则结果：业务码、是否变更资产、候选快照（变更时）、RNG 计数。
/// 可选冻结业务摘要（Harvest/Gather cmdresult-v3 / 批量 farm-batch-summary-v1）。
/// 全失败批量允许 MutatesAssets=false 仍携带摘要（无变更 Recorded 重放）。Candidate 每次 get DeepCopy。
/// </summary>
public sealed class RuleEvaluation
{
    readonly PlayerState? _candidate;
    readonly byte[]? _frozenOperationSummary;

    public RuleEvaluation(
        int code,
        bool mutatesAssets,
        PlayerState? candidate,
        int rngCalls,
        string? message = null,
        byte[]? frozenOperationSummary = null)
    {
        Code = code;
        MutatesAssets = mutatesAssets;
        RngCalls = rngCalls < 0 ? throw new ArgumentOutOfRangeException(nameof(rngCalls)) : rngCalls;
        Message = message ?? "";
        if (mutatesAssets && candidate == null)
            throw new ArgumentException("mutating rule must provide candidate", nameof(candidate));
        if (!mutatesAssets && candidate != null)
            throw new ArgumentException("non-mutating rule must not provide candidate", nameof(candidate));
        // T034：无资产变更的批量 Recorded 仍可带 farm-batch-summary-v1；单项拒绝保持无摘要。
        _candidate = candidate?.DeepCopy();
        if (frozenOperationSummary == null || frozenOperationSummary.Length == 0)
            _frozenOperationSummary = null;
        else
        {
            _frozenOperationSummary = new byte[frozenOperationSummary.Length];
            Buffer.BlockCopy(frozenOperationSummary, 0, _frozenOperationSummary, 0, frozenOperationSummary.Length);
        }
    }

    public int Code { get; }
    public bool MutatesAssets { get; }
    public PlayerState? Candidate => _candidate?.DeepCopy();
    public int RngCalls { get; }
    public string Message { get; }
    /// <summary>Harvest/批量等操作冻结摘要副本；无则为 null。每次 get 独立拷贝。</summary>
    public byte[]? FrozenOperationSummary
    {
        get
        {
            if (_frozenOperationSummary == null)
                return null;
            var copy = new byte[_frozenOperationSummary.Length];
            Buffer.BlockCopy(_frozenOperationSummary, 0, copy, 0, _frozenOperationSummary.Length);
            return copy;
        }
    }
}

public interface IGameplayRule
{
    string Kind { get; }
    RuleEvaluation Evaluate(PlayerState observed, CommandEnvelope command, CommandEvalContext ctx);
}

public interface IGameplayRuleEvaluator
{
    /// <summary>是否已注册可执行规则（未注册 → 受理前 504 NotAccepted，不消费 op）。</summary>
    bool IsRegistered(string kind);

    RuleEvaluation Evaluate(PlayerState observed, CommandEnvelope command, CommandEvalContext ctx);
}

/// <summary>
/// 显式 kind→规则表。未注册 kind 不得静默成功；Coordinator 在账本 Lookup 之后、新写受理前用 IsRegistered 短路。
/// 无 SQL/网络；非批量规则一次求值。
/// </summary>
public sealed class GameplayRuleDispatcher : IGameplayRuleEvaluator
{
    readonly Dictionary<string, IGameplayRule> _rules;

    public GameplayRuleDispatcher(IEnumerable<IGameplayRule>? rules = null)
    {
        _rules = new Dictionary<string, IGameplayRule>(StringComparer.Ordinal);
        if (rules == null) return;
        foreach (IGameplayRule rule in rules)
        {
            if (rule == null) throw new ArgumentException("null rule");
            if (!_rules.TryAdd(rule.Kind, rule))
                throw new ArgumentException("duplicate rule kind: " + rule.Kind);
        }
    }

    public void Register(IGameplayRule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        if (!_rules.TryAdd(rule.Kind, rule))
            throw new InvalidOperationException("duplicate rule kind: " + rule.Kind);
    }

    /// <summary>测试/热下线：移除 kind 后新写 504，已记账仍可经 Coordinator Lookup 重放。</summary>
    public bool Unregister(string kind)
        => !string.IsNullOrEmpty(kind) && _rules.Remove(kind);

    public bool IsRegistered(string kind)
        => !string.IsNullOrEmpty(kind) && _rules.ContainsKey(kind);

    public RuleEvaluation Evaluate(PlayerState observed, CommandEnvelope command, CommandEvalContext ctx)
    {
        if (observed == null) throw new ArgumentNullException(nameof(observed));
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));

        if (!_rules.TryGetValue(command.Kind, out IGameplayRule? rule))
        {
            // 防御：正式路径应已 IsRegistered 短路为 504 NotAccepted。
            return new RuleEvaluation(
                code: 504,
                mutatesAssets: false,
                candidate: null,
                rngCalls: 0,
                message: "no rule registered for kind: " + command.Kind);
        }

        return rule.Evaluate(observed, command, ctx);
    }
}
