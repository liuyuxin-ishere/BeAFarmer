using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Game.Framework.Boot
{
    /// <summary>
    /// Launcher 前的固定 C# 补丁进度界面。面向用户阶段文案；重试单门闸；不暴露内部包名/堆栈/凭据。
    /// </summary>
    public sealed class PatchProgressView : MonoBehaviour
    {
        static readonly Regex BearerSecret = new Regex(
            @"Bearer\s+\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex PasswordSecret = new Regex(
            @"password\s*=\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex TokenSecret = new Regex(
            @"\b(token|access_token|api[_-]?key)\s*[:=]\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [SerializeField] Text statusText;
        [SerializeField] Text detailText;
        [SerializeField] Slider progressSlider;
        [SerializeField] Button retryButton;

        Action _retryHandler;
        int _retryGate;
        string _lastStage = "";
        string _lastUserStage = "";

        /// <summary>当前是否已占用重试门闸（1=占用）。测试可读。</summary>
        public bool RetryGateTaken => _retryGate != 0;

        /// <summary>最近一次 Render 的内部 Stage（诊断用，不直接展示）。</summary>
        public string LastStage => _lastStage;

        /// <summary>最近一次面向用户的阶段文案。</summary>
        public string LastUserStage => _lastUserStage;

        /// <summary>状态文本组件（可空时跳过写入）。</summary>
        public Text StatusText => statusText;

        /// <summary>重试按钮（可空）。</summary>
        public Button RetryButton => retryButton;

        /// <summary>
        /// 绑定 UI 引用。供代码动态创建或测试装配。
        /// </summary>
        public void Bind(Text status, Text detail, Slider slider, Button retry)
        {
            statusText = status;
            detailText = detail;
            progressSlider = slider;
            retryButton = retry;
            if (retryButton != null)
            {
                retryButton.onClick.RemoveAllListeners();
                retryButton.onClick.AddListener(OnRetryClicked);
                retryButton.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 根据进度刷新界面。TotalBytes/TotalFiles 为 0 时显示完成且不除零。
        /// 状态行使用 MapStageToUserText，不直接写内部包名。
        /// </summary>
        public void Render(PatchProgress progress)
        {
            _lastStage = progress.Stage ?? "";
            _lastUserStage = MapStageToUserText(_lastStage);
            if (statusText != null)
                statusText.text = _lastUserStage;

            long totalBytes = progress.TotalBytes;
            int totalFiles = progress.TotalFiles;
            if (detailText != null)
            {
                detailText.text = string.Format(
                    "{0} / {1} 字节 · {2} / {3} 文件",
                    progress.DownloadedBytes,
                    totalBytes,
                    progress.CompletedFiles,
                    totalFiles);
            }

            if (progressSlider != null)
            {
                progressSlider.minValue = 0f;
                progressSlider.maxValue = 1f;
                if (totalBytes <= 0 && totalFiles <= 0)
                {
                    progressSlider.value = IsReadyStage(_lastStage) ? 1f : 0f;
                }
                else if (totalBytes > 0)
                {
                    progressSlider.value = Mathf.Clamp01((float)progress.DownloadedBytes / totalBytes);
                }
                else
                {
                    progressSlider.value = Mathf.Clamp01((float)progress.CompletedFiles / totalFiles);
                }
            }
        }

        /// <summary>
        /// 展示对用户可读的错误，并挂上单次重试回调。不展示堆栈/路径凭据。
        /// </summary>
        public void ShowError(string message, Action onRetry)
        {
            _retryHandler = onRetry;
            InterlockedExchangeGate(0);
            string reason = MapFailureReason(message);
            if (statusText != null)
                statusText.text = "更新失败：" + reason;
            if (detailText != null)
                detailText.text = _lastUserStage;
            if (retryButton != null)
            {
                retryButton.onClick.RemoveAllListeners();
                retryButton.onClick.AddListener(OnRetryClicked);
                retryButton.gameObject.SetActive(true);
                retryButton.interactable = true;
            }
        }

        /// <summary>清除错误态与重试回调。</summary>
        public void ClearError()
        {
            _retryHandler = null;
            InterlockedExchangeGate(0);
            if (retryButton != null)
            {
                retryButton.gameObject.SetActive(false);
                retryButton.interactable = true;
            }
        }

        /// <summary>测试用：直接触发重试点击逻辑。</summary>
        public void InvokeRetryForTests()
        {
            OnRetryClicked();
        }

        /// <summary>
        /// 通过真实 UI 事件路径点击重试（ExecuteEvents），供验收；非仅 InvokeRetryForTests。
        /// </summary>
        public bool TryClickRetryViaEventSystem()
        {
            if (retryButton == null || !retryButton.gameObject.activeInHierarchy)
                return false;
            EnsureEventSystem();
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left
            };
            ExecuteEvents.Execute(retryButton.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            return true;
        }

        void OnRetryClicked()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _retryGate, 1, 0) != 0)
                return;

            if (retryButton != null)
                retryButton.interactable = false;

            Action handler = _retryHandler;
            if (handler == null)
            {
                InterlockedExchangeGate(0);
                if (retryButton != null)
                    retryButton.interactable = true;
                return;
            }

            try
            {
                handler();
            }
            catch (Exception)
            {
                InterlockedExchangeGate(0);
                if (retryButton != null)
                    retryButton.interactable = true;
                throw;
            }
        }

        /// <summary>重试请求结束后释放门闸，允许再次点击。</summary>
        public void ReleaseRetryGate()
        {
            InterlockedExchangeGate(0);
            if (retryButton != null)
                retryButton.interactable = true;
        }

        void InterlockedExchangeGate(int value)
        {
            System.Threading.Interlocked.Exchange(ref _retryGate, value);
        }

        /// <summary>
        /// 内部阶段 → 用户可读中文。不暴露包名/原始 Stage 前缀细节。
        /// </summary>
        public static string MapStageToUserText(string stage)
        {
            if (string.IsNullOrEmpty(stage))
                return "准备中";
            if (stage.StartsWith("Failed", StringComparison.Ordinal)
                || string.Equals(stage, "Failed", StringComparison.Ordinal))
                return "更新失败";
            if (string.Equals(stage, "Cancelled", StringComparison.Ordinal))
                return "已取消";
            if (IsReadyStage(stage))
                return "准备完成";
            if (stage.StartsWith("Download", StringComparison.Ordinal))
                return "正在下载更新";
            if (stage.StartsWith("LoadManifest", StringComparison.Ordinal))
                return "正在加载清单";
            if (stage.StartsWith("RequestVersion", StringComparison.Ordinal))
                return "正在检查版本";
            if (stage.StartsWith("Initialize", StringComparison.Ordinal)
                || stage.StartsWith("PackageReady", StringComparison.Ordinal))
                return "正在准备更新";
            return "正在更新";
        }

        /// <summary>失败原因映射：网络/取消/通用，去除堆栈与秘密。</summary>
        public static string MapFailureReason(string message)
        {
            string cleaned = SanitizeUserMessage(message);
            string lower = cleaned.ToLowerInvariant();
            if (lower.Contains("cancel"))
                return "操作已取消，可重试";
            if (lower.Contains("timeout") || lower.Contains("timed out"))
                return "网络超时，请重试";
            if (lower.Contains("404") || lower.Contains("not found") || lower.Contains("连接")
                || lower.Contains("http") || lower.Contains("download") || lower.Contains("下载")
                || lower.Contains("unreachable") || lower.Contains("failed to connect"))
                return "网络异常，请检查后重试";
            if (cleaned.Length > 80)
                return "发生错误，请重试";
            // 仍可能含内部词，再压成通用文案若像异常类型
            if (cleaned.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || cleaned.IndexOf(" at ", StringComparison.Ordinal) >= 0)
                return "发生错误，请重试";
            return cleaned;
        }

        /// <summary>
        /// 去掉堆栈；真正移除 Bearer/密码/Token 值（不是只删关键字留下密文）。
        /// </summary>
        public static string SanitizeUserMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "未知错误";
            string cleaned = message;
            int at = cleaned.IndexOf(" at ", StringComparison.Ordinal);
            if (at > 0)
                cleaned = cleaned.Substring(0, at);
            int stack = cleaned.IndexOf("\n  at ", StringComparison.Ordinal);
            if (stack > 0)
                cleaned = cleaned.Substring(0, stack);
            cleaned = BearerSecret.Replace(cleaned, "Bearer ***");
            cleaned = PasswordSecret.Replace(cleaned, "password=***");
            cleaned = TokenSecret.Replace(cleaned, "$1=***");
            if (cleaned.Length > 240)
                cleaned = cleaned.Substring(0, 240) + "…";
            return cleaned.Trim();
        }

        /// <summary>
        /// 在场景中创建最小进度 UI，并确保单一 EventSystem + 兼容输入模块。
        /// </summary>
        public static PatchProgressView CreateRuntime(Transform parent = null)
        {
            EnsureEventSystem();

            var root = new GameObject("PatchProgressView");
            if (parent != null)
                root.transform.SetParent(parent, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            root.AddComponent<GraphicRaycaster>();

            // 深色底，保证截图可见
            var bg = new GameObject("Background");
            bg.transform.SetParent(root.transform, false);
            var bgRect = bg.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.08f, 0.1f, 0.14f, 1f);
            bgImage.raycastTarget = false;

            var view = root.AddComponent<PatchProgressView>();
            Font font = ResolveUiFont();

            Text status = CreateText(root.transform, "Status", font, 28, new Vector2(0.5f, 0.65f));
            Text detail = CreateText(root.transform, "Detail", font, 20, new Vector2(0.5f, 0.55f));
            Slider slider = CreateSlider(root.transform);
            Button retry = CreateButton(root.transform, font);
            view.Bind(status, detail, slider, retry);
            return view;
        }

        /// <summary>确保场景仅一套 EventSystem；Input System 工程挂兼容模块。</summary>
        public static void EnsureEventSystem()
        {
            EventSystem existing = EventSystem.current != null
                ? EventSystem.current
                : UnityEngine.Object.FindObjectOfType<EventSystem>();
            if (existing == null)
            {
                var go = new GameObject("EventSystem");
                existing = go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
                if (go.GetComponent<InputSystemUIInputModule>() == null
                    && go.GetComponent<StandaloneInputModule>() == null)
                {
                    go.AddComponent<InputSystemUIInputModule>();
                }
#else
                if (go.GetComponent<StandaloneInputModule>() == null)
                    go.AddComponent<StandaloneInputModule>();
#endif
                return;
            }

#if ENABLE_INPUT_SYSTEM
            if (existing.GetComponent<InputSystemUIInputModule>() == null
                && existing.GetComponent<StandaloneInputModule>() == null)
            {
                existing.gameObject.AddComponent<InputSystemUIInputModule>();
            }
#else
            if (existing.GetComponent<StandaloneInputModule>() == null
                && existing.GetComponent<BaseInputModule>() == null)
            {
                existing.gameObject.AddComponent<StandaloneInputModule>();
            }
#endif
        }

        /// <summary>Windows 中文可读字体；回退 LegacyRuntime。</summary>
        public static Font ResolveUiFont()
        {
            try
            {
                Font os = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Arial Unicode MS", "Arial" },
                    24);
                if (os != null)
                    return os;
            }
            catch
            {
            }

            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static bool IsReadyStage(string stage)
        {
            return string.Equals(stage, "Ready", StringComparison.Ordinal)
                   || string.Equals(stage, "PackageReady", StringComparison.Ordinal)
                   || (stage != null && stage.StartsWith("PackageReady", StringComparison.Ordinal))
                   || (stage != null && stage.StartsWith("Ready", StringComparison.Ordinal));
        }

        static Text CreateText(Transform parent, string name, Font font, int size, Vector2 anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(900, 48);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = name;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        static Slider CreateSlider(Transform parent)
        {
            var go = new GameObject("Progress");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.45f);
            rect.anchorMax = new Vector2(0.5f, 0.45f);
            rect.sizeDelta = new Vector2(640, 24);
            var slider = go.AddComponent<Slider>();
            var bg = new GameObject("Background");
            bg.transform.SetParent(go.transform, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var fillArea = new GameObject("Fill Area");
            fillArea.transform.SetParent(go.transform, false);
            var fillAreaRect = fillArea.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = Vector2.zero;
            fillAreaRect.anchorMax = Vector2.one;
            fillAreaRect.offsetMin = Vector2.zero;
            fillAreaRect.offsetMax = Vector2.zero;
            var fill = new GameObject("Fill");
            fill.transform.SetParent(fillArea.transform, false);
            var fillImage = fill.AddComponent<Image>();
            fillImage.color = new Color(0.2f, 0.7f, 0.3f, 1f);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            slider.fillRect = fillRect;
            slider.targetGraphic = fillImage;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            return slider;
        }

        static Button CreateButton(Transform parent, Font font)
        {
            var go = new GameObject("Retry");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.32f);
            rect.anchorMax = new Vector2(0.5f, 0.32f);
            rect.sizeDelta = new Vector2(200, 48);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.35f, 0.55f, 1f);
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            var label = labelGo.AddComponent<Text>();
            label.font = font;
            label.fontSize = 22;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = "重试";
            go.SetActive(false);
            return button;
        }
    }
}
