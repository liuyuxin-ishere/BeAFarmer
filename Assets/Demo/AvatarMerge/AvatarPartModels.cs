using System;
using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    /// <summary>单件外观在 Demo 中的资源定位（可与 AvatarItem.id 对齐）。</summary>
    [Serializable]
    public class AvatarPartAsset
    {
        public int Id;
        public AvatarPartType Part;
        public string MeshPath;
        public string TexturePath;
        public string[] BoneNames;
        public Color PlaceholderColor = Color.white;
        public Vector3 LocalOffset;
        public Vector3 LocalScale = Vector3.one;
    }

    /// <summary>Demo 用部位表。</summary>
    [Serializable]
    public class AvatarPartCatalog
    {
        public AvatarPartAsset[] Parts = Array.Empty<AvatarPartAsset>();

        public AvatarPartAsset Get(int id)
        {
            if (Parts == null) return null;
            for (int i = 0; i < Parts.Length; i++)
            {
                if (Parts[i] != null && Parts[i].Id == id)
                    return Parts[i];
            }
            return null;
        }

        public AvatarPartAsset Get(AvatarPartType part, int id)
        {
            var asset = Get(id);
            if (asset == null) return null;
            return asset.Part == part ? asset : null;
        }
    }

    public sealed class AvatarPartRuntime
    {
        public Mesh Mesh;
        public Texture2D Albedo;
        public Transform[] Bones;
        public string[] BoneNames;
    }

    public sealed class AvatarMergeResult : IDisposable
    {
        public long OutfitHash;
        public Mesh CombinedMesh;
        public Texture2D Atlas;
        public Material Material;
        public bool OwnedByCache;

        public void Dispose()
        {
            if (OwnedByCache) return;
            DestroyOwned();
        }

        internal void DestroyOwned()
        {
            if (CombinedMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(CombinedMesh);
                CombinedMesh = null;
            }
            if (Atlas != null)
            {
                UnityEngine.Object.DestroyImmediate(Atlas);
                Atlas = null;
            }
            if (Material != null)
            {
                UnityEngine.Object.DestroyImmediate(Material);
                Material = null;
            }
        }
    }
}
