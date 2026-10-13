using UnityEngine;

namespace Game.Framework.Resource
{
    /// <summary>
    /// PoC #3：可热更 Prefab 上的可见标记（版本号 + 颜色）。
    /// </summary>
    public sealed class PatchCubeMarker : MonoBehaviour
    {
        public string Version = "v1";
        public Color Color = Color.red;

        void Awake()
        {
            ApplyVisual();
        }

        public void ApplyVisual()
        {
            name = $"PatchCube_{Version}";

            if (!Application.isPlaying)
                return;

            var renderer = GetComponent<Renderer>();
            if (renderer == null)
                return;

            var mat = renderer.material;
            mat.color = Color;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", Color);
        }
    }
}
