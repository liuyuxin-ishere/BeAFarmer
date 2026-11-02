using System.Diagnostics;

namespace Game.Framework.Gameplay
{
    /// <summary>生产单调源：Stopwatch，与墙钟/时区无关。</summary>
    public sealed class StopwatchMonotonicClock : IMonotonicClock
    {
        readonly Stopwatch _watch;

        public StopwatchMonotonicClock()
        {
            _watch = Stopwatch.StartNew();
        }

        public long ElapsedMilliseconds => _watch.ElapsedMilliseconds;
    }
}
