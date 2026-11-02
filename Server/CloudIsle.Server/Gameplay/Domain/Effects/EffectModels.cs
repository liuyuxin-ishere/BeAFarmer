using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Random;
using CloudIsle.Domain.Reward;
using CloudIsle.Domain.Trade;

namespace CloudIsle.Domain.Effects
{
    /// <summary>单次命令固定上下文：同一 Config 快照；不重新 Capture。</summary>
    public sealed class CommandContext
    {
        public CommandContext(long nowMs, GameConfigSnapshot config, IRandomSource? random = null)
        {
            if (nowMs < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMs));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            NowMs = nowMs;
            ConfigVersion = config.Version;
            Random = random;
        }

        public long NowMs { get; }
        public int ConfigVersion { get; }
        public GameConfigSnapshot Config { get; }
        /// <summary>可选抽签源；Harvest/Gather 正式路径须非 null（规则注入生产默认或测试装饰）。</summary>
        public IRandomSource? Random { get; }
    }

    /// <summary>领域合成结果：一次候选或全无；不 Save/Ntf/自增 revision。</summary>
    public sealed class DomainResult<T> where T : class
    {
        DomainResult(bool success, int code, T? candidate, IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            Candidate = candidate;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        public T? Candidate { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static DomainResult<T> Ok(T candidate, IReadOnlyList<string>? diagnostics = null)
            => new(true, 0, candidate, FreezeDiagnostics(diagnostics));

        public static DomainResult<T> Fail(int code, IReadOnlyList<string>? diagnostics = null)
            => new(false, code, null, FreezeDiagnostics(diagnostics));

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
    /// 有界模块补丁（覆盖当前已存在聚合模块类型）。禁止无限制 delegate 绕过身份/revision/经济所有权。
    /// 不声称 Craft 槽位已接入。
    /// </summary>
    public abstract class AggregateModulePatch
    {
        private protected AggregateModulePatch()
        {
        }

        internal abstract bool TryApply(WorkingModules modules, List<string> diagnostics, out int code);
    }

    /// <summary>按 plotId 替换地块状态（如收获清地）。</summary>
    public sealed class PlotReplacePatch : AggregateModulePatch
    {
        public const string DiagMalformed = "malformed-plot-patch";

        public PlotReplacePatch(PlotStateView next)
        {
            Next = next;
        }

        public PlotStateView Next { get; }

        internal override bool TryApply(WorkingModules modules, List<string> diagnostics, out int code)
        {
            if (!TryValidateStructure(Next, diagnostics, out code))
                return false;

            for (int i = 0; i < modules.Plots.Count; i++)
            {
                if (modules.Plots[i].PlotId != Next.PlotId)
                    continue;
                modules.Plots[i] = Next;
                return true;
            }

            diagnostics.Add("plot-not-found:" + Next.PlotId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            code = CloudIsle.Net.ErrorCodes.BadPacket;
            return false;
        }

        /// <summary>
        /// 校验本补丁结构；Growing 与 <see cref="FarmStatePolicy.IsValidGrowingInvariant"/> 对齐
        ///（接受照料后 ready==plant 立即成熟；拒绝无 care 零期限 / 逆序），避免只读 Project 与 Compose 分歧。
        /// </summary>
        internal static bool TryValidateStructure(PlotStateView next, List<string> diagnostics, out int code)
        {
            code = CloudIsle.Net.ErrorCodes.Ok;
            if (next.PlotId <= 0
                || !Enum.IsDefined(typeof(DomainPlotState), next.State)
                || next.CropId < 0
                || next.PlantTime < 0
                || next.ReadyTime < 0)
            {
                diagnostics.Add(DiagMalformed);
                code = CloudIsle.Net.ErrorCodes.BadPacket;
                return false;
            }

            if (next.State == DomainPlotState.Growing
                && !FarmStatePolicy.IsValidGrowingInvariant(next))
            {
                diagnostics.Add(DiagMalformed);
                code = CloudIsle.Net.ErrorCodes.BadPacket;
                return false;
            }

            return true;
        }
    }

    /// <summary>按 toolId 替换或追加工具等级。</summary>
    public sealed class ToolReplacePatch : AggregateModulePatch
    {
        public const string DiagMalformed = "malformed-tool-patch";

        public ToolReplacePatch(ToolStateView next)
        {
            Next = next;
        }

        public ToolStateView Next { get; }

        internal override bool TryApply(WorkingModules modules, List<string> diagnostics, out int code)
        {
            if (!TryValidateStructure(Next, diagnostics, out code))
                return false;

            for (int i = 0; i < modules.Tools.Count; i++)
            {
                if (modules.Tools[i].ToolId != Next.ToolId)
                    continue;
                modules.Tools[i] = Next;
                return true;
            }

            modules.Tools.Add(Next);
            return true;
        }

        internal static bool TryValidateStructure(ToolStateView next, List<string> diagnostics, out int code)
        {
            code = CloudIsle.Net.ErrorCodes.Ok;
            if (next.ToolId <= 0 || next.Level < 0)
            {
                diagnostics.Add(DiagMalformed);
                code = CloudIsle.Net.ErrorCodes.BadPacket;
                return false;
            }
            return true;
        }
    }

    /// <summary>按 nodeId 替换采集点。</summary>
    public sealed class NodeReplacePatch : AggregateModulePatch
    {
        public const string DiagMalformed = "malformed-node-patch";

        public NodeReplacePatch(NodeStateView next)
        {
            Next = next;
        }

        public NodeStateView Next { get; }

        internal override bool TryApply(WorkingModules modules, List<string> diagnostics, out int code)
        {
            if (!TryValidateStructure(Next, diagnostics, out code))
                return false;

            for (int i = 0; i < modules.Nodes.Count; i++)
            {
                if (modules.Nodes[i].NodeId != Next.NodeId)
                    continue;
                modules.Nodes[i] = Next;
                return true;
            }

            diagnostics.Add("node-not-found:" + Next.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            code = CloudIsle.Net.ErrorCodes.BadPacket;
            return false;
        }

        internal static bool TryValidateStructure(NodeStateView next, List<string> diagnostics, out int code)
        {
            code = CloudIsle.Net.ErrorCodes.Ok;
            // ConfigNodeId 须为正配置 ID；时间非负。
            if (next.NodeId <= 0 || next.ConfigNodeId <= 0 || next.NextReadyTime < 0)
            {
                diagnostics.Add(DiagMalformed);
                code = CloudIsle.Net.ErrorCodes.BadPacket;
                return false;
            }
            return true;
        }
    }

    /// <summary>按 facilityId 替换设施。</summary>
    public sealed class FacilityReplacePatch : AggregateModulePatch
    {
        public const string DiagMalformed = "malformed-facility-patch";

        public FacilityReplacePatch(FacilityStateView next)
        {
            Next = next;
        }

        public FacilityStateView Next { get; }

        internal override bool TryApply(WorkingModules modules, List<string> diagnostics, out int code)
        {
            if (!TryValidateStructure(Next, diagnostics, out code))
                return false;

            for (int i = 0; i < modules.Facilities.Count; i++)
            {
                if (modules.Facilities[i].FacilityId != Next.FacilityId)
                    continue;
                modules.Facilities[i] = Next;
                return true;
            }

            diagnostics.Add(
                "facility-not-found:" + Next.FacilityId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            code = CloudIsle.Net.ErrorCodes.BadPacket;
            return false;
        }

        internal static bool TryValidateStructure(FacilityStateView next, List<string> diagnostics, out int code)
        {
            code = CloudIsle.Net.ErrorCodes.Ok;
            // ConfigFacilityId 允许 0（未解析）；等级非负；实例 ID 为正。
            if (next.FacilityId <= 0 || next.ConfigFacilityId < 0 || next.Level < 0)
            {
                diagnostics.Add(DiagMalformed);
                code = CloudIsle.Net.ErrorCodes.BadPacket;
                return false;
            }
            return true;
        }
    }

    /// <summary>后段失败注入（测试/显式拒绝）：有界 typed，非 delegate。</summary>
    public sealed class ForcedFailurePatch : AggregateModulePatch
    {
        public ForcedFailurePatch(int code, string diagnostic)
        {
            Code = code;
            Diagnostic = diagnostic ?? "";
        }

        public int Code { get; }
        public string Diagnostic { get; }

        internal override bool TryApply(WorkingModules modules, List<string> diagnostics, out int code)
        {
            diagnostics.Add(Diagnostic);
            code = Code;
            return false;
        }
    }

    /// <summary>Composer 工作副本；仅合成过程可变，成功后冻结进候选。</summary>
    internal sealed class WorkingModules
    {
        public WorkingModules(PlayerAggregate source)
        {
            Plots = CopyPlots(source.Plots);
            Nodes = CopyNodes(source.Nodes);
            Tools = CopyTools(source.Tools);
            Facilities = CopyFacilities(source.Facilities);
        }

        public List<PlotStateView> Plots { get; }
        public List<NodeStateView> Nodes { get; }
        public List<ToolStateView> Tools { get; }
        public List<FacilityStateView> Facilities { get; }

        static List<PlotStateView> CopyPlots(IReadOnlyList<PlotStateView> source)
        {
            var list = new List<PlotStateView>(source.Count);
            for (int i = 0; i < source.Count; i++)
                list.Add(source[i]);
            return list;
        }

        static List<NodeStateView> CopyNodes(IReadOnlyList<NodeStateView> source)
        {
            var list = new List<NodeStateView>(source.Count);
            for (int i = 0; i < source.Count; i++)
                list.Add(source[i]);
            return list;
        }

        static List<ToolStateView> CopyTools(IReadOnlyList<ToolStateView> source)
        {
            var list = new List<ToolStateView>(source.Count);
            for (int i = 0; i < source.Count; i++)
                list.Add(source[i]);
            return list;
        }

        static List<FacilityStateView> CopyFacilities(IReadOnlyList<FacilityStateView> source)
        {
            var list = new List<FacilityStateView>(source.Count);
            for (int i = 0; i < source.Count; i++)
                list.Add(source[i]);
            return list;
        }
    }

    /// <summary>
    /// 同命令多域效果：消耗与奖励分离；多来源奖励列表；有界模块补丁。
    /// ExpGain 与各 RewardBundle.Exp 由 Composer 各计一次后交 ProgressionPolicy。
    /// </summary>
    public sealed class DomainEffects
    {
        public DomainEffects(
            long goldCost = 0,
            long gemCost = 0,
            long ticketCost = 0,
            long shardCost = 0,
            IReadOnlyList<ItemAmount>? itemCosts = null,
            IReadOnlyList<RewardBundle>? rewardSources = null,
            IReadOnlyList<AggregateModulePatch>? basePatches = null,
            long expGain = 0,
            int targetBagSlots = 0,
            ShopPurchaseCounts? shopPurchasesAfter = null,
            AcceptedTaskState? acceptedTasksAfter = null)
        {
            GoldCost = goldCost;
            GemCost = gemCost;
            TicketCost = ticketCost;
            ShardCost = shardCost;
            ItemCosts = FreezeAmounts(itemCosts);
            RewardSources = FreezeBundles(rewardSources);
            BasePatches = FreezePatches(basePatches);
            ExpGain = expGain;
            // 0 = 无容量效果；正数 = 目标格数（Inventory 所有权，Composer 内一次应用）。
            TargetBagSlots = targetBagSlots;
            // null = 保留源商店计数；非 null = 本笔替换（购买限购 / 日切清空经 DailyPatch）。
            ShopPurchasesAfter = shopPurchasesAfter?.Clone();
            // null = 保留源已接受任务；非 null = 本笔替换（T030 播种起写入）。
            AcceptedTasksAfter = acceptedTasksAfter?.Clone();
        }

        public long GoldCost { get; }
        public long GemCost { get; }
        public long TicketCost { get; }
        public long ShardCost { get; }
        public IReadOnlyList<ItemAmount> ItemCosts { get; }
        public IReadOnlyList<RewardBundle> RewardSources { get; }
        public IReadOnlyList<AggregateModulePatch> BasePatches { get; }
        public long ExpGain { get; }
        /// <summary>有界目标背包格数；0 表示本笔不改容量。负值由 Composer 拒绝。</summary>
        public int TargetBagSlots { get; }
        /// <summary>购买后商店计数；null 表示本笔不改 sidecar。</summary>
        public ShopPurchaseCounts? ShopPurchasesAfter { get; }
        /// <summary>播种等写入的已接受任务；null 表示本笔不改 sidecar。</summary>
        public AcceptedTaskState? AcceptedTasksAfter { get; }

        static IReadOnlyList<ItemAmount> FreezeAmounts(IReadOnlyList<ItemAmount>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<ItemAmount>());
            var arr = new ItemAmount[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i];
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<RewardBundle> FreezeBundles(IReadOnlyList<RewardBundle>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<RewardBundle>());
            var arr = new RewardBundle[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i] ?? RewardBundle.Empty;
            return Array.AsReadOnly(arr);
        }

        static IReadOnlyList<AggregateModulePatch> FreezePatches(IReadOnlyList<AggregateModulePatch>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<AggregateModulePatch>());
            var arr = new AggregateModulePatch[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] == null)
                    throw new ArgumentException("basePatches 含 null", nameof(source));
                arr[i] = source[i];
            }
            return Array.AsReadOnly(arr);
        }
    }
}
