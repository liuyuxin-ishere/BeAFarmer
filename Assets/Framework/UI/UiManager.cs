using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Core;
using Game.Framework.Lua;
using Game.Framework.Resource;
using UnityEngine;
using UnityEngine.UI;
using XLua;

namespace Game.Framework.UI
{
    /// <summary>
    /// UI 注册/栈/开关。钩子由本类单点调用；同 windowId 仅一实例（含 pending）。
    /// Lua 业务控制器经 ILuaUiController；无 C# LoginWindow/HomeWindow 业务类。
    /// </summary>
    public sealed class UiManager : IDisposable
    {
        readonly Dictionary<string, UiWindowRegistration> _registry =
            new Dictionary<string, UiWindowRegistration>(StringComparer.Ordinal);
        readonly Dictionary<string, WindowInstance> _open =
            new Dictionary<string, WindowInstance>(StringComparer.Ordinal);
        readonly HashSet<string> _opening = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, CancellationTokenSource> _openingCts =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        readonly List<string> _stack = new List<string>();
        readonly List<CallbackLease> _pendingOps = new List<CallbackLease>();
        readonly Dictionary<CallbackLease, CancellationTokenRegistration> _ctRegs =
            new Dictionary<CallbackLease, CancellationTokenRegistration>();
        readonly List<ToastEntry> _activeToasts = new List<ToastEntry>();
        // 通用ObjectPool.Clear只丢弃引用；本拥有者必须销毁所有活跃/闲置Unity对象。
        readonly HashSet<GameObject> _ownedToastObjects = new HashSet<GameObject>();

        UiRoot _root;
        ResMgr _resources;
        LuaEnv _luaEnv;
        ObjectPool<GameObject> _toastPool;
        UiTimerDriver _timerDriver;
        int _toastActive;
        int _epoch;
        const int MaxConcurrentToasts = 5;
        bool _disposed;
        bool _closingAll;

        /// <summary>测试可注入控制器工厂；正式路径走 Lua require + Cast。</summary>
        public Func<string, ILuaUiController> ControllerResolver;

        /// <summary>
        /// 测试注入：Prefab路径在取得真实租约后、实例化前挂起；runtime窗在创建前挂起。
        /// 用于同 ID 并发 / CloseAll / Dispose 晚到。
        /// </summary>
        public Func<string, CancellationToken, UniTask> OpenDelayGate;

        public int OpenCount => _open.Count;
        public int StackCount => _stack.Count;

        /// <summary>
        /// 是否存在 modal 输入占用：已打开 modal，或已 admission 进 _opening 的注册 modal
        ///（含 OpenDelayGate 挂起的 pending 异步加载）。pending 非 modal 不挡；
        /// Cancel/CloseAll 清 _opening 后释放门控。
        /// </summary>
        public bool HasModalOpen
        {
            get
            {
                foreach (KeyValuePair<string, WindowInstance> kv in _open)
                {
                    if (kv.Value != null && kv.Value.Modal)
                        return true;
                }

                foreach (string openingId in _opening)
                {
                    if (_registry.TryGetValue(openingId, out UiWindowRegistration reg) && reg.Modal)
                        return true;
                }

                return false;
            }
        }
        public int PendingOpCount => _pendingOps.Count;
        public int ToastActiveCount => _toastActive;
        public int OpeningCount => _opening.Count;
        public UiRoot Root => _root;

        public void Bind(UiRoot root, ResMgr resources, LuaEnv luaEnv)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _resources = resources;
            _luaEnv = luaEnv;
            _timerDriver = UiTimerDriver.Ensure();
            EnsureToastPool();
        }

        /// <summary>注册窗口。重复 ID 抛异常。</summary>
        public void Register(UiWindowRegistration registration)
        {
            ThrowIfDisposed();
            if (registration == null)
                throw new ArgumentNullException(nameof(registration));
            if (string.IsNullOrWhiteSpace(registration.WindowId))
                throw new ArgumentException("windowId 空白");
            if (string.IsNullOrWhiteSpace(registration.ControllerModule))
                throw new ArgumentException("controllerModule 空白");
            if (_registry.ContainsKey(registration.WindowId))
                throw new InvalidOperationException("duplicate-windowId " + registration.WindowId);
            _registry[registration.WindowId] = registration;
        }

