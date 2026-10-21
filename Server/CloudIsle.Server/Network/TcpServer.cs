using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CloudIsle.Server.Network
{
    /// <summary>
    /// 接受 TCP 连接并为每条连接分配一个不复用的会话。
    /// 某个会话失败只结束该会话，不停止监听。
    /// </summary>
    public sealed class TcpServer
    {
        readonly Func<ClientSession, CloudIsle.Net.Packet, CancellationToken, Task>? _onPacket;
        readonly int _sendQueueCapacity;
        readonly ConcurrentDictionary<long, ClientSession> _sessions = new ConcurrentDictionary<long, ClientSession>();
        readonly HashSet<Task> _runs = new HashSet<Task>();
        readonly object _runLock = new object();
        readonly TaskCompletionSource _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static long _nextSessionId;
        int _runOnce;

        /// <summary>记下收包回调和每条连接的发送队列长度。</summary>
        /// <param name="onPacket">每个完整包交给它。为空时只接收，不回调。</param>
        /// <param name="sendQueueCapacity">每条连接最多排队的帧数。小于 1 时抛异常。</param>
        public TcpServer(Func<ClientSession, CloudIsle.Net.Packet, CancellationToken, Task>? onPacket, int sendQueueCapacity = 32)
        {
            if (sendQueueCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(sendQueueCapacity));

            _onPacket = onPacket;
            _sendQueueCapacity = sendQueueCapacity;
        }

        /// <summary>监听套接字已经绑定。端口 0 时这里能读到系统分配的端口。</summary>
        public Task Started => _started.Task;

        /// <summary>实际绑定的本机端点。尚未开始监听时为空。</summary>
        public IPEndPoint? LocalEndpoint { get; private set; }

        /// <summary>当前仍由本监听器跟踪的连接数。会话循环结束后移除，不是历史累计，也不是已认证玩家数。</summary>
        public int ConnectionCount => _sessions.Count;

        /// <summary>监听 endpoint。取消时停止接受连接，关闭已经接入的会话，并等待这些会话任务结束。</summary>
        /// <param name="endpoint">要绑定的本机端点。端口 0 由系统分配。</param>
        /// <param name="ct">停止监听和已接入的会话。</param>
        public async Task RunAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));
            if (Interlocked.Exchange(ref _runOnce, 1) != 0)
                throw new InvalidOperationException("监听已经在运行");

            var listener = new TcpListener(endpoint);
            try
            {
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    _started.TrySetException(ex);
                    throw;
                }

                LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
                _started.TrySetResult();

                using (ct.Register(static state =>
                {
                    var tcp = (TcpListener)state!;
                    try
                    {
                        tcp.Stop();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (SocketException)
                    {
                    }
                }, listener))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        TcpClient client;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch (SocketException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (InvalidOperationException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        if (ct.IsCancellationRequested)
                        {
                            client.Dispose();
                            break;
                        }

                        long sessionId = Interlocked.Increment(ref _nextSessionId);
                        ClientSession session = ClientSession.Accept(sessionId, client, _sendQueueCapacity, _onPacket);
                        _sessions[sessionId] = session;
                        Task run = RunSessionAsync(session, ct);
                        lock (_runLock)
                            _runs.Add(run);
                        _ = run.ContinueWith(
                            finished =>
                            {
                                lock (_runLock)
                                    _runs.Remove(finished);
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }
            finally
            {
                try
                {
                    listener.Stop();
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                foreach (ClientSession session in _sessions.Values)
                {
                    try
                    {
                        await session.CloseAsync("服务器停止", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }

                Task[] runs;
                lock (_runLock)
                    runs = _runs.ToArray();

                try
                {
                    await Task.WhenAll(runs).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                _started.TrySetResult();
            }
        }

        /// <summary>跑一个会话。它自己的异常不传到别的会话。</summary>
        /// <param name="session">已经接入的会话。</param>
        /// <param name="ct">停止这个会话。</param>
        async Task RunSessionAsync(ClientSession session, CancellationToken ct)
        {
            try
            {
                await session.RunAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    await session.CloseAsync("会话失败", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                _sessions.TryRemove(session.SessionId, out _);
            }
        }
    }
}
