using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using CloudIsle.Server.Players;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>A11/E11验收夹具：真实账号创建、缓存修改、SQLite写入失败/恢复和关闭重开；无生产调试入口。</summary>
public sealed class PlayerPersistenceTests
{
    /// <summary>修改测试昵称后经SaveService提交，关闭所有DB连接后重开同文件，完整业务快照保持。</summary>
    [Test]
    public async Task ModifiedNickname_SaveCloseReopen_PreservesFullBusinessSnapshot()
    {
        string root = NewWorkspace();
        long playerId;
        PlayerFullData expected;
        await using (var world = new World(root))
        {
            playerId = await world.RegisterAsync();
            PlayerState before = await world.Players.LoadAsync(playerId, CancellationToken.None);
            File.WriteAllText(Path.Combine(root, "before.json"), JsonFormatter.Default.Format(before.Data));
            await world.ChangeNicknameAsync(playerId, "M1存档标记");
            PlayerState dirty = await world.ReadCacheAsync(playerId);
            Assert.That(dirty.Dirty, Is.True);
            expected = dirty.Data.Clone();
            await world.Saves.FlushPlayerAsync(playerId, CancellationToken.None);
            PlayerState saved = await world.ReadCacheAsync(playerId);
            Assert.That(saved.Dirty, Is.False);
            Assert.That(saved.SavedRevision, Is.EqualTo(dirty.Revision));
        }

        using var reopened = new SqliteStore();
        reopened.Initialize(Path.Combine(root, "m1.db"));
        PlayerState loaded = await new PlayerStore(reopened).LoadAsync(playerId, CancellationToken.None);
        Assert.That(loaded.Data.Info.PlayerId, Is.EqualTo(playerId));
        Assert.That(loaded.Data.Info.Nickname, Is.EqualTo("M1存档标记"));
        Assert.That(loaded.Data, Is.EqualTo(expected), "真实业务快照不是只重读默认值");
        Assert.That(loaded.SavedRevision, Is.GreaterThan(0));
        File.WriteAllText(Path.Combine(root, "after-reopen.json"), JsonFormatter.Default.Format(loaded.Data));
    }

    /// <summary>SQLite工作连接query_only使真实写入返回SQLITE_READONLY；Dirty不误确认，恢复写入后同快照重试成功。</summary>
    [Test]
    public async Task DatabaseReadOnly_KeepsDirty_ThenWritableRetryPersists()
    {
        await using var world = new World(NewWorkspace());
        long playerId = await world.RegisterAsync();
        PlayerState original = await world.Players.LoadAsync(playerId, CancellationToken.None);
        await world.ChangeNicknameAsync(playerId, "M1重试标记");
        PlayerState wanted = await world.ReadCacheAsync(playerId);
        await world.SetQueryOnlyAsync(true);
        try
        {
            SqliteException? failure = Assert.ThrowsAsync<SqliteException>(async () =>
                await world.Saves.FlushPlayerAsync(playerId, CancellationToken.None));
            Assert.That(failure!.SqliteErrorCode, Is.EqualTo(8), "必须是真实SQLITE_READONLY，不能是假处理器抛错");
            Assert.That(world.Saves.LastFailure, Is.InstanceOf<SqliteException>());
            Assert.That(world.Saves.FailedSaveCount, Is.EqualTo(1));
            Assert.That(world.Saves.ConfirmedSaveCount, Is.Zero);
            PlayerState dirty = await world.ReadCacheAsync(playerId);
            Assert.That(dirty.Dirty, Is.True);
            Assert.That(dirty.Revision, Is.EqualTo(wanted.Revision));
            Assert.That(dirty.SavedRevision, Is.EqualTo(original.SavedRevision));
            PlayerState onDisk = await world.Players.LoadAsync(playerId, CancellationToken.None);
            Assert.That(onDisk.Data, Is.EqualTo(original.Data), "失败事务不能部分修改已提交快照");
        }
        finally { await world.SetQueryOnlyAsync(false); }

        await world.Saves.FlushPlayerAsync(playerId, CancellationToken.None);
        PlayerState clean = await world.ReadCacheAsync(playerId);
        Assert.That(clean.Dirty, Is.False);
        Assert.That(clean.SavedRevision, Is.EqualTo(wanted.Revision));
        Assert.That(world.Saves.ConfirmedSaveCount, Is.EqualTo(1));
        PlayerState reloaded = await world.Players.LoadAsync(playerId, CancellationToken.None);
        Assert.That(reloaded.Data, Is.EqualTo(wanted.Data));
        Assert.That(reloaded.Data.Info.Nickname, Is.EqualTo("M1重试标记"));
    }

