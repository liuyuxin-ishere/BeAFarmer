using System;
using System.Collections.Generic;
using CloudIsle.Domain.Init;
using CloudIsle.Domain.Reward;

namespace CloudIsle.Domain.Farm
{
    /// <summary>批量种类：仅 Plant / Harvest（Water/Fertilize 保持单地块）。</summary>
    public enum FarmBatchKind
    {
        Plant = 1,
        Harvest = 2,
    }

    /// <summary>
    /// 批量单项命令：Plant 须 SeedId&gt;0；Harvest 的 SeedId 恒为 0。
    /// </summary>
    public readonly struct PlotCommand
    {
        public PlotCommand(int plotId, int seedId = 0)
        {
            PlotId = plotId;
            SeedId = seedId;
        }

        public int PlotId { get; }
        public int SeedId { get; }
    }

    /// <summary>调用方批量意图；Normalize 前可无序；构造时防御拷贝。</summary>
    public sealed class BatchIntent
    {
        public BatchIntent(FarmBatchKind kind, IReadOnlyList<PlotCommand> items)
        {
            if (kind != FarmBatchKind.Plant && kind != FarmBatchKind.Harvest)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            Kind = kind;
            var arr = new PlotCommand[items.Count];
            for (int i = 0; i < items.Count; i++)
                arr[i] = items[i];
            Items = Array.AsReadOnly(arr);
        }

        public FarmBatchKind Kind { get; }
        public IReadOnlyList<PlotCommand> Items { get; }
    }

    /// <summary>规范化后的稳定批量：plotId 严格升序、无重复、1..maxItems。</summary>
    public sealed class NormalizedFarmBatch
    {
        internal NormalizedFarmBatch(FarmBatchKind kind, IReadOnlyList<PlotCommand> items)
        {
            Kind = kind;
            Items = items;
        }

        public FarmBatchKind Kind { get; }
        public IReadOnlyList<PlotCommand> Items { get; }
    }

    /// <summary>Normalize 结果：成功含稳定批量；失败为整包拒绝码。</summary>
    public sealed class FarmBatchNormalizeResult
    {
        FarmBatchNormalizeResult(bool success, int code, NormalizedFarmBatch? batch, IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            Batch = batch;
            Diagnostics = diagnostics;
        }

        public bool Success { get; }
        public int Code { get; }
        public NormalizedFarmBatch? Batch { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static FarmBatchNormalizeResult Ok(NormalizedFarmBatch batch)
            => new(true, 0, batch, Array.AsReadOnly(Array.Empty<string>()));

        public static FarmBatchNormalizeResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
        {
            IReadOnlyList<string> d;
            if (diagnostics == null || diagnostics.Count == 0)
                d = Array.AsReadOnly(Array.Empty<string>());
            else
            {
                var arr = new string[diagnostics.Count];
                for (int i = 0; i < diagnostics.Count; i++)
                    arr[i] = diagnostics[i] ?? "";
                d = Array.AsReadOnly(arr);
            }

            return new FarmBatchNormalizeResult(false, code, null, d);
        }
    }

    /// <summary>批量单项冻结业务结果（稳定顺序）。</summary>
    public sealed class FarmBatchItemResult
    {
        public FarmBatchItemResult(
            int plotId,
            int code,
            PlotStateView? plotAfter,
            int yieldItemId,
            int yieldCount,
            int expGained,
            int rngCalls)
        {
            if (plotId <= 0)
                throw new ArgumentOutOfRangeException(nameof(plotId));
            if (rngCalls < 0)
                throw new ArgumentOutOfRangeException(nameof(rngCalls));
            if (yieldCount < 0 || expGained < 0)
                throw new ArgumentOutOfRangeException(nameof(yieldCount));
            if (code == 0 && yieldItemId < 0)
                throw new ArgumentOutOfRangeException(nameof(yieldItemId));

            PlotId = plotId;
            Code = code;
            PlotAfter = plotAfter;
            YieldItemId = yieldItemId;
            YieldCount = yieldCount;
            ExpGained = expGained;
            RngCalls = rngCalls;
        }

