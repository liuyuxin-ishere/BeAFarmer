using System;
using System.Collections.Generic;
using CloudIsle.Net;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// D08 批量规范化：maxItems 默认 24；重复/超长/空/非正 ID 整包拒绝；稳定按 plotId 升序。
    /// 无 DB/网络；不修改调用方列表。
    /// </summary>
    public static class FarmBatchNormalizer
    {
        public const int DefaultMaxItems = 24;
        public const string DiagEmpty = "farm-batch-empty";
        public const string DiagTooMany = "farm-batch-too-many";
        public const string DiagDuplicate = "farm-batch-duplicate-plot";
        public const string DiagNonPositive = "farm-batch-non-positive-id";
        public const string DiagPlantSeedRequired = "farm-batch-plant-seed-required";
        public const string DiagHarvestSeedMustZero = "farm-batch-harvest-seed-must-zero";

        public static FarmBatchNormalizeResult Normalize(BatchIntent intent, int maxItems = DefaultMaxItems)
        {
            if (intent == null)
                throw new ArgumentNullException(nameof(intent));
            if (maxItems < 1)
                throw new ArgumentOutOfRangeException(nameof(maxItems));

            IReadOnlyList<PlotCommand> raw = intent.Items;
            if (raw.Count == 0)
                return FarmBatchNormalizeResult.Fail(ErrorCodes.BadPacket, new[] { DiagEmpty });
            if (raw.Count > maxItems)
                return FarmBatchNormalizeResult.Fail(ErrorCodes.BadPacket, new[] { DiagTooMany });

            var sorted = new PlotCommand[raw.Count];
            for (int i = 0; i < raw.Count; i++)
                sorted[i] = raw[i];
            Array.Sort(sorted, (a, b) => a.PlotId.CompareTo(b.PlotId));

            var seen = new HashSet<int>();
            for (int i = 0; i < sorted.Length; i++)
            {
                PlotCommand cmd = sorted[i];
                if (cmd.PlotId <= 0)
                    return FarmBatchNormalizeResult.Fail(ErrorCodes.BadPacket, new[] { DiagNonPositive });
                if (!seen.Add(cmd.PlotId))
                    return FarmBatchNormalizeResult.Fail(ErrorCodes.BadPacket, new[] { DiagDuplicate });

                if (intent.Kind == FarmBatchKind.Plant)
                {
                    if (cmd.SeedId <= 0)
                        return FarmBatchNormalizeResult.Fail(ErrorCodes.BadPacket, new[] { DiagPlantSeedRequired });
                }
                else if (cmd.SeedId != 0)
                {
                    return FarmBatchNormalizeResult.Fail(ErrorCodes.BadPacket, new[] { DiagHarvestSeedMustZero });
                }
            }

            return FarmBatchNormalizeResult.Ok(
                new NormalizedFarmBatch(intent.Kind, Array.AsReadOnly(sorted)));
        }
    }
}
