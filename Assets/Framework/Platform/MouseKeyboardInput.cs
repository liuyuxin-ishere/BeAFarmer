using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Framework.Platform
{
    /// <summary>
    /// 从 MouseKeyboard 动作地图读取键盘和鼠标。Tick 负责采样、识别点击或拖拽，并写调试日志。
    /// </summary>
    public sealed class MouseKeyboardInput : IInputProvider
    {
        /// <summary>按下后指针离开起点超过这个像素数，这次手势算拖拽。</summary>
        public const float DragThresholdPixels = 8f;

        readonly InputActionAsset _asset;
        InputActionMap _map;
        InputAction _move;
        InputAction _point;
        InputAction _press;
        InputAction _zoomAction;
        Vector2 _moveValue;
        Vector2 _pointer;
        Vector2 _pressOrigin;
        Vector2 _lastDragPoint;
        float _zoom;
        bool _enabled;
        bool _pressing;
        bool _dragging;
        bool _tap;

        /// <param name="asset">含 MouseKeyboard 地图的输入配置。</param>
        public MouseKeyboardInput(InputActionAsset asset)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));
            _asset = asset;
        }

        /// <summary>最近一次 Tick 的移动。没有输入时是零向量，长度不超过 1。</summary>
        public Vector2 ReadMove()
        {
            return _moveValue;
        }

        /// <summary>最近一次 Tick 的指针位置，单位是屏幕像素。</summary>
        public Vector2 ReadPointer()
        {
            return _pointer;
        }

        /// <summary>最近一次 Tick 是否把这次按下识别成点击。</summary>
        public bool WasTapPressed()
        {
            return _tap;
        }

        /// <summary>指针按键是否仍按下（只读观察；不改变既有 tap/drag 语义）。</summary>
        public bool IsPressing => _pressing;

        /// <summary>最近一次 Tick 的滚轮 y。</summary>
        public float ReadZoom()
        {
            return _zoom;
        }

        /// <summary>拖拽开始。参数是按下时的屏幕像素。</summary>
        public event Action<Vector2> DragStarted;

        /// <summary>拖拽过程中指针位置变化。参数是当前屏幕像素。</summary>
        public event Action<Vector2> Dragged;

        /// <summary>拖拽结束。参数是松开时的屏幕像素。</summary>
        public event Action<Vector2> DragEnded;

        /// <summary>启用 MouseKeyboard 地图。重复调用会抛异常。</summary>
        public void Enable()
        {
            if (_enabled)
                throw new InvalidOperationException("输入已经启用");

            _map = _asset.FindActionMap("MouseKeyboard", false);
            if (_map == null)
                throw new InvalidOperationException("缺少动作地图 MouseKeyboard");

            _move = RequireAction("Move");
            _point = RequireAction("Point");
            _press = RequireAction("Press");
            _zoomAction = RequireAction("Zoom");
            _map.Enable();
            _enabled = true;
        }

        /// <summary>关闭采样并清掉未完成的按下和拖拽。重复调用直接返回。</summary>
        public void Disable()
        {
            if (!_enabled)
                return;

            _map.Disable();
            _enabled = false;
            _pressing = false;
            _dragging = false;
            _tap = false;
            _moveValue = Vector2.zero;
            _pointer = Vector2.zero;
            _zoom = 0f;
        }

        /// <summary>采样当前动作。未启用时直接返回，不写日志、不发事件。</summary>
        public void Tick()
        {
            if (!_enabled)
                return;

            _tap = false;
            _moveValue = _move.ReadValue<Vector2>();
            if (_moveValue.sqrMagnitude > 1f)
                _moveValue.Normalize();
            if (_moveValue.sqrMagnitude > 0f)
                Log("move", _moveValue);

            _pointer = _point.ReadValue<Vector2>();
            _zoom = _zoomAction.ReadValue<float>();
            if (_zoom != 0f)
                LogZoom(_zoom);

            bool pressedThisFrame = _press.WasPressedThisFrame();
            bool releasedThisFrame = _press.WasReleasedThisFrame();
            if (pressedThisFrame)
            {
                _pressing = true;
                _dragging = false;
                _pressOrigin = _pointer;
            }

            if (_pressing && !_dragging)
            {
                float distance = Vector2.Distance(_pointer, _pressOrigin);
                if (distance > DragThresholdPixels)
                {
                    _dragging = true;
                    _lastDragPoint = _pointer;
                    Log("drag-start", _pressOrigin);
                    DragStarted?.Invoke(_pressOrigin);
                    Log("drag", _pointer);
                    Dragged?.Invoke(_pointer);
                }
            }
            else if (_dragging && _pointer != _lastDragPoint)
            {
                _lastDragPoint = _pointer;
                Log("drag", _pointer);
                Dragged?.Invoke(_pointer);
            }

            if (!releasedThisFrame)
                return;

            _pressing = false;
            if (_dragging)
            {
                _dragging = false;
                Log("drag-end", _pointer);
                DragEnded?.Invoke(_pointer);
                return;
            }

            _tap = true;
            Log("tap", _pointer);
        }

        InputAction RequireAction(string name)
        {
            InputAction action = _map.FindAction(name, false);
            if (action == null)
                throw new InvalidOperationException("缺少动作 " + name);
            return action;
        }

        static void Log(string kind, Vector2 value)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[Input] {0}=({1},{2})",
                kind,
                value.x,
                value.y));
        }

        static void LogZoom(float value)
        {
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[Input] zoom={0}", value));
        }
    }
}
