using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Game.Framework.UI
{
    /// <summary>
    /// 常驻 UI 框架根：Canvas + CanvasScaler + 六层 + SafeArea + EventSystem。
    /// 不销毁；场景切换保留。
    /// </summary>
    public sealed class UiRoot : MonoBehaviour
    {
        public const string RootName = "M1_UiRoot";

        RectTransform[] _layers;
        SafeAreaAdapter _safeArea;
        CanvasScaler _scaler;
        GameObject _modalBlocker;
        int _modalDepth;

        public CanvasScaler Scaler => _scaler;
        public SafeAreaAdapter SafeArea => _safeArea;
        public int ModalDepth => _modalDepth;
        public GameObject ModalBlocker => _modalBlocker;

        /// <summary>获取或创建常驻根。</summary>
        public static UiRoot Ensure()
        {
            var existing = FindObjectOfType<UiRoot>();
            if (existing != null)
                return existing;

            var go = new GameObject(RootName);
            DontDestroyOnLoad(go);
            return go.AddComponent<UiRoot>();
        }

        void Awake()
        {
            Build();
        }

        void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            _scaler = gameObject.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(1920, 1080);
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();

            var safeGo = new GameObject("SafeArea", typeof(RectTransform));
            safeGo.transform.SetParent(transform, false);
            _safeArea = safeGo.AddComponent<SafeAreaAdapter>();
            var safeRt = (RectTransform)safeGo.transform;
            safeRt.anchorMin = Vector2.zero;
            safeRt.anchorMax = Vector2.one;
            safeRt.offsetMin = Vector2.zero;
            safeRt.offsetMax = Vector2.zero;

            _layers = new RectTransform[6];
            string[] names = { "Layer_Scene", "Layer_Hud", "Layer_Window", "Layer_Popup", "Layer_Guide", "Layer_Toast" };
            for (int i = 0; i < names.Length; i++)
            {
                var layerGo = new GameObject(names[i], typeof(RectTransform));
                layerGo.transform.SetParent(safeGo.transform, false);
                var rt = (RectTransform)layerGo.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                _layers[i] = rt;
            }

            var blockerGo = new GameObject("ModalBlocker", typeof(RectTransform), typeof(Image), typeof(Button));
            blockerGo.transform.SetParent(_layers[(int)UILayer.Popup], false);
            var brt = (RectTransform)blockerGo.transform;
            brt.anchorMin = Vector2.zero;
            brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero;
            brt.offsetMax = Vector2.zero;
            var img = blockerGo.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.45f);
            img.raycastTarget = true;
            blockerGo.SetActive(false);
            _modalBlocker = blockerGo;

            EnsureEventSystem();
        }

        /// <summary>取得层根。layer 越界夹到有效范围。</summary>
        public RectTransform GetLayer(UILayer layer)
        {
            int i = Mathf.Clamp((int)layer, 0, _layers.Length - 1);
            return _layers[i];
        }

        /// <summary>
        /// 模态深度 +1。遮挡显示并放到 modalWindow 下方（若提供），保证当前模态窗按钮可点。
        /// </summary>
        public void PushModal(Transform modalWindow = null)
        {
            _modalDepth++;
            if (_modalBlocker == null)
                return;
            _modalBlocker.SetActive(true);
            if (modalWindow != null && modalWindow.parent == _modalBlocker.transform.parent)
            {
                int winIndex = modalWindow.GetSiblingIndex();
                _modalBlocker.transform.SetSiblingIndex(winIndex);
                modalWindow.SetAsLastSibling();
                // 再把 blocker 紧贴窗口下方：窗口最上，blocker 次之
                int top = modalWindow.GetSiblingIndex();
                _modalBlocker.transform.SetSiblingIndex(Mathf.Max(0, top - 1));
            }
            else
            {
                _modalBlocker.transform.SetAsLastSibling();
            }
        }

        /// <summary>模态深度 -1；归零时隐藏遮挡。</summary>
        public void PopModal()
        {
            if (_modalDepth > 0)
                _modalDepth--;
            if (_modalDepth <= 0 && _modalBlocker != null)
                _modalBlocker.SetActive(false);
        }

        public void ResetModal()
        {
            _modalDepth = 0;
            if (_modalBlocker != null)
                _modalBlocker.SetActive(false);
        }

        static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
                return;
            var es = new GameObject("EventSystem");
            DontDestroyOnLoad(es);
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
