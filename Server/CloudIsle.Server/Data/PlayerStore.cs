using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Trade;
using CloudIsle.Proto;
using CloudIsle.Server.Gameplay.Infrastructure.Farm;
using CloudIsle.Server.Gameplay.Infrastructure.Trade;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Data;

/// <summary>
/// 经 SqliteStore 的唯一队列读写玩家快照。不另开数据库线程，也不修改缓存里的脏标记。
/// </summary>
public sealed class PlayerStore
{
    readonly SqliteStore _store;
    int _loadQueryCount;

    /// <summary>使用已经初始化的库。</summary>
    /// <param name="store">提供队列连接的 SQLite 存储。为空时抛出异常。</param>
    public PlayerStore(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>实际执行过的加载查询次数。缓存命中不会增加。</summary>
    internal int LoadQueryCount => Volatile.Read(ref _loadQueryCount);

    /// <summary>按主键加载一名玩家。没有玩家行时失败，不插入账号或玩家。</summary>
    /// <param name="playerId">玩家主键，必须是正数。</param>
    /// <param name="ct">入队前取消则不查询。</param>
    public Task<PlayerState> LoadAsync(long playerId, CancellationToken ct)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家主键必须是正数");

        return _store.ExecuteAsync(connection =>
        {
            Interlocked.Increment(ref _loadQueryCount);
            return Read(connection, playerId);
        }, ct);
    }

