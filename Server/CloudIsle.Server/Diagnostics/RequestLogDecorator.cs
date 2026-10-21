using System.Diagnostics;
using CloudIsle.Diagnostics;
using CloudIsle.Net;
using CloudIsle.Server.Network;
using Serilog;

namespace CloudIsle.Server.Diagnostics;

/// <summary>
/// 套在真实 MessageRouter 分发外面的请求日志。只记录关联字段和结果，不读请求正文。
/// T60 再把它装进正式 Host。这里不启动监听。
/// </summary>
public sealed class RequestLogDecorator
{
    readonly MessageRouter _router;
    readonly ILogger _logger;

    /// <summary>使用已经 Configure 的日志和现有路由。</summary>
    /// <param name="router">真正执行分发的路由。本对象不注册玩法消息。</param>
    /// <param name="logging">提供 Serilog 的配置。尚未 Configure 时构造会失败。</param>
    public RequestLogDecorator(MessageRouter router, LoggingSetup logging)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        if (logging == null)
            throw new ArgumentNullException(nameof(logging));
        _logger = logging.Logger;
    }

    /// <summary>
    /// 分发一帧并写下这次的 LogContext。成功、取消和异常都会带上耗时。
    /// 异常继续抛给调用方。日志里的异常由脱敏 sink 处理，这里不调用 Exception.ToString。
    /// </summary>
    /// <param name="context">会话、玩家、消息 ID 和序号。不从正文里取。</param>
    /// <param name="packet">交给路由的帧。本方法不读取、不记录 Body。</param>
    /// <param name="opId">这次写操作的编号。没有操作时传 0。不从正文解析。</param>
    /// <param name="ct">取消分发。</param>
    public async Task DispatchAsync(RequestContext context, Packet packet, long opId, CancellationToken ct)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (packet.Body == null)
            throw new ArgumentNullException(nameof(packet));

        var ambient = new AmbientRequest(
            context.PlayerId,
            context.MsgId,
            context.Seq,
            opId,
            Stopwatch.GetTimestamp());
        using (RequestAmbient.Push(ambient))
        {
            try
            {
                await _router.DispatchAsync(context, packet, ct).ConfigureAwait(false);
                Write(ambient.ToLogContext(), "ok", null);
            }
            catch (OperationCanceledException)
            {
                Write(ambient.ToLogContext(), "canceled", null);
                throw;
            }
            catch (Exception ex)
            {
                Write(ambient.ToLogContext(), "fail", ex);
                throw;
            }
        }
    }

    /// <summary>写一条不含正文的请求结果。</summary>
    /// <param name="context">五个关联数字。</param>
    /// <param name="outcome">ok、fail 或 canceled。</param>
    /// <param name="error">失败时的异常。取消和成功为空。不在这里展开堆栈。</param>
    void Write(LogContext context, string outcome, Exception? error)
    {
        if (error == null)
        {
            _logger.Information(
                "request {Outcome} {PlayerId} {MsgId} {Seq} {OpId} {ElapsedMs}",
                outcome,
                context.PlayerId,
                context.MsgId,
                context.Seq,
                context.OpId,
                context.ElapsedMs);
            return;
        }

        _logger.Error(
            error,
            "request {Outcome} {PlayerId} {MsgId} {Seq} {OpId} {ElapsedMs}",
            outcome,
            context.PlayerId,
            context.MsgId,
            context.Seq,
            context.OpId,
            context.ElapsedMs);
    }
}
