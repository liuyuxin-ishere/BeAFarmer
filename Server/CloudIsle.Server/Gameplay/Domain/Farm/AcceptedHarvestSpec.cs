using System;
using CloudIsle.Config;

namespace CloudIsle.Domain.Farm
{
    /// <summary>
    /// 已接受收获规格（不可变值）：播种时 Capture，持久后不得再查可变 Crop 表。
    /// 含结算所需产物 ID/数量区间/经验/公式身份与配置版本；不持有配置表引用。
    /// </summary>
    public sealed class AcceptedHarvestSpec : IEquatable<AcceptedHarvestSpec>
    {
        /// <summary>产量区间公式：结算时在 [YieldMin,YieldMax] 内取整；不依赖历史 Crop 行。</summary>
        public const string FormulaCropYieldRangeV1 = "crop-yield-range-v1";

        public AcceptedHarvestSpec(
            int cropId,
            int seedId,
            int yieldItemId,
            int yieldMin,
            int yieldMax,
            int exp,
            int growSec,
            int unlockLevel,
            int configVersion,
            string formulaId)
        {
            if (cropId <= 0)
                throw new ArgumentOutOfRangeException(nameof(cropId));
            if (seedId <= 0)
                throw new ArgumentOutOfRangeException(nameof(seedId));
            if (yieldItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldItemId));
            if (yieldMin <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldMin));
            if (yieldMax < yieldMin)
                throw new ArgumentOutOfRangeException(nameof(yieldMax));
            if (exp < 0)
                throw new ArgumentOutOfRangeException(nameof(exp));
            if (growSec <= 0)
                throw new ArgumentOutOfRangeException(nameof(growSec));
            if (unlockLevel <= 0)
                throw new ArgumentOutOfRangeException(nameof(unlockLevel));
            if (configVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(configVersion));
            if (string.IsNullOrEmpty(formulaId))
                throw new ArgumentException("formulaId required", nameof(formulaId));

            CropId = cropId;
            SeedId = seedId;
            YieldItemId = yieldItemId;
            YieldMin = yieldMin;
            YieldMax = yieldMax;
            Exp = exp;
            GrowSec = growSec;
            UnlockLevel = unlockLevel;
            ConfigVersion = configVersion;
            FormulaId = formulaId;
        }

        public int CropId { get; }
        public int SeedId { get; }
        public int YieldItemId { get; }
        public int YieldMin { get; }
        public int YieldMax { get; }
        public int Exp { get; }
        public int GrowSec { get; }
        public int UnlockLevel { get; }
        public int ConfigVersion { get; }
        public string FormulaId { get; }

        /// <summary>
        /// 从作物投影深拷贝结算所需值；之后改/删 Crop 配置不得改变本规格。
        /// </summary>
        public static AcceptedHarvestSpec Capture(CropSpec crop, int configVersion)
        {
            if (crop == null)
                throw new ArgumentNullException(nameof(crop));
            return new AcceptedHarvestSpec(
                crop.CropId,
                crop.SeedId,
                crop.YieldItemId,
                crop.YieldMin,
                crop.YieldMax,
                crop.Exp,
                crop.GrowSec,
                crop.UnlockLevel,
                configVersion,
                FormulaCropYieldRangeV1);
        }

        /// <summary>
        /// 仅用已存规格结算预览：不查当前配置；rolled 须落在 [YieldMin,YieldMax]。
        /// </summary>
        public bool TryResolveSettlement(int rolledInclusive, out int itemId, out int count, out int exp)
        {
            itemId = 0;
            count = 0;
            exp = 0;
            if (rolledInclusive < YieldMin || rolledInclusive > YieldMax)
                return false;
            if (!string.Equals(FormulaId, FormulaCropYieldRangeV1, StringComparison.Ordinal))
                return false;
            itemId = YieldItemId;
            count = rolledInclusive;
            exp = Exp;
            return true;
        }

        public AcceptedHarvestSpec Clone()
        {
            return new AcceptedHarvestSpec(
                CropId, SeedId, YieldItemId, YieldMin, YieldMax, Exp,
                GrowSec, UnlockLevel, ConfigVersion, FormulaId);
        }

        public bool Equals(AcceptedHarvestSpec? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return CropId == other.CropId
                   && SeedId == other.SeedId
                   && YieldItemId == other.YieldItemId
                   && YieldMin == other.YieldMin
                   && YieldMax == other.YieldMax
                   && Exp == other.Exp
                   && GrowSec == other.GrowSec
                   && UnlockLevel == other.UnlockLevel
                   && ConfigVersion == other.ConfigVersion
                   && string.Equals(FormulaId, other.FormulaId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as AcceptedHarvestSpec);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = CropId;
                h = (h * 397) ^ SeedId;
                h = (h * 397) ^ YieldItemId;
                h = (h * 397) ^ YieldMin;
                h = (h * 397) ^ YieldMax;
                h = (h * 397) ^ Exp;
                h = (h * 397) ^ GrowSec;
                h = (h * 397) ^ UnlockLevel;
                h = (h * 397) ^ ConfigVersion;
                h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(FormulaId);
                return h;
            }
        }
    }
}
