using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    /// <summary>程序化生成 6 部位色块蒙皮网格与贴图（无需外部 FBX）。</summary>
    public static class AvatarPlaceholderFactory
    {
        public static readonly string[] RigBoneNames =
        {
            "Root", "Hips", "Spine", "Chest", "Neck", "Head",
            "L_UpperArm", "L_LowerArm", "L_Hand",
            "R_UpperArm", "R_LowerArm", "R_Hand",
            "L_UpperLeg", "L_LowerLeg", "L_Foot",
            "R_UpperLeg", "R_LowerLeg", "R_Foot"
        };

        public static AvatarPartRuntime CreatePart(AvatarPartAsset asset)
        {
            var mesh = BuildPartMesh(asset);
            var tex = BuildPartTexture(asset);
            return new AvatarPartRuntime
            {
                Mesh = mesh,
                Albedo = tex,
                BoneNames = GetBoneNamesForPart(asset.Part)
            };
        }

        public static Texture2D BuildPartTexture(AvatarPartAsset asset)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"tex_{asset.Part}_{asset.Id}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var baseColor = asset.PlaceholderColor;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool border = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
                bool stripe = ((x / 8) + (y / 8)) % 2 == 0;
                var c = border ? Color.Lerp(baseColor, Color.black, 0.35f)
                    : stripe ? Color.Lerp(baseColor, Color.white, 0.15f) : baseColor;
                // Encode variant id as slight tint so switch is visible
                float variant = (asset.Id % 10) * 0.03f;
                c = Color.Lerp(c, Color.yellow, variant);
                pixels[y * size + x] = c;
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        public static Mesh BuildPartMesh(AvatarPartAsset asset)
        {
            GetPartShape(asset.Part, out var center, out var size, out var boneIndices);
            center += asset.LocalOffset;
            size = Vector3.Scale(size, asset.LocalScale);

            var mesh = CreateSkinnedBox(center, size, boneIndices);
            mesh.name = $"mesh_{asset.Part}_{asset.Id}";
            return mesh;
        }

        public static string[] GetBoneNamesForPart(AvatarPartType part)
        {
            switch (part)
            {
                case AvatarPartType.Hair:
                case AvatarPartType.Hat:
                    return new[] { "Head" };
                case AvatarPartType.Top:
                    return new[] { "Spine", "Chest", "L_UpperArm", "R_UpperArm" };
                case AvatarPartType.Bottom:
                    return new[] { "Hips", "L_UpperLeg", "R_UpperLeg" };
                case AvatarPartType.Shoes:
                    return new[] { "L_Foot", "R_Foot" };
                case AvatarPartType.Acc:
                    return new[] { "Chest" };
                default:
                    return new[] { "Hips" };
            }
        }

        static void GetPartShape(AvatarPartType part, out Vector3 center, out Vector3 size, out int[] boneIndices)
        {
            // Bone index map matches RigBoneNames order.
            switch (part)
            {
                case AvatarPartType.Hair:
                    center = new Vector3(0f, 1.72f, 0f);
                    size = new Vector3(0.28f, 0.22f, 0.28f);
                    boneIndices = new[] { 5 };
                    break;
                case AvatarPartType.Hat:
                    center = new Vector3(0f, 1.86f, 0f);
                    size = new Vector3(0.32f, 0.12f, 0.32f);
                    boneIndices = new[] { 5 };
                    break;
                case AvatarPartType.Top:
                    center = new Vector3(0f, 1.25f, 0f);
                    size = new Vector3(0.45f, 0.45f, 0.28f);
                    boneIndices = new[] { 2, 3, 6, 9 };
                    break;
                case AvatarPartType.Bottom:
                    center = new Vector3(0f, 0.75f, 0f);
                    size = new Vector3(0.38f, 0.4f, 0.24f);
                    boneIndices = new[] { 1, 12, 15 };
                    break;
                case AvatarPartType.Shoes:
                    center = new Vector3(0f, 0.08f, 0.05f);
                    size = new Vector3(0.42f, 0.1f, 0.22f);
                    boneIndices = new[] { 14, 17 };
                    break;
                case AvatarPartType.Acc:
                    center = new Vector3(0.22f, 1.2f, 0f);
                    size = new Vector3(0.1f, 0.18f, 0.08f);
                    boneIndices = new[] { 3 };
                    break;
                default:
                    center = Vector3.zero;
                    size = Vector3.one * 0.2f;
                    boneIndices = new[] { 1 };
                    break;
            }
        }

        static Mesh CreateSkinnedBox(Vector3 center, Vector3 size, int[] boneIndices)
        {
            var mesh = new Mesh { name = "PartBox" };
            var hx = size.x * 0.5f;
            var hy = size.y * 0.5f;
            var hz = size.z * 0.5f;

            var verts = new Vector3[8];
            int vi = 0;
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            for (int x = -1; x <= 1; x += 2)
                verts[vi++] = center + new Vector3(x * hx, y * hy, z * hz);

            var tris = new[]
            {
                0, 2, 1, 1, 2, 3,
                4, 5, 6, 5, 7, 6,
                0, 1, 4, 1, 5, 4,
                2, 6, 3, 3, 6, 7,
                0, 4, 2, 2, 4, 6,
                1, 3, 5, 3, 7, 5
            };

            var uvs = new Vector2[8];
            for (int i = 0; i < 8; i++)
                uvs[i] = new Vector2(((verts[i].x - center.x) / size.x) + 0.5f,
                    ((verts[i].y - center.y) / size.y) + 0.5f);

            var weights = new BoneWeight[8];
            int primary = boneIndices[0];
            int secondary = boneIndices.Length > 1 ? boneIndices[1] : boneIndices[0];
            for (int i = 0; i < 8; i++)
            {
                weights[i].boneIndex0 = primary;
                weights[i].weight0 = 0.7f;
                weights[i].boneIndex1 = secondary;
                weights[i].weight1 = 0.3f;
            }

            var bindposes = new Matrix4x4[RigBoneNames.Length];
            for (int i = 0; i < bindposes.Length; i++)
                bindposes[i] = Matrix4x4.identity;

            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.uv = uvs;
            mesh.boneWeights = weights;
            mesh.bindposes = bindposes;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
