using System;
using System.Collections.Generic;
using CloudIsle.Commands;
using CloudIsle.Domain.Commands;
using CloudIsle.Proto;
using Google.Protobuf;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 自正式写请求构造不可变 <see cref="CommandEnvelope"/>。
/// 规范业务字节委托 Shared <see cref="CanonicalBusinessEncoder"/>（与客户端 Pending 同源）。
/// </summary>
public static class CommandEnvelopeFactory
{
    /// <summary>与客户端 Allowlist 写键对齐；只读，不可经 IList 改写。</summary>
    public static IReadOnlyList<string> WriteKinds => CanonicalBusinessEncoder.WriteKinds;

    public static bool IsWriteKind(string kind) => CanonicalBusinessEncoder.IsWriteKind(kind);

    /// <summary>唯一公开构造入口：kind↔类型、未知线字段、规范化业务载荷。</summary>
    public static CommandEnvelope FromRequest(long playerId, string kind, IMessage request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!IsWriteKind(kind))
            throw new ArgumentException("not a formal write kind: " + kind, nameof(kind));

        byte[] business = CanonicalBusinessEncoder.Encode(kind, request);

        return kind switch
        {
            "BagSell" => BuildMeta(playerId, kind, Require<BagSellReq>(kind, request), business),
            "BagExpand" => BuildMeta(playerId, kind, Require<BagExpandReq>(kind, request), business),
            "GuideStep" => BuildMeta(playerId, kind, Require<GuideStepReq>(kind, request), business),
            "PlotUnlock" => BuildMeta(playerId, kind, Require<PlotUnlockReq>(kind, request), business),
            "Plant" => BuildMeta(playerId, kind, Require<PlantReq>(kind, request), business),
            "Harvest" => BuildMeta(playerId, kind, Require<HarvestReq>(kind, request), business),
            "Water" => BuildMeta(playerId, kind, Require<WaterReq>(kind, request), business),
            "Fertilize" => BuildMeta(playerId, kind, Require<FertilizeReq>(kind, request), business),
            "Gather" => BuildMeta(playerId, kind, Require<GatherReq>(kind, request), business),
            "ToolUpgrade" => BuildMeta(playerId, kind, Require<ToolUpgradeReq>(kind, request), business),
            "FacilityBuild" => BuildMeta(playerId, kind, Require<FacilityBuildReq>(kind, request), business),
            "FacilityUpgrade" => BuildMeta(playerId, kind, Require<FacilityUpgradeReq>(kind, request), business),
            "CraftStart" => BuildMeta(playerId, kind, Require<CraftStartReq>(kind, request), business),
            "CraftCollect" => BuildMeta(playerId, kind, Require<CraftCollectReq>(kind, request), business),
            "CraftSpeedup" => BuildMeta(playerId, kind, Require<CraftSpeedupReq>(kind, request), business),
            "OrderSubmit" => BuildMeta(playerId, kind, Require<OrderSubmitReq>(kind, request), business),
            "OrderReroll" => BuildMeta(playerId, kind, Require<OrderRerollReq>(kind, request), business),
            "OrderMilestoneClaim" => BuildMeta(playerId, kind, Require<OrderMilestoneClaimReq>(kind, request), business),
            "ShopBuy" => BuildMeta(playerId, kind, Require<ShopBuyReq>(kind, request), business),
            _ => throw new ArgumentException("unhandled kind: " + kind, nameof(kind))
        };
    }

    /// <summary>委托 Shared 严格未知字段检查（含嵌套）。</summary>
    public static void RejectUnknownWireFields(IMessage message)
        => WireFieldGuard.RejectUnknownWireFields(message);

    static CommandEnvelope BuildMeta(long playerId, string kind, BagSellReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, BagExpandReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, GuideStepReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, PlotUnlockReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, PlantReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, HarvestReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, WaterReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, FertilizeReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, GatherReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, ToolUpgradeReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, FacilityBuildReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, FacilityUpgradeReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, CraftStartReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, CraftCollectReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, CraftSpeedupReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, OrderSubmitReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, OrderRerollReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, OrderMilestoneClaimReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope BuildMeta(long playerId, string kind, ShopBuyReq req, byte[] business)
        => Build(playerId, req.OperationScopeId, req.OpId, kind, req.ExpectedRevision, req.ConfigVersion, business);

    static CommandEnvelope Build(
        long playerId, long scopeId, long opId, string kind, long expectedRevision, int configVersion,
        byte[] business)
    {
        var key = new CommandKey(playerId, scopeId, opId);
        return new CommandEnvelope(key, kind, expectedRevision, configVersion, business);
    }

    static T Require<T>(string kind, IMessage request) where T : class, IMessage
    {
        if (request is T typed)
            return typed;
        throw new ArgumentException(
            "kind " + kind + " requires " + typeof(T).Name + " but got " + request.GetType().Name,
            nameof(request));
    }
}
