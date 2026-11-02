using System;
using System.Globalization;
using XLua;

namespace Game.Framework.Home
{
    /// <summary>
    /// Lua 可见家园农场门面：输入门控、手势回调、地块/采集点显示推送。不发网络、不改 Model。
    /// T041：Node 选择回调与 ApplyNodeDisplays；公开面变化需 Root GenAll + 新基包，非纯 Lua 补丁。
    /// </summary>
    public static class LuaHomeFarmFacade
    {
        static HomeFarmRuntime _runtime;
        static HomeFarmGestureHandler _gestureHandler;
        static HomeFarmCancelHandler _cancelHandler;
        static HomeFarmDisplayTickHandler _tickHandler;
        static HomeFarmDisplayTickHandler _nodeTickHandler;
        static HomeFarmDisplayTickHandler _recoveryPollHandler;
        static HomeFarmNodeSelectHandler _nodeSelectHandler;
        static long _attachEpoch;
        static long _nodeAttachEpoch;
        static long _nodeTickEpoch;
        static long _recoveryPollEpoch;
        /// <summary>Farm / Gather 各自按 epoch 持有共享世界输入；全关才禁用 Adapter。</summary>
        static bool _explicitInputEnabled = true;
        static bool _farmInputHeld;
        static bool _gatherInputHeld;
        static long _farmInputEpoch;
        static long _gatherInputEpoch;

        [CSharpCallLua]
        public delegate void HomeFarmGestureHandler(string toolMode, string plotIdsJson, bool previewOnly);

        [CSharpCallLua]
        public delegate void HomeFarmCancelHandler(string reason);

        [CSharpCallLua]
        public delegate void HomeFarmDisplayTickHandler();

        [CSharpCallLua]
        public delegate void HomeFarmNodeSelectHandler(int nodeInstanceId);

        internal static void BindRuntime(HomeFarmRuntime runtime)
        {
            if (_runtime != null && !ReferenceEquals(_runtime, runtime))
                UnbindRuntime(_runtime);
            _runtime = runtime;
            // 重建不继承旧 lease：无消费者时禁用，避免 Adapter 默认 true 常开
            ApplySharedInputGate();
        }

        internal static void UnbindRuntime(HomeFarmRuntime runtime)
        {
            // 已被替换的旧 runtime 无权清理当前场景的回调或输入消费者。
            if (!ReferenceEquals(_runtime, runtime))
                return;
            _runtime = null;
            // 场景销毁：抬升 epoch，旧 Lua 回调不得再驱动已销毁 runtime；输入租约作废
            _attachEpoch++;
            _nodeAttachEpoch++;
            _gestureHandler = null;
            _cancelHandler = null;
            _tickHandler = null;
            _nodeTickHandler = null;
            _recoveryPollHandler = null;
            _nodeSelectHandler = null;
            _nodeTickEpoch++;
            _recoveryPollEpoch++;
            _explicitInputEnabled = true;
            _farmInputHeld = false;
            _gatherInputHeld = false;
            _farmInputEpoch++;
            _gatherInputEpoch++;
        }

        /// <summary>FarmPresenter OnOpen 登记；OnClose/OnDestroy 必须 Detach。同时按 epoch 持有 Farm 输入消费者。</summary>
        public static long AttachHandlers(
            HomeFarmGestureHandler onGesture,
            HomeFarmCancelHandler onCancel,
            HomeFarmDisplayTickHandler onTick)
        {
            _gestureHandler = onGesture;
            _cancelHandler = onCancel;
            _tickHandler = onTick;
            _attachEpoch++;
            _farmInputHeld = true;
            _farmInputEpoch = _attachEpoch;
            ApplySharedInputGate();
            return _attachEpoch;
        }

        public static void DetachHandlers(long epoch)
        {
            if (epoch != _attachEpoch)
                return;
            _gestureHandler = null;
            _cancelHandler = null;
            _tickHandler = null;
            if (_farmInputHeld && epoch == _farmInputEpoch)
                _farmInputHeld = false;
            _attachEpoch++;
            ApplySharedInputGate();
        }

