using System;
using Game.Framework.Lua;
using Game.Framework.Resource;
using Game.Framework.Scenes;
using XLua;

namespace Game.Framework.UI
{
    /// <summary>
    /// Lua 可见的 UI/场景固定桥。属 m1-bridge-0.3；业务窗口逻辑仍在 Lua 模块。
    /// </summary>
    public static class UiBridge
    {
        static UiManager _manager;
        static SceneService _scenes;

        public static UiManager Manager => _manager;
        public static SceneService Scenes => _scenes;

        /// <summary>绑定常驻框架。resources 可为 null（仅用运行时窗）。</summary>
        public static UiManager EnsureManager(ResMgr resources, LuaEnv luaEnv)
        {
            if (_manager != null)
            {
                _manager.Bind(UiRoot.Ensure(), resources, luaEnv);
                return _manager;
            }

            _manager = new UiManager();
            _manager.Bind(UiRoot.Ensure(), resources, luaEnv);
            return _manager;
        }

        public static void ClearManager(UiManager manager)
        {
            if (ReferenceEquals(_manager, manager))
                _manager = null;
        }

        public static void BindSceneService(SceneService scenes) => _scenes = scenes;

        public static void Register(
            string windowId,
            string prefabAddress,
            int layer,
            string controllerModule,
            bool modal,
            bool canGoBack)
        {
            EnsureMgr().Register(new UiWindowRegistration
            {
                WindowId = windowId,
                PrefabAddress = prefabAddress ?? "",
                Layer = (UILayer)layer,
                ControllerModule = controllerModule,
                Modal = modal,
                CanGoBack = canGoBack
            });
        }

        public static CallbackLease Open(string windowId, string argsJson, LuaCallback done) =>
            EnsureMgr().Open(windowId, argsJson, done);

        public static CallbackLease Close(string windowId, LuaCallback done) =>
            EnsureMgr().Close(windowId, done);

        public static CallbackLease CloseTop(LuaCallback done) => EnsureMgr().CloseTop(done);

        public static void CloseAll() => EnsureMgr().CloseAll();

        public static void ShowToast(string message, float durationSeconds) =>
            EnsureMgr().ShowToast(message, durationSeconds);

        public static CallbackLease Confirm(string title, string message, LuaCallback done) =>
            EnsureMgr().Confirm(title, message, done);

        public static int OpenCount => _manager?.OpenCount ?? 0;
        public static int StackCount => _manager?.StackCount ?? 0;

        /// <summary>家园世界输入门控：任一 modal 打开时禁止透传点击/拖拽写。</summary>
        public static bool HasModalOpen => _manager != null && _manager.HasModalOpen;
        public static int LiveClicks => ClickSubscription.LiveCount;
        public static int LiveCallbackLeases => CallbackLease.LiveCount;

        public static void ApplySafeArea(float x, float y, float w, float h, int screenW, int screenH)
        {
            var root = UiRoot.Ensure();
            root.SafeArea.Apply(new UnityEngine.Rect(x, y, w, h), new UnityEngine.Vector2Int(screenW, screenH));
        }

        public static float SafeAnchorMinX() => UiRoot.Ensure().SafeArea.AnchorMin.x;
        public static float SafeAnchorMinY() => UiRoot.Ensure().SafeArea.AnchorMin.y;
        public static float SafeAnchorMaxX() => UiRoot.Ensure().SafeArea.AnchorMax.x;
        public static float SafeAnchorMaxY() => UiRoot.Ensure().SafeArea.AnchorMax.y;

        static UiManager EnsureMgr()
        {
            if (_manager == null)
                throw new InvalidOperationException("UiBridge: call EnsureManager first");
            return _manager;
        }
    }
}
