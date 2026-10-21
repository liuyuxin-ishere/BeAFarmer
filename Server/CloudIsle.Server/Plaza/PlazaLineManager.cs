namespace CloudIsle.Server.Plaza;

public interface IPlazaLineManager
{
    PlazaEnterResult Enter(PlazaPlayer player, int preferLineId);
    void Leave(long playerId);
    void OnMove(long playerId, PlazaMoveInput input);
    IReadOnlyList<PlazaLine> Lines { get; }
    PlazaLineMetrics AggregateMetrics();
}

public sealed class PlazaLineManager : IPlazaLineManager, IDisposable
{
    readonly object _gate = new();
    readonly List<PlazaLine> _lines = new();
    readonly Dictionary<long, int> _playerLine = new();
    readonly int _lineCap;
    readonly int _tickMs;

    public IReadOnlyList<PlazaLine> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public PlazaLineManager(int lineCap = 100, int tickMs = 100)
    {
        _lineCap = lineCap;
        _tickMs = tickMs;
        EnsureLine(1);
    }

    PlazaLine EnsureLine(int id)
    {
        lock (_gate)
        {
            var existing = _lines.FirstOrDefault(l => l.LineId == id);
            if (existing != null) return existing;
            var line = new PlazaLine(id, _lineCap, _tickMs);
            line.Start();
            _lines.Add(line);
            return line;
        }
    }

    PlazaLine? FindLine(int id)
    {
        lock (_gate) return _lines.FirstOrDefault(l => l.LineId == id);
    }

    public PlazaEnterResult Enter(PlazaPlayer player, int preferLineId)
    {
        // 已在线则先离开
        Leave(player.PlayerId);

        PlazaLine target;
        if (preferLineId > 0)
        {
            target = EnsureLine(preferLineId);
            if (target.TryEnter(player, out var preferred))
            {
                lock (_gate) _playerLine[player.PlayerId] = target.LineId;
                return preferred;
            }
        }

        lock (_gate)
        {
            foreach (var line in _lines)
            {
                if (line.PlayerCount >= _lineCap) continue;
                if (line.TryEnter(player, out var result))
                {
                    _playerLine[player.PlayerId] = line.LineId;
                    return result;
                }
            }

            int nextId = _lines.Count == 0 ? 1 : _lines.Max(l => l.LineId) + 1;
            var created = new PlazaLine(nextId, _lineCap, _tickMs);
            created.Start();
            _lines.Add(created);
            if (!created.TryEnter(player, out var entered))
                return entered;
            _playerLine[player.PlayerId] = created.LineId;
            return entered;
        }
    }

    public void Leave(long playerId)
    {
        int lineId;
        lock (_gate)
        {
            if (!_playerLine.TryGetValue(playerId, out lineId))
                return;
            _playerLine.Remove(playerId);
        }
        FindLine(lineId)?.Leave(playerId);
    }

    public void OnMove(long playerId, PlazaMoveInput input)
    {
        int lineId;
        lock (_gate)
        {
            if (!_playerLine.TryGetValue(playerId, out lineId))
                return;
        }
        FindLine(lineId)?.OnMove(playerId, input);
    }

    public PlazaLineMetrics AggregateMetrics()
    {
        var lines = Lines;
        if (lines.Count == 0)
            return new PlazaLineMetrics();
        var primary = lines[0].SnapshotMetrics();
        primary.Online = lines.Sum(l => l.PlayerCount);
        primary.CorrectCount = lines.Sum(l => l.SnapshotMetrics().CorrectCount);
        primary.P99TickMs = lines.Max(l => l.SnapshotMetrics().P99TickMs);
        primary.P50TickMs = lines.Average(l => l.SnapshotMetrics().P50TickMs);
        return primary;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var line in _lines)
                line.Dispose();
            _lines.Clear();
            _playerLine.Clear();
        }
    }
}
