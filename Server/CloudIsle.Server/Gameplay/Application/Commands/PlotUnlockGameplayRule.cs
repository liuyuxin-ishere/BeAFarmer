using System;
using System.Collections.Generic;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 正式 PlotUnlock：空规范业务载荷 → FarmCommandApplication.Evaluate(Unlock) → 模板保留 + ShopPurchases sidecar。
/// 无 SQL/网络/再入队/自增 revision；业务失败 mutatesAssets=false。
/// </summary>
public sealed class PlotUnlockGameplayRule : IGameplayRule
{
    public const string KindName = "PlotUnlock";

    /// <summary>05 ERR_PARAM：上限/非规范 legacy 等无专有公开码时使用。</summary>
    public const int PublishedParamError = 101;

    public string Kind => KindName;

    public RuleEvaluation Evaluate(PlayerState observed, CommandEnvelope command, CommandEvalContext ctx)
    {
        if (observed == null)
            throw new ArgumentNullException(nameof(observed));
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (ctx == null)
            throw new ArgumentNullException(nameof(ctx));
        if (!string.Equals(command.Kind, KindName, StringComparison.Ordinal))
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: "kind mismatch");
        }

        if (ctx.Config == null)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: "config required");
        }

        // PlotUnlockReq 仅 op 元数据；规范业务字节为空；拒尾随垃圾。
        byte[] payload = command.CopyCanonicalBusinessPayload();
        if (payload == null || payload.Length != 0)
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: payload == null ? "null payload" : "trailing garbage");
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);
        DomainResult<PlayerAggregate> composed;
        try
        {
            var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config);
            composed = FarmCommandApplication.Evaluate(source, FarmCommand.Unlock(), cmdCtx);
        }
        catch (InvalidOperationException ex)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new RuleEvaluation(
                ErrorCodes.BadPacket, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: ex.Message);
        }

        if (!composed.Success)
        {
            return new RuleEvaluation(
                MapPublishedUnlockFailure(composed), mutatesAssets: false, candidate: null, rngCalls: 0,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "unlock refused");
        }

        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(composed.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId, nextData, observed.Revision, observed.SavedRevision,
            composed.Candidate!.ShopPurchases, composed.Candidate!.AcceptedTasks);
        return new RuleEvaluation(0, mutatesAssets: true, candidate, rngCalls: 0, message: "ok");
    }

    /// <summary>
    /// 正式公开码：上限/非规范 BadPacket → 101；等级 108；金币不足 303；
    /// 纯 Policy 码不变。不发明 cap 专码。
    /// </summary>
    internal static int MapPublishedUnlockFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;

        IReadOnlyList<string> diagnostics = result.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, FarmStatePolicy.DiagUnlockAtCap, StringComparison.Ordinal)
                || string.Equals(d, FarmStatePolicy.DiagUnlockNonCanonical, StringComparison.Ordinal)
                || string.Equals(d, FarmStatePolicy.DiagUnlockMissingPlot, StringComparison.Ordinal)
                || string.Equals(d, FarmStatePolicy.DiagUnlockTargetNotLocked, StringComparison.Ordinal)
                || string.Equals(d, FarmStatePolicy.DiagUnlockMalformed, StringComparison.Ordinal)
                || string.Equals(d, FarmStatePolicy.DiagUnlockGoldOverflow, StringComparison.Ordinal))
            {
                return PublishedParamError;
            }
        }

        return result.Code;
    }
}
