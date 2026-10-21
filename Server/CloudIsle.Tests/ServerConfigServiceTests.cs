using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CloudIsle.Config;

namespace CloudIsle.Tests
{
    public sealed class ServerConfigServiceTests
    {
        /// <summary>正式目录能读到版本 100 的多条 Global，并且和导出原文一致。</summary>
        [Test]
        public void Load_Version100_ReadsExportedGlobals()
        {
            var service = new CloudIsle.Server.Config.ServerConfigService();
            service.Load(OfficialDirectory(), 100);

            Assert.That(service.GetGlobal("bag_init_slots"), Is.EqualTo("60"));
            Assert.That(service.GetGlobal("water_ratio"), Is.EqualTo("0.1"));
            Assert.That(service.GetGlobal("bag_expand_costs"), Is.EqualTo("[500,1000,2000,4000,8000]"));
            Assert.That(service.GetGlobal("new_player_gem"), Is.EqualTo("300"));
            Assert.That(service.GetGlobal("new_player_ticket"), Is.EqualTo("5"));
        }

        /// <summary>未知键必须抛异常，不能返回空字符串。</summary>
        [Test]
        public void GetGlobal_UnknownKey_Throws()
        {
            var service = new CloudIsle.Server.Config.ServerConfigService();
            service.Load(OfficialDirectory(), 100);

            Assert.Throws<KeyNotFoundException>(() => service.GetGlobal("m1_patch_message"));
            Assert.Throws<KeyNotFoundException>(() => service.GetGlobal("no_such_key"));
        }

        /// <summary>快照持有自己的字典，原字典和强制转换都不能改它。</summary>
        [Test]
        public void Snapshot_OwnsGlobals_CastCannotMutate()
        {
            var source = new Dictionary<string, string> { { "bag_init_slots", "60" } };
            var snapshot = new ConfigSnapshot(100, source);
            source["bag_init_slots"] = "1";
            source["extra"] = "2";

            Assert.That(snapshot.Version, Is.EqualTo(100));
            Assert.That(snapshot.Globals["bag_init_slots"], Is.EqualTo("60"));
            Assert.That(snapshot.Globals.ContainsKey("extra"), Is.False);
            Assert.Throws<InvalidCastException>(() =>
            {
                ((Dictionary<string, string>)snapshot.Globals)["bag_init_slots"] = "9";
            });
        }

        /// <summary>错误版本、缺文件、坏 JSON、哈希破坏、缺必需键都失败，正式目录不变。</summary>
        [Test]
        public void Load_CorruptCopies_FailWithoutChangingOfficial()
        {
            string before = FileHash(Path.Combine(OfficialDirectory(), "tbglobalkv.json"));
            using (var workspace = new Workspace())
            {
                AssertMessage(workspace.Copy(), "要求的配置版本必须大于 0", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 0);
                });

