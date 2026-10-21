using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using Google.Protobuf;

namespace CloudIsle.Server.Network
{
    /// <summary>
    /// 按 MsgRegistry 把一帧交给已注册的处理函数。
    /// 认证前只放行心跳、注册、登录、重连。登出必须已认证，并且没有响应。
    /// 普通请求每个主体每秒 20 次。未认证主体按会话分开计数。
    /// </summary>
    public sealed class MessageRouter
    {
        /// <summary>未登录。</summary>
        public const int CodeNotLogin = 102;

        /// <summary>请求过快。</summary>
        public const int CodeTooFast = 104;

        /// <summary>正式注册表里没有这条消息，不能当成已上线的业务。</summary>
        public const int CodeUnknown = 101;

        const int MaxRequestsPerWindow = 20;
        const int MaxConsecutiveParseFailures = 5;

        readonly Func<RequestContext, Packet, CancellationToken, Task> _sendAsync;
        readonly Func<RequestContext, string, CancellationToken, Task> _closeAsync;
        readonly IRouterClock _clock;
        readonly Action<string>? _trace;
        readonly Dictionary<ushort, RouteHandler> _handlers = new Dictionary<ushort, RouteHandler>();
        readonly Dictionary<long, long> _sessionPlayer = new Dictionary<long, long>();
        readonly Dictionary<long, RateBucket> _sessionRates = new Dictionary<long, RateBucket>();
        readonly Dictionary<long, RateBucket> _playerRates = new Dictionary<long, RateBucket>();
        readonly Dictionary<long, int> _parseFailures = new Dictionary<long, int>();
        readonly object _gate = new object();
        long _requestCount;

        /// <summary>
        /// 用系统时钟创建路由。发送和关闭由调用方接到会话上。
        /// </summary>
        /// <param name="sendAsync">写出一帧响应。无响应的拒绝不会调用它。</param>
        /// <param name="closeAsync">连续反序列化失败达到 5 次时关闭这条会话。</param>
        public MessageRouter(
            Func<RequestContext, Packet, CancellationToken, Task> sendAsync,
            Func<RequestContext, string, CancellationToken, Task> closeAsync)
            : this(sendAsync, closeAsync, new SystemRouterClock(), null)
        {
        }

        /// <summary>创建可注入时钟和追踪的路由。</summary>
        /// <param name="sendAsync">写出一帧响应。</param>
        /// <param name="closeAsync">需要关闭会话时调用。</param>
        /// <param name="clock">限流窗口使用的时钟。</param>
        /// <param name="trace">写追踪行。为空则不追踪。</param>
        internal MessageRouter(
            Func<RequestContext, Packet, CancellationToken, Task> sendAsync,
            Func<RequestContext, string, CancellationToken, Task> closeAsync,
            IRouterClock clock,
            Action<string>? trace)
        {
            if (sendAsync == null)
                throw new ArgumentNullException(nameof(sendAsync));
            if (closeAsync == null)
                throw new ArgumentNullException(nameof(closeAsync));
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _sendAsync = sendAsync;
            _closeAsync = closeAsync;
            _clock = clock;
            _trace = trace;
        }

        internal int SessionRateCount
        {
            get
            {
                lock (_gate)
                    return _sessionRates.Count;
            }
        }

        internal int PlayerRateCount
        {
            get
            {
                lock (_gate)
                    return _playerRates.Count;
            }
        }

        internal int ParseStateCount
        {
            get
            {
                lock (_gate)
                    return _parseFailures.Count;
            }
        }

        /// <summary>已经进入分发的次数，包含被拒绝和稍后失败的请求。供请求率使用，不是成功次数。</summary>
        public long RequestCount => Interlocked.Read(ref _requestCount);

        internal int SessionBindingCount
        {
            get
            {
                lock (_gate)
                    return _sessionPlayer.Count;
            }
        }

        /// <summary>
        /// 注册一个请求处理函数。消息 ID 必须等于 MsgRegistry 里该类型的 ID。
        /// 同一个 ID 再注册一次会失败，已有处理函数保持不变。
        /// </summary>
        /// <typeparam name="TRequest">请求的 protobuf 类型。</typeparam>
        /// <param name="msgId">线路上的消息 ID。</param>
        /// <param name="handler">通过认证和限流后调用。</param>
        public void Register<TRequest>(
            ushort msgId,
            Func<RequestContext, TRequest, CancellationToken, Task> handler)
            where TRequest : class, IMessage
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            ushort actualId = MsgRegistry.GetId(typeof(TRequest));
            if (actualId != msgId)
            {
                throw new InvalidOperationException(
                    "注册类型与消息 ID 不一致: " + typeof(TRequest).Name + " 的 ID 是 " + actualId + "，给出的是 " + msgId);
            }

