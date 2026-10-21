namespace CloudIsle.Net
{
    /// <summary>
    /// 与 05_协议与接口.md 对齐的消息 ID（PoC 子集）。
    /// </summary>
    public static class MsgIds
    {
        public const ushort HeartbeatReq = 1001;
        public const ushort HeartbeatResp = 1002;

        public const ushort LoginReq = 1005;
        public const ushort LoginResp = 1006;

        public const ushort PlantReq = 3003;
        public const ushort PlantResp = 3004;
        public const ushort HarvestReq = 3005;
        public const ushort HarvestResp = 3006;

        public const ushort CraftStartReq = 5005;
        public const ushort CraftStartResp = 5006;
        public const ushort CraftCollectReq = 5007;
        public const ushort CraftCollectResp = 5008;

        public const ushort OrderSubmitReq = 6003;
        public const ushort OrderSubmitResp = 6004;

        /// <summary>灰盒快照推送 / 拉取。</summary>
        public const ushort GreyboxSnapshotReq = 3998;
        public const ushort GreyboxSnapshotNtf = 3999;

        public const ushort PlazaEnterReq = 9001;
        public const ushort PlazaEnterResp = 9002;
        public const ushort PlazaLeaveReq = 9003;
        public const ushort PlazaLeaveResp = 9004;
        public const ushort PlazaMoveReq = 9005;
        public const ushort PlazaEntityEnterNtf = 9101;
        public const ushort PlazaEntityLeaveNtf = 9102;
        public const ushort PlazaSyncNtf = 9103;
        public const ushort PlazaCorrectNtf = 9104;

        /// <summary>调试段 12xxx：PoC 写金币。</summary>
        public const ushort PocAddGoldReq = 12001;
        public const ushort PocAddGoldResp = 12002;
    }

    public static class ErrorCodes
    {
        public const int Ok = 0;
        public const int NotLogin = 102;
        public const int BadPacket = 103;
        public const int TooFast = 104;
        public const int Version = 106;
        public const int AuthFailed = 201;
        public const int BagFull = 301;
        public const int ItemNotEnough = 302;
        public const int PlotNotEmpty = 402;
        public const int CropNotRipe = 403;
        public const int SlotBusy = 409;
        public const int SlotNotDone = 411;
        public const int OrderInvalid = 501;
        public const int NotInPlaza = 802;
        public const int PlazaFull = 803;
    }

    /// <summary>灰盒玩法常量（对齐操作说明）。</summary>
    public static class GreyboxIds
    {
        public const int WheatSeedId = 2001;
        public const int WheatItemId = 1001;
        public const int FlourItemId = 1101;
        public const int MillFacilityId = 1;
        public const int FlourRecipeId = 1;
        public const int OrderId = 1;
        public const int PlotCount = 9;
        public const int StartingSeeds = 9;
        public const int HarvestYield = 2;
        public const int RecipeWheatCost = 3;
        public const int OrderFlourNeed = 1;
        public const int OrderGoldReward = 20;
        public const int OrderExpReward = 10;
        public const int GrowSec = 10;
        public const int CraftSec = 8;
    }

    public static class ProtoVersions
    {
        public const int Current = 1;
    }
}
