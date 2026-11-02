using CloudIsle.Net;
using CloudIsle.Proto.Greybox;
using CloudIsle.Server.Data;

namespace CloudIsle.Server.Greybox;

public sealed class GreyboxService
{
    readonly GreyboxWorld _world = new();
    readonly PlayerDb _db;

    public GreyboxService(PlayerDb db) => _db = db;

    public GreyboxPlayerState Ensure(long playerId)
    {
        var rec = _db.FindById(playerId);
        return _world.GetOrCreate(playerId, rec.Gold);
    }

    public void ResetForLogin(long playerId)
    {
        // 灰盒每次登录重置为可验收初始态（种子9、金100）
        _db.SetGold(playerId, 100);
        _world.Reset(playerId, 100);
    }

    public GreyboxSnapshotNtf BuildSnapshot(GreyboxPlayerState st)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        st.RefreshRipe(now);
        var ntf = new GreyboxSnapshotNtf
        {
            ServerTimeMs = now,
            SeedCount = st.SeedCount,
            WheatCount = st.WheatCount,
            FlourCount = st.FlourCount,
            Gold = st.Gold,
            Exp = st.Exp,
            SlotState = (int)st.SlotState,
            SlotFinishMs = st.SlotFinishMs,
            OrderCompleted = st.OrderCompleted ? 1 : 0
        };
        for (int i = 0; i < st.Plots.Length; i++)
        {
            ntf.PlotStates.Add((int)st.Plots[i].State);
            ntf.PlotReadyMs.Add(st.Plots[i].ReadyTimeMs);
        }
        return ntf;
    }

    public PlantResp Plant(long playerId, PlantReq req)
    {
        var st = Ensure(playerId);
        if (TryIdempotent(st, req.OpId, out var cached))
            return PlantResp.ParseFrom(cached);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        st.RefreshRipe(now);

        if (req.PlotId < 0 || req.PlotId >= GreyboxIds.PlotCount)
            return Cache(st, req.OpId, new PlantResp { Code = ErrorCodes.BadPacket, SeedCount = st.SeedCount });
        if (req.SeedId != GreyboxIds.WheatSeedId)
            return Cache(st, req.OpId, new PlantResp { Code = ErrorCodes.BadPacket, SeedCount = st.SeedCount });
        if (st.SeedCount <= 0)
            return Cache(st, req.OpId, new PlantResp { Code = ErrorCodes.ItemNotEnough, SeedCount = st.SeedCount });
        var plot = st.Plots[req.PlotId];
        if (plot.State != PlotStateKind.Empty)
            return Cache(st, req.OpId, new PlantResp
            {
                Code = ErrorCodes.PlotNotEmpty,
                PlotId = req.PlotId,
                State = (int)plot.State,
                ReadyTimeMs = plot.ReadyTimeMs,
                SeedCount = st.SeedCount
            });

        st.SeedCount--;
        plot.State = PlotStateKind.Growing;
        plot.ReadyTimeMs = now + GreyboxIds.GrowSec * 1000L;

        return Cache(st, req.OpId, new PlantResp
        {
            Code = ErrorCodes.Ok,
            PlotId = req.PlotId,
            State = (int)plot.State,
            ReadyTimeMs = plot.ReadyTimeMs,
            SeedCount = st.SeedCount
        });
    }

    public HarvestResp Harvest(long playerId, HarvestReq req)
    {
        var st = Ensure(playerId);
        if (TryIdempotent(st, req.OpId, out var cached))
            return HarvestResp.ParseFrom(cached);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        st.RefreshRipe(now);

        if (req.PlotId < 0 || req.PlotId >= GreyboxIds.PlotCount)
            return Cache(st, req.OpId, new HarvestResp { Code = ErrorCodes.BadPacket, WheatCount = st.WheatCount });

        var plot = st.Plots[req.PlotId];
        if (plot.State != PlotStateKind.Ripe)
            return Cache(st, req.OpId, new HarvestResp
            {
                Code = ErrorCodes.CropNotRipe,
                PlotId = req.PlotId,
                State = (int)plot.State,
                WheatCount = st.WheatCount
            });

        st.WheatCount += GreyboxIds.HarvestYield;
        plot.State = PlotStateKind.Empty;
        plot.ReadyTimeMs = 0;

        return Cache(st, req.OpId, new HarvestResp
        {
            Code = ErrorCodes.Ok,
            PlotId = req.PlotId,
            State = (int)plot.State,
            WheatGained = GreyboxIds.HarvestYield,
            WheatCount = st.WheatCount
        });
    }

    public CraftStartResp CraftStart(long playerId, CraftStartReq req)
    {
        var st = Ensure(playerId);
        if (TryIdempotent(st, req.OpId, out var cached))
            return CraftStartResp.ParseFrom(cached);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        st.RefreshRipe(now);

        if (req.FacilityId != GreyboxIds.MillFacilityId || req.RecipeId != GreyboxIds.FlourRecipeId)
            return Cache(st, req.OpId, new CraftStartResp { Code = ErrorCodes.BadPacket, WheatCount = st.WheatCount });
        if (st.SlotState != SlotStateKind.Idle)
            return Cache(st, req.OpId, new CraftStartResp
            {
                Code = ErrorCodes.SlotBusy,
                State = (int)st.SlotState,
                FinishTimeMs = st.SlotFinishMs,
                WheatCount = st.WheatCount
            });
        if (st.WheatCount < GreyboxIds.RecipeWheatCost)
            return Cache(st, req.OpId, new CraftStartResp
            {
                Code = ErrorCodes.ItemNotEnough,
                State = (int)st.SlotState,
                WheatCount = st.WheatCount
            });

        st.WheatCount -= GreyboxIds.RecipeWheatCost;
        st.SlotState = SlotStateKind.Working;
        st.SlotFinishMs = now + GreyboxIds.CraftSec * 1000L;

        return Cache(st, req.OpId, new CraftStartResp
        {
            Code = ErrorCodes.Ok,
            State = (int)st.SlotState,
            FinishTimeMs = st.SlotFinishMs,
            WheatCount = st.WheatCount
        });
    }

    public CraftCollectResp CraftCollect(long playerId, CraftCollectReq req)
    {
        var st = Ensure(playerId);
        if (TryIdempotent(st, req.OpId, out var cached))
            return CraftCollectResp.ParseFrom(cached);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        st.RefreshRipe(now);

        if (st.SlotState != SlotStateKind.Done)
            return Cache(st, req.OpId, new CraftCollectResp
            {
                Code = ErrorCodes.SlotNotDone,
                FlourCount = st.FlourCount,
                State = (int)st.SlotState
            });

        st.FlourCount += 1;
        st.SlotState = SlotStateKind.Idle;
        st.SlotFinishMs = 0;

        return Cache(st, req.OpId, new CraftCollectResp
        {
            Code = ErrorCodes.Ok,
            FlourGained = 1,
            FlourCount = st.FlourCount,
            State = (int)st.SlotState
        });
    }

    public OrderSubmitResp OrderSubmit(long playerId, OrderSubmitReq req)
    {
        var st = Ensure(playerId);
        if (TryIdempotent(st, req.OpId, out var cached))
            return OrderSubmitResp.ParseFrom(cached);

        if (req.OrderId != GreyboxIds.OrderId)
            return Cache(st, req.OpId, new OrderSubmitResp
            {
                Code = ErrorCodes.OrderInvalid,
                Gold = st.Gold,
                Exp = st.Exp,
                FlourCount = st.FlourCount,
                OrderCompleted = st.OrderCompleted ? 1 : 0
            });
        if (st.OrderCompleted)
            return Cache(st, req.OpId, new OrderSubmitResp
            {
                Code = ErrorCodes.OrderInvalid,
                Gold = st.Gold,
                Exp = st.Exp,
                FlourCount = st.FlourCount,
                OrderCompleted = 1
            });
        if (st.FlourCount < GreyboxIds.OrderFlourNeed)
            return Cache(st, req.OpId, new OrderSubmitResp
            {
                Code = ErrorCodes.ItemNotEnough,
                Gold = st.Gold,
                Exp = st.Exp,
                FlourCount = st.FlourCount,
                OrderCompleted = 0
            });

        st.FlourCount -= GreyboxIds.OrderFlourNeed;
        st.Gold += GreyboxIds.OrderGoldReward;
        st.Exp += GreyboxIds.OrderExpReward;
        st.OrderCompleted = true;

        // 持久化金币
        long delta = GreyboxIds.OrderGoldReward;
        _db.AddGold(playerId, delta);
        st.Gold = _db.FindById(playerId).Gold;

        return Cache(st, req.OpId, new OrderSubmitResp
        {
            Code = ErrorCodes.Ok,
            Gold = st.Gold,
            Exp = st.Exp,
            FlourCount = st.FlourCount,
            OrderCompleted = 1
        });
    }

    static bool TryIdempotent(GreyboxPlayerState st, long opId, out byte[] body)
    {
        if (opId != 0 && st.Idempotent.TryGetValue(opId, out body!))
            return true;
        body = Array.Empty<byte>();
        return false;
    }

    static T Cache<T>(GreyboxPlayerState st, long opId, T resp) where T : class
    {
        if (opId == 0) return resp;
        byte[] bytes = resp switch
        {
            PlantResp p => p.ToByteArray(),
            HarvestResp h => h.ToByteArray(),
            CraftStartResp c => c.ToByteArray(),
            CraftCollectResp c => c.ToByteArray(),
            OrderSubmitResp o => o.ToByteArray(),
            _ => Array.Empty<byte>()
        };
        if (bytes.Length > 0)
            st.Idempotent[opId] = bytes;
        if (st.Idempotent.Count > 64)
        {
            var first = st.Idempotent.Keys.First();
            st.Idempotent.Remove(first);
        }
        return resp;
    }
}