        public bool IsRegistered(string windowId) =>
            !string.IsNullOrEmpty(windowId) && _registry.ContainsKey(windowId);

        /// <summary>
        /// 异步打开。同 ID 已打开或 opening 则 Complete(0)；返回可取消租约。
        /// done 主线程至多一次。
        /// </summary>
        public CallbackLease Open(string windowId, string argsJson, LuaCallback done)
        {
            return Open(windowId, argsJson, done, CancellationToken.None);
        }

        /// <summary>带 CancellationToken 的打开（测试/宿主取消 pending 加载）。</summary>
        public CallbackLease Open(string windowId, string argsJson, LuaCallback done, CancellationToken ct)
        {
            ThrowIfDisposed();
            var lease = new CallbackLease(done);
            _pendingOps.Add(lease);
            if (ct.CanBeCanceled)
            {
                var reg = ct.Register(() => PostCancel(lease, "ct"));
                _ctRegs[lease] = reg;
            }

            OpenAsync(windowId, argsJson ?? "", lease, ct).Forget();
            return lease;
        }

        /// <summary>供测试：等待打开结果码。</summary>
        public async UniTask<int> OpenAwait(string windowId, string argsJson, CancellationToken ct)
        {
            var tcs = new UniTaskCompletionSource<int>();
            Open(windowId, argsJson, (code, _) => tcs.TrySetResult(code), ct);
            return await tcs.Task;
        }

        async UniTaskVoid OpenAsync(string windowId, string argsJson, CallbackLease lease, CancellationToken ct)
        {
            ResourceLease<GameObject> prefabLease = null;
            GameObject go = null;
            ViewHost view = null;
            ILuaUiController controller = null;
            bool createHooked = false;
            bool modalPushed = false;
            object modalIdentity = null;
            CancellationTokenSource localCts = null;
            int epoch = _epoch;
            bool claimed = false;

            try
            {
                if (!_registry.TryGetValue(windowId, out UiWindowRegistration reg))
                {
                    FinishOp(lease, 2, "not-registered");
                    return;
                }

                if (_open.ContainsKey(windowId) || !_opening.Add(windowId))
                {
                    FinishOp(lease, 0, _open.ContainsKey(windowId) ? "already-open" : "already-opening");
                    return;
                }

                claimed = true;
                localCts = new CancellationTokenSource();
                _openingCts[windowId] = localCts;
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, localCts.Token))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (IsOpenAborted(epoch, lease))
                    {
                        FinishOp(lease, -1, "cancelled");
                        return;
                    }

                    if (!string.IsNullOrEmpty(reg.PrefabAddress) && _resources != null)
                    {
                        prefabLease = await _resources.LoadAsync<GameObject>(
                            reg.PrefabAddress, "ui:" + windowId, linked.Token);
                        if (OpenDelayGate != null)
                            await OpenDelayGate(windowId, linked.Token);
                        if (IsOpenAborted(epoch, lease))
                        {
                            prefabLease.Dispose();
                            prefabLease = null;
                            FinishOp(lease, -1, "cancelled");
                            return;
                        }

                        go = UnityEngine.Object.Instantiate(prefabLease.Asset, _root.GetLayer(reg.Layer));
                    }
                    else
                    {
                        if (OpenDelayGate != null)
                            await OpenDelayGate(windowId, linked.Token);
                        if (IsOpenAborted(epoch, lease))
                        {
                            FinishOp(lease, -1, "cancelled");
                            return;
                        }
                        go = BuildRuntimeWindow(windowId, reg.Layer);
                    }