        /// <summary>GatherPresenter OnOpen 登记节点选择；关窗 Detach，不取消全局已受理事务。同时按 epoch 持有 Gather 输入消费者。</summary>
        public static long AttachNodeSelectHandler(HomeFarmNodeSelectHandler onNodeSelect)
        {
            _nodeSelectHandler = onNodeSelect;
            _nodeAttachEpoch++;
            _gatherInputHeld = true;
            _gatherInputEpoch = _nodeAttachEpoch;
            ApplySharedInputGate();
            return _nodeAttachEpoch;
        }

        public static void DetachNodeSelectHandler(long epoch)
        {
            if (epoch != _nodeAttachEpoch)
                return;
            _nodeSelectHandler = null;
            if (_gatherInputHeld && epoch == _gatherInputEpoch)
                _gatherInputHeld = false;
            _nodeAttachEpoch++;
            ApplySharedInputGate();
        }

        /// <summary>
        /// Gather 独立 C09 显示 tick；不覆盖 Farm AttachHandlers 的 tick。
        /// 关窗 DetachNodeDisplayTick，退订后无幽灵刷新。
        /// </summary>
        public static long AttachNodeDisplayTick(HomeFarmDisplayTickHandler onTick)
        {
            _nodeTickHandler = onTick;
            _nodeTickEpoch++;
            return _nodeTickEpoch;
        }

        public static void DetachNodeDisplayTick(long epoch)
        {
            if (epoch != _nodeTickEpoch)
                return;
            _nodeTickHandler = null;
            _nodeTickEpoch++;
        }

        /// <summary>
        /// Tool 独立恢复轮询；不覆盖 Farm AttachHandlers 或 Gather AttachNodeDisplayTick。
        /// 关窗 DetachRecoveryPoll，退订后无幽灵刷新；不伪造已提交。
        /// </summary>
        public static long AttachRecoveryPoll(HomeFarmDisplayTickHandler onPoll)
        {
            _recoveryPollHandler = onPoll;
            _recoveryPollEpoch++;
            return _recoveryPollEpoch;
        }

        public static void DetachRecoveryPoll(long epoch)
        {
            if (epoch != _recoveryPollEpoch)
                return;
            _recoveryPollHandler = null;
            _recoveryPollEpoch++;
        }

        /// <summary>
        /// 显式输入门闸保留原契约：false 必须禁用，且不消费 Farm/Gather 的 epoch。
        /// 窗口关闭只 Detach 本窗；重新 true 后按仍存在的消费者恢复。
        /// </summary>
        public static void SetInputEnabled(bool enabled)
        {
            _explicitInputEnabled = enabled;
            if (_farmInputHeld || _gatherInputHeld)
                ApplySharedInputGate();
            else
                ApplyDirectInputEnabled(enabled); // 保留旧无租约装配调用的显式控制。
        }

        static void ApplySharedInputGate()
        {
            ApplyDirectInputEnabled(_explicitInputEnabled && (_farmInputHeld || _gatherInputHeld));
        }

        static void ApplyDirectInputEnabled(bool enabled)
        {
            if (_runtime != null && _runtime.Gestures != null)
                _runtime.Gestures.SetInputEnabled(enabled);
            else if (_runtime != null && _runtime.Adapter != null)
                _runtime.Adapter.SetInputEnabled(enabled);
        }

        public static void SetToolMode(string mode)
        {
            _runtime?.Gestures?.SetToolMode(mode);
        }

        public static void CancelActiveGesture(string reason)
        {
            _runtime?.Gestures?.CancelActive(reason ?? "lua-cancel");
        }

        public static bool IsRuntimeReady()
        {
            return _runtime != null && _runtime.IsReady;
        }

        /// <summary>幂等装配家园灰盒/输入；供 Lua 窗打开时调用。</summary>
        public static void EnsureRuntime()
        {
            HomeFarmRuntime.EnsureInActiveScene();
        }

        /// <summary>测试/Lua：屏幕点解析稳定 plot instanceId；失败返回 0。</summary>
        public static int TryResolvePlotAtScreen(float x, float y)
        {
            if (_runtime == null || _runtime.Adapter == null)
                return 0;
            if (!_runtime.Adapter.TryResolveScreenPoint(new UnityEngine.Vector2(x, y), out HomeTarget t))
                return 0;
            return t.IsPlot ? t.InstanceId : 0;
        }

