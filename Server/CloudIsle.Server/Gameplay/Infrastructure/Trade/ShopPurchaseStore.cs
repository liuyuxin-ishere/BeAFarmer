using System;
using System.Collections.Generic;
using CloudIsle.Domain.Trade;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Trade;

/// <summary>
/// 商店日购买计数 sidecar：与 player 快照同事务读写。schema6。
/// 不得写入 DailyModuleMarker.CapabilityHash / GrantVersion / Proto unknown。
/// </summary>
public static class ShopPurchaseStore
{
    public static ShopPurchaseCounts ReadAll(
        SqliteConnection connection, SqliteTransaction? tx, long playerId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        var list = new List<KeyValuePair<int, int>>();
        using var cmd = connection.CreateCommand();
        if (tx != null)
            cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT shop_item_id, count FROM player_shop_daily_count
            WHERE player_id = $p ORDER BY shop_item_id;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int shopId = reader.GetInt32(0);
            int count = reader.GetInt32(1);
            if (shopId <= 0 || count <= 0)
                throw new InvalidDataException("corrupt shop purchase row");
            list.Add(new KeyValuePair<int, int>(shopId, count));
        }

        return list.Count == 0 ? ShopPurchaseCounts.Empty : new ShopPurchaseCounts(list);
    }

    /// <summary>用 next 全量替换该玩家购买计数（删旧写新）。</summary>
    public static void ReplaceAll(
        SqliteConnection connection,
        SqliteTransaction tx,
        long playerId,
        ShopPurchaseCounts counts)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        if (counts == null)
            throw new ArgumentNullException(nameof(counts));
        if (tx == null)
            throw new ArgumentNullException(nameof(tx));

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM player_shop_daily_count WHERE player_id = $p;";
            del.Parameters.AddWithValue("$p", playerId);
            del.ExecuteNonQuery();
        }

        foreach (KeyValuePair<int, int> kv in counts.Counts)
        {
            if (kv.Key <= 0 || kv.Value <= 0)
                throw new ArgumentException("invalid shop purchase entry");
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO player_shop_daily_count (player_id, shop_item_id, count)
                VALUES ($p, $s, $c);
                """;
            ins.Parameters.AddWithValue("$p", playerId);
            ins.Parameters.AddWithValue("$s", kv.Key);
            ins.Parameters.AddWithValue("$c", kv.Value);
            if (ins.ExecuteNonQuery() != 1)
                throw new InvalidDataException("shop purchase insert failed");
        }
    }
}
