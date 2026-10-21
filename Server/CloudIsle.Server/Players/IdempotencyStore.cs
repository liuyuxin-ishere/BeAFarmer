using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CloudIsle.Net;

namespace CloudIsle.Server.Players;

/// <summary>
/// 同一玩家、同一操作、同一请求摘要只保留一份成功业务正文。
/// 每个玩家最多 64 条。顺序是首次成功 Save 的 FIFO：第 65 个新操作淘汰最早写入的一条。
/// 重放命中不改变顺序，也不计成新的业务写入。失败结果不进入这个窗口。
/// 正文不包含 seq。调用方要用本次请求的 seq 自行重封。
/// 只存在于本进程内存。不表示进程重启或换机器之后仍能 ExactlyOnce。
/// 玩家队列空闲时会摘掉执行器，但幂等窗口要留到显式 RemovePlayer，否则同进程稍后的重试会丢掉结果。
/// 本类型不接入登出，避免改动已经完成的登出业务。
/// </summary>
public sealed class IdempotencyStore
{
    /// <summary>每个玩家保留的成功操作上限。再写入一条不同的 opId 时淘汰最早成功的那条。</summary>
    public const int Capacity = 64;

    readonly object _gate = new();
    readonly Dictionary<long, Window> _windows = new();

    /// <summary>
    /// 计算请求摘要。材料是固定前缀 IDM1、小端操作类型 2 字节、以及业务载荷原文。
    /// seq、连接号和其他重试时会变的关联字段都不是参数，不能算进摘要。
    /// 结果是 SHA-256 的大写十六进制。比较时使用 Ordinal，不忽略大小写。
    /// 本方法不把载荷交给窗口保存。
    /// </summary>
    /// <param name="operationType">操作类型。相同载荷配上不同类型会得到不同摘要。</param>
    /// <param name="businessPayload">业务载荷正文。空数组可以计算。空引用会抛异常。不要把 seq 拼进这个数组。</param>
    /// <returns>64 个大写十六进制字符。窗口只保存调用方稍后传入的这个字符串，不保存载荷。</returns>
    public static string ComputeRequestHash(ushort operationType, byte[] businessPayload)
    {
        if (businessPayload == null)
            throw new ArgumentNullException(nameof(businessPayload));
        return ComputeRequestHash(operationType, (ReadOnlySpan<byte>)businessPayload);
    }

    /// <summary>
    /// 用操作类型和业务载荷计算摘要。seq 不参与。
    /// </summary>
    /// <param name="operationType">操作类型，按小端两字节写入材料。</param>
    /// <param name="businessPayload">业务载荷。只参与这一次哈希，方法返回后不保留这段字节。</param>
    /// <returns>SHA-256 的大写十六进制字符串。</returns>
    public static string ComputeRequestHash(ushort operationType, ReadOnlySpan<byte> businessPayload)
    {
        byte[] material = new byte[4 + 2 + businessPayload.Length];
        material[0] = (byte)'I';
        material[1] = (byte)'D';
        material[2] = (byte)'M';
        material[3] = (byte)'1';
        BinaryPrimitives.WriteUInt16LittleEndian(material.AsSpan(4, 2), operationType);
        businessPayload.CopyTo(material.AsSpan(6));
        return Convert.ToHexString(SHA256.HashData(material));
    }