        /// <summary>测试/Lua：屏幕点解析稳定 node instanceId；失败返回 0。</summary>
        public static int TryResolveNodeAtScreen(float x, float y)
        {
            if (_runtime == null || _runtime.Adapter == null)
                return 0;
            if (!_runtime.Adapter.TryResolveScreenPoint(new UnityEngine.Vector2(x, y), out HomeTarget t))
                return 0;
            return t.IsNode ? t.InstanceId : 0;
        }

        /// <summary>
        /// 只读观测：地块屏幕坐标（Unity 像素，原点左下）。成功返回 "x,y"；失败返回空串。
        /// </summary>
        public static string TryGetPlotScreenPoint(int plotId)
        {
            if (_runtime == null || !_runtime.TryGetPlotScreenPoint(plotId, out float x, out float y))
                return "";
            return x.ToString("F2", CultureInfo.InvariantCulture)
                   + "," + y.ToString("F2", CultureInfo.InvariantCulture);
        }

        /// <summary>只读观测：采集点屏幕坐标。成功 "x,y"；失败空串。</summary>
        public static string TryGetNodeScreenPoint(int nodeInstanceId)
        {
            if (_runtime == null || !_runtime.TryGetNodeScreenPoint(nodeInstanceId, out float x, out float y))
                return "";
            return x.ToString("F2", CultureInfo.InvariantCulture)
                   + "," + y.ToString("F2", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 推送地块显示。payload 形如 "1001:2;1002:3"（id:kindInt）。
        /// 倒计时到 0 只反映 Ripe，不触发收获。
        /// </summary>
        public static void ApplyPlotDisplays(string payload)
        {
            if (_runtime == null || string.IsNullOrEmpty(payload))
                return;
            string[] parts = payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                int colon = p.IndexOf(':');
                if (colon <= 0)
                    continue;
                if (!int.TryParse(p.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    continue;
                if (!int.TryParse(p.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kind))
                    continue;
                if (kind < 0 || kind > (int)HomePlotDisplayKind.Unknown)
                    continue;
                _runtime.SetPlotDisplay(id, (HomePlotDisplayKind)kind);
            }
        }

        /// <summary>
        /// 推送采集点显示。payload "2001:1;2002:2"（instanceId:kindInt）。
        /// 冷却到 0 只反映 Ready，不自动采集/发奖。
        /// </summary>
        public static void ApplyNodeDisplays(string payload)
        {
            if (_runtime == null || string.IsNullOrEmpty(payload))
                return;
            string[] parts = payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                int colon = p.IndexOf(':');
                if (colon <= 0)
                    continue;
                if (!int.TryParse(p.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    continue;
                if (!int.TryParse(p.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kind))
                    continue;
                if (kind < 0 || kind > (int)HomeNodeDisplayKind.Unknown)
                    continue;
                _runtime.SetNodeDisplay(id, (HomeNodeDisplayKind)kind);
            }
        }

        internal static void RaiseGesture(string mode, string plotIdsJson, bool previewOnly)
        {
            HomeFarmGestureHandler h = _gestureHandler;
            if (h == null)
                return;
            try { h(mode, plotIdsJson ?? "[]", previewOnly); }
            catch (Exception) { /* 观察者异常不回灌输入 */ }
        }

        internal static void RaiseGestureCancelled(string reason)
        {
            HomeFarmCancelHandler h = _cancelHandler;
            if (h == null)
                return;
            try { h(reason ?? ""); }
            catch (Exception) { }
        }

        internal static void RaiseDisplayTick()
        {
            HomeFarmDisplayTickHandler farmTick = _tickHandler;
            if (farmTick != null)
            {
                try { farmTick(); }
                catch (Exception) { /* 观察者异常不回灌 */ }
            }

            HomeFarmDisplayTickHandler nodeTick = _nodeTickHandler;
            if (nodeTick != null)
            {
                try { nodeTick(); }
                catch (Exception) { }
            }

            HomeFarmDisplayTickHandler recoveryPoll = _recoveryPollHandler;
            if (recoveryPoll != null)
            {
                try { recoveryPoll(); }
                catch (Exception) { }
            }
        }

        internal static void RaiseNodeSelect(int nodeInstanceId)
        {
            HomeFarmNodeSelectHandler h = _nodeSelectHandler;
            if (h == null)
                return;
            try { h(nodeInstanceId); }
            catch (Exception) { }
        }
    }
}
