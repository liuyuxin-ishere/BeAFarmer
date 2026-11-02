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
/// 正式 ShopBuy：严格解码规范业务字节 → TradePolicy.Buy → Composer 恰好一次 → 模板保留 + 商店计数 sidecar。
/// 无 SQL/网络/再入队/自增 revision；业务失败 mutatesAssets=false。
/// </summary>
public sealed class ShopBuyGameplayRule : IGameplayRule
{
    public const string KindName = "ShopBuy";

    /// <summary>05 ERR_PARAM：正式公开面用户输入畸形。</summary>
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

        if (!TryDecodeBuyIntent(command.CopyCanonicalBusinessPayload(), out BuyIntent intent, out string decodeMsg))
        {
            return new RuleEvaluation(
                PublishedParamError, mutatesAssets: false, candidate: null, rngCalls: 0,
                message: decodeMsg);
        }

        PlayerAggregate source = PlayerFullDataInitMapper.ToAggregate(
            observed.Data, observed.ShopPurchases, observed.AcceptedTasks);
        var cmdCtx = new CommandContext(ctx.NowMs, ctx.Config);
        DomainResult<PlayerAggregate> composed = TradeCommandApplication.Evaluate(
            source, TradeCommand.Buy(intent), cmdCtx);
        if (!composed.Success)
        {
            return new RuleEvaluation(
                MapPublishedBuyFailure(composed), mutatesAssets: false, candidate: null, rngCalls: 0,
                message: composed.Diagnostics.Count > 0 ? composed.Diagnostics[0] : "buy refused");
        }

        PlayerFullData nextData = PlayerFullDataInitMapper.ToFullData(composed.Candidate!, observed.Data);
        var candidate = new PlayerState(
            observed.PlayerId,
            nextData,
            observed.Revision,
            observed.SavedRevision,
            composed.Candidate!.ShopPurchases,
            composed.Candidate!.AcceptedTasks);
        return new RuleEvaluation(0, mutatesAssets: true, candidate, rngCalls: 0, message: "ok");
    }

    /// <summary>
    /// 正式公开码：数量/解码畸形 → 101；未知/限购 → 703；等级 → 108；货币不足保持 303/304/…；
    /// 纯 Policy BadPacket 参数类映射 101。
    /// </summary>
    internal static int MapPublishedBuyFailure(DomainResult<PlayerAggregate> result)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.Code != ErrorCodes.BadPacket)
            return result.Code;

        IReadOnlyList<string> diagnostics = result.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string d = diagnostics[i];
            if (string.Equals(d, TradePolicy.DiagBuyNonPositiveCount, StringComparison.Ordinal))
                return PublishedParamError;
        }

        return result.Code;
    }

    /// <summary>
    /// 严格解码与 <c>CanonicalBusinessEncoder.EncodeShopBuy</c> 对称：
    /// tag shop_item_id + i32 + tag count + i32；拒尾随垃圾；拒未知价字段注入。
    /// </summary>
    internal static bool TryDecodeBuyIntent(byte[] payload, out BuyIntent intent, out string message)
    {
        intent = new BuyIntent(0, 0);
        message = "";
        if (payload == null)
        {
            message = "null payload";
            return false;
        }

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            if (!TryReadTag(ms, out string tag1) || !string.Equals(tag1, "shop_item_id", StringComparison.Ordinal))
            {
                message = "missing shop_item_id tag";
                return false;
            }

            if (!TryReadI32(ms, out int shopId))
            {
                message = "bad shop_item_id";
                return false;
            }

            if (!TryReadTag(ms, out string tag2) || !string.Equals(tag2, "count", StringComparison.Ordinal))
            {
                message = "missing count tag";
                return false;
            }

            if (!TryReadI32(ms, out int count))
            {
                message = "bad count";
                return false;
            }

            if (ms.Position != ms.Length)
            {
                message = "trailing garbage";
                return false;
            }

            if (shopId <= 0 || count <= 0)
            {
                message = "nonpositive shop_item_id/count";
                return false;
            }

            intent = new BuyIntent(shopId, count);
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
