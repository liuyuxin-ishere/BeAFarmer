using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace CloudIsle.Config
{
    /// <summary>商店行（只读投影，不暴露 Luban 可变 List）。</summary>
    public sealed class ShopRow
    {
        public ShopRow(int id, string category, int itemId, int priceCurrency, int price, int limitCount, int unlockLevel)
        {
            Id = id;
            Category = category ?? throw new ArgumentNullException(nameof(category));
            ItemId = itemId;
            PriceCurrency = priceCurrency;
            Price = price;
            LimitCount = limitCount;
            UnlockLevel = unlockLevel;
        }

        public int Id { get; }
        public string Category { get; }
        public int ItemId { get; }
        public int PriceCurrency { get; }
        public int Price { get; }
        public int LimitCount { get; }
        public int UnlockLevel { get; }
    }

    /// <summary>地图实例行（稳定 instanceId；坐标单位为米，与 Demo 一致，非协议 cm）。</summary>
    public sealed class MapInstanceRow
    {
        public MapInstanceRow(int instanceId, string kind, int typeId, float posX, float posY, float posZ, string label)
        {
            InstanceId = instanceId;
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            TypeId = typeId;
            PosX = posX;
            PosY = posY;
            PosZ = posZ;
            Label = label ?? "";
        }

        public int InstanceId { get; }
        public string Kind { get; }
        public int TypeId { get; }
        public float PosX { get; }
        public float PosY { get; }
        public float PosZ { get; }
        public string Label { get; }
    }

    /// <summary>地块解锁阶段行（只读投影；阶段价按次收取，不按地块乘价）。</summary>
    public sealed class PlotStageRow
    {
        public PlotStageRow(int stage, int needPlayerLevel, int totalPlots, int unlockGold)
        {
            if (stage < 0)
                throw new ArgumentOutOfRangeException(nameof(stage));
            if (needPlayerLevel <= 0)
                throw new ArgumentOutOfRangeException(nameof(needPlayerLevel));
            if (totalPlots <= 0)
                throw new ArgumentOutOfRangeException(nameof(totalPlots));
            if (unlockGold < 0)
                throw new ArgumentOutOfRangeException(nameof(unlockGold));

            Stage = stage;
            NeedPlayerLevel = needPlayerLevel;
            TotalPlots = totalPlots;
            UnlockGold = unlockGold;
        }

        public int Stage { get; }
        public int NeedPlayerLevel { get; }
        public int TotalPlots { get; }
        /// <summary>本阶段一次收取的金币；初始档（stage0）为 0。</summary>
        public int UnlockGold { get; }
    }

    /// <summary>玩法初始化行。货币仍取 Global/player 默认，本行不赋金钻券。</summary>
    public sealed class PlayerInitRow
    {
        public PlayerInitRow(
            int initialPlotCount,
            IReadOnlyList<int> starterTools,
            IReadOnlyList<int> starterSeeds,
            IReadOnlyList<int> starterFacilityInstances,
            IReadOnlyList<int> unlockGiftTools,
            int grantSchemaVersion,
            string note)
        {
            InitialPlotCount = initialPlotCount;
            StarterTools = FreezeIntList(starterTools, nameof(starterTools));
            StarterSeeds = FreezeIntList(starterSeeds, nameof(starterSeeds));
            StarterFacilityInstances = FreezeIntList(starterFacilityInstances, nameof(starterFacilityInstances));
            UnlockGiftTools = FreezeIntList(unlockGiftTools, nameof(unlockGiftTools));
            GrantSchemaVersion = grantSchemaVersion;
            Note = note ?? "";
        }

        public int InitialPlotCount { get; }
        public IReadOnlyList<int> StarterTools { get; }
        public IReadOnlyList<int> StarterSeeds { get; }
        public IReadOnlyList<int> StarterFacilityInstances { get; }
        /// <summary>升级一次赠送工具（门槛取 Item.unlockLevel；M2 含 5102@Lv3）。</summary>
        public IReadOnlyList<int> UnlockGiftTools { get; }
        public int GrantSchemaVersion { get; }
        public string Note { get; }

        static IReadOnlyList<int> FreezeIntList(IReadOnlyList<int> source, string name)
        {
            if (source == null)
                throw new ArgumentNullException(name);
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<int>());
            var arr = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>
    /// Domain 注入用的固定配置快照：版本 + Global + Shop/地图/初始化 + 已解析材料。
    /// 不持有 Luban 可变 List；调用方不得通过本类型修改导出 JSON。
    /// </summary>
    public sealed class GameConfigSnapshot
    {
        readonly IReadOnlyDictionary<int, ShopRow> _shops;
        readonly IReadOnlyDictionary<int, MapInstanceRow> _map;
        readonly IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> _materials;
        readonly IReadOnlyDictionary<int, int> _itemUnlockLevels;
        readonly IReadOnlyDictionary<int, int> _itemStackMax;
        readonly IReadOnlyDictionary<int, int> _itemSellPrice;
        readonly IReadOnlyDictionary<int, int> _levelNeedExp;
        readonly IReadOnlyList<PlotStageRow> _plotStages;
        readonly IReadOnlyDictionary<int, CropSpec> _crops;
        readonly IReadOnlyDictionary<int, GatherNodeSpec> _gatherNodes;
        readonly IReadOnlyDictionary<long, ToolLevelSpec> _toolLevels;
        readonly int[] _knownItemIds;
        readonly HashSet<int> _knownItemLookup;
        readonly int _stackDefault;
        readonly int _maxLevel;

        /// <summary>正式最高玩家等级（03 Level / ADR D08）；无 Level 表时为 0。</summary>
        public const int FormalMaxPlayerLevel = 20;

        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey)
            : this(globals, shops, mapInstances, playerInit, materialsByKey, null, null, null, 0, null, 0, null, null)
        {
        }

        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            int stackDefault)
            : this(globals, shops, mapInstances, playerInit, materialsByKey, knownItemIds, itemUnlockLevels, null, stackDefault, null, 0, null, null)
        {
        }

        /// <summary>旧 9 参构造：无 Level / 售价面（兼容）；Level/售价为空，不得臆造价格。</summary>
        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, int> itemStackMax,
            int stackDefault)
            : this(globals, shops, mapInstances, playerInit, materialsByKey, knownItemIds, itemUnlockLevels, itemStackMax, stackDefault, null, 0, null, null)
        {
        }

        /// <summary>旧 11 参构造：无售价面（兼容）；不得臆造出售价格。</summary>
        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, int> itemStackMax,
            int stackDefault,
            IReadOnlyDictionary<int, int> levelNeedExp,
            int maxLevel)
            : this(
                globals, shops, mapInstances, playerInit, materialsByKey,
                knownItemIds, itemUnlockLevels, itemStackMax, stackDefault,
                levelNeedExp, maxLevel, null, null)
        {
        }

        /// <summary>旧 12 参构造：无 Plot 阶段面（兼容）；不得臆造解锁经济。</summary>
        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, int> itemStackMax,
            int stackDefault,
            IReadOnlyDictionary<int, int> levelNeedExp,
            int maxLevel,
            IReadOnlyDictionary<int, int> itemSellPrice)
            : this(
                globals, shops, mapInstances, playerInit, materialsByKey,
                knownItemIds, itemUnlockLevels, itemStackMax, stackDefault,
                levelNeedExp, maxLevel, itemSellPrice, null)
        {
        }

        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, int> itemStackMax,
            int stackDefault,
            IReadOnlyDictionary<int, int> levelNeedExp,
            int maxLevel,
            IReadOnlyDictionary<int, int> itemSellPrice,
            IReadOnlyList<PlotStageRow> plotStages)
            : this(
                globals, shops, mapInstances, playerInit, materialsByKey,
                knownItemIds, itemUnlockLevels, itemStackMax, stackDefault,
                levelNeedExp, maxLevel, itemSellPrice, plotStages, null)
        {
        }

        /// <summary>含 Crop 只读面（T029）；旧构造缺 Crop 时为空，不得臆造产量。</summary>
        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, int> itemStackMax,
            int stackDefault,
            IReadOnlyDictionary<int, int> levelNeedExp,
            int maxLevel,
            IReadOnlyDictionary<int, int> itemSellPrice,
            IReadOnlyList<PlotStageRow> plotStages,
            IReadOnlyDictionary<int, CropSpec>? crops)
            : this(
                globals, shops, mapInstances, playerInit, materialsByKey,
                knownItemIds, itemUnlockLevels, itemStackMax, stackDefault,
                levelNeedExp, maxLevel, itemSellPrice, plotStages, crops, null, null)
        {
        }

        /// <summary>
        /// 含 GatherNode/Tool 只读面（T038）；旧构造缺表时为空，不得臆造采集规则。
        /// </summary>
        public GameConfigSnapshot(
            ConfigSnapshot globals,
            IReadOnlyDictionary<int, ShopRow> shops,
            IReadOnlyDictionary<int, MapInstanceRow> mapInstances,
            PlayerInitRow playerInit,
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materialsByKey,
            ISet<int> knownItemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, int> itemStackMax,
            int stackDefault,
            IReadOnlyDictionary<int, int> levelNeedExp,
            int maxLevel,
            IReadOnlyDictionary<int, int> itemSellPrice,
            IReadOnlyList<PlotStageRow> plotStages,
            IReadOnlyDictionary<int, CropSpec>? crops,
            IReadOnlyDictionary<int, GatherNodeSpec>? gatherNodes,
            IReadOnlyDictionary<long, ToolLevelSpec>? toolLevels)
        {
            Globals = globals ?? throw new ArgumentNullException(nameof(globals));
            PlayerInit = playerInit ?? throw new ArgumentNullException(nameof(playerInit));
            _shops = FreezeShops(shops);
            _map = FreezeMap(mapInstances);
            _materials = FreezeMaterials(materialsByKey);
            _plotStages = FreezePlotStages(plotStages);
            _crops = FreezeCrops(crops);
            _gatherNodes = FreezeGatherNodes(gatherNodes);
            _toolLevels = FreezeToolLevels(toolLevels);
            Version = globals.Version;

            if (knownItemIds == null || itemUnlockLevels == null || stackDefault <= 0)
            {
                // 兼容旧构造：Item 面可空；stack_default 仍须从 Global 严格读取正数（禁止 fallback 99 掩盖缺失）
                _knownItemIds = Array.Empty<int>();
                _knownItemLookup = new HashSet<int>();
                _itemUnlockLevels = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());
                _itemStackMax = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());
                _itemSellPrice = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());
                _stackDefault = stackDefault > 0 ? stackDefault : RequireStackDefault(globals);
            }
            else
            {
                _knownItemLookup = new HashSet<int>(knownItemIds);
                _knownItemIds = new int[_knownItemLookup.Count];
                _knownItemLookup.CopyTo(_knownItemIds);
                Array.Sort(_knownItemIds);
                _itemUnlockLevels = FreezeIntMap(itemUnlockLevels);
                _itemStackMax = FreezePositiveStackMax(itemStackMax, _knownItemLookup);
                _stackDefault = stackDefault;
                // 缺售价面 → 空表；禁止臆造价格。仅物理可售（正 sellPrice）进入本视图。
                _itemSellPrice = itemSellPrice == null
                    ? new ReadOnlyDictionary<int, int>(new Dictionary<int, int>())
                    : FreezePositiveSellPrice(itemSellPrice, _knownItemLookup, _itemStackMax);
            }

            if (levelNeedExp == null || maxLevel <= 0)
            {
                _levelNeedExp = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());
                _maxLevel = 0;
            }
            else
            {
                _levelNeedExp = FreezeLevelNeedExp(levelNeedExp, maxLevel);
                _maxLevel = maxLevel;
            }
        }

        public int Version { get; }
        public ConfigSnapshot Globals { get; }
        public PlayerInitRow PlayerInit { get; }
        public IReadOnlyDictionary<int, ShopRow> Shops { get { return _shops; } }
        public IReadOnlyDictionary<int, MapInstanceRow> MapInstances { get { return _map; } }
        public IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> MaterialsByKey { get { return _materials; } }

        /// <summary>正式 Item 表 ID（只读拷贝；与 T006 同一 tbitem 装载路径）。</summary>
        public IReadOnlyList<int> KnownItemIds { get { return Array.AsReadOnly(_knownItemIds); } }

        /// <summary>itemId→unlockLevel（只读）。</summary>
        public IReadOnlyDictionary<int, int> ItemUnlockLevels { get { return _itemUnlockLevels; } }

        /// <summary>
        /// 物理背包 itemId→stackMax（只读；排除 ItemType=Currency）。
        /// Currency 属货币字段，不占格、不入本视图；不得回落 stack_default。
        /// </summary>
        public IReadOnlyDictionary<int, int> ItemStackMax { get { return _itemStackMax; } }

        /// <summary>
        /// 物理可售 itemId→sellPrice（只读；仅正售价；排除 Currency / sellPrice≤0）。
        /// 旧构造缺售价面时为空，不得臆造价格。
        /// </summary>
        public IReadOnlyDictionary<int, int> ItemSellPrice { get { return _itemSellPrice; } }

        /// <summary>Global.stack_default。</summary>
        public int StackDefault { get { return _stackDefault; } }

        /// <summary>
        /// 当前等级→升至下一级所需经验（只读；最高级无行）。
        /// 经验为相对当前级字段，非终身累计。
        /// </summary>
        public IReadOnlyDictionary<int, int> LevelNeedExp { get { return _levelNeedExp; } }

        /// <summary>最高玩家等级；无 Level 表时为 0。</summary>
        public int MaxLevel { get { return _maxLevel; } }

        /// <summary>
        /// Plot 解锁阶段（只读拷贝；旧构造缺阶段面时为空，不得臆造等级/费用）。
        /// stage0=初始免费档；其后每阶段一次收 unlockGold，扩到 totalPlots。
        /// </summary>
        public IReadOnlyList<PlotStageRow> PlotStages { get { return _plotStages; } }

        /// <summary>最大地块总数（阶段表末行）；无阶段面时为 0。</summary>
        public int MaxPlotTotal
        {
            get
            {
                if (_plotStages.Count == 0)
                    return 0;
                return _plotStages[_plotStages.Count - 1].TotalPlots;
            }
        }

        /// <summary>作物只读面（cropId→CropSpec）；旧构造缺 Crop 时为空。</summary>
        public IReadOnlyDictionary<int, CropSpec> Crops { get { return _crops; } }

        /// <summary>采集点只读面（nodeId→GatherNodeSpec）；旧构造缺表时为空。</summary>
        public IReadOnlyDictionary<int, GatherNodeSpec> GatherNodes { get { return _gatherNodes; } }

        /// <summary>
        /// 工具等级只读面（<see cref="ToolLevelSpec.PackKey"/>→规格）；旧构造缺表时为空。
        /// </summary>
        public IReadOnlyDictionary<long, ToolLevelSpec> ToolLevels { get { return _toolLevels; } }

        public bool IsKnownItem(int itemId) { return _knownItemLookup.Contains(itemId); }

        /// <summary>按作物 ID 取只读投影；缺失返回 false（不臆造）。</summary>
        public bool TryGetCrop(int cropId, out CropSpec crop)
        {
            if (_crops.TryGetValue(cropId, out CropSpec found))
            {
                crop = found;
                return true;
            }

            crop = null;
            return false;
        }

        /// <summary>按采集点类型 ID 取只读投影；缺失返回 false（不臆造）。</summary>
        public bool TryGetGatherNode(int nodeId, out GatherNodeSpec node)
        {
            if (_gatherNodes.TryGetValue(nodeId, out GatherNodeSpec found))
            {
                node = found;
                return true;
            }

            node = null;
            return false;
        }

        /// <summary>按工具 ID+等级取只读投影；缺失返回 false（不臆造）。</summary>
        public bool TryGetToolLevel(int toolId, int level, out ToolLevelSpec tool)
        {
            if (_toolLevels.TryGetValue(ToolLevelSpec.PackKey(toolId, level), out ToolLevelSpec found))
            {
                tool = found;
                return true;
            }

            tool = null;
            return false;
        }

        /// <summary>读取当前等级升至下一级所需经验；满级/缺失返回 false。</summary>
        public bool TryGetLevelNeedExp(int level, out int needExp)
        {
            return _levelNeedExp.TryGetValue(level, out needExp);
        }

        /// <summary>
        /// 物理背包 stackMax；未知 ID / Currency 返回 false（不回落 stack_default）。
        /// </summary>
        public bool TryGetItemStackMax(int itemId, out int stackMax)
        {
            return _itemStackMax.TryGetValue(itemId, out stackMax);
        }

        /// <summary>
        /// 物理可售售价；Currency / sellPrice≤0 / 未知 / 缺售价面 → false（不臆造）。
        /// </summary>
        public bool TryGetItemSellPrice(int itemId, out int sellPrice)
        {
            return _itemSellPrice.TryGetValue(itemId, out sellPrice);
        }

        /// <summary>是否可出售：等同 <see cref="TryGetItemSellPrice"/> 成功。</summary>
        public bool IsSellable(int itemId)
        {
            return _itemSellPrice.ContainsKey(itemId);
        }

        /// <summary>
        /// 将配置 long 安全转为 int。越界显式失败，禁止静默截断（如 4294967297→1）。
        /// </summary>
        public static int ToConfigInt32(long value, string context)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new ConfigLoadException(
                    (context ?? "字段") + " 超出 int 范围: " + value.ToString(CultureInfo.InvariantCulture));
            }
            return checked((int)value);
        }

        /// <summary>
        /// 从已发布 ConfigLuban 目录构建。失败不返回半套快照。
        /// 依赖清单校验（ConfigSnapshotReader）与表语义校验。
        /// </summary>
        public static GameConfigSnapshot LoadDirectory(string directory, int expectedVersion)
        {
            ConfigSnapshot globals = ConfigSnapshotReader.ReadDirectory(directory, expectedVersion);
            string root = Path.GetFullPath(directory);

            var itemIds = LoadIdSet(Path.Combine(root, "tbitem.json"), "id");
            var itemUnlockLevels = LoadIntFieldMap(Path.Combine(root, "tbitem.json"), "id", "unlockLevel");
            // 物理堆叠面：按 type 排除 Currency（stackMax=0 合法）；非 Currency 仍须正 stackMax
            var itemStackMax = LoadPhysicalItemStackMax(Path.Combine(root, "tbitem.json"), itemIds);
            // 物理可售售价：同份 tbitem；Currency / sellPrice≤0 不入表（不硬编码价表）
            var itemSellPrice = LoadPhysicalItemSellPrice(Path.Combine(root, "tbitem.json"), itemStackMax);
            var nodeIds = LoadIdSet(Path.Combine(root, "tbgathernode.json"), "id");
            var nodeExpectedCounts = LoadIntFieldMap(Path.Combine(root, "tbgathernode.json"), "id", "count");
            var facilityIds = LoadIdSet(Path.Combine(root, "tbfacility.json"), "id");
            IReadOnlyList<PlotStageRow> plotStages = LoadPlotStages(Path.Combine(root, "tbplot.json"));
            int maxPlots = plotStages[plotStages.Count - 1].TotalPlots;

            IReadOnlyDictionary<int, ShopRow> shops = LoadShops(Path.Combine(root, "tbshop.json"), itemIds);
            IReadOnlyDictionary<int, MapInstanceRow> map = LoadMap(
                Path.Combine(root, "tbmapinstance.json"),
                nodeIds,
                facilityIds,
                nodeExpectedCounts,
                maxPlots,
                facilityIds);
            PlayerInitRow init = LoadPlayerInit(
                Path.Combine(root, "tbplayerinit.json"),
                itemIds,
                itemUnlockLevels,
                map);
            if (init.InitialPlotCount != plotStages[0].TotalPlots)
            {
                throw new ConfigLoadException(
                    "PlayerInit.initialPlotCount 须等于 Plot stage0.totalPlots="
                    + plotStages[0].TotalPlots.ToString(CultureInfo.InvariantCulture)
                    + "，实际="
                    + init.InitialPlotCount.ToString(CultureInfo.InvariantCulture));
            }

            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> materials = LoadMaterials(root, itemIds);
            int stackDefault = RequireStackDefault(globals);
            IReadOnlyDictionary<int, int> levelNeedExp = LoadLevelNeedExp(
                Path.Combine(root, "tblevel.json"), FormalMaxPlayerLevel);
            IReadOnlyDictionary<int, CropSpec> crops = LoadCrops(
                Path.Combine(root, "tbcrop.json"), itemIds);
            // 合法工具集合仅来自同一 Item 表 type==Tool，不按 id 范围/stackMax/白名单推断
            ISet<int> toolItemIds = LoadItemIdsOfType(
                Path.Combine(root, "tbitem.json"), "Tool");
            IReadOnlyDictionary<int, GatherNodeSpec> gatherNodes = LoadGatherNodes(
                Path.Combine(root, "tbgathernode.json"), itemIds, toolItemIds);
            IReadOnlyDictionary<long, ToolLevelSpec> toolLevels = LoadToolLevels(
                Path.Combine(root, "tbtool.json"), itemIds, toolItemIds);
            ValidateGatherNodeToolLevel1(gatherNodes, toolLevels);

            return new GameConfigSnapshot(
                globals,
                shops,
                map,
                init,
                materials,
                itemIds,
                itemUnlockLevels,
                itemStackMax,
                stackDefault,
                levelNeedExp,
                FormalMaxPlayerLevel,
                itemSellPrice,
                plotStages,
                crops,
                gatherNodes,
                toolLevels);
        }

        /// <summary>
        /// 装载 Level 表：须恰好覆盖 1..(maxLevel-1)，needExp 全为正；禁止缺口/非正/满级行。
        /// 不改曲线、不重新推导热公式。
        /// </summary>
        static IReadOnlyDictionary<int, int> LoadLevelNeedExp(string path, int maxLevel)
        {
            if (maxLevel <= 1)
                throw new ConfigLoadException("MaxLevel 必须大于 1");

            var map = new Dictionary<int, int>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int level = RequirePositiveInt(row, "level", path);
                int needExp = RequirePositiveInt(row, "needExp", path);
                if (level >= maxLevel)
                {
                    throw new ConfigLoadException(
                        path + " 满级不得有 needExp 行 level=" + level.ToString(CultureInfo.InvariantCulture));
                }
                if (map.ContainsKey(level))
                {
                    throw new ConfigLoadException(
                        path + " 重复 level=" + level.ToString(CultureInfo.InvariantCulture));
                }
                map.Add(level, needExp);
            }

            for (int lv = 1; lv < maxLevel; lv++)
            {
                if (!map.TryGetValue(lv, out int need) || need <= 0)
                {
                    throw new ConfigLoadException(
                        path + " 缺 level/needExp 或非正: level=" + lv.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (map.Count != maxLevel - 1)
            {
                throw new ConfigLoadException(
                    path
                    + " Level 行数须为 "
                    + (maxLevel - 1).ToString(CultureInfo.InvariantCulture)
                    + "，实际="
                    + map.Count.ToString(CultureInfo.InvariantCulture));
            }

            return new ReadOnlyDictionary<int, int>(map);
        }

        static IReadOnlyDictionary<int, int> FreezeLevelNeedExp(IReadOnlyDictionary<int, int> source, int maxLevel)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (maxLevel <= 1)
                throw new ConfigLoadException("MaxLevel 必须大于 1");

            var copy = new Dictionary<int, int>(source.Count);
            foreach (KeyValuePair<int, int> pair in source)
            {
                if (pair.Key <= 0 || pair.Key >= maxLevel)
                {
                    throw new ConfigLoadException(
                        "Level.needExp 等级越界 level=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                if (pair.Value <= 0)
                {
                    throw new ConfigLoadException(
                        "Level.needExp 必须为正 level=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                copy.Add(pair.Key, pair.Value);
            }

            for (int lv = 1; lv < maxLevel; lv++)
            {
                if (!copy.ContainsKey(lv))
                {
                    throw new ConfigLoadException(
                        "Level.needExp 缺 level=" + lv.ToString(CultureInfo.InvariantCulture));
                }
            }

            return new ReadOnlyDictionary<int, int>(copy);
        }

        /// <summary>
        /// 装载物理背包 stackMax：Currency 按 schema type 排除（允许 stackMax=0）；
        /// 其余正式 Item 必须正 stackMax。不把 0 回落为 stack_default。
        /// </summary>
        static IReadOnlyDictionary<int, int> LoadPhysicalItemStackMax(string path, ISet<int> allItemIds)
        {
            var physical = new Dictionary<int, int>();
            var currencyIds = new HashSet<int>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int id = RequirePositiveInt(row, "id", path);
                string type = RequireEnumName(row, "type", ItemTypeNames);
                int stackMax = RequireNonNegativeInt(row, "stackMax", path);
                if (string.Equals(type, "Currency", StringComparison.Ordinal))
                {
                    currencyIds.Add(id);
                    continue;
                }

                if (stackMax <= 0)
                {
                    throw new ConfigLoadException(
                        "非 Currency Item.stackMax 必须为正 id="
                        + id.ToString(CultureInfo.InvariantCulture)
                        + " type="
                        + type);
                }

                if (physical.ContainsKey(id))
                {
                    throw new ConfigLoadException(
                        path + " 重复 id=" + id.ToString(CultureInfo.InvariantCulture));
                }

                physical.Add(id, stackMax);
            }

            foreach (int id in allItemIds)
            {
                if (currencyIds.Contains(id))
                    continue;
                if (!physical.TryGetValue(id, out int max) || max <= 0)
                {
                    throw new ConfigLoadException(
                        "Item.stackMax 缺失或非法 id=" + id.ToString(CultureInfo.InvariantCulture));
                }
            }

            return new ReadOnlyDictionary<int, int>(physical);
        }

        static IReadOnlyDictionary<int, int> FreezePositiveStackMax(
            IReadOnlyDictionary<int, int> source,
            HashSet<int> known)
        {
            if (source == null)
                return new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());

            var copy = new Dictionary<int, int>(source.Count);
            foreach (KeyValuePair<int, int> pair in source)
            {
                if (pair.Value <= 0)
                {
                    throw new ConfigLoadException(
                        "Item.stackMax 必须为正 id=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                if (!known.Contains(pair.Key))
                {
                    throw new ConfigLoadException(
                        "Item.stackMax 含未知 id=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                copy.Add(pair.Key, pair.Value);
            }
            return new ReadOnlyDictionary<int, int>(copy);
        }

        /// <summary>
        /// 装载物理可售售价：Currency 排除；sellPrice≤0 不入表；仅物理 stackMax 面内正价。
        /// </summary>
        static IReadOnlyDictionary<int, int> LoadPhysicalItemSellPrice(
            string path,
            IReadOnlyDictionary<int, int> physicalStackMax)
        {
            var sellable = new Dictionary<int, int>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int id = RequirePositiveInt(row, "id", path);
                string type = RequireEnumName(row, "type", ItemTypeNames);
                int sellPrice = RequireNonNegativeInt(row, "sellPrice", path);
                if (string.Equals(type, "Currency", StringComparison.Ordinal))
                    continue;
                if (!physicalStackMax.ContainsKey(id))
                    continue;
                if (sellPrice <= 0)
                    continue;
                if (sellable.ContainsKey(id))
                {
                    throw new ConfigLoadException(
                        path + " 重复 id=" + id.ToString(CultureInfo.InvariantCulture));
                }
                sellable.Add(id, sellPrice);
            }
            return new ReadOnlyDictionary<int, int>(sellable);
        }

        static IReadOnlyDictionary<int, int> FreezePositiveSellPrice(
            IReadOnlyDictionary<int, int> source,
            HashSet<int> known,
            IReadOnlyDictionary<int, int> physicalStackMax)
        {
            if (source == null)
                return new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());

            var copy = new Dictionary<int, int>(source.Count);
            foreach (KeyValuePair<int, int> pair in source)
            {
                if (pair.Key <= 0)
                {
                    throw new ConfigLoadException(
                        "Item.sellPrice 含非法 id=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                if (pair.Value <= 0)
                {
                    throw new ConfigLoadException(
                        "Item.sellPrice 必须为正 id=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                if (!known.Contains(pair.Key))
                {
                    throw new ConfigLoadException(
                        "Item.sellPrice 含未知 id=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                if (!physicalStackMax.ContainsKey(pair.Key))
                {
                    throw new ConfigLoadException(
                        "Item.sellPrice 非物理背包面 id=" + pair.Key.ToString(CultureInfo.InvariantCulture));
                }
                copy.Add(pair.Key, pair.Value);
            }
            return new ReadOnlyDictionary<int, int>(copy);
        }

        static int RequireStackDefault(ConfigSnapshot globals)
        {
            if (!globals.Globals.TryGetValue("stack_default", out string raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value <= 0)
            {
                throw new ConfigLoadException("Global.stack_default 缺失或非法");
            }

            return value;
        }

        static IReadOnlyDictionary<int, int> FreezeIntMap(IReadOnlyDictionary<int, int> source)
        {
            var copy = new Dictionary<int, int>(source.Count);
            foreach (KeyValuePair<int, int> pair in source)
                copy.Add(pair.Key, pair.Value);
            return new ReadOnlyDictionary<int, int>(copy);
        }

        static IReadOnlyDictionary<int, ShopRow> LoadShops(string path, ISet<int> itemIds)
        {
            var map = new Dictionary<int, ShopRow>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int id = RequirePositiveInt(row, "id", "Shop");
                string category = RequireEnumName(row, "category", ShopCategoryNames);
                if (category == "BagExpand")
                    throw new ConfigLoadException("Shop 不得含 BagExpand；扩容走 BagExpandReq/Global 档位");
                if (category == "Furniture" || category == "Avatar")
                    throw new ConfigLoadException("Shop 本阶段不允许分类: " + category);
                int itemId = RequirePositiveInt(row, "itemId", "Shop");
                int priceCurrency = RequirePositiveInt(row, "priceCurrency", "Shop");
                int price = RequireNonNegativeInt(row, "price", "Shop");
                int limitCount = RequireNonNegativeInt(row, "limitCount", "Shop");
                int unlockLevel = RequireNonNegativeInt(row, "unlockLevel", "Shop");
                if (!itemIds.Contains(itemId))
                    throw new ConfigLoadException("Shop 物品引用无效: " + itemId.ToString(CultureInfo.InvariantCulture));
                if (!itemIds.Contains(priceCurrency))
                    throw new ConfigLoadException("Shop 货币引用无效: " + priceCurrency.ToString(CultureInfo.InvariantCulture));
                if (map.ContainsKey(id))
                    throw new ConfigLoadException("Shop id 重复: " + id.ToString(CultureInfo.InvariantCulture));
                map.Add(id, new ShopRow(id, category, itemId, priceCurrency, price, limitCount, unlockLevel));
            }
            return new ReadOnlyDictionary<int, ShopRow>(map);
        }

        static readonly string[] ShopCategoryNames = { "Seed", "Consumable", "BagExpand", "Furniture", "Avatar" };
        static readonly string[] MapKindNames = { "Plot", "Node", "Facility" };
        /// <summary>与 Item.type / ItemType 枚举一致；Currency 不入物理背包堆叠面。</summary>
        static readonly string[] ItemTypeNames =
        {
            "Seed", "Crop", "Craft", "Gather", "Tool", "Consumable", "Furniture", "Currency"
        };

        static IReadOnlyDictionary<int, MapInstanceRow> LoadMap(
            string path,
            ISet<int> nodeIds,
            ISet<int> facilityIds,
            IReadOnlyDictionary<int, int> nodeExpectedCounts,
            int expectedPlotCount,
            ISet<int> requiredFacilityTypes)
        {
            var map = new Dictionary<int, MapInstanceRow>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int instanceId = RequirePositiveInt(row, "instanceId", "MapInstance");
                string kind = RequireEnumName(row, "kind", MapKindNames);
                int typeId = RequireNonNegativeInt(row, "typeId", "MapInstance");
                float x = (float)row.GetDouble("posX");
                float y = (float)row.GetDouble("posY");
                float z = (float)row.GetDouble("posZ");
                string label = row.Has("label") ? row.GetString("label") : "";
                if (kind == "Plot")
                {
                    if (typeId != 0)
                        throw new ConfigLoadException("Plot 实例 typeId 必须为 0: " + instanceId.ToString(CultureInfo.InvariantCulture));
                }
                else if (kind == "Node")
                {
                    if (typeId <= 0 || !nodeIds.Contains(typeId))
                        throw new ConfigLoadException("Node 实例缺 GatherNode 引用: " + typeId.ToString(CultureInfo.InvariantCulture));
                }
                else if (kind == "Facility")
                {
                    if (typeId <= 0 || !facilityIds.Contains(typeId))
                        throw new ConfigLoadException("Facility 实例缺 Facility 引用: " + typeId.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    throw new ConfigLoadException("未知 MapInstanceKind: " + kind);
                }
                if (map.ContainsKey(instanceId))
                    throw new ConfigLoadException("地图实例 id 重复: " + instanceId.ToString(CultureInfo.InvariantCulture));
                map.Add(instanceId, new MapInstanceRow(instanceId, kind, typeId, x, y, z, label));
            }

            ValidateMapLayout(map, nodeExpectedCounts, expectedPlotCount, requiredFacilityTypes);
            return new ReadOnlyDictionary<int, MapInstanceRow>(map);
        }

        /// <summary>
        /// Plot 数=Plot.totalPlots 最大值；各 Node typeId 实例数=GatherNode.count；每类 Facility 恰好 1 个未建造占位。
        /// </summary>
        static void ValidateMapLayout(
            IReadOnlyDictionary<int, MapInstanceRow> map,
            IReadOnlyDictionary<int, int> nodeExpectedCounts,
            int expectedPlotCount,
            ISet<int> requiredFacilityTypes)
        {
            int plotCount = 0;
            var nodeCounts = new Dictionary<int, int>();
            var facilityTypes = new HashSet<int>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in map)
            {
                MapInstanceRow row = pair.Value;
                if (row.Kind == "Plot")
                {
                    plotCount++;
                }
                else if (row.Kind == "Node")
                {
                    int c;
                    nodeCounts.TryGetValue(row.TypeId, out c);
                    nodeCounts[row.TypeId] = c + 1;
                }
                else if (row.Kind == "Facility")
                {
                    if (!facilityTypes.Add(row.TypeId))
                    {
                        throw new ConfigLoadException(
                            "Facility 类型重复布局 typeId=" + row.TypeId.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            if (plotCount != expectedPlotCount)
            {
                throw new ConfigLoadException(
                    "Plot 实例数须等于 max Plot.totalPlots="
                    + expectedPlotCount.ToString(CultureInfo.InvariantCulture)
                    + "，实际="
                    + plotCount.ToString(CultureInfo.InvariantCulture));
            }

            foreach (KeyValuePair<int, int> expected in nodeExpectedCounts)
            {
                int actual;
                nodeCounts.TryGetValue(expected.Key, out actual);
                if (actual != expected.Value)
                {
                    throw new ConfigLoadException(
                        "GatherNode typeId="
                        + expected.Key.ToString(CultureInfo.InvariantCulture)
                        + " 要求 count="
                        + expected.Value.ToString(CultureInfo.InvariantCulture)
                        + "，实际实例="
                        + actual.ToString(CultureInfo.InvariantCulture));
                }
            }
            foreach (KeyValuePair<int, int> actual in nodeCounts)
            {
                if (!nodeExpectedCounts.ContainsKey(actual.Key))
                {
                    throw new ConfigLoadException(
                        "Node 实例含未知 GatherNode typeId="
                        + actual.Key.ToString(CultureInfo.InvariantCulture));
                }
            }

            foreach (int fid in requiredFacilityTypes)
            {
                if (!facilityTypes.Contains(fid))
                {
                    throw new ConfigLoadException(
                        "缺少 Facility 类型布局 typeId=" + fid.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (facilityTypes.Count != requiredFacilityTypes.Count)
            {
                throw new ConfigLoadException(
                    "Facility 布局类型数不符：要求 "
                    + requiredFacilityTypes.Count.ToString(CultureInfo.InvariantCulture)
                    + "，实际 "
                    + facilityTypes.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        static PlayerInitRow LoadPlayerInit(
            string path,
            ISet<int> itemIds,
            IReadOnlyDictionary<int, int> itemUnlockLevels,
            IReadOnlyDictionary<int, MapInstanceRow> map)
        {
            List<SimpleJson.Obj> rows = ReadArray(path);
            if (rows.Count != 1)
                throw new ConfigLoadException("PlayerInit 必须恰好一行");
            SimpleJson.Obj row = rows[0];
            int plots = RequirePositiveInt(row, "initialPlotCount", "PlayerInit");
            IReadOnlyList<int> tools = ParseIdList(row.RequireString("starterTools"), "starterTools");
            IReadOnlyList<int> seeds = ParseIdList(row.Has("starterSeeds") ? row.GetString("starterSeeds") : "", "starterSeeds");
            IReadOnlyList<int> facilities = ParseIdList(
                row.Has("starterFacilityInstances") ? row.GetString("starterFacilityInstances") : "",
                "starterFacilityInstances");
            IReadOnlyList<int> unlockGifts = ParseIdList(
                row.Has("unlockGiftTools") ? row.GetString("unlockGiftTools") : "",
                "unlockGiftTools");
            int grant = RequirePositiveInt(row, "grantSchemaVersion", "PlayerInit");
            string note = row.Has("note") ? row.GetString("note") : "";
            for (int i = 0; i < tools.Count; i++)
            {
                if (!itemIds.Contains(tools[i]))
                    throw new ConfigLoadException("PlayerInit 工具缺物品引用: " + tools[i].ToString(CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < seeds.Count; i++)
            {
                if (!itemIds.Contains(seeds[i]))
                    throw new ConfigLoadException("PlayerInit 种子缺物品引用: " + seeds[i].ToString(CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < facilities.Count; i++)
            {
                if (!map.TryGetValue(facilities[i], out MapInstanceRow inst) || inst.Kind != "Facility")
                    throw new ConfigLoadException("PlayerInit 设施实例无效: " + facilities[i].ToString(CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < unlockGifts.Count; i++)
            {
                int giftId = unlockGifts[i];
                if (!itemIds.Contains(giftId))
                    throw new ConfigLoadException("PlayerInit unlockGiftTools 缺物品引用: " + giftId.ToString(CultureInfo.InvariantCulture));
                if (!itemUnlockLevels.TryGetValue(giftId, out int unlockLv) || unlockLv <= 0)
                    throw new ConfigLoadException("PlayerInit unlockGiftTools 缺 unlockLevel: " + giftId.ToString(CultureInfo.InvariantCulture));
            }
            return new PlayerInitRow(plots, tools, seeds, facilities, unlockGifts, grant, note);
        }

        static IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> LoadMaterials(string root, ISet<int> itemIds)
        {
            var result = new Dictionary<string, IReadOnlyList<ItemStackSpec>>(StringComparer.Ordinal);
            LoadMaterialField(result, Path.Combine(root, "tbrecipe.json"), "id", "inputs", itemIds);
            LoadMaterialField(result, Path.Combine(root, "tbrecipe.json"), "id", "output", itemIds);
            LoadMaterialField(result, Path.Combine(root, "tbtool.json"), "toolId", "materials", itemIds, extraKey: "level");
            LoadMaterialField(result, Path.Combine(root, "tbfacilitylevel.json"), "level", "materials", itemIds);
            return new ReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>>(result);
        }

        static void LoadMaterialField(
            Dictionary<string, IReadOnlyList<ItemStackSpec>> target,
            string path,
            string idField,
            string materialField,
            ISet<int> itemIds,
            string extraKey = null)
        {
            if (!File.Exists(path))
                throw new ConfigLoadException("缺少配置文件: " + path);
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                string idPart = ToConfigInt32(row.RequireLong(idField), path + "." + idField)
                    .ToString(CultureInfo.InvariantCulture);
                if (extraKey != null && row.Has(extraKey))
                {
                    idPart = idPart + "@" + ToConfigInt32(row.RequireLong(extraKey), path + "." + extraKey)
                        .ToString(CultureInfo.InvariantCulture);
                }
                string key = Path.GetFileNameWithoutExtension(path) + "." + materialField + "#" + idPart;
                string raw = "";
                if (row.Has(materialField))
                    raw = row.GetString(materialField);
                IReadOnlyList<ItemStackSpec> stacks;
                try
                {
                    stacks = MaterialList.Parse(raw);
                }
                catch (Exception ex)
                {
                    throw new ConfigLoadException("材料解析失败 " + key + ": " + ex.Message);
                }
                MaterialList.EnsureItemRefs(stacks, itemIds, key);
                if (target.ContainsKey(key))
                    throw new ConfigLoadException("材料键重复: " + key);
                target.Add(key, stacks);
            }
        }

        /// <summary>Luban 枚举在 simple-json 中可能是名字字符串或从 1 起的整型值。</summary>
        static string RequireEnumName(SimpleJson.Obj row, string field, string[] names)
        {
            string text = row.GetString(field);
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(text, names[i], StringComparison.Ordinal))
                    return names[i];
            }
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal)
                && ordinal >= 1 && ordinal <= names.Length)
            {
                return names[ordinal - 1];
            }
            throw new ConfigLoadException("非法枚举 " + field + "=" + text);
        }

        static ISet<int> LoadIdSet(string path, string field)
        {
            var set = new HashSet<int>();
            foreach (SimpleJson.Obj row in ReadArray(path))
                set.Add(RequirePositiveInt(row, field, path));
            return set;
        }

        /// <summary>
        /// 从同一 Item 表按 schema type 收集 id；不按 id 范围、stackMax 或硬编码白名单推断。
        /// </summary>
        static ISet<int> LoadItemIdsOfType(string path, string expectedType)
        {
            var set = new HashSet<int>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int id = RequirePositiveInt(row, "id", path);
                string type = RequireEnumName(row, "type", ItemTypeNames);
                if (string.Equals(type, expectedType, StringComparison.Ordinal))
                    set.Add(id);
            }
            return set;
        }

        static IReadOnlyDictionary<int, int> LoadIntFieldMap(string path, string keyField, string valueField)
        {
            var map = new Dictionary<int, int>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int key = RequirePositiveInt(row, keyField, path);
                int value = RequireNonNegativeInt(row, valueField, path);
                if (map.ContainsKey(key))
                    throw new ConfigLoadException(path + " 重复 " + keyField + "=" + key.ToString(CultureInfo.InvariantCulture));
                map.Add(key, value);
            }
            return new ReadOnlyDictionary<int, int>(map);
        }

        /// <summary>
        /// 装载完整 Plot 阶段表：stage 从 0 连续、totalPlots 严格递增、
        /// needPlayerLevel 正且非降、stage0.unlockGold=0 且其后为正。禁止只读 max 掩盖缺档。
        /// </summary>
        static IReadOnlyList<PlotStageRow> LoadPlotStages(string path)
        {
            var byStage = new Dictionary<int, PlotStageRow>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int stage = RequireNonNegativeInt(row, "stage", "Plot");
                int needLevel = RequirePositiveInt(row, "needPlayerLevel", "Plot");
                int total = RequirePositiveInt(row, "totalPlots", "Plot");
                int gold = RequireNonNegativeInt(row, "unlockGold", "Plot");
                if (byStage.ContainsKey(stage))
                {
                    throw new ConfigLoadException(
                        path + " 重复 stage=" + stage.ToString(CultureInfo.InvariantCulture));
                }
                byStage.Add(stage, new PlotStageRow(stage, needLevel, total, gold));
            }

            if (byStage.Count == 0)
                throw new ConfigLoadException("Plot 阶段表为空");

            var ordered = new PlotStageRow[byStage.Count];
            for (int s = 0; s < byStage.Count; s++)
            {
                if (!byStage.TryGetValue(s, out PlotStageRow stageRow))
                {
                    throw new ConfigLoadException(
                        path + " 缺连续 stage=" + s.ToString(CultureInfo.InvariantCulture));
                }
                ordered[s] = stageRow;
            }

            if (ordered[0].UnlockGold != 0)
                throw new ConfigLoadException(path + " stage0.unlockGold 必须为 0");

            for (int i = 1; i < ordered.Length; i++)
            {
                if (ordered[i].TotalPlots <= ordered[i - 1].TotalPlots)
                {
                    throw new ConfigLoadException(
                        path + " totalPlots 须严格递增 stage="
                        + i.ToString(CultureInfo.InvariantCulture));
                }
                if (ordered[i].NeedPlayerLevel < ordered[i - 1].NeedPlayerLevel)
                {
                    throw new ConfigLoadException(
                        path + " needPlayerLevel 不得下降 stage="
                        + i.ToString(CultureInfo.InvariantCulture));
                }
                if (ordered[i].UnlockGold <= 0)
                {
                    throw new ConfigLoadException(
                        path + " 付费阶段 unlockGold 必须为正 stage="
                        + i.ToString(CultureInfo.InvariantCulture));
                }
            }

            return Array.AsReadOnly(ordered);
        }

        /// <summary>
        /// 装载 Crop 表：正 id/种子/产物/生长/产量区间/经验；seed/yield 须引用已知 Item。
        /// </summary>
        static IReadOnlyDictionary<int, CropSpec> LoadCrops(string path, ISet<int> itemIds)
        {
            var map = new Dictionary<int, CropSpec>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int id = RequirePositiveInt(row, "id", "Crop");
                int seedId = RequirePositiveInt(row, "seedId", "Crop");
                int yieldItemId = RequirePositiveInt(row, "yieldItemId", "Crop");
                int unlockLevel = RequirePositiveInt(row, "unlockLevel", "Crop");
                int growSec = RequirePositiveInt(row, "growSec", "Crop");
                int yieldMin = RequirePositiveInt(row, "yieldMin", "Crop");
                int yieldMax = RequirePositiveInt(row, "yieldMax", "Crop");
                int exp = RequireNonNegativeInt(row, "exp", "Crop");
                if (yieldMax < yieldMin)
                {
                    throw new ConfigLoadException(
                        path + " yieldMax<yieldMin cropId=" + id.ToString(CultureInfo.InvariantCulture));
                }
                if (!itemIds.Contains(seedId))
                {
                    throw new ConfigLoadException(
                        "Crop 种子引用无效: " + seedId.ToString(CultureInfo.InvariantCulture));
                }
                if (!itemIds.Contains(yieldItemId))
                {
                    throw new ConfigLoadException(
                        "Crop 产物引用无效: " + yieldItemId.ToString(CultureInfo.InvariantCulture));
                }
                if (map.ContainsKey(id))
                {
                    throw new ConfigLoadException(
                        "Crop id 重复: " + id.ToString(CultureInfo.InvariantCulture));
                }
                map.Add(id, new CropSpec(id, seedId, yieldItemId, unlockLevel, growSec, yieldMin, yieldMax, exp));
            }

            if (map.Count == 0)
                throw new ConfigLoadException("Crop 表为空");
            return new ReadOnlyDictionary<int, CropSpec>(map);
        }

        static IReadOnlyDictionary<int, CropSpec> FreezeCrops(IReadOnlyDictionary<int, CropSpec>? source)
        {
            if (source == null || source.Count == 0)
                return new ReadOnlyDictionary<int, CropSpec>(new Dictionary<int, CropSpec>());
            var copy = new Dictionary<int, CropSpec>(source.Count);
            foreach (KeyValuePair<int, CropSpec> kv in source)
            {
                CropSpec row = kv.Value ?? throw new ArgumentException("crops 含 null", nameof(source));
                if (kv.Key != row.CropId)
                    throw new ArgumentException("crops key 须等于 CropId", nameof(source));
                if (copy.ContainsKey(kv.Key))
                    throw new ArgumentException("crops 重复", nameof(source));
                copy.Add(kv.Key, new CropSpec(
                    row.CropId, row.SeedId, row.YieldItemId, row.UnlockLevel,
                    row.GrowSec, row.YieldMin, row.YieldMax, row.Exp));
            }
            return new ReadOnlyDictionary<int, CropSpec>(copy);
        }

        /// <summary>
        /// 装载 GatherNode：正 id/工具/解锁/数量/产出/产量区间/冷却/稀有物/经验；引用已知 Item。
        /// rareChance 须存在且为 JSON 数值后转 ppm（0..1e6）；本表概率不参与正式采集抽样。
        /// toolId 须属于 Item.type=Tool 集合；level1 完备性在两表装载后由
        /// <see cref="ValidateGatherNodeToolLevel1"/> 校验。
        /// </summary>
        static IReadOnlyDictionary<int, GatherNodeSpec> LoadGatherNodes(
            string path,
            ISet<int> itemIds,
            ISet<int> toolItemIds)
        {
            var map = new Dictionary<int, GatherNodeSpec>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int id = RequirePositiveInt(row, "id", "GatherNode");
                int toolId = RequirePositiveInt(row, "toolId", "GatherNode");
                int unlockLevel = RequirePositiveInt(row, "unlockLevel", "GatherNode");
                int count = RequirePositiveInt(row, "count", "GatherNode");
                int itemId = RequirePositiveInt(row, "itemId", "GatherNode");
                int yieldMin = RequirePositiveInt(row, "yieldMin", "GatherNode");
                int yieldMax = RequirePositiveInt(row, "yieldMax", "GatherNode");
                int cooldownSec = RequirePositiveInt(row, "cooldownSec", "GatherNode");
                int rareItemId = RequirePositiveInt(row, "rareItemId", "GatherNode");
                int exp = RequireNonNegativeInt(row, "exp", "GatherNode");
                if (yieldMax < yieldMin)
                {
                    throw new ConfigLoadException(
                        "GatherNode 产量区间非法 id=" + id.ToString(CultureInfo.InvariantCulture));
                }
                if (!itemIds.Contains(toolId))
                {
                    throw new ConfigLoadException(
                        "GatherNode 工具引用无效: " + toolId.ToString(CultureInfo.InvariantCulture));
                }
                if (!toolItemIds.Contains(toolId))
                {
                    throw new ConfigLoadException(
                        "GatherNode 工具须为 Item.type=Tool: "
                        + toolId.ToString(CultureInfo.InvariantCulture));
                }
                if (!itemIds.Contains(itemId))
                {
                    throw new ConfigLoadException(
                        "GatherNode 产物引用无效: " + itemId.ToString(CultureInfo.InvariantCulture));
                }
                if (!itemIds.Contains(rareItemId))
                {
                    throw new ConfigLoadException(
                        "GatherNode 稀有物引用无效: " + rareItemId.ToString(CultureInfo.InvariantCulture));
                }
                int rarePpm = ChanceToPpm(
                    RequireJsonChanceNumber(row, "rareChance", "GatherNode"),
                    "GatherNode.rareChance");
                if (map.ContainsKey(id))
                {
                    throw new ConfigLoadException(
                        "GatherNode id 重复: " + id.ToString(CultureInfo.InvariantCulture));
                }
                map.Add(id, new GatherNodeSpec(
                    id, toolId, unlockLevel, count, itemId, yieldMin, yieldMax,
                    cooldownSec, rareItemId, rarePpm, exp));
            }

            if (map.Count == 0)
                throw new ConfigLoadException("GatherNode 表为空");
            return new ReadOnlyDictionary<int, GatherNodeSpec>(map);
        }

        /// <summary>
        /// 装载 Tool 等级行：(toolId,level) 唯一；yieldBonusMax≥0；rareChance 须 JSON 数值→最终概率 ppm；
        /// materials 必须为真实 JSON 字符串（RequireString；拒缺失/null/非字符串）；
        /// 显式 ""/空白依 MaterialList 仍表示无材料。一次解析写入不可变投影（升级不得再读 JSON）。
        /// 每行 toolId 须属于同一 Item 表 type=Tool 集合。负成本/非法数量/重复 itemId/溢出在解析期拒绝。
        /// </summary>
        static IReadOnlyDictionary<long, ToolLevelSpec> LoadToolLevels(
            string path,
            ISet<int> itemIds,
            ISet<int> toolItemIds)
        {
            var map = new Dictionary<long, ToolLevelSpec>();
            foreach (SimpleJson.Obj row in ReadArray(path))
            {
                int toolId = RequirePositiveInt(row, "toolId", "Tool");
                int level = RequirePositiveInt(row, "level", "Tool");
                int needPlayerLevel = RequireNonNegativeInt(row, "needPlayerLevel", "Tool");
                int goldCost = RequireNonNegativeInt(row, "goldCost", "Tool");
                int yieldBonusMax = RequireNonNegativeInt(row, "yieldBonusMax", "Tool");
                if (!itemIds.Contains(toolId))
                {
                    throw new ConfigLoadException(
                        "Tool 工具引用无效: " + toolId.ToString(CultureInfo.InvariantCulture));
                }
                if (!toolItemIds.Contains(toolId))
                {
                    throw new ConfigLoadException(
                        "Tool 工具须为 Item.type=Tool: "
                        + toolId.ToString(CultureInfo.InvariantCulture));
                }
                int rarePpm = ChanceToPpm(
                    RequireJsonChanceNumber(row, "rareChance", "Tool"),
                    "Tool.rareChance");
                string materialsRaw;
                try
                {
                    materialsRaw = row.RequireString("materials");
                }
                catch (FormatException)
                {
                    throw new ConfigLoadException(
                        "Tool materials 须为 JSON 字符串 toolId="
                        + toolId.ToString(CultureInfo.InvariantCulture)
                        + " level="
                        + level.ToString(CultureInfo.InvariantCulture));
                }
                IReadOnlyList<ItemStackSpec> materials;
                try
                {
                    materials = MaterialList.Parse(materialsRaw);
                }
                catch (Exception ex)
                {
                    throw new ConfigLoadException(
                        "Tool materials 解析失败 toolId="
                        + toolId.ToString(CultureInfo.InvariantCulture)
                        + " level="
                        + level.ToString(CultureInfo.InvariantCulture)
                        + ": "
                        + ex.Message);
                }
                MaterialList.EnsureItemRefs(
                    materials,
                    itemIds,
                    "Tool materials toolId="
                    + toolId.ToString(CultureInfo.InvariantCulture)
                    + "@"
                    + level.ToString(CultureInfo.InvariantCulture));
                long key = ToolLevelSpec.PackKey(toolId, level);
                if (map.ContainsKey(key))
                {
                    throw new ConfigLoadException(
                        "Tool (toolId,level) 重复: "
                        + toolId.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + level.ToString(CultureInfo.InvariantCulture));
                }
                map.Add(key, new ToolLevelSpec(
                    toolId, level, needPlayerLevel, goldCost, yieldBonusMax, rarePpm, materials));
            }

            if (map.Count == 0)
                throw new ConfigLoadException("Tool 表为空");
            return new ReadOnlyDictionary<long, ToolLevelSpec>(map);
        }

        /// <summary>
        /// 每个 GatherNode.toolId 必须能解析到 Tool 的 level1 定义；
        /// 仅 Item 表存在（消耗品/货币等）不足，缺定义直接拒绝，不伪造默认工具。
        /// 类别（Item.type=Tool）已在装载时校验。
        /// </summary>
        static void ValidateGatherNodeToolLevel1(
            IReadOnlyDictionary<int, GatherNodeSpec> gatherNodes,
            IReadOnlyDictionary<long, ToolLevelSpec> toolLevels)
        {
            foreach (KeyValuePair<int, GatherNodeSpec> kv in gatherNodes)
            {
                GatherNodeSpec node = kv.Value;
                long level1Key = ToolLevelSpec.PackKey(node.ToolId, 1);
                if (!toolLevels.ContainsKey(level1Key))
                {
                    throw new ConfigLoadException(
                        "GatherNode 工具缺 Tool level1 定义: toolId="
                        + node.ToolId.ToString(CultureInfo.InvariantCulture)
                        + " nodeId="
                        + node.NodeId.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// 新 Gather/Tool 概率字段专用：必须存在且为 JSON 数值；不改 <see cref="SimpleJson.Obj.GetDouble"/> 兼容行为。
        /// </summary>
        static double RequireJsonChanceNumber(SimpleJson.Obj row, string field, string context)
        {
            try
            {
                return row.RequireJsonNumber(field);
            }
            catch (FormatException)
            {
                throw new ConfigLoadException(
                    context + "." + field + " 必须存在且为 JSON 数值");
            }
        }

        /// <summary>
        /// 将 [0,1] 概率转为 ppm；0→0、1→Scale；禁止 NaN/Infinity/越界。
        /// Round AwayFromZero，避免 0% 因截断变正或 100% 落空。
        /// </summary>
        internal static int ChanceToPpm(double chance, string fieldName)
        {
            if (double.IsNaN(chance) || double.IsInfinity(chance))
                throw new ConfigLoadException(fieldName + " 非法: NaN/Infinity");
            if (chance < 0d || chance > 1d)
            {
                throw new ConfigLoadException(
                    fieldName + " 须落在 [0,1]，实际=" + chance.ToString(CultureInfo.InvariantCulture));
            }
            if (chance <= 0d)
                return 0;
            if (chance >= 1d)
                return ToolLevelSpec.ChanceScalePpm;
            int ppm = (int)Math.Round(chance * ToolLevelSpec.ChanceScalePpm, MidpointRounding.AwayFromZero);
            if (ppm < 0)
                ppm = 0;
            if (ppm > ToolLevelSpec.ChanceScalePpm)
                ppm = ToolLevelSpec.ChanceScalePpm;
            return ppm;
        }

        static IReadOnlyDictionary<int, GatherNodeSpec> FreezeGatherNodes(
            IReadOnlyDictionary<int, GatherNodeSpec>? source)
        {
            if (source == null || source.Count == 0)
                return new ReadOnlyDictionary<int, GatherNodeSpec>(new Dictionary<int, GatherNodeSpec>());
            var copy = new Dictionary<int, GatherNodeSpec>(source.Count);
            foreach (KeyValuePair<int, GatherNodeSpec> kv in source)
            {
                GatherNodeSpec row = kv.Value
                    ?? throw new ArgumentException("gatherNodes 含 null", nameof(source));
                if (kv.Key != row.NodeId)
                    throw new ArgumentException("gatherNodes key 须等于 NodeId", nameof(source));
                if (copy.ContainsKey(kv.Key))
                    throw new ArgumentException("gatherNodes 重复", nameof(source));
                copy.Add(kv.Key, new GatherNodeSpec(
                    row.NodeId, row.ToolId, row.UnlockLevel, row.Count, row.ItemId,
                    row.YieldMin, row.YieldMax, row.CooldownSec, row.RareItemId,
                    row.RareChancePpm, row.Exp));
            }
            return new ReadOnlyDictionary<int, GatherNodeSpec>(copy);
        }

        static IReadOnlyDictionary<long, ToolLevelSpec> FreezeToolLevels(
            IReadOnlyDictionary<long, ToolLevelSpec>? source)
        {
            if (source == null || source.Count == 0)
                return new ReadOnlyDictionary<long, ToolLevelSpec>(new Dictionary<long, ToolLevelSpec>());
            var copy = new Dictionary<long, ToolLevelSpec>(source.Count);
            foreach (KeyValuePair<long, ToolLevelSpec> kv in source)
            {
                ToolLevelSpec row = kv.Value
                    ?? throw new ArgumentException("toolLevels 含 null", nameof(source));
                long expected = ToolLevelSpec.PackKey(row.ToolId, row.Level);
                if (kv.Key != expected)
                    throw new ArgumentException("toolLevels key 须等于 PackKey(toolId,level)", nameof(source));
                if (copy.ContainsKey(kv.Key))
                    throw new ArgumentException("toolLevels 重复", nameof(source));
                // 必须保留 HasCompleteUpgradeCost：禁止把旧六参数不完整投影洗成“完整空材料”。
                copy.Add(kv.Key, new ToolLevelSpec(
                    row.ToolId, row.Level, row.NeedPlayerLevel, row.GoldCost,
                    row.YieldBonusMax, row.RareChancePpm, row.Materials, row.HasCompleteUpgradeCost));
            }
            return new ReadOnlyDictionary<long, ToolLevelSpec>(copy);
        }

        static IReadOnlyList<PlotStageRow> FreezePlotStages(IReadOnlyList<PlotStageRow> source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<PlotStageRow>());

            var copy = new PlotStageRow[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                PlotStageRow row = source[i] ?? throw new ArgumentException("plotStages 含 null", nameof(source));
                if (row.Stage != i)
                {
                    throw new ConfigLoadException(
                        "PlotStages 须按 stage 0..n-1 连续，index="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " stage="
                        + row.Stage.ToString(CultureInfo.InvariantCulture));
                }
                copy[i] = new PlotStageRow(row.Stage, row.NeedPlayerLevel, row.TotalPlots, row.UnlockGold);
            }

            if (copy[0].UnlockGold != 0)
                throw new ConfigLoadException("PlotStages stage0.unlockGold 必须为 0");

            for (int i = 1; i < copy.Length; i++)
            {
                if (copy[i].TotalPlots <= copy[i - 1].TotalPlots)
                    throw new ConfigLoadException("PlotStages totalPlots 须严格递增");
                if (copy[i].NeedPlayerLevel < copy[i - 1].NeedPlayerLevel)
                    throw new ConfigLoadException("PlotStages needPlayerLevel 不得下降");
                if (copy[i].UnlockGold <= 0)
                    throw new ConfigLoadException("PlotStages 付费阶段 unlockGold 必须为正");
            }

            return Array.AsReadOnly(copy);
        }

        /// <summary>兼容旧调用：仅取 max totalPlots；正式装载请用 <see cref="LoadPlotStages"/>。</summary>
        static int LoadMaxPlotTotal(string path)
        {
            IReadOnlyList<PlotStageRow> stages = LoadPlotStages(path);
            return stages[stages.Count - 1].TotalPlots;
        }

        static List<SimpleJson.Obj> ReadArray(string path)
        {
            if (!File.Exists(path))
                throw new ConfigLoadException("缺少配置文件: " + path);
            string text = File.ReadAllText(path, Encoding.UTF8);
            try
            {
                return SimpleJson.ParseArray(text);
            }
            catch (FormatException ex)
            {
                throw new ConfigLoadException("配置 JSON 非法: " + path + " — " + ex.Message);
            }
        }

        static int RequirePositiveInt(SimpleJson.Obj row, string field, string context)
        {
            long v = row.RequireLong(field);
            if (v <= 0)
                throw new ConfigLoadException(context + "." + field + " 必须为正 int");
            return ToConfigInt32(v, context + "." + field);
        }

        static int RequireNonNegativeInt(SimpleJson.Obj row, string field, string context)
        {
            long v = row.RequireLong(field);
            if (v < 0)
                throw new ConfigLoadException(context + "." + field + " 不能为负");
            return ToConfigInt32(v, context + "." + field);
        }

        static IReadOnlyList<int> ParseIdList(string raw, string context)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.AsReadOnly(Array.Empty<int>());
            // 与材料串同严：不丢弃空段
            string[] parts = raw.Split(new[] { ';' }, StringSplitOptions.None);
            var list = new List<int>(parts.Length);
            var seen = new HashSet<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0)
                    throw new ConfigLoadException(context + " 含空段");
                if (!long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out long idLong)
                    || idLong <= 0)
                    throw new ConfigLoadException(context + " 含非法 id: " + p);
                int id = ToConfigInt32(idLong, context);
                if (!seen.Add(id))
                    throw new ConfigLoadException(context + " 重复 id: " + id.ToString(CultureInfo.InvariantCulture));
                list.Add(id);
            }
            return Array.AsReadOnly(list.ToArray());
        }

        static IReadOnlyDictionary<int, ShopRow> FreezeShops(IReadOnlyDictionary<int, ShopRow> source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            var copy = new Dictionary<int, ShopRow>(source.Count);
            foreach (KeyValuePair<int, ShopRow> pair in source)
                copy.Add(pair.Key, pair.Value);
            return new ReadOnlyDictionary<int, ShopRow>(copy);
        }

        static IReadOnlyDictionary<int, MapInstanceRow> FreezeMap(IReadOnlyDictionary<int, MapInstanceRow> source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            var copy = new Dictionary<int, MapInstanceRow>(source.Count);
            foreach (KeyValuePair<int, MapInstanceRow> pair in source)
                copy.Add(pair.Key, pair.Value);
            return new ReadOnlyDictionary<int, MapInstanceRow>(copy);
        }

        static IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> FreezeMaterials(
            IReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>> source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            var copy = new Dictionary<string, IReadOnlyList<ItemStackSpec>>(source.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, IReadOnlyList<ItemStackSpec>> pair in source)
            {
                var arr = new ItemStackSpec[pair.Value.Count];
                for (int i = 0; i < pair.Value.Count; i++)
                    arr[i] = pair.Value[i];
                copy.Add(pair.Key, Array.AsReadOnly(arr));
            }
            return new ReadOnlyDictionary<string, IReadOnlyList<ItemStackSpec>>(copy);
        }
    }
}
