using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    public sealed class ResMgrLoadTests
    {
        readonly List<ResourceLease<GameObject>> _open = new List<ResourceLease<GameObject>>();
        ResMgr _resources;

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
        public IEnumerator Load_Twice_KeepsTheOtherLease()
        {
            ResourceLease<GameObject> first = null;
            ResourceLease<GameObject> second = null;
            yield return _resources.LoadAsync<GameObject>("PatchCube", "owner-a", CancellationToken.None)
                .ToCoroutine(value => first = value);
            yield return _resources.LoadAsync<GameObject>("PatchCube", "owner-b", CancellationToken.None)
                .ToCoroutine(value => second = value);
            _open.Add(first);
            _open.Add(second);

            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.Address, Is.EqualTo("PatchCube"));
            Assert.That(second.Address, Is.EqualTo("PatchCube"));
            Assert.That(first.Owner, Is.EqualTo("owner-a"));
            Assert.That(second.Owner, Is.EqualTo("owner-b"));
            Assert.That(first.Asset, Is.Not.Null);
            Assert.That(second.Asset, Is.Not.Null);

            UnityEngine.Object kept = second.Asset;
            first.Dispose();
            Assert.That(first.IsDisposed, Is.True);
            Assert.That(first.Asset, Is.Null);
            Assert.That(second.IsDisposed, Is.False);
            Assert.That(second.Asset, Is.SameAs(kept));

            first.Dispose();
            Assert.That(first.IsDisposed, Is.True);
        }

        [UnityTest]
        public IEnumerator Load_MissingAddress_ThenPatchCubeStillLoads()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Failed to map location to asset path: 'MissingAddress-T14'"));
            LogAssert.Expect(LogType.Error, new Regex("Failed to load asset"));

            Exception caught = null;
            yield return LoadMissing().ToCoroutine(value => caught = value);
            Assert.That(caught, Is.Not.Null);

            ResourceLease<GameObject> lease = null;
            yield return _resources.LoadAsync<GameObject>("PatchCube", "owner-retry", CancellationToken.None)
                .ToCoroutine(value => lease = value);
            _open.Add(lease);
            Assert.That(lease.Asset, Is.Not.Null);
            Assert.That(lease.Owner, Is.EqualTo("owner-retry"));
        }

        async UniTask<Exception> LoadMissing()
        {
            try
            {
                await _resources.LoadAsync<GameObject>("MissingAddress-T14", "owner-missing", CancellationToken.None);
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
            for (int i = 0; i < _open.Count; i++)
                _open[i].Dispose();
            _open.Clear();

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
