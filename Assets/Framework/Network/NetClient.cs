using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;
using Google.Protobuf;

namespace Game.Framework.Network
{
    /// <summary>
    /// 把后台收到的包按顺序交给创建时所在的线程，并按代次、seq、响应消息完成请求。
    /// 超时、取消和断线都会结束等待，且只结束一次。
    /// </summary>
    public sealed class NetClient
    {
        /// <summary>没有传入更短超时时使用的默认时长。</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

        /// <summary>队列里最多保留的包数。再多就拒绝，避免一直堆积。</summary>
        public const int MaxQueuedPackets = 256;

        readonly object gate = new object();
        readonly Queue<Incoming> incoming = new Queue<Incoming>();
        readonly Dictionary<ulong, PendingRequest> pending = new Dictionary<ulong, PendingRequest>();
        readonly Action<Packet> onDispatch;
        readonly TcpTransport transport;
        readonly SynchronizationContext ownerContext;
        readonly int ownerThreadId;
        int generation = 1;
        uint nextSeq;
        bool linked = true;
        ITransportSend sessionChannel;

        /// <summary>
        /// 记住当前线程，作为以后分发 Model / UI 的线程。没有发送口时不能发请求。
        /// </summary>
        /// <param name="onDispatch">主线程取出包时调用。不要在接收回调里做这件事。</param>
        public NetClient(Action<Packet> onDispatch)
            : this(null, onDispatch)
        {
        }

