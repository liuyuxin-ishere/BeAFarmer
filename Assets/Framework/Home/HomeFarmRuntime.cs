using System;
using System.Collections.Generic;
using Game.Framework.Gameplay;
using Game.Framework.Platform;
using Game.Framework.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Framework.Home
{
    /// <summary>
    /// M1_Home 装配：灰盒 24 地 + 相机 + 输入适配/手势。场景销毁时取消拖拽并释放。
    /// </summary>
    public sealed class HomeFarmRuntime : MonoBehaviour
    {
        public const string HostObjectName = "HomeFarmRuntime";

        HomeInputAdapter _adapter;
        HomeFarmGestureController _gestures;
        MouseKeyboardInput _input;
        InputActionAsset _ownedAsset;
        Camera _camera;
        Transform _plotsRoot;
        Transform _nodesRoot;
        readonly Dictionary<int, HomePlotVisual> _visuals = new Dictionary<int, HomePlotVisual>();
        readonly Dictionary<int, HomeNodeVisual> _nodeVisuals = new Dictionary<int, HomeNodeVisual>();
        float _tickAccum;
        bool _wired;

        public HomeInputAdapter Adapter => _adapter;
        public HomeFarmGestureController Gestures => _gestures;
        public bool IsReady => _wired && _adapter != null;

        public static HomeFarmRuntime EnsureInActiveScene()
        {
            var existing = FindObjectOfType<HomeFarmRuntime>();
            if (existing != null)
                return existing;
            var go = new GameObject(HostObjectName);
            return go.AddComponent<HomeFarmRuntime>();
        }

        void Awake()
        {
            Bootstrap();
        }

        void OnDestroy()
        {
            Teardown("scene-destroy");
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                _gestures?.CancelActive("focus-lost");
        }

        void Update()
        {
            if (!_wired || _gestures == null)
                return;

            // 门控轮询 + 单击采样；拖拽走 Input 事件。不在此发网络写。
            _gestures.TickFrame();

            _tickAccum += Time.unscaledDeltaTime;
            if (_tickAccum >= 0.25f)
            {
                _tickAccum = 0f;
                LuaHomeFarmFacade.RaiseDisplayTick();
            }
        }

        public void Bootstrap()
        {
            if (_wired)
                return;

            HomePlotLayout.ValidateOrThrow();
            HomeNodeLayout.ValidateOrThrow();
            _camera = HomeGreyboxSpawner.EnsureHomeCamera(transform);
            _plotsRoot = HomeGreyboxSpawner.SpawnPlots(transform);
            _nodesRoot = HomeGreyboxSpawner.SpawnNodes(transform);
            CacheVisuals();
            CacheNodeVisuals();

            _ownedAsset = CreateInputAsset();
            _input = new MouseKeyboardInput(_ownedAsset);
            _input.Enable();

            _adapter = new HomeInputAdapter(_camera, ~0);
            _gestures = new HomeFarmGestureController(
                _adapter,
                _input,
                () => GameplayBridgeBinding.TryIsWritable(out _),
                () => UiBridge.HasModalOpen);

            _input.DragStarted += OnDragStarted;
            _input.Dragged += OnDragged;
            _input.DragEnded += OnDragEnded;

            _gestures.SetHandlers(OnGestureCommit, OnGestureCancelled);
            _gestures.SetNodeSelectHandler(OnNodeSelect);
            LuaHomeFarmFacade.BindRuntime(this);
            _wired = true;
        }

        public void SetPlotDisplay(int instanceId, HomePlotDisplayKind kind)
        {
            if (_visuals.TryGetValue(instanceId, out HomePlotVisual v))
                v.SetDisplay(kind);
        }

        public void SetNodeDisplay(int instanceId, HomeNodeDisplayKind kind)
        {
            if (_nodeVisuals.TryGetValue(instanceId, out HomeNodeVisual v))
                v.SetDisplay(kind);
        }

        public void ApplyNodeDisplays(IReadOnlyDictionary<int, HomeNodeDisplayKind> map)
        {
            if (map == null)
                return;
            foreach (KeyValuePair<int, HomeNodeDisplayKind> kv in map)
                SetNodeDisplay(kv.Key, kv.Value);
        }

        /// <summary>
        /// 只读：运行时相机下地块碰撞中心的屏幕像素（Unity 原点左下）。失败不造假坐标。
        /// 非有限或视口外坐标一律拒绝（禁止 Clamp 成别的合法点）。
        /// </summary>
        public bool TryGetPlotScreenPoint(int instanceId, out float x, out float y)
        {
            return TryGetVisualScreenPoint(
                _visuals.TryGetValue(instanceId, out HomePlotVisual visual) ? visual : null,
                out x,
                out y);
        }

        public bool TryGetNodeScreenPoint(int instanceId, out float x, out float y)
        {
            return TryGetVisualScreenPoint(
                _nodeVisuals.TryGetValue(instanceId, out HomeNodeVisual visual) ? visual : null,
                out x,
                out y);
        }

        bool TryGetVisualScreenPoint(Component visual, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (!_wired || _camera == null || visual == null)
                return false;
            Collider col = visual.GetComponent<Collider>();
            Vector3 world = col != null ? col.bounds.center : visual.transform.position;
            Vector3 sp = _camera.WorldToScreenPoint(world);
            if (sp.z <= 0f)
                return false;
            if (float.IsNaN(sp.x) || float.IsNaN(sp.y) || float.IsInfinity(sp.x) || float.IsInfinity(sp.y))
                return false;
            if (sp.x < 0f || sp.y < 0f || sp.x > Screen.width || sp.y > Screen.height)
                return false;
            x = sp.x;
            y = sp.y;
            return true;
        }

        public void ApplyPlotDisplays(IReadOnlyDictionary<int, HomePlotDisplayKind> map)
        {
            if (map == null)
                return;
            foreach (KeyValuePair<int, HomePlotDisplayKind> kv in map)
                SetPlotDisplay(kv.Key, kv.Value);
        }

        void CacheVisuals()
        {
            _visuals.Clear();
            if (_plotsRoot == null)
                return;
            var all = _plotsRoot.GetComponentsInChildren<HomePlotVisual>(true);
            for (int i = 0; i < all.Length; i++)
            {
                HomePlotVisual v = all[i];
                if (v.InstanceId > 0)
                    _visuals[v.InstanceId] = v;
            }
        }

        void CacheNodeVisuals()
        {
            _nodeVisuals.Clear();
            if (_nodesRoot == null)
                return;
            var all = _nodesRoot.GetComponentsInChildren<HomeNodeVisual>(true);
            for (int i = 0; i < all.Length; i++)
            {
                HomeNodeVisual v = all[i];
                if (v.InstanceId > 0)
                    _nodeVisuals[v.InstanceId] = v;
            }
        }

        void OnDragStarted(Vector2 p) => _gestures?.OnDragStarted(p);

        void OnDragged(Vector2 p) => _gestures?.OnDragged(p);

        void OnDragEnded(Vector2 p) => _gestures?.OnDragEnded(p);

        void OnGestureCommit(string mode, IReadOnlyList<int> plotIds, bool previewOnly)
        {
            string json = HomeFarmGestureController.FormatPlotIdsJson(plotIds);
            LuaHomeFarmFacade.RaiseGesture(mode ?? "", json, previewOnly);
        }

        void OnGestureCancelled(string reason)
        {
            LuaHomeFarmFacade.RaiseGestureCancelled(reason ?? "");
        }

        void OnNodeSelect(int nodeInstanceId)
        {
            LuaHomeFarmFacade.RaiseNodeSelect(nodeInstanceId);
        }

        void Teardown(string reason)
        {
            LuaHomeFarmFacade.UnbindRuntime(this);
            if (_input != null)
            {
                _input.DragStarted -= OnDragStarted;
                _input.Dragged -= OnDragged;
                _input.DragEnded -= OnDragEnded;
                try { _input.Disable(); } catch { /* ignore */ }
                _input = null;
            }

            _gestures?.Dispose();
            _gestures = null;
            _adapter = null;
            if (_ownedAsset != null)
            {
                Destroy(_ownedAsset);
                _ownedAsset = null;
            }

            _wired = false;
            _ = reason;
        }

        static InputActionAsset CreateInputAsset()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionMap map = asset.AddActionMap("MouseKeyboard");
            InputAction move = map.AddAction("Move", InputActionType.Value);
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            InputAction point = map.AddAction("Point", InputActionType.Value, "<Mouse>/position");
            point.expectedControlType = "Vector2";
            InputAction press = map.AddAction("Press", InputActionType.Button, "<Mouse>/leftButton");
            InputAction zoom = map.AddAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y");
            zoom.expectedControlType = "Axis";
            _ = press;
            return asset;
        }
    }
}
