using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Game.Framework.Network;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class OpIdStoreTests
    {
        [Test]
        public void Next_IncrementsPerPlayer_AndReadLastDoesNotAllocate()
        {
            using (var dir = new IsolatedDirectory())
            {
                var store = new OpIdStore(dir.FullPath);
                Assert.AreEqual(0, store.ReadLast(11));
                Assert.AreEqual(0, Directory.GetFiles(dir.FullPath).Length);

                long first = store.Next(11);
                long other = store.Next(22);
                long second = store.Next(11);

                Assert.AreEqual(1, first);
                Assert.AreEqual(1, other);
                Assert.AreEqual(2, second);
                Assert.Greater(first, 0);
                Assert.Greater(second, first);
                Assert.AreEqual(2, store.ReadLast(11));
                Assert.AreEqual(1, store.ReadLast(22));
                Assert.AreEqual(0, store.ReadLast(33));
                Assert.AreEqual(2, Directory.GetFiles(dir.FullPath).Length);
            }
        }

        [Test]
        public void RecreatedStore_ReadsLastAndContinues()
        {
            using (var dir = new IsolatedDirectory())
            {
                var first = new OpIdStore(dir.FullPath);
                first.Next(7);
                long last = first.Next(7);

                var second = new OpIdStore(dir.FullPath);
                Assert.AreEqual(last, second.ReadLast(7));
                Assert.AreEqual(last + 1, second.Next(7));
                Assert.Greater(second.ReadLast(7), last);
            }
        }

        [Test]
        public void SamePlayer_ParallelNext_HasNoDuplicates()
        {
            using (var dir = new IsolatedDirectory())
            {
                var store = new OpIdStore(dir.FullPath);
                var ids = new long[32];
                Parallel.For(0, ids.Length, i => ids[i] = store.Next(5));

                var seen = new HashSet<long>();
                for (int i = 0; i < ids.Length; i++)
                    Assert.IsTrue(seen.Add(ids[i]));
                Assert.AreEqual(1, Min(ids));
                Assert.AreEqual(ids.Length, Max(ids));
            }
        }

        [Test]
        public void TwoStores_SameDirectory_DoNotRepeat()
        {
            using (var dir = new IsolatedDirectory())
            {
                var first = new OpIdStore(dir.FullPath);
                var second = new OpIdStore(dir.FullPath + Path.DirectorySeparatorChar);
                var ids = new long[40];
                Parallel.For(0, ids.Length, i =>
                {
                    OpIdStore store = i % 2 == 0 ? first : second;
                    ids[i] = store.Next(42);
                });

                var seen = new HashSet<long>();
                for (int i = 0; i < ids.Length; i++)
                    Assert.IsTrue(seen.Add(ids[i]));
                Assert.AreEqual(1, Min(ids));
                Assert.AreEqual(ids.Length, Max(ids));
                Assert.AreEqual(ids.Length, first.ReadLast(42));
                Assert.AreEqual(ids.Length, second.ReadLast(42));
            }
        }

        [Test]
        public void DifferentPlayers_AllocateIndependently()
        {
            using (var dir = new IsolatedDirectory())
            {
                var store = new OpIdStore(dir.FullPath);
                var seen = new long[2][];
                seen[0] = new long[8];
                seen[1] = new long[8];
                Parallel.For(0, 16, i =>
                {
                    int playerSlot = i % 2;
                    long id = store.Next(100 + playerSlot);
                    seen[playerSlot][i / 2] = id;
                });

                AssertUniqueRange(seen[0]);
                AssertUniqueRange(seen[1]);
            }
        }

        [Test]
        public void WriteFailure_ThrowsWithoutIssuing_ThenContinues()
        {
            using (var dir = new IsolatedDirectory())
            {
                var store = new OpIdStore(dir.FullPath);
                long issued = store.Next(9);
                string file = PlayerFile(dir.FullPath);
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Throws<IOException>(() => store.Next(9));

                Assert.AreEqual(issued, store.ReadLast(9));
                Assert.AreEqual(issued + 1, store.Next(9));
                Assert.AreEqual(issued + 1, store.ReadLast(9));
            }
        }

        [Test]
        public void CorruptFile_DoesNotResetToZero()
        {
            using (var dir = new IsolatedDirectory())
            {
                var store = new OpIdStore(dir.FullPath);
                long issued = store.Next(4);
                File.WriteAllText(PlayerFile(dir.FullPath), "broken", new UTF8Encoding(false));

                Assert.Throws<InvalidDataException>(() => store.ReadLast(4));
                Assert.Throws<InvalidDataException>(() => store.Next(4));
                Assert.AreEqual("broken", File.ReadAllText(PlayerFile(dir.FullPath)));
                Assert.AreNotEqual(0, issued);
            }
        }

        [Test]
        public void LongMax_RefusesAnotherIdWithoutWrap()
        {
            using (var dir = new IsolatedDirectory())
            {
                var store = new OpIdStore(dir.FullPath);
                store.Next(8);
                string text = (long.MaxValue - 1).ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(PlayerFile(dir.FullPath), text, new UTF8Encoding(false));

                Assert.AreEqual(long.MaxValue, store.Next(8));
                Assert.AreEqual(long.MaxValue, store.ReadLast(8));
                Assert.Throws<InvalidOperationException>(() => store.Next(8));
                Assert.AreEqual(long.MaxValue, store.ReadLast(8));
                Assert.AreEqual(
                    long.MaxValue.ToString(CultureInfo.InvariantCulture),
                    File.ReadAllText(PlayerFile(dir.FullPath)).Trim());
            }
        }

        static void AssertUniqueRange(long[] ids)
        {
            var seen = new HashSet<long>();
            for (int i = 0; i < ids.Length; i++)
                Assert.IsTrue(seen.Add(ids[i]));
            Assert.AreEqual(1, Min(ids));
            Assert.AreEqual(ids.Length, Max(ids));
        }

        static long Min(long[] ids)
        {
            long min = ids[0];
            for (int i = 1; i < ids.Length; i++)
            {
                if (ids[i] < min)
                    min = ids[i];
            }

            return min;
        }

        static long Max(long[] ids)
        {
            long max = ids[0];
            for (int i = 1; i < ids.Length; i++)
            {
                if (ids[i] > max)
                    max = ids[i];
            }

            return max;
        }

        static string PlayerFile(string directory)
        {
            string[] files = Directory.GetFiles(directory, "*.opid");
            Assert.AreEqual(1, files.Length);
            return files[0];
        }

        sealed class IsolatedDirectory : IDisposable
        {
            public IsolatedDirectory()
            {
                string root = Path.Combine(Path.GetTempPath(), "FarmingOpIdStoreTests");
                FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(FullPath);
            }

            public string FullPath { get; }

            public void Dispose()
            {
                string full = Path.GetFullPath(FullPath);
                string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingOpIdStoreTests"));
                string prefix = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                    + System.IO.Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("拒绝删除测试目录之外的路径");
                if (Directory.Exists(full))
                    Directory.Delete(full, true);
            }
        }
    }
}