            var route = new RouteHandler(
                typeof(TRequest),
                (context, message, token) => handler(context, (TRequest)message, token));

            lock (_gate)
            {
                if (_handlers.ContainsKey(msgId))
                    throw new InvalidOperationException("重复注册消息 ID: " + msgId);

                _handlers.Add(msgId, route);
            }
        }

        /// <summary>
        /// 按帧分发。上下文里的消息 ID 和序号必须与帧一致。
        /// 拒绝已有响应的请求时，用真实响应的 code 字段；没有响应时不发帧。
        /// </summary>
        /// <param name="context">会话、玩家、消息 ID 和序号。</param>
        /// <param name="packet">已经完成分帧的包。正文由调用方持有。</param>
        /// <param name="ct">取消分发、回包和关闭。</param>
        public async Task DispatchAsync(RequestContext context, Packet packet, CancellationToken ct)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (packet.Body == null)
                throw new ArgumentNullException(nameof(packet));
            if (context.MsgId != packet.MsgId || context.Seq != packet.Seq)
                throw new InvalidOperationException("上下文与帧的消息 ID 或序号不一致");

            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            RememberSession(context);

            if (context.PlayerId <= 0 && !IsPreAuthSystemRequest(packet.MsgId))
            {
                await RejectAsync(context, packet, CodeNotLogin, ct).ConfigureAwait(false);
                return;
            }

            IMessage message;
            try
            {
                message = MsgRegistry.Parse(packet.MsgId, packet.Body);
            }
            catch (InvalidProtocolBufferException)
            {
                await NoteParseFailureAsync(context, ct).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                await RejectAsync(context, packet, CodeUnknown, ct).ConfigureAwait(false);
                return;
            }

            ClearParseFailures(context.SessionId);

            RouteHandler? handler;
            lock (_gate)
                _handlers.TryGetValue(packet.MsgId, out handler);

            if (handler == null)
            {
                await RejectAsync(context, packet, CodeUnknown, ct).ConfigureAwait(false);
                return;
            }

            if (!handler.RequestType.IsInstanceOfType(message))
                throw new InvalidOperationException("解析类型与注册类型不一致");

            if (!TryConsumeRequest(context))
            {
                await RejectAsync(context, packet, CodeTooFast, ct).ConfigureAwait(false);
                return;
            }