    /// <summary>
    /// 查找已经成功的业务正文。命中时交出独立副本，不刷新 FIFO 顺序，也不记成新写入。
    /// 没有该操作时返回 false。同一操作但摘要不同时抛出冲突，不返回旧正文。
    /// 不在该玩家的队列动作里时抛出异常，不用 false 表示“现在不能访问”。
    /// </summary>
    /// <param name="playerId">玩家主键，必须是正数，并且当前异步流正在执行这名玩家的队列动作。</param>
    /// <param name="opId">操作编号，必须是正数。0 不是一次可缓存的写操作。</param>
    /// <param name="requestHash">请求摘要。按 Ordinal 与已保存的摘要比较。空引用或空白会抛异常。</param>
    /// <param name="responseBody">命中时是缓存正文的新副本。未命中或拒绝时是长度 0 的数组，不表示一份已缓存的空结果。</param>
    /// <returns>玩家、操作和摘要都一致时为 true。窗口里没有这个操作时为 false。</returns>
    public bool TryGet(long playerId, long opId, string requestHash, out byte[] responseBody)
    {
        responseBody = Array.Empty<byte>();
        RequireIdentity(playerId, opId, requestHash);
        RequireCurrent(playerId);
        lock (_gate)
        {
            if (!_windows.TryGetValue(playerId, out Window? window))
                return false;
            if (!window.ByOp.TryGetValue(opId, out Record? record))
                return false;
            if (!string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
                throw new IdempotencyConflictException(
                    $"玩家 {playerId} 的操作 {opId} 已有结果，请求摘要不同，拒绝覆盖");
            responseBody = Copy(record.ResponseBody);
            return true;
        }
    }

    /// <summary>
    /// 保存一次成功的业务正文。输入数组会被复制，之后调用方再改自己的数组不影响缓存。
    /// 同一操作已经存在时，摘要或正文任一不同都拒绝，已保存的那份保持不变，顺序也不变。
    /// 摘要和正文都相同则直接返回，不把这条挪到队尾，也不重复占名额。
    /// 新操作使该玩家超过 64 条时，淘汰最早首次写入的一条。
    /// </summary>
    /// <param name="playerId">玩家主键，必须是正数，并且当前异步流正在执行这名玩家的队列动作。</param>
    /// <param name="opId">操作编号，必须是正数。同一玩家的不同编号各占一条。</param>
    /// <param name="requestHash">这次成功请求的摘要。只存字符串，不存原始载荷。</param>
    /// <param name="responseBody">成功的业务正文，不能含有旧 seq。空引用、或长于一帧允许的正文时抛异常。空数组可以保存。</param>
    public void Save(long playerId, long opId, string requestHash, byte[] responseBody)
    {
        RequireIdentity(playerId, opId, requestHash);
        if (responseBody == null)
            throw new ArgumentNullException(nameof(responseBody));
        if (responseBody.Length > FrameCodec.MaxBodyLength)
            throw new ArgumentOutOfRangeException(nameof(responseBody), "业务正文超过一帧允许的 65536 字节");
        RequireCurrent(playerId);
        byte[] copy = Copy(responseBody);
        lock (_gate)
        {
            if (!_windows.TryGetValue(playerId, out Window? window))
            {
                window = new Window();
                _windows.Add(playerId, window);
            }

            if (window.ByOp.TryGetValue(opId, out Record? existing))
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new IdempotencyConflictException(
                        $"玩家 {playerId} 的操作 {opId} 已有结果，请求摘要不同，拒绝覆盖");
                if (!existing.ResponseBody.AsSpan().SequenceEqual(responseBody))
                    throw new IdempotencyConflictException(
                        $"玩家 {playerId} 的操作 {opId} 已保存结果，不能换成另一份业务正文");
                return;
            }

            if (window.Order.Count == Capacity)
            {
                Record oldest = window.Order.First!.Value;
                window.Order.RemoveFirst();
                window.ByOp.Remove(oldest.OpId);
            }

            var created = new Record(opId, requestHash, copy);
            window.ByOp.Add(opId, created);
            window.Order.AddLast(created);
        }
    }

    /// <summary>
    /// 在同一玩家的一次队列动作里完成查找、业务和成功保存。
    /// 命中时不调用业务动作。业务抛出异常时不写入成功缓存。
    /// 两个调用可以同时入队；同一玩家会按入队顺序各跑完一整段，因此不会出现两次未命中都去执行业务。
    /// 不同玩家使用各自的队列，可以并行。
    /// </summary>
    /// <param name="queue">玩家队列。检查、业务和保存都发生在它为该玩家执行的同一次动作里。</param>
    /// <param name="playerId">要串行执行的玩家，必须是正数。</param>
    /// <param name="opId">操作编号，必须是正数。重试必须沿用同一个编号。</param>
    /// <param name="requestHash">已经算好的请求摘要。本方法不接收原始载荷。</param>
    /// <param name="action">只有未命中时才调用。参数是这一次队列动作的取消标记。返回成功的业务正文；返回的任务或正文为空时抛异常且不保存。</param>
    /// <param name="ct">入队前已经取消则不会执行。执行中取消只传给业务动作；动作失败时不保存。</param>
    /// <returns>缓存命中或本次新写入的结果。正文是独立副本。命中标志为真时表示没有新的业务写入。</returns>
    public async Task<IdempotentReply> ExecuteAsync(
        PlayerQueue queue,
        long playerId,
        long opId,
        string requestHash,
        Func<CancellationToken, Task<byte[]>> action,
        CancellationToken ct)
    {
        if (queue == null)
            throw new ArgumentNullException(nameof(queue));
        if (action == null)
            throw new ArgumentNullException(nameof(action));
        RequireIdentity(playerId, opId, requestHash);

        IdempotentReply reply = default;
        bool assigned = false;
        await queue.EnqueueAsync(playerId, async token =>
        {
            reply = await InvokeAsync(playerId, opId, requestHash, action, token).ConfigureAwait(false);
            assigned = true;
        }, ct).ConfigureAwait(false);

        if (!assigned)
            throw new InvalidOperationException("玩家动作结束时没有幂等结果");
        return reply;
    }

