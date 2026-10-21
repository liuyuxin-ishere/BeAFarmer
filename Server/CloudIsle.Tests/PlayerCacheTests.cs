using System.Globalization;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Players;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

public sealed class PlayerCacheTests
{
    /// <summary>动作结束后，派生任务继承的异步上下文不能继续访问玩家状态。</summary>
    [Test]
    public async Task EscapedChild_AfterActionEnds_CannotModifyCache()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 42);
        var cache = new PlayerCache(new PlayerStore(store));
        using var queue = new PlayerQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;
        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            child = Task.Run(async () =>
            {
                await release.Task;
                Assert.That(PlayerQueue.IsCurrentPlayer(11), Is.False);
                Assert.Throws<InvalidOperationException>(() => cache.ReplaceNickname(11, "escaped"));
            });
        }, CancellationToken.None);
        release.TrySetResult();
        await child!.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.EnqueueAsync(11, async ct =>
        {
            Assert.That((await cache.GetAsync(11, ct)).Data.Info.Nickname, Is.EqualTo("Ann"));
        }, CancellationToken.None);
    }

    /// <summary>错误上下文释放失败后，仍能回到玩家队列正确释放同一句柄。</summary>
    [Test]
    public async Task Hold_DisposeOutsideQueue_DoesNotConsumeHandle()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 42);
        var cache = new PlayerCache(new PlayerStore(store));
        using var queue = new PlayerQueue();
        IDisposable? hold = null;
        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            hold = cache.AcquireHold(11);
        }, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => hold!.Dispose());
        await queue.EnqueueAsync(11, _ =>
        {
            Assert.Throws<InvalidOperationException>(() => cache.Remove(11));
            hold!.Dispose();
            hold.Dispose();
            cache.Remove(11);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.That(cache.Count, Is.Zero);
    }

    /// <summary>旧版本保存失败时，基础列和完整快照都保持新版本。</summary>
    [Test]
    public async Task StaleSnapshot_CannotOverwriteNewerSavedRevision()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 42);
        var players = new PlayerStore(store);
        PlayerFullData data = (await players.LoadAsync(11, CancellationToken.None)).Data;
        data.Info.Nickname = "new";
        await players.SaveAsync(new PlayerState(11, data, 2, 0), CancellationToken.None);
        data.Info.Nickname = "stale";
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await players.SaveAsync(new PlayerState(11, data, 1, 0), CancellationToken.None));
        PlayerState reloaded = await players.LoadAsync(11, CancellationToken.None);
        Assert.That(reloaded.Data.Info.Nickname, Is.EqualTo("new"));
        Assert.That(reloaded.SavedRevision, Is.EqualTo(2));
    }

    [Test]
    public async Task FirstLoad_HitsCacheWithoutSecondQuery()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 42);
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();

        PlayerState? first = null;
        PlayerState? second = null;
        await queue.EnqueueAsync(11, async ct =>
        {
            first = await cache.GetAsync(11, ct);
            second = await cache.GetAsync(11, ct);
        }, CancellationToken.None);

        Assert.That(players.LoadQueryCount, Is.EqualTo(1));
        Assert.That(second!.Data, Is.Not.SameAs(first!.Data));
        Assert.That(second.Data.Info.Nickname, Is.EqualTo("Ann"));
        Assert.That(second.Revision, Is.EqualTo(first.Revision));
        Assert.That(second.SavedRevision, Is.EqualTo(first.SavedRevision));
        Assert.That(first.Dirty, Is.False);
    }

    [Test]
    public async Task DifferentPlayers_DoNotShareData()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 1);
        Seed(dir, 12, 2, "Bea", 2);
        var cache = new PlayerCache(new PlayerStore(store));
        using var queue = new PlayerQueue();
        var bothInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inside = 0;

        Task left = queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            if (Interlocked.Increment(ref inside) == 2)
                bothInside.TrySetResult();
            await bothInside.Task;
            cache.ReplaceNickname(11, "Ann2");
        }, CancellationToken.None);
        Task right = queue.EnqueueAsync(12, async ct =>
        {
            await cache.GetAsync(12, ct);
            if (Interlocked.Increment(ref inside) == 2)
                bothInside.TrySetResult();
            await bothInside.Task;
            cache.ReplaceNickname(12, "Bea2");
        }, CancellationToken.None);
        await Task.WhenAll(left, right);

        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState state = await cache.GetAsync(11, ct);
            Assert.That(state.Data.Info.Nickname, Is.EqualTo("Ann2"));
            Assert.That(state.Revision, Is.EqualTo(1));
        }, CancellationToken.None);
        await queue.EnqueueAsync(12, async ct =>
        {
            PlayerState state = await cache.GetAsync(12, ct);
            Assert.That(state.Data.Info.Nickname, Is.EqualTo("Bea2"));
            Assert.That(state.Revision, Is.EqualTo(1));
        }, CancellationToken.None);
    }

    [Test]
    public async Task OutsideQueue_AndWrongPlayer_FailWithoutChangingCache()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 1);
        Seed(dir, 12, 2, "Bea", 2);
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();

        InvalidOperationException outside = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cache.GetAsync(11, CancellationToken.None));
        Assert.That(outside!.Message, Does.Contain("队列"));
        Assert.That(players.LoadQueryCount, Is.Zero);
        Assert.That(cache.Count, Is.Zero);

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            InvalidOperationException wrong = Assert.Throws<InvalidOperationException>(() => cache.MarkDirty(12));
            Assert.That(wrong.Message, Does.Contain("12"));
            Assert.Throws<InvalidOperationException>(() => cache.Remove(12));
            Assert.Throws<InvalidOperationException>(() => cache.ReplaceNickname(12, "stolen"));
        }, CancellationToken.None);

        await queue.EnqueueAsync(12, async ct =>
        {
            PlayerState state = await cache.GetAsync(12, ct);
            Assert.That(state.Data.Info.Nickname, Is.EqualTo("Bea"));
            Assert.That(state.Revision, Is.Zero);
        }, CancellationToken.None);
        Assert.That(cache.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ConcurrentEdits_OnSamePlayer_KeepFinalNicknameAndRevision()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 1);
        var cache = new PlayerCache(new PlayerStore(store));
        using var queue = new PlayerQueue();
        await queue.EnqueueAsync(11, async ct => { await cache.GetAsync(11, ct); }, CancellationToken.None);

        int last = -1;
        var edits = new Task[6];
        for (int i = 0; i < edits.Length; i++)
        {
            int name = i;
            edits[i] = Task.Run(async () => await queue.EnqueueAsync(11, _ =>
            {
                cache.ReplaceNickname(11, "n" + name.ToString(CultureInfo.InvariantCulture));
                Volatile.Write(ref last, name);
                return Task.CompletedTask;
            }, CancellationToken.None));
        }

        await Task.WhenAll(edits);
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState state = await cache.GetAsync(11, ct);
            Assert.That(state.Revision, Is.EqualTo(edits.Length));
            Assert.That(state.Data.Info.Nickname, Is.EqualTo("n" + Volatile.Read(ref last).ToString(CultureInfo.InvariantCulture)));
            Assert.That(state.Dirty, Is.True);
        }, CancellationToken.None);
    }

    [Test]
    public async Task ReturnedSnapshot_IsIndependentFromCache()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 1);
        var cache = new PlayerCache(new PlayerStore(store));
        using var queue = new PlayerQueue();

        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState returned = await cache.GetAsync(11, ct);
            returned.Data.Info.Nickname = "changed";
            returned.Data.Bag.Add(new ItemStack { ItemId = 9, Count = 4 });
            PlayerState again = await cache.GetAsync(11, ct);
            Assert.That(again.Data.Info.Nickname, Is.EqualTo("Ann"));
            Assert.That(again.Data.Bag, Is.Empty);
            Assert.That(again.Data, Is.Not.SameAs(returned.Data));
            Assert.That(again.Revision, Is.EqualTo(returned.Revision));
        }, CancellationToken.None);
    }

    [Test]
    public async Task FullSnapshot_RoundTripsCollectionsAndBaseColumns()
    {
        using var dir = new IsolatedDirectory();
        string database = DatabasePath(dir);
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 42);
        var players = new PlayerStore(store);
        PlayerFullData written = SampleData(11);

        await players.SaveAsync(new PlayerState(11, written, 4, 0), CancellationToken.None);
        store.Dispose();

        using var reopened = new SqliteStore();
        reopened.Initialize(database);
        var loaded = await new PlayerStore(reopened).LoadAsync(11, CancellationToken.None);

        Assert.That(loaded.Data, Is.EqualTo(written));
        Assert.That(loaded.Revision, Is.EqualTo(4));
        Assert.That(loaded.SavedRevision, Is.EqualTo(4));
        Assert.That(loaded.Dirty, Is.False);
        using var connection = Open(database);
        Assert.That(ScalarString(connection, "SELECT nickname FROM player WHERE id = 11;"), Is.EqualTo("Ann2"));
        Assert.That(ScalarLong(connection, "SELECT gold FROM player WHERE id = 11;"), Is.EqualTo(90));
        Assert.That(ScalarLong(connection, "SELECT gem FROM player WHERE id = 11;"), Is.EqualTo(8));
        Assert.That(ScalarLong(connection, "SELECT level FROM player WHERE id = 11;"), Is.EqualTo(3));
        Assert.That(ScalarLong(connection, "SELECT bag_slots FROM player WHERE id = 11;"), Is.EqualTo(70));
        Assert.That(ScalarLong(connection, "SELECT guide_step FROM player WHERE id = 11;"), Is.EqualTo(2));
        Assert.That(ScalarLong(connection, "SELECT last_daily_reset FROM player WHERE id = 11;"), Is.EqualTo(42));
        Assert.That(ScalarLong(connection, "SELECT account_id FROM player WHERE id = 11;"), Is.EqualTo(1));
        Assert.That(Count(connection, "account"), Is.EqualTo(1));
    }

    [Test]
    public async Task FailedTransaction_LeavesNoPartialSnapshotAndKeepsDirty()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 42);
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "New");
        }, CancellationToken.None);
        using (var connection = Open(DatabasePath(dir)))
        {
            Execute(connection, """
                CREATE TRIGGER player_snapshot_fail BEFORE INSERT ON player_snapshot
                BEGIN
                  SELECT RAISE(ABORT, 'snapshot-fail');
                END;
                """);
        }

        SqliteException? error = null;
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState snapshot = await cache.GetAsync(11, ct);
            try
            {
                await players.SaveAsync(snapshot, ct);
            }
            catch (SqliteException ex)
            {
                error = ex;
            }

            PlayerState still = await cache.GetAsync(11, ct);
            Assert.That(still.Dirty, Is.True);
            Assert.That(still.Data.Info.Nickname, Is.EqualTo("New"));
            Assert.That(still.SavedRevision, Is.Zero);
        }, CancellationToken.None);

        Assert.That(error, Is.Not.Null);
        using var check = Open(DatabasePath(dir));
        Assert.That(ScalarString(check, "SELECT nickname FROM player WHERE id = 11;"), Is.EqualTo("Ann"));
        Assert.That(ScalarLong(check, "SELECT last_daily_reset FROM player WHERE id = 11;"), Is.EqualTo(42));
        Assert.That(Count(check, "player_snapshot"), Is.Zero);
    }

    [Test]
    public async Task Remove_RejectsDirtyOrHold_AndReloadsAfterCleanRemoval()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 7);
        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();

        await queue.EnqueueAsync(11, async ct =>
        {
            await cache.GetAsync(11, ct);
            IDisposable hold = cache.AcquireHold(11);
            InvalidOperationException held = Assert.Throws<InvalidOperationException>(() => cache.Remove(11));
            Assert.That(held.Message, Does.Contain("持有"));
            hold.Dispose();
            cache.Remove(11);
            Assert.That(cache.Count, Is.Zero);

            await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Kept");
            InvalidOperationException dirty = Assert.Throws<InvalidOperationException>(() => cache.Remove(11));
            Assert.That(dirty.Message, Does.Contain("未保存"));

            PlayerState snapshot = await cache.GetAsync(11, ct);
            cache.ReplaceNickname(11, "Later");
            await players.SaveAsync(snapshot, ct);
            cache.ConfirmSaved(11, snapshot.Revision);
            PlayerState afterOlderSave = await cache.GetAsync(11, ct);
            Assert.That(afterOlderSave.Dirty, Is.True);
            Assert.That(afterOlderSave.SavedRevision, Is.EqualTo(snapshot.Revision));
            Assert.That(afterOlderSave.Data.Info.Nickname, Is.EqualTo("Later"));

            PlayerState latest = await cache.GetAsync(11, ct);
            await players.SaveAsync(latest, ct);
            PlayerState beforeConfirm = await cache.GetAsync(11, ct);
            Assert.That(beforeConfirm.Dirty, Is.True);
            cache.ConfirmSaved(11, latest.Revision);
            PlayerState clean = await cache.GetAsync(11, ct);
            Assert.That(clean.Dirty, Is.False);
            cache.Remove(11);
        }, CancellationToken.None);

        int queriesAfterRemove = players.LoadQueryCount;
        await queue.EnqueueAsync(11, async ct =>
        {
            PlayerState reloaded = await cache.GetAsync(11, ct);
            Assert.That(reloaded.Data.Info.Nickname, Is.EqualTo("Later"));
            Assert.That(reloaded.Dirty, Is.False);
        }, CancellationToken.None);
        Assert.That(players.LoadQueryCount, Is.EqualTo(queriesAfterRemove + 1));
    }

    [Test]
    public async Task Version1Database_UpgradesAndKeepsRows()
    {
        using var dir = new IsolatedDirectory();
        string database = DatabasePath(dir);
        using (var connection = Open(database))
        {
            Execute(connection, """
                CREATE TABLE account (
                  id INTEGER PRIMARY KEY,
                  username TEXT NOT NULL UNIQUE,
                  pwd_hash TEXT NOT NULL,
                  salt TEXT NOT NULL,
                  created_at INTEGER NOT NULL
                );
                """);
            Execute(connection, """
                CREATE TABLE player (
                  id INTEGER PRIMARY KEY,
                  account_id INTEGER NOT NULL UNIQUE REFERENCES account(id),
                  nickname TEXT NOT NULL,
                  level INTEGER NOT NULL DEFAULT 1,
                  exp INTEGER NOT NULL DEFAULT 0,
                  gold INTEGER NOT NULL DEFAULT 200,
                  gem INTEGER NOT NULL DEFAULT 300,
                  ticket INTEGER NOT NULL DEFAULT 5,
                  shard INTEGER NOT NULL DEFAULT 0,
                  bag_slots INTEGER NOT NULL DEFAULT 60,
                  guide_step INTEGER NOT NULL DEFAULT 0,
                  last_daily_reset INTEGER NOT NULL DEFAULT 0
                );
                """);
            Execute(connection, """
                CREATE TABLE auth_token (
                  token_hash TEXT PRIMARY KEY,
                  player_id INTEGER NOT NULL REFERENCES player(id),
                  expires_at_ms INTEGER NOT NULL,
                  revoked INTEGER NOT NULL
                );
                """);
            Execute(connection, """
                CREATE TABLE schema_meta (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  version INTEGER NOT NULL
                );
                """);
            Execute(connection, "INSERT INTO schema_meta (id, version) VALUES (1, 1);");
            Execute(connection, "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES (1, 'ada', 'hash', 'salt', 10);");
            Execute(connection, "INSERT INTO player (id, account_id, nickname, level, gold, last_daily_reset) VALUES (11, 1, 'Ada', 4, 15, 77);");
            Execute(connection, "INSERT INTO auth_token (token_hash, player_id, expires_at_ms, revoked) VALUES ('abc', 11, 99, 0);");
        }

        using var store = new SqliteStore();
        store.Initialize(database);
        PlayerState loaded = await new PlayerStore(store).LoadAsync(11, CancellationToken.None);

        Assert.That(loaded.Data.Info.Nickname, Is.EqualTo("Ada"));
        Assert.That(loaded.Data.Info.Level, Is.EqualTo(4));
        Assert.That(loaded.Data.Info.Gold, Is.EqualTo(15));
        Assert.That(loaded.Data.Bag, Is.Empty);
        Assert.That(loaded.Data.Plots, Is.Empty);
        Assert.That(loaded.Data.OrderProgress, Is.Null);
        Assert.That(loaded.Data.Home, Is.Null);
        Assert.That(loaded.Data.Avatar, Is.Null);
        Assert.That(loaded.Revision, Is.Zero);
        using var check = Open(database);
        Assert.That(ScalarLong(check, "SELECT version FROM schema_meta WHERE id = 1;"), Is.EqualTo(SqliteStore.CurrentVersion));
        Assert.That(ScalarString(check, "SELECT username FROM account WHERE id = 1;"), Is.EqualTo("ada"));
        Assert.That(ScalarString(check, "SELECT token_hash FROM auth_token WHERE player_id = 11;"), Is.EqualTo("abc"));
        Assert.That(ScalarLong(check, "SELECT last_daily_reset FROM player WHERE id = 11;"), Is.EqualTo(77));
        Assert.That(Count(check, "player_snapshot"), Is.Zero);
        Assert.That(Count(check, "account"), Is.EqualTo(1));
    }

    [Test]
    public async Task BrokenSnapshot_IsNotCached()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        Seed(dir, 11, 1, "Ann", 1);
        using (var connection = Open(DatabasePath(dir)))
        {
            Execute(connection, "INSERT INTO player_snapshot (player_id, saved_revision, body) VALUES (11, 3, X'');");
        }

        var players = new PlayerStore(store);
        var cache = new PlayerCache(players);
        using var queue = new PlayerQueue();
        await queue.EnqueueAsync(11, token =>
        {
            Assert.ThrowsAsync<InvalidDataException>(async () => await cache.GetAsync(11, token));
            Assert.That(cache.Count, Is.Zero);
            Assert.ThrowsAsync<InvalidDataException>(async () => await cache.GetAsync(11, token));
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.That(players.LoadQueryCount, Is.EqualTo(2));
        Assert.That(cache.Count, Is.Zero);
    }

    static PlayerFullData SampleData(long playerId)
    {
        var data = new PlayerFullData
        {
            Info = new PlayerInfo
            {
                PlayerId = playerId,
                Nickname = "Ann2",
                Level = 3,
                Exp = 12,
                Gold = 90,
                Gem = 8,
                Ticket = 2,
                Shard = 6,
                BagSlots = 70,
                GuideStep = 2
            },
            OrderProgress = new OrderProgress
            {
                DayIndex = 4,
                DoneCount = 1,
                RerollCount = 2,
                Claimed3 = true,
                NextRefreshTime = 88
            },
            Home = new HomeLayout { Version = 5 },
            Avatar = new AvatarData()
        };
        data.Bag.Add(new ItemStack { ItemId = 100, Count = 3 });
        data.Plots.Add(new PlotData
        {
            PlotId = 2,
            State = PlotState.PlotGrowing,
            CropId = 9,
            PlantTime = 10,
            ReadyTime = 20,
            Watered = true
        });
        data.Nodes.Add(new NodeData { NodeId = 3, NextReadyTime = 30 });
        data.Tools.Add(new ToolData { ToolId = 4, Level = 2 });
        data.Facilities.Add(new FacilityData
        {
            FacilityId = 5,
            Level = 2,
            Slots = { new SlotData { SlotId = 1, RecipeId = 8, FinishTime = 40, State = SlotState.SlotWorking } }
        });
        data.Orders.Add(new OrderData
        {
            OrderId = 6,
            Slot = 1,
            Needs = { new ItemStack { ItemId = 7, Count = 2 } },
            Reward = new Reward { Gold = 5, Items = { new ItemStack { ItemId = 8, Count = 1 } } },
            Rare = true,
            Status = 1
        });
        data.Home.Items.Add(new FurniturePlacement { FurnitureId = 9, X = 1, Y = 2, Rotation = 90 });
        data.Avatar.Owned.Add(15);
        data.Avatar.Equipped[1] = 15;
        data.GachaStates.Add(new GachaPoolState { PoolId = 1, PitySsr = 10, PitySr = 3, UpGuarantee = true });
        data.Activities.Add(new ActivityInfo { ActivityId = 2, Name = "spring", Type = "gacha", PoolId = 1, StartTime = 5, EndTime = 9 });
        return data;
    }

    static SqliteStore OpenStore(IsolatedDirectory dir)
    {
        var store = new SqliteStore();
        store.Initialize(DatabasePath(dir));
        return store;
    }

    static void Seed(IsolatedDirectory dir, long playerId, long accountId, string nickname, long lastDailyReset)
    {
        using var connection = Open(DatabasePath(dir));
        Execute(connection, string.Format(CultureInfo.InvariantCulture,
            "INSERT INTO account (id, username, pwd_hash, salt, created_at) VALUES ({0}, 'user{0}', 'hash', 'salt', 1);",
            accountId));
        Execute(connection, string.Format(CultureInfo.InvariantCulture,
            "INSERT INTO player (id, account_id, nickname, last_daily_reset) VALUES ({0}, {1}, '{2}', {3});",
            playerId, accountId, nickname, lastDailyReset));
    }

    static string DatabasePath(IsolatedDirectory dir)
    {
        return Path.Combine(dir.FullPath, "game.db");
    }

    static SqliteConnection Open(string database)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long Count(SqliteConnection connection, string table)
    {
        return ScalarLong(connection, string.Format(CultureInfo.InvariantCulture, "SELECT COUNT(*) FROM {0};", table));
    }

    static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingPlayerCacheTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingPlayerCacheTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
