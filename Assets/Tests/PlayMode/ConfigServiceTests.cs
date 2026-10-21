using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using CloudIsle.Config;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Config;
using Game.Framework.Resource;
using NUnit.Framework;
using UnityEngine.TestTools;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    public sealed class ConfigServiceTests
    {
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
            // InitializeAsync 只建包；LoadAsync 要求已有活动清单（YooAsset 3.0.6）。
            yield return _resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();

            ResourcePackage package = YooAssets.GetPackage(ResMgr.PackageName);
            RequestPackageVersionOperation versionOperation = package.RequestPackageVersionAsync();
            yield return versionOperation;
            Assert.That(versionOperation.Status, Is.EqualTo(EOperationStatus.Succeeded),
                "simulate package version must succeed before ConfigService LoadAsync");

            // 激活模拟清单，与 ResMgrLoadTests 同一模式。
            LoadPackageManifestOperation manifestOperation = package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(versionOperation.PackageVersion, 60));
            yield return manifestOperation;
            Assert.That(manifestOperation.Status, Is.EqualTo(EOperationStatus.Succeeded),
                "active package manifest must be loaded for ConfigServiceTests");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            ConfigService.TestInterTableDelayMs = 0;
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

        [UnityTest]
        public IEnumerator Load_Version100_MatchesExportedGlobalsAndReleasesLeases()
        {
            var service = new ConfigService(_resources);
            yield return service.LoadAsync(ConfigAddresses.Manifest, 100, CancellationToken.None).ToCoroutine();

            Assert.That(service.GetGlobal("bag_init_slots"), Is.EqualTo("60"));
            Assert.That(service.GetGlobal("water_ratio"), Is.EqualTo("0.1"));
            Assert.That(service.GetGlobal("bag_expand_costs"), Is.EqualTo("[500,1000,2000,4000,8000]"));
            Assert.That(service.GetGlobal("new_player_gem"), Is.EqualTo("300"));
            Assert.That(service.GetGlobal("new_player_ticket"), Is.EqualTo("5"));
            Assert.Throws<KeyNotFoundException>(() => service.GetGlobal("m1_patch_message"));
            Assert.That(_resources.CaptureLeaks(ConfigService.LeaseOwner).Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Load_WrongVersion_KeepsPreviousAndReleasesLeases()
        {
            var service = new ConfigService(_resources);
            Exception first = null;
            yield return service.LoadAsync(ConfigAddresses.Manifest, 99, CancellationToken.None)
                .ToCoroutine(error => first = error);

            Assert.That(first, Is.InstanceOf<ConfigLoadException>());
            Assert.That(first.Message, Does.Contain("配置版本不匹配"));
            Assert.Throws<InvalidOperationException>(() => service.GetGlobal("bag_init_slots"));
            Assert.That(_resources.CaptureLeaks(ConfigService.LeaseOwner).Count, Is.EqualTo(0));

            yield return service.LoadAsync(ConfigAddresses.Manifest, 100, CancellationToken.None).ToCoroutine();
            Exception second = null;
            yield return service.LoadAsync(ConfigAddresses.Manifest, 101, CancellationToken.None)
                .ToCoroutine(error => second = error);

            Assert.That(second, Is.InstanceOf<ConfigLoadException>());
            Assert.That(service.GetGlobal("bag_init_slots"), Is.EqualTo("60"));
            Assert.That(service.GetGlobal("fertilize_ratio"), Is.EqualTo("0.3"));
            Assert.That(_resources.CaptureLeaks(ConfigService.LeaseOwner).Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Load_Cancel_ReleasesLeasesAndKeepsUnloaded()
        {
            var service = new ConfigService(_resources);
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Exception error = null;
            yield return service.LoadAsync(ConfigAddresses.Manifest, 100, cancelled.Token)
                .ToCoroutine(caught => error = caught);

            Assert.That(error, Is.InstanceOf<OperationCanceledException>());
            Assert.Throws<InvalidOperationException>(() => service.GetGlobal("bag_init_slots"));
            Assert.That(_resources.CaptureLeaks(ConfigService.LeaseOwner).Count, Is.EqualTo(0));
        }

        /// <summary>
        /// 表间延迟窗口内取消：证明等待中取消也会释放租约且不提交快照（非预取消）。
        /// </summary>
        [UnityTest]
        public IEnumerator Load_MidCancel_WhileTablesPending_ReleasesLeases()
        {
            ConfigService.TestInterTableDelayMs = 400;
            var service = new ConfigService(_resources);
            var cts = new CancellationTokenSource();
            Exception error = null;
            var task = service.LoadAsync(ConfigAddresses.Manifest, 100, cts.Token);

            // 跨过清单加载进入表间 Delay 后再取消。
            yield return new UnityEngine.WaitForSeconds(0.25f);
            cts.Cancel();
            yield return task.ToCoroutine(caught => error = caught);

            Assert.That(error, Is.InstanceOf<OperationCanceledException>());
            Assert.Throws<InvalidOperationException>(() => service.GetGlobal("bag_init_slots"));
            Assert.That(_resources.CaptureLeaks(ConfigService.LeaseOwner).Count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Load_ConcurrentWrongVersion_DoesNotRollBack()
        {
            var service = new ConfigService(_resources);
            Exception goodError = null;
            Exception badError = null;
            yield return RunConcurrent(service).ToCoroutine(error =>
            {
                if (error != null)
                    throw error;
            });

            async UniTask RunConcurrent(ConfigService target)
            {
                UniTask<Exception> good = Capture(target.LoadAsync(ConfigAddresses.Manifest, 100, CancellationToken.None));
                UniTask<Exception> bad = Capture(target.LoadAsync(ConfigAddresses.Manifest, 98, CancellationToken.None));
                goodError = await good;
                badError = await bad;
            }

            Assert.That(goodError, Is.Null);
            Assert.That(badError, Is.InstanceOf<ConfigLoadException>());
            Assert.That(service.GetGlobal("bag_init_slots"), Is.EqualTo("60"));
            Assert.That(service.GetGlobal("order_daily_count"), Is.EqualTo("6"));
            Assert.That(_resources.CaptureLeaks(ConfigService.LeaseOwner).Count, Is.EqualTo(0));
        }

        static async UniTask<Exception> Capture(UniTask task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
