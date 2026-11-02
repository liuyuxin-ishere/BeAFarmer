using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Framework.Gameplay;
using Game.Framework.Platform;
using Game.Framework.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Framework.Home
{
    /// <summary>
    /// 采样 MouseKeyboardInput，把 tap/drag 收成至多一次许可意图。
    /// 模态/UI 遮挡/失焦/不可写/禁用输入时取消手势；陈旧 release 不发写。
    /// </summary>
    public sealed class HomeFarmGestureController : IDisposable
    {
        public const int MaxBatchPlots = 24;

        public delegate void GestureCommitHandler(string toolMode, IReadOnlyList<int> plotIds, bool previewOnly);
        public delegate void GestureCancelledHandler(string reason);
        public delegate void NodeSelectHandler(int nodeInstanceId);

        readonly HomeInputAdapter _adapter;
        readonly MouseKeyboardInput _input;
        readonly Func<bool> _isWritable;
        readonly Func<bool> _hasModal;
        readonly Func<bool> _isFocused;
        readonly Func<Vector2, bool> _isPointerOverUi;
        readonly HashSet<int> _dragSet = new HashSet<int>();
        readonly List<int> _dragOrder = new List<int>(MaxBatchPlots);

        GestureCommitHandler _onCommit;
        GestureCancelledHandler _onCancel;
        NodeSelectHandler _onNodeSelect;
        string _toolMode = "harvest";
        int _gestureGen;
        int _activeGen = -1;
        bool _dragging;
        bool _disposed;
        /// <summary>
        /// 受阻 press 后须吞掉同一次按键的 release→tap；CancelActive 不得清掉本标志。
        /// </summary>
        bool _ignoreCurrentPress;

        public HomeFarmGestureController(
            HomeInputAdapter adapter,
            MouseKeyboardInput input,
            Func<bool> isWritable = null,
            Func<bool> hasModal = null,
            Func<bool> isFocused = null,
            Func<Vector2, bool> isPointerOverUi = null)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _isWritable = isWritable ?? DefaultWritable;
            _hasModal = hasModal ?? DefaultHasModal;
            _isFocused = isFocused ?? DefaultIsFocused;
            _isPointerOverUi = isPointerOverUi ?? DefaultIsPointerOverUi;
        }

        public bool IsDragging => _dragging;
        public int GestureGeneration => _gestureGen;
        public string ToolMode => _toolMode;
        public IReadOnlyList<int> PreviewPlotIds => _dragOrder;

        public void SetHandlers(GestureCommitHandler onCommit, GestureCancelledHandler onCancel)
        {
            _onCommit = onCommit;
            _onCancel = onCancel;
        }

        /// <summary>采集点单击选择；不发网络、不进入地块拖拽批次。</summary>
        public void SetNodeSelectHandler(NodeSelectHandler onNodeSelect)
        {
            _onNodeSelect = onNodeSelect;
        }

        public void SetToolMode(string mode)
        {
            string next = string.IsNullOrEmpty(mode) ? "harvest" : mode;
            if (_dragging && !string.Equals(_toolMode, next, StringComparison.Ordinal))
            {
                _ignoreCurrentPress = true;
                CancelActive("tool-change");
            }

            _toolMode = next;
        }

        public void SetInputEnabled(bool enabled)
        {
            _adapter.SetInputEnabled(enabled);
            if (!enabled)
                CancelActive("input-disabled");
        }

        /// <summary>
        /// 每帧调用：Tick 输入；采样当前 pointer 做 UI/门控。
        /// 受阻 press 吞掉整次物理按键直至 release；held/drag 每帧检查遮挡（不依赖移动事件）。
        /// </summary>
        public void TickFrame()
        {
            if (_disposed)
                return;

            if (!IsFocusedNow())
            {
                CancelActive("focus-lost");
                TickWhileBlocked();
                return;
            }

            if (_hasModal())
            {
                CancelActive("modal");
                TickWhileBlocked();
                return;
            }

            if (!_adapter.InputEnabled || !_isWritable())
            {
                CancelActive(!_adapter.InputEnabled ? "input-disabled" : "not-writable");
                TickWhileBlocked();
                return;
            }

            _input.Tick();
            Vector2 pointer = _input.ReadPointer();

            // 拖拽中：静止 pointer 下 UI 出现也必须取消（OnDragged 仅在移动时触发）
            if (_dragging)
            {
                if (!GatesAllow() || IsPointerOverUiAt(pointer))
                {
                    _ignoreCurrentPress = true;
                    CancelActive("ui-obstruction");
                }

                return;
            }

            // 整次物理按键已标记忽略：吞掉直至 release（含随后可能的 tap）
            if (_ignoreCurrentPress)
            {
                // Drag release has no tap flag. Its canceled physical cycle is nevertheless over.
                if (!_input.IsPressing)
                    _ignoreCurrentPress = false;
                return;
            }

            // 按下源或 held 未成拖时：当前 pointer 落在 UI 上则取消本轮按键
            if (_input.IsPressing && IsPointerOverUiAt(pointer))
            {
                _ignoreCurrentPress = true;
                return;
            }

            if (!_input.WasTapPressed())
                return;

            if (IsPointerOverUiAt(pointer))
                return;

            if (TrySelectNodeAt(pointer))
                return;

            _dragSet.Clear();
            _dragOrder.Clear();
            _activeGen = ++_gestureGen;
            if (!TryAddAt(pointer, out _))
            {
                _activeGen = -1;
                return;
            }

            CommitCurrent(previewOnly: false);
        }

        public void OnDragStarted(Vector2 screen)
        {
            if (_disposed)
                return;

            // 已忽略的物理按键不得因 UI 消失后的移动变成 drag 写
            if (_ignoreCurrentPress || !GatesAllow() || IsPointerOverUiAt(screen))
            {
                _ignoreCurrentPress = true;
                CancelActive("ui-or-gate-on-press");
                return;
            }

            // 采集点：不启动地块拖拽；单击选择由 TickFrame tap 路径处理
            if (_adapter.TryResolveScreenPoint(screen, out HomeTarget hit) && hit.IsNode)
            {
                _ignoreCurrentPress = true;
                return;
            }

            _dragging = true;
            _activeGen = ++_gestureGen;
            _dragSet.Clear();
            _dragOrder.Clear();
            TryAddAt(screen, out _);
            EmitPreview();
        }

        public void OnDragged(Vector2 screen)
        {
            if (_disposed || !_dragging || _activeGen != _gestureGen)
                return;

            if (!GatesAllow() || IsPointerOverUiAt(screen))
            {
                _ignoreCurrentPress = true;
                CancelActive("ui-obstruction");
                return;
            }

            TryAddAt(screen, out _);
            EmitPreview();
        }

        public void OnDragEnded(Vector2 screen)
        {
            if (_disposed)
                return;

            int gen = _activeGen;
            if (!_dragging || gen < 0 || gen != _gestureGen)
            {
                // 陈旧/已取消 release：不写；若仍忽略本按键则吞掉
                _dragging = false;
                if (_ignoreCurrentPress)
                    _ignoreCurrentPress = false;
                return;
            }

            if (!GatesAllow() || IsPointerOverUiAt(screen))
            {
                _ignoreCurrentPress = true;
                CancelActive("gate-on-release");
                return;
            }

            TryAddAt(screen, out _);
            CommitCurrent(previewOnly: false);
        }

        public void CancelActive(string reason)
        {
            bool had = _dragging || _dragOrder.Count > 0 || _activeGen >= 0;
            _dragging = false;
            _activeGen = -1;
            _dragSet.Clear();
            _dragOrder.Clear();
            // 故意不清除 _ignoreCurrentPress：受阻 press 后的 release 仍须忽略
            if (had)
                _onCancel?.Invoke(reason ?? "cancel");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelActive("dispose");
            _ignoreCurrentPress = false;
            _onCommit = null;
            _onCancel = null;
            _onNodeSelect = null;
        }

        bool TrySelectNodeAt(Vector2 screen)
        {
            if (!_adapter.TryResolveScreenPoint(screen, out HomeTarget target) || !target.IsNode)
                return false;
            NodeSelectHandler h = _onNodeSelect;
            if (h == null)
                return true; // 命中 Node 仍吞掉，避免误当地块
            try { h(target.InstanceId); }
            catch (Exception) { /* 观察者异常不回灌输入 */ }
            return true;
        }

        /// <summary>
        /// 门控阻挡帧：仍采样输入。仅当按键仍按下时置 ignore；
        /// 阻挡期内的 release→tap 直接吞掉并清 ignore，避免无按键时永久吞后续 tap。
        /// </summary>
        void TickWhileBlocked()
        {
            _input.Tick();
            if (_input.WasTapPressed())
            {
                _ignoreCurrentPress = false;
                return;
            }

            if (_input.IsPressing)
                _ignoreCurrentPress = true;
        }

        bool GatesAllow()
        {
            if (!IsFocusedNow())
                return false;
            if (_hasModal())
                return false;
            if (!_adapter.InputEnabled)
                return false;
            if (!_isWritable())
                return false;
            return true;
        }

        bool IsFocusedNow() => _isFocused();

        bool IsPointerOverUiAt(Vector2 screen) => _isPointerOverUi(screen);

        bool TryAddAt(Vector2 screen, out int plotId)
        {
            plotId = 0;
            if (!_adapter.TryResolveScreenPoint(screen, out HomeTarget target) || !target.IsPlot)
                return false;

            plotId = target.InstanceId;
            if (_dragSet.Contains(plotId))
                return true;

            if (_dragOrder.Count >= MaxBatchPlots)
                return false;

            _dragSet.Add(plotId);
            _dragOrder.Add(plotId);
            return true;
        }

        void EmitPreview()
        {
            if (_dragOrder.Count == 0)
                return;
            _onCommit?.Invoke(_toolMode, new List<int>(_dragOrder), previewOnly: true);
        }

        void CommitCurrent(bool previewOnly)
        {
            int gen = _activeGen;
            var snapshot = new List<int>(_dragOrder);
            _dragging = false;
            _activeGen = -1;
            _dragSet.Clear();
            _dragOrder.Clear();
            _ignoreCurrentPress = false;

            if (gen < 0 || snapshot.Count == 0)
                return;

            // Water/Fertilize：只允许单目标；多选时本地拒绝不发写
            string mode = _toolMode ?? "";
            if ((mode == "water" || mode == "fertilize") && snapshot.Count != 1)
            {
                _onCancel?.Invoke("single-target-required");
                return;
            }

            _onCommit?.Invoke(mode, snapshot, previewOnly);
        }

        static bool DefaultWritable() => GameplayBridgeBinding.TryIsWritable(out _);

        static bool DefaultHasModal() => UiBridge.HasModalOpen;

        static bool DefaultIsFocused() => Application.isFocused;

        /// <summary>按实际 screen point 做 UI raycast；不用无参 IsPointerOverGameObject 前帧缓存。</summary>
        static bool DefaultIsPointerOverUi(Vector2 screen)
        {
            EventSystem es = EventSystem.current;
            if (es == null)
                return false;

            var ped = new PointerEventData(es)
            {
                position = screen
            };
            var hits = new List<RaycastResult>(8);
            es.RaycastAll(ped, hits);
            return hits.Count > 0;
        }

        public static string FormatPlotIdsJson(IReadOnlyList<int> plotIds)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < plotIds.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(plotIds[i].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(']');
            return sb.ToString();
        }
    }
}
