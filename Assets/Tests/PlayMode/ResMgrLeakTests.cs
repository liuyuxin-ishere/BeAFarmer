using System.Collections;
using System.Collections.Generic;
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
    public sealed class ResMgrLeakTests
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
        public IEnumerator KeepOne_ReportsThenDisappears()
        {
            ResourceLease<GameObject> lease = null;
            yield return Load("PatchCube", "owner-a", value => lease = value);
            UnityEngine.Object asset = lease.Asset;

            IReadOnlyList<ResourceLeakInfo> before = _resources.CaptureLeaks("owner-a");
            Assert.That(before.Count, Is.EqualTo(1));
            Assert.That(before[0].Address, Is.EqualTo("PatchCube"));
            Assert.That(before[0].Owner, Is.EqualTo("owner-a"));
            Assert.That(before[0].RefCount, Is.EqualTo(1));
            Assert.That(before[0].CreatedAtMs, Is.GreaterThan(0));
            Assert.That(lease.IsDisposed, Is.False);
            Assert.That(lease.Asset, Is.SameAs(asset));

            lease.Dispose();
            IReadOnlyList<ResourceLeakInfo> after = _resources.CaptureLeaks("owner-a");
            Assert.That(after.Count, Is.EqualTo(0));
            Assert.That(lease.IsDisposed, Is.True);
        }

        [UnityTest]
        public IEnumerator SameOwner_RefCountDropsAsLeasesRelease()
        {
            ResourceLease<GameObject> first = null;
            ResourceLease<GameObject> second = null;
            yield return Load("PatchCube", "owner-a", value => first = value);
            long createdAtMs = _resources.CaptureLeaks("owner-a")[0].CreatedAtMs;

            yield return Load("PatchCube", "owner-a", value => second = value);
            IReadOnlyList<ResourceLeakInfo> both = _resources.CaptureLeaks("owner-a");
            Assert.That(both.Count, Is.EqualTo(1));
            Assert.That(both[0].RefCount, Is.EqualTo(2));
            Assert.That(both[0].CreatedAtMs, Is.EqualTo(createdAtMs));
            Assert.That(both[0].Address, Is.EqualTo("PatchCube"));
            Assert.That(both[0].Owner, Is.EqualTo("owner-a"));

            first.Dispose();
            IReadOnlyList<ResourceLeakInfo> one = _resources.CaptureLeaks("owner-a");
            Assert.That(one.Count, Is.EqualTo(1));
            Assert.That(one[0].RefCount, Is.EqualTo(1));
            Assert.That(one[0].CreatedAtMs, Is.EqualTo(createdAtMs));
            Assert.That(second.IsDisposed, Is.False);

            second.Dispose();
            Assert.That(_resources.CaptureLeaks("owner-a").Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Resident_StaysOutOfSceneReport()
        {
            ResourceLease<GameObject> resident = null;
            ResourceLease<GameObject> scene = null;
            yield return Load("PatchCube", "resident", value => resident = value);
            yield return Load("PatchCube", "scene-home", value => scene = value);
            UnityEngine.Object residentAsset = resident.Asset;

            IReadOnlyList<ResourceLeakInfo> sceneLeaks = _resources.CaptureLeaks("scene-home");
            Assert.That(sceneLeaks.Count, Is.EqualTo(1));
            Assert.That(sceneLeaks[0].Owner, Is.EqualTo("scene-home"));
            Assert.That(sceneLeaks[0].Address, Is.EqualTo("PatchCube"));
            Assert.That(sceneLeaks[0].RefCount, Is.EqualTo(1));
            Assert.That(resident.IsDisposed, Is.False);
            Assert.That(resident.Asset, Is.SameAs(residentAsset));

            IReadOnlyList<ResourceLeakInfo> residentLeaks = _resources.CaptureLeaks("resident");
            Assert.That(residentLeaks.Count, Is.EqualTo(1));
            Assert.That(residentLeaks[0].Owner, Is.EqualTo("resident"));
        }

        [UnityTest]
        public IEnumerator OtherOwner_IsEmptyAndQueryKeepsLease()
        {
            ResourceLease<GameObject> lease = null;
            yield return Load("PatchCube", "owner-a", value => lease = value);
            UnityEngine.Object asset = lease.Asset;

            IReadOnlyList<ResourceLeakInfo> other = _resources.CaptureLeaks("owner-b");
            Assert.That(other.Count, Is.EqualTo(0));
            Assert.That(lease.IsDisposed, Is.False);
            Assert.That(lease.Asset, Is.SameAs(asset));

            IReadOnlyList<ResourceLeakInfo> own = _resources.CaptureLeaks("owner-a");
            Assert.That(own.Count, Is.EqualTo(1));
            Assert.That(own[0].RefCount, Is.EqualTo(1));
            Assert.That(lease.Asset, Is.SameAs(asset));
        }

        IEnumerator Load(string address, string owner, System.Action<ResourceLease<GameObject>> assign)
        {
            ResourceLease<GameObject> lease = null;
            yield return _resources.LoadAsync<GameObject>(address, owner, CancellationToken.None)
                .ToCoroutine(value => lease = value);
            _open.Add(lease);
            assign(lease);
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
