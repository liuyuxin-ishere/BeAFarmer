using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;

namespace CloudIsle.Domain.Farm
{
    /// <summary>只读投影用呈现态；Ripe 可为 deadline 投影，不要求落库。</summary>
    public enum PlotPresentation
    {
        Locked = 0,
        Empty = 1,
        Growing = 2,
        Ripe = 3,
        Unknown = 99,
    }

    /// <summary>解锁意图：无经济/目标字段；下一阶段与 plotId 由服务器按配置选择。</summary>
    public readonly struct UnlockPlotIntent
    {
        public static UnlockPlotIntent Default => default;
    }

    /// <summary>农场规则输入：等级/金币/地块视图；不持有可变聚合引用。</summary>
    public sealed class FarmState
    {
        public FarmState(int level, long gold, IReadOnlyList<PlotStateView> plots)
        {
            if (level < 0)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (gold < 0)
                throw new ArgumentOutOfRangeException(nameof(gold));
            if (plots == null)
                throw new ArgumentNullException(nameof(plots));

            Level = level;
            Gold = gold;
            Plots = FreezePlots(plots);
        }

        public int Level { get; }
        public long Gold { get; }
        public IReadOnlyList<PlotStateView> Plots { get; }

        public static FarmState FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return new FarmState(aggregate.Level, aggregate.Gold, aggregate.Plots);
        }

        public long Fingerprint()
        {
            unchecked
            {
                long h = Level;
                h = (h * 397) ^ Gold;
                h = (h * 397) ^ Plots.Count;
                for (int i = 0; i < Plots.Count; i++)
                {
                    PlotStateView p = Plots[i];
                    h = (h * 397) ^ p.PlotId;
                    h = (h * 397) ^ (int)p.State;
                    h = (h * 397) ^ p.CropId;
                    h = (h * 397) ^ p.PlantTime;
                    h = (h * 397) ^ p.ReadyTime;
                    h = (h * 397) ^ (p.Watered ? 1 : 0);
                    h = (h * 397) ^ (p.Fertilized ? 1 : 0);
                }
                return h;
            }
        }

        static IReadOnlyList<PlotStateView> FreezePlots(IReadOnlyList<PlotStateView> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<PlotStateView>());
            var arr = new PlotStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的地块阶段/稳定 ID 视图。
    /// 价格与档位仅来自阶段表；Map Plot 实例 ID 稳定排序（正式 1001..1024）。
    /// </summary>
    public sealed class PlotRules
    {
        /// <summary>非规范 legacy 兼容策略名（诊断原样记录；不得静默回落廉价档）。</summary>
        public const string CompatCanonicalPrefixStageTotal = "compat=canonical-prefix-stage-total";

        public PlotRules(IReadOnlyList<PlotStageRow> stages, IReadOnlyList<int> stablePlotIds)
        {
            if (stages == null || stages.Count == 0)
                throw new ArgumentException("stages required", nameof(stages));
            if (stablePlotIds == null || stablePlotIds.Count == 0)
                throw new ArgumentException("stablePlotIds required", nameof(stablePlotIds));

            var stageCopy = new PlotStageRow[stages.Count];
            for (int i = 0; i < stages.Count; i++)
            {
                PlotStageRow row = stages[i] ?? throw new ArgumentException("stages 含 null", nameof(stages));
                if (row.Stage != i)
                    throw new ArgumentException("stages 须按 stage 0..n-1", nameof(stages));
                stageCopy[i] = new PlotStageRow(row.Stage, row.NeedPlayerLevel, row.TotalPlots, row.UnlockGold);
            }

            if (stageCopy[0].UnlockGold != 0)
                throw new ArgumentException("stage0.unlockGold 必须为 0", nameof(stages));
            for (int i = 1; i < stageCopy.Length; i++)
            {
                if (stageCopy[i].TotalPlots <= stageCopy[i - 1].TotalPlots)
                    throw new ArgumentException("totalPlots 须严格递增", nameof(stages));
                if (stageCopy[i].NeedPlayerLevel < stageCopy[i - 1].NeedPlayerLevel)
                    throw new ArgumentException("needPlayerLevel 不得下降", nameof(stages));
                if (stageCopy[i].UnlockGold <= 0)
                    throw new ArgumentException("付费阶段 unlockGold 必须为正", nameof(stages));
            }

            int maxTotal = stageCopy[stageCopy.Length - 1].TotalPlots;
            if (stablePlotIds.Count != maxTotal)
            {
                throw new ArgumentException(
                    "stablePlotIds.Count 须等于 max totalPlots=" + maxTotal, nameof(stablePlotIds));
            }

            var idCopy = new int[stablePlotIds.Count];
            var seen = new HashSet<int>();
            for (int i = 0; i < stablePlotIds.Count; i++)
            {
                int id = stablePlotIds[i];
                if (id <= 0)
                    throw new ArgumentException("stablePlotIds 必须为正", nameof(stablePlotIds));
                if (!seen.Add(id))
                    throw new ArgumentException("stablePlotIds 重复", nameof(stablePlotIds));
                if (i > 0 && idCopy[i - 1] >= id)
                    throw new ArgumentException("stablePlotIds 须严格升序", nameof(stablePlotIds));
                idCopy[i] = id;
            }

            Stages = Array.AsReadOnly(stageCopy);
            StablePlotIds = Array.AsReadOnly(idCopy);
            MaxTotalPlots = maxTotal;
            InitialTotalPlots = stageCopy[0].TotalPlots;
        }

