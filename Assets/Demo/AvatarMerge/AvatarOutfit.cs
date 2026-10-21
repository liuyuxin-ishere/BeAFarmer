namespace Game.Demo.AvatarMerge
{
    /// <summary>玩家当前 1 套外观（玩法 §10.1-4）。</summary>
    [System.Serializable]
    public struct AvatarOutfit
    {
        public int HairId;
        public int TopId;
        public int BottomId;
        public int ShoesId;
        public int HatId;
        public int AccId;

        public static AvatarOutfit Default => new AvatarOutfit
        {
            HairId = 7101,
            TopId = 7201,
            BottomId = 7301,
            ShoesId = 7401,
            HatId = 7501,
            AccId = 7601
        };

        /// <summary>稳定哈希，用作缓存键（FNV-1a 64）。</summary>
        public long GetOutfitHash()
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                hash = Mix(hash, (uint)HairId);
                hash = Mix(hash, (uint)TopId);
                hash = Mix(hash, (uint)BottomId);
                hash = Mix(hash, (uint)ShoesId);
                hash = Mix(hash, (uint)HatId);
                hash = Mix(hash, (uint)AccId);
                return (long)hash;
            }
        }

        static ulong Mix(ulong hash, uint value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 1099511628211UL;
                return hash;
            }
        }

        public int GetPartId(AvatarPartType part)
        {
            switch (part)
            {
                case AvatarPartType.Hair: return HairId;
                case AvatarPartType.Top: return TopId;
                case AvatarPartType.Bottom: return BottomId;
                case AvatarPartType.Shoes: return ShoesId;
                case AvatarPartType.Hat: return HatId;
                case AvatarPartType.Acc: return AccId;
                default: return 0;
            }
        }

        public AvatarOutfit WithPart(AvatarPartType part, int id)
        {
            var o = this;
            switch (part)
            {
                case AvatarPartType.Hair: o.HairId = id; break;
                case AvatarPartType.Top: o.TopId = id; break;
                case AvatarPartType.Bottom: o.BottomId = id; break;
                case AvatarPartType.Shoes: o.ShoesId = id; break;
                case AvatarPartType.Hat: o.HatId = id; break;
                case AvatarPartType.Acc: o.AccId = id; break;
            }
            return o;
        }

        public override string ToString()
        {
            return $"H{HairId}/T{TopId}/B{BottomId}/S{ShoesId}/Ht{HatId}/A{AccId}";
        }
    }
}