                    if (IsOpenAborted(epoch, lease))
                    {
                        AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                            createHooked, modalPushed, modalIdentity);
                        FinishOp(lease, -1, "cancelled");
                        return;
                    }

                    go.name = "Win_" + windowId;
                    view = new ViewHost(go);
                    controller = CreateController(reg.ControllerModule);

                    try
                    {
                        createHooked = true;
                        controller.OnCreate(view);
                    }
                    catch
                    {
                        AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                            createHooked, modalPushed: false, modalIdentity: null);
                        throw;
                    }

                    if (IsOpenAborted(epoch, lease))
                    {
                        AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                            createHooked, modalPushed, modalIdentity);
                        FinishOp(lease, -1, "cancelled");
                        return;
                    }

                    if (reg.Modal)
                    {
                        modalIdentity = go.transform;
                        _root.PushModal(go.transform, modalIdentity);
                        modalPushed = true;
                    }

                    try
                    {
                        controller.OnOpen(argsJson ?? "");
                    }
                    catch
                    {
                        AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                            createHooked, modalPushed, modalIdentity);
                        throw;
                    }

                    if (IsOpenAborted(epoch, lease))
                    {
                        AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                            createHooked, modalPushed, modalIdentity);
                        FinishOp(lease, -1, "cancelled");
                        return;
                    }

                    var created = new WindowInstance
                    {
                        Id = windowId,
                        Reg = reg,
                        View = view,
                        Controller = controller,
                        PrefabLease = prefabLease,
                        Modal = reg.Modal,
                        ModalPushed = modalPushed,
                        ModalIdentity = modalIdentity
                    };
                    prefabLease = null;
                    go = null;
                    view = null;
                    controller = null;
                    modalPushed = false;
                    modalIdentity = null;

                    _open[windowId] = created;
                    _stack.Add(windowId);
                    FinishOp(lease, 0, "opened");
                }
            }
            catch (OperationCanceledException)
            {
                AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                    createHooked, modalPushed, modalIdentity);
                FinishOp(lease, -1, "cancelled");
            }
            catch (Exception ex)
            {
                AbortPartial(ref go, ref view, ref controller, ref prefabLease,
                    createHooked, modalPushed, modalIdentity);
                FinishOp(lease, 3, "open-fail " + ex.Message);
            }
            finally
            {
                if (claimed)
                {
                    _opening.Remove(windowId);
                    if (_openingCts.TryGetValue(windowId, out var cts))
                    {
                        _openingCts.Remove(windowId);
                        try { cts.Dispose(); } catch { }
                    }
                }
            }
        }

        bool IsOpenAborted(int epoch, CallbackLease lease)
        {
            return _disposed || epoch != _epoch || lease == null || lease.IsCompleted;
        }

        void AbortPartial(
            ref GameObject go,
            ref ViewHost view,
            ref ILuaUiController controller,
            ref ResourceLease<GameObject> prefabLease,
            bool createHooked,
            bool modalPushed,
            object modalIdentity)
        {
            if (modalPushed)
            {
                try
                {
                    if (modalIdentity != null)
                        _root?.PopModal(modalIdentity);
                    else
                        _root?.PopModal();
                }
                catch { }
            }

            if (createHooked && controller != null)
            {
                try { controller.OnDestroy(); }
                catch (Exception ex) { Debug.LogWarning("[UiManager] OnDestroy(abort): " + ex.Message); }
            }

            DisposeController(controller);
            controller = null;

            try
            {
                if (view != null) view.Dispose();
                else if (go != null) UnityEngine.Object.Destroy(go);
            }
            catch { }
            view = null;
            go = null;

            try { prefabLease?.Dispose(); }
            catch { }
            prefabLease = null;
        }

        /// <summary>关闭指定窗；done 至多一次。仅关闭字典中仍是该实例的条目（防重入关新开同 ID）。</summary>
        public CallbackLease Close(string windowId, LuaCallback done)
        {
            ThrowIfDisposed();
            var lease = new CallbackLease(done);
            _pendingOps.Add(lease);
            try
            {
                if (string.IsNullOrEmpty(windowId) || !_open.TryGetValue(windowId, out WindowInstance inst))
                {
                    FinishOp(lease, 0, "not-open");
                    return lease;
                }

                CloseInstance(inst);
                FinishOp(lease, 0, "closed");
            }
            catch (Exception ex)
            {
                FinishOp(lease, 3, "close-fail " + ex.Message);
            }

            return lease;
        }

        /// <summary>仅关栈顶可返回窗口。</summary>
        public CallbackLease CloseTop(LuaCallback done)
        {
            ThrowIfDisposed();
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                string id = _stack[i];
                if (_open.TryGetValue(id, out WindowInstance inst) && inst.Reg.CanGoBack)
                    return Close(id, done);
            }

            var lease = new CallbackLease(done);
            _pendingOps.Add(lease);
            FinishOp(lease, 0, "empty-stack");
            return lease;
        }

        /// <summary>推进代次、取消 opening CTS、关窗、清 Toast、取消 pending。</summary>
        public void CloseAll()
        {
            if (_closingAll) return;
            ThrowIfDisposed();
            _closingAll = true;
            try
            {
                _epoch++;
                CancelOpeningLoads();
                var ids = new List<string>(_stack);
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    if (_open.TryGetValue(ids[i], out WindowInstance inst))
                        CloseInstance(inst);
                }

                _stack.Clear();
                _open.Clear();
                _root?.ResetModal();
                ClearActiveToasts(returnToPool: true);
                CancelPending("closeAll");
            }
            finally { _closingAll = false; }
        }

        /// <summary>
        /// Toast：非缩放计时后归还对象池。CloseAll/Dispose 取消计时并清理，之后不再回调。
        /// durationSeconds 必须 &gt;0。
        /// </summary>
        public void ShowToast(string message, float durationSeconds)
        {
            ThrowIfDisposed();
            if (durationSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Toast duration 必须 > 0");
            if (_toastActive >= MaxConcurrentToasts)
                return;

            EnsureToastPool();
            GameObject go = _toastPool.Rent();
            go.transform.SetParent(_root.GetLayer(UILayer.Toast), false);
            go.SetActive(true);
            var text = go.GetComponentInChildren<Text>();
            if (text != null)
                text.text = message ?? "";
            _toastActive++;

            var entry = new ToastEntry { Go = go };
            entry.Handle = _timerDriver.Timers.Schedule(
                TimeSpan.FromSeconds(durationSeconds),
                () => CompleteToast(entry),
                false);
            _activeToasts.Add(entry);
        }

        void CompleteToast(ToastEntry entry)
        {
            if (entry == null || entry.Finished)
                return;
            entry.Finished = true;
            _activeToasts.Remove(entry);
            try
            {
                if (entry.Go != null && _toastPool != null && !_disposed)
                {
                    entry.Go.SetActive(false);
                    _toastPool.Return(entry.Go);
                }
                else if (entry.Go != null)
                {
                    UnityEngine.Object.Destroy(entry.Go);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UiManager] Toast return: " + ex.Message);
            }
            finally
            {
                _toastActive = Math.Max(0, _toastActive - 1);
            }
        }

        void ClearActiveToasts(bool returnToPool)
        {
            var copy = new List<ToastEntry>(_activeToasts);
            _activeToasts.Clear();
            for (int i = 0; i < copy.Count; i++)
            {
                ToastEntry entry = copy[i];
                if (entry == null || entry.Finished)
                    continue;
                entry.Finished = true;
                try { _timerDriver?.Timers.Cancel(entry.Handle); }
                catch { }

                try
                {
                    if (entry.Go != null)
                    {
                        if (returnToPool && _toastPool != null && !_disposed)
                        {
                            entry.Go.SetActive(false);
                            _toastPool.Return(entry.Go);
                        }
                        else
                        {
                            UnityEngine.Object.Destroy(entry.Go);
                        }
                    }
                }
                catch { }
            }

            _toastActive = 0;
        }

        /// <summary>
        /// Dialog 确认。确认 true(code=1)/取消 false(code=0)；外部 Close 也结束等待。
        /// </summary>
        public CallbackLease Confirm(string title, string message, LuaCallback done)
        {
            ThrowIfDisposed();
            var lease = new CallbackLease(done);
            _pendingOps.Add(lease);
            const string dialogId = "__dialog";
            if (!_registry.ContainsKey(dialogId))
            {
                Register(new UiWindowRegistration
                {
                    WindowId = dialogId,
                    PrefabAddress = "",
                    Layer = UILayer.Popup,
                    ControllerModule = "__runtime_dialog",
                    Modal = true,
                    CanGoBack = true
                });
            }

            if (_open.ContainsKey(dialogId) || _opening.Contains(dialogId))
            {
                FinishOp(lease, 0, "dialog-busy-false");
                return lease;
            }

            try
            {
                GameObject go = BuildRuntimeDialog(title, message, () =>
                {
                    FinishDialog(dialogId, lease, 1, "true");
                }, () =>
                {
                    FinishDialog(dialogId, lease, 0, "false");
                });
                go.name = "Win_Dialog";

                var view = new ViewHost(go);
                object modalIdentity = go.transform;
                var inst = new WindowInstance
                {
                    Id = dialogId,
                    Reg = _registry[dialogId],
                    View = view,
                    Controller = new RuntimeNopController(),
                    Modal = true,
                    ModalPushed = true,
                    ModalIdentity = modalIdentity,
                    DialogLease = lease
                };
                _root.PushModal(go.transform, modalIdentity);
                _open[dialogId] = inst;
                _stack.Add(dialogId);
            }
            catch (Exception ex)
            {
                FinishOp(lease, 3, "dialog-fail " + ex.Message);
            }

            return lease;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            // 在调用任意Lua/用户钩子前关门，防止同步Open产生不在清理快照内的新窗口。
            _disposed = true;
            _epoch++;
            CancelOpeningLoads();
            try
            {
                var ids = new List<string>(_stack);
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    if (_open.TryGetValue(ids[i], out WindowInstance inst))
                        CloseInstance(inst);
                }

                _stack.Clear();
                _open.Clear();
                _root?.ResetModal();
                ClearActiveToasts(returnToPool: false);
                CancelPending("dispose");
                _toastPool?.Clear();
            }
            finally
            {
                foreach (GameObject toast in _ownedToastObjects)
                    if (toast != null) UnityEngine.Object.Destroy(toast);
                _ownedToastObjects.Clear();
                _toastPool?.Clear();
                UiBridge.ClearManager(this);
            }
        }

        ILuaUiController CreateController(string module)
        {
            if (module == "__runtime_dialog" || module == "__runtime_nop")
                return new RuntimeNopController();
            if (ControllerResolver != null)
                return ControllerResolver(module) ?? throw new InvalidOperationException("resolver-null " + module);
            if (_luaEnv == null)
                throw new InvalidOperationException("LuaEnv 未绑定，无法 require " + module);

            string safe = (module ?? "").Replace("'", "").Replace("\\", "");
            object[] ret = _luaEnv.DoString("return require('" + safe + "')", "ui-require");
            if (ret == null || ret.Length == 0 || ret[0] == null)
                throw new InvalidOperationException("controller-require-null " + module);

            if (!(ret[0] is LuaTable moduleTable))
                throw new InvalidOperationException("controller-not-LuaTable " + module);

            LuaTable instanceTable = null;
            LuaFunction newFn = null;
            ILuaUiController bridge = null;
            try
            {
                moduleTable.Get("new", out newFn);
                if (newFn != null)
                {
                    object[] created = newFn.Call();
                    if (created == null || created.Length == 0 || !(created[0] is LuaTable))
                        throw new InvalidOperationException("controller-new-null " + module);
                    instanceTable = (LuaTable)created[0];
                }
                else
                {
                    instanceTable = moduleTable;
                    moduleTable = null;
                }

                bridge = instanceTable.Cast<ILuaUiController>();
                if (bridge == null)
                    throw new InvalidOperationException("controller-cast-null " + module);

                var owned = new OwnedLuaUiController(bridge);
                bridge = null;
                return owned;
            }
            finally
            {
                try { newFn?.Dispose(); } catch { }
                if (instanceTable != null && !ReferenceEquals(instanceTable, moduleTable))
                {
                    try { instanceTable.Dispose(); } catch { }
                }

                try { moduleTable?.Dispose(); } catch { }
                if (bridge is IDisposable d)
                {
                    try { d.Dispose(); } catch { }
                }
            }
        }

        static void DisposeController(ILuaUiController controller)
        {
            if (controller is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }
        }

        void CloseInstance(WindowInstance inst)
        {
            if (inst == null)
                return;
            if (!_open.TryGetValue(inst.Id, out WindowInstance cur) || !ReferenceEquals(cur, inst))
                return;

            _stack.Remove(inst.Id);
            _open.Remove(inst.Id);
            try
            {
                inst.Controller?.OnClose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UiManager] OnClose: " + ex.Message);
            }

            DestroyInstance(inst, invokeHooks: true);
        }

        void DestroyInstance(WindowInstance inst, bool invokeHooks)
        {
            if (inst == null)
                return;
            if (inst.ModalPushed)
            {
                try
                {
                    if (inst.ModalIdentity != null)
                        _root?.PopModal(inst.ModalIdentity);
                    else
                        _root?.PopModal();
                }
                catch { }
                inst.ModalPushed = false;
            }
            if (invokeHooks)
            {
                try
                {
                    inst.Controller?.OnDestroy();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[UiManager] OnDestroy: " + ex.Message);
                }
            }

            DisposeController(inst.Controller);
            inst.Controller = null;

            try
            {
                inst.View?.Dispose();
            }
            catch
            {
            }

            try
            {
                inst.PrefabLease?.Dispose();
            }
            catch
            {
            }

            if (inst.DialogLease != null)
                inst.DialogLease.Complete(0, "external-close");
        }

        void FinishOp(CallbackLease lease, int code, string payload)
        {
            if (lease == null)
                return;
            ReleaseCtRegistration(lease);
            _pendingOps.Remove(lease);
            try
            {
                lease.Complete(code, payload);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UiManager] done callback: " + ex.Message);
            }
        }

        void ReleaseCtRegistration(CallbackLease lease)
        {
            if (lease == null)
                return;
            if (_ctRegs.TryGetValue(lease, out var reg))
            {
                _ctRegs.Remove(lease);
                try { reg.Dispose(); } catch { }
            }
        }

        /// <summary>先 Complete 再关窗，避免 DestroyInstance 用 external-close 覆盖确认结果。</summary>
        void FinishDialog(string dialogId, CallbackLease lease, int code, string payload)
        {
            if (_open.TryGetValue(dialogId, out WindowInstance inst))
                inst.DialogLease = null;
            FinishOp(lease, code, payload);
            if (_open.TryGetValue(dialogId, out WindowInstance still) && still.DialogLease == null)
                CloseInstance(still);
        }

        void CancelPending(string reason)
        {
            var copy = new List<CallbackLease>(_pendingOps);
            _pendingOps.Clear();
            for (int i = 0; i < copy.Count; i++)
            {
                ReleaseCtRegistration(copy[i]);
                try { copy[i]?.Cancel(reason); }
                catch { }
            }
        }

        void CancelOpeningLoads()
        {
            var keys = new List<string>(_openingCts.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                if (_openingCts.TryGetValue(keys[i], out var cts))
                {
                    try { cts.Cancel(); } catch { }
                }
            }
        }

        void PostCancel(CallbackLease lease, string reason)
        {
            void Do() => FinishOp(lease, -1, reason);

            if (PlayerLoopHelper.IsMainThread)
                Do();
            else
                UniTask.Post(Do);
        }

        void EnsureToastPool()
        {
            if (_toastPool != null)
                return;
            _toastPool = new ObjectPool<GameObject>(
                () => BuildToastItem(),
                go =>
                {
                    if (go != null)
                    {
                        go.SetActive(false);
                        go.transform.SetParent(null, false);
                    }
                },
                MaxConcurrentToasts);
        }

        GameObject BuildToastItem()
        {
            var go = new GameObject("ToastItem", typeof(RectTransform), typeof(Image));
            _ownedToastObjects.Add(go);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(640, 72);
            go.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.12f, 0.9f);
            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(12, 8);
            trt.offsetMax = new Vector2(-12, -8);
            var text = textGo.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = 28;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            go.SetActive(false);
            return go;
        }

        GameObject BuildRuntimeWindow(string windowId, UILayer layer)
        {
            Transform layerTf = _root.GetLayer(layer);
            // T026/T035/T041：缺 Prefab 或无 ResMgr 时走正式布局 builder（非测试桩）。
            if (string.Equals(windowId, "Bag", System.StringComparison.Ordinal))
                return M2U01WindowBuilder.BuildBag(layerTf);
            if (string.Equals(windowId, "Shop", System.StringComparison.Ordinal))
                return M2U01WindowBuilder.BuildShop(layerTf);
            if (string.Equals(windowId, "Farm", System.StringComparison.Ordinal))
                return M2U01WindowBuilder.BuildFarm(layerTf);
            if (string.Equals(windowId, "Gather", System.StringComparison.Ordinal))
                return M2U01WindowBuilder.BuildGather(layerTf);
            if (string.Equals(windowId, "Tool", System.StringComparison.Ordinal))
                return M2U01WindowBuilder.BuildTool(layerTf);
            if (string.Equals(windowId, "HomeHud", System.StringComparison.Ordinal))
                return M2U01WindowBuilder.BuildHomeHud(layerTf);

            var go = new GameObject("RuntimeWin", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(layerTf, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.2f, 0.25f);
            rt.anchorMax = new Vector2(0.8f, 0.75f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = new Color(0.15f, 0.18f, 0.22f, 0.96f);

            CreateText(go.transform, "Title", new Vector2(0.05f, 0.75f), new Vector2(0.95f, 0.95f), windowId, 32);
            CreateText(go.transform, "Body", new Vector2(0.05f, 0.35f), new Vector2(0.95f, 0.7f), "body", 24);
            CreateButton(go.transform, "OkButton", new Vector2(0.3f, 0.08f), new Vector2(0.7f, 0.28f), "OK");
            return go;
        }

        GameObject BuildRuntimeDialog(string title, string message, Action onYes, Action onNo)
        {
            var go = BuildRuntimeWindow("Dialog", UILayer.Popup);
            var titleT = go.transform.Find("Title");
            if (titleT != null)
                titleT.GetComponent<Text>().text = title ?? "";
            var bodyT = go.transform.Find("Body");
            if (bodyT != null)
                bodyT.GetComponent<Text>().text = message ?? "";

            var ok = go.transform.Find("OkButton")?.GetComponent<Button>();
            if (ok != null)
            {
                ok.GetComponentInChildren<Text>().text = "确认";
                ok.onClick.AddListener(() => onYes?.Invoke());
            }

            var cancelGo = CreateButton(go.transform, "CancelButton",
                new Vector2(0.05f, 0.08f), new Vector2(0.28f, 0.28f), "取消");
            cancelGo.GetComponent<Button>().onClick.AddListener(() => onNo?.Invoke());
            return go;
        }

        static Text CreateText(Transform parent, string name, Vector2 aMin, Vector2 aMax, string value, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var text = go.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = value ?? "";
            return text;
        }

        static GameObject CreateButton(Transform parent, string name, Vector2 aMin, Vector2 aMax, string label)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = new Color(0.25f, 0.55f, 0.35f, 1f);
            CreateText(go.transform, "Text", Vector2.zero, Vector2.one, label, 26);
            return go;
        }

        static Font BuiltinFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UiManager));
            if (_closingAll)
                throw new InvalidOperationException("UI正在关闭全部窗口，不能重入新的操作");
        }

        sealed class ToastEntry
        {
            public GameObject Go;
            public TimerHandle Handle;
            public bool Finished;
        }

        sealed class WindowInstance
        {
            public string Id;
            public UiWindowRegistration Reg;
            public ViewHost View;
            public ILuaUiController Controller;
            public ResourceLease<GameObject> PrefabLease;
            public bool Modal;
            public bool ModalPushed;
            public object ModalIdentity;
            public CallbackLease DialogLease;
        }

        /// <summary>持有 Cast 产生的 interface bridge；关窗时 Dispose，不靠 GC。</summary>
        sealed class OwnedLuaUiController : ILuaUiController, IDisposable
        {
            ILuaUiController _inner;
            bool _disposed;

            public OwnedLuaUiController(ILuaUiController inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public void OnCreate(ViewHost view) => _inner.OnCreate(view);
            public void OnOpen(string argsJson) => _inner.OnOpen(argsJson);
            public void OnClose() => _inner.OnClose();
            public void OnDestroy() => _inner.OnDestroy();

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (_inner is IDisposable d)
                {
                    try { d.Dispose(); } catch { }
                }

                _inner = null;
            }
        }

        sealed class RuntimeNopController : ILuaUiController
        {
            public void OnCreate(ViewHost view) { }
            public void OnOpen(string argsJson) { }
            public void OnClose() { }
            public void OnDestroy() { }
        }
    }
}
