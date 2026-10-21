using CloudIsle.Server.Players;

namespace CloudIsle.Tests;

/// <summary>测试用虚拟时钟。Advance 推进时间，不真实等待间隔。</summary>
public sealed class ManualSaveClock : ISaveClock
{
    readonly object _gate = new();
    readonly List<Waiter> _waiters = new();
    long _now;

    /// <summary>当前虚拟毫秒。</summary>
    public long NowMilliseconds
    {
        get
        {
            lock (_gate)
                return _now;
        }
    }

    /// <summary>把虚拟时间向前推进一段。</summary>
    /// <param name="elapsed">要增加的时间。负值抛出异常。已经到期的等待会完成一次。</param>
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "推进时间不能为负");

        var ready = new List<Waiter>();
        lock (_gate)
        {
            _now += (long)elapsed.TotalMilliseconds;
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Due <= _now)
                {
                    ready.Add(_waiters[i]);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (Waiter waiter in ready)
        {
            waiter.Registration.Dispose();
            waiter.Done.TrySetResult(true);
        }
    }

    /// <summary>虚拟时间到达 dueMilliseconds 时返回 true。</summary>
    /// <param name="dueMilliseconds">到期的虚拟毫秒。</param>
    /// <param name="ct">取消标记。取消后返回 false，并且不再占用等待名额。</param>
    public Task<bool> WaitUntilAsync(long dueMilliseconds, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(false);

        Waiter waiter;
        lock (_gate)
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult(false);
            if (_now >= dueMilliseconds)
                return Task.FromResult(true);

            waiter = new Waiter(dueMilliseconds);
            _waiters.Add(waiter);
        }

        waiter.Registration = ct.Register(() =>
        {
            bool removed;
            lock (_gate)
                removed = _waiters.Remove(waiter);
            if (removed)
                waiter.Done.TrySetResult(false);
        });
        return waiter.Done.Task;
    }

    sealed class Waiter
    {
        /// <summary>记下这次等待的到期时间。</summary>
        /// <param name="due">到期的虚拟毫秒。</param>
        public Waiter(long due)
        {
            Due = due;
        }

        /// <summary>到期的虚拟毫秒。</summary>
        public long Due { get; }

        /// <summary>时间到达时结果为 true，取消时为 false。</summary>
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>取消注册。到期后释放。</summary>
        public CancellationTokenRegistration Registration;
    }
}
