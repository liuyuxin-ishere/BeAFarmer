using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Boot;
using Game.Framework.Lua;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    /// <summary>正式 LuaLoader / LuaIndex：校验拒绝、成功启停、取消清理。</summary>
    public sealed class LuaLoaderTests
    {
        [UnityTest]
        public IEnumerator Prepare_RejectsDuplicateModule()
        {
            var bytes = Encoding.UTF8.GetBytes("return {}\n");
            string sha = HashUtil.Sha256(bytes);
            var index = new LuaIndex
            {
                ResVersion = "100",
                EntryModule = "entry",
                ConfigAddress = "cfg",
                ConfigSha256 = sha,
                BridgeContract = BridgeApi.BridgeContractValue,
                RequiredAppVersion = Application.version,
                ProtoVersion = BridgeApi.RequiredProtoVersion,
                Modules = new[]
                {
                    new LuaIndexModule { Name = "entry", Address = "a", Sha256 = sha },
                    new LuaIndexModule { Name = "entry", Address = "b", Sha256 = sha }
                }
            };
            var modules = new Dictionary<string, byte[]> { ["entry"] = bytes };
            using (var loader = new LuaLoader())
            {
                Exception error = null;
                yield return loader.PrepareAsync(index, modules, bytes, CancellationToken.None)
                    .ToCoroutine(e => error = e);
                Assert.That(error, Is.Not.Null);
                Assert.That(error.Message, Does.Contain("duplicate-module-name"));
                Assert.That(loader.IsReady, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator Prepare_RejectsBadBridge()
        {
            var bytes = Encoding.UTF8.GetBytes("return {}\n");
            string sha = HashUtil.Sha256(bytes);
            var index = GoodIndex(sha);
            index.BridgeContract = "wrong-bridge";
            var modules = new Dictionary<string, byte[]> { ["entry"] = bytes };
            using (var loader = new LuaLoader())
            {
                Exception error = null;
                yield return loader.PrepareAsync(index, modules, bytes, CancellationToken.None)
                    .ToCoroutine(e => error = e);
                Assert.That(error.Message, Does.Contain("bridgeContract-mismatch"));
            }
        }

        [UnityTest]
        public IEnumerator Prepare_RejectsSyntax()
        {
            var bad = Encoding.UTF8.GetBytes("function (((( bad");
            string sha = HashUtil.Sha256(bad);
            var index = GoodIndex(sha);
            var modules = new Dictionary<string, byte[]> { ["entry"] = bad };
            using (var loader = new LuaLoader())
            {
                Exception error = null;
                yield return loader.PrepareAsync(index, modules, bad, CancellationToken.None)
                    .ToCoroutine(e => error = e);
                Assert.That(error.Message, Does.Contain("syntax"));
            }
        }

        [UnityTest]
        public IEnumerator PrepareAndStart_BuiltinEntry_ReadyThenDispose()
        {
            Dictionary<string, byte[]> modules;
            byte[] config;
            LuaIndex index;
            if (!TryLoadBuiltin(out index, out modules, out config))
            {
                Assert.Ignore("builtin Lua assets missing");
                yield break;
            }

            using (var loader = new LuaLoader())
            {
                Exception prepError = null;
                yield return loader.PrepareAsync(index, modules, config, CancellationToken.None)
                    .ToCoroutine(e => prepError = e);
                Assert.That(prepError, Is.Null, prepError != null ? prepError.ToString() : "");

                Exception startError = null;
                yield return loader.StartEntryAsync(
                        new BootContext
                        {
                            Versions = new CloudIsle.Contracts.VersionManifest
                            {
                                ResVersion = 100,
                                ProtoVersion = 1,
                                ConfigVersion = 100
                            }
                        },
                        "boot",
                        CancellationToken.None)
                    .ToCoroutine(e => startError = e);
                Assert.That(startError, Is.Null, startError != null ? startError.ToString() : "");
                Assert.That(loader.IsReady, Is.True);
                loader.Tick();
                loader.Dispose();
                Assert.That(loader.IsReady, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator Start_DisallowedPhase_Fails()
        {
            var bytes = Encoding.UTF8.GetBytes("return { Start = function(self, phase) return 'ok' end }\n");
            // 最小可编译入口，不依赖完整 BridgeApi 面板。
            string sha = HashUtil.Sha256(bytes);
            var index = GoodIndex(sha);
            var modules = new Dictionary<string, byte[]> { ["entry"] = bytes };
            using (var loader = new LuaLoader())
            {
                yield return loader.PrepareAsync(index, modules, bytes, CancellationToken.None).ToCoroutine();
                Exception error = null;
                yield return loader.StartEntryAsync(new BootContext(), "not-allowed", CancellationToken.None)
                    .ToCoroutine(e => error = e);
                Assert.That(error.Message, Does.Contain("entry-phase-not-allowed"));
            }
        }

        [UnityTest]
        public IEnumerator DuplicateStart_WhileReady_Rejected_DisposeErrorEmpty()
        {
            Dictionary<string, byte[]> modules;
            byte[] config;
            LuaIndex index;
            if (!TryLoadBuiltin(out index, out modules, out config))
            {
                Assert.Ignore("builtin Lua assets missing");
                yield break;
            }

            using (var loader = new LuaLoader())
            {
                yield return loader.PrepareAsync(index, modules, config, CancellationToken.None).ToCoroutine();
                yield return loader.StartEntryAsync(
                        new BootContext
                        {
                            Versions = new CloudIsle.Contracts.VersionManifest
                            {
                                ResVersion = 100,
                                ProtoVersion = 1,
                                ConfigVersion = 100
                            }
                        },
                        "boot",
                        CancellationToken.None)
                    .ToCoroutine();
                Assert.That(loader.IsReady, Is.True);
                Assert.That(loader.CancelRegistered, Is.True);
                Assert.That(loader.AsyncFrames, Is.GreaterThanOrEqualTo(1));

                Exception dup = null;
                yield return loader.StartEntryAsync(new BootContext(), "boot", CancellationToken.None)
                    .ToCoroutine(e => dup = e);
                Assert.That(dup, Is.Not.Null);
                Assert.That(dup.Message, Does.Contain("Prepare").Or.Contain("Ready").Or.Contain("门闸"));

                loader.Dispose();
                Assert.That(loader.DisposeError, Is.Null.Or.Empty);
                Assert.That(loader.State, Is.EqualTo(LuaLoader.HostState.Disposed));
                Assert.That(AsyncLease.LiveSubscriptions, Is.EqualTo(0));
            }
        }

        [UnityTest]
        public IEnumerator Start_CancelAfterPanelCreation_ReleasesOwnedObjects()
        {
            var root = new GameObject("CancelOwnedPanelRoot", typeof(RectTransform));
            BridgeApi.BindPanelRoot(root.transform, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            byte[] script = Encoding.UTF8.GetBytes(
                "return { Start = function(self, phase) " +
                "CS.Game.Framework.Lua.BridgeApi.CreatePanel('cancel_owned','Cancel probe','created before cancellation'); " +
                "return 'ready' end }");
            byte[] config = Encoding.UTF8.GetBytes("{}");
            LuaIndex index = GoodIndex(HashUtil.Sha256(script));
            index.ConfigSha256 = HashUtil.Sha256(config);
            var modules = new Dictionary<string, byte[]> { ["entry"] = script };
            var loader = new LuaLoader();
            var cts = new CancellationTokenSource();
            try
            {
                yield return loader.PrepareAsync(index, modules, config, CancellationToken.None).ToCoroutine();
                UniTask start = loader.StartEntryAsync(new BootContext(), "boot", cts.Token);
                Assert.That(start.GetAwaiter().IsCompleted, Is.False, "须在启动等待中取消");
                Assert.That(root.transform.childCount, Is.EqualTo(1), "Lua入口必须已实际创建对象");
                cts.Cancel();
                Exception cancelled = null;
                yield return start.ToCoroutine(e => cancelled = e);
                Assert.That(cancelled, Is.InstanceOf<OperationCanceledException>());
                yield return null; // Unity Destroy 在帧尾执行。
                Assert.That(root.transform.childCount, Is.EqualTo(0));
                Assert.That(BridgeApi.PanelDump(), Is.Empty);
                Assert.That(AsyncLease.LiveSubscriptions, Is.EqualTo(0));
                Assert.That(loader.DisposeError, Is.Empty);
            }
            finally
            {
                loader.Dispose();
                cts.Dispose();
                BridgeApi.BindPanelRoot(null, null);
                UnityEngine.Object.Destroy(root);
            }
        }

        static LuaIndex GoodIndex(string sha)
        {
            return new LuaIndex
            {
                ResVersion = "100",
                EntryModule = "entry",
                ConfigAddress = "cfg",
                ConfigSha256 = sha,
                BridgeContract = BridgeApi.BridgeContractValue,
                RequiredAppVersion = Application.version,
                ProtoVersion = BridgeApi.RequiredProtoVersion,
                Modules = new[]
                {
                    new LuaIndexModule { Name = "entry", Address = "entry", Sha256 = sha }
                }
            };
        }

        static bool TryLoadBuiltin(out LuaIndex index, out Dictionary<string, byte[]> modules, out byte[] config)
        {
            index = null;
            modules = new Dictionary<string, byte[]>();
            config = null;
            string[] names = { "entry", "panel_base", "proto_case", "async_case" };
            var list = new List<LuaIndexModule>();
            foreach (string name in names)
            {
                string path = System.IO.Path.Combine(Application.dataPath, "Lua", name + ".txt");
                if (!System.IO.File.Exists(path))
                    return false;
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                modules[name] = bytes;
                list.Add(new LuaIndexModule
                {
                    Name = name,
                    Address = name,
                    Sha256 = HashUtil.Sha256(bytes)
                });
            }

            string cfgPath = System.IO.Path.Combine(Application.dataPath, "Lua", "cfg_runtime.txt");
            if (!System.IO.File.Exists(cfgPath))
                return false;
            config = System.IO.File.ReadAllBytes(cfgPath);
            index = new LuaIndex
            {
                ResVersion = "100",
                EntryModule = "entry",
                ConfigAddress = "cfg_runtime",
                ConfigSha256 = HashUtil.Sha256(config),
                BridgeContract = BridgeApi.BridgeContractValue,
                RequiredAppVersion = Application.version,
                ProtoVersion = BridgeApi.RequiredProtoVersion,
                Modules = list.ToArray()
            };
            return true;
        }
    }
}