    /// <summary>
    /// 兼容 Save：事务内读 stored → <see cref="PlayerSaveGuard"/> → NoOp 跳过写；
    /// 允许则写 base+snapshot。经济抬版本不得由此旁路（见 Guard）。无 forceSave API。
    /// </summary>
    /// <param name="snapshot">要保存的快照。Info 必须存在，且 Info.PlayerId 等于 PlayerId。</param>
    /// <param name="ct">入队前取消则不写库。</param>
    public Task SaveAsync(PlayerState snapshot, CancellationToken ct)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.Data.Info == null)
            throw new ArgumentException("快照缺少 Info", nameof(snapshot));
        if (snapshot.Data.Info.PlayerId != snapshot.PlayerId)
            throw new ArgumentException("快照 Info.PlayerId 与 PlayerId 不一致", nameof(snapshot));

        PlayerFullData copy = snapshot.Data.Clone();
        long playerId = snapshot.PlayerId;
        long revision = snapshot.Revision;
        ShopPurchaseCounts shop = snapshot.ShopPurchases ?? ShopPurchaseCounts.Empty;
        AcceptedTaskState tasks = snapshot.AcceptedTasks ?? AcceptedTaskState.Empty;
        if (copy.GrantVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "grant_version 不能为负");
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "revision 不能为负");
        copy.Revision = revision;
        var incoming = new PlayerState(playerId, copy, revision, snapshot.SavedRevision, shop, tasks);
        return _store.ExecuteAsync(connection =>
        {
            using var tx = connection.BeginTransaction();
            PlayerState stored = ReadInTransaction(connection, tx, playerId);
            bool hasSnapshot = SnapshotExistsInTransaction(connection, tx, playerId);
            SaveDecision decision = PlayerSaveGuard.Evaluate(stored, incoming, hasSnapshot);
            switch (decision.Kind)
            {
                case SaveDecisionKind.NoOp:
                    tx.Commit();
                    return true;
                case SaveDecisionKind.Conflict:
                case SaveDecisionKind.RejectStale:
                case SaveDecisionKind.RejectEconomicBypass:
                    throw new InvalidOperationException(decision.Message);
                case SaveDecisionKind.AllowBootstrap:
                case SaveDecisionKind.AllowCompatibility:
                    break;
                default:
                    throw new InvalidOperationException("unknown save decision");
            }

            byte[] body = copy.ToByteArray();
            WriteBaseAndSnapshotInTransaction(
                connection, tx, playerId, copy.Info!, revision, copy.GrantVersion, body);
            // 兼容 Save 允许写时同步 sidecar（经济旁路已被 Guard 拒绝，此处多为 bootstrap/昵称冲洗）。
            ShopPurchaseStore.ReplaceAll(connection, tx, playerId, shop);
            AcceptedTaskStore.Replace(connection, tx, playerId, tasks);
            tx.Commit();
            return true;
        }, ct);
    }

    static bool SnapshotExistsInTransaction(
        SqliteConnection connection, SqliteTransaction tx, long playerId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM player_snapshot WHERE player_id = $p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", playerId);
        object? o = cmd.ExecuteScalar();
        return o is not null and not DBNull;
    }

    /// <summary>读取玩家行。没有快照时用基础列组装空集合；有快照时必须和基础列一致。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="playerId">要读取的玩家主键。</param>
    static PlayerState Read(SqliteConnection connection, long playerId)
    {
        using var command = connection.CreateCommand();
        // grant_version 为 schema3 权威列；旧 protobuf 缺省 0 时以本列为准投影到 Data.GrantVersion。
        command.CommandText = """
            SELECT p.nickname, p.level, p.exp, p.gold, p.gem, p.ticket, p.shard, p.bag_slots, p.guide_step,
                   p.grant_version,
                   s.saved_revision, s.body
            FROM player p
            LEFT JOIN player_snapshot s ON s.player_id = p.id
            WHERE p.id = $playerId;
            """;
        command.Parameters.AddWithValue("$playerId", playerId);
        BaseRow row;
        long savedRevision;
        byte[]? body;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException($"玩家 {playerId} 不存在");

            row = new BaseRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9));

            if (reader.IsDBNull(11))
            {
                savedRevision = 0;
                body = null;
            }
            else
            {
                savedRevision = reader.GetInt64(10);
                if (savedRevision < 0)
                    throw new InvalidDataException("已保存版本不能为负");
                body = (byte[])reader.GetValue(11);
            }
        }

        ShopPurchaseCounts shop = ShopPurchaseStore.ReadAll(connection, null, playerId);
        AcceptedTaskState tasks = AcceptedTaskStore.Read(connection, null, playerId);
        if (body == null)
        {
            PlayerFullData bare = FromBaseRow(playerId, row);
            bare.Revision = 0;
            bare.GrantVersion = row.GrantVersion;
            return new PlayerState(playerId, bare, 0, 0, shop, tasks);
        }

        PlayerFullData data = Parse(body);
        EnsureMatches(playerId, data, row);
        // 权威持久 revision：snapshot.saved_revision。旧 blob 新增字段缺省 0 时覆盖投影。
        data.Revision = savedRevision;
        data.GrantVersion = row.GrantVersion;
        return new PlayerState(playerId, data, savedRevision, savedRevision, shop, tasks);
    }

    /// <summary>在一个事务里更新基础列并替换快照。任一步失败都回滚。</summary>
    static void Write(SqliteConnection connection, long playerId, PlayerInfo info, long revision, int grantVersion, byte[] body)
    {
        using var transaction = connection.BeginTransaction();
        WriteBaseAndSnapshotInTransaction(connection, transaction, playerId, info, revision, grantVersion, body);
        transaction.Commit();
    }

    /// <summary>
    /// 供 CommitStore 等外层事务复用：更新 player 列 + upsert snapshot，不 Begin/Commit。
    /// 可选 CAS：当 <paramref name="casObservedRevision"/> 非 null 时要求当前 saved_revision 等于该值（无行视为 0）。
    /// 可选写点回调：Before/After 分别夹在 base UPDATE 与 snapshot UPSERT 外侧（不测两者之间的“半写”以外的模糊点）。
    /// </summary>
    internal static void WriteBaseAndSnapshotInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long playerId,
        PlayerInfo info,
        long revision,
        int grantVersion,
        byte[] body,
        long? casObservedRevision = null,
        Action? beforePlayerUpdate = null,
        Action? afterPlayerUpdate = null,
        Action? beforeSnapshot = null,
        Action? afterSnapshot = null)
    {
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT saved_revision FROM player_snapshot WHERE player_id = $playerId;";
            current.Parameters.AddWithValue("$playerId", playerId);
            object? saved = current.ExecuteScalar();
            long existing = saved is null or DBNull ? 0L : Convert.ToInt64(saved);
            if (casObservedRevision is long expected && existing != expected)
                throw new InvalidOperationException("玩家快照 CAS 失败：observed revision 不匹配");
            if (existing > revision)
                throw new InvalidOperationException("拒绝用旧版本覆盖已保存的玩家快照");
        }

        beforePlayerUpdate?.Invoke();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE player
                SET nickname = $nickname,
                    level = $level,
                    exp = $exp,
                    gold = $gold,
                    gem = $gem,
                    ticket = $ticket,
                    shard = $shard,
                    bag_slots = $bagSlots,
                    guide_step = $guideStep,
                    grant_version = $grantVersion
                WHERE id = $playerId;
                """;
            update.Parameters.AddWithValue("$nickname", info.Nickname);
            update.Parameters.AddWithValue("$level", info.Level);
            update.Parameters.AddWithValue("$exp", info.Exp);
            update.Parameters.AddWithValue("$gold", info.Gold);
            update.Parameters.AddWithValue("$gem", info.Gem);
            update.Parameters.AddWithValue("$ticket", info.Ticket);
            update.Parameters.AddWithValue("$shard", info.Shard);
            update.Parameters.AddWithValue("$bagSlots", info.BagSlots);
            update.Parameters.AddWithValue("$guideStep", info.GuideStep);
            update.Parameters.AddWithValue("$grantVersion", grantVersion);
            update.Parameters.AddWithValue("$playerId", playerId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"玩家 {playerId} 不存在，拒绝保存");
        }
        afterPlayerUpdate?.Invoke();

        beforeSnapshot?.Invoke();
        using (var snapshot = connection.CreateCommand())
        {
            snapshot.Transaction = transaction;
            snapshot.CommandText = """
                INSERT INTO player_snapshot (player_id, saved_revision, body)
                VALUES ($playerId, $revision, $body)
                ON CONFLICT(player_id) DO UPDATE SET
                  saved_revision = excluded.saved_revision,
                  body = excluded.body;
                """;
            snapshot.Parameters.AddWithValue("$playerId", playerId);
            snapshot.Parameters.AddWithValue("$revision", revision);
            snapshot.Parameters.Add("$body", SqliteType.Blob).Value = body;
            if (snapshot.ExecuteNonQuery() != 1)
                throw new InvalidDataException("玩家快照没有写成一行");
        }
        afterSnapshot?.Invoke();
    }

    /// <summary>在已有事务中只读加载（CommitStore CAS 前复核）。</summary>
    internal static PlayerState ReadInTransaction(SqliteConnection connection, SqliteTransaction tx, long playerId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT p.nickname, p.level, p.exp, p.gold, p.gem, p.ticket, p.shard, p.bag_slots, p.guide_step,
                   p.grant_version,
                   s.saved_revision, s.body
            FROM player p
            LEFT JOIN player_snapshot s ON s.player_id = p.id
            WHERE p.id = $playerId;
            """;
        command.Parameters.AddWithValue("$playerId", playerId);
        BaseRow row;
        long savedRevision;
        byte[]? body;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException($"玩家 {playerId} 不存在");

            row = new BaseRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9));

            if (reader.IsDBNull(11))
            {
                savedRevision = 0;
                body = null;
            }
            else
            {
                savedRevision = reader.GetInt64(10);
                if (savedRevision < 0)
                    throw new InvalidDataException("已保存版本不能为负");
                body = (byte[])reader.GetValue(11);
            }
        }

        ShopPurchaseCounts shop = ShopPurchaseStore.ReadAll(connection, tx, playerId);
        AcceptedTaskState tasks = AcceptedTaskStore.Read(connection, tx, playerId);
        if (body == null)
        {
            PlayerFullData bare = FromBaseRow(playerId, row);
            bare.Revision = 0;
            bare.GrantVersion = row.GrantVersion;
            return new PlayerState(playerId, bare, 0, 0, shop, tasks);
        }

        PlayerFullData data = Parse(body);
        EnsureMatches(playerId, data, row);
        data.Revision = savedRevision;
        data.GrantVersion = row.GrantVersion;
        return new PlayerState(playerId, data, savedRevision, savedRevision, shop, tasks);
    }

    /// <summary>用正式玩家列组装快照。重复字段保持空集合，三个嵌套消息保持 null。</summary>
    /// <param name="playerId">玩家主键，写入 Info.PlayerId。</param>
    /// <param name="row">正式玩家表上的基础列。last_daily_reset 不在协议里，不放进快照。</param>
    static PlayerFullData FromBaseRow(long playerId, BaseRow row)
    {
        return new PlayerFullData
        {
            Info = new PlayerInfo
            {
                PlayerId = playerId,
                Nickname = row.Nickname,
                Level = row.Level,
                Exp = row.Exp,
                Gold = row.Gold,
                Gem = row.Gem,
                Ticket = row.Ticket,
                Shard = row.Shard,
                BagSlots = row.BagSlots,
                GuideStep = row.GuideStep
            },
            GrantVersion = row.GrantVersion,
            Revision = 0
        };
    }

    /// <summary>解析快照体。字节不是合法 protobuf 时失败。</summary>
    /// <param name="body">player_snapshot.body。</param>
    static PlayerFullData Parse(byte[] body)
    {
        try
        {
            return PlayerFullData.Parser.ParseFrom(body);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException("玩家快照无法解析", ex);
        }
    }

    /// <summary>核对快照里的基础字段和正式玩家列。不一致时拒绝载入。</summary>
    /// <param name="playerId">行主键。</param>
    /// <param name="data">刚解析出的快照。</param>
    /// <param name="row">同一行的正式基础列。</param>
    static void EnsureMatches(long playerId, PlayerFullData data, BaseRow row)
    {
        if (data.Info == null)
            throw new InvalidDataException("玩家快照缺少 Info，拒绝载入");
        if (data.Info.PlayerId != playerId
            || data.Info.Nickname != row.Nickname
            || data.Info.Level != row.Level
            || data.Info.Exp != row.Exp
            || data.Info.Gold != row.Gold
            || data.Info.Gem != row.Gem
            || data.Info.Ticket != row.Ticket
            || data.Info.Shard != row.Shard
            || data.Info.BagSlots != row.BagSlots
            || data.Info.GuideStep != row.GuideStep)
        {
            throw new InvalidDataException("玩家基础列与快照 Info 不一致，拒绝载入");
        }
    }

    /// <summary>正式 player 表上、且协议 Info 也有的那些列。</summary>
    /// <param name="Nickname">昵称。</param>
    /// <param name="Level">等级。</param>
    /// <param name="Exp">经验。</param>
    /// <param name="Gold">金币。</param>
    /// <param name="Gem">钻石。</param>
    /// <param name="Ticket">抽卡券。</param>
    /// <param name="Shard">碎片。</param>
    /// <param name="BagSlots">背包格数。</param>
    /// <param name="GuideStep">引导步骤。</param>
    /// <param name="GrantVersion">玩法初始化版本；0 表示待初始化。</param>
    readonly record struct BaseRow(
        string Nickname,
        int Level,
        int Exp,
        long Gold,
        long Gem,
        long Ticket,
        long Shard,
        int BagSlots,
        int GuideStep,
        int GrantVersion);
}
