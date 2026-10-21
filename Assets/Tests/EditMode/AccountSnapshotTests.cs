using System.IO;
using CloudIsle.Proto;
using Game.Framework.Account;
using NUnit.Framework;
using UnityEngine;
using XLua;

namespace Game.Framework.EditModeTests
{
    public sealed class AccountSnapshotTests
    {
        [Test]
        public void SnapshotCodec_ProtoJson_IncludesPlotsOrderProgress_AndInt64Strings()
        {
            var a = new PlayerFullData
            {
                Info = new PlayerInfo
                {
                    PlayerId = 9007199254740993L,
                    Nickname = "ann\"x",
                    Level = 7,
                    Gold = 9007199254740993L,
                    Gem = 9007199254740994L
                },
                OrderProgress = new OrderProgress
                {
                    DayIndex = 3,
                    DoneCount = 2,
                    RerollCount = 1,
                    Claimed3 = true,
                    NextRefreshTime = 9007199254740995L
                }
            };
            a.Bag.Add(new ItemStack { ItemId = 11, Count = 5 });
            a.Plots.Add(new PlotData
            {
                PlotId = 3,
                State = PlotState.PlotGrowing,
                CropId = 42,
                PlantTime = 1001,
                ReadyTime = 2002,
                Watered = true,
                Fertilized = false
            });

            string j1 = SnapshotCodec.ToLuaJson(a);
            // JsonFormatter 可能在冒号后插入空格
            Assert.That(j1, Does.Match("\"playerId\"\\s*:\\s*\"9007199254740993\""));
            Assert.That(j1, Does.Match("\"gold\"\\s*:\\s*\"9007199254740993\""));
            Assert.That(j1, Does.Contain("orderProgress"));
            Assert.That(j1, Does.Match("\"dayIndex\"\\s*:\\s*3"));
            Assert.That(j1, Does.Match("\"nextRefreshTime\"\\s*:\\s*\"9007199254740995\""));
            Assert.That(j1, Does.Match("\"plotId\"\\s*:\\s*3"));
            Assert.That(j1, Does.Contain("cropId"));
            Assert.That(j1, Does.Contain("ann"));
            // 原消息未被 Format 过程改写
            Assert.That(a.Bag.Count, Is.EqualTo(1));
            Assert.That(a.Plots[0].CropId, Is.EqualTo(42));
            Assert.That(a.OrderProgress.DayIndex, Is.EqualTo(3));

            a.Bag.Clear();
            a.Bag.Add(new ItemStack { ItemId = 9, Count = 1 });
            a.Plots.Clear();
            a.Plots.Add(new PlotData { PlotId = 7, CropId = 99 });
            a.OrderProgress = new OrderProgress { DayIndex = 9, DoneCount = 8 };
            string j2 = SnapshotCodec.ToLuaJson(a);
            Assert.That(j2, Does.Match("\"itemId\"\\s*:\\s*9"));
            Assert.That(j2, Does.Not.Match("\"itemId\"\\s*:\\s*11"));
            Assert.That(j2, Does.Match("\"plotId\"\\s*:\\s*7"));
            Assert.That(j2, Does.Not.Match("\"plotId\"\\s*:\\s*3(?!\\d)"));
            Assert.That(j2, Does.Match("\"dayIndex\"\\s*:\\s*9"));
            Assert.That(j2, Does.Not.Match("\"dayIndex\"\\s*:\\s*3(?!\\d)"));
        }

