using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    public sealed class ResMgrSceneTests
    {
        ResMgr _resources;
        SceneLease _open;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _resources = new ResMgr();
            _resources.PlayMode = ResourcePlayMode.EditorSimulate;
            var manifest = new VersionManifest
            {
                BaseUrl = "http://127.0.0.1/unused",
                PackageVersion = "2"
            };
            yield return _resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();

            ResourcePackage package = YooAssets.GetPackage(ResMgr.PackageName);
            RequestPackageVersionOperation versionOperation = package.RequestPackageVersionAsync();
            yield return versionOperation;
            Assert.That(versionOperation.Status, Is.EqualTo(EOperationStatus.Succeeded));

            LoadPackageManifestOperation manifestOperation = package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(versionOperation.PackageVersion, 60));
            yield return manifestOperation;
            Assert.That(manifestOperation.Status, Is.EqualTo(EOperationStatus.Succeeded));
        }

        [UnityTest]
        public IEnumerator Home_LoadsThenUnloads()
        {
            int sceneCount = SceneManager.sceneCount;
            SceneLease lease = null;
            yield return LoadHome(value => lease = value);

            Assert.That(lease.Address, Is.EqualTo("Home"));
            Assert.That(lease.IsLoaded, Is.True);
            Assert.That(SceneManager.GetSceneByName("Home").isLoaded, Is.True);
            Assert.That(GameObject.Find("HomeMarker"), Is.Not.Null);

            yield return _resources.UnloadSceneAsync(lease, CancellationToken.None).ToCoroutine();
            Assert.That(lease.IsLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByName("Home").isLoaded, Is.False);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));

            yield return _resources.UnloadSceneAsync(lease, CancellationToken.None).ToCoroutine();
            Assert.That(lease.IsLoaded, Is.False);
        }

        [UnityTest]
        public IEnumerator Cancel_RemovesHomeAndLaterLoadWorks()
        {
            int sceneCount = SceneManager.sceneCount;
            var cts = new CancellationTokenSource();
            cts.Cancel();

            Exception caught = null;
            yield return LoadHomeCancelled(cts.Token).ToCoroutine(value => caught = value);
            Assert.That(caught, Is.InstanceOf<OperationCanceledException>());
            Assert.That(SceneManager.GetSceneByName("Home").isLoaded, Is.False);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));

            SceneLease lease = null;
            yield return LoadHome(value => lease = value);
            Assert.That(lease.IsLoaded, Is.True);
            Assert.That(SceneManager.GetSceneByName("Home").isLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator MissingScene_ReleasesHandleAndHomeStillLoads()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Failed to map location to asset path: 'MissingScene-T16'"));
            LogAssert.Expect(LogType.Error, new Regex("Failed to load"));

            int sceneCount = SceneManager.sceneCount;
            Exception caught = null;
            yield return LoadMissing().ToCoroutine(value => caught = value);
            Assert.That(caught, Is.InstanceOf<InvalidOperationException>());
            Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));

            SceneLease lease = null;
            yield return LoadHome(value => lease = value);
            Assert.That(lease.IsLoaded, Is.True);
            Assert.That(GameObject.Find("HomeMarker"), Is.Not.Null);
        }

        IEnumerator LoadHome(Action<SceneLease> assign)
        {
            SceneLease lease = null;
            yield return _resources.LoadSceneAsync("Home", CancellationToken.None).ToCoroutine(value => lease = value);
            _open = lease;
            assign(lease);
        }

        async UniTask<Exception> LoadHomeCancelled(CancellationToken ct)
        {
            try
            {
                await _resources.LoadSceneAsync("Home", ct);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        async UniTask<Exception> LoadMissing()
        {
            try
            {
                await _resources.LoadSceneAsync("MissingScene-T16", CancellationToken.None);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_open != null && _open.IsLoaded)
                yield return _resources.UnloadSceneAsync(_open, CancellationToken.None).ToCoroutine();
            _open = null;

            if (YooAssets.IsInitialized && YooAssets.TryGetPackage(ResMgr.PackageName, out ResourcePackage package) && package != null)
            {
                DestroyPackageOperation destroy = package.DestroyPackageAsync();
                yield return destroy;
                YooAssets.RemovePackage(ResMgr.PackageName);
            }

            YooAssets.Destroy();
        }
    }
}
