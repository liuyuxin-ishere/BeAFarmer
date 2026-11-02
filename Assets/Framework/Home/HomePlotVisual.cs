using UnityEngine;

namespace Game.Framework.Home
{
    /// <summary>灰盒地块显示态（只读着色；倒计时到 0 不自动收获）。</summary>
    public enum HomePlotDisplayKind
    {
        Locked = 0,
        Empty = 1,
        Growing = 2,
        Ripe = 3,
        Selected = 4,
        Busy = 5,
        Error = 6,
        Unknown = 7,
    }

    /// <summary>
    /// 单地块可视组件；状态由 Presenter/装配推送。
    /// 自有材质实例来自 Resources/M2Farm/M2FarmPlot 模板 clone；禁止复制 Primitive 默认 Standard，禁止改共享材质。
    /// </summary>
    public sealed class HomePlotVisual : MonoBehaviour
    {
        public const string MaterialResourcePath = "M2Farm/M2FarmPlot";

        static readonly Color Locked = new Color(0.25f, 0.25f, 0.28f, 1f);
        static readonly Color Empty = new Color(0.45f, 0.36f, 0.22f, 1f);
        static readonly Color Growing = new Color(0.22f, 0.55f, 0.28f, 1f);
        static readonly Color Ripe = new Color(0.85f, 0.72f, 0.18f, 1f);
        static readonly Color Selected = new Color(0.25f, 0.55f, 0.95f, 1f);
        static readonly Color Busy = new Color(0.55f, 0.55f, 0.55f, 1f);
        static readonly Color Error = new Color(0.75f, 0.20f, 0.20f, 1f);
        static readonly Color Unknown = new Color(0.55f, 0.20f, 0.55f, 1f);

        static Material _template;
        static bool _templateLoadAttempted;
        static bool _missingLogged;

        int _instanceId;
        HomePlotDisplayKind _kind = HomePlotDisplayKind.Locked;
        MeshRenderer _renderer;
        Material _ownedMaterial;

        public int InstanceId => _instanceId;
        public HomePlotDisplayKind DisplayKind => _kind;

        public void Bind(int instanceId)
        {
            _instanceId = instanceId;
        }

        public void AttachRenderer(MeshRenderer renderer)
        {
            _renderer = renderer;
            EnsureOwnedMaterial();
            ApplyColor();
        }

        public void SetDisplay(HomePlotDisplayKind kind)
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
            _template = Resources.Load<Material>(MaterialResourcePath);
            if (_template == null && !_missingLogged)
            {
                _missingLogged = true;
                Debug.LogError(
                    "[HomePlotVisual] missing Resources/" + MaterialResourcePath +
                    " — run menu Farming/M2/T035 Bake Farm Prefabs And Home Greybox (EnsureM2FarmPlotResources). " +
                    "Do not clone Primitive Standard; Player URP would render magenta.");
            }

            return _template;
        }

        void EnsureOwnedMaterial()
        {
            if (_renderer == null)
                _renderer = GetComponent<MeshRenderer>();
            if (_renderer == null)
                return;
            if (_ownedMaterial != null)
            {
                if (_renderer.sharedMaterial != _ownedMaterial)
                    _renderer.sharedMaterial = _ownedMaterial;
                return;
            }

            Material template = TryLoadTemplate();
            if (template == null)
                return;

            _ownedMaterial = new Material(template)
            {
                name = template.name + " (HomePlotOwned)"
            };
            _renderer.sharedMaterial = _ownedMaterial;
        }

        void ReleaseOwnedMaterial()
        {
            if (_ownedMaterial == null)
                return;

            if (_renderer != null && _renderer.sharedMaterial == _ownedMaterial)
                _renderer.sharedMaterial = null;

            if (Application.isPlaying)
                Destroy(_ownedMaterial);
            else
                DestroyImmediate(_ownedMaterial);

            _ownedMaterial = null;
        }

        void ApplyColor()
        {
            EnsureOwnedMaterial();
            if (_ownedMaterial == null)
                return;

            Color c;
            switch (_kind)
            {
                case HomePlotDisplayKind.Empty: c = Empty; break;
                case HomePlotDisplayKind.Growing: c = Growing; break;
                case HomePlotDisplayKind.Ripe: c = Ripe; break;
                case HomePlotDisplayKind.Selected: c = Selected; break;
                case HomePlotDisplayKind.Busy: c = Busy; break;
                case HomePlotDisplayKind.Error: c = Error; break;
                case HomePlotDisplayKind.Unknown: c = Unknown; break;
                default: c = Locked; break;
            }

            // 写自有实例；不用 Renderer.material（EditMode 会再实例化并打错误）
            if (_ownedMaterial.HasProperty("_BaseColor"))
                _ownedMaterial.SetColor("_BaseColor", c);
            if (_ownedMaterial.HasProperty("_Color"))
                _ownedMaterial.SetColor("_Color", c);
        }
    }
}
