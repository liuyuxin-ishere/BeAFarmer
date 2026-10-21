using System;
using System.Collections;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using Game.Framework.Scenes;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>T52：Loading + SceneService 经 ResMgr；取消等底层完成。</summary>
    public sealed class SceneServiceTests
    {
        ResMgr _resources;
        LoadingView _loading;
        SceneService _scenes;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
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

            _loading = LoadingView.Ensure();
            _scenes = new SceneService(_resources, _loading, "M1_Login");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scenes != null)
            {
                float until = Time.realtimeSinceStartup + 10f;
                while (_scenes.IsBusy && Time.realtimeSinceStartup < until)
                    yield return null;
                if (!_scenes.IsBusy)
                    yield return _scenes.UnloadCurrentAsync(CancellationToken.None).ToCoroutine();
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Switch_Home_LoadingShowsThenHides()
        {
            Assert.That(_loading.IsVisible, Is.False);
            var task = _scenes.SwitchAsync("M1_Home", CancellationToken.None);
            Assert.That(_loading.IsVisible, Is.True, "真正加载开始时Loading先可见");
            // 切换中 Loading 应曾显示；结束后隐藏。
            yield return task.ToCoroutine();
            Assert.That(_scenes.CurrentAddress, Is.EqualTo("M1_Home"));
            Assert.That(_loading.IsVisible, Is.False);
            Assert.That(GameObject.Find("M1HomeMarker"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Switch_TenTimes_MutexAndStable()
        {
            for (int i = 0; i < 10; i++)
            {
                string address = i % 2 == 0 ? "M1_Login" : "M1_Home";
                string previous = i % 2 == 0 ? "M1_Home" : "M1_Login";
                yield return _scenes.SwitchAsync(address, CancellationToken.None).ToCoroutine();
                Assert.That(_scenes.IsBusy, Is.False);
                Assert.That(SceneManager.GetSceneByName(address).isLoaded, Is.True);
                Assert.That(SceneManager.GetSceneByName(previous).isLoaded, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator Cancel_DoesNotLeaveScene_AndRetryWorks()
        {
            int before = SceneManager.sceneCount;
            var cts = new CancellationTokenSource();
            Exception caught = null;
            var pending = _scenes.SwitchAsync("M1_Home", cts.Token);
            Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Pending), "底层真实加载已开始，不能用预取消代替");
            Assert.That(_scenes.IsBusy, Is.True);
            Assert.That(_loading.IsVisible, Is.True);
            cts.Cancel();
            yield return pending
                .ToCoroutine(e => caught = e);
            Assert.That(caught, Is.InstanceOf<OperationCanceledException>());
            Assert.That(SceneManager.GetSceneByName("M1_Home").isLoaded, Is.False);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(before));
            Assert.That(_loading.IsVisible, Is.False);

            yield return _scenes.SwitchAsync("M1_Home", CancellationToken.None).ToCoroutine();
            Assert.That(_scenes.CurrentAddress, Is.EqualTo("M1_Home"));
        }

        [UnityTest]
        public IEnumerator ConcurrentSwitch_Rejected()
        {
            var t1 = _scenes.SwitchAsync("M1_Home", CancellationToken.None);
            Assert.That(_scenes.IsBusy, Is.True);
            Exception busyEx = null;
            yield return _scenes.SwitchAsync("M1_Login", CancellationToken.None)
                .ToCoroutine(e => busyEx = e);
            Assert.That(busyEx, Is.InstanceOf<InvalidOperationException>());
            Assert.That(busyEx.Message, Does.Contain("busy"));
            yield return t1.ToCoroutine();
            Assert.That(_scenes.IsBusy, Is.False);
        }

        /// <summary>真实缺失目标失败后加载正式Login回退场景，Loading不悬挂，仍允许再次进入Home。</summary>
        [UnityTest]
        public IEnumerator MissingScene_FallsBackToFormalLogin_ThenCanRetry()
        {
            yield return _scenes.SwitchAsync("M1_Home", CancellationToken.None).ToCoroutine();
            LogAssert.Expect(LogType.Error, "Failed to load scene. Error: Location is invalid: 'M1_Missing_Scene'.");
            yield return _scenes.SwitchAsync("M1_Missing_Scene", CancellationToken.None).ToCoroutine();
            Assert.That(_scenes.CurrentAddress, Is.EqualTo("M1_Login"));
            Assert.That(SceneManager.GetSceneByName("M1_Home").isLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByName("M1_Login").isLoaded, Is.True);
            Assert.That(_loading.IsVisible, Is.False);
            yield return _scenes.SwitchAsync("M1_Home", CancellationToken.None).ToCoroutine();
            Assert.That(_scenes.CurrentAddress, Is.EqualTo("M1_Home"));
        }
    }
}
