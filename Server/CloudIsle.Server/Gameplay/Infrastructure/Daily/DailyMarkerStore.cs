using System;
using System.Collections.Generic;
using CloudIsle.Domain.Daily;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Daily;

/// <summary>
/// 日切模块 marker sidecar：与 player 快照同事务读写。schema5。
/// </summary>
public static class DailyMarkerStore
{
    public static IReadOnlyDictionary<string, DailyModuleMarker> ReadAll(
        SqliteConnection connection, SqliteTransaction? tx, long playerId)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        var map = new Dictionary<string, DailyModuleMarker>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT module_key, init_version, last_reset_day, availability, capability_hash
            FROM player_daily_module WHERE player_id = $p;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            int init = reader.GetInt32(1);
            int day = reader.GetInt32(2);
            var avail = (ModuleAvailability)reader.GetInt32(3);
            string hash = reader.IsDBNull(4) ? "" : reader.GetString(4);
            map[key] = new DailyModuleMarker(key, init, day, avail, hash);
        }
        return map;
    }

    /// <summary>用 next 全量替换该玩家 markers（删旧写新），保留未提及键则先清后写传入集合。</summary>
    public static void ReplaceAll(
        SqliteConnection connection,
        SqliteTransaction tx,
        long playerId,
        IReadOnlyDictionary<string, DailyModuleMarker> markers)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (markers == null) throw new ArgumentNullException(nameof(markers));
        if (tx == null) throw new ArgumentNullException(nameof(tx));

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM player_daily_module WHERE player_id = $p;";
            del.Parameters.AddWithValue("$p", playerId);
            del.ExecuteNonQuery();
        }

        foreach (KeyValuePair<string, DailyModuleMarker> kv in markers)
        {
            DailyModuleMarker m = kv.Value ?? throw new ArgumentException("null marker");
            if (!string.Equals(kv.Key, m.ModuleKey, StringComparison.Ordinal))
                throw new ArgumentException("marker key mismatch");
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO player_daily_module
                  (player_id, module_key, init_version, last_reset_day, availability, capability_hash)
                VALUES ($p, $k, $i, $d, $a, $h);
                """;
            ins.Parameters.AddWithValue("$p", playerId);
            ins.Parameters.AddWithValue("$k", m.ModuleKey);
            ins.Parameters.AddWithValue("$i", m.InitVersion);
            ins.Parameters.AddWithValue("$d", m.LastResetDay);
            ins.Parameters.AddWithValue("$a", (int)m.Availability);
            ins.Parameters.AddWithValue("$h", m.CapabilityHash ?? "");
            if (ins.ExecuteNonQuery() != 1)
                throw new InvalidDataException("daily marker insert failed");
        }
    }

    public static bool SameMarkers(
        IReadOnlyDictionary<string, DailyModuleMarker> a,
        IReadOnlyDictionary<string, DailyModuleMarker> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        foreach (KeyValuePair<string, DailyModuleMarker> kv in a)
        {
            if (!b.TryGetValue(kv.Key, out DailyModuleMarker? other))
                return false;
            if (!kv.Value.Equals(other))
                return false;
        }
        return true;
    }
}
