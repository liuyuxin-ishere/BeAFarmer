using System;

namespace CloudIsle.Config
{
    /// <summary>
    /// 作物只读投影（P02）：AcceptedHarvestSpec.Capture 输入；不暴露 Luban 可变行引用。
    /// </summary>
    public sealed class CropSpec
    {
        public CropSpec(
            int cropId,
            int seedId,
            int yieldItemId,
            int unlockLevel,
            int growSec,
            int yieldMin,
            int yieldMax,
            int exp)
        {
            if (cropId <= 0)
                throw new ArgumentOutOfRangeException(nameof(cropId));
            if (seedId <= 0)
                throw new ArgumentOutOfRangeException(nameof(seedId));
            if (yieldItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldItemId));
            if (unlockLevel <= 0)
                throw new ArgumentOutOfRangeException(nameof(unlockLevel));
            if (growSec <= 0)
                throw new ArgumentOutOfRangeException(nameof(growSec));
            if (yieldMin <= 0)
                throw new ArgumentOutOfRangeException(nameof(yieldMin));
            if (yieldMax < yieldMin)
                throw new ArgumentOutOfRangeException(nameof(yieldMax));
            if (exp < 0)
                throw new ArgumentOutOfRangeException(nameof(exp));

            CropId = cropId;
            SeedId = seedId;
            YieldItemId = yieldItemId;
            UnlockLevel = unlockLevel;
            GrowSec = growSec;
            YieldMin = yieldMin;
            YieldMax = yieldMax;
            Exp = exp;
        }

        public int CropId { get; }
        public int SeedId { get; }
        public int YieldItemId { get; }
        public int UnlockLevel { get; }
        public int GrowSec { get; }
        public int YieldMin { get; }
        public int YieldMax { get; }
        public int Exp { get; }
    }
}
