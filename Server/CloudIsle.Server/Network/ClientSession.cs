using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CloudIsle.Net;

namespace CloudIsle.Server.Network
{
    /// <summary>
    /// 一次 TCP 连接上的接收和发送。接收交给 PipelineFrameReader。
    /// 发送只有一个循环，帧在入队时已经是自己的字节。
    /// </summary>
    public sealed class ClientSession
    {
        const int LifeIdle = 0;
        const int LifeRunning = 1;
        const int LifeClosed = 2;

        static readonly AsyncLocal<bool> InSessionLoop = new AsyncLocal<bool>();

        readonly TcpClient? _client;
        readonly Stream _stream;
        readonly IFrameTransport _transport;
        readonly Channel<PendingSend> _outbound;
        readonly CancellationTokenSource _sessionCts = new CancellationTokenSource();
        readonly TaskCompletionSource _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<ClientSession, Packet, CancellationToken, Task>? _deliver;
        int _life = LifeIdle;
        int _closeOnce;
        int _ioReleased;
        string _closeReason = "会话结束";

        ClientSession(
            long sessionId,
            TcpClient? client,
            Stream stream,
            IFrameTransport transport,
            int sendQueueCapacity)
        {
            if (sessionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));
            if (sendQueueCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(sendQueueCapacity));

