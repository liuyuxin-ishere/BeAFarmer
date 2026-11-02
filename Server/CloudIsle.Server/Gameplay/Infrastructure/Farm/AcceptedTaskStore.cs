using System;
using System.IO;
using CloudIsle.Domain.Farm;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Farm;

/// <summary>
/// 已接受任务规格 sidecar：与 player 快照同事务读写。schema7。
/// 缺行 → Empty；损坏/高格式 → 抛错，不得静默清空。
/// </summary>
public static class AcceptedTaskStore
{
    public static AcceptedTaskState Read(
        SqliteConnection connection, SqliteTransaction? tx, long playerId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));

        using var cmd = connection.CreateCommand();
        if (tx != null)
            cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT format_version, body FROM player_accepted_task WHERE player_id = $p LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return AcceptedTaskState.Empty;

        int formatVersion = reader.GetInt32(0);
        if (reader.IsDBNull(1))
            throw new InvalidDataException("accepted task body is null");
        byte[] body = (byte[])reader.GetValue(1);
        return AcceptedTaskStateCodec.Decode(body, formatVersion);
    }

    /// <summary>全量替换该玩家 accepted 状态；Empty 时删除行。</summary>
    public static void Replace(
        SqliteConnection connection,
        SqliteTransaction tx,
        long playerId,
        AcceptedTaskState state)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM player_accepted_task WHERE player_id = $p;";
            del.Parameters.AddWithValue("$p", playerId);
            del.ExecuteNonQuery();
        }

        if (state.IsEmpty)
            return;

        byte[] body = AcceptedTaskStateCodec.Encode(state);
        using var ins = connection.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO player_accepted_task (player_id, format_version, body)
            VALUES ($p, $f, $b);
            """;
        ins.Parameters.AddWithValue("$p", playerId);
        ins.Parameters.AddWithValue("$f", AcceptedTaskStateCodec.CurrentFormatVersion);
        ins.Parameters.Add("$b", SqliteType.Blob).Value = body;
        if (ins.ExecuteNonQuery() != 1)
            throw new InvalidDataException("accepted task insert failed");
    }
}
