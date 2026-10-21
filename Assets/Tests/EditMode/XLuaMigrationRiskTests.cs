using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Framework.Boot;
using Game.Framework.Lua;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.EditModeTests
{
    /// <summary>
    /// 主工程 xLua 迁移风险项的 EditMode 真实验证（非零用例）。
    /// </summary>
    public sealed class XLuaMigrationRiskTests
    {
        [Test]
        public void ValidateCandidate_RejectsBadBridgeContract_AndKeepsSemantics()
        {
            var good = MakeCatalog(BridgeApi.BridgeContractValue, Application.version, 1);
            var loaded = MakeLoaded(good);
            Assert.That(HotfixLoader.ValidateCandidate(good, loaded, Encoding.UTF8.GetBytes("{}")), Is.Null);

            var badBridge = MakeCatalog("wrong-bridge", Application.version, 1);
            var err = HotfixLoader.ValidateCandidate(badBridge, MakeLoaded(badBridge), Encoding.UTF8.GetBytes("{}"));
            Assert.That(err, Does.Contain("bridgeContract-mismatch"));
        }

        [Test]
        public void ValidateCandidate_RejectsDuplicateModuleMapping()
        {
            var catalog = MakeCatalog(HotfixLoader.BridgeContract, Application.version, HotfixLoader.HostProtoVersion);
            catalog.modules = new[]
            {
                catalog.modules[0],
                new CatalogModule { name = catalog.modules[0].name, address = "other", sha256 = catalog.modules[0].sha256 }
            };
            var err = HotfixLoader.ValidateCandidate(catalog, MakeLoaded(catalog), Encoding.UTF8.GetBytes("{}"));
            Assert.That(err, Does.Contain("duplicate-module-name"));
        }

        [Test]
        public void ValidateCandidate_DoesNotTreatIntegerContractAsBridgeCompat()
        {
            // ProtoBridge.Contract 与 BridgeContract 同源比较不能冒充候选兼容：必须读 catalog 字段。
            Assert.That(ProtoBridge.Contract, Is.Not.EqualTo(HotfixLoader.BridgeContract));
            var missing = MakeCatalog(HotfixLoader.BridgeContract, Application.version, HotfixLoader.HostProtoVersion);
            missing.bridgeContract = "";
            var err = HotfixLoader.ValidateCandidate(missing, MakeLoaded(missing), Encoding.UTF8.GetBytes("{}"));
            Assert.That(err, Does.Contain("bridgeContract-empty"));
        }

        [Test]
        public void DriveStack_SurfacesNestedEnumeratorException_AndDisposes()
        {
            Exception caught = null;
            var disposed = false;
            IEnumerator Body()
            {
                yield return Nested(false);
            }

            IEnumerator Nested(bool _)
            {
                try
                {
                    yield return null;
                    throw new InvalidOperationException("inject-nested-enumerator");
                }
                finally
                {
                    disposed = true;
                }
            }

            var driver = HotfixLoader.DriveStack(Body(), e => caught = e);
            while (driver.MoveNext())
            {
                // 推进至结束
            }

            Assert.That(caught, Is.Not.Null);
            Assert.That(caught.Message, Does.Contain("inject-nested-enumerator"));
            Assert.That(disposed, Is.True);
        }

        [Test]
        public void CommitActiveIndex_ReplaceFailure_PreservesExistingIndex()
        {
            var cache = Path.Combine(Application.temporaryCachePath, "xlua-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cache);
            var finalPath = Path.Combine(cache, "active-index.json");
            var original = "{\"activeVersion\":\"100\",\"bridgeContract\":\"m1-bridge-0.3\"}\n";
            File.WriteAllText(finalPath, original, new UTF8Encoding(false));

            var catalog = MakeCatalog(HotfixLoader.BridgeContract, Application.version, HotfixLoader.HostProtoVersion);
            catalog.resVersion = "101";

            HotfixLoader.IndexReplaceOverride = (temp, dest, backup) => false;
            try
            {
                var err = HotfixLoader.TryCommitActiveIndexForTest(cache, catalog, injectFail: false);
                Assert.That(err, Does.Contain("index-commit-failed"));
                Assert.That(File.ReadAllText(finalPath), Is.EqualTo(original));

                var errInject = HotfixLoader.TryCommitActiveIndexForTest(cache, catalog, injectFail: true);
                Assert.That(errInject, Does.Contain("inject-index-fail"));
                Assert.That(File.ReadAllText(finalPath), Is.EqualTo(original));
            }
            finally
            {
                HotfixLoader.IndexReplaceOverride = null;
                try
                {
                    Directory.Delete(cache, true);
                }
                catch
                {
                    // ignore
                }
            }
        }

        [Test]
        public void AtomicIndexReplace_SucceedsWithoutDeletingFirst()
        {
            var cache = Path.Combine(Application.temporaryCachePath, "xlua-index-ok-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cache);
            var finalPath = Path.Combine(cache, "active-index.json");
            File.WriteAllText(finalPath, "old\n", new UTF8Encoding(false));
            var tempPath = finalPath + ".tmp";
            File.WriteAllText(tempPath, "new\n", new UTF8Encoding(false));
            var backup = finalPath + ".bak";
            File.Replace(tempPath, finalPath, backup);
            Assert.That(File.ReadAllText(finalPath), Is.EqualTo("new\n"));
            Assert.That(File.Exists(finalPath), Is.True);
            Directory.Delete(cache, true);
        }

        [Test]
        public void AppVersionAndBridgeContract_AreDistinct()
        {
            Assert.That(Application.version, Is.EqualTo("0.3.0"));
            Assert.That(HotfixLoader.BridgeContract, Is.EqualTo("m1-bridge-0.3"));
            Assert.That(BridgeApi.AppVersion(), Is.EqualTo(Application.version));
            Assert.That(BridgeApi.BridgeContract(), Is.EqualTo("m1-bridge-0.3"));
            Assert.That(HotfixLoader.HostProtoVersion, Is.EqualTo(1));
        }

        static RuntimeCatalog MakeCatalog(string bridge, string app, int proto)
        {
            var bytes = Encoding.UTF8.GetBytes("return {}\n");
            var sha = HashUtil.Sha256(bytes);
            return new RuntimeCatalog
            {
                resVersion = "100",
                entryModule = "entry",
                configAddress = "cfg_runtime",
                configSha256 = sha,
                bridgeContract = bridge,
                requiredAppVersion = app,
                protoVersion = proto,
                modules = new[]
                {
                    new CatalogModule { name = "entry", address = "entry", sha256 = sha }
                }
            };
        }

        static List<HotfixLoader.LoadedModule> MakeLoaded(RuntimeCatalog catalog)
        {
            var list = new List<HotfixLoader.LoadedModule>();
            foreach (var m in catalog.modules)
                list.Add(new HotfixLoader.LoadedModule { Name = m.name, Bytes = Encoding.UTF8.GetBytes("return {}\n") });
            return list;
        }
    }
}