        public IReadOnlyList<PlotStageRow> Stages { get; }
        public IReadOnlyList<int> StablePlotIds { get; }
        public int MaxTotalPlots { get; }
        public int InitialTotalPlots { get; }

        public static PlotRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.PlotStages.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing PlotStages; use LoadDirectory");

            var plotIds = new List<int>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in snapshot.MapInstances)
            {
                if (string.Equals(pair.Value.Kind, "Plot", StringComparison.Ordinal))
                    plotIds.Add(pair.Key);
            }
            plotIds.Sort();
            return new PlotRules(snapshot.PlotStages, plotIds);
        }

        public bool TryGetStageByTotal(int totalPlots, out PlotStageRow stage)
        {
            for (int i = 0; i < Stages.Count; i++)
            {
                if (Stages[i].TotalPlots == totalPlots)
                {
                    stage = Stages[i];
                    return true;
                }
            }
            stage = null!;
            return false;
        }

        public bool TryGetNextStage(int currentTotalPlots, out PlotStageRow next)
        {
            next = null!;
            if (!TryGetStageByTotal(currentTotalPlots, out PlotStageRow current))
                return false;
            int idx = current.Stage + 1;
            if (idx >= Stages.Count)
                return false;
            next = Stages[idx];
            return true;
        }
    }

    /// <summary>单地块播种意图：仅 plotId/seedId；产物/期限由服务器同份 Crop 决定。</summary>
    public readonly struct PlantIntent
    {
        public PlantIntent(int plotId, int seedId)
        {
            PlotId = plotId;
            SeedId = seedId;
        }

        public int PlotId { get; }
        public int SeedId { get; }
    }

    /// <summary>单地块照料意图（浇水/施肥）：仅 plotId；比例与消耗由服务器规则决定。</summary>
    public readonly struct PlotIntent
    {
        public PlotIntent(int plotId)
        {
            PlotId = plotId;
        }

        public int PlotId { get; }
    }

    /// <summary>
    /// 播种规则输入：等级/背包/地块/已接受规格视图；不持有可变聚合引用。
    /// </summary>
    public sealed class FarmInput
    {
        public FarmInput(
            int level,
            IReadOnlyList<BagItemView> bag,
            IReadOnlyList<PlotStateView> plots,
            AcceptedTaskState acceptedTasks)
        {
            if (level < 0)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (bag == null)
                throw new ArgumentNullException(nameof(bag));
            if (plots == null)
                throw new ArgumentNullException(nameof(plots));
            if (acceptedTasks == null)
                throw new ArgumentNullException(nameof(acceptedTasks));

            Level = level;
            Bag = FreezeBag(bag);
            Plots = FreezePlots(plots);
            AcceptedTasks = acceptedTasks.Clone();
        }

        public int Level { get; }
        public IReadOnlyList<BagItemView> Bag { get; }
        public IReadOnlyList<PlotStateView> Plots { get; }
        public AcceptedTaskState AcceptedTasks { get; }

        public static FarmInput FromAggregate(PlayerAggregate aggregate)
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));
            return new FarmInput(
                aggregate.Level, aggregate.Bag, aggregate.Plots, aggregate.AcceptedTasks);
        }

        /// <summary>精确累加背包中 itemId 数量；负 count 视为畸形。</summary>
        public bool TryCountItem(int itemId, out long count, out bool malformed)
        {
            count = 0;
            malformed = false;
            if (itemId <= 0)
                return false;
            try
            {
                for (int i = 0; i < Bag.Count; i++)
                {
                    BagItemView row = Bag[i];
                    if (row.ItemId != itemId)
                        continue;
                    if (row.Count < 0)
                    {
                        malformed = true;
                        count = 0;
                        return false;
                    }

                    count = checked(count + row.Count);
                }
            }
            catch (OverflowException)
            {
                malformed = true;
                count = 0;
                return false;
            }

            return true;
        }

        public long Fingerprint()
        {
            unchecked
            {
                long h = Level;
                h = (h * 397) ^ Bag.Count;
                for (int i = 0; i < Bag.Count; i++)
                {
                    h = (h * 397) ^ Bag[i].ItemId;
                    h = (h * 397) ^ Bag[i].Count;
                }

                h = (h * 397) ^ Plots.Count;
                for (int i = 0; i < Plots.Count; i++)
                {
                    PlotStateView p = Plots[i];
                    h = (h * 397) ^ p.PlotId;
                    h = (h * 397) ^ (int)p.State;
                    h = (h * 397) ^ p.CropId;
                    h = (h * 397) ^ p.PlantTime;
                    h = (h * 397) ^ p.ReadyTime;
                    h = (h * 397) ^ (p.Watered ? 1 : 0);
                    h = (h * 397) ^ (p.Fertilized ? 1 : 0);
                }

                h = (h * 397) ^ AcceptedTasks.GetHashCode();
                return h;
            }
        }

        static IReadOnlyList<BagItemView> FreezeBag(IReadOnlyList<BagItemView> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<BagItemView>());
            var arr = new BagItemView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<PlotStateView> FreezePlots(IReadOnlyList<PlotStateView> source)
        {
            if (source.Count == 0)
                return Array.AsReadOnly(Array.Empty<PlotStateView>());
            var arr = new PlotStateView[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>
    /// 同份 <see cref="GameConfigSnapshot"/> 的作物/种子/稳定地块身份视图。
    /// 种子→作物映射唯一；配置重复 seed 拒绝装载。
    /// </summary>
    public sealed class CropRules
    {
        public CropRules(
            int configVersion,
            IReadOnlyDictionary<int, CropSpec> bySeedId,
            IReadOnlyCollection<int> stablePlotIds)
        {
            if (configVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(configVersion));
            if (bySeedId == null || bySeedId.Count == 0)
                throw new ArgumentException("crops by seed required", nameof(bySeedId));
            if (stablePlotIds == null || stablePlotIds.Count == 0)
                throw new ArgumentException("stablePlotIds required", nameof(stablePlotIds));

            var seedMap = new Dictionary<int, CropSpec>(bySeedId.Count);
            foreach (KeyValuePair<int, CropSpec> kv in bySeedId)
            {
                CropSpec crop = kv.Value ?? throw new ArgumentException("crop null", nameof(bySeedId));
                if (kv.Key <= 0 || crop.SeedId != kv.Key)
                    throw new ArgumentException("bySeedId 键须等于 CropSpec.SeedId", nameof(bySeedId));
                if (!seedMap.TryAdd(kv.Key, new CropSpec(
                        crop.CropId, crop.SeedId, crop.YieldItemId, crop.UnlockLevel,
                        crop.GrowSec, crop.YieldMin, crop.YieldMax, crop.Exp)))
                    throw new ArgumentException("重复 seedId", nameof(bySeedId));
            }

            var idList = new List<int>();
            var seen = new HashSet<int>();
            foreach (int id in stablePlotIds)
            {
                if (id <= 0)
                    throw new ArgumentException("stablePlotIds 必须为正", nameof(stablePlotIds));
                if (!seen.Add(id))
                    throw new ArgumentException("stablePlotIds 重复", nameof(stablePlotIds));
                idList.Add(id);
            }

            idList.Sort();
            ConfigVersion = configVersion;
            BySeedId = new System.Collections.ObjectModel.ReadOnlyDictionary<int, CropSpec>(seedMap);
            StablePlotIds = Array.AsReadOnly(idList.ToArray());
            _stablePlotSet = seen;
        }

        readonly HashSet<int> _stablePlotSet;

        public int ConfigVersion { get; }
        public IReadOnlyDictionary<int, CropSpec> BySeedId { get; }
        /// <summary>稳定 Plot 实例 ID（严格升序）。</summary>
        public IReadOnlyList<int> StablePlotIds { get; }

        public static CropRules FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Crops.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing Crops; use LoadDirectory");

            var bySeed = new Dictionary<int, CropSpec>();
            foreach (KeyValuePair<int, CropSpec> kv in snapshot.Crops)
            {
                CropSpec crop = kv.Value;
                if (crop.CropId != kv.Key)
                    throw new InvalidOperationException("Crops 键须等于 cropId");
                if (!bySeed.TryAdd(crop.SeedId, crop))
                {
                    throw new InvalidOperationException(
                        "duplicate seedId in Crops: " + crop.SeedId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            var plotIds = new List<int>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in snapshot.MapInstances)
            {
                if (string.Equals(pair.Value.Kind, "Plot", StringComparison.Ordinal))
                    plotIds.Add(pair.Key);
            }

            if (plotIds.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing Plot map instances");
            return new CropRules(snapshot.Version, bySeed, plotIds);
        }

        public bool TryGetBySeed(int seedId, out CropSpec crop)
        {
            if (BySeedId.TryGetValue(seedId, out CropSpec? found) && found != null)
            {
                crop = found;
                return true;
            }

            crop = null!;
            return false;
        }

        public bool IsStablePlot(int plotId) => plotId > 0 && _stablePlotSet.Contains(plotId);
    }

    /// <summary>农场命令：Unlock / 单地块 Plant / Water / Fertilize / Harvest；批量见 BatchIntent/FarmBatchExecutor。</summary>
    public sealed class FarmCommand
    {
        FarmCommand(FarmCommandKind kind, PlantIntent? plantIntent, PlotIntent? plotIntent)
        {
            Kind = kind;
            PlantIntent = plantIntent;
            PlotIntent = plotIntent;
        }

        public FarmCommandKind Kind { get; }
        public PlantIntent? PlantIntent { get; }
        public PlotIntent? PlotIntent { get; }

        public static FarmCommand Unlock()
            => new FarmCommand(FarmCommandKind.Unlock, plantIntent: null, plotIntent: null);

        public static FarmCommand Plant(PlantIntent intent)
            => new FarmCommand(FarmCommandKind.Plant, intent, plotIntent: null);

        public static FarmCommand Water(PlotIntent intent)
            => new FarmCommand(FarmCommandKind.Water, plantIntent: null, intent);

        public static FarmCommand Fertilize(PlotIntent intent)
            => new FarmCommand(FarmCommandKind.Fertilize, plantIntent: null, intent);

        public static FarmCommand Harvest(PlotIntent intent)
            => new FarmCommand(FarmCommandKind.Harvest, plantIntent: null, intent);
    }

    public enum FarmCommandKind
    {
        Unlock = 1,
        Plant = 2,
        Water = 3,
        Fertilize = 4,
        Harvest = 5,
    }

    /// <summary>
    /// D07 纯照料结果：Water 成功含 watered+缩短后的 Growing 地块（无经济消耗）；
    /// Fertilize 成功另带须扣的肥料 itemId（恰好 1）。失败无效果。
    /// </summary>
    public sealed class FarmCareResult
    {
        FarmCareResult(
            bool success,
            int code,
            PlotStateView? plotAfter,
            int consumedFertilizerItemId,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            PlotAfter = plotAfter;
            ConsumedFertilizerItemId = consumedFertilizerItemId;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        public PlotStateView? PlotAfter { get; }
        /// <summary>Fertilize 成功时须扣的肥料 itemId（&gt;0）；Water/失败为 0。</summary>
        public int ConsumedFertilizerItemId { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>浇水成功（无肥料消耗）。既有调用保持此重载。</summary>
        public static FarmCareResult Ok(
            PlotStateView plotAfter,
            IReadOnlyList<string>? diagnostics = null)
        {
            return new FarmCareResult(
                true, 0, plotAfter, consumedFertilizerItemId: 0, FreezeDiagnostics(diagnostics));
        }

        /// <summary>施肥成功：须扣 <paramref name="fertilizerItemId"/> 恰好 1。</summary>
        public static FarmCareResult Ok(
            PlotStateView plotAfter,
            int fertilizerItemId,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (fertilizerItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(fertilizerItemId));
            return new FarmCareResult(
                true, 0, plotAfter, fertilizerItemId, FreezeDiagnostics(diagnostics));
        }

        public static FarmCareResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new FarmCareResult(
                false, code, plotAfter: null, consumedFertilizerItemId: 0, FreezeDiagnostics(diagnostics));
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

    /// <summary>
    /// D06 纯播种结果（计划表 RuleResult 的种植具体类型）：
    /// 成功含扣种、Growing 地块、完整 AcceptedHarvestSpec；失败无效果。
    /// </summary>
    public sealed class FarmPlantResult
    {
        FarmPlantResult(
            bool success,
            int code,
            int seedId,
            PlotStateView? plotAfter,
            AcceptedHarvestSpec? acceptedSpec,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            SeedId = seedId;
            PlotAfter = plotAfter;
            AcceptedSpec = acceptedSpec;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        /// <summary>成功时须扣的种子 itemId（恰好 1）。</summary>
        public int SeedId { get; }
        public PlotStateView? PlotAfter { get; }
        public AcceptedHarvestSpec? AcceptedSpec { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static FarmPlantResult Ok(
            int seedId,
            PlotStateView plotAfter,
            AcceptedHarvestSpec acceptedSpec,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (seedId <= 0)
                throw new ArgumentOutOfRangeException(nameof(seedId));
            if (acceptedSpec == null)
                throw new ArgumentNullException(nameof(acceptedSpec));
            return new FarmPlantResult(
                true, 0, seedId, plotAfter, acceptedSpec.Clone(), FreezeDiagnostics(diagnostics));
        }

        public static FarmPlantResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new FarmPlantResult(
                false, code, seedId: 0, plotAfter: null, acceptedSpec: null, FreezeDiagnostics(diagnostics));
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

    /// <summary>
    /// 纯解锁结果：GoldCost + 有限 PlotReplace（Locked→Empty）；失败无效果。
    /// </summary>
    public sealed class FarmUnlockResult
    {
        FarmUnlockResult(
            bool success,
            int code,
            long goldCost,
            IReadOnlyList<PlotStateView> unlockedPlots,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            GoldCost = goldCost;
            UnlockedPlots = unlockedPlots ?? Array.AsReadOnly(Array.Empty<PlotStateView>());
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        public long GoldCost { get; }
        /// <summary>本阶段新解锁为空地的规范行（供 PlotReplacePatch）。</summary>
        public IReadOnlyList<PlotStateView> UnlockedPlots { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static FarmUnlockResult Ok(
            long goldCost,
            IReadOnlyList<PlotStateView> unlockedPlots,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (goldCost <= 0)
                throw new ArgumentOutOfRangeException(nameof(goldCost));
            if (unlockedPlots == null || unlockedPlots.Count == 0)
                throw new ArgumentException("unlockedPlots required", nameof(unlockedPlots));
            return new FarmUnlockResult(true, 0, goldCost, FreezePlots(unlockedPlots), FreezeDiagnostics(diagnostics));
        }

        public static FarmUnlockResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            return new FarmUnlockResult(
                false, code, goldCost: 0, Array.AsReadOnly(Array.Empty<PlotStateView>()), FreezeDiagnostics(diagnostics));
        }

        static IReadOnlyList<PlotStateView> FreezePlots(IReadOnlyList<PlotStateView> source)
        {
            var arr = new PlotStateView[source.Count];
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
