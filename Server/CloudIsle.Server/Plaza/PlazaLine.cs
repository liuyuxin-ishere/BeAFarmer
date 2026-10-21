using System.Diagnostics;
using CloudIsle.Net;
using CloudIsle.Proto;

namespace CloudIsle.Server.Plaza;

/// <summary>单条分线：Tick 驱动校验、全图 AOI（PoC 简化）、5 Hz 差量广播。</summary>
public sealed class PlazaLine : IDisposable
{
    public const int MapSizeCm = 20000; // 200 m
    public const double MaxSpeedCmPerSec = 800; // 8 m/s
    public const double SpeedSlack = 1.2;

    readonly object _gate = new();
    readonly Dictionary<long, PlazaPlayer> _players = new();
    readonly List<double> _tickSamples = new(256);
    readonly int _tickMs;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;

    long _moveCountWindow;
    long _syncCountWindow;
    long _correctTotal;
    DateTime _windowStart = DateTime.UtcNow;
    double _lastTickMs;
    double _p50;
    double _p99;

    public int LineId { get; }
    public int Capacity { get; }
    public int PlayerCount { get { lock (_gate) return _players.Count; } }

    public PlazaLine(int lineId, int capacity, int tickMs = 100)
    {
        LineId = lineId;
        Capacity = capacity;
        _tickMs = Math.Max(20, tickMs);
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(RunLoopAsync);
        Console.WriteLine($"[Plaza] PlazaLine#{LineId} ready tick={_tickMs}ms cap={Capacity}");
    }

    public bool TryEnter(PlazaPlayer player, out PlazaEnterResult result)
    {
        result = new PlazaEnterResult { LineId = LineId };
        lock (_gate)
        {
            if (_players.Count >= Capacity)
            {
                result.Code = ErrorCodes.PlazaFull;
                return false;
            }

            if (_players.ContainsKey(player.PlayerId))
                LeaveUnlocked(player.PlayerId);

            // 均匀出生，避免全员重叠
            int idx = _players.Count;
            int cols = 10;
            player.X = 1000 + (idx % cols) * 1800;
            player.Y = 1000 + (idx / cols) * 1800;
            player.Dir = 0;
            player.State = 0;
            player.Dirty = true;
            player.LastMoveUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _players[player.PlayerId] = player;

            result.Code = ErrorCodes.Ok;
            result.Self = player.ToEntity();
            foreach (var kv in _players)
            {
                if (kv.Key == player.PlayerId) continue;
                result.Entities.Add(kv.Value.ToEntity());
            }
            return true;
        }
    }

    public void Leave(long playerId)
    {
        lock (_gate) LeaveUnlocked(playerId);
    }

    void LeaveUnlocked(long playerId)
    {
        _players.Remove(playerId);
    }

    public void OnMove(long playerId, PlazaMoveInput input)
    {
        PlazaCorrectNtf? correct = null;
        IPlazaOutbound? outbound = null;

        lock (_gate)
        {
            if (!_players.TryGetValue(playerId, out var p))
                return;

            Interlocked.Increment(ref _moveCountWindow);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double dt = Math.Max(0.05, (now - p.LastMoveUnixMs) / 1000.0);
            double dx = input.X - p.X;
            double dy = input.Y - p.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double maxDist = MaxSpeedCmPerSec * dt * SpeedSlack + 50;

            bool oob = input.X < 0 || input.Y < 0 || input.X > MapSizeCm || input.Y > MapSizeCm;
            if (oob || dist > maxDist)
            {
                _correctTotal++;
                correct = new PlazaCorrectNtf
                {
                    X = p.X,
                    Y = p.Y,
                    Reason = oob ? 2 : (dist > maxDist * 2 ? 3 : 1)
                };
                outbound = p.Outbound;
            }
            else
            {
                p.X = input.X;
                p.Y = input.Y;
                p.Dir = ((input.Dir % 360) + 360) % 360;
                p.State = input.State;
                p.LastMoveUnixMs = now;
                p.Dirty = true;
            }
        }

        if (correct != null && outbound != null)
            _ = outbound.SendAsync(MsgIds.PlazaCorrectNtf, correct.ToByteArray());
    }

    public PlazaLineMetrics SnapshotMetrics()
    {
        lock (_gate)
        {
            return new PlazaLineMetrics
            {
                LineId = LineId,
                Online = _players.Count,
                LastTickMs = _lastTickMs,
                P50TickMs = _p50,
                P99TickMs = _p99,
                MoveMsgPerSec = 0,
                SyncMsgPerSec = 0,
                CorrectCount = _correctTotal
            };
        }
    }

    async Task RunLoopAsync()
    {
        var sw = new Stopwatch();
        int tickIndex = 0;
        while (!_cts.IsCancellationRequested)
        {
            sw.Restart();
            try
            {
                Tick(tickIndex++);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plaza] Line#{LineId} tick error: {ex.Message}");
            }
            sw.Stop();
            _lastTickMs = sw.Elapsed.TotalMilliseconds;
            RecordTick(_lastTickMs);
            MaybeLogMetrics();

            int delay = _tickMs - (int)sw.ElapsedMilliseconds;
            if (delay > 0)
            {
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    void Tick(int tickIndex)
    {
        // 5 Hz sync：每 2 个 100ms tick
        if (tickIndex % 2 != 0)
            return;

        List<(IPlazaOutbound outbound, byte[] body)> sends = new();
        lock (_gate)
        {
            if (_players.Count == 0) return;

            var moves = new List<EntityMove>(_players.Count);
            foreach (var p in _players.Values)
            {
                if (!p.Dirty) continue;
                moves.Add(new EntityMove
                {
                    PlayerId = p.PlayerId,
                    X = p.X,
                    Y = p.Y,
                    Dir = p.Dir,
                    State = p.State
                });
                p.Dirty = false;
            }

            if (moves.Count == 0) return;

            var ntf = new PlazaSyncNtf
            {
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            ntf.Moves.AddRange(moves);
            byte[] body = ntf.ToByteArray();
            Interlocked.Add(ref _syncCountWindow, _players.Count);

            foreach (var p in _players.Values)
                sends.Add((p.Outbound, body));
        }

        foreach (var s in sends)
            _ = s.outbound.SendAsync(MsgIds.PlazaSyncNtf, s.body);
    }

    void RecordTick(double ms)
    {
        lock (_gate)
        {
            _tickSamples.Add(ms);
            if (_tickSamples.Count > 200)
                _tickSamples.RemoveRange(0, _tickSamples.Count - 200);
            if (_tickSamples.Count == 0) return;
            var sorted = _tickSamples.OrderBy(x => x).ToList();
            _p50 = sorted[sorted.Count / 2];
            _p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
        }
    }

    void MaybeLogMetrics()
    {
        var now = DateTime.UtcNow;
        if ((now - _windowStart).TotalSeconds < 10)
            return;

        double sec = Math.Max(0.001, (now - _windowStart).TotalSeconds);
        long moves = Interlocked.Exchange(ref _moveCountWindow, 0);
        long syncs = Interlocked.Exchange(ref _syncCountWindow, 0);
        _windowStart = now;

        var m = SnapshotMetrics();
        Console.WriteLine(
            $"[PlazaMetrics] line={m.LineId} online={m.Online} tick_last={m.LastTickMs:F2}ms " +
            $"tick_p50={m.P50TickMs:F2} tick_p99={m.P99TickMs:F2} " +
            $"move/s={moves / sec:F1} sync_send/s={syncs / sec:F1} corrects={m.CorrectCount}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
