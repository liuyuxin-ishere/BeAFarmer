using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Account
{
    /// <summary>
    /// 登录后断线时的单一全屏遮罩，阻断业务输入。同一时刻最多一个实例。
    /// </summary>
    public sealed class DisconnectOverlay
    {
        static DisconnectOverlay _live;
        GameObject _root;
        Text _label;

        /// <summary>当前是否显示。</summary>
        public bool IsVisible => _root != null && _root.activeSelf;

        /// <summary>显示或更新遮罩文案。重复调用只更新文本。</summary>
        public void Show(string message)
        {
            if (_root == null)
                Build();
            _root.SetActive(true);
            if (_label != null)
                _label.text = string.IsNullOrEmpty(message) ? "网络断开，正在重连…" : message;
            _root.transform.SetAsLastSibling();
        }

        /// <summary>隐藏遮罩；未创建时无操作。</summary>
        public void Hide()
        {
            if (_root != null)
                _root.SetActive(false);
        }

        /// <summary>销毁实例并清空静态 live。</summary>
        public void Dispose()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
                _label = null;
            }

            if (ReferenceEquals(_live, this))
                _live = null;
        }

        /// <summary>取或建单例。</summary>
        public static DisconnectOverlay Ensure()
        {
            if (_live != null && _live._root != null)
                return _live;
            _live = new DisconnectOverlay();
            _live.Build();
            return _live;
        }

        void Build()
        {
            var canvasGo = GameObject.Find("M1DisconnectOverlayCanvas");
            if (canvasGo == null)
            {
                canvasGo = new GameObject("M1DisconnectOverlayCanvas");
                Object.DontDestroyOnLoad(canvasGo);
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;
                canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasGo.AddComponent<GraphicRaycaster>();
            }

            _root = new GameObject("DisconnectMask", typeof(RectTransform), typeof(Image));
            _root.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)_root.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = _root.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.72f);
            img.raycastTarget = true;

            var textGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(_root.transform, false);
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = new Vector2(0.1f, 0.4f);
            trt.anchorMax = new Vector2(0.9f, 0.6f);
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            _label = textGo.GetComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_label.font == null)
                _label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _label.fontSize = 36;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = Color.white;
            _label.raycastTarget = false;
            _root.SetActive(false);
        }
    }
}
