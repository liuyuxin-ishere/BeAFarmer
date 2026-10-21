using System.Collections;
using System.IO;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using NUnit.Framework;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    public sealed class ResMgrInitializeTests
    {
        [UnityTest]
        public IEnumerator EditorSimulate_InitializesPackage()
        {
            ResMgr resources = new ResMgr();
            resources.PlayMode = ResourcePlayMode.EditorSimulate;
            var manifest = new VersionManifest
            {
                BaseUrl = "http://127.0.0.1/unused",
                PackageVersion = "2"
            };

            yield return resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();

            Assert.That(resources.ActiveMode, Is.EqualTo(EPlayMode.EditorSimulateMode));
            Assert.That(Directory.Exists(resources.RootPath), Is.True);
        }

        [UnityTest]
        public IEnumerator HostHttp_InitializesPackage()
        {
            ResMgr resources = new ResMgr();
            resources.PlayMode = ResourcePlayMode.HostHttp;
            var manifest = new VersionManifest
            {
                BaseUrl = "http://127.0.0.1/CloudIsle"
            };

            yield return resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();

            Assert.That(resources.ActiveMode, Is.EqualTo(EPlayMode.HostPlayMode));
            Assert.That(resources.RootPath, Is.EqualTo("http://127.0.0.1/CloudIsle"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (YooAssets.IsInitialized && YooAssets.TryGetPackage(ResMgr.PackageName, out ResourcePackage package) && package != null)
            {
                DestroyPackageOperation destroy = package.DestroyPackageAsync();
                yield return destroy;
                YooAssets.RemovePackage(ResMgr.PackageName);
                YooPackageOwnership.Clear(ResMgr.PackageName);
            }

            YooAssets.Destroy();
            YooPackageOwnership.ClearAllForTests();
        }
    }
}
