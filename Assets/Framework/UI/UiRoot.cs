using System.Collections.Generic;
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

        sealed class ModalEntry
        {
            public Transform Window;
            public object Identity;
        }

        RectTransform[] _layers;
        SafeAreaAdapter _safeArea;
        CanvasScaler _scaler;
        GameObject _modalBlocker;
        readonly List<ModalEntry> _modalStack = new List<ModalEntry>();

        public CanvasScaler Scaler => _scaler;
        public SafeAreaAdapter SafeArea => _safeArea;
        public int ModalDepth => _modalStack.Count;
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
        /// 模态入栈。遮挡显示并放到 modalWindow 下方（若提供），保证当前模态窗按钮可点。
        /// 身份默认取 modalWindow；匿名 push（null）使用独立哨兵，仅参数化 PopModal() 可 LIFO 弹出。
        /// </summary>
        public void PushModal(Transform modalWindow = null)
        {
            PushModal(modalWindow, identity: modalWindow);
        }

        /// <summary>
        /// 带稳定身份的模态入栈。identity 用于非 LIFO / 失败清理的精确出栈；
        /// 迟到的下层 Pop 不会误弹上层。identity 为 null 时回退为 modalWindow（再空则匿名哨兵）。
        /// </summary>
        public void PushModal(Transform modalWindow, object identity)
        {
            object id = identity ?? (object)modalWindow ?? new object();
            _modalStack.Add(new ModalEntry { Window = modalWindow, Identity = id });
            ApplyBlockerToRemainingTop();
        }

        /// <summary>
        /// 兼容旧调用：LIFO 弹出栈顶并恢复遮罩到剩余顶层窗之下；栈空则隐藏。
        /// </summary>
        public void PopModal()
        {
            if (_modalStack.Count == 0)
                return;
            _modalStack.RemoveAt(_modalStack.Count - 1);
            ApplyBlockerToRemainingTop();
        }

        /// <summary>
        /// 按身份精确出栈。未命中则空操作（不夺上层）；命中后把遮罩恢复到剩余存活顶层窗之下。
        /// identity 为 null 时回退 LIFO <see cref="PopModal()"/>。
        /// </summary>
        public void PopModal(object identity)
        {
            if (identity == null)
            {
                PopModal();
                return;
            }

            for (int i = _modalStack.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_modalStack[i].Identity, identity))
                {
                    _modalStack.RemoveAt(i);
                    break;
                }
            }

            ApplyBlockerToRemainingTop();
        }

        public void ResetModal()
        {
            _modalStack.Clear();
            if (_modalBlocker != null)
                _modalBlocker.SetActive(false);
        }

        void ApplyBlockerToRemainingTop()
        {
            if (_modalBlocker == null)
                return;

            if (_modalStack.Count == 0)
            {
                _modalBlocker.SetActive(false);
                return;
            }

            _modalBlocker.SetActive(true);

            Transform topWindow = null;
            for (int i = _modalStack.Count - 1; i >= 0; i--)
            {
                Transform w = _modalStack[i].Window;
                // Unity 已销毁 Transform 视为假 null，跳过，不把迟到下层当顶层。
                if (w != null)
                {
                    topWindow = w;
                    break;
                }
            }

            if (topWindow != null && topWindow.parent == _modalBlocker.transform.parent)
            {
                topWindow.SetAsLastSibling();
                int top = topWindow.GetSiblingIndex();
                _modalBlocker.transform.SetSiblingIndex(Mathf.Max(0, top - 1));
            }
            else
            {
                _modalBlocker.transform.SetAsLastSibling();
            }
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
