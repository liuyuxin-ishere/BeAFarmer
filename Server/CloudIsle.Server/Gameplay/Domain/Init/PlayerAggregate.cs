using System;
using System.Collections.Generic;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Trade;

namespace CloudIsle.Domain.Init
{
    /// <summary>玩家来源标记（已有 M1 域 vs 新号）。</summary>
    public enum PlayerOrigin
    {
        NewAccount = 0,
        MigratedM1 = 1,
    }

    /// <summary>地块域状态（不依赖 Proto 枚举）。</summary>
    public enum DomainPlotState
    {
        Locked = 0,
        Empty = 1,
        Growing = 2,
        Ripe = 3,
        Unknown = 99,
    }

    public readonly struct BagItemView
    {
        public BagItemView(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
    }

    public readonly struct PlotStateView
    {
        public PlotStateView(int plotId, DomainPlotState state, int cropId, long plantTime, long readyTime, bool watered, bool fertilized)
        {
            PlotId = plotId;
            State = state;
            CropId = cropId;
            PlantTime = plantTime;
            ReadyTime = readyTime;
            Watered = watered;
            Fertilized = fertilized;
        }

        public int PlotId { get; }
        public DomainPlotState State { get; }
        public int CropId { get; }
        public long PlantTime { get; }
        public long ReadyTime { get; }
        public bool Watered { get; }
        public bool Fertilized { get; }
    }

    public readonly struct NodeStateView
    {
        public NodeStateView(int nodeId, int configNodeId, long nextReadyTime)
        {
            NodeId = nodeId;
            ConfigNodeId = configNodeId;
            NextReadyTime = nextReadyTime;
        }

        public int NodeId { get; }
        public int ConfigNodeId { get; }
        public long NextReadyTime { get; }
    }

    public readonly struct ToolStateView
    {
        public ToolStateView(int toolId, int level)
        {
            ToolId = toolId;
            Level = level;
        }

        public int ToolId { get; }
        public int Level { get; }
    }

    public readonly struct FacilityStateView
    {
        /// <param name="configFacilityId">
        /// 配置类型 ID。Proto 无可靠来源时必须为 0（未解析）；
        /// 不得把 instanceId / FacilityId 当作 typeId。
        /// </param>
        public FacilityStateView(int facilityId, int configFacilityId, int level)
        {
            FacilityId = facilityId;
            ConfigFacilityId = configFacilityId;
            Level = level;
        }

        public int FacilityId { get; }
        /// <summary>未解析时为 0；勿等同实例 ID。</summary>
        public int ConfigFacilityId { get; }
        public int Level { get; }
    }

    /// <summary>
    /// 服务端最小纯领域玩家聚合（初始化 + 后续 T011 扩展）。
    /// 无 Proto/Unity/Socket/SQL。
    /// </summary>
    public sealed class PlayerAggregate
    {
        public PlayerAggregate(
            long playerId,
            string nickname,
            int level,
            int exp,
            long gold,
            long gem,
            long ticket,
            long shard,
            int bagSlots,
            int guideStep,
            int grantVersion,
            long revision,
            IReadOnlyList<PlotStateView> plots,
            IReadOnlyList<NodeStateView> nodes,
            IReadOnlyList<ToolStateView> tools,
            IReadOnlyList<FacilityStateView> facilities,
            IReadOnlyList<BagItemView> bag,
            bool ordersInitializedEmpty,
            ShopPurchaseCounts? shopPurchases = null,
            AcceptedTaskState? acceptedTasks = null)
        {
            PlayerId = playerId;
            Nickname = nickname ?? "";
            Level = level;
            Exp = exp;
            Gold = gold;
            Gem = gem;
            Ticket = ticket;
            Shard = shard;
            BagSlots = bagSlots;
            GuideStep = guideStep;
            GrantVersion = grantVersion;
            Revision = revision;
            Plots = FreezePlots(plots);
            Nodes = FreezeNodes(nodes);
            Tools = FreezeTools(tools);
            Facilities = FreezeFacilities(facilities);
            Bag = FreezeBag(bag);
            OrdersInitializedEmpty = ordersInitializedEmpty;
            ShopPurchases = shopPurchases?.Clone() ?? ShopPurchaseCounts.Empty;
            AcceptedTasks = acceptedTasks?.Clone() ?? AcceptedTaskState.Empty;
        }

        public long PlayerId { get; }
        public string Nickname { get; }
        public int Level { get; }
        public int Exp { get; }
        public long Gold { get; }
        public long Gem { get; }
        public long Ticket { get; }
        public long Shard { get; }
        public int BagSlots { get; }
        public int GuideStep { get; }
        public int GrantVersion { get; }
        public long Revision { get; }
        public IReadOnlyList<PlotStateView> Plots { get; }
        public IReadOnlyList<NodeStateView> Nodes { get; }
        public IReadOnlyList<ToolStateView> Tools { get; }
        public IReadOnlyList<FacilityStateView> Facilities { get; }
        public IReadOnlyList<BagItemView> Bag { get; }
        /// <summary>T008 仅空订单态/marker；不调用订单生成。</summary>
        public bool OrdersInitializedEmpty { get; }

        /// <summary>当日商店购买计数 sidecar（typed；不进 Proto）。</summary>
        public ShopPurchaseCounts ShopPurchases { get; }

        /// <summary>已接受任务规格 sidecar（typed；不进 Proto）。</summary>
        public AcceptedTaskState AcceptedTasks { get; }

        /// <summary>返回携带新商店计数的不可变副本（日切/购买合成用）。</summary>
        public PlayerAggregate WithShopPurchases(ShopPurchaseCounts purchases)
        {
            if (purchases == null)
                throw new ArgumentNullException(nameof(purchases));
            return new PlayerAggregate(
                PlayerId, Nickname, Level, Exp, Gold, Gem, Ticket, Shard, BagSlots, GuideStep,
                GrantVersion, Revision, Plots, Nodes, Tools, Facilities, Bag,
                OrdersInitializedEmpty, purchases, AcceptedTasks);
        }

        /// <summary>返回携带新已接受任务规格的不可变副本。</summary>
        public PlayerAggregate WithAcceptedTasks(AcceptedTaskState tasks)
        {
            if (tasks == null)
                throw new ArgumentNullException(nameof(tasks));
            return new PlayerAggregate(
                PlayerId, Nickname, Level, Exp, Gold, Gem, Ticket, Shard, BagSlots, GuideStep,
                GrantVersion, Revision, Plots, Nodes, Tools, Facilities, Bag,
                OrdersInitializedEmpty, ShopPurchases, tasks);
        }

        public IReadOnlyDictionary<int, int> ToolLevelsById()
        {
            var map = new Dictionary<int, int>(Tools.Count);
            for (int i = 0; i < Tools.Count; i++)
                map[Tools[i].ToolId] = Tools[i].Level;
            return map;
        }

        static IReadOnlyList<PlotStateView> FreezePlots(IReadOnlyList<PlotStateView> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<PlotStateView>();
            var arr = new PlotStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<NodeStateView> FreezeNodes(IReadOnlyList<NodeStateView> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<NodeStateView>();
            var arr = new NodeStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<ToolStateView> FreezeTools(IReadOnlyList<ToolStateView> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<ToolStateView>();
            var arr = new ToolStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<FacilityStateView> FreezeFacilities(IReadOnlyList<FacilityStateView> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<FacilityStateView>();
            var arr = new FacilityStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<BagItemView> FreezeBag(IReadOnlyList<BagItemView> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<BagItemView>();
            var arr = new BagItemView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }
}