        [Test]
        public void TokenStore_ReplaceKeepsOld_OnWriteFailure()
        {
            string dir = Path.Combine(Path.GetTempPath(), "FarmingTokenStoreTests", System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new TokenStore(dir);
                Assert.That(store.Save("tok-old"), Is.True);
                Assert.That(store.Read(), Is.EqualTo("tok-old"));

                // 独占锁住正式文件，迫使 Replace 失败；旧值必须仍可读。
                using (var locked = new FileStream(
                           store.PathForTests, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.That(store.Save("tok-new"), Is.False);
                }

                Assert.That(store.Read(), Is.EqualTo("tok-old"));
                Assert.That(store.Save("tok-new"), Is.True);
                Assert.That(store.Read(), Is.EqualTo("tok-new"));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        [Test]
        public void TokenStore_SaveClear_RoundTrip_NoPassword()
        {
            string dir = Path.Combine(Path.GetTempPath(), "FarmingTokenStoreTests", System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new TokenStore(dir);
                Assert.That(store.HasToken(), Is.False);
                Assert.That(store.Save("tok-abc"), Is.True);
                Assert.That(store.HasToken(), Is.True);
                Assert.That(store.Read(), Is.EqualTo("tok-abc"));
                Assert.That(store.Clear(), Is.True);
                Assert.That(store.HasToken(), Is.False);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        [Test]
        public void LuaPlayerModel_TwoApplies_FullProtoJson_Int64Exact()
        {
            var a = new PlayerFullData
            {
                Info = new PlayerInfo
                {
                    PlayerId = 9007199254740993L,
                    Nickname = "Lu\"a",
                    Level = 2,
                    Gold = 9007199254740993L
                },
                OrderProgress = new OrderProgress
                {
                    DayIndex = 4,
                    DoneCount = 1,
                    NextRefreshTime = 9007199254740995L
                }
            };
            a.Bag.Add(new ItemStack { ItemId = 1, Count = 3 });
            a.Plots.Add(new PlotData { PlotId = 8, CropId = 15, Watered = true });

            var env = new LuaEnv();
            try
            {
                env.AddLoader((ref string path) =>
                {
                    string key = path ?? "";
                    if (key.EndsWith(".lua"))
                        key = key.Substring(0, key.Length - 4);
                    if (key.EndsWith(".txt"))
                        key = key.Substring(0, key.Length - 4);
                    string file = Path.Combine(Application.dataPath, "Lua", key.Replace('.', '/') + ".txt");
                    if (!File.Exists(file))
                        file = Path.Combine(Application.dataPath, "Lua", key + ".txt");
                    if (!File.Exists(file))
                        return null;
                    path = file;
                    return File.ReadAllBytes(file);
                });

                string json1 = SnapshotCodec.ToLuaJson(a);
                env.DoString(
                    "local PM = require('PlayerModel')\n"
                    + "PM.applySnapshot([===[" + json1 + "]===])\n"
                    + "assert(PM.playerId == '9007199254740993')\n"
                    + "assert(PM.gold == '9007199254740993')\n"
                    + "assert(PM.orderProgress ~= nil)\n"
                    + "assert(tonumber(PM.orderProgress.dayIndex) == 4)\n"
                    + "assert(tostring(PM.orderProgress.nextRefreshTime) == '9007199254740995')\n"
                    + "assert(#PM.bag == 1 and PM.bag[1].itemId == 1)\n"
                    + "assert(#PM.plots == 1 and PM.plots[1].plotId == 8)\n"
                    + "assert(PM.plots[1].cropId == 15)\n");

                a.Bag.Clear();
                a.Bag.Add(new ItemStack { ItemId = 9, Count = 1 });
                a.Plots.Clear();
                a.Plots.Add(new PlotData { PlotId = 1, CropId = 2 });
                a.OrderProgress = new OrderProgress { DayIndex = 9, NextRefreshTime = 9007199254740996L };
                string json2 = SnapshotCodec.ToLuaJson(a);
                env.DoString(
                    "local PM = require('PlayerModel')\n"
                    + "PM.applySnapshot([===[" + json2 + "]===])\n"
                    + "assert(#PM.bag == 1 and PM.bag[1].itemId == 9)\n"
                    + "assert(#PM.plots == 1 and PM.plots[1].plotId == 1)\n"
                    + "assert(tonumber(PM.orderProgress.dayIndex) == 9)\n"
                    + "assert(tostring(PM.orderProgress.nextRefreshTime) == '9007199254740996')\n");

                Assert.That(a.Plots[0].PlotId, Is.EqualTo(1));
                Assert.That(a.OrderProgress.DayIndex, Is.EqualTo(9));
            }
            finally
            {
                env.Dispose();
            }
        }
    }
}
