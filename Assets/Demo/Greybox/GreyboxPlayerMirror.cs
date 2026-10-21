using System;
using CloudIsle.Net;
using CloudIsle.Proto;
using LoginResp = CloudIsle.Proto.Poc.LoginResp;

namespace Game.Demo.Greybox
{
    public enum PlotVisualState { Empty = 0, Growing = 1, Ripe = 2 }
    public enum SlotVisualState { Idle = 0, Working = 1, Done = 2 }

    /// <summary>仅由服务端回包更新的镜像状态。</summary>
    public sealed class GreyboxPlayerMirror
    {
        public long PlayerId;
        public int SeedCount = GreyboxIds.StartingSeeds;
        public int WheatCount;
        public int FlourCount;
        public long Gold = 100;
        public int Exp;
        public bool OrderCompleted;
        public SlotVisualState SlotState;
        public long SlotFinishMs;
        public readonly PlotVisualState[] PlotStates = new PlotVisualState[GreyboxIds.PlotCount];
        public readonly long[] PlotReadyMs = new long[GreyboxIds.PlotCount];
        public long ServerTimeOffsetMs; // serverNow - localNow

        public void ApplyLogin(LoginResp resp)
        {
            if (resp?.Data == null) return;
            PlayerId = resp.Data.PlayerId;
            Gold = resp.Data.Gold;
            foreach (var stack in resp.Data.Bag)
            {
                if (stack.ItemId == GreyboxIds.WheatSeedId) SeedCount = stack.Count;
                if (stack.ItemId == GreyboxIds.WheatItemId) WheatCount = stack.Count;
                if (stack.ItemId == GreyboxIds.FlourItemId) FlourCount = stack.Count;
            }
            AlignServerTime(resp.ServerTime);
        }

        public void ApplySnapshot(GreyboxSnapshotNtf ntf)
        {
            if (ntf == null) return;
            AlignServerTime(ntf.ServerTimeMs);
            SeedCount = ntf.SeedCount;
            WheatCount = ntf.WheatCount;
            FlourCount = ntf.FlourCount;
            Gold = ntf.Gold;
            Exp = ntf.Exp;
            SlotState = (SlotVisualState)ntf.SlotState;
            SlotFinishMs = ntf.SlotFinishMs;
            OrderCompleted = ntf.OrderCompleted != 0;
            for (int i = 0; i < PlotStates.Length && i < ntf.PlotStates.Count; i++)
                PlotStates[i] = (PlotVisualState)ntf.PlotStates[i];
            for (int i = 0; i < PlotReadyMs.Length && i < ntf.PlotReadyMs.Count; i++)
                PlotReadyMs[i] = ntf.PlotReadyMs[i];
        }

        public void ApplyPlant(PlantResp r)
        {
            if (r.Code != ErrorCodes.Ok) return;
            SeedCount = r.SeedCount;
            if (r.PlotId >= 0 && r.PlotId < PlotStates.Length)
            {
                PlotStates[r.PlotId] = (PlotVisualState)r.State;
                PlotReadyMs[r.PlotId] = r.ReadyTimeMs;
            }
        }

        public void ApplyHarvest(HarvestResp r)
        {
            if (r.Code != ErrorCodes.Ok) return;
            WheatCount = r.WheatCount;
            if (r.PlotId >= 0 && r.PlotId < PlotStates.Length)
            {
                PlotStates[r.PlotId] = (PlotVisualState)r.State;
                PlotReadyMs[r.PlotId] = 0;
            }
        }

        public void ApplyCraftStart(CraftStartResp r)
        {
            if (r.Code != ErrorCodes.Ok) return;
            WheatCount = r.WheatCount;
            SlotState = (SlotVisualState)r.State;
            SlotFinishMs = r.FinishTimeMs;
        }

        public void ApplyCraftCollect(CraftCollectResp r)
        {
            if (r.Code != ErrorCodes.Ok) return;
            FlourCount = r.FlourCount;
            SlotState = (SlotVisualState)r.State;
            SlotFinishMs = 0;
        }

        public void ApplyOrder(OrderSubmitResp r)
        {
            if (r.Code != ErrorCodes.Ok) return;
            Gold = r.Gold;
            Exp = r.Exp;
            FlourCount = r.FlourCount;
            OrderCompleted = r.OrderCompleted != 0;
        }

        public long NowServerMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerTimeOffsetMs;
        }

        void AlignServerTime(long serverTimeMs)
        {
            if (serverTimeMs <= 0) return;
            ServerTimeOffsetMs = serverTimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