            WriteTrace(context, 0, "handler");
            await handler.Invoke(context, message, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 会话关闭后丢掉这条连接的计数和非法消息状态。
        /// 同一个玩家已经没有其他会话时，一并丢掉该玩家的限流计数。
        /// </summary>
        /// <param name="sessionId">已经关闭的会话。</param>
        public void ReleaseSession(long sessionId)
        {
            lock (_gate)
            {
                _sessionRates.Remove(sessionId);
                _parseFailures.Remove(sessionId);
                if (!_sessionPlayer.TryGetValue(sessionId, out long playerId))
                    return;

                _sessionPlayer.Remove(sessionId);
                if (playerId <= 0 || SessionStillBound(playerId))
                    return;

                _playerRates.Remove(playerId);
            }
        }

        /// <summary>未认证时只允许心跳、注册、登录和重连。</summary>
        /// <param name="msgId">本帧消息 ID。</param>
        static bool IsPreAuthSystemRequest(ushort msgId)
        {
            return msgId == MsgRegistry.GetId(typeof(HeartbeatReq))
                || msgId == MsgRegistry.GetId(typeof(RegisterReq))
                || msgId == MsgRegistry.GetId(typeof(LoginReq))
                || msgId == MsgRegistry.GetId(typeof(ReconnectReq));
        }

        /// <summary>记下这条会话当前绑定的玩家。</summary>
        /// <param name="context">这一次分发的上下文。</param>
        void RememberSession(RequestContext context)
        {
            lock (_gate)
                _sessionPlayer[context.SessionId] = context.PlayerId;
        }

        /// <summary>同一主体在当前 1 秒窗口内未超过 20 次时返回 true。</summary>
        /// <param name="context">用来选择计数键的上下文。</param>
        bool TryConsumeRequest(RequestContext context)
        {
            long window = _clock.UnixMilliseconds / 1000L;
            lock (_gate)
            {
                Dictionary<long, RateBucket> map = context.PlayerId > 0 ? _playerRates : _sessionRates;
                long key = context.PlayerId > 0 ? context.PlayerId : context.SessionId;
                if (!map.TryGetValue(key, out RateBucket? bucket) || bucket.Window != window)
                {
                    bucket = new RateBucket { Window = window, Count = 0 };
                    map[key] = bucket;
                }

                bucket.Count++;
                return bucket.Count <= MaxRequestsPerWindow;
            }
        }

        /// <summary>合法消息之后把连续反序列化失败次数清零。</summary>
        /// <param name="sessionId">连接会话编号。</param>
        void ClearParseFailures(long sessionId)
        {
            lock (_gate)
                _parseFailures.Remove(sessionId);
        }

        /// <summary>累计反序列化失败。连续 5 次就关闭会话。</summary>
        /// <param name="context">失败的那一次分发。</param>
        /// <param name="ct">关闭会话时使用的取消令牌。</param>
        async Task NoteParseFailureAsync(RequestContext context, CancellationToken ct)
        {
            int count;
            lock (_gate)
            {
                _parseFailures.TryGetValue(context.SessionId, out count);
                count++;
                _parseFailures[context.SessionId] = count;
            }

            WriteTrace(context, 0, "deserialize-failed");
            if (count < MaxConsecutiveParseFailures)
                return;

            try
            {
                await _closeAsync(context, "连续反序列化失败", ct).ConfigureAwait(false);
            }
            finally
            {
                ReleaseSession(context.SessionId);
            }
        }

        /// <summary>有配对响应时只回 code。没有响应时只记录结果。</summary>
        /// <param name="context">被拒绝的请求上下文。</param>
        /// <param name="packet">原始请求帧。</param>
        /// <param name="code">业务码，例如 102 或 104。</param>
        /// <param name="ct">发送拒绝响应时的取消令牌。</param>
        async Task RejectAsync(RequestContext context, Packet packet, int code, CancellationToken ct)
        {
            WriteTrace(context, code, "reject");
            if (!MsgRegistry.TryGetResponseId(packet.MsgId, out ushort responseId))
                return;

            IMessage? response = CreateCodedResponse(responseId, code);
            if (response == null)
                return;

            var reply = new Packet(responseId, packet.Seq, response.ToByteArray());
            await _sendAsync(context, reply, ct).ConfigureAwait(false);
        }

        /// <summary>按响应 ID 造一条只填 code 的消息。没有这种响应时返回空。</summary>
        /// <param name="responseId">配对的响应消息 ID。</param>
        /// <param name="code">要写入的业务码。</param>
        static IMessage? CreateCodedResponse(ushort responseId, int code)
        {
            IMessage response = MsgRegistry.Parse(responseId, Array.Empty<byte>());
            switch (response)
            {
                case HeartbeatResp heartbeat:
                    heartbeat.Code = code;
                    return heartbeat;
                case RegisterResp register:
                    register.Code = code;
                    return register;
                case LoginResp login:
                    login.Code = code;
                    login.Token = string.Empty;
                    return login;
                case ReconnectResp reconnect:
                    reconnect.Code = code;
                    return reconnect;
                default:
                    return null;
            }
        }

        /// <summary>这个玩家是否还有别的会话绑定着。</summary>
        /// <param name="playerId">玩家编号。</param>
        bool SessionStillBound(long playerId)
        {
            foreach (KeyValuePair<long, long> pair in _sessionPlayer)
            {
                if (pair.Value == playerId)
                    return true;
            }

            return false;
        }

        /// <summary>写一行不含正文、密码和 Token 的追踪。</summary>
        /// <param name="context">这次分发的上下文。</param>
        /// <param name="code">结果码。</param>
        /// <param name="action">短动作名。</param>
        void WriteTrace(RequestContext context, int code, string action)
        {
            if (_trace == null)
                return;

            _trace("session=" + context.SessionId + " msg=" + context.MsgId + " seq=" + context.Seq + " code=" + code + " action=" + action);
        }

        sealed class RouteHandler
        {
            /// <summary>记下一种请求的处理函数。</summary>
            /// <param name="requestType">请求的 protobuf 类型。</param>
            /// <param name="invoke">通过认证和限流后调用。</param>
            public RouteHandler(Type requestType, Func<RequestContext, IMessage, CancellationToken, Task> invoke)
            {
                RequestType = requestType;
                Invoke = invoke;
            }

            public Type RequestType { get; }

            public Func<RequestContext, IMessage, CancellationToken, Task> Invoke { get; }
        }

        sealed class RateBucket
        {
            public long Window;
            public int Count;
        }
    }

    internal interface IRouterClock
    {
        long UnixMilliseconds { get; }
    }

    sealed class SystemRouterClock : IRouterClock
    {
        public long UnixMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
