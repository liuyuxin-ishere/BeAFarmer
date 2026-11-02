using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CloudIsle.Config;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Inventory;
using CloudIsle.Domain.Reward;

namespace CloudIsle.Domain.Trade
{
    /// <summary>单行出售意图（正 itemId / 正 count）。</summary>
    public readonly struct SellLine : IEquatable<SellLine>
    {
        public SellLine(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }

        public bool Equals(SellLine other) => ItemId == other.ItemId && Count == other.Count;
        public override bool Equals(object? obj) => obj is SellLine other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ItemId, Count);
    }

    /// <summary>出售意图列表；Policy 拒空表/超限/重复身份（整包无效，不合并）。</summary>
    public sealed class SellIntent
    {
        public SellIntent(IReadOnlyList<SellLine> lines)
        {
            Lines = Freeze(lines);
        }

        public IReadOnlyList<SellLine> Lines { get; }

        static IReadOnlyList<SellLine> Freeze(IReadOnlyList<SellLine> source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<SellLine>());
            var arr = new SellLine[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>购买意图：仅 shopId/count；价格/币种/限购/等级只从 ShopRules 取。</summary>
    public readonly struct BuyIntent : IEquatable<BuyIntent>
    {
        public BuyIntent(int shopId, int count)
        {
            ShopId = shopId;
            Count = count;
        }

        public int ShopId { get; }
        public int Count { get; }

        public bool Equals(BuyIntent other) => ShopId == other.ShopId && Count == other.Count;
        public override bool Equals(object? obj) => obj is BuyIntent other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ShopId, Count);
    }

    /// <summary>交易只读状态：货币 + 等级 + 库存 + 当日商店计数；不别名源集合。</summary>
    public sealed class TradeState
    {
        public TradeState(
            long gold,
            InventoryState inventory,
            int level = 1,
            ShopPurchaseCounts? shopPurchases = null,
            long gem = 0,
            long ticket = 0,
            long shard = 0)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            Gold = gold;
            Gem = gem;
            Ticket = ticket;
            Shard = shard;
            Level = level;
            Inventory = inventory;
            ShopPurchases = shopPurchases?.Clone() ?? ShopPurchaseCounts.Empty;
        }

        public long Gold { get; }
        public long Gem { get; }
        public long Ticket { get; }
        public long Shard { get; }
        public int Level { get; }
        public InventoryState Inventory { get; }
        public ShopPurchaseCounts ShopPurchases { get; }

        public static TradeState FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return new TradeState(
                aggregate.Gold,
                InventoryState.FromAggregate(aggregate),
                aggregate.Level,
                aggregate.ShopPurchases,
                aggregate.Gem,
                aggregate.Ticket,
                aggregate.Shard);
        }

        public long Fingerprint()
        {
            unchecked
            {
                long h = Gold;
                h = (h * 31) + Gem;
                h = (h * 31) + Ticket;
                h = (h * 31) + Shard;
                h = (h * 31) + Level;
                h = (h * 31) + Inventory.Fingerprint();
                h = (h * 31) + ShopPurchases.GetHashCode();
                return h;
            }
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的商店规则视图：价格/币种/等级/限购不可变；
    /// 客户端不得注入价；limitCount=0 表示不限。
    /// </summary>
    public sealed class ShopRules
    {
        readonly IReadOnlyDictionary<int, ShopOffer> _offers;
        readonly IReadOnlyDictionary<int, CurrencyKind> _currencyByItemId;
        readonly HashSet<int> _physicalItems;

        public ShopRules(
            IReadOnlyDictionary<int, ShopOffer> offers,
            IReadOnlyDictionary<int, CurrencyKind> currencyByItemId,
            IReadOnlyDictionary<int, int> physicalStackMax)
        {
            if (offers == null)
                throw new ArgumentNullException(nameof(offers));
            if (currencyByItemId == null)
                throw new ArgumentNullException(nameof(currencyByItemId));
            if (physicalStackMax == null)
                throw new ArgumentNullException(nameof(physicalStackMax));

            var copy = new Dictionary<int, ShopOffer>(offers.Count);
            foreach (KeyValuePair<int, ShopOffer> pair in offers)
            {
                if (pair.Key <= 0 || pair.Value == null || pair.Value.ShopId != pair.Key)
                    throw new ArgumentException("ShopOffer 非法", nameof(offers));
                copy.Add(pair.Key, pair.Value);
            }

            var currency = new Dictionary<int, CurrencyKind>(currencyByItemId.Count);
            foreach (KeyValuePair<int, CurrencyKind> pair in currencyByItemId)
            {
                if (pair.Key <= 0)
                    throw new ArgumentException("currency itemId 非法", nameof(currencyByItemId));
                if (!IsKnownCurrencyKind(pair.Value))
                    throw new ArgumentException("currency kind 非法", nameof(currencyByItemId));
                currency.Add(pair.Key, pair.Value);
            }

            // Offer 的 PriceCurrencyItemId 必须在不可变货币视图中，且 Kind 与视图一致；禁止缺失/错位后靠 Buy 回落 Gold。
            foreach (KeyValuePair<int, ShopOffer> pair in copy)
            {
                ShopOffer offer = pair.Value;
                if (!IsKnownCurrencyKind(offer.PriceCurrencyKind))
                    throw new ArgumentException("ShopOffer PriceCurrencyKind 非法", nameof(offers));
                if (!currency.TryGetValue(offer.PriceCurrencyItemId, out CurrencyKind mapped)
                    || mapped != offer.PriceCurrencyKind)
                {
                    throw new ArgumentException(
                        "ShopOffer currency metadata 与 currencyByItemId 不一致或缺失", nameof(offers));
                }
            }

            _physicalItems = new HashSet<int>();
            foreach (KeyValuePair<int, int> pair in physicalStackMax)
            {
                if (pair.Key <= 0 || pair.Value <= 0)
                    throw new ArgumentException("physicalStackMax 非法", nameof(physicalStackMax));
                _physicalItems.Add(pair.Key);
            }

            _offers = new ReadOnlyDictionary<int, ShopOffer>(copy);
            _currencyByItemId = new ReadOnlyDictionary<int, CurrencyKind>(currency);
            Offers = _offers;
        }

        /// <summary>正式四币种；未定义枚举值不得参与计价。</summary>
        public static bool IsKnownCurrencyKind(CurrencyKind kind)
            => kind is CurrencyKind.Gold or CurrencyKind.Gem or CurrencyKind.Ticket or CurrencyKind.Shard;

        public IReadOnlyDictionary<int, ShopOffer> Offers { get; }

        public bool TryGetOffer(int shopId, out ShopOffer offer)
            => _offers.TryGetValue(shopId, out offer!);

        public bool TryGetCurrencyKind(int currencyItemId, out CurrencyKind kind)
            => _currencyByItemId.TryGetValue(currencyItemId, out kind);

        public bool IsPhysicalRewardItem(int itemId) => _physicalItems.Contains(itemId);

        public static ShopRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.ItemStackMax.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing ItemStackMax; use LoadDirectory");

            RewardRules reward = RewardRules.FromSnapshot(snapshot);
            var currency = new Dictionary<int, CurrencyKind>();
            for (int i = 0; i < reward.CurrencyItemIds.Count; i++)
            {
                int id = reward.CurrencyItemIds[i];
                if (!reward.TryGetCurrencyKind(id, out CurrencyKind kind))
                    throw new InvalidOperationException("currency map incomplete");
                currency.Add(id, kind);
            }

            var offers = new Dictionary<int, ShopOffer>();
            foreach (KeyValuePair<int, ShopRow> pair in snapshot.Shops)
            {
                ShopRow row = pair.Value;
                if (!currency.TryGetValue(row.PriceCurrency, out CurrencyKind kind))
                    throw new InvalidOperationException(
                        "Shop priceCurrency 非已知货币: " + row.PriceCurrency.ToString(CultureInfo.InvariantCulture));
                if (!snapshot.ItemStackMax.ContainsKey(row.ItemId))
                    throw new InvalidOperationException(
                        "Shop itemId 非物理面: " + row.ItemId.ToString(CultureInfo.InvariantCulture));
                // BagExpand/Furniture/Avatar 正式表不得出现；LoadShops 已拒。
                offers.Add(row.Id, new ShopOffer(
                    row.Id, row.Category, row.ItemId, row.PriceCurrency, kind,
                    row.Price, row.LimitCount, row.UnlockLevel));
            }

            return new ShopRules(offers, currency, snapshot.ItemStackMax);
        }
    }

    /// <summary>不可变商店报价行（服务器权威）。</summary>
    public sealed class ShopOffer
    {
        public ShopOffer(
            int shopId,
            string category,
            int itemId,
            int priceCurrencyItemId,
            CurrencyKind priceCurrencyKind,
            int unitPrice,
            int limitCount,
            int unlockLevel)
        {
            if (shopId <= 0)
                throw new ArgumentOutOfRangeException(nameof(shopId));
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("category required", nameof(category));
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId));
            if (priceCurrencyItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(priceCurrencyItemId));
            if (unitPrice < 0)
                throw new ArgumentOutOfRangeException(nameof(unitPrice));
            if (limitCount < 0)
                throw new ArgumentOutOfRangeException(nameof(limitCount));
            if (unlockLevel < 0)
                throw new ArgumentOutOfRangeException(nameof(unlockLevel));

            ShopId = shopId;
            Category = category;
            ItemId = itemId;
            PriceCurrencyItemId = priceCurrencyItemId;
            PriceCurrencyKind = priceCurrencyKind;
            UnitPrice = unitPrice;
            LimitCount = limitCount;
            UnlockLevel = unlockLevel;
        }

        public int ShopId { get; }
        public string Category { get; }
        public int ItemId { get; }
        public int PriceCurrencyItemId { get; }
        public CurrencyKind PriceCurrencyKind { get; }
        public int UnitPrice { get; }
        /// <summary>0 = 不限购。</summary>
        public int LimitCount { get; }
        public int UnlockLevel { get; }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的出售规则视图：仅物理正售价可卖；
    /// Currency / 未知 / sellPrice≤0 / 缺售价面 → 不可售。
    /// </summary>
    public sealed class ItemRules
    {
        public const int DefaultMaxSellEntries = InventoryRules.DefaultMaxDeltaEntries;

        readonly HashSet<int> _knownAll;
        readonly HashSet<int> _physical;
        readonly IReadOnlyDictionary<int, int> _sellPrice;

        public ItemRules(
            IReadOnlyList<int> knownItemIds,
            IReadOnlyDictionary<int, int> physicalStackMax,
            IReadOnlyDictionary<int, int> sellPriceByItemId,
            int maxSellEntries = DefaultMaxSellEntries)
        {
            if (knownItemIds == null)
                throw new ArgumentNullException(nameof(knownItemIds));
            if (physicalStackMax == null)
                throw new ArgumentNullException(nameof(physicalStackMax));
            if (sellPriceByItemId == null)
                throw new ArgumentNullException(nameof(sellPriceByItemId));
            if (maxSellEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSellEntries));

            _knownAll = new HashSet<int>();
            for (int i = 0; i < knownItemIds.Count; i++)
            {
                int id = knownItemIds[i];
                if (id <= 0)
                    throw new ArgumentException("knownItemIds 必须为正", nameof(knownItemIds));
                _knownAll.Add(id);
            }

            _physical = new HashSet<int>();
            foreach (KeyValuePair<int, int> pair in physicalStackMax)
            {
                if (pair.Key <= 0 || pair.Value <= 0)
                    throw new ArgumentException("physicalStackMax 非法", nameof(physicalStackMax));
                // 物理面必须 ⊆ known：禁止用售价/堆叠视图把 unknown retained legacy 洗成可售。
                if (!_knownAll.Contains(pair.Key))
                    throw new ArgumentException("physicalStackMax 含未知 id", nameof(physicalStackMax));
                _physical.Add(pair.Key);
            }

            var copy = new Dictionary<int, int>(sellPriceByItemId.Count);
            foreach (KeyValuePair<int, int> pair in sellPriceByItemId)
            {
                if (pair.Key <= 0 || pair.Value <= 0)
                    throw new ArgumentException("sellPrice 必须为正", nameof(sellPriceByItemId));
                if (!_physical.Contains(pair.Key))
                    throw new ArgumentException("sellPrice 非物理面", nameof(sellPriceByItemId));
                copy.Add(pair.Key, pair.Value);
            }

            _sellPrice = new ReadOnlyDictionary<int, int>(copy);
            MaxSellEntries = maxSellEntries;
            SellPriceByItemId = _sellPrice;
        }

        public int MaxSellEntries { get; }
        public IReadOnlyDictionary<int, int> SellPriceByItemId { get; }

        public bool IsKnownItem(int itemId) => _knownAll.Contains(itemId);

        public bool IsPhysicalItem(int itemId) => _physical.Contains(itemId);

        public bool IsCurrencyItem(int itemId) => _knownAll.Contains(itemId) && !_physical.Contains(itemId);

        public bool TryGetSellPrice(int itemId, out int price)
        {
            if (!_knownAll.Contains(itemId))
            {
                price = 0;
                return false;
            }
            return _sellPrice.TryGetValue(itemId, out price);
        }

        public bool IsSellable(int itemId)
            => _knownAll.Contains(itemId) && _sellPrice.ContainsKey(itemId);

        public static ItemRules FromSnapshot(
            GameConfigSnapshot snapshot,
            int maxSellEntries = DefaultMaxSellEntries)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            // 缺 ItemStackMax 视为未装载正式目录；售价面可空（旧构造）→ 无可售物，不臆造。
            if (snapshot.ItemStackMax.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing ItemStackMax; use LoadDirectory");
            return new ItemRules(
                snapshot.KnownItemIds,
                snapshot.ItemStackMax,
                snapshot.ItemSellPrice,
                maxSellEntries);
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> Global 扩容规则视图：init/step/costs 复制只读；
    /// MaxSlots = init + step × costs.Length（checked）；禁止硬编码经济数值。
    /// </summary>
    public sealed class ExpandRules
    {
        public ExpandRules(int initSlots, int step, IReadOnlyList<int> costs)
        {
            if (initSlots <= 0)
                throw new ArgumentOutOfRangeException(nameof(initSlots));
            if (step <= 0)
                throw new ArgumentOutOfRangeException(nameof(step));
            if (costs == null || costs.Count == 0)
                throw new ArgumentException("costs 不能为空", nameof(costs));

            var copy = new int[costs.Count];
            for (int i = 0; i < costs.Count; i++)
            {
                if (costs[i] <= 0)
                    throw new ArgumentException("costs 必须为正", nameof(costs));
                copy[i] = costs[i];
            }

            int max;
            try
            {
                max = checked(initSlots + step * costs.Count);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(costs), "MaxSlots 溢出");
            }

            if (max <= initSlots)
                throw new ArgumentOutOfRangeException(nameof(costs), "MaxSlots 必须大于 init");
            // 与 InventoryRules.DefaultMaxBagSlots 对齐，避免 Policy 通过后 Composer 容量面拒收。
            if (max > InventoryRules.DefaultMaxBagSlots)
                throw new ArgumentOutOfRangeException(nameof(costs), "MaxSlots 超过库存有界上限");

            InitSlots = initSlots;
            Step = step;
            Costs = Array.AsReadOnly(copy);
            MaxSlots = max;
        }

        public int InitSlots { get; }
        public int Step { get; }
        public IReadOnlyList<int> Costs { get; }
        public int MaxSlots { get; }

        /// <summary>
        /// 从同份快照 Globals 读取 bag_init_slots / bag_expand_step / bag_expand_costs；
        /// 缺失或非法显式失败，不回落默认经济数值。
        /// </summary>
        public static ExpandRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            IReadOnlyDictionary<string, string> globals = snapshot.Globals.Globals;
            int init = RequirePositiveGlobalInt(globals, "bag_init_slots");
            int step = RequirePositiveGlobalInt(globals, "bag_expand_step");
            IReadOnlyList<int> costs = RequirePositiveGlobalIntList(globals, "bag_expand_costs");
            return new ExpandRules(init, step, costs);
        }

        static int RequirePositiveGlobalInt(IReadOnlyDictionary<string, string> globals, string key)
        {
            if (!globals.TryGetValue(key, out string? raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value <= 0)
            {
                throw new InvalidOperationException("Global." + key + " 缺失或非法");
            }

            return value;
        }

        /// <summary>接受 <c>[500,1000,...]</c> 或分号分隔；禁止空段/非正/溢出。</summary>
        static IReadOnlyList<int> RequirePositiveGlobalIntList(
            IReadOnlyDictionary<string, string> globals,
            string key)
        {
            if (!globals.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Global." + key + " 缺失或非法");

            string text = raw.Trim();
            if (text.Length >= 2 && text[0] == '[' && text[text.Length - 1] == ']')
                text = text.Substring(1, text.Length - 2).Trim();

            char sep = text.IndexOf(';') >= 0 ? ';' : ',';
            string[] parts = text.Split(sep);
            if (parts.Length == 0)
                throw new InvalidOperationException("Global." + key + " 为空列表");

            var list = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0)
                    throw new InvalidOperationException("Global." + key + " 含空段");
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v <= 0)
                    throw new InvalidOperationException("Global." + key + " 含非法值: " + p);
                list[i] = v;
            }

            return Array.AsReadOnly(list);
        }
    }

    /// <summary>交易命令：Sell / Expand / Buy。</summary>
    public sealed class TradeCommand
    {
        TradeCommand(TradeCommandKind kind, SellIntent? sellIntent, BuyIntent? buyIntent)
        {
            Kind = kind;
            SellIntent = sellIntent;
            BuyIntent = buyIntent;
        }

        public TradeCommandKind Kind { get; }
        public SellIntent? SellIntent { get; }
        public BuyIntent? BuyIntent { get; }

        public static TradeCommand Sell(SellIntent intent)
        {
            if (intent == null)
                throw new ArgumentNullException(nameof(intent));
            return new TradeCommand(TradeCommandKind.Sell, intent, buyIntent: null);
        }

        public static TradeCommand Expand()
            => new TradeCommand(TradeCommandKind.Expand, sellIntent: null, buyIntent: null);

        public static TradeCommand Buy(BuyIntent intent)
            => new TradeCommand(TradeCommandKind.Buy, sellIntent: null, buyIntent: intent);
    }

    public enum TradeCommandKind
    {
        Sell = 1,
        Expand = 2,
        Buy = 3
    }

    /// <summary>
    /// 纯交易结果：Sell 用 ItemCosts+GoldReward；Expand 用 GoldCost+BagSlotsAfter；
    /// Buy 用货币成本+ItemRewards+ShopPurchasesAfter。失败无效果；不 Save/Ntf/revision。
    /// </summary>
    public sealed class TradeResult
    {
        TradeResult(
            bool success,
            int code,
            IReadOnlyList<ItemAmount> itemCosts,
            long goldReward,
            long goldCost,
            long gemCost,
            long ticketCost,
            long shardCost,
            int bagSlotsAfter,
            IReadOnlyList<ItemAmount> itemRewards,
            ShopPurchaseCounts? shopPurchasesAfter,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            ItemCosts = itemCosts ?? Array.AsReadOnly(Array.Empty<ItemAmount>());
            GoldReward = goldReward;
            GoldCost = goldCost;
            GemCost = gemCost;
            TicketCost = ticketCost;
            ShardCost = shardCost;
            BagSlotsAfter = bagSlotsAfter;
            ItemRewards = itemRewards ?? Array.AsReadOnly(Array.Empty<ItemAmount>());
            ShopPurchasesAfter = shopPurchasesAfter;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        /// <summary>正数量物品成本（供 Composer ItemCosts）。</summary>
        public IReadOnlyList<ItemAmount> ItemCosts { get; }
        public long GoldReward { get; }
        /// <summary>扣款（供 Composer；Buy/Expand）。</summary>
        public long GoldCost { get; }
        public long GemCost { get; }
        public long TicketCost { get; }
        public long ShardCost { get; }
        /// <summary>扩容后目标格数；出售/购买成功为 0（表示无容量效果）。</summary>
        public int BagSlotsAfter { get; }
        /// <summary>购买入包物品（供 Composer RewardBundle）。</summary>
        public IReadOnlyList<ItemAmount> ItemRewards { get; }
        /// <summary>购买成功后的商店计数；非 Buy 为 null。</summary>
        public ShopPurchaseCounts? ShopPurchasesAfter { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static TradeResult Ok(
            IReadOnlyList<ItemAmount> itemCosts,
            long goldReward,
            IReadOnlyList<string>? diagnostics = null)
        {
            return new TradeResult(
                true, 0, FreezeCosts(itemCosts), goldReward,
                goldCost: 0, gemCost: 0, ticketCost: 0, shardCost: 0, bagSlotsAfter: 0,
                Array.AsReadOnly(Array.Empty<ItemAmount>()), shopPurchasesAfter: null,
                FreezeDiagnostics(diagnostics));
        }

        public static TradeResult OkExpand(
            long goldCost,
            int bagSlotsAfter,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (goldCost <= 0)
                throw new ArgumentOutOfRangeException(nameof(goldCost));
            if (bagSlotsAfter <= 0)
                throw new ArgumentOutOfRangeException(nameof(bagSlotsAfter));
            return new TradeResult(
                true, 0, Array.AsReadOnly(Array.Empty<ItemAmount>()), goldReward: 0,
                goldCost, gemCost: 0, ticketCost: 0, shardCost: 0, bagSlotsAfter,
                Array.AsReadOnly(Array.Empty<ItemAmount>()), shopPurchasesAfter: null,
                FreezeDiagnostics(diagnostics));
        }

        public static TradeResult OkBuy(
            long goldCost,
            long gemCost,
            long ticketCost,
            long shardCost,
            IReadOnlyList<ItemAmount> itemRewards,
            ShopPurchaseCounts shopPurchasesAfter,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (shopPurchasesAfter == null)
                throw new ArgumentNullException(nameof(shopPurchasesAfter));
            if (goldCost < 0 || gemCost < 0 || ticketCost < 0 || shardCost < 0)
                throw new ArgumentOutOfRangeException("currency cost negative");
            if (goldCost == 0 && gemCost == 0 && ticketCost == 0 && shardCost == 0)
                throw new ArgumentException("buy must spend currency");
            return new TradeResult(
                true, 0, Array.AsReadOnly(Array.Empty<ItemAmount>()), goldReward: 0,
                goldCost, gemCost, ticketCost, shardCost, bagSlotsAfter: 0,
                FreezeCosts(itemRewards), shopPurchasesAfter,
                FreezeDiagnostics(diagnostics));
        }

        public static TradeResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new TradeResult(
                false,
                code,
                Array.AsReadOnly(Array.Empty<ItemAmount>()),
                goldReward: 0,
                goldCost: 0,
                gemCost: 0,
                ticketCost: 0,
                shardCost: 0,
                bagSlotsAfter: 0,
                Array.AsReadOnly(Array.Empty<ItemAmount>()),
                shopPurchasesAfter: null,
                FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<ItemAmount> FreezeCosts(IReadOnlyList<ItemAmount>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<ItemAmount>());
            var arr = new ItemAmount[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<string> FreezeDiagnostics(IReadOnlyList<string>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<string>());
            var arr = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i] ?? "";
            return Array.AsReadOnly(arr);
        }
    }
}