        /// <summary>
        /// 记住当前线程，并用这条连接发送请求帧。
        /// </summary>
        /// <param name="transport">已经连接的发送口。</param>
        /// <param name="onDispatch">主线程取出仍属于当前代次的包时调用。</param>
        public NetClient(TcpTransport transport, Action<Packet> onDispatch)
        {
            if (onDispatch == null)
                throw new ArgumentNullException(nameof(onDispatch));
            this.transport = transport;
            this.onDispatch = onDispatch;
            ownerContext = SynchronizationContext.Current;
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>已经入队、还没有分发的包数。</summary>
        public int QueuedCount
        {
            get
            {
                lock (gate)
                    return incoming.Count;
            }
        }

        /// <summary>还没有被响应完成的请求数。</summary>
        internal int PendingCount
        {
            get
            {
                lock (gate)
                    return pending.Count;
            }
        }

        /// <summary>测试把一帧直接放进主线程队列。</summary>
        /// <param name="packet">已经切好的包。</param>
        public void EnqueueReceived(Packet packet)
        {
            int captured;
            lock (gate)
                captured = generation;
            Enqueue(packet, captured);
        }

        /// <summary>
        /// 记下此刻的连接代次。返回的回调以后入队都用这个代次，不用入队时的新代次。
        /// </summary>
        internal Action<Packet> CaptureReceive()
        {
            int captured;
            lock (gate)
                captured = generation;
            return packet => Enqueue(packet, captured);
        }

        /// <summary>
        /// 用启动这一刻的代次开始接收。循环退出、对端关闭或接收失败时结束这一代等待。
        /// </summary>
        /// <param name="cancellationToken">停止这一次接收。</param>
        public void BeginReceive(CancellationToken cancellationToken)
        {
            if (transport == null)
                throw new InvalidOperationException("没有可接收的连接");

            List<PendingRequest> replaced;
            int captured;
            ITransportSend owned;
            lock (gate)
            {
                replaced = AdoptTransportIfReplaced();
                linked = true;
                captured = generation;
                owned = sessionChannel;
            }

            if (replaced != null)
                FinishPending(replaced, new IOException("连接已断开"));

            Action<Packet> receive = packet => Enqueue(packet, captured);
            Task loop = transport.ReceiveLoopAsync(receive, cancellationToken);
            loop.ContinueWith(
                finished =>
                {
                    Exception error = finished.IsFaulted ? finished.Exception : null;
                    string reason = error == null ? "对端已关闭" : error.GetBaseException().Message;
                    EndSession(captured, owned, reason, false);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        /// <summary>
        /// 换上新连接。已排队的旧包在分发时丢掉，不能完成新请求。
        /// 不断开等待；断线清等待用 <see cref="Disconnect"/>。
        /// </summary>
        internal void AdvanceConnection()
        {
            EnsureOwner();
            lock (gate)
            {
                generation++;
                if (generation == 0)
                    generation = 1;
                nextSeq = 0;
            }
        }

        /// <summary>
        /// 结束当前连接上的全部等待，丢掉已入队的旧包，并关闭连接。重复调用不做额外的事。
        /// </summary>
        /// <param name="reason">交给等待方的原因。空字符串按断线处理。</param>
        public void Disconnect(string reason)
        {
            int captured;
            lock (gate)
                captured = generation;
            if (string.IsNullOrEmpty(reason))
                reason = "连接已断开";
            EndSession(captured, null, reason, true);
        }

        /// <summary>
        /// 分配 seq、登记等待并发送整帧。响应在 <see cref="DispatchPending"/> 里完成。
        /// 超时按真实经过时间计算。
        /// </summary>
        /// <param name="request">要发送的请求。</param>
        /// <param name="timeout">超时时间。默认使用 <see cref="DefaultTimeout"/>。</param>
        /// <param name="ct">发送和等待使用的取消标记。</param>
        public async UniTask<TResponse> SendRequestAsync<TResponse>(IMessage request, TimeSpan timeout, CancellationToken ct)
            where TResponse : class, IMessage
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            ushort requestId = MsgRegistry.GetId(request.GetType());
            if (!MsgRegistry.TryGetResponseId(requestId, out ushort responseId))
                throw new InvalidOperationException("该请求没有配对响应");
            if (MsgRegistry.GetId(typeof(TResponse)) != responseId)
                throw new InvalidOperationException("响应类型与请求不配对");
            IMessage message = await SendRequestMessageAsync(request, timeout, ct);
            return (TResponse)message;
        }

        /// <summary>玩法适配：按 MsgRegistry 配对发送，返回已解析的响应消息（非泛型）。</summary>
        public async UniTask<IMessage> SendRequestMessageAsync(IMessage request, TimeSpan timeout, CancellationToken ct)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (timeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            EnsureOwner();
            ct.ThrowIfCancellationRequested();
            if (transport == null)
                throw new InvalidOperationException("没有可发送的连接");

            ushort requestId = MsgRegistry.GetId(request.GetType());
            if (!MsgRegistry.TryGetResponseId(requestId, out ushort responseId))
                throw new InvalidOperationException("该请求没有配对响应");

            byte[] body = request.ToByteArray();
            CancellationTokenSource abort = new CancellationTokenSource();
            PendingRequest waiting;
            byte[] frame;
            List<PendingRequest> replaced;
            lock (gate)
            {
                replaced = AdoptTransportIfReplaced();
                linked = true;
                uint seq = NextSeq();
                waiting = new PendingRequest(generation, seq, responseId, ct, new UniTaskCompletionSource<IMessage>(), abort);
                frame = FrameCodec.Encode(requestId, seq, body);
                pending.Add(Key(waiting.Generation, waiting.Seq), waiting);
            }

            if (replaced != null)
                FinishPending(replaced, new IOException("连接已断开"));

            try
            {
                Arm(waiting, timeout, ct);
                try
                {
                    using (CancellationTokenSource linkedSend = CancellationTokenSource.CreateLinkedTokenSource(ct, abort.Token))
                        await transport.SendAsync(frame, linkedSend.Token);
                }
                catch (Exception)
                {
                    if (waiting.Finished)
                        return await waiting.Done.Task;
                    if (Detach(waiting))
                        Release(waiting);
                    throw;
                }

                return await waiting.Done.Task;
            }
            finally
            {
                DisposeAbort(waiting);
            }
        }

        /// <summary>在创建线程上最多取出这么多包并回调。</summary>
        /// <param name="maxMessages">这一次最多分发的包数。</param>
        public void DispatchPending(int maxMessages)
        {
            if (maxMessages <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMessages));
            EnsureOwner();

            for (int i = 0; i < maxMessages; i++)
            {
                Incoming item;
                lock (gate)
                {
                    if (incoming.Count == 0)
                        break;
                    item = incoming.Dequeue();
                }

                if (item.Generation != CurrentGeneration())
                    continue;

                Packet packet = item.Packet;
                if (packet.Seq != 0)
                    TryComplete(item.Generation, packet);
                onDispatch(packet);
            }
        }

        /// <summary>结束这一代等待。只关闭仍属于这一代的连接。</summary>
        /// <param name="capturedGeneration">要结束的连接代次。</param>
        /// <param name="ownedChannel">这一代启动时的发送口。</param>
        /// <param name="reason">结束原因，写入等待项的异常。</param>
        /// <param name="closeLive">为 true 时，发送口仍是这一代才关闭它。</param>
        void EndSession(int capturedGeneration, ITransportSend ownedChannel, string reason, bool closeLive)
        {
            List<PendingRequest> doomed = null;
            ITransportSend closing = null;
            bool closeCurrent = false;
            lock (gate)
            {
                if (generation != capturedGeneration)
                    return;

                ITransportSend live = transport != null ? transport.CurrentChannel : null;
                bool replaced = !closeLive
                    && ownedChannel != null
                    && live != null
                    && !ReferenceEquals(live, ownedChannel);
                if (replaced)
                {
                    if (!linked)
                        return;
                    doomed = StealPending();
                    BumpGeneration();
                    incoming.Clear();
                    sessionChannel = live;
                    linked = true;
                }
                else
                {
                    if (!linked)
                        return;
                    linked = false;
                    BumpGeneration();
                    incoming.Clear();
                    doomed = StealPending();
                    sessionChannel = null;
                    if (closeLive)
                        closeCurrent = true;
                    else
                        closing = ownedChannel;
                }
            }

            if (transport != null)
            {
                if (closeCurrent)
                    transport.Close();
                else if (closing != null)
                    transport.CloseIfSame(closing);
            }

            if (string.IsNullOrEmpty(reason))
                reason = "连接已断开";
            FinishPending(doomed, new IOException(reason));
        }

        /// <summary>
        /// 发送口已经换成另一只时，结束仍挂在旧口上的等待，并把代次让给新连接。调用方持有 gate。
        /// </summary>
        List<PendingRequest> AdoptTransportIfReplaced()
        {
            if (transport == null)
                return null;
            ITransportSend live = transport.CurrentChannel;
            if (sessionChannel == null)
            {
                sessionChannel = live;
                return null;
            }

            if (live == null || ReferenceEquals(live, sessionChannel))
                return null;

            List<PendingRequest> doomed = StealPending();
            BumpGeneration();
            incoming.Clear();
            sessionChannel = live;
            linked = true;
            return doomed;
        }

        /// <summary>推进连接代次，使旧包不能完成新请求。</summary>
        void BumpGeneration()
        {
            generation++;
            if (generation == 0)
                generation = 1;
            nextSeq = 0;
        }

        /// <summary>取出全部等待并清空表。这些请求随即以断线结束。</summary>
        List<PendingRequest> StealPending()
        {
            List<PendingRequest> doomed = new List<PendingRequest>(pending.Count);
            foreach (KeyValuePair<ulong, PendingRequest> pair in pending)
            {
                pair.Value.Finished = true;
                doomed.Add(pair.Value);
            }

            pending.Clear();
            return doomed;
        }

        /// <summary>让这些等待以同一个异常结束，并卸掉计时器。</summary>
        /// <param name="doomed">要结束的等待。</param>
        /// <param name="error">交给每一项的异常。</param>
        void FinishPending(List<PendingRequest> doomed, Exception error)
        {
            if (doomed == null)
                return;
            for (int i = 0; i < doomed.Count; i++)
            {
                PendingRequest waiting = doomed[i];
                Release(waiting);
                Deliver(() => waiting.Done.TrySetException(error));
                CancelSend(waiting);
            }
        }

        /// <summary>给这项等待装上超时和取消。零超时也会在发送还堵着时结束调用方。</summary>
        /// <param name="waiting">正在等待响应的请求。</param>
        /// <param name="timeout">从现在起的超时。零表示立即超时。</param>
        /// <param name="ct">调用方的取消令牌。</param>
        void Arm(PendingRequest waiting, TimeSpan timeout, CancellationToken ct)
        {
            int due = timeout.TotalMilliseconds >= int.MaxValue
                ? int.MaxValue
                : (int)timeout.TotalMilliseconds;
            if (due < 0)
                due = 0;

            Timer timer = new Timer(_ => OnTimeout(waiting), null, Timeout.Infinite, Timeout.Infinite);
            bool keepTimer = false;
            lock (gate)
            {
                if (!waiting.Finished)
                {
                    waiting.Timer = timer;
                    keepTimer = true;
                }
            }

            if (!keepTimer)
            {
                timer.Dispose();
                return;
            }

            CancellationTokenRegistration registration = default;
            if (ct.CanBeCanceled)
                registration = ct.Register(() => OnCancel(waiting));

            bool orphanRegistration = false;
            lock (gate)
            {
                if (waiting.Finished)
                    orphanRegistration = true;
                else
                    waiting.Registration = registration;
            }

            if (orphanRegistration)
                registration.Dispose();

            bool startTimer = false;
            lock (gate)
                startTimer = !waiting.Finished && ReferenceEquals(waiting.Timer, timer);
            if (!startTimer)
                return;
            try
            {
                timer.Change(due, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>超时回调。请求还没完成就以超时结束并取消发送。</summary>
        /// <param name="waiting">超时的那一项。</param>
        void OnTimeout(PendingRequest waiting)
        {
            if (!Detach(waiting))
                return;
            Release(waiting);
            Deliver(() => waiting.Done.TrySetException(new TimeoutException("请求超时")));
            CancelSend(waiting);
        }

        /// <summary>取消回调。请求还没完成就以取消结束并取消发送。</summary>
        /// <param name="waiting">被取消的那一项。</param>
        void OnCancel(PendingRequest waiting)
        {
            if (!Detach(waiting))
                return;
            Release(waiting);
            CancellationToken token = waiting.CancelToken;
            Deliver(() => waiting.Done.TrySetCanceled(token));
            CancelSend(waiting);
        }

        /// <summary>代次、序号和响应消息 ID 都匹配时完成等待。</summary>
        /// <param name="packetGeneration">收到这个包时的连接代次。</param>
        /// <param name="packet">收到的包。</param>
        void TryComplete(int packetGeneration, Packet packet)
        {
            ulong key = Key(packetGeneration, packet.Seq);
            PendingRequest waiting;
            lock (gate)
            {
                if (!pending.TryGetValue(key, out waiting))
                    return;
                if (waiting.Finished || waiting.Generation != packetGeneration || packet.MsgId != waiting.ResponseId)
                    return;
            }

            IMessage message = MsgRegistry.Parse(packet.MsgId, packet.Body);
            if (!Detach(waiting))
                return;
            Release(waiting);
            waiting.Done.TrySetResult(message);
        }

        /// <summary>从等待表拿掉这一项。已经拿掉时返回 false。</summary>
        /// <param name="waiting">要拿掉的等待。</param>
        bool Detach(PendingRequest waiting)
        {
            lock (gate)
            {
                if (waiting.Finished)
                    return false;
                ulong key = Key(waiting.Generation, waiting.Seq);
                PendingRequest current;
                if (!pending.TryGetValue(key, out current) || !ReferenceEquals(current, waiting))
                    return false;
                pending.Remove(key);
                waiting.Finished = true;
                return true;
            }
        }

        /// <summary>卸掉这一项的计时器和取消注册。</summary>
        /// <param name="waiting">已经结束的等待。</param>
        void Release(PendingRequest waiting)
        {
            Timer timer;
            CancellationTokenRegistration registration;
            lock (gate)
            {
                timer = waiting.Timer;
                waiting.Timer = null;
                registration = waiting.Registration;
                waiting.Registration = default;
            }

            if (timer != null)
                timer.Dispose();
            registration.Dispose();
        }

        /// <summary>取消这一项还在进行的发送。</summary>
        /// <param name="waiting">拥有发送取消源的等待。</param>
        static void CancelSend(PendingRequest waiting)
        {
            CancellationTokenSource abort = waiting.Abort;
            if (abort == null)
                return;
            try
            {
                abort.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>释放发送取消源。</summary>
        /// <param name="waiting">拥有发送取消源的等待。</param>
        static void DisposeAbort(PendingRequest waiting)
        {
            CancellationTokenSource abort = Interlocked.Exchange(ref waiting.Abort, null);
            if (abort == null)
                return;
            try
            {
                abort.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>把动作送到创建 NetClient 的线程。已经在该线程就直接执行。</summary>
        /// <param name="action">要在创建线程执行的动作。</param>
        void Deliver(Action action)
        {
            if (ownerContext != null && Thread.CurrentThread.ManagedThreadId != ownerThreadId)
                ownerContext.Post(_ => action(), null);
            else
                action();
        }

        /// <summary>把包和收到时的代次放进主线程队列。</summary>
        /// <param name="packet">收到的包。</param>
        /// <param name="capturedGeneration">收到时的连接代次。</param>
        void Enqueue(Packet packet, int capturedGeneration)
        {
            lock (gate)
            {
                if (incoming.Count >= MaxQueuedPackets)
                    throw new InvalidOperationException("接收队列已满");
                incoming.Enqueue(new Incoming(packet, capturedGeneration));
            }
        }

        /// <summary>返回当前连接代次（玩法 stamp / 门控用）。</summary>
        public int ConnectionGeneration
        {
            get
            {
                lock (gate)
                    return generation;
            }
        }

        /// <summary>返回当前连接代次。</summary>
        int CurrentGeneration() => ConnectionGeneration;

        /// <summary>分配下一个请求序号。0 留给系统，用完后从 1 继续。</summary>
        uint NextSeq()
        {
            nextSeq++;
            if (nextSeq == 0)
                nextSeq = 1;
            return nextSeq;
        }

        /// <summary>用代次和序号组成等待表的键。</summary>
        /// <param name="capturedGeneration">连接代次。</param>
        /// <param name="seq">请求序号。</param>
        static ulong Key(int capturedGeneration, uint seq)
        {
            return ((ulong)capturedGeneration << 32) | seq;
        }

        /// <summary>不是创建线程时抛异常。</summary>
        void EnsureOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThreadId)
                throw new InvalidOperationException("只能在创建 NetClient 的线程分发");
        }

        readonly struct Incoming
        {
            /// <summary>记下包和收到它时的代次。</summary>
            /// <param name="packet">收到的包。</param>
            /// <param name="capturedGeneration">收到时的连接代次。</param>
            public Incoming(Packet packet, int capturedGeneration)
            {
                Packet = packet;
                Generation = capturedGeneration;
            }

            public Packet Packet { get; }
            public int Generation { get; }
        }

        sealed class PendingRequest
        {
            /// <summary>记下一次尚未完成的请求。</summary>
            /// <param name="generation">发出时的连接代次。</param>
            /// <param name="seq">请求序号。</param>
            /// <param name="responseId">期待的响应消息 ID。</param>
            /// <param name="cancelToken">调用方的取消令牌。</param>
            /// <param name="done">响应、超时、取消或断线时完成它。</param>
            /// <param name="abort">用来取消仍在进行的发送。</param>
            public PendingRequest(
                int generation,
                uint seq,
                ushort responseId,
                CancellationToken cancelToken,
                UniTaskCompletionSource<IMessage> done,
                CancellationTokenSource abort)
            {
                Generation = generation;
                Seq = seq;
                ResponseId = responseId;
                CancelToken = cancelToken;
                Done = done;
                Abort = abort;
            }

            public int Generation { get; }
            public uint Seq { get; }
            public ushort ResponseId { get; }
            public CancellationToken CancelToken { get; }
            public UniTaskCompletionSource<IMessage> Done { get; }
            public CancellationTokenSource Abort;
            public Timer Timer;
            public CancellationTokenRegistration Registration;
            public bool Finished;
        }
    }
}
