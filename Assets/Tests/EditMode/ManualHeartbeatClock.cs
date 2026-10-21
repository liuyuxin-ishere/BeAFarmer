using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// 测试用的单调时钟。推进时按下一个等待点分段，避免一次跳过中间的发送。
    /// </summary>
    internal sealed class ManualHeartbeatClock : IHeartbeatClock
    {
        readonly object _gate = new object();
        readonly List<Waiter> _waiters = new List<Waiter>();
        long _now;

        public long ElapsedMilliseconds
        {
            get
            {
                lock (_gate)
                    return _now;
            }
        }

        public UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            long milliseconds = delay.TotalMilliseconds <= 0d ? 0L : (long)delay.TotalMilliseconds;
            if (milliseconds <= 0L || cancellationToken.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                    return UniTask.FromCanceled(cancellationToken);
                return UniTask.CompletedTask;
            }

            var waiter = new Waiter();
            lock (_gate)
            {
                waiter.Due = _now + milliseconds;
                _waiters.Add(waiter);
            }

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() =>
                {
                    bool removed;
                    lock (_gate)
                        removed = _waiters.Remove(waiter);
                    if (removed)
                        waiter.Done.TrySetCanceled(cancellationToken);
                });
            }

            return waiter.Done.Task;
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));

            long step = (long)delta.TotalMilliseconds;
            long target;
            lock (_gate)
                target = _now + step;

            for (int guard = 0; guard < 100000; guard++)
            {
                Waiter due = null;
                lock (_gate)
                {
                    if (_now >= target)
                        return;
                    due = TakeDue(target);
                    if (due == null)
                    {
                        _now = target;
                        return;
                    }

                    _now = due.Due;
                }

                due.Registration.Dispose();
                due.Done.TrySetResult();
            }

            throw new InvalidOperationException("心跳时钟推进没有收敛");
        }

        Waiter TakeDue(long target)
        {
            int index = -1;
            for (int i = 0; i < _waiters.Count; i++)
            {
                if (_waiters[i].Due > target)
                    continue;
                if (index < 0 || _waiters[i].Due < _waiters[index].Due)
                    index = i;
            }

            if (index < 0)
                return null;

            Waiter waiter = _waiters[index];
            _waiters.RemoveAt(index);
            return waiter;
        }

        sealed class Waiter
        {
            public long Due;
            public readonly UniTaskCompletionSource Done = new UniTaskCompletionSource();
            public CancellationTokenRegistration Registration;
        }
    }
}

