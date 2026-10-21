using System.Collections.Generic;
using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    public sealed class AvatarAtlasBakeResult
    {
        public Texture2D Atlas;
        public Rect[] Uvs;
        public int Size;
    }

    /// <summary>将多张部位贴图打入一张图集，并返回每张对应 UV 矩形。</summary>
    public static class AvatarAtlasBaker
    {
        public static AvatarAtlasBakeResult Bake(IReadOnlyList<Texture2D> textures, int preferredSize = 512)
        {
            if (textures == null || textures.Count == 0)
                throw new System.ArgumentException("textures empty");

            int size = preferredSize;
            Rect[] rects = null;
            Texture2D atlas = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (atlas != null)
                    Object.DestroyImmediate(atlas);

                atlas = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    name = $"AvatarAtlas_{size}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                var readable = new Texture2D[textures.Count];
                for (int i = 0; i < textures.Count; i++)
                    readable[i] = EnsureReadable(textures[i]);

                rects = atlas.PackTextures(readable, 2, size, false);
                for (int i = 0; i < readable.Length; i++)
                {
                    if (readable[i] != textures[i])
                        Object.DestroyImmediate(readable[i]);
                }

                bool ok = true;
                for (int i = 0; i < rects.Length; i++)
                {
                    if (rects[i].width <= 0f || rects[i].height <= 0f)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    if (size != preferredSize)
                        Debug.Log($"[AvatarMerge] Atlas upgraded {preferredSize} → {size}");
                    return new AvatarAtlasBakeResult { Atlas = atlas, Uvs = rects, Size = size };
                }

                size *= 2;
            }

            throw new System.InvalidOperationException("Failed to pack avatar atlas even at 2048.");
        }

        public static void RemapUVs(Mesh mesh, Rect uvRect)
        {
            var uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0)
            {
                uvs = new Vector2[mesh.vertexCount];
                for (int i = 0; i < uvs.Length; i++)
                    uvs[i] = new Vector2(0.5f, 0.5f);
            }

            for (int i = 0; i < uvs.Length; i++)
            {
                uvs[i] = new Vector2(
                    uvRect.x + uvs[i].x * uvRect.width,
                    uvRect.y + uvs[i].y * uvRect.height);
            }
            mesh.uv = uvs;
        }

        static Texture2D EnsureReadable(Texture2D src)
        {
            if (src == null)
            {
                var fallback = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var pixels = new Color[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.magenta;
                fallback.SetPixels(pixels);
                fallback.Apply(false, false);
                return fallback;
            }

            try
            {
                src.GetPixel(0, 0);
                return src;
            }
            catch
            {
                var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                copy.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return copy;
            }
        }
    }
}
