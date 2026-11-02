using System;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Domain.Clock;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Server.Gameplay.Infrastructure.Clock;

/// <summary>
/// 有效单调时钟：max(rawUtc, persistedFloor)，抬升 floor 时先写既有 server_clock_meta 再返回。
/// 不进玩家 lane；仅走 SqliteStore 队列。测试可注入 rawUtc 与故障点。
/// </summary>
public sealed class PersistedGameClock : IGameClock
{
    public enum ClockFaultPoint
    {
        None = 0,
        /// <summary>读 floor 之后、写之前抛错 → 不得返回新时间。</summary>
        BeforePersist = 1,
        /// <summary>写成功后、返回前抛错 → floor 已持久，调用方见异常（崩溃-before-ack）。</summary>
        AfterPersistBeforeReturn = 2
    }

    readonly SqliteStore _store;
    readonly Func<long> _rawUtcMs;
    ClockFaultPoint _fault;

    public PersistedGameClock(SqliteStore store, Func<long>? rawUtcMs = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rawUtcMs = rawUtcMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>测试专用故障注入。</summary>
    internal ClockFaultPoint FaultPoint
    {
        get => _fault;
        set => _fault = value;
    }

    public Task<long> GetEffectiveNowMsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long raw = _rawUtcMs();
        if (raw < 0)
            throw new InvalidOperationException("raw UTC ms must be >= 0");

        ClockFaultPoint fault = _fault;
        return _store.ExecuteAsync(connection =>
        {
            long floor = ReadFloor(connection);
            long effective = raw >= floor ? raw : floor;

            if (effective > floor)
            {
                if (fault == ClockFaultPoint.BeforePersist)
                    throw new InvalidOperationException("clock fault injected: BeforePersist");

                // 只抬升：并发实例不能压低已持久 floor
                RaiseFloor(connection, effective);
                long persisted = ReadFloor(connection);
                if (persisted < effective)
                    throw new InvalidOperationException("clock floor persist lost raise");

                if (fault == ClockFaultPoint.AfterPersistBeforeReturn)
                    throw new InvalidOperationException("clock fault injected: AfterPersistBeforeReturn");

                return persisted;
            }

            // 无需抬升：返回当前有效值（已是持久 floor 或 raw==floor）
            if (fault == ClockFaultPoint.BeforePersist)
                throw new InvalidOperationException("clock fault injected: BeforePersist");
            if (fault == ClockFaultPoint.AfterPersistBeforeReturn)
                throw new InvalidOperationException("clock fault injected: AfterPersistBeforeReturn");
            return effective;
        }, ct);
    }

    /// <summary>测试观察当前持久 floor（不抬升）。</summary>
    public Task<long> ReadPersistedFloorAsync(CancellationToken ct = default)
    {
        return _store.ExecuteAsync(ReadFloor, ct);
    }

    static long ReadFloor(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT time_lower_bound_ms FROM server_clock_meta WHERE id = 1;";
        object? o = cmd.ExecuteScalar();
        if (o is null or DBNull)
            throw new InvalidDataException("server_clock_meta missing");
        long floor = Convert.ToInt64(o);
        if (floor < 0)
            throw new InvalidDataException("time_lower_bound_ms negative");
        return floor;
    }

    static void RaiseFloor(SqliteConnection connection, long effective)
    {
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE server_clock_meta
                SET time_lower_bound_ms = $v
                WHERE id = 1 AND time_lower_bound_ms < $v;
                """;
            cmd.Parameters.AddWithValue("$v", effective);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
