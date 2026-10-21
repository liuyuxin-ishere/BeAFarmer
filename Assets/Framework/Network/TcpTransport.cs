using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;

namespace Game.Framework.Network
{
    /// <summary>
    /// 测试用来替代套接字的发送口。每次调用可以只接受缓冲区里的一部分字节。
    /// </summary>
    internal interface ITransportSend
    {
        /// <summary>发送缓冲区中的一段。返回值可以小于 count。</summary>
        /// <param name="buffer">待发送的字节。</param>
        /// <param name="offset">从 buffer 的这个下标开始。</param>
        /// <param name="count">这一次最多发送的字节数。</param>
        /// <param name="cancellationToken">取消这次发送。</param>
        Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

        /// <summary>关闭这个发送口。重复调用应无效果。</summary>
        void Close();
    }

    /// <summary>
    /// 测试或套接字用来按块读取。一次调用可以只填满缓冲区的一部分。
    /// </summary>
    internal interface ITransportReceive
    {
        /// <summary>读入一段字节。返回 0 表示对端已经关闭。</summary>
        /// <param name="buffer">接收缓冲。</param>
        /// <param name="offset">从 buffer 的这个下标开始写入。</param>
        /// <param name="count">这一次最多读取的字节数。</param>
        /// <param name="cancellationToken">取消这次读取。</param>
        Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 客户端 TCP 连接。同一连接上并发提交的帧按整帧依次发送。
    /// 接收在后台切帧，回调只负责把包交出去。
    /// </summary>
    public sealed class TcpTransport
    {
        readonly object gate = new object();
        readonly Queue<PendingSend> queue = new Queue<PendingSend>();
        ITransportSend channel;
        CancellationTokenSource connectCts;
        int generation;
        int receiveId;
        bool pumpRunning;
        bool connecting;
        bool receiveRunning;

        /// <summary>连接到端点。已经连着或正在连接时必须先关闭。</summary>
        /// <param name="endpoint">主机和端口。</param>
        /// <param name="cancellationToken">取消这次连接。</param>
        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken cancellationToken)
        {
            return ConnectAttemptAsync(endpoint, OpenSocketAsync, cancellationToken);
        }