    /// <summary>每次新目录，保留业务快照供验收复核；不删除任何旧库或读取正式库。</summary>
    static string NewWorkspace()
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "Server", "ConfigLuban", "config_manifest.json")))
            repo = repo.Parent;
        if (repo == null) throw new DirectoryNotFoundException("找不到工程根");
        string root = Path.Combine(repo.FullName, "Temp", "m1-player-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>正式生产组件组成的隔离夹具；只有内部测试钩子修改昵称，不新增协议。</summary>
    sealed class World : IAsyncDisposable
    {
        internal SqliteStore Store { get; }
        internal PlayerStore Players { get; }
        internal PlayerCache Cache { get; }
        internal PlayerQueue Queue { get; }
        internal SaveService Saves { get; }

        internal World(string root)
        {
            Store = new SqliteStore();
            Store.Initialize(Path.Combine(root, "m1.db"));
            Players = new PlayerStore(Store);
            Cache = new PlayerCache(Players);
            Queue = new PlayerQueue();
            Saves = new SaveService(Queue, Cache, Players);
        }

        /// <summary>真实注册并鉴权获得DB主键；随机凭据只在内存，不进入日志或证据。</summary>
        internal async Task<long> RegisterAsync()
        {
            string username = "m1p" + Guid.NewGuid().ToString("N")[..10];
            string password = Guid.NewGuid().ToString("N");
            var accounts = new AccountRepository(Store, new PasswordHasher());
            Assert.That(await accounts.RegisterAsync(new RegisterReq
            {
                Username = username, Password = password, Nickname = "初始昵称", ProtoVersion = 1
            }, CancellationToken.None), Is.Zero);
            long playerId = await accounts.AuthenticateAsync(username, password, CancellationToken.None);
            Assert.That(playerId, Is.GreaterThan(0));
            return playerId;
        }

        /// <summary>测试专用昵称修改，仅在正式同玩家队列内操作缓存。</summary>
        internal Task ChangeNicknameAsync(long playerId, string nickname) => Queue.EnqueueAsync(playerId, async ct =>
        {
            await Cache.GetAsync(playerId, ct);
            Cache.ReplaceNickname(playerId, nickname);
        }, CancellationToken.None);

        /// <summary>在合法队列上下文读取独立快照，不能通过返回引用修改真实缓存。</summary>
        internal async Task<PlayerState> ReadCacheAsync(long playerId)
        {
            PlayerState? copy = null;
            await Queue.EnqueueAsync(playerId, async ct => copy = await Cache.GetAsync(playerId, ct), CancellationToken.None);
            return copy!;
        }

        /// <summary>在同一SQLite工作连接改变query_only，确实使后续生产Save写入失败或恢复。</summary>
        internal Task<int> SetQueryOnlyAsync(bool readOnly) => Store.ExecuteAsync(connection =>
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = readOnly ? "PRAGMA query_only=ON;" : "PRAGMA query_only=OFF;";
            pragma.ExecuteNonQuery();
            return 0;
        }, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Saves.DisposeAsync();
            Queue.Dispose();
            Store.Dispose();
        }
    }
}
