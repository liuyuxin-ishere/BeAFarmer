using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// 断线后按 1、2、4、8、16、30 秒退避重连，30 秒之后保持 30 秒。
    /// 只负责连接；Token 和快照由调用方回调处理。
    /// </summary>
    public sealed class ReconnectService
    {
        static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16, 30 };

        readonly NetClient _client;
        readonly TcpTransport _transport;
        readonly IReconnectBoundary _boundary;
        readonly SynchronizationContext _ownerContext;
        readonly int _ownerThreadId;
        CancellationTokenSource _receive;
        int _running;
        int _ticket;
        /// <summary>最近一次退避/连接尝试序号（验收可观测；从 1 起）。</summary>
        public int LastAttempt { get; private set; }
        /// <summary>最近一次尝试的 UTC 时间。</summary>
        public DateTime LastAttemptUtc { get; private set; }

        /// <summary>
        /// 绑定创建时所在的线程。之后的连接、接收和回调都回到这条线程。
        /// </summary>
        /// <param name="client">要恢复的客户端。必须和 <paramref name="transport"/> 是同一条连接。</param>
        /// <param name="transport">用来重新连接的传输。</param>
        public ReconnectService(NetClient client, TcpTransport transport)
            : this(client, transport, new LiveReconnectBoundary())
        {
        }

        /// <summary>用可控的连接和等待边界创建重连服务。</summary>
        /// <param name="client">恢复成功后继续使用的客户端。</param>
        /// <param name="transport">用来发起连接的传输。</param>
        /// <param name="boundary">测试替换延迟和开口的边界。</param>
        internal ReconnectService(NetClient client, TcpTransport transport, IReconnectBoundary boundary)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));
            if (boundary == null)
                throw new ArgumentNullException(nameof(boundary));

            _client = client;
            _transport = transport;
            _boundary = boundary;
            _ownerContext = SynchronizationContext.Current;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 退避重连，并在连上后调用恢复回调。
        /// true 表示快照已恢复并停止；false 表示回调要求停止。
        /// 取消时抛出取消，并停掉这一轮的连接和等待。
        /// </summary>
        /// <param name="endpoint">重连目标。</param>
        /// <param name="restoreSessionAsync">连上后恢复会话。true 停止；false 表示不可恢复，也停止。</param>
        /// <param name="ct">取消在途连接、退避等待和这一轮回调等待。</param>
        public async UniTask<bool> RecoverAsync(
            ServerEndpoint endpoint,
            Func<CancellationToken, UniTask<bool>> restoreSessionAsync,
            CancellationToken ct)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));
            if (restoreSessionAsync == null)
                throw new ArgumentNullException(nameof(restoreSessionAsync));
            EnsureOwner();
            ct.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new InvalidOperationException("重连已在运行");

            try
            {
                await CleanupAttempt();
                int step = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int ticket = Interlocked.Increment(ref _ticket);
                    try
                    {
                        await ConnectGuarded(endpoint, ct);
                    }
                    catch (Exception ex)
                    {
                        await CleanupAttempt();
                        if (!CanRetry(ex, ct))
                            throw;
                        await DelayGuarded(NextDelay(ref step), ct);
                        continue;
                    }

                    await SwitchToOwner(CancellationToken.None);
                    if (ct.IsCancellationRequested || ticket != Volatile.Read(ref _ticket))
                    {
                        await CleanupAttempt();
                        throw new OperationCanceledException(ct);
                    }

                    bool restored;
                    try
                    {
                        StartReceive();
                        restored = await RestoreGuarded(restoreSessionAsync, ct, ticket);
                    }
                    catch (Exception ex)
                    {
                        await CleanupAttempt();
                        if (!CanRetry(ex, ct))
                            throw;
                        await DelayGuarded(NextDelay(ref step), ct);
                        continue;
                    }

                    if (ticket != Volatile.Read(ref _ticket))
                        throw new OperationCanceledException(ct);
                    if (!restored)
                    {
                        await CleanupAttempt();
                        return false;
                    }

                    return true;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>发起一次连接，并和取消等待竞争。</summary>
        /// <param name="endpoint">这一次要连接的端点。</param>
        /// <param name="ct">取消这次连接。</param>
        async UniTask ConnectGuarded(ServerEndpoint endpoint, CancellationToken ct)
        {
            Task connecting = _boundary.ConnectAsync(_transport, endpoint, ct);
            if (!await FinishedFirst(connecting, ct))
            {
                _transport.Close();
                Observe(connecting);
                throw new OperationCanceledException(ct);
            }

            await connecting;
        }

        /// <summary>等待一轮退避，并和取消等待竞争。</summary>
        /// <param name="delay">这一轮要等的时长。</param>
        /// <param name="ct">取消这次等待。</param>
        async UniTask DelayGuarded(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Task waiting = _boundary.DelayAsync(delay, ct).AsTask();
            if (!await FinishedFirst(waiting, ct))
            {
                Observe(waiting);
                throw new OperationCanceledException(ct);
            }

            await waiting;
        }

        /// <summary>调用恢复回调，并和取消等待竞争。</summary>
        /// <param name="restore">连上后的恢复回调。</param>
        /// <param name="ct">取消这次回调等待。</param>
        /// <param name="ticket">这一次尝试的编号。旧回调不能作用于新尝试。</param>
        async UniTask<bool> RestoreGuarded(
            Func<CancellationToken, UniTask<bool>> restore,
            CancellationToken ct,
            int ticket)
        {
            await SwitchToOwner(CancellationToken.None);
            Task<bool> pending = restore(ct).AsTask();
            if (!await FinishedFirst(pending, ct))
            {
                Interlocked.Increment(ref _ticket);
                Observe(pending);
                throw new OperationCanceledException(ct);
            }

            if (ticket != Volatile.Read(ref _ticket))
                throw new OperationCanceledException(ct);
            return await pending;
        }

        /// <summary>工作和取消谁先结束就返回谁。工作先结束时返回 true。</summary>
        /// <param name="work">正在进行的连接、等待或回调。</param>
        /// <param name="ct">取消令牌。</param>
        async UniTask<bool> FinishedFirst(Task work, CancellationToken ct)
        {
            if (work == null)
                throw new InvalidOperationException("重连任务为空");
            if (!ct.CanBeCanceled)
                return true;

            var waiter = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task cancelWait = Task.Delay(Timeout.Infinite, waiter.Token);
            await Task.WhenAny(work, cancelWait);
            waiter.Cancel();
            Observe(cancelWait, waiter);
            return work.IsCompleted && !ct.IsCancellationRequested;
        }

        /// <summary>在创建线程上启动接收。启动失败时走同一套清理。</summary>
        void StartReceive()
        {
            CancelReceive();
            _receive = new CancellationTokenSource();
            _client.BeginReceive(_receive.Token);
        }

        /// <summary>清掉这一次失败尝试留下的等待和接收。</summary>
        async UniTask CleanupAttempt()
        {
            await SwitchToOwner(CancellationToken.None);
            CancelReceive();
            _client.Disconnect("重连尝试结束");
        }

        /// <summary>取消旧接收，并在结束后释放取消源。</summary>
        void CancelReceive()
        {
            CancellationTokenSource receive = _receive;
            _receive = null;
            if (receive == null)
                return;
            try
            {
                if (!receive.IsCancellationRequested)
                    receive.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                receive.Dispose();
            }
        }

        /// <summary>切回创建重连服务的线程。</summary>
        /// <param name="token">取消这次切换。</param>
        UniTask SwitchToOwner(CancellationToken token)
        {
            if (Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
                return UniTask.CompletedTask;
            if (_ownerContext == null)
                throw new InvalidOperationException("重连续延离开了创建线程，且没有同步上下文");

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
                throw new InvalidOperationException("只能在创建重连服务的线程运行");
        }

        /// <summary>按 1、2、4、8、16、30 秒取下一轮等待，之后保持 30 秒。</summary>
        /// <param name="step">已经失败的次数，调用后加一。</param>
        TimeSpan NextDelay(ref int step)
        {
            int seconds = step < BackoffSeconds.Length ? BackoffSeconds[step] : 30;
            LastAttempt = step + 1;
            LastAttemptUtc = DateTime.UtcNow;
            // 验收可观测：Player.log 必须能读到真实 1/2/4/8/16/30 序列。
            UnityEngine.Debug.Log(
                "[ReconnectService] utc=" + LastAttemptUtc.ToString("o")
                + " backoff-wait-seconds=" + seconds + " attempt=" + step);
            if (step < int.MaxValue)
                step++;
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>只有连接失败这类可恢复错误才继续退避。</summary>
        /// <param name="ex">这一轮捕获的异常。</param>
        /// <param name="ct">调用方的取消令牌。已取消时不重试。</param>
        static bool CanRetry(Exception ex, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || ex is OperationCanceledException)
                return false;
            return ex is IOException || ex is SocketException || ex is TimeoutException;
        }

        /// <summary>观察这项工作，结束后释放取消源。</summary>
        /// <param name="work">要观察的工作。</param>
        /// <param name="source">工作结束后释放的取消源。</param>
        static void Observe(Task work, CancellationTokenSource source)
        {
            if (work == null)
            {
                DisposeQuiet(source);
                return;
            }

            work.ContinueWith(
                finished =>
                {
                    _ = finished.Exception;
                    DisposeQuiet(source);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>观察这项工作，吞掉取消和已经处理过的异常。</summary>
        /// <param name="work">要观察的工作。</param>
        static void Observe(Task work)
        {
            Observe(work, null);
        }

        /// <summary>释放取消源。已经释放时忽略。</summary>
        /// <param name="source">要释放的取消源。</param>
        static void DisposeQuiet(CancellationTokenSource source)
        {
            if (source == null)
                return;
            try
            {
                source.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        sealed class LiveReconnectBoundary : IReconnectBoundary
        {
        /// <summary>按真实时间等待一轮退避。</summary>
        /// <param name="delay">要等待的时长。</param>
        /// <param name="cancellationToken">取消这次等待。</param>
            public async UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                if (delay <= TimeSpan.Zero)
                    return;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

        /// <summary>让传输连到端点。</summary>
        /// <param name="transport">要连接的传输。</param>
        /// <param name="endpoint">连接目标。</param>
        /// <param name="cancellationToken">取消这次连接。</param>
            public Task ConnectAsync(TcpTransport transport, ServerEndpoint endpoint, CancellationToken cancellationToken)
            {
                if (transport == null)
                    throw new ArgumentNullException(nameof(transport));
                return transport.ConnectAsync(endpoint, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 测试用来替换真实等待和开口。正式路径走套接字连接。
    /// </summary>
    internal interface IReconnectBoundary
    {
        /// <summary>等待一轮退避。</summary>
        /// <param name="delay">要等待的时长。</param>
        /// <param name="cancellationToken">取消这次等待。</param>
        UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

        /// <summary>让传输连到端点。</summary>
        /// <param name="transport">要连接的传输。</param>
        /// <param name="endpoint">连接目标。</param>
        /// <param name="cancellationToken">取消这次连接。</param>
        Task ConnectAsync(TcpTransport transport, ServerEndpoint endpoint, CancellationToken cancellationToken);
    }
}
