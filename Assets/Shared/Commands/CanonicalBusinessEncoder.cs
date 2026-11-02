using System;
using System.Collections.Generic;
using CloudIsle.Proto;
using Google.Protobuf;

namespace CloudIsle.Commands
{
    /// <summary>
    /// 19 种写命令业务载荷规范化（排序批、拒绝重复身份、未知 wire 已由调用方拒）。
    /// 输出不可变字节；供服务端信封与客户端 Pending 共用同一黄金摘要。
    /// </summary>
    public static class CanonicalBusinessEncoder
    {
        static readonly string[] WriteKindsCore =
        {
            "BagSell", "BagExpand", "GuideStep",
            "PlotUnlock", "Plant", "Harvest", "Water", "Fertilize",
            "Gather", "ToolUpgrade",
            "FacilityBuild", "FacilityUpgrade", "CraftStart", "CraftCollect", "CraftSpeedup",
            "OrderSubmit", "OrderReroll", "OrderMilestoneClaim",
            "ShopBuy"
        };

        public static IReadOnlyList<string> WriteKinds { get; } = Array.AsReadOnly(WriteKindsCore);

        public static bool IsWriteKind(string kind)
        {
            for (int i = 0; i < WriteKindsCore.Length; i++)
                if (string.Equals(WriteKindsCore[i], kind, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>编码规范业务字节；先拒未知 wire。</summary>
        public static byte[] Encode(string kind, IMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!IsWriteKind(kind))
                throw new ArgumentException("not a formal write kind: " + kind, nameof(kind));
            WireFieldGuard.RejectUnknownWireFields(request);
            return kind switch
            {
                "BagSell" => EncodeBagSell(Require<BagSellReq>(kind, request)),
                "BagExpand" => Empty(),
                "GuideStep" => EncodeGuideStep(Require<GuideStepReq>(kind, request)),
                "PlotUnlock" => Empty(),
                "Plant" => EncodePlant(Require<PlantReq>(kind, request)),
                "Harvest" => EncodeHarvest(Require<HarvestReq>(kind, request)),
                "Water" => EncodeWater(Require<WaterReq>(kind, request)),
                "Fertilize" => EncodeFertilize(Require<FertilizeReq>(kind, request)),
                "Gather" => EncodeGather(Require<GatherReq>(kind, request)),
                "ToolUpgrade" => EncodeToolUpgrade(Require<ToolUpgradeReq>(kind, request)),
                "FacilityBuild" => EncodeFacilityBuild(Require<FacilityBuildReq>(kind, request)),
                "FacilityUpgrade" => EncodeFacilityUpgrade(Require<FacilityUpgradeReq>(kind, request)),
                "CraftStart" => EncodeCraftStart(Require<CraftStartReq>(kind, request)),
                "CraftCollect" => EncodeCraftCollect(Require<CraftCollectReq>(kind, request)),
                "CraftSpeedup" => EncodeCraftSpeedup(Require<CraftSpeedupReq>(kind, request)),
                "OrderSubmit" => EncodeOrderSubmit(Require<OrderSubmitReq>(kind, request)),
                "OrderReroll" => EncodeOrderReroll(Require<OrderRerollReq>(kind, request)),
                "OrderMilestoneClaim" => EncodeOrderMilestone(Require<OrderMilestoneClaimReq>(kind, request)),
                "ShopBuy" => EncodeShopBuy(Require<ShopBuyReq>(kind, request)),
                _ => throw new ArgumentException("unhandled kind: " + kind, nameof(kind))
            };
        }

        static byte[] Empty() => new CanonicalBusinessWriter().ToArray();

        static byte[] EncodeBagSell(BagSellReq req)
        {
            var items = new List<(int Id, int Count)>(req.Items.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < req.Items.Count; i++)
            {
                int id = req.Items[i].ItemId;
                int count = req.Items[i].Count;
                // 线字段畸形 → ArgumentException → WriteHandler 101（05 ERR_PARAM）；
                // 与 duplicate 同路径。已编码字节上的非正仍可由解码/Policy 以 BadPacket=103 防御。
                if (id <= 0 || count <= 0)
                    throw new ArgumentException("BagSell nonpositive item_id/count");
                if (!seen.Add(id))
                    throw new ArgumentException("BagSell duplicate item_id");
                items.Add((id, count));
            }
            items.Sort((a, b) => a.Id.CompareTo(b.Id));
            var w = new CanonicalBusinessWriter();
            w.WriteTag("items");
            w.WriteI32(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                w.WriteI32(items[i].Id);
                w.WriteI32(items[i].Count);
            }
            return w.ToArray();
        }

        static byte[] EncodeGuideStep(GuideStepReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("step");
            w.WriteI32(req.Step);
            return w.ToArray();
        }

        static byte[] EncodePlant(PlantReq req)
        {
            var items = new List<(int Plot, int Seed)>(req.Items.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < req.Items.Count; i++)
            {
                int plot = req.Items[i].PlotId;
                if (!seen.Add(plot))
                    throw new ArgumentException("Plant duplicate plot_id");
                items.Add((plot, req.Items[i].SeedId));
            }
            items.Sort((a, b) => a.Plot.CompareTo(b.Plot));
            var w = new CanonicalBusinessWriter();
            w.WriteTag("items");
            w.WriteI32(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                w.WriteI32(items[i].Plot);
                w.WriteI32(items[i].Seed);
            }
            return w.ToArray();
        }

        static byte[] EncodeHarvest(HarvestReq req)
        {
            var ids = new List<int>(req.PlotIds.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < req.PlotIds.Count; i++)
            {
                int id = req.PlotIds[i];
                if (!seen.Add(id))
                    throw new ArgumentException("Harvest duplicate plot_id");
                ids.Add(id);
            }
            ids.Sort();
            var w = new CanonicalBusinessWriter();
            w.WriteTag("plot_ids");
            w.WriteI32(ids.Count);
            for (int i = 0; i < ids.Count; i++)
                w.WriteI32(ids[i]);
            return w.ToArray();
        }

        static byte[] EncodeWater(WaterReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("plot_id");
            w.WriteI32(req.PlotId);
            return w.ToArray();
        }

        static byte[] EncodeFertilize(FertilizeReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("plot_id");
            w.WriteI32(req.PlotId);
            return w.ToArray();
        }

        static byte[] EncodeGather(GatherReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("node_id");
            w.WriteI32(req.NodeId);
            return w.ToArray();
        }

        static byte[] EncodeToolUpgrade(ToolUpgradeReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("tool_id");
            w.WriteI32(req.ToolId);
            return w.ToArray();
        }

        static byte[] EncodeFacilityBuild(FacilityBuildReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("facility_id");
            w.WriteI32(req.FacilityId);
            return w.ToArray();
        }

        static byte[] EncodeFacilityUpgrade(FacilityUpgradeReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("facility_id");
            w.WriteI32(req.FacilityId);
            return w.ToArray();
        }

        static byte[] EncodeCraftStart(CraftStartReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("facility_id"); w.WriteI32(req.FacilityId);
            w.WriteTag("slot_id"); w.WriteI32(req.SlotId);
            w.WriteTag("recipe_id"); w.WriteI32(req.RecipeId);
            return w.ToArray();
        }

        static byte[] EncodeCraftCollect(CraftCollectReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("facility_id"); w.WriteI32(req.FacilityId);
            w.WriteTag("slot_id"); w.WriteI32(req.SlotId);
            return w.ToArray();
        }

        static byte[] EncodeCraftSpeedup(CraftSpeedupReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("facility_id"); w.WriteI32(req.FacilityId);
            w.WriteTag("slot_id"); w.WriteI32(req.SlotId);
            return w.ToArray();
        }

        static byte[] EncodeOrderSubmit(OrderSubmitReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("order_id");
            w.WriteI32(req.OrderId);
            return w.ToArray();
        }

        static byte[] EncodeOrderReroll(OrderRerollReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("order_id");
            w.WriteI32(req.OrderId);
            return w.ToArray();
        }

        static byte[] EncodeOrderMilestone(OrderMilestoneClaimReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("milestone");
            w.WriteI32(req.Milestone);
            return w.ToArray();
        }

        static byte[] EncodeShopBuy(ShopBuyReq req)
        {
            var w = new CanonicalBusinessWriter();
            w.WriteTag("shop_item_id"); w.WriteI32(req.ShopItemId);
            w.WriteTag("count"); w.WriteI32(req.Count);
            return w.ToArray();
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
}
