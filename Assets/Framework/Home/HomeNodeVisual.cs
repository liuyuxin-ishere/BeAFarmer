using UnityEngine;

namespace Game.Framework.Home
{
    /// <summary>灰盒采集点显示态（只读着色；冷却到 0 不自动采集/发奖）。</summary>
    public enum HomeNodeDisplayKind
    {
        Locked = 0,
        Ready = 1,
        Cooling = 2,
        Selected = 3,
        Busy = 4,
        Disabled = 5,
        Unknown = 6,
    }

    /// <summary>
    /// 单采集点可视；状态由 Presenter/装配推送。材质复用 M2Farm Resources 模板 clone。
    /// </summary>
    public sealed class HomeNodeVisual : MonoBehaviour
    {
        static readonly Color Locked = new Color(0.28f, 0.28f, 0.30f, 1f);
        static readonly Color Ready = new Color(0.20f, 0.62f, 0.38f, 1f);
        static readonly Color Cooling = new Color(0.35f, 0.45f, 0.55f, 1f);
        static readonly Color Selected = new Color(0.25f, 0.55f, 0.95f, 1f);
        static readonly Color Busy = new Color(0.55f, 0.55f, 0.55f, 1f);
        static readonly Color Disabled = new Color(0.40f, 0.22f, 0.22f, 1f);
        static readonly Color Unknown = new Color(0.55f, 0.20f, 0.55f, 1f);

        static readonly Color[] TypeTint =
        {
            new Color(0.35f, 0.55f, 0.28f, 1f), // unused
            new Color(0.32f, 0.52f, 0.26f, 1f), // tree
            new Color(0.55f, 0.28f, 0.45f, 1f), // berry
            new Color(0.45f, 0.45f, 0.48f, 1f), // rock
            new Color(0.72f, 0.45f, 0.55f, 1f), // flower
        };

        static Material _template;
        static bool _templateLoadAttempted;
        static bool _missingLogged;

        int _instanceId;
        int _typeId;
        HomeNodeDisplayKind _kind = HomeNodeDisplayKind.Ready;
        MeshRenderer _renderer;
        Material _ownedMaterial;

        public int InstanceId => _instanceId;
        public int TypeId => _typeId;
        public HomeNodeDisplayKind DisplayKind => _kind;

        public void Bind(int instanceId, int typeId)
        {
            _instanceId = instanceId;
            _typeId = typeId;
        }

        public void AttachRenderer(MeshRenderer renderer)
        {
            _renderer = renderer;
            EnsureOwnedMaterial();
            ApplyColor();
        }

        public void SetDisplay(HomeNodeDisplayKind kind)
        {
            _kind = kind;
            ApplyColor();
        }

        void OnDestroy()
        {
            ReleaseOwnedMaterial();
        }

        static Material TryLoadTemplate()
        {
            if (_template != null)
                return _template;
            if (_templateLoadAttempted)
                return null;

            _templateLoadAttempted = true;
            _template = Resources.Load<Material>(HomePlotVisual.MaterialResourcePath);
            if (_template == null && !_missingLogged)
            {
                _missingLogged = true;
                Debug.LogWarning("[HomeNodeVisual] missing Resources/" + HomePlotVisual.MaterialResourcePath);
            }

            return _template;
        }

        void EnsureOwnedMaterial()
        {
            if (_ownedMaterial != null || _renderer == null)
                return;
            Material template = TryLoadTemplate();
            if (template == null)
                return;
            _ownedMaterial = new Material(template);
            _renderer.sharedMaterial = _ownedMaterial;
        }

        void ReleaseOwnedMaterial()
        {
            if (_ownedMaterial == null)
                return;
            if (_renderer != null && ReferenceEquals(_renderer.sharedMaterial, _ownedMaterial))
                _renderer.sharedMaterial = null;
            Destroy(_ownedMaterial);
            _ownedMaterial = null;
        }

        void ApplyColor()
        {
            if (_ownedMaterial == null)
                EnsureOwnedMaterial();
            if (_ownedMaterial == null)
                return;

            Color c = ColorFor(_kind);
            if (_ownedMaterial.HasProperty("_BaseColor"))
                _ownedMaterial.SetColor("_BaseColor", c);
            if (_ownedMaterial.HasProperty("_Color"))
                _ownedMaterial.SetColor("_Color", c);
        }

        Color ColorFor(HomeNodeDisplayKind kind)
        {
            switch (kind)
            {
                case HomeNodeDisplayKind.Locked: return Locked;
                case HomeNodeDisplayKind.Ready:
                    return _typeId >= 1 && _typeId < TypeTint.Length ? TypeTint[_typeId] : Ready;
                case HomeNodeDisplayKind.Cooling: return Cooling;
                case HomeNodeDisplayKind.Selected: return Selected;
                case HomeNodeDisplayKind.Busy: return Busy;
                case HomeNodeDisplayKind.Disabled: return Disabled;
                default: return Unknown;
            }
        }
    }
}
