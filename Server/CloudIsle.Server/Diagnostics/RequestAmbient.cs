using System.Diagnostics;
using CloudIsle.Diagnostics;

namespace CloudIsle.Server.Diagnostics;

/// <summary>
/// 一次请求在当前异步流上的关联字段。并发请求各有各的 AsyncLocal，结束后恢复外层。
/// </summary>
static class RequestAmbient
{
    static readonly AsyncLocal<AmbientRequest?> CurrentSlot = new();

    /// <summary>当前异步流上的请求。没有请求装饰器压栈时为空。</summary>
    public static AmbientRequest? Current => CurrentSlot.Value;

    /// <summary>当前异步流是否还停在某次请求装饰器里。测试用来确认没有泄漏。</summary>
    internal static bool HasCurrent => CurrentSlot.Value != null;

    /// <summary>压入这一次请求。Dispose 时恢复之前的值。</summary>
    /// <param name="request">这一次分发的关联字段。为空时抛出异常。</param>
    /// <returns>恢复用的句柄。</returns>
    public static IDisposable Push(AmbientRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var previous = CurrentSlot.Value;
        CurrentSlot.Value = request;
        return new Pop(previous);
    }

    sealed class Pop : IDisposable
    {
        readonly AmbientRequest? _previous;
        int _disposed;

        /// <summary>记住外层请求。</summary>
        /// <param name="previous">压栈前的请求。没有外层时为空。</param>
        public Pop(AmbientRequest? previous) => _previous = previous;

        /// <summary>恢复外层请求。重复调用只恢复一次。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            CurrentSlot.Value = _previous;
        }
    }
}

/// <summary>一次分发的关联数字。耗时按开始时刻现算，不在并发请求之间共用。</summary>
sealed class AmbientRequest
{
    readonly long _startedAt;

    /// <summary>记下这次请求的五个关联值。</summary>
    /// <param name="playerId">玩家编号。未认证为 0。</param>
    /// <param name="msgId">消息 ID。</param>
    /// <param name="seq">请求序号。</param>
    /// <param name="opId">幂等操作编号。没有操作时为 0。</param>
    /// <param name="startedAt">Stopwatch 时间戳，用来计算 ElapsedMs。</param>
    public AmbientRequest(long playerId, ushort msgId, uint seq, long opId, long startedAt)
    {
        PlayerId = playerId;
        MsgId = msgId;
        Seq = seq;
        OpId = opId;
        _startedAt = startedAt;
    }

    /// <summary>玩家编号。</summary>
    public long PlayerId { get; }

    /// <summary>消息 ID。</summary>
    public ushort MsgId { get; }

    /// <summary>请求序号。</summary>
    public uint Seq { get; }

    /// <summary>幂等操作编号。</summary>
    public long OpId { get; }

    /// <summary>从开始到现在的毫秒数。</summary>
    public double ElapsedMs => Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

    /// <summary>收成共享层的日志上下文。只含这五个数字。</summary>
    public LogContext ToLogContext() => new LogContext(PlayerId, MsgId, Seq, OpId, ElapsedMs);
}
