using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using XLua;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>T48–T51：窗口生命周期、拥有权、真实 Lua、Dialog/Toast/模态。</summary>
    public sealed class UiLifecycleTests
    {
        UiManager _ui;
        int _baselineClicks;
        int _baselineLeases;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _baselineClicks = ClickSubscription.LiveCount;
            _baselineLeases = CallbackLease.LiveCount;
            var root = UiRoot.Ensure();
            _ui = new UiManager();
            _ui.Bind(root, null, null);
            _ui.ControllerResolver = module => new TrackingController();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _ui?.Dispose();
            _ui = null;
            yield return null;
            Assert.That(ClickSubscription.LiveCount, Is.EqualTo(_baselineClicks));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
        }

        [UnityTest]
        public IEnumerator Register_DuplicateId_Rejected()
        {
            _ui.Register(Reg("w1"));
            Assert.Throws<InvalidOperationException>(() => _ui.Register(Reg("w1")));
            yield return null;
        }

        [UnityTest]
        public IEnumerator OpenClose_ReturnsBaseline_TwentyTimes()
        {
            _ui.Register(Reg("loop"));
            for (int i = 0; i < 20; i++)
            {
                int code = -99;
                yield return _ui.OpenAwait("loop", "a=" + i, CancellationToken.None)
                    .ToCoroutine(r => code = r);
                Assert.That(code, Is.EqualTo(0));
                Assert.That(_ui.OpenCount, Is.EqualTo(1));
                bool closed = false;
                _ui.Close("loop", (c, _) => closed = c == 0);
                yield return null;
                Assert.That(closed, Is.True);
                Assert.That(_ui.OpenCount, Is.EqualTo(0));
            }
        }

        [UnityTest]
        public IEnumerator DoubleOpen_SameId_SingleInstance()
        {
            _ui.Register(Reg("once"));
            yield return _ui.OpenAwait("once", "", CancellationToken.None).ToCoroutine();
            int second = -1;
            yield return _ui.OpenAwait("once", "", CancellationToken.None).ToCoroutine(r => second = r);
            Assert.That(second, Is.EqualTo(0));
            Assert.That(_ui.OpenCount, Is.EqualTo(1));
            Assert.That(_ui.StackCount, Is.EqualTo(1));
            _ui.CloseAll();
        }

        [UnityTest]
        public IEnumerator CloseTop_OnlyTopReturnable()
        {
            _ui.Register(Reg("a"));
            _ui.Register(new UiWindowRegistration
            {
                WindowId = "b",
                PrefabAddress = "",
                Layer = UILayer.Window,
                ControllerModule = "track",
                CanGoBack = true
            });
            yield return _ui.OpenAwait("a", "", CancellationToken.None).ToCoroutine();
            yield return _ui.OpenAwait("b", "", CancellationToken.None).ToCoroutine();
            bool done = false;
            _ui.CloseTop((c, _) => done = c == 0);
            yield return null;
            Assert.That(done, Is.True);
            Assert.That(_ui.OpenCount, Is.EqualTo(1));
            Assert.That(GameObject.Find("Win_b"), Is.Null);
            Assert.That(GameObject.Find("Win_a"), Is.Not.Null);
            _ui.CloseAll();
        }

        [UnityTest]
        public IEnumerator Modal_BlocksRaycast_WhenOpen()
        {
            _ui.Register(new UiWindowRegistration
            {
                WindowId = "modal",
                PrefabAddress = "",
                Layer = UILayer.Popup,
                ControllerModule = "track",
                Modal = true
            });
            yield return _ui.OpenAwait("modal", "", CancellationToken.None).ToCoroutine();
            Assert.That(_ui.Root.ModalDepth, Is.GreaterThan(0));
            var blocker = GameObject.Find("ModalBlocker");
            Assert.That(blocker, Is.Not.Null);
            Assert.That(blocker.activeSelf, Is.True);
            Assert.That(blocker.GetComponent<Image>().raycastTarget, Is.True);
            _ui.CloseAll();
            Assert.That(_ui.Root.ModalDepth, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Dialog_Confirm_EventSystemHitsOk_NotCoveredByBlocker()
        {
            int code = -9;
            _ui.Confirm("T", "M", (c, _) => code = c);
            yield return null;
            Assert.That(_ui.OpenCount, Is.EqualTo(1));

            var ok = GameObject.Find("OkButton");
            var blocker = GameObject.Find("ModalBlocker");
            var win = GameObject.Find("Win_Dialog");
            Assert.That(ok, Is.Not.Null);
            Assert.That(blocker, Is.Not.Null);
            Assert.That(win, Is.Not.Null);
            Assert.That(win.transform.GetSiblingIndex(), Is.GreaterThan(blocker.transform.GetSiblingIndex()),
                "ModalBlocker 须在 Dialog 下方，不能盖住确认按钮");

            var es = EventSystem.current ?? UnityEngine.Object.FindObjectOfType<EventSystem>();
            Assert.That(es, Is.Not.Null);
            var canvas = ok.GetComponentInParent<Canvas>();
            Assert.That(canvas, Is.Not.Null);
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            Assert.That(raycaster, Is.Not.Null);
            var cam = canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, ok.transform.position);
            var ped = new PointerEventData(es) { position = screen };
            var results = new List<RaycastResult>();
            raycaster.Raycast(ped, results);
            if (results.Count == 0)
                es.RaycastAll(ped, results);
            Assert.That(results.Count, Is.GreaterThan(0), "须有 UI 射线命中");
            Transform hit = results[0].gameObject.transform;
            bool hitOk = hit == ok.transform || hit.IsChildOf(ok.transform);
            Assert.That(hitOk, Is.True, "首击应为确认按钮，实际=" + results[0].gameObject.name);
            Assert.That(results[0].gameObject.name, Is.Not.EqualTo("ModalBlocker"));

            // 不用 Button.onClick.Invoke；走 ExecuteEvents 证明可交互目标是按钮
            ExecuteEvents.Execute(ok, ped, ExecuteEvents.pointerClickHandler);
            yield return null;
            Assert.That(code, Is.EqualTo(1), "EventSystem 点击确认应生效；blocker 不得盖住按钮");
            Assert.That(_ui.OpenCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Dialog_ExternalCloseEndsWait()
        {
            int code = -9;
            _ui.Confirm("T2", "M2", (c, _) => code = c);
            yield return null;
            _ui.CloseAll();
            yield return null;
            Assert.That(code, Is.EqualTo(0));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
        }

        [UnityTest]
        public IEnumerator Toast_Recycles_Bounded()
        {
            for (int i = 0; i < 3; i++)
                _ui.ShowToast("t" + i, 0.05f);
            Assert.That(_ui.ToastActiveCount, Is.LessThanOrEqualTo(5));
            float until = Time.realtimeSinceStartup + 1f;
            while (_ui.ToastActiveCount > 0 && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.That(_ui.ToastActiveCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Toast_CloseAll_CancelsTimers_NoCallbackAfter()
        {
            int returnedAfter = 0;
            _ui.ShowToast("long", 30f);
            Assert.That(_ui.ToastActiveCount, Is.EqualTo(1));
            var toast = GameObject.Find("ToastItem");
            Assert.That(toast, Is.Not.Null);
            _ui.CloseAll();
            Assert.That(_ui.ToastActiveCount, Is.EqualTo(0));
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(_ui.ToastActiveCount, Is.EqualTo(0));
            Assert.That(returnedAfter, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator SafeArea_NonZeroInsets_AnchorsCorrect()
        {
            var adapter = UiRoot.Ensure().SafeArea;
            adapter.Apply(new Rect(100, 50, 1080, 1920), new Vector2Int(1280, 2200));
            Assert.That(adapter.AnchorMin.x, Is.EqualTo(100f / 1280f).Within(0.001f));
            Assert.That(adapter.AnchorMin.y, Is.EqualTo(50f / 2200f).Within(0.001f));
            adapter.Apply(new Rect(0, 0, 0, 0), new Vector2Int(0, 0));
            Assert.That(adapter.AnchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(adapter.AnchorMax, Is.EqualTo(Vector2.one));
            yield return null;
        }

        [UnityTest]
        public IEnumerator CancelPendingOpen_NoDirtyWindow()
        {
            _ui.Register(Reg("slow"));
            var cts = new CancellationTokenSource();
            cts.Cancel();
            int code = 0;
            yield return _ui.OpenAwait("slow", "", cts.Token).ToCoroutine(r => code = r);
            Assert.That(code, Is.EqualTo(-1));
            Assert.That(_ui.OpenCount, Is.EqualTo(0));
            Assert.That(GameObject.Find("Win_slow"), Is.Null);
        }

        [UnityTest]
        public IEnumerator CallbackLease_DisposePending_CompletesOnce_LiveCountBaseline()
        {
            int calls = 0;
            int last = 99;
            var lease = new CallbackLease((c, _) =>
            {
                calls++;
                last = c;
            });
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases + 1));
            lease.Dispose();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(last, Is.EqualTo(-1));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
            lease.Dispose();
            Assert.That(calls, Is.EqualTo(1));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ConcurrentOpen_SameId_WhilePending_SingleInstance()
        {
            var gate = new UniTaskCompletionSource();
            _ui.OpenDelayGate = async (_, ct) =>
            {
                await gate.Task.AttachExternalCancellation(ct);
            };
            _ui.Register(Reg("pend"));

            int c1 = -9, c2 = -9;
            var t1 = _ui.OpenAwait("pend", "first", CancellationToken.None);
            yield return null;
            Assert.That(_ui.OpeningCount, Is.EqualTo(1));
            var t2 = _ui.OpenAwait("pend", "second", CancellationToken.None);
            yield return t2.ToCoroutine(r => c2 = r);
            Assert.That(c2, Is.EqualTo(0));
            gate.TrySetResult();
            yield return t1.ToCoroutine(r => c1 = r);
            Assert.That(c1, Is.EqualTo(0));
            Assert.That(_ui.OpenCount, Is.EqualTo(1));
            Assert.That(GameObject.Find("Win_pend"), Is.Not.Null);
            _ui.CloseAll();
            _ui.OpenDelayGate = null;
        }

        [UnityTest]
        public IEnumerator CloseAll_DuringPending_NoResurrection()
        {
            var gate = new UniTaskCompletionSource();
            _ui.OpenDelayGate = async (_, ct) =>
            {
                await gate.Task.AttachExternalCancellation(ct);
            };
            _ui.Register(Reg("late"));
            int code = -9;
            var task = _ui.OpenAwait("late", "", CancellationToken.None);
            yield return null;
            _ui.CloseAll();
            gate.TrySetResult();
            yield return task.ToCoroutine(r => code = r);
            Assert.That(code, Is.EqualTo(-1));
            Assert.That(_ui.OpenCount, Is.EqualTo(0));
            Assert.That(GameObject.Find("Win_late"), Is.Null);
            _ui.OpenDelayGate = null;
        }

        [UnityTest]
        public IEnumerator Close_Reentry_DoesNotCloseNewlyOpenedSameId()
        {
            var first = new ReopenController();
            first.OnCloseAction = () =>
            {
                _ui.ControllerResolver = _ => new TrackingController();
                _ui.Open("re", "new", (_, __) => { });
            };
            _ui.ControllerResolver = _ => first;
            _ui.Register(Reg("re"));
            yield return _ui.OpenAwait("re", "old", CancellationToken.None).ToCoroutine();
            _ui.Close("re", (_, __) => { });
            yield return null;
            yield return null;
            Assert.That(_ui.OpenCount, Is.EqualTo(1));
            Assert.That(GameObject.Find("Win_re"), Is.Not.Null);
            var body = GameObject.Find("Win_re")?.transform.Find("Body")?.GetComponent<Text>();
            Assert.That(body, Is.Not.Null);
            Assert.That(body.text, Is.EqualTo("new"));
            _ui.CloseAll();
        }

        /// <summary>钩子部分初始化后失败，必须调用销毁钩子释放控制器自行拥有的内容。</summary>
        [UnityTest]
        public IEnumerator OnCreate_PartiallyInitialized_ThenThrows_ReleasesOwnedContent()
        {
            var partial = new PartialController();
            _ui.ControllerResolver = _ => partial;
            _ui.Register(Reg("partial"));
            int code = -99;
            yield return _ui.OpenAwait("partial", "", CancellationToken.None).ToCoroutine(r => code = r);
            yield return null;
            Assert.That(code, Is.EqualTo(3));
            Assert.That(partial.OwnedCount, Is.Zero);
            Assert.That(partial.DestroyCalls, Is.EqualTo(1));
            Assert.That(GameObject.Find("Win_partial"), Is.Null);
            Assert.That(_ui.OpenCount, Is.Zero);
            Assert.That(ClickSubscription.LiveCount, Is.EqualTo(_baselineClicks));
        }

        /// <summary>批量关闭与Dispose均应在调用用户钩子前阻止同步新开窗口。</summary>
        [UnityTest]
        public IEnumerator ClosingAll_AndDispose_RejectReentrantOpen()
        {
            _ui.Register(Reg("stray"));
            for (int i = 0; i < 2; i++)
            {
                int refused = 0;
                var owner = new ReopenController();
                owner.OnCloseAction = () =>
                {
                    _ui.ControllerResolver = _ => new TrackingController();
                    try { _ui.Open("stray", "must not reopen", (_, __) => { }); }
                    catch (InvalidOperationException) { refused++; }
                };
                _ui.ControllerResolver = _ => owner;
                string id = "closing" + i;
                _ui.Register(Reg(id));
                yield return _ui.OpenAwait(id, "", CancellationToken.None).ToCoroutine();
                if (i == 0) _ui.CloseAll();
                else _ui.Dispose();
                yield return null;
                Assert.That(refused, Is.EqualTo(1));
                Assert.That(_ui.OpenCount, Is.Zero);
                Assert.That(GameObject.Find("Win_stray"), Is.Null);
            }
        }

        /// <summary>不能只检查活跃计数：归还池中的闲置GameObject也必须在Dispose后销毁。</summary>
        [UnityTest]
        public IEnumerator Toast_ReturnedToPool_ThenDispose_DestroysActualObjects()
        {
            int before = ToastObjectCount();
            _ui.ShowToast("owned", 30f);
            Assert.That(ToastObjectCount(), Is.EqualTo(before + 1));
            _ui.CloseAll();
            Assert.That(_ui.ToastActiveCount, Is.Zero);
            Assert.That(ToastObjectCount(), Is.EqualTo(before + 1), "闲置池仍实际持有对象");
            _ui.Dispose();
            yield return null;
            Assert.That(ToastObjectCount(), Is.EqualTo(before));
        }

        static int ToastObjectCount()
        {
            int count = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.scene.IsValid() && go.name == "ToastItem") count++;
            return count;
        }

        sealed class PartialController : ILuaUiController
        {
            public int OwnedCount;
            public int DestroyCalls;
            ClickSubscription _click;
            public void OnCreate(ViewHost view)
            {
                OwnedCount++;
                _click = view.SubscribeClick("OkButton", () => { });
                throw new InvalidOperationException("partial initialization failed");
            }
            public void OnOpen(string argsJson) { }
            public void OnClose() { }
            public void OnDestroy()
            {
                DestroyCalls++;
                OwnedCount--;
                _click?.Dispose();
                _click = null;
            }
        }

        static UiWindowRegistration Reg(string id) => new UiWindowRegistration
        {
            WindowId = id,
            PrefabAddress = "",
            Layer = UILayer.Window,
            ControllerModule = "track",
            CanGoBack = true
        };

        sealed class TrackingController : ILuaUiController
        {
            ViewHost _view;
            ClickSubscription _sub;

            public void OnCreate(ViewHost view)
            {
                _view = view;
                _sub = view.SubscribeClick("OkButton", () => { });
            }

            public void OnOpen(string argsJson) => _view?.SetText("Body", argsJson ?? "");

            public void OnClose() { }

            public void OnDestroy()
            {
                _sub?.Dispose();
                _sub = null;
                _view = null;
            }
        }

        sealed class ReopenController : ILuaUiController
        {
            public Action OnCloseAction;
            ViewHost _view;
            ClickSubscription _sub;

            public void OnCreate(ViewHost view)
            {
                _view = view;
                _sub = view.SubscribeClick("OkButton", () => { });
            }

            public void OnOpen(string argsJson) => _view?.SetText("Body", argsJson ?? "");

            public void OnClose() => OnCloseAction?.Invoke();

            public void OnDestroy()
            {
                _sub?.Dispose();
                _sub = null;
            }
        }
    }

    /// <summary>真实 LuaEnv + 生成桥 + ResMgr Prefab；不注入 C# ControllerResolver。</summary>
    public sealed class UiLuaRealControllerTests
    {
        ResMgr _resources;
        LuaEnv _lua;
        UiManager _ui;
        int _baselineClicks;
        int _baselineLeases;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _baselineClicks = ClickSubscription.LiveCount;
            _baselineLeases = CallbackLease.LiveCount;

            _resources = new ResMgr { PlayMode = ResourcePlayMode.EditorSimulate };
            var manifest = new VersionManifest { BaseUrl = "http://127.0.0.1/unused", PackageVersion = "2" };
            yield return _resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();

            ResourcePackage package = YooAssets.GetPackage(ResMgr.PackageName);
            var ver = package.RequestPackageVersionAsync();
            yield return ver;
            Assert.That(ver.Status, Is.EqualTo(EOperationStatus.Succeeded));
            var man = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(ver.PackageVersion, 60));
            yield return man;
            Assert.That(man.Status, Is.EqualTo(EOperationStatus.Succeeded));

            _lua = new LuaEnv();
            _lua.AddLoader(LoadLuaFromAssets);
            UiRoot.Ensure();
            _ui = UiBridge.EnsureManager(_resources, _lua);
            // 故意不设 ControllerResolver：走 require + Cast
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _ui?.Dispose();
            _ui = null;
            if (_lua != null)
            {
                _lua.Dispose();
                _lua = null;
            }

            yield return null;
            Assert.That(ClickSubscription.LiveCount, Is.EqualTo(_baselineClicks));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
        }

        [UnityTest]
        public IEnumerator LuaPrefab_OpenClose_TwentyTimes_Baseline()
        {
            _ui.Register(new UiWindowRegistration
            {
                WindowId = "luaWin",
                PrefabAddress = "UiTestWindow",
                Layer = UILayer.Window,
                ControllerModule = "UI/Windows/TestWindow",
                CanGoBack = true
            });

            int leaksBefore = _resources.CaptureLeaks("ui:luaWin").Count;
            for (int i = 0; i < 20; i++)
            {
                int code = -99;
                yield return _ui.OpenAwait("luaWin", "{\"i\":" + i + "}", CancellationToken.None)
                    .ToCoroutine(r => code = r);
                Assert.That(code, Is.EqualTo(0), "open #" + i);
                Assert.That(GameObject.Find("Win_luaWin"), Is.Not.Null);
                bool closed = false;
                _ui.Close("luaWin", (c, _) => closed = c == 0);
                yield return null;
                Assert.That(closed, Is.True);
                Assert.That(_ui.OpenCount, Is.EqualTo(0));
            }

            Assert.That(_resources.CaptureLeaks("ui:luaWin").Count, Is.EqualTo(leaksBefore));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
            Assert.That(ClickSubscription.LiveCount, Is.EqualTo(_baselineClicks));
        }

        [UnityTest]
        public IEnumerator LuaPrefab_PendingGate_ExternalCancel_AndCloseAll()
        {
            var gate = new UniTaskCompletionSource();
            _ui.OpenDelayGate = async (_, ct) =>
            {
                await gate.Task.AttachExternalCancellation(ct);
            };
            _ui.Register(new UiWindowRegistration
            {
                WindowId = "luaPend",
                PrefabAddress = "UiTestWindow",
                Layer = UILayer.Window,
                ControllerModule = "UI/Windows/TestWindow"
            });

            var cts = new CancellationTokenSource();
            int codeCancel = -9;
            var tCancel = _ui.OpenAwait("luaPend", "", cts.Token);
            float until = Time.realtimeSinceStartup + 10f;
            while (_resources.CaptureLeaks("ui:luaPend").Count == 0 && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.That(_resources.CaptureLeaks("ui:luaPend").Count, Is.GreaterThan(0), "取消前已取得真实Prefab租约");
            cts.Cancel();
            gate.TrySetResult();
            yield return tCancel.ToCoroutine(r => codeCancel = r);
            Assert.That(codeCancel, Is.EqualTo(-1));
            Assert.That(_ui.OpenCount, Is.EqualTo(0));
            Assert.That(GameObject.Find("Win_luaPend"), Is.Null);
            Assert.That(_resources.CaptureLeaks("ui:luaPend").Count, Is.Zero);

            gate = new UniTaskCompletionSource();
            _ui.OpenDelayGate = async (_, ct) =>
            {
                await gate.Task.AttachExternalCancellation(ct);
            };
            int codeAll = -9;
            var tAll = _ui.OpenAwait("luaPend", "", CancellationToken.None);
            until = Time.realtimeSinceStartup + 10f;
            while (_resources.CaptureLeaks("ui:luaPend").Count == 0 && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.That(_resources.CaptureLeaks("ui:luaPend").Count, Is.GreaterThan(0), "关闭前真实lease已经存在");
            _ui.CloseAll();
            gate.TrySetResult();
            yield return tAll.ToCoroutine(r => codeAll = r);
            Assert.That(codeAll, Is.EqualTo(-1));
            Assert.That(_ui.OpenCount, Is.EqualTo(0));
            Assert.That(_resources.CaptureLeaks("ui:luaPend").Count, Is.Zero);

            gate = new UniTaskCompletionSource();
            _ui.OpenDelayGate = async (_, ct) => await gate.Task.AttachExternalCancellation(ct);
            var tDispose = _ui.OpenAwait("luaPend", "", CancellationToken.None);
            until = Time.realtimeSinceStartup + 10f;
            while (_resources.CaptureLeaks("ui:luaPend").Count == 0 && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.That(_resources.CaptureLeaks("ui:luaPend").Count, Is.GreaterThan(0));
            _ui.Dispose();
            gate.TrySetResult();
            yield return tDispose.ToCoroutine();
            yield return null;
            Assert.That(_resources.CaptureLeaks("ui:luaPend").Count, Is.Zero);
            Assert.That(GameObject.Find("Win_luaPend"), Is.Null);
            _ui.OpenDelayGate = null;
        }

        [UnityTest]
        public IEnumerator Lua_OpenTableArgs_NotDropped()
        {
            _lua.DoString(@"
                package.loaded['UI/UIManager'] = nil
                local UI = require('UI/UIManager')
                UI.register({
                  windowId = 'argsLua',
                  prefabAddress = 'UiTestWindow',
                  layer = 2,
                  controllerModule = 'UI/Windows/TestWindow',
                  canGoBack = true
                })
                UI.open('argsLua', { user = 'b', n = 2 }, function(code, payload) end)
            ", "table-args");
            float until = Time.realtimeSinceStartup + 8f;
            while (_ui.OpenCount == 0 && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.That(_ui.OpenCount, Is.EqualTo(1));
            var body = GameObject.Find("Win_argsLua")?.transform.Find("Body")?.GetComponent<Text>();
            Assert.That(body, Is.Not.Null);
            Assert.That(body.text, Does.Contain("b").And.Contain("user"),
                "table args 须 JSON 序列化进 OnOpen，不能静默变空串");
            _ui.CloseAll();
        }

        static byte[] LoadLuaFromAssets(ref string filepath)
        {
            string rel = (filepath ?? "").Replace('.', '/').Replace('\\', '/');
            if (rel.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(0, rel.Length - 4);
            string path = Path.Combine(Application.dataPath, "Lua", rel + ".txt");
            if (!File.Exists(path))
                return null;
            filepath = path;
            return File.ReadAllBytes(path);
        }
    }
}