    /// <summary>
    /// 丢掉一名玩家的全部内存窗口。不要求当前正处在该玩家队列里，方便在玩家不再使用时释放。
    /// 不存在的玩家再清一次也没有效果。不会改动其他玩家。
    /// 调用方要保证这名玩家没有仍会写入的队列动作；本方法不接入登出。
    /// </summary>
    /// <param name="playerId">要释放的玩家主键，必须是正数。</param>
    public void RemovePlayer(long playerId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家主键必须是正数");
        lock (_gate)
            _windows.Remove(playerId);
    }

    /// <summary>这名玩家当前保留的成功操作条数。没有窗口时为 0。供测试观察上限和清理。</summary>
    /// <param name="playerId">要查看的玩家。不要求处在该玩家队列中，也不返回正文。</param>
    /// <returns>0 到 64。已经 RemovePlayer 或从未写入时为 0。</returns>
    internal int CountFor(long playerId)
    {
        lock (_gate)
            return _windows.TryGetValue(playerId, out Window? window) ? window.Order.Count : 0;
    }

    /// <summary>当前还占着窗口的玩家数量。RemovePlayer 之后不再计入。</summary>
    internal int PlayerWindowCount
    {
        get
        {
            lock (_gate)
                return _windows.Count;
        }
    }

    /// <summary>按淘汰顺序返回 opId。下标 0 是最早首次写入、下一次新操作会先被淘汰的那条。</summary>
    /// <param name="playerId">要查看的玩家。没有窗口时返回空数组。</param>
    /// <returns>新的编号数组。重复命中不会改变这个顺序。</returns>
    internal long[] RetentionOrder(long playerId)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(playerId, out Window? window))
                return Array.Empty<long>();
            var ids = new long[window.Order.Count];
            int index = 0;
            foreach (Record record in window.Order)
                ids[index++] = record.OpId;
            return ids;
        }
    }

    /// <summary>
    /// 把该玩家已保存的摘要和业务正文接在一起，供测试确认原始请求没有留下。
    /// </summary>
    /// <param name="playerId">要查看的玩家。没有窗口时返回空数组。</param>
    /// <returns>只含摘要文本的 UTF-8 和当时保存的业务正文。不含请求载荷。</returns>
    internal byte[] ConcatenateStoredMaterial(long playerId)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(playerId, out Window? window))
                return Array.Empty<byte>();
            var parts = new List<byte>();
            foreach (Record record in window.Order)
            {
                parts.AddRange(Encoding.UTF8.GetBytes(record.RequestHash));
                parts.AddRange(record.ResponseBody);
            }

            return parts.ToArray();
        }
    }

    /// <summary>
    /// 队列动作内部的单次执行。先查，未命中才调用业务，成功后才保存。
    /// 锁只包住查和存，不包住业务动作；同一玩家不会交错，是因为整段都在同一次入队里。
    /// </summary>
    /// <param name="playerId">当前队列动作所属的玩家。</param>
    /// <param name="opId">这次请求沿用的操作编号。</param>
    /// <param name="requestHash">这次请求的摘要。</param>
    /// <param name="action">未命中时执行的业务。失败时不保存。</param>
    /// <param name="ct">队列交给这次动作的取消标记。</param>
    /// <returns>命中或新保存的独立正文。命中时 CacheHit 为真。</returns>
    async Task<IdempotentReply> InvokeAsync(
        long playerId,
        long opId,
        string requestHash,
        Func<CancellationToken, Task<byte[]>> action,
        CancellationToken ct)
    {
        if (TryGet(playerId, opId, requestHash, out byte[] cached))
            return new IdempotentReply(true, cached);

        Task<byte[]> work = action(ct);
        if (work == null)
            throw new InvalidOperationException("幂等业务动作返回了空任务");
        byte[] produced = await work.ConfigureAwait(false);
        if (produced == null)
            throw new InvalidOperationException("幂等业务动作返回了空正文");

        Save(playerId, opId, requestHash, produced);
        if (!TryGet(playerId, opId, requestHash, out byte[] stored))
            throw new InvalidOperationException("成功结果刚刚写入后无法读回");
        return new IdempotentReply(false, stored);
    }

    /// <summary>核对玩家、操作和摘要的基本合法性。不检查玩家队列上下文。</summary>
    /// <param name="playerId">玩家主键。小于等于 0 时抛异常。</param>
    /// <param name="opId">操作编号。小于等于 0 时抛异常。</param>
    /// <param name="requestHash">请求摘要。空引用或空白时抛异常。</param>
    static void RequireIdentity(long playerId, long opId, string requestHash)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家主键必须是正数");
        if (opId <= 0)
            throw new ArgumentOutOfRangeException(nameof(opId), "操作编号必须是正数");
        if (requestHash == null)
            throw new ArgumentNullException(nameof(requestHash));
        if (string.IsNullOrWhiteSpace(requestHash))
            throw new ArgumentException("请求摘要不能为空", nameof(requestHash));
    }

    /// <summary>核对当前异步流就是这名玩家的队列动作。</summary>
    /// <param name="playerId">调用方声称要访问的玩家。与当前动作的玩家不同，或不在动作中时抛异常。</param>
    static void RequireCurrent(long playerId)
    {
        if (!PlayerQueue.IsCurrentPlayer(playerId))
            throw new InvalidOperationException($"玩家 {playerId} 的幂等窗口只能由该玩家队列中的动作访问");
    }

    /// <summary>复制业务正文。空正文返回共享的空数组。</summary>
    /// <param name="source">已经确认非空的正文。</param>
    /// <returns>与 source 内容相同的另一块数组。长度为 0 时不新分配。</returns>
    static byte[] Copy(byte[] source)
    {
        if (source.Length == 0)
            return Array.Empty<byte>();
        var copy = new byte[source.Length];
        Buffer.BlockCopy(source, 0, copy, 0, source.Length);
        return copy;
    }

    /// <summary>一名玩家的成功操作窗口。字典负责按 opId 查找，链表负责 FIFO 淘汰顺序。</summary>
    sealed class Window
    {
        /// <summary>opId 到记录。同一 opId 只有一条。</summary>
        public Dictionary<long, Record> ByOp { get; } = new();

        /// <summary>首次成功写入的先后。队头最早，下一次新操作先淘汰它。</summary>
        public LinkedList<Record> Order { get; } = new();
    }

    /// <summary>一条成功操作。只保留编号、摘要和业务正文，不保留原始请求。</summary>
    sealed class Record
    {
        /// <summary>记下一条新的成功结果。正文必须已经是独立副本。</summary>
        /// <param name="opId">操作编号。</param>
        /// <param name="requestHash">请求摘要。</param>
        /// <param name="responseBody">已经复制过的业务正文。</param>
        public Record(long opId, string requestHash, byte[] responseBody)
        {
            OpId = opId;
            RequestHash = requestHash;
            ResponseBody = responseBody;
        }

        /// <summary>操作编号。</summary>
        public long OpId { get; }

        /// <summary>请求摘要。比较时使用 Ordinal。</summary>
        public string RequestHash { get; }

        /// <summary>业务正文。不含 seq。</summary>
        public byte[] ResponseBody { get; }
    }
}

