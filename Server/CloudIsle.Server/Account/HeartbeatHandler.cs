using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Network;
using Google.Protobuf;

namespace CloudIsle.Server.Account
{
    /// <summary>
    /// 处理正式心跳。存活时间只记录服务端收到请求的 UTC 时刻。
    /// 响应使用请求的 seq，不根据客户端填写的时间做判定。
    /// </summary>
    public sealed class HeartbeatHandler
    {
        readonly Func<RequestContext, Packet, CancellationToken, Task> _sendAsync;
        readonly Dictionary<long, long> _aliveUnixMs = new Dictionary<long, long>();
        readonly object _gate = new object();

        /// <summary>
        /// 记下发送口。发送口与路由使用的是同一种回包委托。
        /// </summary>
        /// <param name="sendAsync">把响应帧交给当前会话。</param>
        public HeartbeatHandler(Func<RequestContext, Packet, CancellationToken, Task> sendAsync)
        {
            if (sendAsync == null)
                throw new ArgumentNullException(nameof(sendAsync));
            _sendAsync = sendAsync;
        }

        /// <summary>
        /// 注册到已有路由。消息 ID 使用正式注册表里的心跳请求。
        /// </summary>
        /// <param name="router">启动时持有的路由。</param>
        public void Register(MessageRouter router)
        {
            if (router == null)
                throw new ArgumentNullException(nameof(router));
            router.Register<HeartbeatReq>(MsgRegistry.GetId(typeof(HeartbeatReq)), HandleAsync);
        }

        /// <summary>
        /// 返回这条连接最后一次心跳的服务端 UTC Unix 毫秒。还没有心跳时为 0。
        /// </summary>
        /// <param name="sessionId">连接会话编号。</param>
        public long LastAliveUnixMs(long sessionId)
        {
            lock (_gate)
            {
                long value;
                return _aliveUnixMs.TryGetValue(sessionId, out value) ? value : 0L;
            }
        }

        /// <summary>会话关闭后释放其存活记录。重复释放安全。</summary>
        /// <param name="sessionId">连接会话编号。</param>
        public void ReleaseSession(long sessionId)
        {
            lock (_gate)
                _aliveUnixMs.Remove(sessionId);
        }

        /// <summary>
        /// 更新连接存活时间，并回送 code 为 0 的心跳响应。
        /// </summary>
        /// <param name="context">当前帧的会话和 seq。</param>
        /// <param name="request">正式心跳请求。其中的客户端时间不参与判定。</param>
        /// <param name="ct">取消回包。</param>
        public async Task HandleAsync(RequestContext context, HeartbeatReq request, CancellationToken ct)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ct.ThrowIfCancellationRequested();
            long serverUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_gate)
                _aliveUnixMs[context.SessionId] = serverUnixMs;

            var response = new HeartbeatResp
            {
                Code = 0,
                ServerTime = serverUnixMs
            };
            ushort responseId = MsgRegistry.GetId(typeof(HeartbeatResp));
            await _sendAsync(context, new Packet(responseId, context.Seq, response.ToByteArray()), ct)
                .ConfigureAwait(false);
        }
    }
}
