using System;
using System.Collections.Generic;
using CloudIsle.Proto;
using Google.Protobuf;

namespace Game.Framework.Gameplay
{
    /// <summary>命令描述：固定 Req/Resp 类型与 MsgId；不开放任意 TypeName。</summary>
    public sealed class CommandDescriptor
    {
        public CommandDescriptor(
            string key,
            MsgId requestId,
            MsgId responseId,
            MessageParser requestParser,
            MessageParser responseParser,
            bool isWriteCommand)
        {
            Key = key;
            RequestId = requestId;
            ResponseId = responseId;
            RequestParser = requestParser;
            ResponseParser = responseParser;
            IsWriteCommand = isWriteCommand;
        }

        public string Key { get; }
        public MsgId RequestId { get; }
        public MsgId ResponseId { get; }
        public MessageParser RequestParser { get; }
        public MessageParser ResponseParser { get; }
        public bool IsWriteCommand { get; }
    }

    /// <summary>M2 正式请求允许列表。</summary>
    public static class GameplayCommandAllowlist
    {
        static readonly Dictionary<string, CommandDescriptor> Map;

        static GameplayCommandAllowlist()
        {
            var list = new[]
            {
                D("BagSell", MsgId.BagSellReq, MsgId.BagSellResp, BagSellReq.Parser, BagSellResp.Parser, true),
                D("BagExpand", MsgId.BagExpandReq, MsgId.BagExpandResp, BagExpandReq.Parser, BagExpandResp.Parser, true),
                D("GuideStep", MsgId.GuideStepReq, MsgId.GuideStepResp, GuideStepReq.Parser, GuideStepResp.Parser, true),
                D("SyncState", MsgId.SyncStateReq, MsgId.SyncStateResp, SyncStateReq.Parser, SyncStateResp.Parser, false),
                D("CommandResultQuery", MsgId.CommandResultQueryReq, MsgId.CommandResultQueryResp, CommandResultQueryReq.Parser, CommandResultQueryResp.Parser, false),
                D("PlotUnlock", MsgId.PlotUnlockReq, MsgId.PlotUnlockResp, PlotUnlockReq.Parser, PlotUnlockResp.Parser, true),
                D("Plant", MsgId.PlantReq, MsgId.PlantResp, PlantReq.Parser, PlantResp.Parser, true),
                D("Harvest", MsgId.HarvestReq, MsgId.HarvestResp, HarvestReq.Parser, HarvestResp.Parser, true),
                D("Water", MsgId.WaterReq, MsgId.WaterResp, WaterReq.Parser, WaterResp.Parser, true),
                D("Fertilize", MsgId.FertilizeReq, MsgId.FertilizeResp, FertilizeReq.Parser, FertilizeResp.Parser, true),
                D("Gather", MsgId.GatherReq, MsgId.GatherResp, GatherReq.Parser, GatherResp.Parser, true),
                D("ToolUpgrade", MsgId.ToolUpgradeReq, MsgId.ToolUpgradeResp, ToolUpgradeReq.Parser, ToolUpgradeResp.Parser, true),
                D("FacilityBuild", MsgId.FacilityBuildReq, MsgId.FacilityBuildResp, FacilityBuildReq.Parser, FacilityBuildResp.Parser, true),
                D("FacilityUpgrade", MsgId.FacilityUpgradeReq, MsgId.FacilityUpgradeResp, FacilityUpgradeReq.Parser, FacilityUpgradeResp.Parser, true),
                D("CraftStart", MsgId.CraftStartReq, MsgId.CraftStartResp, CraftStartReq.Parser, CraftStartResp.Parser, true),
                D("CraftCollect", MsgId.CraftCollectReq, MsgId.CraftCollectResp, CraftCollectReq.Parser, CraftCollectResp.Parser, true),
                D("CraftSpeedup", MsgId.CraftSpeedupReq, MsgId.CraftSpeedupResp, CraftSpeedupReq.Parser, CraftSpeedupResp.Parser, true),
                D("OrderList", MsgId.OrderListReq, MsgId.OrderListResp, OrderListReq.Parser, OrderListResp.Parser, false),
                D("OrderSubmit", MsgId.OrderSubmitReq, MsgId.OrderSubmitResp, OrderSubmitReq.Parser, OrderSubmitResp.Parser, true),
                D("OrderReroll", MsgId.OrderRerollReq, MsgId.OrderRerollResp, OrderRerollReq.Parser, OrderRerollResp.Parser, true),
                D("OrderMilestoneClaim", MsgId.OrderMilestoneClaimReq, MsgId.OrderMilestoneClaimResp, OrderMilestoneClaimReq.Parser, OrderMilestoneClaimResp.Parser, true),
                D("ShopBuy", MsgId.ShopBuyReq, MsgId.ShopBuyResp, ShopBuyReq.Parser, ShopBuyResp.Parser, true),
            };
            Map = new Dictionary<string, CommandDescriptor>(StringComparer.Ordinal);
            for (int i = 0; i < list.Length; i++)
                Map.Add(list[i].Key, list[i]);
        }

        static CommandDescriptor D(
            string key,
            MsgId req,
            MsgId resp,
            MessageParser reqP,
            MessageParser respP,
            bool write)
        {
            return new CommandDescriptor(key, req, resp, reqP, respP, write);
        }

        public static IReadOnlyCollection<string> Keys => Map.Keys;

        public static bool IsAllowed(string commandKey)
        {
            return !string.IsNullOrEmpty(commandKey) && Map.ContainsKey(commandKey);
        }

        public static bool TryGet(string commandKey, out CommandDescriptor descriptor)
        {
            return Map.TryGetValue(commandKey, out descriptor);
        }

        public static CommandDescriptor Get(string commandKey)
        {
            if (!Map.TryGetValue(commandKey, out CommandDescriptor d))
                throw new KeyNotFoundException("unknown-command:" + commandKey);
            return d;
        }
    }
}
