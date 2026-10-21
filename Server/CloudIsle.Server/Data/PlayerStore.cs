using CloudIsle.Proto;
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

    /// <summary>把一份完整快照写入正式玩家列和快照表。调用当下就复制数据和版本。</summary>
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
        byte[] body = copy.ToByteArray();
        return _store.ExecuteAsync(connection =>
        {
            Write(connection, playerId, copy.Info, revision, body);
            return true;
        }, ct);
    }

    /// <summary>读取玩家行。没有快照时用基础列组装空集合；有快照时必须和基础列一致。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="playerId">要读取的玩家主键。</param>
    static PlayerState Read(SqliteConnection connection, long playerId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.nickname, p.level, p.exp, p.gold, p.gem, p.ticket, p.shard, p.bag_slots, p.guide_step,
                   s.saved_revision, s.body
            FROM player p
            LEFT JOIN player_snapshot s ON s.player_id = p.id
            WHERE p.id = $playerId;
            """;
        command.Parameters.AddWithValue("$playerId", playerId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"玩家 {playerId} 不存在");

        var row = new BaseRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8));

        if (reader.IsDBNull(10))
            return new PlayerState(playerId, FromBaseRow(playerId, row), 0, 0);

        long savedRevision = reader.GetInt64(9);
        if (savedRevision < 0)
            throw new InvalidDataException("已保存版本不能为负");

        byte[] body = (byte[])reader.GetValue(10);
        PlayerFullData data = Parse(body);
        EnsureMatches(playerId, data, row);
        return new PlayerState(playerId, data, savedRevision, savedRevision);
    }

    /// <summary>在一个事务里更新基础列并替换快照。任一步失败都回滚。</summary>
    /// <param name="connection">队列当前持有的连接。</param>
    /// <param name="playerId">要更新的玩家主键。不存在时失败，不插入新玩家。</param>
    /// <param name="info">要写回正式玩家列的基础字段。</param>
    /// <param name="revision">这次成功写入后记录的版本。</param>
    /// <param name="body">调用时已经序列化好的完整 PlayerFullData。</param>
    static void Write(SqliteConnection connection, long playerId, PlayerInfo info, long revision, byte[] body)
    {
        using var transaction = connection.BeginTransaction();
        // 已成功提交的新版本不能被迟到的旧快照覆盖。
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT saved_revision FROM player_snapshot WHERE player_id = $playerId;";
            current.Parameters.AddWithValue("$playerId", playerId);
            object? saved = current.ExecuteScalar();
            if (saved != null && saved != DBNull.Value && Convert.ToInt64(saved) > revision)
                throw new InvalidOperationException("拒绝用旧版本覆盖已保存的玩家快照");
        }
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
                    guide_step = $guideStep
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
            update.Parameters.AddWithValue("$playerId", playerId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"玩家 {playerId} 不存在，拒绝保存");
        }

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

        transaction.Commit();
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
            }
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
    readonly record struct BaseRow(
        string Nickname,
        int Level,
        int Exp,
        long Gold,
        long Gem,
        long Ticket,
        long Shard,
        int BagSlots,
        int GuideStep);
}