            SessionId = sessionId;
            SendQueueCapacity = sendQueueCapacity;
            _client = client;
            _stream = stream;
            _transport = transport;
            _outbound = Channel.CreateBounded<PendingSend>(new BoundedChannelOptions(sendQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        /// <summary>本连接的编号。进程内递增，关闭后不复用。</summary>
        public long SessionId { get; }

        /// <summary>一旦开始关闭即为 true，不必等待所有收发回调结束。</summary>
        public bool IsCloseRequested => Volatile.Read(ref _closeOnce) != 0;

        /// <summary>发送队列能同时放下的帧数。正在写出的那一帧不占这个名额。</summary>
        internal int SendQueueCapacity { get; }

        /// <summary>会话运行结束并且排队发送已经收尾。</summary>
        internal Task Completion => _closed.Task;

        /// <summary>从刚接受的套接字创建会话。</summary>
        /// <param name="sessionId">这条连接的编号，不会复用。</param>
        /// <param name="client">已经 Accept 的套接字。</param>
        /// <param name="sendQueueCapacity">发送队列最多容纳的帧数。</param>
        /// <param name="onPacket">每个完整包交给它。</param>
        internal static ClientSession Accept(
            long sessionId,
            TcpClient client,
            int sendQueueCapacity,
            Func<ClientSession, Packet, CancellationToken, Task>? onPacket)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var session = new ClientSession(
                sessionId,
                client,
                stream,
                new StreamFrameTransport(stream),
                sendQueueCapacity);
            session._deliver = onPacket;
            return session;
        }

        /// <summary>用测试传输创建会话，不打开真实套接字。</summary>
        /// <param name="sessionId">这条连接的编号。</param>
        /// <param name="readStream">接收端读取的流。</param>
        /// <param name="transport">测试用的写边界。</param>
        /// <param name="sendQueueCapacity">发送队列最多容纳的帧数。</param>
        /// <param name="onPacket">每个完整包交给它。</param>
        internal static ClientSession CreateForTransport(
            long sessionId,
            Stream readStream,
            IFrameTransport transport,
            int sendQueueCapacity,
            Func<ClientSession, Packet, CancellationToken, Task>? onPacket)
        {
            var session = new ClientSession(sessionId, client: null, readStream, transport, sendQueueCapacity);
            session._deliver = onPacket;
            return session;
        }

        /// <summary>同时跑接收和发送。任一环结束就关闭本会话。</summary>
        /// <param name="ct">停止这个会话。</param>
        public async Task RunAsync(CancellationToken ct)
        {
            int previous = Interlocked.CompareExchange(ref _life, LifeRunning, LifeIdle);
            if (previous == LifeRunning)
                throw new InvalidOperationException("会话已经在运行");
            if (previous == LifeClosed)
            {
                _closed.TrySetResult();
                return;
            }

            InSessionLoop.Value = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            Task sendTask = SendLoopAsync(linked.Token);
            try
            {
                if (Volatile.Read(ref _closeOnce) != 0)
                    return;

                PipeReader reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
                await PipelineFrameReader.ReadLoopAsync(
                    reader,
                    packet => DeliverAsync(packet, linked.Token),
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                BeginClose(_closeReason);
                try
                {
                    await sendTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Observe(Task.FromException(ex));
                }

                ReleaseIo();
                Interlocked.Exchange(ref _life, LifeClosed);
                _closed.TrySetResult();
                try
                {
                    _sessionCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>把一帧编成独立字节后排队。队列已满时关闭本连接。</summary>
        /// <param name="packet">要发送的包。正文不能为空。</param>
        /// <param name="ct">入队前取消则不发送。</param>
        public async Task SendAsync(Packet packet, CancellationToken ct)
        {
            if (packet.Body == null)
                throw new ArgumentNullException(nameof(packet));

            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closeOnce) != 0)
                throw new IOException("会话已关闭");

            byte[] ownedFrame = FrameCodec.Encode(packet.MsgId, packet.Seq, packet.Body);
            var pending = new PendingSend(ownedFrame);
            if (Volatile.Read(ref _closeOnce) != 0 || !_outbound.Writer.TryWrite(pending))
            {
                if (Volatile.Read(ref _closeOnce) == 0)
                    BeginClose("发送队列已满");

                var error = new IOException(Volatile.Read(ref _closeOnce) != 0 ? _closeReason : "发送队列已满");
                pending.Completed.TrySetException(error);
                Observe(pending.Completed.Task);
                throw error;
            }

            Observe(pending.Completed.Task);
            await pending.Completed.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>关闭会话并等待收发结束。从收发循环里调用时只发信号。</summary>
        /// <param name="reason">关闭原因，写入等待中的发送。</param>
        /// <param name="ct">等待收发结束时的取消令牌。</param>
        public async Task CloseAsync(string reason, CancellationToken ct)
        {
            RequestClose(reason);
            if (InSessionLoop.Value)
                return;

            int previous = Interlocked.CompareExchange(ref _life, LifeClosed, LifeIdle);
            if (previous == LifeIdle)
            {
                ReleaseIo();
                _closed.TrySetResult();
                return;
            }

            await _closed.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 只发起关闭，不等待收发任务结束。业务队列关闭触发自身收包的连接时使用它，避免回环等待。
        /// 需要等待完整收尾的外部调用方仍可使用 CloseAsync 或观察 Completion。
        /// </summary>
        /// <param name="reason">关闭原因。空白时使用“关闭”。</param>
        public void RequestClose(string reason)
        {
            BeginClose(string.IsNullOrWhiteSpace(reason) ? "关闭" : reason);
        }

        /// <summary>把一帧交给业务回调。</summary>
        /// <param name="packet">已经复制出来的包。</param>
        /// <param name="ct">取消这次回调。</param>
        async Task DeliverAsync(Packet packet, CancellationToken ct)
        {
            InSessionLoop.Value = true;
            if (_deliver == null)
                return;
            await _deliver(this, packet, ct).ConfigureAwait(false);
        }

        /// <summary>一次写完一整帧再取下一帧。</summary>
        /// <param name="ct">停止发送循环。</param>
        async Task SendLoopAsync(CancellationToken ct)
        {
            InSessionLoop.Value = true;
            try
            {
                while (await _outbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_outbound.Reader.TryRead(out PendingSend? pending))
                    {
                        try
                        {
                            ReadOnlyMemory<byte> remaining = pending!.Frame;
                            while (!remaining.IsEmpty)
                            {
                                int written = await _transport.WriteAsync(remaining, ct).ConfigureAwait(false);
                                if (written <= 0 || written > remaining.Length)
                                    throw new IOException("发送写出长度非法");
                                remaining = remaining.Slice(written);
                            }

                            pending.Completed.TrySetResult();
                        }
                        catch (Exception ex)
                        {
                            pending.Completed.TrySetException(ex);
                            BeginClose(_closeReason == "会话结束" ? "发送失败" : _closeReason);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                BeginClose("发送失败");
            }
            finally
            {
                while (_outbound.Reader.TryRead(out PendingSend? pending))
                    pending!.Completed.TrySetException(new IOException(_closeReason));
            }
        }

        /// <summary>标记关闭并让还排着队的发送失败。</summary>
        /// <param name="reason">关闭原因。</param>
        void BeginClose(string reason)
        {
            if (Interlocked.Exchange(ref _closeOnce, 1) != 0)
                return;

            if (!string.IsNullOrWhiteSpace(reason))
                _closeReason = reason;

            try
            {
                _outbound.Writer.TryComplete();
            }
            catch (ChannelClosedException)
            {
            }

            try
            {
                _sessionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            ReleaseIo();
        }

        /// <summary>释放套接字或测试流。</summary>
        void ReleaseIo()
        {
            if (Interlocked.Exchange(ref _ioReleased, 1) != 0)
                return;

            try
            {
                _stream.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_client == null)
                return;

            try
            {
                _client.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>观察收发任务，避免未观察异常。</summary>
        /// <param name="task">要观察的任务。</param>
        static void Observe(Task task)
        {
            if (task.IsCompleted)
            {
                _ = task.Exception;
                return;
            }

            _ = task.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        sealed class PendingSend
        {
            /// <summary>记住已经编好的一帧。</summary>
            /// <param name="frame">这一帧自己的字节。</param>
            public PendingSend(byte[] frame)
            {
                Frame = frame;
                Completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public byte[] Frame { get; }

            public TaskCompletionSource Completed { get; }
        }
    }

    /// <summary>
    /// 会话写出的最小边界。返回本次实际写出的字节数，可以小于缓冲长度。
    /// </summary>
    internal interface IFrameTransport
    {
        /// <summary>写出一段字节。返回值可以小于缓冲长度。</summary>
        /// <param name="buffer">要写出的字节。</param>
        /// <param name="cancellationToken">取消这次写出。</param>
        ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    }

    /// <summary>把一截字节写到流上。NetworkStream 一次写完时返回全部长度。</summary>
    internal sealed class StreamFrameTransport : IFrameTransport
    {
        readonly Stream _stream;

        /// <summary>用流写出帧。</summary>
        /// <param name="stream">底层流。</param>
        public StreamFrameTransport(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>向流写出一段。</summary>
        /// <param name="buffer">要写出的字节。</param>
        /// <param name="cancellationToken">取消这次写出。</param>
        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.Length;
        }
    }
}
