using System.Threading.Tasks;
using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    /// <summary>Demo 加载器：优先 Resources / 磁盘纹理，否则按部位生成色块占位 Mesh+贴图。</summary>
    public sealed class AvatarPartLoader : IAvatarPartLoader
    {
        public Task<AvatarPartRuntime> LoadAsync(AvatarPartAsset asset)
        {
            if (asset == null)
                throw new System.ArgumentNullException(nameof(asset));

            var runtime = new AvatarPartRuntime();

            if (!string.IsNullOrEmpty(asset.MeshPath))
            {
                var mesh = Resources.Load<Mesh>(asset.MeshPath);
                if (mesh != null)
                    runtime.Mesh = Object.Instantiate(mesh);
            }

            if (!string.IsNullOrEmpty(asset.TexturePath))
            {
                runtime.Albedo = Resources.Load<Texture2D>(asset.TexturePath);
            }

            if (runtime.Mesh == null || runtime.Albedo == null)
            {
                var generated = AvatarPlaceholderFactory.CreatePart(asset);
                if (runtime.Mesh == null) runtime.Mesh = generated.Mesh;
                else Object.DestroyImmediate(generated.Mesh);

                if (runtime.Albedo == null) runtime.Albedo = generated.Albedo;
                else if (generated.Albedo != null) Object.DestroyImmediate(generated.Albedo);

                runtime.BoneNames = generated.BoneNames;
            }
            else
            {
                runtime.BoneNames = asset.BoneNames;
            }

            return Task.FromResult(runtime);
        }
    }
}