/// <summary>一次幂等执行的结果。正文是独立副本，缓存命中不是新的业务写入。</summary>
public readonly struct IdempotentReply
{
    /// <summary>记下这次是命中还是新写入。</summary>
    /// <param name="cacheHit">为真表示没有调用业务动作。为假表示刚刚成功写入。</param>
    /// <param name="responseBody">业务正文的独立副本。空引用会抛异常。</param>
    public IdempotentReply(bool cacheHit, byte[] responseBody)
    {
        if (responseBody == null)
            throw new ArgumentNullException(nameof(responseBody));
        CacheHit = cacheHit;
        ResponseBody = responseBody;
    }

    /// <summary>为真时本次没有执行业务，也没有再次 Save。</summary>
    public bool CacheHit { get; }

    /// <summary>业务正文。重封时使用本次请求的 seq，不要把旧 seq 写回这里。</summary>
    public byte[] ResponseBody { get; }
}

/// <summary>同一操作已经有结果，但这次摘要或正文与已保存的不一致。</summary>
public sealed class IdempotencyConflictException : InvalidOperationException
{
    /// <summary>用说明创建冲突。说明里不放原始请求载荷。</summary>
    /// <param name="message">哪名玩家的哪个操作被拒绝，以及拒绝原因。</param>
    public IdempotencyConflictException(string message)
        : base(message)
    {
    }
}
