using UnityEngine;

namespace Game.Demo.AvatarMerge
{
    /// <summary>生成 Demo 默认部位表：每部位 2 套，ID 与操作说明示例对齐。</summary>
    public static class AvatarCatalogFactory
    {
        public static AvatarPartCatalog CreateDefault()
        {
            var parts = new[]
            {
                Make(7101, AvatarPartType.Hair, new Color(0.95f, 0.75f, 0.35f)),
                Make(7102, AvatarPartType.Hair, new Color(0.35f, 0.2f, 0.1f)),
                Make(7201, AvatarPartType.Top, new Color(0.25f, 0.55f, 0.95f)),
                Make(7202, AvatarPartType.Top, new Color(0.9f, 0.3f, 0.35f)),
                Make(7301, AvatarPartType.Bottom, new Color(0.2f, 0.35f, 0.55f)),
                Make(7302, AvatarPartType.Bottom, new Color(0.45f, 0.7f, 0.4f)),
                Make(7401, AvatarPartType.Shoes, new Color(0.15f, 0.15f, 0.18f)),
                Make(7402, AvatarPartType.Shoes, new Color(0.7f, 0.55f, 0.3f)),
                Make(7501, AvatarPartType.Hat, new Color(0.85f, 0.2f, 0.2f)),
                Make(7502, AvatarPartType.Hat, new Color(0.2f, 0.7f, 0.75f)),
                Make(7601, AvatarPartType.Acc, new Color(0.95f, 0.85f, 0.2f)),
                Make(7602, AvatarPartType.Acc, new Color(0.7f, 0.4f, 0.95f)),
            };
            return new AvatarPartCatalog { Parts = parts };
        }

        static AvatarPartAsset Make(int id, AvatarPartType part, Color color)
        {
            string folder = part.ToString();
            string prefix = part.ToString().ToLowerInvariant();
            return new AvatarPartAsset
            {
                Id = id,
                Part = part,
                MeshPath = $"Avatar/Parts/{folder}/{prefix}_{id}",
                TexturePath = $"Avatar/Parts/{folder}/{prefix}_{id}_d",
                PlaceholderColor = color,
                BoneNames = AvatarPlaceholderFactory.GetBoneNamesForPart(part)
            };
        }
    }
}