        /// <summary>用指定的打开函数连接。测试用它停住连接，避免公网 DNS。</summary>
        /// <param name="endpoint">主机和端口。</param>
        /// <param name="open">打开发送口。测试可以在这里暂停。</param>
        /// <param name="cancellationToken">取消这次连接。</param>
        internal Task ConnectAttemptAsync(
            ServerEndpoint endpoint,
            Func<ServerEndpoint, CancellationToken, Task<ITransportSend>> open,
            CancellationToken cancellationToken)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));
            if (open == null)
                throw new ArgumentNullException(nameof(open));
            cancellationToken.ThrowIfCancellationRequested();

            int attempt;
            CancellationTokenSource attemptCts;
            lock (gate)
            {
                if (channel != null || connecting)
                    throw new InvalidOperationException("已经连接，先关闭再连接");
                generation++;
                attempt = generation;
                connecting = true;
                connectCts = new CancellationTokenSource();
                attemptCts = connectCts;
            }

            return CompleteConnectAsync(endpoint, open, cancellationToken, attempt, attemptCts);
        }

        /// <summary>等打开函数返回，代次仍有效才挂上连接。</summary>
        /// <param name="endpoint">传给打开函数的端点。</param>
        /// <param name="open">实际打开发送口的函数。</param>
        /// <param name="cancellationToken">调用方的取消令牌。</param>
        /// <param name="attempt">这次尝试记下的连接代次。</param>
        /// <param name="attemptCts">关闭时用来取消这次尝试的源。</param>
        async Task CompleteConnectAsync(
            ServerEndpoint endpoint,
            Func<ServerEndpoint, CancellationToken, Task<ITransportSend>> open,
            CancellationToken cancellationToken,
            int attempt,
            CancellationTokenSource attemptCts)
        {
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptCts.Token);
            ITransportSend opened = null;
            try
            {
                opened = await open(endpoint, linked.Token).ConfigureAwait(false);
                if (opened == null)
                    throw new IOException("连接没有返回发送口");

                lock (gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (attempt != generation || channel != null || !connecting)
                        throw new OperationCanceledException(linked.Token);
                    connecting = false;
                    if (connectCts == attemptCts)
                        connectCts = null;
                    AttachLocked(opened);
                    opened = null;
                }
            }
            catch (Exception)
            {
                CloseQuiet(opened);
                ClearConnectAttempt(attempt, attemptCts);
                if (cancellationToken.IsCancellationRequested || attemptCts.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : attemptCts.Token);
                throw;
            }
            finally
            {
                linked.Dispose();
                if (attemptCts == connectCts)
                {
                    lock (gate)
                    {
                        if (connectCts == attemptCts)
                            connectCts = null;
                    }
                }

                attemptCts.Dispose();
            }
        }

        /// <summary>解析 IPv4 并连上套接字，成功后包成发送口。</summary>
        /// <param name="endpoint">主机和端口。</param>
        /// <param name="cancellationToken">取消解析或连接。</param>
        async Task<ITransportSend> OpenSocketAsync(ServerEndpoint endpoint, CancellationToken cancellationToken)
        {
            IPAddress address = await ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            try
            {
                using (cancellationToken.Register(() => QuietClose(socket)))
                    await socket.ConnectAsync(address, endpoint.Port).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new SocketTransportSend(socket);
            }
            catch (Exception)
            {
                QuietClose(socket);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
        }

        /// <summary>用测试发送口进入已连接状态。正式路径走 ConnectAsync。</summary>
        /// <param name="next">要挂上的发送口。</param>
        internal void Attach(ITransportSend next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));
            lock (gate)
            {
                if (channel != null)
                    throw new InvalidOperationException("已经连接，先关闭再连接");
                AttachLocked(next);
            }
        }

        /// <summary>把一整帧排进发送队列。当前帧发完之前不会发送下一帧。</summary>
        /// <param name="frame">已经编好的一整帧。方法会自己复制一份。</param>
        /// <param name="cancellationToken">取消这一帧。半帧之后取消会使连接作废。</param>
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (frame.Length == 0)
                throw new ArgumentException("帧不能为空", nameof(frame));

            byte[] copy = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, copy, 0, frame.Length);
            PendingSend item = new PendingSend(
                copy,
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

            bool start = false;
            int gen = 0;
            ITransportSend writer = null;
            lock (gate)
            {
                if (channel == null)
                    throw new InvalidOperationException("连接不可用");
                if (cancellationToken.IsCancellationRequested)
                {
                    item.Done.TrySetCanceled(cancellationToken);
                    return item.Done.Task;
                }

                queue.Enqueue(item);
                if (!pumpRunning)
                {
                    pumpRunning = true;
                    start = true;
                    gen = generation;
                    writer = channel;
                }
            }

            if (start)
                Task.Run(() => PumpAsync(gen, writer));
            return item.Done.Task;
        }

        /// <summary>后台读取并切帧。回调里不要操作 Unity 对象，只把包放进队列。</summary>
        /// <param name="onPacket">每解出一帧调用一次。</param>
        /// <param name="cancellationToken">取消接收。代次已经变了就不再断开新连接。</param>
        public Task ReceiveLoopAsync(Action<Packet> onPacket, CancellationToken cancellationToken)
        {
            if (onPacket == null)
                throw new ArgumentNullException(nameof(onPacket));

            int gen;
            int id;
            ITransportSend writer;
            ITransportReceive reader;
            lock (gate)
            {
                if (channel == null)
                    throw new InvalidOperationException("连接不可用");
                if (receiveRunning)
                    throw new InvalidOperationException("接收循环已在运行");
                reader = channel as ITransportReceive;
                if (reader == null)
                    throw new InvalidOperationException("当前连接不能接收");
                receiveRunning = true;
                receiveId++;
                id = receiveId;
                gen = generation;
                writer = channel;
            }

            return RunReceiveAsync(gen, id, writer, reader, onPacket, cancellationToken);
        }

        /// <summary>在后台把字节切成帧，并只断开这次记下的连接。</summary>
        /// <param name="gen">启动时的连接代次。</param>
        /// <param name="id">这次接收循环的编号，用来判断自己是否仍在运行。</param>
        /// <param name="writer">启动时的发送口，用来识别连接有没有被换掉。</param>
        /// <param name="reader">从当前连接读字节。</param>
        /// <param name="onPacket">每解出一帧调用一次。</param>
        /// <param name="cancellationToken">取消这次接收。</param>
        async Task RunReceiveAsync(
            int gen,
            int id,
            ITransportSend writer,
            ITransportReceive reader,
            Action<Packet> onPacket,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[FrameCodec.MaxFrameLength];
            int filled = 0;
            try
            {
                while (true)
                {
                    if (IsStale(gen, writer))
                        return;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Retire(gen, writer);
                        throw new OperationCanceledException(cancellationToken);
                    }

                    int space = buffer.Length - filled;
                    if (space == 0)
                    {
                        Retire(gen, writer);
                        throw new IOException("接收缓冲已满仍不够一帧");
                    }

                    int read;
                    try
                    {
                        read = await reader.ReceiveAsync(buffer, filled, space, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        if (IsStale(gen, writer))
                            return;
                        if (cancellationToken.IsCancellationRequested || ex is OperationCanceledException)
                        {
                            Retire(gen, writer);
                            throw new OperationCanceledException(cancellationToken);
                        }

                        Retire(gen, writer);
                        throw;
                    }

                    if (IsStale(gen, writer))
                        return;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Retire(gen, writer);
                        throw new OperationCanceledException(cancellationToken);
                    }
                    if (read == 0)
                    {
                        Retire(gen, writer);
                        return;
                    }
                    if (read < 0 || read > space)
                    {
                        Retire(gen, writer);
                        throw new IOException("接收字节数无效");
                    }

                    filled += read;
                    int offset = 0;
                    while (offset < filled)
                    {
                        DecodeStatus status = FrameCodec.TryDecode(
                            buffer.AsSpan(offset, filled - offset),
                            out Packet packet,
                            out int consumed);
                        if (status == DecodeStatus.NeedMoreData)
                            break;
                        if (status != DecodeStatus.Success || consumed <= 0 || offset + consumed > filled)
                        {
                            if (IsStale(gen, writer))
                                return;
                            Retire(gen, writer);
                            throw new IOException("帧长度非法");
                        }

                        offset += consumed;
                        if (IsStale(gen, writer))
                            return;
                        try
                        {
                            onPacket(packet);
                        }
                        catch (Exception)
                        {
                            if (!IsStale(gen, writer))
                                Retire(gen, writer);
                            throw;
                        }
                    }

                    int remain = filled - offset;
                    if (offset > 0 && remain > 0)
                        Buffer.BlockCopy(buffer, offset, buffer, 0, remain);
                    filled = remain;
                }
            }
            finally
            {
                lock (gate)
                {
                    if (receiveId == id)
                        receiveRunning = false;
                }
            }
        }

        /// <summary>当前发送口。没有连接时是 null。</summary>
        internal ITransportSend CurrentChannel
        {
            get
            {
                lock (gate)
                    return channel;
            }
        }

        /// <summary>
        /// 关闭连接并结束尚未发送的帧。重复调用不做额外的事。
        /// </summary>
        public void Close()
        {
            CloseCore(null, false);
        }

        /// <summary>只关闭仍是这一只发送口的连接。已经换成新连接时什么也不做。</summary>
        /// <param name="expected">只关闭仍等于它的发送口。</param>
        internal void CloseIfSame(ITransportSend expected)
        {
            if (expected == null)
                return;
            CloseCore(expected, true);
        }

        /// <summary>增加代次、关掉发送口，并让队列里的帧失败。</summary>
        /// <param name="expected">要求匹配的发送口。不要求匹配时忽略。</param>
        /// <param name="requireSame">为 true 时，发送口已经换过就直接返回。</param>
        void CloseCore(ITransportSend expected, bool requireSame)
        {
            List<PendingSend> leftover;
            ITransportSend closing;
            CancellationTokenSource attemptCts;
            lock (gate)
            {
                if (requireSame && !ReferenceEquals(channel, expected))
                    return;
                generation++;
                receiveId++;
                connecting = false;
                receiveRunning = false;
                attemptCts = connectCts;
                connectCts = null;
                closing = channel;
                channel = null;
                pumpRunning = false;
                leftover = TakeQueue();
            }

            if (attemptCts != null)
            {
                try
                {
                    attemptCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            if (closing != null)
                closing.Close();
            FailAll(leftover, new ObjectDisposedException(nameof(TcpTransport)));
        }

        /// <summary>代次或发送口已经变了，就说明这次循环过期。</summary>
        /// <param name="gen">循环启动时记下的代次。</param>
        /// <param name="writer">循环启动时记下的发送口。</param>
        bool IsStale(int gen, ITransportSend writer)
        {
            lock (gate)
                return generation != gen || channel != writer;
        }

        /// <summary>这次循环仍拥有连接时，把它关掉。</summary>
        /// <param name="gen">循环启动时记下的代次。</param>
        /// <param name="writer">循环启动时记下的发送口。</param>
        void Retire(int gen, ITransportSend writer)
        {
            if (!IsStale(gen, writer))
                Break(gen, writer);
        }

        /// <summary>在已持有锁时挂上发送口，并推进代次。</summary>
        /// <param name="next">新的发送口。</param>
        void AttachLocked(ITransportSend next)
        {
            channel = next;
            generation++;
        }

        /// <summary>按队列顺序把每一帧发完。半帧失败或返回 0 时作废连接。</summary>
        /// <param name="gen">开始泵送时的代次。</param>
        /// <param name="writer">开始泵送时的发送口。</param>
        async Task PumpAsync(int gen, ITransportSend writer)
        {
            while (true)
            {
                PendingSend item;
                lock (gate)
                {
                    if (generation != gen || channel != writer)
                        return;
                    if (queue.Count == 0)
                    {
                        pumpRunning = false;
                        return;
                    }

                    item = queue.Dequeue();
                }

                if (item.Token.IsCancellationRequested)
                {
                    item.Done.TrySetCanceled(item.Token);
                    continue;
                }

                int offset = 0;
                bool attempted = false;
                try
                {
                    while (offset < item.Frame.Length)
                    {
                        if (!attempted)
                            item.Token.ThrowIfCancellationRequested();

                        lock (gate)
                        {
                            if (generation != gen || channel != writer)
                                throw new ObjectDisposedException(nameof(TcpTransport));
                        }

                        attempted = true;
                        int count = item.Frame.Length - offset;
                        int sent = await writer.SendAsync(item.Frame, offset, count, item.Token).ConfigureAwait(false);
                        if (sent <= 0 || sent > count)
                            throw new IOException("发送字节数无效");
                        attempted = false;
                        offset += sent;
                    }

                    item.Done.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    bool fatal = attempted || offset > 0;
                    if (fatal)
                        Break(gen, writer);
                    Complete(item, ex);
                    if (fatal)
                        return;
                }
            }
        }

        /// <summary>半帧之后连接已不可用。关掉它并让剩余帧失败。</summary>
        /// <param name="gen">失败时记下的代次。</param>
        /// <param name="writer">失败时记下的发送口。</param>
        void Break(int gen, ITransportSend writer)
        {
            List<PendingSend> leftover = null;
            ITransportSend closing = null;
            lock (gate)
            {
                if (generation != gen || channel != writer)
                    return;
                generation++;
                closing = channel;
                channel = null;
                pumpRunning = false;
                leftover = TakeQueue();
            }

            if (closing != null)
                closing.Close();
            FailAll(leftover, new IOException("连接在半帧之后已关闭"));
        }

        /// <summary>取出队列里还没开始发送的帧，并清空队列。</summary>
        List<PendingSend> TakeQueue()
        {
            List<PendingSend> leftover = new List<PendingSend>(queue.Count);
            while (queue.Count > 0)
                leftover.Add(queue.Dequeue());
            return leftover;
        }

        /// <summary>让这些尚未完成的发送以同一个异常结束。</summary>
        /// <param name="items">还在等待的发送项。</param>
        /// <param name="error">交给每一项的异常。</param>
        static void FailAll(List<PendingSend> items, Exception error)
        {
            for (int i = 0; i < items.Count; i++)
                items[i].Done.TrySetException(error);
        }

        /// <summary>结束一项发送。调用方已取消时，套接字关闭异常改成取消。</summary>
        /// <param name="item">这一帧的等待项。</param>
        /// <param name="error">发送过程中捕获的异常。</param>
        static void Complete(PendingSend item, Exception error)
        {
            if (item.Done.Task.IsCompleted)
                return;
            OperationCanceledException canceled = error as OperationCanceledException;
            if (canceled != null)
            {
                CancellationToken token = canceled.CancellationToken.CanBeCanceled ? canceled.CancellationToken : item.Token;
                item.Done.TrySetCanceled(token);
                return;
            }

            if (item.Token.IsCancellationRequested && (error is ObjectDisposedException || error is SocketException))
            {
                item.Done.TrySetCanceled(item.Token);
                return;
            }

            item.Done.TrySetException(error);
        }

        /// <summary>连接失败后清掉这次尝试的进行中标记。</summary>
        /// <param name="attempt">这次尝试的代次。代次已经变了就不动新连接。</param>
        /// <param name="attemptCts">这次尝试自己的取消源。</param>
        void ClearConnectAttempt(int attempt, CancellationTokenSource attemptCts)
        {
            lock (gate)
            {
                if (generation != attempt)
                    return;
                connecting = false;
                if (connectCts == attemptCts)
                    connectCts = null;
            }
        }

        /// <summary>关闭发送口。空引用或已经关闭时忽略。</summary>
        /// <param name="sender">要关闭的发送口，可以为空。</param>
        static void CloseQuiet(ITransportSend sender)
        {
            if (sender == null)
                return;
            try
            {
                sender.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>把主机解析成一个 IPv4 地址。</summary>
        /// <param name="host">主机名或 IP。</param>
        /// <param name="cancellationToken">取消解析。</param>
        static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                    throw new IOException("当前只连接 IPv4");
                return parsed;
            }

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                    return addresses[i];
            }

            throw new IOException("没有可用的 IPv4 地址");
        }

        /// <summary>关闭套接字。已经释放时忽略。</summary>
        /// <param name="socket">要关闭的套接字。</param>
        static void QuietClose(Socket socket)
        {
            try
            {
                socket.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        sealed class PendingSend
        {
            /// <summary>记住一帧和它的完成源。</summary>
            /// <param name="frame">这一帧自己的字节。</param>
            /// <param name="token">调用方的取消令牌。</param>
            /// <param name="done">这一帧发送结束时完成它。</param>
            public PendingSend(byte[] frame, CancellationToken token, TaskCompletionSource<bool> done)
            {
                Frame = frame;
                Token = token;
                Done = done;
            }

            public byte[] Frame { get; }
            public CancellationToken Token { get; }
            public TaskCompletionSource<bool> Done { get; }
        }

        sealed class SocketTransportSend : ITransportSend, ITransportReceive
        {
            readonly Socket socket;
            int closed;

            /// <summary>用已连接的套接字收发。</summary>
            /// <param name="socket">已经连上的套接字。</param>
            public SocketTransportSend(Socket socket)
            {
                this.socket = socket;
            }

            /// <summary>向套接字写出一段。取消时关掉套接字。</summary>
            /// <param name="buffer">待发送的字节。</param>
            /// <param name="offset">开始下标。</param>
            /// <param name="count">这一次请求发送的字节数。</param>
            /// <param name="cancellationToken">取消这次发送。</param>
            public async Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task<int> sending = socket.SendAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None);
                try
                {
                    if (sending.IsCompleted)
                        return await sending.ConfigureAwait(false);
                    using (cancellationToken.Register(Close))
                        return await sending.ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            /// <summary>从套接字读入一段。返回 0 表示对端关闭。</summary>
            /// <param name="buffer">接收缓冲。</param>
            /// <param name="offset">开始写入的下标。</param>
            /// <param name="count">这一次最多读取的字节数。</param>
            /// <param name="cancellationToken">取消这次读取。</param>
            public async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task<int> reading = socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None);
                try
                {
                    if (reading.IsCompleted)
                        return await reading.ConfigureAwait(false);
                    using (cancellationToken.Register(Close))
                        return await reading.ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            /// <summary>关闭套接字的收发。重复调用只执行一次。</summary>
            public void Close()
            {
                if (Interlocked.Exchange(ref closed, 1) != 0)
                    return;
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }

                try
                {
                    socket.Close();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