                AssertMessage(workspace.Copy(), "配置版本不匹配", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 99);
                });

                string missing = workspace.Copy();
                File.Delete(Path.Combine(missing, "tbplot.json"));
                AssertMessage(missing, "缺少配置文件", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });

                string badJson = workspace.Copy();
                WriteAndRetarget(badJson, "tbglobalkv.json", Encoding.UTF8.GetBytes("{"));
                AssertMessage(badJson, "Global 表不是合法 JSON", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });

                string badHash = workspace.Copy();
                string itemPath = Path.Combine(badHash, "tbitem.json");
                byte[] item = File.ReadAllBytes(itemPath);
                item[0] ^= 0x01;
                File.WriteAllBytes(itemPath, item);
                AssertMessage(badHash, "配置文件哈希不符", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });

                // 只改清单 length，文件字节不变 → 长度不符（哈希仍可能偶然一致前先拦长度）。
                string badLength = workspace.Copy();
                string manifestPath = Path.Combine(badLength, ConfigSnapshotReader.ManifestFileName);
                string manifestText = File.ReadAllText(manifestPath, Encoding.UTF8);
                string marker = "\"name\": \"tbplot.json\", \"sha256\": \"";
                int hashStart = manifestText.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                string plotHash = manifestText.Substring(hashStart, 64);
                int oldLen = LengthInManifest(manifestText, "tbplot.json");
                int fakeLen = oldLen + 1;
                manifestText = manifestText.Replace(
                    "\"name\": \"tbplot.json\", \"sha256\": \"" + plotHash + "\", \"length\": " + oldLen.ToString(),
                    "\"name\": \"tbplot.json\", \"sha256\": \"" + plotHash + "\", \"length\": " + fakeLen.ToString(),
                    StringComparison.Ordinal);
                File.WriteAllText(manifestPath, manifestText, new UTF8Encoding(false));
                AssertMessage(badLength, "配置文件长度不符", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });

                string missingKey = workspace.Copy();
                string kv = File.ReadAllText(Path.Combine(missingKey, "tbglobalkv.json"), Encoding.UTF8);
                kv = kv.Replace("\"bag_init_slots\"", "\"bag_init_slots_removed\"", StringComparison.Ordinal);
                WriteAndRetarget(missingKey, "tbglobalkv.json", Encoding.UTF8.GetBytes(kv));
                AssertMessage(missingKey, "缺少必需 Global 键: bag_init_slots", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });

                string forbidden = workspace.Copy();
                string withPatch = kv.Replace(
                    "\"bag_init_slots_removed\"",
                    "\"bag_init_slots\"",
                    StringComparison.Ordinal);
                withPatch = withPatch.Replace(
                    "\"new_player_ticket\"",
                    "\"m1_patch_message\"",
                    StringComparison.Ordinal);
                WriteAndRetarget(forbidden, "tbglobalkv.json", Encoding.UTF8.GetBytes(withPatch));
                AssertMessage(forbidden, "不能包含 m1_patch_message", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });

                string escape = workspace.Copy();
                string manifest = File.ReadAllText(Path.Combine(escape, ConfigSnapshotReader.ManifestFileName), Encoding.UTF8);
                manifest = manifest.Replace("\"tbplot.json\"", "\"../secret.json\"", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(escape, ConfigSnapshotReader.ManifestFileName), manifest, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(workspace.Root, "secret.json"), "secret", Encoding.UTF8);
                AssertMessage(escape, "非法配置文件名", directory =>
                {
                    var service = new CloudIsle.Server.Config.ServerConfigService();
                    service.Load(directory, 100);
                });
            }

            Assert.That(FileHash(Path.Combine(OfficialDirectory(), "tbglobalkv.json")), Is.EqualTo(before));
        }

        /// <summary>第一次失败保持未加载；成功后再失败仍返回旧值。</summary>
        [Test]
        public void Load_Failure_KeepsPreviousSnapshot()
        {
            using (var workspace = new Workspace())
            {
                var service = new CloudIsle.Server.Config.ServerConfigService();
                string broken = workspace.Copy();
                File.Delete(Path.Combine(broken, "tbplot.json"));
                Assert.Throws<ConfigLoadException>(() => service.Load(broken, 100));
                Assert.Throws<InvalidOperationException>(() => service.GetGlobal("bag_init_slots"));

                service.Load(OfficialDirectory(), 100);
                Assert.Throws<ConfigLoadException>(() => service.Load(broken, 100));
                Assert.Throws<ConfigLoadException>(() => service.Load(OfficialDirectory(), 101));
                Assert.That(service.GetGlobal("bag_init_slots"), Is.EqualTo("60"));
                Assert.That(service.GetGlobal("fertilize_ratio"), Is.EqualTo("0.3"));
            }
        }

        /// <summary>成功加载和失败加载并行结束后，快照仍是版本 100。</summary>
        [Test]
        public void Load_ConcurrentFailure_DoesNotRollBack()
        {
            var service = new CloudIsle.Server.Config.ServerConfigService();
            string official = OfficialDirectory();
            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() =>
                {
                    if (index % 2 == 0)
                        service.Load(official, 100);
                    else
                        Assert.Throws<ConfigLoadException>(() => service.Load(official, 99));
                });
            }

            Task.WaitAll(tasks);
            Assert.That(service.GetGlobal("bag_init_slots"), Is.EqualTo("60"));
            Assert.That(service.GetGlobal("order_daily_count"), Is.EqualTo("6"));
        }

        static void AssertMessage(string directory, string fragment, Action<string> load)
        {
            ConfigLoadException error = Assert.Throws<ConfigLoadException>(() => load(directory));
            Assert.That(error.Message, Does.Contain(fragment));
        }

        static void WriteAndRetarget(string directory, string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            string manifestPath = Path.Combine(directory, ConfigSnapshotReader.ManifestFileName);
            string manifest = File.ReadAllText(manifestPath, Encoding.UTF8);
            string hash = Sha256(bytes);
            string oldHash = HashInManifest(manifest, name);
            int oldLength = LengthInManifest(manifest, name);
            manifest = manifest.Replace(oldHash, hash, StringComparison.Ordinal);
            manifest = manifest.Replace(
                "\"" + name + "\", \"sha256\": \"" + hash + "\", \"length\": " + oldLength.ToString(),
                "\"" + name + "\", \"sha256\": \"" + hash + "\", \"length\": " + bytes.Length.ToString(),
                StringComparison.Ordinal);
            File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        }

        static string HashInManifest(string manifest, string name)
        {
            string marker = "\"name\": \"" + name + "\", \"sha256\": \"";
            int start = manifest.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("清单里没有 " + name);
            start += marker.Length;
            return manifest.Substring(start, 64);
        }

        static int LengthInManifest(string manifest, string name)
        {
            string hash = HashInManifest(manifest, name);
            string marker = "\"name\": \"" + name + "\", \"sha256\": \"" + hash + "\", \"length\": ";
            int start = manifest.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            int end = manifest.IndexOf('}', start);
            return int.Parse(manifest.Substring(start, end - start).Trim());
        }

        static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        static string FileHash(string path)
        {
            return Sha256(File.ReadAllBytes(path));
        }

        static string OfficialDirectory()
        {
            return Path.Combine(FindRepo(), "Server", "ConfigLuban");
        }

        static string FindRepo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string manifest = Path.Combine(dir.FullName, "Server", "ConfigLuban", "config_manifest.json");
                if (File.Exists(manifest) && Directory.Exists(Path.Combine(dir.FullName, "Assets")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new InvalidOperationException("找不到工程根目录");
        }

        sealed class Workspace : IDisposable
        {
            public Workspace()
            {
                Root = Path.Combine(FindRepo(), "Temp", "t42-server-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string Root { get; }

            public string Copy()
            {
                string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                foreach (string file in Directory.GetFiles(OfficialDirectory()))
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                return directory;
            }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
        }
    }
}
