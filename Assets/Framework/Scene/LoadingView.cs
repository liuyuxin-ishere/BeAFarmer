using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Scenes
{
    /// <summary>
    /// 常驻 AOT Loading。不依赖热更窗口；进度夹在 0–1。
    /// </summary>
    public sealed class LoadingView : MonoBehaviour
    {
        public const string RootName = "M1_LoadingView";

        Text _label;
        Slider _slider;
        CanvasGroup _group;

        public bool IsVisible => _group != null && _group.alpha > 0.01f && gameObject.activeSelf;

        public static LoadingView Ensure()
        {
            var existing = FindObjectOfType<LoadingView>();
            if (existing != null)
                return existing;

            var go = new GameObject(RootName);
            DontDestroyOnLoad(go);
            return go.AddComponent<LoadingView>();
        }

        void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            gameObject.AddComponent<GraphicRaycaster>();

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(transform, false);
            var prt = (RectTransform)panel.transform;
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.offsetMin = Vector2.zero;
            prt.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.08f, 0.85f);

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelGo.transform.SetParent(panel.transform, false);
            var lrt = (RectTransform)labelGo.transform;
            lrt.anchorMin = new Vector2(0.1f, 0.55f);
            lrt.anchorMax = new Vector2(0.9f, 0.7f);
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;
            _label = labelGo.GetComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_label.font == null)
                _label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _label.fontSize = 36;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = Color.white;
            _label.text = "Loading…";

            var sliderGo = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            sliderGo.transform.SetParent(panel.transform, false);
            var srt = (RectTransform)sliderGo.transform;
            srt.anchorMin = new Vector2(0.2f, 0.4f);
            srt.anchorMax = new Vector2(0.8f, 0.48f);
            srt.offsetMin = Vector2.zero;
            srt.offsetMax = Vector2.zero;
            _slider = sliderGo.GetComponent<Slider>();
            _slider.minValue = 0f;
            _slider.maxValue = 1f;
            _slider.value = 0f;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sliderGo.transform, false);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            fill.GetComponent<Image>().color = new Color(0.2f, 0.75f, 0.35f, 1f);
            _slider.fillRect = (RectTransform)fill.transform;
            ((RectTransform)fillArea.transform).anchorMin = Vector2.zero;
            ((RectTransform)fillArea.transform).anchorMax = Vector2.one;
            ((RectTransform)fill.transform).anchorMin = Vector2.zero;
            ((RectTransform)fill.transform).anchorMax = Vector2.one;

            Hide();
        }

        public void Show(string message)
        {
            if (_label != null)
                _label.text = string.IsNullOrEmpty(message) ? "Loading…" : message;
            gameObject.SetActive(true);
            if (_group != null)
            {
                _group.alpha = 1f;
                _group.blocksRaycasts = true;
            }
        }

        public void SetProgress(float normalizedProgress)
        {
            if (_slider != null)
                _slider.value = Mathf.Clamp01(normalizedProgress);
        }

        public void Hide()
        {
            if (_group != null)
            {
                _group.alpha = 0f;
                _group.blocksRaycasts = false;
            }

            if (_slider != null)
                _slider.value = 0f;
        }
    }
}

