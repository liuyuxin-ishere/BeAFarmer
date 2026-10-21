using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using Game.Framework.Scenes;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// T62：06 约定两例——20 次开关回基线；取消切场景后无临时资源残留。
    /// </summary>
    public sealed class UIResourceTests
    {
        UiManager _ui;
        int _baselineClicks;
        int _baselineLeases;
        ResMgr _resources;
        LoadingView _loading;
        SceneService _scenes;

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
            if (_scenes != null)
            {
                float until = Time.realtimeSinceStartup + 10f;
                while (_scenes.IsBusy && Time.realtimeSinceStartup < until)
                    yield return null;
                if (!_scenes.IsBusy)
                    yield return _scenes.UnloadCurrentAsync(CancellationToken.None).ToCoroutine();
            }

            _scenes = null;
            _resources = null;
            yield return null;
            Assert.That(ClickSubscription.LiveCount, Is.EqualTo(_baselineClicks));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
        }

        [UnityTest]
        public IEnumerator OpenClose_ReturnsToBaseline()
        {
            _ui.Register(new UiWindowRegistration
            {
                WindowId = "ures",
                PrefabAddress = "",
                Layer = UILayer.Window,
                ControllerModule = "track",
                CanGoBack = false
            });

            int baseOpen = _ui.OpenCount;
            for (int i = 0; i < 20; i++)
            {
                int code = -99;
                yield return _ui.OpenAwait("ures", "i=" + i, CancellationToken.None)
                    .ToCoroutine(r => code = r);
                Assert.That(code, Is.EqualTo(0), "open#" + i);
                Assert.That(_ui.OpenCount, Is.EqualTo(baseOpen + 1));

                bool closed = false;
                _ui.Close("ures", (c, _) => closed = c == 0);
                yield return null;
                Assert.That(closed, Is.True, "close#" + i);
                Assert.That(_ui.OpenCount, Is.EqualTo(baseOpen));
            }

            Assert.That(ClickSubscription.LiveCount, Is.EqualTo(_baselineClicks));
            Assert.That(CallbackLease.LiveCount, Is.EqualTo(_baselineLeases));
        }

        [UnityTest]
        public IEnumerator CancelSwitch_ReleasesTemporaryHandles()
        {
            _resources = new ResMgr { PlayMode = ResourcePlayMode.EditorSimulate };
            var manifest = new CloudIsle.Contracts.VersionManifest
            {
                BaseUrl = "http://127.0.0.1/unused",
                PackageVersion = "2"
            };
            yield return _resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();
            ResourcePackage package = YooAssets.GetPackage(ResMgr.PackageName);
            var ver = package.RequestPackageVersionAsync();
            yield return ver;
            Assert.That(ver.Status, Is.EqualTo(EOperationStatus.Succeeded));
            var man = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(ver.PackageVersion, 60));
            yield return man;
            Assert.That(man.Status, Is.EqualTo(EOperationStatus.Succeeded));

            _loading = LoadingView.Ensure();
            _scenes = new SceneService(_resources, _loading, "M1_Login");

            for (int i = 0; i < 10; i++)
            {
                string address = i % 2 == 0 ? "M1_Login" : "M1_Home";
                if (i == 4)
                {
                    var cts = new CancellationTokenSource();
                    Exception caught = null;
                    var pending = _scenes.SwitchAsync("M1_Home", cts.Token);
                    Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Pending));
                    cts.Cancel();
                    yield return pending.ToCoroutine(e => caught = e);
                    Assert.That(caught, Is.InstanceOf<OperationCanceledException>());
                    Assert.That(_scenes.IsBusy, Is.False);
                    Assert.That(_loading.IsVisible, Is.False);
                }
                else
                {
                    yield return _scenes.SwitchAsync(address, CancellationToken.None).ToCoroutine();
                    Assert.That(_scenes.IsBusy, Is.False);
                    Assert.That(_loading.IsVisible, Is.False);
                }
            }
        }

        sealed class TrackingController : ILuaUiController
        {
            public void OnCreate(ViewHost view) { }
            public void OnOpen(string argsJson) { }
            public void OnClose() { }
            public void OnDestroy() { }
        }
    }
}
