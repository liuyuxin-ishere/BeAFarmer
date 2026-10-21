using System;
using System.Collections;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Boot;
using Game.Framework.Lua;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    /// <summary>启动门闸、中途取消与宿主销毁的 PlayMode 验证。</summary>
    public sealed class XLuaBootCancelTests
    {
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            HotfixLoader.ResetBootGateForTests();
            HotfixLoader.ArmPreCommitWaitForTests(0);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            HotfixLoader.ResetBootGateForTests();
            HotfixLoader.ArmPreCommitWaitForTests(0);
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartAsync_PreCancelled_Throws()
        {
            var go = new GameObject("CancelPre");
            go.SetActive(false);
            var loader = go.AddComponent<HotfixLoader>();
            loader.SetAutoStartForTests(false);
            go.SetActive(true);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            var context = new BootContext
            {
                Versions = new VersionManifest { ResVersion = 100, ProtoVersion = 1, ConfigVersion = 1 }
            };

            Exception caught = null;
            yield return loader.StartAsync(context, cts.Token).ToCoroutine(e => caught = e);
            Assert.That(caught, Is.InstanceOf<OperationCanceledException>());
            Assert.That(HotfixLoader.BootGateTaken, Is.False);
            UnityEngine.Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartAsync_MidCancel_WhilePending_ThrowsOCE_NoCommitLeak()
        {
            // 提交前等待屏障：确保取消时仍 Pending 且已跨帧进入启动。
            HotfixLoader.ArmPreCommitWaitForTests(40);
            var go = new GameObject("CancelMid");
            go.SetActive(false);
            var loader = go.AddComponent<HotfixLoader>();
            loader.SetAutoStartForTests(false);
            go.SetActive(true);

            var cts = new CancellationTokenSource();
            var context = new BootContext
            {
                Versions = new VersionManifest { ResVersion = 100, ProtoVersion = 1, ConfigVersion = 1 }
            };

            Exception caught = null;
            var task = loader.StartAsync(context, cts.Token);

            var until = Time.realtimeSinceStartup + 30f;
            while (!HotfixLoader.TestPreCommitWaiting && Time.realtimeSinceStartup < until)
                yield return null;

            Assert.That(HotfixLoader.TestPreCommitWaiting, Is.True,
                "must reach pre-commit barrier before cancel");
            Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending),
                "task must still be Pending at cancel time");

            cts.Cancel();

            until = Time.realtimeSinceStartup + 45f;
            while (task.Status == UniTaskStatus.Pending && Time.realtimeSinceStartup < until)
                yield return null;

            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.That(caught, Is.InstanceOf<OperationCanceledException>(),
                "mid-cancel must surface OCE, not a generic finish");
            Assert.That(loader.BootFinished, Is.True);
            Assert.That(AsyncLease.LiveSubscriptions, Is.EqualTo(0));
            Assert.That(loader.DisposeError, Is.EqualTo(string.Empty),
                "dispose must report empty error string");
            UnityEngine.Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartAsync_DestroyWhilePending_EndsWaitAndReleases()
        {
            // 不用 ignoreFailingMessages 全面吞异常；依赖 OCE/订阅数断言，finally 恢复标志。
            var prevIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = false;
            try
            {
                HotfixLoader.ArmPreCommitWaitForTests(40);
                var go = new GameObject("DestroyMid");
                go.SetActive(false);
                var loader = go.AddComponent<HotfixLoader>();
                loader.SetAutoStartForTests(false);
                go.SetActive(true);

                var cts = new CancellationTokenSource();
                var context = new BootContext
                {
                    Versions = new VersionManifest { ResVersion = 100, ProtoVersion = 1, ConfigVersion = 1 }
                };

                Exception caught = null;
                var task = loader.StartAsync(context, cts.Token);

                var until = Time.realtimeSinceStartup + 30f;
                while (!HotfixLoader.TestPreCommitWaiting && Time.realtimeSinceStartup < until)
                    yield return null;

                Assert.That(HotfixLoader.TestPreCommitWaiting, Is.True);
                Assert.That(task.Status, Is.EqualTo(UniTaskStatus.Pending));

                UnityEngine.Object.Destroy(go);
                yield return null;
                yield return null;

                until = Time.realtimeSinceStartup + 20f;
                while (task.Status == UniTaskStatus.Pending && Time.realtimeSinceStartup < until)
                    yield return null;

                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                Assert.That(caught, Is.InstanceOf<OperationCanceledException>(),
                    "destroy while pending must cancel the wait");
                Assert.That(AsyncLease.LiveSubscriptions, Is.EqualTo(0));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = prevIgnore;
            }
        }
    }
}
