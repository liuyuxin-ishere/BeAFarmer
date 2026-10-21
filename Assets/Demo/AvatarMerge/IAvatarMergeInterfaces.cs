using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    public interface IAvatarPartLoader
    {
        Task<AvatarPartRuntime> LoadAsync(AvatarPartAsset asset);
    }

    public interface IAvatarMergeCache
    {
        bool TryGet(long outfitHash, out AvatarMergeResult result);
        void Put(long outfitHash, AvatarMergeResult result);
        void Clear();
        int Count { get; }
        int Capacity { get; }
        int HitCount { get; }
        int MissCount { get; }
    }

    /// <summary>合并主入口。</summary>
    public interface IAvatarMergeService
    {
        Task<AvatarMergeResult> BuildAsync(
            AvatarOutfit outfit,
            Transform rigRoot,
            CancellationToken ct = default);

        void Apply(SkinnedMeshRenderer target, AvatarMergeResult result, Transform rigRoot);
    }
}