        public int PlotId { get; }
        public int Code { get; }
        /// <summary>仅成功项非 null（该项冻结后态视图）。</summary>
        public PlotStateView? PlotAfter { get; }
        public int YieldItemId { get; }
        public int YieldCount { get; }
        public int ExpGained { get; }
        public int RngCalls { get; }

        public bool Success => Code == 0;
    }

    /// <summary>
    /// 批量求值结果：顶层已处理（code0）；MutatesAssets 表示是否有成功项。
    /// Candidate 仅在 MutatesAssets 时非 null（相对源的最终候选）。
    /// </summary>
    public sealed class BatchDomainResult
    {
        BatchDomainResult(
            bool mutatesAssets,
            PlayerAggregate? candidate,
            IReadOnlyList<FarmBatchItemResult> items,
            IReadOnlyList<ItemAmount> aggregateGained,
            int aggregateExp,
            int totalRngCalls,
            IReadOnlyList<string> diagnostics)
        {
            MutatesAssets = mutatesAssets;
            Candidate = candidate;
            Items = items;
            AggregateGained = aggregateGained;
            AggregateExp = aggregateExp;
            TotalRngCalls = totalRngCalls;
            Diagnostics = diagnostics;
        }

        /// <summary>语法已接受批量的顶层处理码恒为 0（见 ADR T1）。</summary>
        public int Code => 0;

        public bool MutatesAssets { get; }
        public PlayerAggregate? Candidate { get; }
        public IReadOnlyList<FarmBatchItemResult> Items { get; }
        public IReadOnlyList<ItemAmount> AggregateGained { get; }
        public int AggregateExp { get; }
        public int TotalRngCalls { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static BatchDomainResult Create(
            bool mutatesAssets,
            PlayerAggregate? candidate,
            IReadOnlyList<FarmBatchItemResult> items,
            IReadOnlyList<ItemAmount> aggregateGained,
            int aggregateExp,
            int totalRngCalls,
            IReadOnlyList<string>? diagnostics = null)
        {
            if (items == null || items.Count == 0)
                throw new ArgumentException("items required", nameof(items));
            if (mutatesAssets && candidate == null)
                throw new ArgumentException("mutating batch requires candidate", nameof(candidate));
            if (!mutatesAssets && candidate != null)
                throw new ArgumentException("no-change batch must not expose candidate", nameof(candidate));
            if (aggregateGained == null)
                throw new ArgumentNullException(nameof(aggregateGained));
            if (aggregateExp < 0)
                throw new ArgumentOutOfRangeException(nameof(aggregateExp));
            if (totalRngCalls < 0)
                throw new ArgumentOutOfRangeException(nameof(totalRngCalls));

            var itemArr = new FarmBatchItemResult[items.Count];
            for (int i = 0; i < items.Count; i++)
                itemArr[i] = items[i] ?? throw new ArgumentException("null item", nameof(items));

            var gainArr = new ItemAmount[aggregateGained.Count];
            for (int i = 0; i < aggregateGained.Count; i++)
                gainArr[i] = aggregateGained[i];

            IReadOnlyList<string> d;
            if (diagnostics == null || diagnostics.Count == 0)
                d = Array.AsReadOnly(Array.Empty<string>());
            else
            {
                var da = new string[diagnostics.Count];
                for (int i = 0; i < diagnostics.Count; i++)
                    da[i] = diagnostics[i] ?? "";
                d = Array.AsReadOnly(da);
            }

            return new BatchDomainResult(
                mutatesAssets,
                candidate,
                Array.AsReadOnly(itemArr),
                Array.AsReadOnly(gainArr),
                aggregateExp,
                totalRngCalls,
                d);
        }
    }
}
