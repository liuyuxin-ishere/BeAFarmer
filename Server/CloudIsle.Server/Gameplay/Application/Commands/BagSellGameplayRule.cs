using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Commands;
using CloudIsle.Domain.Effects;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Trade;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 正式 BagSell：严格解码规范业务字节 → TradePolicy.Sell → Composer 恰好一次 → 模板保留映射。
/// 无 SQL/网络/再入队/自增 revision；业务失败 mutatesAssets=false（Coordinator 记账拒绝并推进 scope）。
/// </summary>
public sealed class BagSellGameplayRule : IGameplayRule
{
    public const string KindName = "BagSell";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形（空列表/解码尾垃圾/超限/非正/重复等）。</summary>
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

        // 用户输入畸形（长度/尾垃圾/超限/截断等）→ 公开 101；不改纯 Policy 的 BadPacket=103。
        if (!TryDecodeSellIntent(command.CopyCanonicalBusinessPayload(), out SellIntent intent, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);
        var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config);
        DomainResult<PlayerAggregate> composed = TradeCommandApplication.Evaluate(
            source, TradeCommand.Sell(intent), cmdCtx);
        if (!composed.Success)
        {
            return new RuleEvaluation(
                MapPublishedTradeFailure(composed), mutatesAssets: false, candidate: null, rngCalls: 0,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "sell refused");
        }

        // 模板保留：unowned 字段/槽位/未知 protobuf/grant 元数据/商店计数/accepted。禁止对成功候选再改。
        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(composed.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId, nextData, observed.Revision, observed.SavedRevision,
            composed.Candidate!.ShopPurchases, composed.Candidate!.AcceptedTasks);
        return new RuleEvaluation(0, mutatesAssets: true, candidate, rngCalls: 0, message: "ok");
    }

    /// <summary>
    /// 正式公开码：空/超限/非正/重复身份等参数类 BadPacket → 101；
    /// 金币溢出等账本语义仍保持 BadPacket=103。纯 <see cref="TradePolicy"/> 码不变。
    /// </summary>
    internal static int MapPublishedTradeFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;

        IReadOnlyList<string> diagnostics = result.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, TradePolicy.DiagEmptyIntent, StringComparison.Ordinal)
                || string.Equals(d, TradePolicy.DiagOversizedIntent, StringComparison.Ordinal)
                || string.Equals(d, TradePolicy.DiagNonPositiveLine, StringComparison.Ordinal)
                || string.Equals(d, TradePolicy.DiagDuplicateIdentity, StringComparison.Ordinal))
            {
                return PublishedParamError;
            }
        }

        return result.Code;
    }

    /// <summary>兼容纯 Policy 失败码映射（夹具直接测 TradeResult）。</summary>
    internal static int MapPublishedTradeFailure(TradeResult trade)
    {
        if (trade == null)
            throw new ArgumentNullException(nameof(trade));
        if (trade.Code != ErrorCodes.BadPacket)
            return trade.Code;

        IReadOnlyList<string> diagnostics = trade.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, TradePolicy.DiagEmptyIntent, StringComparison.Ordinal)
                || string.Equals(d, TradePolicy.DiagOversizedIntent, StringComparison.Ordinal)
                || string.Equals(d, TradePolicy.DiagNonPositiveLine, StringComparison.Ordinal)
                || string.Equals(d, TradePolicy.DiagDuplicateIdentity, StringComparison.Ordinal))
            {
                return PublishedParamError;
            }
        }

        return trade.Code;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodeBagSell</c> 对称：tag items + count + id/count 对；拒尾随垃圾。
    /// </summary>
    internal static bool TryDecodeSellIntent(byte[] payload, out SellIntent intent, out string message)
    {
        intent = new SellIntent(Array.Empty<SellLine>());
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag) || !string.Equals(tag, "items", StringComparison.Ordinal))
            {
                message = "missing items tag";
                return false;
            }

            if (!TryReadI32(ms, out int count) || count < 0)
            {
                message = "bad items count";
                return false;
            }

            // 与 Inventory MaxDeltaEntries / ItemRules.MaxSellEntries 对齐；超限在 Policy 再拒亦可，解码侧先挡畸形大包。
            if (count > ItemRules.DefaultMaxSellEntries)
            {
                message = "items count oversized";
                return false;
            }

            var lines = new List<SellLine>(count);
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (!TryReadI32(ms, out int id) || !TryReadI32(ms, out int n))
                {
                    message = "truncated item pair";
                    return false;
                }

                if (id <= 0 || n <= 0)
                {
                    message = "nonpositive id/count";
                    return false;
                }

                if (!seen.Add(id))
                {
                    message = "duplicate item_id";
                    return false;
                }

                lines.Add(new SellLine(id, n));
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            intent = new SellIntent(lines);
            return true;
        }
        catch (Exception ex)
        {
            message = "decode failed: " + ex.Message;
            return false;
        }
    }

    static bool TryReadTag(Stream s, out string tag)
    {
        tag = "";
        if (!TryReadI32(s, out int len) || len < 0 || len > 1024)
            return false;
        if (s.Length - s.Position < len)
            return false;
        var buf = new byte[len];
        int read = s.Read(buf, 0, len);
        if (read != len)
            return false;
        tag = Encoding.UTF8.GetString(buf);
        return true;
    }

    static bool TryReadI32(Stream s, out int value)
    {
        value = 0;
        if (s.Length - s.Position < 4)
            return false;
        int b0 = s.ReadByte();
        int b1 = s.ReadByte();
        int b2 = s.ReadByte();
        int b3 = s.ReadByte();
        if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
            return false;
        value = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        return true;
    }
}
