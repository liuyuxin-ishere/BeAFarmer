using UnityEngine;

namespace Game.Framework.UI
{
    /// <summary>
    /// 固定 SafeArea 适配：safeAreaPixels / screenSize → 锚点；屏幕变化重算。
    /// 零大小或越界输入时回退全屏锚点，不抛未处理异常。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SafeAreaAdapter : MonoBehaviour
    {
        RectTransform _rect;
        Rect _lastSafe;
        Vector2Int _lastScreen;

        void Awake()
        {
            _rect = GetComponent<RectTransform>();
            Apply(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));
        }

        void Update()
        {
            Rect safe = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (safe == _lastSafe && screen == _lastScreen)
                return;
            Apply(safe, screen);
        }

        /// <summary>
        /// 将像素安全区转为锚点。screenSize 任一维 ≤0 或 safe 无效时锚点归 (0,0)-(1,1)。
        /// </summary>
        public void Apply(Rect safeAreaPixels, Vector2Int screenSize)
        {
            if (_rect == null)
                _rect = GetComponent<RectTransform>();
            if (_rect == null)
                return;

            _lastSafe = safeAreaPixels;
            _lastScreen = screenSize;

            if (screenSize.x <= 0 || screenSize.y <= 0
                || safeAreaPixels.width <= 0f || safeAreaPixels.height <= 0f)
            {
                _rect.anchorMin = Vector2.zero;
                _rect.anchorMax = Vector2.one;
                _rect.offsetMin = Vector2.zero;
                _rect.offsetMax = Vector2.zero;
                return;
            }

            float xMin = Mathf.Clamp01(safeAreaPixels.xMin / screenSize.x);
            float yMin = Mathf.Clamp01(safeAreaPixels.yMin / screenSize.y);
            float xMax = Mathf.Clamp01(safeAreaPixels.xMax / screenSize.x);
            float yMax = Mathf.Clamp01(safeAreaPixels.yMax / screenSize.y);
            if (xMax < xMin)
                xMax = xMin;
            if (yMax < yMin)
                yMax = yMin;

            _rect.anchorMin = new Vector2(xMin, yMin);
            _rect.anchorMax = new Vector2(xMax, yMax);
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
        }

        /// <summary>当前锚点最小（测试用）。</summary>
        public Vector2 AnchorMin => _rect != null ? _rect.anchorMin : Vector2.zero;

        /// <summary>当前锚点最大（测试用）。</summary>
        public Vector2 AnchorMax => _rect != null ? _rect.anchorMax : Vector2.one;
    }
}
