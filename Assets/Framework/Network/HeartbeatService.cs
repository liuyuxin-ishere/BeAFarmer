using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// 按固定间隔发送心跳，并在静默超时后断开当前连接一次。
    /// 发送间隔从发起时刻计算。静默监测不跟着某一次请求一起等待。
    /// </summary>
    public sealed class HeartbeatService
    {
        /// <summary>正式环境使用的发送间隔。</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

        /// <summary>正式环境使用的静默上限。超过后断开一次。</summary>
        public static readonly TimeSpan DefaultSilenceTimeout = TimeSpan.FromSeconds(30);

        static readonly TimeSpan RequestTimeout = NetClient.DefaultTimeout;

        readonly NetClient _client;
        readonly ServerClock _serverClock;
        readonly IHeartbeatClock _clock;
        readonly SynchronizationContext _ownerContext;
        readonly int _ownerThreadId;
        readonly HashSet<Task> _watches = new HashSet<Task>();
        CancellationTokenSource _loop;
        int _running;
        int _epoch;
        int _disconnected;
        long _lastValid;

        /// <summary>
        /// 绑定创建时所在的线程。之后的发送和断线都回到这条线程。
        /// </summary>
        /// <param name="client">已经连上的客户端。</param>
        /// <param name="serverClock">收到合法心跳后更新的显示时钟。</param>
        public HeartbeatService(NetClient client, ServerClock serverClock)
            : this(client, serverClock, new SystemHeartbeatClock())
        {
        }

        /// <summary>用指定时钟创建心跳。测试用它推进 10 秒和 30 秒。</summary>
        /// <param name="client">发送心跳并在静默时断开的客户端。</param>
        /// <param name="serverClock">合法响应用来校时的时钟。</param>
        /// <param name="clock">间隔和静默读取的单调时钟。</param>
        internal HeartbeatService(NetClient client, ServerClock serverClock, IHeartbeatClock clock)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (serverClock == null)
                throw new ArgumentNullException(nameof(serverClock));
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _client = client;
            _serverClock = serverClock;
            _clock = clock;
            _ownerContext = SynchronizationContext.Current;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 跑一份心跳循环。已有循环时直接失败，避免两份循环同时断线。
        /// </summary>
        /// <param name="interval">两次发起之间的间隔，必须大于 0。</param>
        /// <param name="silenceTimeout">距离上次合法响应的上限，必须大于 0。</param>
        /// <param name="ct">取消发送、等待和静默监测。</param>
        public async UniTask RunAsync(TimeSpan interval, TimeSpan silenceTimeout, CancellationToken ct)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            if (silenceTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(silenceTimeout));
            EnsureOwner();
            ct.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new InvalidOperationException("心跳循环已在运行");

            int epoch = Interlocked.Increment(ref _epoch);
            Interlocked.Exchange(ref _disconnected, 0);
            Interlocked.Exchange(ref _lastValid, _clock.ElapsedMilliseconds);
            var loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = loop;
            try
            {
                Task sending = SendLoop(interval, loop.Token, epoch).AsTask();
                Task silence = SilenceLoop(silenceTimeout, loop.Token, epoch).AsTask();
                await Task.WhenAny(sending, silence);
                CancelLoop(loop);
                try
                {
                    await Task.WhenAll(sending, silence);
                }
                finally
                {
                    await DrainWatches();
                }
            }
            finally
            {
                CancelLoop(loop);
                if (ReferenceEquals(_loop, loop))
                    _loop = null;
                loop.Dispose();
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>按发起时刻重复发送。响应还没回来也不把耗时加进间隔。</summary>
        /// <param name="interval">两次发起之间的间隔。</param>
        /// <param name="token">循环自己的取消令牌。</param>
        /// <param name="epoch">这一份循环的代号。旧循环不能再发送。</param>
        async UniTask SendLoop(TimeSpan interval, CancellationToken token, int epoch)
        {
            try
            {
                long intervalMs = Milliseconds(interval);
                while (!token.IsCancellationRequested)
                {
                    await SwitchToOwner(token);
                    if (token.IsCancellationRequested)
                        return;

                    long started = _clock.ElapsedMilliseconds;
                    Track(SendOne(token, epoch));
                    long wait = intervalMs - (_clock.ElapsedMilliseconds - started);
                    if (wait <= 0)
                        continue;
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(wait), token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>距离上次合法响应达到上限时只断线一次。</summary>
        /// <param name="silenceTimeout">允许的静默时长。</param>
        /// <param name="token">循环自己的取消令牌。</param>
        /// <param name="epoch">这一份循环的代号。</param>
        async UniTask SilenceLoop(TimeSpan silenceTimeout, CancellationToken token, int epoch)
        {
            try
            {
                long silenceMs = Milliseconds(silenceTimeout);
                while (!token.IsCancellationRequested)
                {
                    await SwitchToOwner(token);
                    if (token.IsCancellationRequested)
                        return;

                    long remain = silenceMs - (_clock.ElapsedMilliseconds - Interlocked.Read(ref _lastValid));
                    if (remain <= 0)
                    {
                        DisconnectOnce(epoch);
                        return;
                    }

                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(remain), token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>发出一次心跳。只有 code 为 0 才校时并刷新静默起点。</summary>
        /// <param name="token">循环自己的取消令牌。</param>
        /// <param name="epoch">这一份循环的代号。</param>
        async UniTask SendOne(CancellationToken token, int epoch)
        {
            HeartbeatResp response;
            try
            {
                var request = new HeartbeatReq
                {
                    ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                response = await _client.SendRequestAsync<HeartbeatResp>(request, RequestTimeout, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                await SwitchToOwner(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || epoch != Volatile.Read(ref _epoch))
                return;
            if (response == null || response.Code != 0)
                return;

            long localUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _serverClock.Synchronize(response.ServerTime, localUnixMs);
            Interlocked.Exchange(ref _lastValid, _clock.ElapsedMilliseconds);
        }

        /// <summary>记下一项还在进行的工作，完成后就从集合移除。</summary>
        /// <param name="work">要观察的心跳工作。</param>
        void Track(UniTask work)
        {
            Task active = Observe(work).AsTask();
            lock (_watches)
                _watches.Add(active);
            _ = active.ContinueWith(
                finished =>
                {
                    lock (_watches)
                        _watches.Remove(finished);
                    _ = finished.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>等待这项工作结束，并吞掉取消异常。</summary>
        /// <param name="work">要观察的工作。</param>
        static async UniTask Observe(UniTask work)
        {
            try
            {
                await work;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>取消并等待当前还在集合里的工作。</summary>
        async UniTask DrainWatches()
        {
            Task[] pending;
            lock (_watches)
            {
                pending = new Task[_watches.Count];
                _watches.CopyTo(pending);
                _watches.Clear();
            }

            await Task.WhenAll(pending);
        }

        /// <summary>这一份循环还有效时只断开一次。</summary>
        /// <param name="epoch">要求仍是当前循环。</param>
        void DisconnectOnce(int epoch)
        {
            if (epoch != Volatile.Read(ref _epoch))
                return;
            if (Interlocked.Exchange(ref _disconnected, 1) != 0)
                return;
            CancelLoop(_loop);
            _client.Disconnect("心跳静默");
        }

        /// <summary>切回创建心跳服务的线程。</summary>
        /// <param name="token">取消这次切换。</param>
        UniTask SwitchToOwner(CancellationToken token)
        {
            if (Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
                return UniTask.CompletedTask;
            if (_ownerContext == null)
                throw new InvalidOperationException("心跳续延离开了创建线程，且没有同步上下文");

            var done = new UniTaskCompletionSource();
            _ownerContext.Post(
                _ =>
                {
                    if (token.IsCancellationRequested)
                        done.TrySetCanceled(token);
                    else
                        done.TrySetResult();
                },
                null);
            return done.Task;
        }

        /// <summary>不是创建线程时抛异常。</summary>
        void EnsureOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("只能在创建心跳服务的线程运行");
        }

        /// <summary>取消循环自己的令牌。已经释放时忽略。</summary>
        /// <param name="loop">循环的取消源。</param>
        static void CancelLoop(CancellationTokenSource loop)
        {
            if (loop == null)
                return;
            try
            {
                if (!loop.IsCancellationRequested)
                    loop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>把时间间隔换成整数毫秒。</summary>
        /// <param name="value">要换算的间隔。</param>
        static long Milliseconds(TimeSpan value)
        {
            double ms = value.TotalMilliseconds;
            if (ms >= long.MaxValue)
                return long.MaxValue;
            return Math.Max(1L, (long)Math.Ceiling(ms));
        }
    }

    internal interface IHeartbeatClock
    {
        long ElapsedMilliseconds { get; }

        /// <summary>等待一段时间。</summary>
        /// <param name="delay">要等待的时长。</param>
        /// <param name="cancellationToken">取消这次等待。</param>
        UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    sealed class SystemHeartbeatClock : IHeartbeatClock
    {
        readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

        public long ElapsedMilliseconds
        {
            get
            {
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - _start;
                return (long)(ticks * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        /// <summary>按真实经过时间等待。不受 Time.timeScale 影响。</summary>
        /// <param name="delay">要等待的时长。</param>
        /// <param name="cancellationToken">取消这次等待。</param>
        public async UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero)
                return;
            await Task.Delay(delay, cancellationToken);
        }
    }

}

