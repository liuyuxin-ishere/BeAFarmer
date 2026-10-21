<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 原任务步骤、源码、命令、问题和实测结果完整保留；服务端、协议、资源与公共 C# 能力可按新边界复用，旧热更 DLL／补元数据链的完成记录不构成 xLua 验收。旧自动推进在 T42 部分实现阶段停止，T42 尚未验收；后续按新方案重新安排。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发日志 · 5.3 网络和服务端基础

记录方式对齐 `06_开发计划.md` 第 6.1 节：每项写成可勾选的文档、命令或方法。目录索引见 `M1_Logs.md`。

本文件对应 `06_M1_基础框架任务拆解.md` 第 5.3 节，任务 T18–T27。

## T18：实现 TcpTransport 的发送循环

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T18，依赖 T08
- 需求：R-C03
- 依据：拆解第 3.1 节 `ServerEndpoint`；第 3.2 节“每连接只有一个发送循环，处理部分发送”；第 4.2 节 `TcpTransport.ConnectAsync` / `SendAsync` / `Close`
- 通过条件：分片发送不丢字节；并发提交两帧没有交错的头和 body
- 日期：2026-10-18

### 任务、流程与验证

这一步只做连接、发送和关闭。`ReceiveLoopAsync` 与 `NetClient` 留在 T19 及之后。T08 的 `FrameCodec` 已经能编出完整帧；本步把调用方交来的 `byte[]` 当作一整帧发送，不再解析 msgId。

`ServerEndpoint` 只保存主机和端口。端口必须在 1–65535。`ConnectAsync` 连上之后才能 `SendAsync`。重复连接要先 `Close`。`Close` 可以连续调用。关闭后，还排在队列里的发送以失败结束，不再挂起。

一次 `Socket.SendAsync` 可能只发出一部分字节。发送循环对同一帧反复发送，直到这一帧的字节都发出去，才取下一帧。并发的 `SendAsync` 只负责入队，不等待当前帧发完。因此两帧的字节不会交错。

某一帧已经发出一部分之后，如果取消或发送失败，这条连接作废。剩余字节不再从这条连接补发，队列里还没开始的帧也失败。随后必须重新 `ConnectAsync`。关闭或换代之后，旧的发送循环不能再往新连接上写。

测试不依赖操作系统碰巧少写。内部只增加一个发送接口，测试程序集通过 `InternalsVisibleTo` 使用它。假发送每次最多接受指定字节数，并可以在半帧处停住。这样能数清调用次数，也能在第一帧还没发完时提交第二帧。

```csharp
Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct)
Task SendAsync(byte[] frame, CancellationToken ct)
void Close()
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 分片发送 | 假发送每次只接受 1 字节，连续提交两帧 | 写出的字节与两帧拼接相同；发送调用次数等于总字节数 | Passed。`PartialSend_KeepsEveryByte` |
| 并发整帧 | 第一帧写出 1 字节后停住，此时另一线程提交第二帧 | 第二帧的任务在第一帧完成前就已入队；最终字节是第一帧全部再接第二帧全部 | Passed。`ConcurrentSend_WritesWholeFramesInOrder` |
| 半帧取消 | 已经写出部分字节后取消 | 当前发送结束；随后 `SendAsync` 失败；未发完的字节不会继续补上 | Passed。`Cancel_AfterPartialSend_RetiresConnection`。未发出任何字节的取消不拆连接，见 `Cancel_BeforeAnyByte_KeepsConnection` |
| 半帧失败 | 假发送在写出部分字节后抛出异常 | 连接不可再发；已入队的下一帧失败，且没有写入 | Passed。`Failure_AfterPartialSend_DropsQueuedFrame` |
| 关闭 | 发送停在半帧时 `Close`，并再调用一次 | 两次关闭都不抛异常；待发送项结束；关闭后 `SendAsync` 失败 | Passed。`Close_IsIdempotentAndFinishesPending` |
| 旧连接 | 关闭并换上新的假发送后，放行旧的半帧发送 | 旧发送器不再收到后续字节；新发送器只收到新的一帧 | Passed。`OldConnection_DoesNotWriteIntoReplacement` |
| 真实连接 | 本机 `TcpListener` 接受连接并读完全部字节 | 收到的字节与发送的两帧一致；关闭后不能再发 | Passed。`Connect_SendsToLocalListener_AndCloseBlocksFurtherSend`。拒绝连接见 `Connect_Refused_LeavesTransportClosed` |
| Edit Mode | 已打开编辑器的 Test Runner 跑 `TcpTransportTests` | 失败数 0 | `TcpTransportTests.txt` status=Passed，pass=10，fail=0，skip=0 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Network`。测试放在 `Assets/Tests/EditMode`，程序集仍是 `Game.Framework.EditModeTests`。

#### 1. 增加 `ServerEndpoint`

新建 `Assets/Framework/Network/ServerEndpoint.cs`。主机空白或端口越界时抛异常。

- [x] 已写入磁盘

#### 2. 实现连接、发送循环和关闭

新建 `Assets/Framework/Network/TcpTransport.cs`。内部发送接口只给测试替换每次写出的字节数。正式连接使用 `Socket`。`Game.Framework.asmdef` 对测试程序集开放 internal。

- [x] 已写入磁盘

#### 3. 增加 Edit Mode 测试

新建 `Assets/Tests/EditMode/TcpTransportTests.cs`。用例覆盖验证表里的分片、并发顺序、取消、失败、关闭、旧连接和本机连接。

- [x] 已写入磁盘

#### 4. 用当前编辑器的 Test Runner 跑 Edit Mode

`testMode` 为 EditMode，只筛 `TcpTransportTests`。证据写到 `Docs/Acceptance/M1/20261018-T18/`。以 Test Runner 回调写出的结果为准。

- [x] 已执行并写入证据

本步范围：发送方向的连接与整帧发送。接收循环和主线程入队留在 T19。

### 遇到的问题和解决方案

- `IOException` 在 `System.IO` 中。补上 using 后 `Game.Framework` 编译通过。
- `Game.Framework.asmdef` 的 `internalsVisibleTo` 没有生成可见性特性，测试报 `ITransportSend` 不可访问。另加 `TransportTestVisibility.cs`，用 `InternalsVisibleTo` 只对 `Game.Framework.EditModeTests` 开放。
- `Task.Run` 接受返回 `Task` 的委托时会展开成一个 `Task`，不能用来观察“方法已返回、帧尚未发完”。并发用例改成在线程里调用 `SendAsync`，用外层任务表示调用已经返回。
- 第一次正式跑测即通过，pass=10，fail=0，skip=0。证据在 `Docs/Acceptance/M1/20261018-T18/T18-send.txt`。Console 没有本次新增错误；原有的两条 AI Assistant 会话刷新错误在跑测前就存在。

### 复核修正

对照 `Temp/M1-T18-review.txt`，初稿还有三处。连接代次没有盖住正在进行的 `ConnectAsync`，`Close` 之后旧尝试仍会 `Attach`。第一次 `SendAsync` 返回 0 时，`attempted` 已经被清掉，连接没有作废。取消若落在套接字发送上，关闭套接字抛出的 `ObjectDisposedException` 或 `SocketException` 会被当成网络失败。

修正只动发送侧。`ConnectAsync` 在等待前记下本次尝试的代次，并登记正在连接。`Close` 取消这次尝试。等待结束后，代次不符就关掉刚打开的发送口，不再挂上。已经连接或已有一次连接在进行时，再调用 `ConnectAsync` 直接拒绝。发送循环在确认字节数有效之前保持“已尝试发送”。返回 0 或超过请求长度都使当前连接失效，并结束队列。调用方令牌已取消时，上述套接字异常改成取消结果。

`Game.Framework.asmdef` 里的 `internalsVisibleTo` 没有生效，删掉该字段。`TransportTestVisibility.cs` 继续提供 `InternalsVisibleTo`。

回归测试不走公网 DNS。连接用可暂停的内部打开函数。发送用固定返回值。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 连接期间关闭 | 打开函数停住后 `Close`，再放行 | 旧 `ConnectAsync` 以取消结束；随后 `SendAsync` 失败 | Passed。`Close_DuringConnect_DoesNotRevive` |
| 旧尝试晚返回 | 关闭并完成一次新连接后，旧打开函数才返回发送口 | 新连接仍可用；晚到的发送口被关闭，字节只出现在新发送口 | Passed。`LateConnect_DoesNotReplaceNewConnection` |
| 调用方取消 | 取消令牌后，打开函数才返回发送口 | 任务以取消结束；晚到的发送口被关闭；连接不能发送 | Passed。`Connect_CanceledCallerCannotAttachLateSocket` |
| 并发连接 | 第一次连接尚未返回时再调用一次 | 第二次立刻被拒绝；第一次仍可在放行后连上 | Passed。`SecondConnect_WhileConnecting_IsRejected` |
| 返回 0 | 第一次发送固定返回 0，队列里还有下一帧 | 两帧都失败；连接不能再发；没有写出字节 | Passed。`Send_ZeroBytes_RetiresConnection` |
| 非法长度 | 第一次发送返回超过请求的字节数 | 当前帧失败；连接不能再发 | Passed。`Send_CountAboveRequest_RetiresConnection` |
| 取消分类 | 半帧处取消，发送口抛出 `ObjectDisposedException` | 公开任务是取消而不是失败；连接不能再发 | Passed。`Cancel_DuringSend_ObjectDisposed_IsCancellation` |
| Edit Mode | Test Runner 再跑 `TcpTransportTests` | 失败数 0 | `TcpTransportTests.txt` status=Passed，pass=17，fail=0，skip=0 |

#### 1. 修正连接代次、零字节和取消分类

改 `TcpTransport.cs`。去掉 asmdef 的 `internalsVisibleTo`。

- [x] 已写入磁盘

#### 2. 增加上述回归测试

改 `TcpTransportTests.cs`。

- [x] 已写入磁盘

#### 3. 用当前编辑器重跑 Edit Mode

证据仍写到 `Docs/Acceptance/M1/20261018-T18/`。

- [x] 已执行并写入证据

- 复核第一次跑测即通过，pass=17，fail=0，skip=0。`Game.Framework.asmdef` 已去掉无效的 `internalsVisibleTo`。`TransportTestVisibility.cs` 仍在。Console 没有本次新增错误，仍是原先两条 AI Assistant 会话刷新错误。证据在 `Docs/Acceptance/M1/20261018-T18/T18-send.txt`。


### 执行命令与脚本

#### 命令：已打开编辑器的 Test Runner

```text
复核后的已打开编辑器 Test Runner API:
testMode=EditMode
程序集 Game.Framework.EditModeTests
分组 Game.Framework.EditModeTests.TcpTransportTests
未使用 Unity.exe -batchmode。
```

#### 脚本：Assets/Framework/Network/ServerEndpoint.cs

```csharp
using System;

namespace Game.Framework.Network
{
    /// <summary>
    /// 一次 TCP 连接要使用的主机和端口。不含账号、超时或重连策略。
    /// </summary>
    public sealed class ServerEndpoint
    {
        /// <summary>
        /// 记录连接目标。
        /// </summary>
        /// <param name="host">主机名或 IP。空白时抛出异常。</param>
        /// <param name="port">端口，必须在 1 到 65535。</param>
        public ServerEndpoint(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("主机名为空", nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1 到 65535");

            Host = host;
            Port = port;
        }

        /// <summary>主机名或 IP。</summary>
        public string Host { get; }

        /// <summary>端口。</summary>
        public int Port { get; }
    }
}
```

#### 脚本：Assets/Framework/Network/TransportTestVisibility.cs

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Game.Framework.EditModeTests")]
```

#### 脚本：Assets/Framework/Network/TcpTransport.cs（当前文件；ReceiveLoopAsync 于 T19 加在同一文件）

```csharp
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
```

### 问题经过

1. 时机：编写 `TcpTransport` 后第一次编译 `Game.Framework`。原因：代码使用了 `IOException`，文件没有 `using System.IO`。修改：补上这个 using。结果：`Game.Framework` 编译通过。
2. 时机：测试工程编译，要访问内部发送口 `ITransportSend`。原因：`Game.Framework.asmdef` 里的 `internalsVisibleTo` 不会生成 `InternalsVisibleTo` 特性。修改：新增 `TransportTestVisibility.cs`，只对 `Game.Framework.EditModeTests` 开放；复核时删掉 asmdef 里那个无效字段，特性文件保留。结果：测试程序集可以访问 internal 发送口。
3. 时机：编写并发发送用例。原因：`Task.Run` 会展开返回 `Task` 的委托，外层任务要等这一帧发完才结束，观察不到“调用已经返回、帧还在队列里”。修改：在独立线程里调用 `SendAsync`，用外层任务表示调用已经返回。结果：`ConcurrentSend_WritesWholeFramesInOrder` 能在第一帧未完成时断言第二帧已经入队。
4. 时机：对照 `Temp/M1-T18-review.txt` 复核初稿。当时第一次正式跑测已经是 10 通过。原因有三处：`ConnectAsync` 在等待前没有记下这次尝试的代次，`Close` 之后旧尝试仍会 `Attach`；`PumpAsync` 在确认字节数之前把 `attempted` 清掉，第一次 `SendAsync` 返回 0 时连接没有作废；取消落在套接字发送上时，关闭套接字抛出的 `ObjectDisposedException` 或 `SocketException` 被当成网络失败。修改：等待前登记代次，`Close` 取消这次尝试，代次不符就关掉晚到的发送口；返回 0 或超过请求长度都使当前连接失效；调用方令牌已取消时，上述套接字异常改成取消结果。结果：用已打开编辑器的 Test Runner 重跑 `TcpTransportTests`，17 通过、0 失败、0 跳过。证据是 `Docs/Acceptance/M1/20261018-T18/T18-send.txt`。


## T19：实现 ReceiveLoopAsync 与主线程入队

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T19，依赖 T18
- 需求：R-C03
- 依据：第 4.2 节 `ReceiveLoopAsync(Action<Packet> onPacket, CancellationToken ct)`；第 3.2 节半包、粘包、空 body、非法长度立即断开
- 通过条件：后台不操作 Unity 对象；主线程能按接收顺序取出 Packet
- 日期：2026-10-18

### 任务、流程与验证

这一步补上接收。发送循环保持 T18 的行为。`ReceiveLoopAsync` 在后台读字节，用已有的 `FrameCodec.TryDecode` 切帧。半包留在一块不超过整帧上限的缓冲里，粘包在同一次读完后继续解，直到不够一帧。解出的 `Packet` 使用 `TryDecode` 已经拷走的正文，随后压紧缓冲，不把这块接收内存交给下一次读或回调。

回调只负责入队。`NetClient` 只做这条队列：后台 `EnqueueReceived`，主线程 `DispatchPending` 按顺序交给调用方。Pending、超时和订阅留在 T20 之后。分发线程必须是创建 `NetClient` 的那一条，测试里用它代表主线程。回调本身不改 Model 或 UI。

同一连接不能同时跑两份接收循环。`Close`、取消、对端关掉和非法长度都结束这一份循环。代次变了以后，旧循环不再投递，也不关闭新连接。非法长度只断开当时那一代。

接收缓冲固定为整帧最大长度 65546。队列最多保留 256 个包，满了就失败，不再继续堆积。

```csharp
Task ReceiveLoopAsync(Action<CloudIsle.Net.Packet> onPacket, CancellationToken ct)
void EnqueueReceived(Packet packet)
void DispatchPending(int maxMessages)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 任意位置拆包 | 可控读取每次只给 1 到整帧长度的字节，循环覆盖一个帧的每个切分 | 每个切分都解出同一包；正文与编码一致 | 通过。`Split_EveryChunkSize_DecodesSamePacket` |
| 粘包与顺序 | 一次读入两帧，以及切分落在两帧之间 | 先第一帧后第二帧 | 通过。`Coalesce_AndBoundarySplit_KeepOrder` |
| 空 body | 编码长度为 0 的正文 | `Body` 非空且长度为 0 | 通过。`EmptyBody_IsOwnedEmptyArray` |
| 非法长度 | 写入 len=0 | 接收失败；该连接不能再发 | 通过。`InvalidLength_RetiresConnection` |
| EOF | 读取立刻返回 0 | 循环结束；没有包 | 通过。`Eof_CompletesWithoutPackets`。当前这一代随后不能再发 |
| 取消与关闭 | 读停住时取消，另一次读停住时 `Close` | 取消以取消结束；关闭后循环结束 | 通过。`CancelAndClose_StopTheLoop` |
| 旧连接 | 关闭并换上新读取后，放行旧读取 | 旧帧不入队；新连接只收到自己的帧 | 通过。`OldConnection_DoesNotDeliverIntoReplacement`。新读取口在断言时仍未关闭 |
| 主线程分发 | 包进队列后看 Model，再 `DispatchPending` | 分发前 Model 不变；分发后按顺序变化；别的线程调用被拒绝 | 通过。`Dispatch_UpdatesModelOnlyOnOwnerThreadInOrder`。分发回调抛错时未取出的包仍留在队列 |
| 真实 TCP | 本机监听端写出两帧，其中一帧为空 body | 主线程取出的顺序与内容一致 | 通过。`LocalTcp_DispatchesEmptyBodyThenPayload` |
| 并行接收 | 第一份循环未结束时再启动一次 | 第二次被拒绝 | 通过。`SecondReceive_WhileRunning_IsRejected` |
| Edit Mode | Test Runner 跑 `TcpTransportTests` 与新增接收测试 | 失败数 0 | 通过。pass=28，fail=0，skip=0 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。传输仍在 `Game.Framework.Network`。帧类型继续用 `CloudIsle.Net.Packet`。

#### 1. 实现接收循环和队列桥

在 `TcpTransport` 上增加 `ReceiveLoopAsync`。内部读取口只给套接字和测试替换分片。`NetClient` 从空壳改成入队和主线程分发。

- [x] 已写入磁盘

#### 2. 增加 Edit Mode 测试

新建 `Assets/Tests/EditMode/TcpTransportReceiveTests.cs`。拆包用可控读取。另有一条本机 TCP。

- [x] 已写入磁盘

#### 3. 用当前编辑器跑 Edit Mode

先跑 `TcpTransportTests`，再跑 `TcpTransportReceiveTests`。证据写到 `Docs/Acceptance/M1/20261018-T19/`。

- [x] 已执行并写入证据

本步范围：接收切帧和主线程按序取出。请求 Pending 留在 T20。

### 遇到的问题和解决方案

- 接收缓冲是一块 65546 字节的数组。解完完整帧后用一次 `BlockCopy` 把尾部半包挪到开头，不按字节复制整块缓冲，也不把这块数组交给回调。
- 对端返回 0 时结束当前这一代并断开它，所以 EOF 之后不能再发。代次已经变了的旧循环只返回，不调用断开，因此不会关掉换上去的连接。非法长度、取消和读异常同样只断开仍属于自己的那一代。
- 旧连接用例要在新帧入队后继续停住下一次读取。否则新连接自己的 EOF 会关掉读取口，分不清是旧循环关的。回调里挂住下一次读之后，断言看到的是新帧，读取口仍开着。
- 第一次同组跑测是 pass=27，fail=1：`OldConnection_DoesNotDeliverIntoReplacement` 在新帧已经入队后看到读取口被关闭。测试在新帧回调里挂住下一次读取，用来把新连接自己的 EOF 和旧循环关闭区分开。该用例在随后的同组重跑中通过。中间另有一次 `Cancel_BeforeAnyByte_KeepsConnection` 被编辑器原有的 AI Assistant 会话刷新错误日志判失败，断言没有失败。最后一次是 pass=28，fail=0，skip=0。
- Console 仍只有跑测前就存在的两条 AI Assistant 会话刷新错误。没有本次传输代码的新错误。证据在 `Docs/Acceptance/M1/20261018-T19/T19-receive.txt`。


### 执行命令与脚本

#### 命令：已打开编辑器的 Test Runner

```text
已打开编辑器 Test Runner API。
testMode=EditMode
程序集 Game.Framework.EditModeTests
分组 TcpTransportTests、TcpTransportReceiveTests
证据目录 Docs/Acceptance/M1/20261018-T19/
```

#### 脚本：TcpTransport.ReceiveLoopAsync（T19 加在同一文件）

```csharp
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
```

### 问题经过

1. 时机：实现接收缓冲。原因：一帧最大 65546 字节，解完完整帧后缓冲尾部还可能留着半包，不能把这块数组交给回调。修改：用一次 `BlockCopy` 把尾部半包挪到开头。结果：回调只拿到已经复制出来的 `Packet`，半包留在接收缓冲里。
2. 时机：处理对端返回 0、非法长度、取消和读异常。原因：旧接收循环如果无条件断开，会把已经换代的新连接关掉。修改：对端返回 0 时结束当前这一代并断开它；代次已经变了的旧循环只返回，不调用断开。结果：旧连接不会关掉换上去的连接。
3. 时机：第一次同组跑测，通过 27、失败 1。失败用例是 `OldConnection_DoesNotDeliverIntoReplacement`。原因：新帧已经入队后，新连接自己的 EOF 关掉了读取口，测试分不清是旧循环关的。修改：测试在新帧回调里挂住下一次读取，把新连接自己的 EOF 和旧循环关闭分开。结果：该用例在随后的同组重跑中通过。中间另有一次 `Cancel_BeforeAnyByte_KeepsConnection` 被编辑器里原有的 AI Assistant 会话刷新错误日志判失败，用例断言本身没有失败。最后一次是通过 28、失败 0、跳过 0。


## T20：实现 NetClient Pending 匹配

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T20，依赖 T19、T04
- 需求：R-C03
- 依据：第 4.2 节 `SendRequestAsync`；第 3.2 节响应同时匹配连接代次、seq 和预期 msgId
- 通过条件：乱序响应按 seq 完成；错误 msgId 和旧连接代次不能完成当前请求
- 日期：2026-10-18

### 任务、流程与验证

这一步给 `NetClient` 加上请求等待。发送前分配非 0 的 seq，用 `MsgRegistry` 查出应有的响应 msgId，把等待登记进 Pending，再把整帧交给现有的 `TcpTransport.SendAsync`。`FrameCodec` 和 `MsgRegistry` 继续用已生成的实现，不另写一套对照表。

接收回调产生时就把当时的连接代次和 `Packet` 一起入队。主线程 `DispatchPending` 只用入队时记下的代次，不用消费瞬间的“当前代次”给旧包改标签。代次、seq、响应 msgId 三者都对上才完成对应等待。错误 msgId 留着等待，后面的正确响应仍可完成。推送 seq 为 0 不碰 Pending。重复或没有登记过的 seq 不第二次完成。已经排在队列里的旧代次包直接丢掉。

T19 的入队方式保留：创建时所在线程才能分发；回调抛错时，还没取出的包留在队列里。超时、取消和断线把 Pending 清零留在 T21，本步不实现那三项验收。

```csharp
UniTask<TResponse> SendRequestAsync<TResponse>(IMessage request, TimeSpan timeout, CancellationToken ct)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 乱序响应 | 同时发出登录和注册，先回注册再回登录 | 两个等待各自得到自己的响应 | 通过。`OutOfOrderResponses_CompleteTheirOwnRequests` |
| 错误 msgId | 同一 seq 先送别的消息，再送配对响应 | 第一次不完成；第二次完成 | 通过。`WrongMsgId_LeavesPending_UntilMatchingResponse` |
| 旧代次 | 换连接前入队，以及旧回调在换连接后才入队 | 这些包不完成新请求；新代次的配对响应可以完成 | 通过。`OldGeneration_DoesNotCompleteTheNewRequest` |
| 推送 | seq 为 0 的通知 | Pending 数量不变，登录请求仍在等 | 通过。`PushSeqZero_DoesNotTouchPending` |
| 重复响应 | 同一 seq 的正确响应送两次 | 只完成一次 | 通过。`DuplicateResponse_CompletesOnce`。结果是第一份 |
| 原有收发 | 修改 `NetClient` 后重跑接收测试，并带上发送测试 | 失败数 0 | 通过。三组合计 pass=34，fail=0，skip=0 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。类型仍在 `Game.Framework.Network`。消息类型用 `CloudIsle.Proto`。

#### 1. 实现 Pending 匹配

`NetClient` 增加 `SendRequestAsync`。等待表的键包含代次和 seq。接收入口可以绑定创建回调时的代次。

- [x] 已写入磁盘

#### 2. 增加 Edit Mode 测试

新建 `Assets/Tests/EditMode/NetClientPendingTests.cs`。用可控发送口看发出的帧，用入队模拟响应。

- [x] 已写入磁盘

#### 3. 用当前编辑器跑 Edit Mode

跑 `NetClientPendingTests`，以及 `TcpTransportTests`、`TcpTransportReceiveTests`。证据写到 `Docs/Acceptance/M1/20261018-T20/`。

- [x] 已执行并写入证据

本步范围：按代次、seq、响应 msgId 完成等待。超时、取消和断线清理留在 T21。

### 遇到的问题和解决方案

- 等待登记发生在整帧编码成功之后。正文超过 65536 字节时 `FrameCodec` 拒绝，Pending 保持为空，也不发送。
- 发送循环在另一线程写出帧。测试在读 seq 之前等到帧已经记下，避免把还没写出当成没发送。
- 第一次同组跑测 pass=29，fail=4，都是上面的读取时机。修正后重跑 pass=34，fail=0，skip=0。其中 Pending 6 个，T19 接收 11 个，T18 发送 17 个。
- Console 仍只有跑测前就存在的两条 AI Assistant 会话刷新错误。证据在 `Docs/Acceptance/M1/20261018-T20/T20-pending.txt`。


### 执行命令与脚本

#### 命令：已打开编辑器的 Test Runner

```text
已打开编辑器 Test Runner API。
testMode=EditMode
程序集 Game.Framework.EditModeTests
分组 NetClientPendingTests、TcpTransportTests、TcpTransportReceiveTests
证据目录 Docs/Acceptance/M1/20261018-T20/
```

#### 脚本：Assets/Framework/Network/NetClient.cs

```csharp
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
            if (timeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            EnsureOwner();
            ct.ThrowIfCancellationRequested();
            if (transport == null)
                throw new InvalidOperationException("没有可发送的连接");

            ushort requestId = MsgRegistry.GetId(request.GetType());
            if (!MsgRegistry.TryGetResponseId(requestId, out ushort responseId))
                throw new InvalidOperationException("该请求没有配对响应");
            if (MsgRegistry.GetId(typeof(TResponse)) != responseId)
                throw new InvalidOperationException("响应类型与请求不配对");

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
                        return (TResponse)await waiting.Done.Task;
                    if (Detach(waiting))
                        Release(waiting);
                    throw;
                }

                IMessage message = await waiting.Done.Task;
                return (TResponse)message;
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

        /// <summary>返回当前连接代次。</summary>
        int CurrentGeneration()
        {
            lock (gate)
                return generation;
        }

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
```

### 问题经过

1. 时机：处理超过帧上限的正文。原因：如果先登记 Pending 再编码，`FrameCodec` 拒绝后会留下一个永远等不到的请求。修改：等待登记放在整帧编码成功之后。结果：正文超过 65536 字节时 Pending 保持为空，也不发送。
2. 时机：第一次同组跑测，通过 29、失败 4。原因：发送循环在另一线程写出帧，测试在帧还没记下时就去读 seq。修改：读 seq 之前先等到帧已经记下。结果：重跑通过 34、失败 0、跳过 0。其中 Pending 6 个，接收 11 个，发送 17 个。


## T21：实现请求超时、取消和 Disconnect 清理

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T21，依赖 T20
- 需求：R-C03
- 依据：第 4.2 节 `SendRequestAsync` 与 `Disconnect(string reason)`；第 3.2 节默认超时 8 秒，断线后晚到包不得完成新请求
- 通过条件：8 秒超时有明确错误；取消或断线后 Pending 归零；晚到包不能复活请求
- 日期：2026-10-18

### 任务、流程与验证

这一步补上等待的结束方式。`SendRequestAsync` 仍使用调用方传入的超时。默认时长 8 秒，按真实经过时间计算，到点抛 `TimeoutException`。调用方取消时保留取消语义，不改成超时或普通网络错误。成功、发送失败、超时、取消和 `Disconnect` 都只结束同一次等待，并卸掉计时器和取消注册。

`Disconnect(string reason)` 增加连接代次，清掉已经入队的旧包，结束当前全部等待，并关闭旧连接。重复调用不再抛错，也不要碰到已经换上的新请求。旧等待自己的计时或取消晚到时，只认自己的代次和那一次登记，不能关掉新连接，也不能完成新请求。

接收循环结束、对端关闭和接收异常都走同一条清理，不另留一个没人调用的断线方法。生产接收入口在启动那一刻用 T20 的方式记下代次，旧回调不能改用入队时的新代次。请求结果若要继续改 Model 或 UI，仍在创建 `NetClient` 的线程上完成。

```csharp
UniTask<TResponse> SendRequestAsync<TResponse>(IMessage request, TimeSpan timeout, CancellationToken ct)
void Disconnect(string reason)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 8 秒超时 | 用默认超时发一个请求，不回包 | 抛出超时错误；耗时接近 8 秒；Pending 为 0 | 通过。`DefaultTimeout_FailsWithTimeoutAndClearsPending` |
| 发送后取消 | 整帧发出后再取消调用方令牌 | 以取消结束；Pending 为 0 | 通过。`CancelAfterSend_ClearsPending` |
| 断线 | 两个请求后 `Disconnect`，再调用一次 | 两个等待都结束；Pending 为 0；第二次不抛错 | 通过。`Disconnect_FailsEveryPending_AndRepeatIsSafe` |
| 成功后不超时 | 短超时内先回包，再多等一会儿 | 请求保持成功，不会再被超时打断 | 通过。`Success_IsNotTimedOutLater` |
| 晚到包 | 取消或断线后再发新请求，旧回调送入同 seq 的响应 | 新请求仍在等；新代次的响应可以完成 | 通过。`LatePacket_AfterDisconnect_DoesNotCompleteNewRequest` |
| 旧计时 | 短超时请求被断线结束后立刻发新请求，并等到旧超时已过 | 新请求不被旧计时结束 | 通过。`OldTimeout_DoesNotFailTheReplacementRequest` |
| 本机 TCP | 连上后由对端关闭 | 等待以断线结束；Pending 为 0 | 通过。`RemoteClose_EndsThePendingRequest` |
| 原有匹配和收发 | 重跑 Pending、接收和发送测试 | 失败数 0 | 通过。四组合计 pass=41，fail=0，skip=0 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。改动留在 `Game.Framework.Network.NetClient`。

#### 1. 实现超时、取消和断线清理

在现有 Pending 上登记计时和取消。增加 `Disconnect`。接收循环退出时按启动时的代次清理。

- [x] 已写入磁盘

#### 2. 增加 Edit Mode 测试

新建 `Assets/Tests/EditMode/NetClientLifecycleTests.cs`。8 秒用默认超时。竞态用更短的超时。本机 TCP 由对端关闭。

- [x] 已写入磁盘

#### 3. 用当前编辑器跑 Edit Mode

跑 `NetClientLifecycleTests`、`NetClientPendingTests`、`TcpTransportReceiveTests` 和 `TcpTransportTests`。证据写到 `Docs/Acceptance/M1/20261018-T21/`。

- [x] 已执行并写入证据

本步范围：超时、取消和断线把等待清干净。重连退避留在 T26。

### 遇到的问题和解决方案

- 超时用 `System.Threading.Timer`，按真实毫秒计，不依赖播放循环。成功、失败、取消或断线会卸掉计时器，所以完成后不会再被超时打断。
- 请求完成若发生在别的线程，会送回创建 `NetClient` 时的同步上下文，避免在计时线程上改 Model 或 UI。
- 接收循环退出时按启动那一刻记下的代次清理。代次已经变了就直接返回，不关闭新连接。
- 第一次同组跑测 pass=40，fail=1。失败点是成功用例在 `await` 之后再读 `UniTask.Status`，UniTask 不允许这样读。断言改成看已经拿到的结果和 Pending。重跑 pass=41，fail=0，skip=0。其中生命周期 7 个，Pending 6 个，接收 11 个，发送 17 个。
- Console 仍只有跑测前就存在的两条 AI Assistant 会话刷新错误。证据在 `Docs/Acceptance/M1/20261018-T21/T21-lifecycle.txt`。




### 协调复核待处理

- [x] 发送仍阻塞时，超时也必须结束调用方。已加入 `Timeout_WhileSending_EndsRequest` 回归用例。实际：单独跑测 status=Passed，pass=1，fail=0，skip=0。50ms 超时在发送挂起时以 `TimeoutException` 结束，Pending 为 0。证据 `Docs/Acceptance/M1/20261018-T21/Timeout_WhileSending_EndsRequest.txt`。
- [x] 超时和取消后释放取消注册；处理零超时或同步取消早于资源字段赋值的竞态。实际：超时、取消、成功、发送失败和断线都会卸下计时器与取消注册；计时器先停用再发布，取消注册在回调返回后若请求已结束则当即释放。`ZeroTimeout_WhileSending_EndsWithTimeout` 以 `TimeoutException` 结束且 Pending 为 0。`Cancel_WhileSending_EndsRequest` 以 `OperationCanceledException` 结束且 Pending 为 0。
- [x] 会话清理完整过程须保护代次，旧清理不能在新连接开始后调用 Close 误关新连接。实际：接收循环结束只关闭自己记下的发送口；发送口已经换成新连接时，旧清理结束旧等待并让出代次，不调用会关掉新连接的 `Close`。`OldCleanup_DoesNotCloseReplacementConnection` 通过：旧请求以 `IOException` 结束，新连接未被关闭，新请求仍在等待。

复现和检查细节见 `Temp/M1-T21-review.txt`。以上三项均已修复，并用当前编辑器的 TestRunnerApi 验证。

同组回归：`NetClientLifecycleTests`、`NetClientPendingTests`、`TcpTransportReceiveTests`、`TcpTransportTests`。第一次 status=Failed，pass=44，fail=1，skip=0。失败点是 `Cancel_WhileSending_EndsRequest` 用精确类型断言，实际异常是派生的 `TaskCanceledException`，请求已经按取消结束。断言改为捕获 `OperationCanceledException` 后重跑，status=Passed，pass=45，fail=0，skip=0。其中生命周期 11 个（原 7 个加上阻塞发送超时、阻塞发送取消、零超时、旧清理不关新连接），Pending 6 个，接收 11 个，发送 17 个。证据 `Docs/Acceptance/M1/20261018-T21/T21-lifecycle-review.txt`。

Console 仍只有原先两条 AI Assistant 会话刷新错误：一条来自 `GetAvailableModelProfiles`，一条来自 `ConversationRefresh`。没有本次网络代码或测试新增的编译错误、异常。电量心跳 100%，交流电源接通。`Temp/M1Execution.low-battery` 不存在。


### 执行命令与脚本

#### 命令：Temp/M1-EditModeRunner-reference.cs

```csharp
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

internal class CommandScript : IRunCommand
{
    /// <summary>在已打开的编辑器里跑指定的 EditMode 测试，并把结果写入证据文件。</summary>
    /// <param name="result">把启动结果回给命令执行器。</param>
    public void Execute(ExecutionResult result)
    {
        if (File.Exists("Temp/M1Execution.low-battery"))
        {
            result.LogError("low battery");
            return;
        }
        if (EditorApplication.isCompiling || EditorUtility.scriptCompilationFailed)
        {
            result.LogError("compiling={0} failed={1}", EditorApplication.isCompiling, EditorUtility.scriptCompilationFailed);
            return;
        }

        string dir = "Docs/Acceptance/M1/20261018-T21";
        Directory.CreateDirectory(dir);
        string path = dir + "/NetClientLifecycleTests.txt";
        File.WriteAllText(path, "running\n");
        TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new Recorder(path));
        Filter filter = new Filter();
        filter.testMode = TestMode.EditMode;
        filter.assemblyNames = new[] { "Game.Framework.EditModeTests" };
        filter.groupNames = new[]
        {
            "Game.Framework.EditModeTests.NetClientLifecycleTests",
            "Game.Framework.EditModeTests.NetClientPendingTests",
            "Game.Framework.EditModeTests.TcpTransportReceiveTests",
            "Game.Framework.EditModeTests.TcpTransportTests"
        };
        api.Execute(new ExecutionSettings(filter));
        result.Log("rerun t21");
    }

    sealed class Recorder : ICallbacks
    {
        readonly string path;
        readonly StringBuilder sb = new StringBuilder();

        /// <summary>把测试结果追加到这个文件。</summary>
        /// <param name="path">证据文件路径。</param>
        public Recorder(string path)
        {
            this.path = path;
        }

        /// <summary>整次运行开始。这里不写额外内容。</summary>
        /// <param name="testsToRun">即将运行的测试树。</param>
        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        /// <summary>整次运行结束时写入总数。</summary>
        /// <param name="result">整次运行的汇总结果。</param>
        public void RunFinished(ITestResultAdaptor result)
        {
            sb.Insert(0, string.Format(
                "status={0} pass={1} fail={2} skip={3}{4}",
                result.TestStatus,
                result.PassCount,
                result.FailCount,
                result.SkipCount,
                System.Environment.NewLine));
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>单条测试开始。这里不写额外内容。</summary>
        /// <param name="test">即将运行的测试。</param>
        public void TestStarted(ITestAdaptor test)
        {
        }

        /// <summary>单条叶子测试结束时写入名称和结果。</summary>
        /// <param name="result">这一条测试的结果。</param>
        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.HasChildren)
                return;
            sb.Append(result.TestStatus).Append(' ').Append(result.FullName).Append(System.Environment.NewLine);
            if (result.TestStatus == TestStatus.Failed)
            {
                sb.Append(result.Message).Append(System.Environment.NewLine);
                if (!string.IsNullOrEmpty(result.StackTrace))
                    sb.Append(result.StackTrace).Append(System.Environment.NewLine);
            }
        }
    }
}

```

### 问题经过

1. 时机：实现请求超时。原因：超时不能依赖播放循环，完成回调也不能留在计时线程上改界面。修改：用 `System.Threading.Timer` 按真实毫秒计时；成功、失败、取消或断线都卸掉计时器；请求完成若在别的线程，就送回创建 `NetClient` 时的同步上下文。结果：完成后不会再被超时打断。
2. 时机：第一次同组跑测，通过 40、失败 1。原因：成功用例在 `await` 之后再读 `UniTask.Status`，UniTask 不允许这样读。修改：断言改成看已经拿到的结果和 Pending。结果：重跑通过 41、失败 0。其中生命周期 7 个，Pending 6 个，接收 11 个，发送 17 个。
3. 时机：对照 `Temp/M1-T21-review.txt` 复核。原因有三处：`SendRequestAsync` 先等待发送、再等待完成，定时器只结束等待时，发送还堵着，调用方结束不了；超时和取消只释放计时器，零超时或同步取消可能在字段赋值前就进回调，取消注册泄漏；会话清理在放出锁之后调用 `Close`，新一代已经开始连接时会被旧清理关掉。修改：超时会取消仍在进行的发送，并仍以 `TimeoutException` 结束；所有结束路径释放计时器和取消注册；接收循环只关闭自己记下的发送口，发送口已经换成新连接时不调用会关掉新连接的 `Close`。结果：同组第一次通过 44、失败 1。失败点是 `Cancel_WhileSending_EndsRequest` 用精确类型断言，实际是派生的 `TaskCanceledException`，请求已经按取消结束。断言改为捕获 `OperationCanceledException` 后重跑，通过 45、失败 0、跳过 0。


## T22：实现 PipelineFrameReader.ReadLoopAsync

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T22，依赖 T08 / T05
- 需求：R-S01
- 依据：第 3.2 节帧布局与 consumed/examined；第 4.5 节 `PipelineFrameReader.ReadLoopAsync`
- 通过条件：测试跨 segment 的头和正文、半包；没有重复消费、忙循环，也不引用已经释放的缓冲
- 日期：2026-10-19

### 任务、流程与验证

这一步在服务端增加正式收帧循环。调用方把 `PipeReader` 交进来，每解出一帧就调用一次业务回调。帧格式沿用已经完成的 `FrameCodec`：4 字节小端长度、6 字节消息头、正文。长度只能是 6 到 65542，整帧只能是 10 到 65546。长度非法时马上失败，不等待后续字节。数据不够一整帧时只观察、不消费，等下一次读取。连接已经结束却还留着半帧时，明确失败。

`Packet` 的正文是自己的数组。循环在 `AdvanceTo` 之前把正文拷出来，所以回调开始时即使管道改写或释放旧缓冲，包里的值也不变。每次读取只取一帧。粘在一起的后续帧留到下一次读取。取消和回调抛出的异常都会结束循环，并用 `CompleteAsync` 把同一个错误交给读取器。

```csharp
Task ReadLoopAsync(PipeReader reader, Func<CloudIsle.Net.Packet, Task> onPacket, CancellationToken ct)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 长度头跨段 | 自定义多段 `ReadOnlySequence`，4 字节长度被切开 | 解出原消息；序列不是单段 | 通过。`CrossSegment_LengthHeader_DecodesPacket` |
| 消息头跨段 | 6 字节 msgId/seq 被切开 | msgId 与 seq 正确 | 通过。`CrossSegment_MessageHeader_DecodesMsgIdAndSeq` |
| 正文跨段 | 正文落在不同段 | 正文字节与原包一致 | 通过。`CrossSegment_Body_KeepsBytes` |
| 粘包 | 一次缓冲里放两帧 | 先回调第一帧并 `AdvanceTo` 到帧尾，再取第二帧；不重复 | 通过。`CoalescedFrames_AdvancesOneFrameAtATime` |
| 半包 | 真实 `Pipe` 先写不完整帧，停一会儿再补齐 | 等待期间没有包；读取次数保持个位数；补齐后只有一帧 | 通过。`PartialFrame_OnPipe_WaitsWithoutBusyLoop` |
| 缓冲改写 | `AdvanceTo` 时把原缓冲填成别的值 | 回调看到的 `Packet` 仍是原 msgId、seq、正文 | 通过。`AdvanceTo_RewritesBuffer_PacketKeepsOwnBytes` |
| 最大长度 | body 为 65536 | 成功解出，整帧 65546 | 通过。`MaxFrame_AcrossSegments_Succeeds` |
| 非法长度 | len 为 5 和 65543 | 立即失败并完成读取器 | 通过。`InvalidLength_FailsImmediately` |
| EOF 半包 | 写入半帧后关闭写入端 | 明确失败，不当成正常结束 | 通过。`Eof_WithPartialFrame_Fails` |
| 取消 | 读取等待时取消令牌 | 向外抛出取消，并完成读取器 | 通过。`Cancel_WhileWaiting_CompletesReader` |
| 回调异常 | 回调抛出异常 | 同一异常向外传播，并传给 `CompleteAsync` | 通过。`CallbackFault_CompletesReaderAndPropagates` |
| 已有拆包 | 重跑 `FrameCodecTests` | 失败数 0 | 通过。`FrameCodecTests` 11 个全部通过 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改服务端收帧、测试项目引用、本项测试、本日志和本项证据。不改 PoC 广场或灰盒。

#### 1. 接入 Pipelines 并实现读循环

服务端保持 net8.0。`System.IO.Pipelines` 用 8.0.0 这个与 .NET 8 对齐的包，不升 SDK，也不改其他包。新增 `Server/CloudIsle.Server/Network/PipelineFrameReader.cs`。同步辅助方法先看 4 字节长度，合法且到齐后再拷贝头和正文。成功时 `AdvanceTo` 到这一帧的尾，再等待回调。半包 `AdvanceTo(buffer.Start, buffer.End)`。结束时 `CompleteAsync`。

- [x] 已写入磁盘

#### 2. 让测试工程引用服务端

`CloudIsle.Tests` 增加对 `CloudIsle.Server` 的项目引用。测试放在 `Server/CloudIsle.Tests`。跨段用最小的 `ReadOnlySequenceSegment` 和 `PipeReader` 夹具，并记下每次 `AdvanceTo`。半包用真实 `Pipe`。

- [x] 已写入磁盘

#### 3. 跑 .NET 8 测试并保留报告

`dotnet test` 目标 net8.0，跑新的读循环测试和已有 `FrameCodecTests`。控制台和 TRX 留在 `Docs/Acceptance/M1/20261019-T22/`。

- [x] 已执行并写入证据。证据目录：`Docs/Acceptance/M1/20261019-T22/`

本步范围：服务端按帧读取。TCP 监听和会话留在 T23。

### 遇到的问题和解决方案

- net8.0 目标框架的默认引用里没有 `System.IO.Pipelines`。服务端增加 `System.IO.Pipelines` 8.0.0，与 .NET 8 对齐。SDK 和其他包版本没有改。
- 这个包里的 `PipeReader.AdvanceTo(SequencePosition)` 也是抽象方法。测试夹具补了这个重载，并转给带 examined 的重载。
- 改写缓冲时 `Array.Fill` 不能从 `byte[]?` 推断类型参数。调用改成 `Array.Fill<byte>`。
- 修正后重跑：总数 22，通过 22，失败 0，跳过 0。其中 `PipelineFrameReaderTests` 11 个，`FrameCodecTests` 11 个。控制台在 `Docs/Acceptance/M1/20261019-T22/T22-console.txt`，TRX 在同目录 `T22.trx`。


### 执行命令与脚本

#### 命令：Docs/Acceptance/M1/20261019-T22/T22-reader.txt

```text
T22 PipelineFrameReader.ReadLoopAsync
日期：2026-10-19
命令：dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --framework net8.0
过滤：FullyQualifiedName~FrameCodecTests|FullyQualifiedName~PipelineFrameReaderTests
目标：.NET 8（.NETCoreApp,Version=v8.0）
结果：测试运行成功
总数：22
通过：22
失败：0
跳过：0
PipelineFrameReaderTests：11
FrameCodecTests：11
控制台：Docs/Acceptance/M1/20261019-T22/T22-console.txt
TRX：Docs/Acceptance/M1/20261019-T22/T22.trx
依赖：System.IO.Pipelines 8.0.0
```

#### 脚本：Server/CloudIsle.Server/Network/PipelineFrameReader.cs

```csharp
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;

namespace CloudIsle.Server.Network
{
    /// <summary>
    /// 从 PipeReader 里每次取出一帧。正文在 AdvanceTo 之前拷贝进 Packet。
    /// </summary>
    public static class PipelineFrameReader
    {
        /// <summary>
        /// 持续读帧并调用业务回调。半包等待新数据；长度非法或结束时仍有半帧则失败。
        /// 取消和回调异常都会 CompleteAsync，并把同一个异常传出去。
        /// </summary>
        /// <param name="reader">管道读取端。</param>
        /// <param name="onPacket">每取出一帧后调用。返回后才继续读下一帧。</param>
        /// <param name="ct">取消读取。</param>
        public static async Task ReadLoopAsync(
            PipeReader reader,
            Func<Packet, Task> onPacket,
            CancellationToken ct)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (onPacket == null)
                throw new ArgumentNullException(nameof(onPacket));

            Exception? error = null;
            try
            {
                while (true)
                {
                    ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    if (result.IsCanceled || ct.IsCancellationRequested)
                    {
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        throw new OperationCanceledException(ct);
                    }

                    FrameTake take = TryTakeOne(buffer, out Packet packet, out SequencePosition frameEnd);
                    if (take == FrameTake.Success)
                    {
                        reader.AdvanceTo(frameEnd, frameEnd);
                        await onPacket(packet).ConfigureAwait(false);
                        continue;
                    }

                    bool hasPartialFrame = buffer.Length > 0;
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (take == FrameTake.Invalid)
                        throw new InvalidDataException("帧长度非法");

                    if (result.IsCompleted)
                    {
                        if (hasPartialFrame)
                            throw new InvalidDataException("连接结束时仍有未完成的帧");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                await reader.CompleteAsync(error).ConfigureAwait(false);
            }
        }

        /// <summary>只看缓冲最前面的一帧。长度不够 4 字节或整帧未到齐时不分配正文。</summary>
        /// <param name="buffer">当前已经读到的字节。</param>
        /// <param name="packet">成功时得到复制出来的包。</param>
        /// <param name="frameEnd">成功时指向这一帧的结尾，供 AdvanceTo 使用。</param>
        private static FrameTake TryTakeOne(
            ReadOnlySequence<byte> buffer,
            out Packet packet,
            out SequencePosition frameEnd)
        {
            packet = default;
            frameEnd = buffer.Start;

            if (buffer.Length < 4)
                return FrameTake.NeedMore;

            Span<byte> lengthBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(lengthBytes);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (payloadLength < FrameCodec.HeaderSize || payloadLength > FrameCodec.MaxPayloadLength)
                return FrameTake.Invalid;

            long frameLength = 4L + payloadLength;
            if (buffer.Length < frameLength)
                return FrameTake.NeedMore;

            Span<byte> header = stackalloc byte[FrameCodec.HeaderSize];
            buffer.Slice(4, FrameCodec.HeaderSize).CopyTo(header);
            ushort msgId = BinaryPrimitives.ReadUInt16LittleEndian(header);
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(2));

            int bodyLength = (int)payloadLength - FrameCodec.HeaderSize;
            byte[] body = bodyLength == 0 ? Array.Empty<byte>() : new byte[bodyLength];
            if (bodyLength > 0)
                buffer.Slice(4 + FrameCodec.HeaderSize, bodyLength).CopyTo(body);

            packet = new Packet(msgId, seq, body);
            frameEnd = buffer.GetPosition(frameLength);
            return FrameTake.Success;
        }

        private enum FrameTake
        {
            NeedMore,
            Success,
            Invalid
        }
    }
}
```

### 问题经过

1. 时机：服务端工程编译 `PipelineFrameReader`。原因：net8.0 的默认引用里没有 `System.IO.Pipelines`。修改：服务端增加 `System.IO.Pipelines` 8.0.0，不升 SDK，也不改其他包。结果：可以引用 `PipeReader`。
2. 时机：编写测试夹具。原因：这个包里的 `PipeReader.AdvanceTo(SequencePosition)` 也是抽象方法，只实现带 examined 的重载还不够。修改：夹具补上这个重载，并转给带 examined 的重载。结果：夹具可以实例化。
3. 时机：改写测试缓冲。原因：`Array.Fill` 不能从 `byte[]?` 推断类型参数。修改：调用改成 `Array.Fill<byte>`。结果：修正后重跑，总数 22，通过 22，失败 0，跳过 0。其中读循环 11 个，`FrameCodecTests` 11 个。


## T23：实现 TcpServer 与 ClientSession

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T23，依赖已完成的 T22 / T05
- 需求：R-S01
- 依据：第 3.2 节每连接一个发送循环、部分发送、断线收尾；第 4.5 节 `TcpServer.RunAsync` 与 `ClientSession.RunAsync` / `SendAsync` / `CloseAsync`
- 通过条件：两个客户端各自收发；关掉一个，另一个继续；慢连接发送有界
- 日期：2026-10-19

### 任务、流程与验证

这一步在服务端增加正式监听和会话。`TcpServer` 在本机端点上接连接，给每个连接一个不会复用的 `SessionId`，并持有这些会话的任务。停止时关掉监听和这些连接，再等这些任务结束。某一个连接读失败、写失败或主动关闭，只结束它自己，监听和其他会话继续。

`ClientSession` 的接收直接走已经完成的 `PipelineFrameReader`。发送只有一个循环：调用 `SendAsync` 时先把这一帧编成自己的字节再放入有界队列，循环一次写完一整帧再取下一帧，所以并发提交不会把两帧的头和正文穿插在一起。队列已满就关闭这条慢连接，正在写的和还排着队的发送都要结束，不能停在半路上。工作循环里调用关闭时不能再等待自己。重复关闭、对端关掉、取消和异常都要释放套接字并停掉排队发送。不使用无人等待的 `async void`。

广场和灰盒仍用 `Server/CloudIsle.Server/Net` 里的旧会话。本步不改那条路径，也不做消息路由和账号。

```csharp
Task RunAsync(System.Net.IPEndPoint endpoint, CancellationToken ct)
Task RunAsync(CancellationToken ct)
Task SendAsync(CloudIsle.Net.Packet packet, CancellationToken ct)
Task CloseAsync(string reason, CancellationToken ct)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 两个客户端 | 本机 loopback、端口 0，各发一帧并收回复 | 两帧内容对应；两个 SessionId 不同 | 通过。`TwoClients_OnLoopback_RoundTripWithDistinctSessionIds` |
| 关闭一个 | 关闭会话 A 后再让 B 收发 | A 结束；B 仍能收发 | 通过。`CloseOneSession_OtherClientKeepsRoundTrip` |
| 对端 EOF | A 直接断开，B 再收发 | 只结束 A；B 正常 | 通过。`PeerEof_DoesNotStopOtherClient` |
| 并发发送 | 两帧同时提交，传输一次只写部分字节 | 线上字节能逐帧解出，头和正文不交错；入队后的帧不随原正文被改掉 | 通过。`ConcurrentSends_AreWholeFrames_AndOwnBytes` |
| 队列容量 | 慢写停住时按容量继续入队 | 容量内可以入队；再多一帧就关闭 | 通过。容量 2，停在内部写边界时再入队 2 帧，第 3 帧关闭 |
| 慢连接超限 | 可控的内部写边界，不依赖操作系统套接字缓冲 | 超限后当前发送和排队发送都结束 | 通过。`SlowWrite_OverQueueCapacity_FinishesCurrentAndQueuedSends` |
| 取消服务器 | 取消监听令牌 | 已有连接结束；同一端口可以再次绑定 | 通过。`CancelServer_ReleasesConnectionsAndPort` |
| 重复关闭 | 连续和重叠调用 CloseAsync | 都返回，不挂起 | 通过。`CloseAsync_Repeated_DoesNotHang` |
| 回调里关闭 | 收到包的回调里调用 CloseAsync | 关闭返回，会话结束，不和自身死锁 | 通过。`CloseFromReceiveCallback_DoesNotDeadlock` |
| 业务异常 | 一个连接的收包回调抛异常 | 只结束该连接，另一个连接继续 | 通过。`HandlerException_DoesNotStopOtherClient` |
| 已有分帧 | 重跑 `PipelineFrameReaderTests` | 失败数 0 | 通过。11 个全部通过 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改 `Network` 下的监听和会话、测试可见性、本项测试、本日志和本项证据。

#### 1. 实现有界会话

新增 `Server/CloudIsle.Server/Network/ClientSession.cs`。接收用 `PipeReader` 加 `PipelineFrameReader`。发送用有界 `Channel`，单独一个写循环，按传输返回的字节数处理部分写出。入队前用 `FrameCodec.Encode` 得到独立帧。队列写不进去就关闭本会话。关闭、取消、EOF 和对端异常都完成排队任务并释放流。从接收或发送循环里关闭时只发信号，不等待自己。

- [x] 已写入磁盘

#### 2. 实现监听

新增 `Server/CloudIsle.Server/Network/TcpServer.cs`。`RunAsync` 绑定调用方给出的端点，接受连接并分配递增的 `SessionId`。每个会话的任务单独捕获异常。取消后停止监听、关闭已有会话并等待这些任务。测试工程通过 `InternalsVisibleTo` 使用最小的写边界，不增加公共框架层。

- [x] 已写入磁盘

#### 3. 跑 .NET 8 测试并保留报告

`dotnet test` 跑新的会话测试，并重跑 `PipelineFrameReaderTests`。控制台和 TRX 留在 `Docs/Acceptance/M1/20261019-T23/`。通过之后才勾选本日志和拆解表 T23。

- [x] 已执行并写入证据。证据目录：`Docs/Acceptance/M1/20261019-T23/`

本步范围：服务端 TCP 监听和会话收发。消息路由留在 T24。

### 遇到的问题和解决方案

- 第一次编译时，对端 EOF 测试对只实现异步释放的包装调用了 `Dispose`。测试改为关闭内部的 `TcpClient`。
- 第一次把控制台经管道写入文件时编码乱了。随后用 UTF-8 重跑同一条 `dotnet test`。这次总数 20，通过 20，失败 0，跳过 0。其中 `TcpServerSessionTests` 9 个，`PipelineFrameReaderTests` 11 个。控制台在 `Docs/Acceptance/M1/20261019-T23/T23-console.txt`，TRX 在同目录 `T23.trx`，命令在 `T23-command.txt`。电量 100%，交流电源接通。`Temp/M1Execution.low-battery` 不存在。


### 执行命令与脚本

#### 命令：Docs/Acceptance/M1/20261019-T23/T23-command.txt

```text
dotnet test "E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.Tests\CloudIsle.Tests.csproj" --filter "FullyQualifiedName~TcpServerSessionTests|FullyQualifiedName~PipelineFrameReaderTests" --results-directory "E:\Workbench\UnityWorkBench\Farming\Docs\Acceptance\M1\20261019-T23" --logger "trx;LogFileName=T23.trx" --logger "console;verbosity=normal"
```

#### 脚本：Server/CloudIsle.Server/Network/TcpServer.cs

```csharp
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
```

#### 脚本：Server/CloudIsle.Server/Network/ClientSession.cs

```csharp
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
            BeginClose(string.IsNullOrWhiteSpace(reason) ? "关闭" : reason);
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
```

### 问题经过

1. 时机：第一次把 `dotnet test` 的控制台经管道写入证据文件。原因：管道使用的编码不是 UTF-8，中文输出乱码。修改：用 UTF-8 重跑同一条 `dotnet test`，过滤仍是 `TcpServerSessionTests` 与 `PipelineFrameReaderTests`。结果：总数 20，通过 20，失败 0，跳过 0。其中会话 9 个，读循环 11 个。控制台在 `T23-console.txt`。


## T24：实现 MessageRouter.Register/DispatchAsync

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T24，依赖已完成的 T23 / T04
- 需求：R-S01
- 依据：第 3.1 节 `RequestContext`；第 3.2 节可定位但反序列化失败连续 5 次断开、合法消息重置，长度非法仍由 T22 处理；第 4.5 节 `MessageRouter.Register` / `DispatchAsync`；`05_协议与接口.md` 系统消息 1001–1010、限流 20 次/秒、错误码 102 / 104
- 通过条件：认证前系统白名单可达；未认证受保护请求返回 102 且不调用 handler；重复注册或类型与消息 ID 不符时失败；同一主体第 21 次返回 104；关闭会话后清掉本会话计数
- 日期：2026-10-19

### 任务、流程与验证

这一步只做服务端消息路由。`RequestContext` 放在 Shared，是纯 C# 类型，字段为 `SessionId`、`PlayerId`、`MsgId`、`Seq`。未认证时 `PlayerId` 为 0。类型和消息 ID 的对应只用已经生成的 `MsgRegistry`，不另写一份协议注册表。

当前正式注册表只有 1001–1010：心跳、注册、登录、重连的请求和响应，以及 `LogoutReq`、`KickNtf`。没有通用错误消息。`LogoutReq` 没有响应。网关拒绝不新增线路消息，也不给登出造响应。已有配对响应的请求，把 102 或 104 写进该响应的 `code`。没有响应、或尚未进入 M1 的业务，只在路由拒绝边界记下 code，用测试夹具核对，不宣称这些业务协议已经上线。

认证前可以到达的系统请求是心跳、注册、登录、重连。登出必须已经认证。普通请求每个主体每秒 20 次；未认证主体按 `SessionId` 分开计数。可定位但正文反序列化失败时，同一会话连续 5 次就关闭；中间出现合法消息则把计数清零。关闭会话时丢掉该会话的限流和非法消息状态，避免会话记录无界增长。日志只记 `SessionId`、`msgId`、`seq` 和结果码，不写密码或 Token。

```csharp
void Register<TRequest>(ushort msgId, Func<RequestContext, TRequest, CancellationToken, Task> handler) where TRequest : class, IMessage
Task DispatchAsync(RequestContext context, Packet packet, CancellationToken ct)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 未认证白名单 | 未认证会话发送心跳、注册、登录、重连 | handler 收到正确类型 | 通过。`PreAuthWhitelist_ReachesRegisteredHandlers` |
| 未认证受保护请求 | 未认证发送已注册的登出，以及尚未进入 M1 的业务 ID | code=102；登出 handler 未被调用；不产生伪响应 | 通过。`ProtectedRequest_WhenUnauthenticated_Rejects102WithoutHandler`。1009 与夹具 2001 都是 102，发送列表为空 |
| 已认证 | 已认证会话发送与注册类型一致的请求 | handler 被调用 | 通过。`AuthenticatedRequest_ReachesMatchingHandler` |
| 注册约束 | 同一 ID 注册两次；类型与 `MsgRegistry` 的 ID 不一致 | 两次都失败，先注册的仍在 | 通过。`Register_DuplicateIdOrWrongType_Fails` |
| 限流 | 可控时钟下同一主体连续请求，另一主体穿插，再推进一秒 | 第 21 次 code=104 且 handler 未被调用；另一主体不受影响；新窗口恢复 | 通过。未认证按会话分开；同一玩家的两个会话共享窗口。推进 999 毫秒仍是 104，再推进 1 毫秒恢复 |
| 反序列化 | 同一会话连续提交可定位的坏正文，中间插入合法包 | 连续 5 次关闭；合法包把计数清零 | 通过。`DeserializeFailures_CloseOnFifth_AndLegalPacketResets` |
| 关闭释放 | 会话已有限流或失败计数后调用释放 | 本会话计数消失；同一秒内可以重新计数 | 通过。`ReleaseSession_DropsCounters`。释放 30 个会话后限流、绑定和失败计数都是 0 |
| 追踪 | 请求带密码和 Token | 日志有 SessionId、msgId、seq；不含密码和 Token | 通过。白名单测试里的追踪行含 session=11、msg=1005、seq=3，不含密码和 Token |
| 已有会话与分帧 | 重跑 T23、T22 测试 | 失败数 0 | 通过。`TcpServerSessionTests` 9 个，`PipelineFrameReaderTests` 11 个 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改路由、必要的 Shared 上下文、本项测试、本日志、拆解表 T24 和本项证据。

#### 1. 补齐 RequestContext

若 Shared 还没有 `RequestContext`，按 C# 9 / .NET Standard 2.1 增加 `Assets/Shared/Network/RequestContext.cs`。随后用当前 Unity 导入并看 Console，不用服务端编译代替这一步。

- [x] 已写入磁盘

#### 2. 实现 MessageRouter

新增 `Server/CloudIsle.Server/Network/MessageRouter.cs`。重复 ID 或类型与消息 ID 不符时注册失败。`DispatchAsync` 按上面的白名单、限流、真实响应 `code` 和无响应拒绝边界处理。反序列化连续失败 5 次关闭会话。提供关闭会话后的状态释放。测试注入可控时钟。

- [x] 已写入磁盘

#### 3. 跑 .NET 8 测试并保留报告

`dotnet test` 跑 `MessageRouterTests`，并重跑 `TcpServerSessionTests` 与 `PipelineFrameReaderTests`。控制台、TRX、命令和协议拒绝策略说明留在 `Docs/Acceptance/M1/20261019-T24/`。通过之后才勾选本日志和拆解表 T24。

- [x] 已执行并写入证据。证据目录：`Docs/Acceptance/M1/20261019-T24/`

本步范围：服务端消息路由和 Shared 请求上下文。不实现心跳业务、账号写入或 M2 玩法。`TcpServer` 与 `ClientSession` 没有改。会话真正关闭时，后续接线调用 `ReleaseSession`。

### 遇到的问题和解决方案

- 第一次编译 `MessageRouter` 时，可空的响应引用被赋给不可空局部变量，出现 CS8600。局部变量改为 `IMessage?` 后，正式那次编译不再出现这条警告。
- 第一次让 Unity 导入时，命令返回尚未发现编辑器。随后编辑器状态可读，再次强制导入 `RequestContext.cs` 并刷新。编译结束后 `Game.Shared` 里能构造 `CloudIsle.Net.RequestContext`，编辑器不在编译。Console 没有这条脚本的错误。已有的 Unity AI Assistant 联网错误与这次导入无关。
- 正式测试总数 29，通过 29，失败 0，跳过 0。其中 `MessageRouterTests` 9 个，`TcpServerSessionTests` 9 个，`PipelineFrameReaderTests` 11 个。控制台在 `Docs/Acceptance/M1/20261019-T24/T24-console.txt`，TRX 在同目录 `T24.trx`，命令在 `T24-command.txt`，拒绝策略在 `T24-protocol-rejection.md`。电量 100%，交流电源接通。`Temp/M1Execution.low-battery` 不存在。




### 协调复核补充：拒绝未经解析的消息不重置计数

复核发现未认证拒绝和未知 ID 两个分支尚未成功反序列化，却会清空此前的失败计数。已经移除这两处清空，仅在成功解析后重置。补充 `UnparsedRejectedPacket_DoesNotResetDeserializeFailures(0)` 与 `(7)`，分别覆盖未认证和已认证主体：四次反序列化失败、中间夹一条未经解析的拒绝消息，再来一次失败，仍须关闭。

- [x] 修正两处分支，保留已解析合法消息重置计数的行为。
- [x] 重新编译后执行补充用例和原有路由/会话/分帧回归：31 通过，0 失败，0 跳过。此前 29 项报告早于这两个新增用例，最终以 `T24-review.trx` 为准。

证据：`Docs/Acceptance/M1/20261019-T24/T24-review.trx`、`T24-review-console.txt`、`T24-review-command.txt`。Shared 类型本次复核没有变化，沿用本项已验证的 Unity 编译结果。


### 执行命令与脚本

#### 命令：Docs/Acceptance/M1/20261019-T24/T24-command.txt

```text
dotnet test "E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.Tests\CloudIsle.Tests.csproj" --filter "FullyQualifiedName~MessageRouterTests|FullyQualifiedName~TcpServerSessionTests|FullyQualifiedName~PipelineFrameReaderTests" --results-directory "E:\Workbench\UnityWorkBench\Farming\Docs\Acceptance\M1\20261019-T24" --logger "trx;LogFileName=T24.trx" --logger "console;verbosity=normal" --no-restore

工作目录：E:\Workbench\UnityWorkBench\Farming
这次是可空警告修正后的正式结果。总数 29，通过 29，失败 0。
TRX：Docs/Acceptance/M1/20261019-T24/T24.trx
控制台：Docs/Acceptance/M1/20261019-T24/T24-console.txt
```

#### 复核命令：Docs/Acceptance/M1/20261019-T24/T24-review-command.txt

```text
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageRouterTests|FullyQualifiedName~TcpServerSessionTests|FullyQualifiedName~PipelineFrameReaderTests" --results-directory Docs/Acceptance/M1/20261019-T24 --logger "trx;LogFileName=T24-review.trx" --logger "console;verbosity=normal"

DOTNET_CLI_UI_LANGUAGE=en-US; working directory E:\Workbench\UnityWorkBench\Farming.
Result: 31 passed, 0 failed, 0 skipped. Includes UnparsedRejectedPacket_DoesNotResetDeserializeFailures(0) and (7).
```

#### 脚本：Assets/Shared/Network/RequestContext.cs

```csharp
namespace CloudIsle.Net
{
    /// <summary>
    /// 服务端分发一条消息时带上的调用上下文。
    /// 未认证时 PlayerId 为 0。这里不放密码、Token 或正文。
    /// </summary>
    public sealed class RequestContext
    {
        /// <summary>
        /// 记录这一次分发属于哪条连接、哪个玩家、哪条消息。
        /// </summary>
        /// <param name="sessionId">连接会话编号。</param>
        /// <param name="playerId">已认证玩家编号。未认证传 0。</param>
        /// <param name="msgId">本帧消息 ID。</param>
        /// <param name="seq">本帧序号。</param>
        public RequestContext(long sessionId, long playerId, ushort msgId, uint seq)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            MsgId = msgId;
            Seq = seq;
        }

        /// <summary>连接会话编号。</summary>
        public long SessionId { get; }

        /// <summary>已认证玩家编号。未认证为 0。</summary>
        public long PlayerId { get; }

        /// <summary>本帧消息 ID。</summary>
        public ushort MsgId { get; }

        /// <summary>本帧序号。</summary>
        public uint Seq { get; }
    }
}
```

#### 脚本：Server/CloudIsle.Server/Network/MessageRouter.cs

```csharp
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
```

#### 引用文件：Docs/Acceptance/M1/20261019-T24/T24-protocol-rejection.md

```markdown
# T24 协议拒绝策略

正式 `MsgRegistry` 只有 1001–1010。本步没有新增线路消息，也没有把背包、种植等业务协议接上线。

## 认证前白名单

ID 取自 `MsgRegistry.GetId`，不是另一份注册表。未认证（`PlayerId <= 0`）可以到达：

| ID | 类型 |
|---|---|
| 1001 | HeartbeatReq |
| 1003 | RegisterReq |
| 1005 | LoginReq |
| 1007 | ReconnectReq |

`LogoutReq`（1009）必须已认证。`KickNtf`（1010）不是认证前请求。

## 错误怎么送回去

- 1001、1003、1005、1007 有真实响应。拒绝时解析对应的 1002 / 1004 / 1006 / 1008，只写 `code`，序号沿用请求。登录错误响应不填写 Token。
- 1009 没有响应。102 或 104 只记在路由结果里，不生成 Logout 响应。
- 尚未进入 M1 的业务 ID（夹具使用 2001）同样没有配对响应，不发帧。未认证记 102。已认证记 101，表示正式注册表没有这条消息，不把它当成已实现的业务。

102 是未登录。104 是同一主体在当前 1 秒窗口内的第 21 次普通请求。已认证按 `PlayerId` 计数，未认证按 `SessionId` 分开计数。

## 反序列化

长度非法仍由 T22 的分帧直接断开，路由不重复处理。已经分出帧但 `MsgRegistry.Parse` 抛出 protobuf 格式错误时，同一会话连续 5 次就关闭，原因是「连续反序列化失败」，不另发错误帧。中间任何合法消息都会把这个计数清零。未知消息 ID 走上面的 101 / 102 边界，不计入这 5 次。

## 关闭会话

`ReleaseSession` 删除该会话的限流、绑定和非法消息计数。该玩家已经没有其他会话时，同时删除玩家限流计数。`TcpServer` / `ClientSession` 本步没有改；会话结束时由后续接线调用 `ReleaseSession`。

## 日志

追踪行只有 `session`、`msg`、`seq`、`code`、`action`。不写正文、密码或 Token。
```

### 问题经过

1. 时机：第一次编译 `MessageRouter`。原因：可空的响应引用被赋给不可空局部变量，出现 CS8600。修改：局部变量改为 `IMessage?`。结果：正式那次编译不再出现这条警告。随后测试总数 29，通过 29，失败 0。
2. 时机：第一次让 Unity 导入 `RequestContext.cs`。原因：命令返回时还没有发现编辑器。修改：编辑器状态可读之后，再次强制导入并刷新。结果：编译结束时 `Game.Shared` 里能构造 `CloudIsle.Net.RequestContext`，编辑器不在编译，Console 没有这条脚本的错误。已有的 AI Assistant 联网错误与这次导入无关。
3. 时机：29 项正式结果写出之后的复核。原因：未认证拒绝和未知 ID 两个分支还没有成功反序列化，却清空了此前的失败计数。修改：去掉这两处清空，只在成功解析后重置；补充 `UnparsedRejectedPacket_DoesNotResetDeserializeFailures(0)` 与 `(7)`。结果：重新编译后 31 通过、0 失败、0 跳过。此前 29 项报告早于这两个用例，最终以 `T24-review.trx` 为准。


## T25：实现 HeartbeatHandler 与 HeartbeatService

- 来源：`06_M1_基础框架任务拆解.md` 第 5.3 节 T25，依赖已完成的 T20 / T24
- 需求：R-C03
- 依据：第 4.2 节 `HeartbeatService.RunAsync`、`ServerClock.Synchronize` / `NowUnixMs`；第 4.5 节 `HeartbeatHandler.HandleAsync`；正式 `HeartbeatReq` / `HeartbeatResp`（1001 / 1002，`code` 与 `server_time`）
- 通过条件：10 秒按发起时刻发送；连续 30 秒没有合法心跳响应时只断线一次，且不被尚未结束的请求拖过 30 秒；合法响应重置静默；取消后不再发送；同一时刻只有一个心跳循环，取消后可以再启动；请求失败不留下 Pending，也不产生未观察异常
- 日期：2026-10-19

### 任务、流程与验证

这一步只做心跳和客户端显示校时。服务端 `HeartbeatHandler` 用现有 `MessageRouter.Register` 接入。它不读取客户端时间做判定，只记下这条连接的存活时间，并用同一个 seq 回 `HeartbeatResp`：`code` 为 0，`server_time` 为真实 UTC Unix 毫秒。

客户端 `HeartbeatService.RunAsync(TimeSpan interval, TimeSpan silenceTimeout, CancellationToken ct)` 默认间隔 10 秒、静默 30 秒。下一次发送从本次发起时刻起算，不把响应耗时再加进间隔。静默计时和单次请求等待分开；请求还停在 await 上时，满 30 秒仍要断线，并且只断一次。只有 `code == 0` 的心跳响应才刷新最后有效响应时刻，并调用 `ServerClock.Synchronize`。计时用真实经过时间，不看 `Time.timeScale`。取消会停掉发送、等待和静默监测，并清掉这次心跳自己的 Pending。重复启动不会跑出第二份循环；取消后可以再启动。新循环不使用旧循环的静默起点，旧响应也不能断开或校时新会话。

`ServerClock` 只给客户端显示提供偏移后的 Unix 毫秒。不实现重连退避，也不做心跳 UI。测试用内部时钟推进 10 秒和 30 秒，不把这只时钟加进公共接口。

```csharp
Task HandleAsync(RequestContext context, HeartbeatReq request, CancellationToken ct)
UniTask RunAsync(TimeSpan interval, TimeSpan silenceTimeout, CancellationToken ct)
void Synchronize(long serverUnixMs, long localUnixMs)
long NowUnixMs()
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 服务端响应 | 经 MessageRouter 分发正式心跳请求 | 响应 ID 为 1002，seq 与请求相同，code 为 0，server_time 落在调用前后的真实 UTC 毫秒内；存活时间等于该 server_time，且不等于客户端填写的时间 | 通过。`Handle_UsesRequestSeqAndRealUtcMilliseconds`：seq=17，code=0，server_time 在调用前后，存活时间与之相同，且不是请求里的 -123456789 |
| 10 秒节奏 | 内部时钟每次推进 10 秒，请求一直不回 | 按发起时刻发出下一拍，不因响应未完成而改成“耗时 + 10 秒” | 通过。`Interval_FollowsSendStart_WhileRequestStaysPending`。推进两个 10 秒后有 3 条未完成请求 |
| 30 秒静默 | 内部时钟推进到 30 秒，请求仍未结束 | 只断线一次；断线发生在推进返回之时，不继续等这条请求 | 通过。`Silence_DisconnectsOnce_WithoutWaitingForTheOpenRequest`。29 秒不断，再推进 1 秒断一次；再推进 60 秒仍是一次 |
| 合法响应 | 先给出 code 为 0 的响应，再推进时间；另测非 0 响应 | 合法响应把静默起点重置为收到时刻并校时；非 0 不重置、不校时 | 通过。`ValidResponse_ResetsSilence_AndRejectCodeDoesNot`。code=104 不校时并在 30 秒断开；code=0 把显示时间对齐到给定 server_time，之后 29 秒不断、满 30 秒断一次 |
| 取消 | 发出一次后取消，再把时钟向前推进 | 不再发送；Pending 为 0；不因取消而断线 | 通过。`Cancel_StopsFurtherSends_AndClearsPending` |
| 单一循环 | 第一份仍在运行时再次启动；取消后再启动 | 第二次启动失败且没有第二份发送；取消后可以再跑一份 | 通过。`SecondStart_IsRejected_UntilTheFirstLoopIsCanceled` |
| 旧循环 | 取消或断开旧循环后再启动，并投入旧响应 | 旧响应不校时、不完成新请求，也不让新会话提前断线 | 通过。`Restart_IgnoresTheOldLoopClockAndLateResponse`。旧响应不改变显示时间，新请求仍在等待；新循环自己的 30 秒才断开 |
| 请求失败 | 发送口直接失败 | Pending 回到 0；没有未观察的任务异常 | 通过。`SendFailure_DoesNotLeakPending_OrUnobservedExceptions` |
| 调用线程 | 推进时钟、分发响应和断线都发生在创建 NetClient 的线程 | `SendRequestAsync` 与 `Disconnect` 不离开该线程 | 通过。断线发生在测试线程；另一线程调用 `RunAsync` 被拒绝。`RunAsync_FromAnotherThread_IsRejected` |
| 已有客户端回归 | Unity EditMode 重跑 T20/T21 的 NetClient 与 TcpTransport 测试，并跑本项心跳测试 | 失败数 0 | 通过。合计 53，失败 0，跳过 0。其中心跳 8，NetClientLifecycle 11，NetClientPending 6，TcpTransportReceive 12，TcpTransport 16 |
| 路由回归 | `dotnet test` 跑心跳测试和 MessageRouterTests | 失败数 0 | 通过。合计 13，失败 0，跳过 0。其中 HeartbeatHandlerTests 2，MessageRouterTests 11 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改心跳、必要的网络校时、本项测试、本日志、拆解表 T25 和本项证据。

#### 1. 服务端 HeartbeatHandler

新增 `Server/CloudIsle.Server/Account/HeartbeatHandler.cs`。`Register` 走现有路由。`HandleAsync` 更新会话存活时间并发送同 seq 的成功响应。

- [x] 已写入磁盘

#### 2. 客户端心跳与校时

新增 `Assets/Framework/Network/ServerClock.cs` 与 `HeartbeatService.cs`。内部时钟只供测试推进 10/30 秒。默认间隔和静默分别是 10 秒、30 秒。

- [x] 已写入磁盘

#### 3. 测试与证据

EditMode 用 `Temp/M1-EditModeRunner-reference.cs` 的 TestRunnerApi 跑心跳和既有客户端回归。服务端用 `dotnet test` 跑心跳与 `MessageRouterTests`。证据留在 `Docs/Acceptance/M1/20261019-T25/`。通过之后才勾选本日志和拆解表 T25。

- [x] 已执行并写入证据。证据目录：`Docs/Acceptance/M1/20261019-T25/`

本步范围：心跳处理、心跳循环和显示校时。不含重连退避、操作号和账号 UI。

### 遇到的问题和解决方案

- 服务端第一次编译时，`HeartbeatResp.ToByteArray` 缺少 `Google.Protobuf` 引用。补上 using 后通过。EditMode 测试同样缺这个 using，Unity 因此报 CS1061，`scriptCompilationFailed` 为真。补上后类型可以加载，失败标志恢复为假。
- 第一次 EditMode 有 4 个心跳测试失败，既有 NetClient 与 TcpTransport 回归没有失败。取消后的第二次启动沿用了已经取消的令牌，所以没有发出新心跳；测试改为换一张新令牌。合法响应的校时续延要等发送完成回到创建线程，断言改为等到显示时间对齐。另一线程的异常在 Unity 里直接抛出，不包一层 `AggregateException`。重启用例在新心跳已经写出之后才读取帧数，于是多等了一拍。修正这四处后重跑。
- 正式 EditMode 总数 53，通过 53，失败 0，跳过 0。名单在 `Docs/Acceptance/M1/20261019-T25/HeartbeatEditMode.txt`。服务端总数 13，通过 13，失败 0，跳过 0。控制台在同目录 `T25-console.txt`，TRX 在 `T25.trx`，命令在 `T25-command.txt`。Unity Console 里只有原有的 AI Assistant 联网错误，没有本项脚本错误。电量 100%，交流电源接通。`Temp/M1Execution.low-battery` 不存在。


### 协调复核结果

- [x] 发送和静默循环转成可重复等待的 Task，使用 WhenAny/WhenAll，在退出路径取消并观察所有自有工作，避免重复消费 UniTask。包含这些修正的 Unity 回归为 53 通过、0 失败、0 跳过。
- [x] 活动心跳使用 HashSet 记录，完成立即移除，不再保存整个运行期的历史；请求超时改为既有 NetClient.DefaultTimeout（8 秒），静默监测独立满足 30 秒。
- [x] 使用 GetTimestamp/Frequency 和已支持的 Task API 计时。ManualHeartbeatClock 已移至 Assets/Tests/EditMode，生产代码只保留最小接口和真实时钟。移动后 Unity 实际编译并跑测通过。

具体复核点见 `Temp/M1-T25-review.txt`。最终客户端结果见 `HeartbeatEditMode.txt`。

服务端另补 `HeartbeatHandler.ReleaseSession`，关闭连接时可释放其存活记录，重复释放安全。新增释放用例后重编译，心跳与路由回归共 14 通过、0 失败、0 跳过，最终证据为 `Docs/Acceptance/M1/20261019-T25/T25-review.trx`、`T25-review-console.txt`。后续正式接线需要在会话结束时同时调用心跳的 ReleaseSession 和路由的 ReleaseSession，不能遗漏。


### 执行命令与脚本

#### 命令：Docs/Acceptance/M1/20261019-T25/T25-command.txt

```text
dotnet test "Server\CloudIsle.Tests\CloudIsle.Tests.csproj" --filter "FullyQualifiedName~HeartbeatHandlerTests|FullyQualifiedName~MessageRouterTests" --logger "trx;LogFileName=T25.trx" --results-directory "Docs\Acceptance\M1\20261019-T25"
```

#### 复核命令：Docs/Acceptance/M1/20261019-T25/T25-review-command.txt

```text
dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --no-restore --filter "FullyQualifiedName~HeartbeatHandlerTests|FullyQualifiedName~MessageRouterTests" --results-directory Docs/Acceptance/M1/20261019-T25 --logger "trx;LogFileName=T25-review.trx" --logger "console;verbosity=normal"

Working directory: E:\Workbench\UnityWorkBench\Farming; DOTNET_CLI_UI_LANGUAGE=en-US.
Final server result:14 passed,0 failed,0 skipped. Includes ReleaseSession_DropsLivenessRecord_AndIsIdempotent.
Client final result:53 passed,0 failed,0 skipped in HeartbeatEditMode.txt (after lifetime changes and test clock relocation).
```

#### 脚本：Server/CloudIsle.Server/Account/HeartbeatHandler.cs

```csharp
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
```

#### 脚本：Assets/Framework/Network/ServerClock.cs

```csharp
using System;
using System.Threading;

namespace Game.Framework.Network
{
    /// <summary>
    /// 保存服务端时间与本机 UTC 的差值，供界面显示。
    /// 服务端的发奖和超时不读取这里的结果。
    /// </summary>
    public sealed class ServerClock
    {
        long _offsetMs;

        /// <summary>
        /// 用一对同时取得的时间戳记下偏移。偏移等于服务端毫秒减去本机毫秒。
        /// </summary>
        /// <param name="serverUnixMs">响应里的服务端 UTC Unix 毫秒。</param>
        /// <param name="localUnixMs">收到该响应时的本机 UTC Unix 毫秒。</param>
        public void Synchronize(long serverUnixMs, long localUnixMs)
        {
            Interlocked.Exchange(ref _offsetMs, serverUnixMs - localUnixMs);
        }

        /// <summary>
        /// 返回校时后的 UTC Unix 毫秒。还没校时过时，结果就是本机 UTC。
        /// </summary>
        public long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Read(ref _offsetMs);
        }
    }
}
```

#### 脚本：Assets/Framework/Network/HeartbeatService.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Network
{
    /// <summary>
    /// 按固定间隔发送心跳，并在静默超时后断开当前连接一次。
    /// 发送间隔从发起时刻计算。静默监测不跟着某一次请求一起等待。
    /// </summary>
    public sealed class HeartbeatService
    {
        /// <summary>正式环境使用的发送间隔。</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

        /// <summary>正式环境使用的静默上限。超过后断开一次。</summary>
        public static readonly TimeSpan DefaultSilenceTimeout = TimeSpan.FromSeconds(30);

        static readonly TimeSpan RequestTimeout = NetClient.DefaultTimeout;

        readonly NetClient _client;
        readonly ServerClock _serverClock;
        readonly IHeartbeatClock _clock;
        readonly SynchronizationContext _ownerContext;
        readonly int _ownerThreadId;
        readonly HashSet<Task> _watches = new HashSet<Task>();
        CancellationTokenSource _loop;
        int _running;
        int _epoch;
        int _disconnected;
        long _lastValid;

        /// <summary>
        /// 绑定创建时所在的线程。之后的发送和断线都回到这条线程。
        /// </summary>
        /// <param name="client">已经连上的客户端。</param>
        /// <param name="serverClock">收到合法心跳后更新的显示时钟。</param>
        public HeartbeatService(NetClient client, ServerClock serverClock)
            : this(client, serverClock, new SystemHeartbeatClock())
        {
        }

        /// <summary>用指定时钟创建心跳。测试用它推进 10 秒和 30 秒。</summary>
        /// <param name="client">发送心跳并在静默时断开的客户端。</param>
        /// <param name="serverClock">合法响应用来校时的时钟。</param>
        /// <param name="clock">间隔和静默读取的单调时钟。</param>
        internal HeartbeatService(NetClient client, ServerClock serverClock, IHeartbeatClock clock)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (serverClock == null)
                throw new ArgumentNullException(nameof(serverClock));
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            _client = client;
            _serverClock = serverClock;
            _clock = clock;
            _ownerContext = SynchronizationContext.Current;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 跑一份心跳循环。已有循环时直接失败，避免两份循环同时断线。
        /// </summary>
        /// <param name="interval">两次发起之间的间隔，必须大于 0。</param>
        /// <param name="silenceTimeout">距离上次合法响应的上限，必须大于 0。</param>
        /// <param name="ct">取消发送、等待和静默监测。</param>
        public async UniTask RunAsync(TimeSpan interval, TimeSpan silenceTimeout, CancellationToken ct)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            if (silenceTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(silenceTimeout));
            EnsureOwner();
            ct.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new InvalidOperationException("心跳循环已在运行");

            int epoch = Interlocked.Increment(ref _epoch);
            Interlocked.Exchange(ref _disconnected, 0);
            Interlocked.Exchange(ref _lastValid, _clock.ElapsedMilliseconds);
            var loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = loop;
            try
            {
                Task sending = SendLoop(interval, loop.Token, epoch).AsTask();
                Task silence = SilenceLoop(silenceTimeout, loop.Token, epoch).AsTask();
                await Task.WhenAny(sending, silence);
                CancelLoop(loop);
                try
                {
                    await Task.WhenAll(sending, silence);
                }
                finally
                {
                    await DrainWatches();
                }
            }
            finally
            {
                CancelLoop(loop);
                if (ReferenceEquals(_loop, loop))
                    _loop = null;
                loop.Dispose();
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>按发起时刻重复发送。响应还没回来也不把耗时加进间隔。</summary>
        /// <param name="interval">两次发起之间的间隔。</param>
        /// <param name="token">循环自己的取消令牌。</param>
        /// <param name="epoch">这一份循环的代号。旧循环不能再发送。</param>
        async UniTask SendLoop(TimeSpan interval, CancellationToken token, int epoch)
        {
            try
            {
                long intervalMs = Milliseconds(interval);
                while (!token.IsCancellationRequested)
                {
                    await SwitchToOwner(token);
                    if (token.IsCancellationRequested)
                        return;

                    long started = _clock.ElapsedMilliseconds;
                    Track(SendOne(token, epoch));
                    long wait = intervalMs - (_clock.ElapsedMilliseconds - started);
                    if (wait <= 0)
                        continue;
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(wait), token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>距离上次合法响应达到上限时只断线一次。</summary>
        /// <param name="silenceTimeout">允许的静默时长。</param>
        /// <param name="token">循环自己的取消令牌。</param>
        /// <param name="epoch">这一份循环的代号。</param>
        async UniTask SilenceLoop(TimeSpan silenceTimeout, CancellationToken token, int epoch)
        {
            try
            {
                long silenceMs = Milliseconds(silenceTimeout);
                while (!token.IsCancellationRequested)
                {
                    await SwitchToOwner(token);
                    if (token.IsCancellationRequested)
                        return;

                    long remain = silenceMs - (_clock.ElapsedMilliseconds - Interlocked.Read(ref _lastValid));
                    if (remain <= 0)
                    {
                        DisconnectOnce(epoch);
                        return;
                    }

                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(remain), token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>发出一次心跳。只有 code 为 0 才校时并刷新静默起点。</summary>
        /// <param name="token">循环自己的取消令牌。</param>
        /// <param name="epoch">这一份循环的代号。</param>
        async UniTask SendOne(CancellationToken token, int epoch)
        {
            HeartbeatResp response;
            try
            {
                var request = new HeartbeatReq
                {
                    ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                response = await _client.SendRequestAsync<HeartbeatResp>(request, RequestTimeout, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                await SwitchToOwner(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || epoch != Volatile.Read(ref _epoch))
                return;
            if (response == null || response.Code != 0)
                return;

            long localUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _serverClock.Synchronize(response.ServerTime, localUnixMs);
            Interlocked.Exchange(ref _lastValid, _clock.ElapsedMilliseconds);
        }

        /// <summary>记下一项还在进行的工作，完成后就从集合移除。</summary>
        /// <param name="work">要观察的心跳工作。</param>
        void Track(UniTask work)
        {
            Task active = Observe(work).AsTask();
            lock (_watches)
                _watches.Add(active);
            _ = active.ContinueWith(
                finished =>
                {
                    lock (_watches)
                        _watches.Remove(finished);
                    _ = finished.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>等待这项工作结束，并吞掉取消异常。</summary>
        /// <param name="work">要观察的工作。</param>
        static async UniTask Observe(UniTask work)
        {
            try
            {
                await work;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>取消并等待当前还在集合里的工作。</summary>
        async UniTask DrainWatches()
        {
            Task[] pending;
            lock (_watches)
            {
                pending = new Task[_watches.Count];
                _watches.CopyTo(pending);
                _watches.Clear();
            }

            await Task.WhenAll(pending);
        }

        /// <summary>这一份循环还有效时只断开一次。</summary>
        /// <param name="epoch">要求仍是当前循环。</param>
        void DisconnectOnce(int epoch)
        {
            if (epoch != Volatile.Read(ref _epoch))
                return;
            if (Interlocked.Exchange(ref _disconnected, 1) != 0)
                return;
            CancelLoop(_loop);
            _client.Disconnect("心跳静默");
        }

        /// <summary>切回创建心跳服务的线程。</summary>
        /// <param name="token">取消这次切换。</param>
        UniTask SwitchToOwner(CancellationToken token)
        {
            if (Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
                return UniTask.CompletedTask;
            if (_ownerContext == null)
                throw new InvalidOperationException("心跳续延离开了创建线程，且没有同步上下文");

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
                throw new InvalidOperationException("只能在创建心跳服务的线程运行");
        }

        /// <summary>取消循环自己的令牌。已经释放时忽略。</summary>
        /// <param name="loop">循环的取消源。</param>
        static void CancelLoop(CancellationTokenSource loop)
        {
            if (loop == null)
                return;
            try
            {
                if (!loop.IsCancellationRequested)
                    loop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>把时间间隔换成整数毫秒。</summary>
        /// <param name="value">要换算的间隔。</param>
        static long Milliseconds(TimeSpan value)
        {
            double ms = value.TotalMilliseconds;
            if (ms >= long.MaxValue)
                return long.MaxValue;
            return Math.Max(1L, (long)Math.Ceiling(ms));
        }
    }

    internal interface IHeartbeatClock
    {
        long ElapsedMilliseconds { get; }

        /// <summary>等待一段时间。</summary>
        /// <param name="delay">要等待的时长。</param>
        /// <param name="cancellationToken">取消这次等待。</param>
        UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    sealed class SystemHeartbeatClock : IHeartbeatClock
    {
        readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

        public long ElapsedMilliseconds
        {
            get
            {
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - _start;
                return (long)(ticks * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        /// <summary>按真实经过时间等待。不受 Time.timeScale 影响。</summary>
        /// <param name="delay">要等待的时长。</param>
        /// <param name="cancellationToken">取消这次等待。</param>
        public async UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero)
                return;
            await Task.Delay(delay, cancellationToken);
        }
    }

}

```

### 问题经过

1. 时机：服务端和 EditMode 第一次编译心跳代码。原因：`HeartbeatResp.ToByteArray` 需要 `Google.Protobuf`，服务端和测试都缺这个 using。Unity 报 CS1061，`scriptCompilationFailed` 为真。修改：两边补上 using。结果：类型可以加载，失败标志恢复为假。
2. 时机：第一次 EditMode，4 个心跳测试失败，既有 NetClient 与 TcpTransport 回归没有失败。原因：取消后的第二次启动沿用了已经取消的令牌，所以没有发出新心跳；合法响应的校时要等发送完成回到创建线程，断言当时还没等到；另一线程的异常在 Unity 里直接抛出，不包一层 `AggregateException`；重启用例在新心跳已经写出之前就读取帧数。修改：第二次启动换一张新令牌；校时断言等到显示时间对齐；按直接抛出的异常类型断言；重启用例多等一拍再读帧数。结果：EditMode 总数 53，通过 53，失败 0。服务端心跳与路由总数 13，通过 13，失败 0。
3. 时机：对照 `Temp/M1-T25-review.txt` 复核。原因有四处：`UniTask.WhenAny` 之后又 await 同一份 UniTask，违反单次消费；活动列表把整个运行期的观察任务都留着；`Stopwatch.GetElapsedTime` 是 .NET 7 接口，Unity 的 .NET Standard 2.1 没有；`ManualHeartbeatClock` 放在生产程序集里；心跳请求超时用了大约 24 天的上限。修改：进入 `WhenAny` 前转成可重复等待的 `Task`，退出时取消并观察自有工作；活动任务完成就从集合移除；计时改用 `GetTimestamp` / `Frequency`；测试时钟移到 `Assets/Tests/EditMode`；请求超时改用 `NetClient.DefaultTimeout`，静默 30 秒仍单独监测。服务端另补 `ReleaseSession`，重复释放安全。结果：包含这些修正的客户端回归仍是 53 通过、0 失败。补上释放用例后，心跳与路由回归 14 通过、0 失败，证据是 `T25-review.trx`。


## T26：实现 ReconnectService.RecoverAsync

- 来源：`06_M1_基础框架任务拆解.md` 第 4.2 / 5.3 节 T26，依赖已完成的 T21
- 需求：R-C03
- 依据：第 4.2 节 `ReconnectService.RecoverAsync`；第 2.4 节断线后按 1/2/4/8/16/30 秒退避再连接；热更回调负责 Token 与快照，本服务只负责连接退避
- 通过条件：记录 1/2/4/8/16/30/30 退避；成功后停止后续重试且只恢复一次；回调 false 停止并关闭失败候选；延迟期间和连接期间取消都结束且不再尝试；同一时刻只有一个恢复流程；成功后连接仍可用；旧失败回调不破坏新会话
- 日期：2026-10-19

### 任务、流程与验证

这一步只做客户端重连退避。`RecoverAsync(ServerEndpoint endpoint, Func<CancellationToken, UniTask<bool>> restoreSessionAsync, CancellationToken ct)` 返回 `UniTask<bool>`。第一次连接立即尝试。可恢复失败之后依次等待 1、2、4、8、16、30 秒，此后每次 30 秒，再进入下一轮。网络重新可用时，下一轮连上并调用恢复回调。回调返回 true 表示快照已经恢复，方法返回 true 并停止。回调返回 false 表示 Token 失效、顶号或版本不兼容，关闭这一次失败候选连接并返回 false，不再重试。只有连接失败、超时这类可恢复失败才继续。调用方取消时以取消结束，并停掉在途连接和延迟等待。编程错误原样抛出，不把所有异常都当成可重试。

同一时刻只跑一个恢复流程。失败尝试清掉 Pending，也不留下旧接收循环。旧尝试晚完成不能盖住或关闭新连接。连接成功后，不因为 `RecoverAsync` 的 finally 取消临时令牌而马上断开刚恢复的连接。业务异步使用 UniTask。`NetClient` 仍只在创建它的线程上使用。不实现断线遮罩，也不实现账号 Token；这些由回调负责。

测试用内部可控的连接和延迟边界推进退避，不把这套边界加进公共接口。

```csharp
UniTask<bool> RecoverAsync(ServerEndpoint endpoint, Func<CancellationToken, UniTask<bool>> restoreSessionAsync, CancellationToken ct)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 退避序列 | 连接持续失败，内部边界记下每次等待 | 1、2、4、8、16、30、30 | 通过。`Backoff_Records_1_2_4_8_16_30_30`。七次失败对应七次等待，取消后没有第八次连接 |
| 成功停止 | 失败若干次后连接成功，回调返回 true | 不再发起后续连接；回调只执行一次 | 通过。`Success_StopsFurtherAttempts_AndRestoresOnce`。等待为 1、2，连接 3 次，回调 1 次 |
| false 停止 | 连接成功但回调返回 false | 返回 false；候选连接关闭；不再重试；不留 Pending 或旧接收循环 | 通过。`FalseCallback_Stops_AndDropsCandidate`。Pending 为 0，没有下一次连接；再次接收被拒绝的原因不是接收循环仍在运行 |
| 延迟期间取消 | 停在某次等待时取消 | 以取消结束；不再连接 | 通过。`CancelDuringDelay_EndsWithoutAnotherConnect`。只打开过 1 次，之后释放延迟也不再连接 |
| 连接期间取消 | 连接尚未返回时取消 | 以取消结束；停掉在途连接；不再尝试 | 通过。`CancelDuringConnect_EndsWithoutAnotherAttempt`。连接 1 次，没有等待，当前通道为空 |
| 单一流程 | 第一份仍在运行时再次启动 | 第二次失败，没有第二份连接 | 通过。`SecondStart_IsRejected`。第二次提示已在运行，打开次数仍是 1 |
| 成功后仍可用 | 回调返回 true 后发送一帧，并稍等 | 连接没有被临时令牌取消关掉，发送仍然成功 | 通过。`Success_KeepsTheConnectionUsable`。当前通道仍是刚连上的那只，关闭次数为 0，发出的帧还在 |
| 旧失败回调 | 取消旧恢复后再建立新会话，随后旧回调返回 false；另测旧连接晚完成 | 新连接不被关闭，也不被旧通道替换 | 通过。`LateFalseCallback_DoesNotCloseTheNewSession` 与 `LateOpen_DoesNotReplaceOrCloseTheNewConnection`。新通道关闭次数为 0；晚返回的通道被单独关闭 |
| 编程错误 | 开口直接抛出编程异常 | 异常交给调用方；不进入下一次退避 | 通过。`ProgrammingError_StopsWithoutRetry`。打开 1 次，等待 0 次 |
| 已有客户端回归 | Unity EditMode 重跑 NetClient 与 TcpTransport，并跑本项重连测试 | 失败数 0 | 通过。合计 56，失败 0，跳过 0。其中重连 11，NetClientLifecycle 11，NetClientPending 6，TcpTransportReceive 12，TcpTransport 16 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改重连服务、必要的网络测试、本日志、拆解表 T26 和本项证据。

#### 1. 实现 ReconnectService

新增 `Assets/Framework/Network/ReconnectService.cs`。正式路径使用现有 `TcpTransport.ConnectAsync` 和 `NetClient`。内部边界只供测试替换延迟和开口。

- [x] 已写入磁盘

#### 2. EditMode 验证

用 `Temp/M1-EditModeRunner-reference.cs` 的 TestRunnerApi 跑 `ReconnectServiceTests`，以及 NetClient、TcpTransport 回归。Console 与结果留在 `Docs/Acceptance/M1/20261019-T26/`。通过之后才勾选本日志和拆解表 T26。

- [x] 已执行并写入证据。证据目录：`Docs/Acceptance/M1/20261019-T26/`

本步范围：客户端重连退避。不含断线遮罩、账号 Token、操作号和服务端重连处理。

### 遇到的问题和解决方案

- 第一次编译测试时，`Delays` 是 `int[]`，三处误用了 `Count`，出现 CS1061，`scriptCompilationFailed` 为真。改成 `Length` 后类型可以加载，失败标志恢复为假。
- 正式 EditMode 总数 56，通过 56，失败 0，跳过 0。名单在 `Docs/Acceptance/M1/20261019-T26/ReconnectEditMode.txt`，命令在 `T26-command.txt`，Console 摘要在 `T26-console.txt`。Unity Console 里只有原有的 AI Assistant 联网错误，没有本项脚本错误。电量 100%，交流电源接通。`Temp/M1Execution.low-battery` 不存在。


### 协调复核：验证收尾清理修正

接收启动已移入失败处理，启动失败时走同一清理；取消旧接收后在 finally 释放取消源。源码的这两处修改晚于原56项报告所使用的程序集编译，因此原报告暂不证明这两处修改。先重新导入编译、复跑相关用例，保留独立 ReconnectReview.txt，再完成本项；不新增业务。

- [x] 当前源码实际编译并验证通过。

重新导入 `Assets/Framework/Network/ReconnectService.cs` 后，编辑器 `isCompiling=false`，`scriptCompilationFailed=false`。用 TestRunnerApi 复跑 `ReconnectServiceTests`、`NetClientLifecycleTests`、`NetClientPendingTests`、`TcpTransportReceiveTests`、`TcpTransportTests`。结果 `status=Passed pass=56 fail=0 skip=0`，56 条叶子用例全部通过，写在 `Docs/Acceptance/M1/20261019-T26/ReconnectReview.txt`，没有覆盖 `ReconnectEditMode.txt`。Console 仍只有原先的 AI Assistant 联网错误，没有这两处收尾修正引入的脚本错误。拆解表 T26 已重新勾选。


### 执行命令与脚本

#### 命令：Docs/Acceptance/M1/20261019-T26/T26-command.txt

```text
Unity EditMode，TestRunnerApi。
程序集：Game.Framework.EditModeTests
分组：
- Game.Framework.EditModeTests.ReconnectServiceTests
- Game.Framework.EditModeTests.NetClientLifecycleTests
- Game.Framework.EditModeTests.NetClientPendingTests
- Game.Framework.EditModeTests.TcpTransportReceiveTests
- Game.Framework.EditModeTests.TcpTransportTests

结果文件：Docs/Acceptance/M1/20261019-T26/ReconnectEditMode.txt
status=Passed pass=56 fail=0 skip=0
其中 ReconnectServiceTests 11，NetClientLifecycleTests 11，NetClientPendingTests 6，TcpTransportReceiveTests 12，TcpTransportTests 16。
```

#### 脚本：Assets/Framework/Network/ReconnectService.cs

```csharp
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
        static TimeSpan NextDelay(ref int step)
        {
            int seconds = step < BackoffSeconds.Length ? BackoffSeconds[step] : 30;
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
```

### 问题经过

1. 时机：测试第一次编译。原因：`Delays` 是 `int[]`，三处误用了 `Count`，出现 CS1061，`scriptCompilationFailed` 为真。修改：改成 `Length`，再让编辑器重新编译。结果：类型可以加载，失败标志恢复为假。正式 EditMode 总数 56，通过 56，失败 0，跳过 0。
2. 时机：56 项报告已经写出之后。原因：`StartReceive` 移入失败捕获、`CancelReceive` 在 finally 释放取消源，这两处修改晚于该报告所使用的程序集编译，原报告不能证明它们。修改：重新导入 `ReconnectService.cs`，等 `isCompiling=false` 且 `scriptCompilationFailed=false`，再复跑重连、生命周期、Pending、接收和发送五组测试，结果写入 `ReconnectReview.txt`，不覆盖 `ReconnectEditMode.txt`。结果：56 条叶子用例全部通过，没有这两处收尾修正引入的脚本错误。


## T27：实现 OpIdStore.Next

- 来源：`06_M1_基础框架任务拆解.md` 第 3.2 / 4.2 / 5.3 节 T27，依赖 T02
- 需求：R-C03
- 依据：第 4.2 节 `OpIdStore.Next(long playerId)` 与 `ReadLast(long playerId)`；第 3.2 节 op_id 按玩家持久化，且在发送前分配保存
- 通过条件：进程重启后新值大于旧值；保存失败不返回 ID
- 日期：2026-10-19

### 任务、流程与验证

这一步只做客户端按玩家分配操作号。同一玩家的分配互斥，不同玩家各自计数。新号必须是正数；已经到达 `long` 最大值时拒绝再分配，不回绕成负数或 0。`ReadLast` 只读取上次成功保存的值，不分配新号。没有记录时读到 0。

`Next` 先把新值写入临时文件并刷盘，再原子替换正式文件。替换成功后才返回这个号。写入或替换失败时正式文件保持原值，调用方拿不到新号。已经存在但读不出或已损坏的记录不能当成 0 再从头发号。两个 `OpIdStore` 指向同一目录时，按规范化路径在本进程内共用锁，并且以磁盘上的成功值为准，不能各自记一份缓存后发出重复号。

M1 不做跨设备发号，也不做对外游戏写接口。幂等业务夹具留在 T39。

```csharp
long Next(long playerId)
long ReadLast(long playerId)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 连续分配与不同玩家 | 同一玩家连续 Next，另一玩家穿插分配，并 ReadLast | 同玩家递增且为正；不同玩家互不影响；ReadLast 不产生新号 | 通过。`Next_IncrementsPerPlayer_AndReadLastDoesNotAllocate` 与 `DifferentPlayers_AllocateIndependently`。玩家 11 得到 1、2，玩家 22 得到 1；未分配玩家读到 0 且不产生文件。两个玩家各 8 个号，均为 1 到 8 且不重复 |
| 重新创建 Store | 分配后新建指向同一目录的 Store，读上次再 Next | 读到上次成功值，新号比它大 | 通过。`RecreatedStore_ReadsLastAndContinues`。新实例读到 2，继续得到 3 |
| 同玩家并发与双实例 | 同一玩家并行 Next；两个实例指向同一目录并行 Next | 没有重复号 | 通过。`SamePlayer_ParallelNext_HasNoDuplicates` 为 1 到 32。`TwoStores_SameDirectory_DoNotRepeat` 中带尾部分隔符的第二个实例一起得到 1 到 40，两边 ReadLast 都是 40 |
| 写入失败 | 正式文件被占用时 Next，然后解除占用再 Next | 失败抛错且未发出新号；恢复后从原值继续 | 通过。`WriteFailure_ThrowsWithoutIssuing_ThenContinues`。占用期间抛出 IOException，ReadLast 仍是 1；解除后得到 2 |
| 损坏文件 | 把已有记录改成无法解析的内容后读取和分配 | 抛错，不从 0 重新发号 | 通过。`CorruptFile_DoesNotResetToZero`。读取和分配都抛出 InvalidDataException，文件内容仍是 broken |
| long 最大值 | 持久值已是 long 最大值时 Next | 拒绝分配，不回绕 | 通过。`LongMax_RefusesAnotherIdWithoutWrap`。先发出 long.MaxValue，再次分配抛出 InvalidOperationException，磁盘值不变 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改 `OpIdStore`、必要的最小持久化边界、本项测试、本日志、拆解表 T27 和本项证据。

#### 1. 实现 OpIdStore

新增 `Assets/Framework/Network/OpIdStore.cs`。每个玩家一个正式文件。临时文件刷盘后再提交。

- [x] 已写入磁盘

#### 2. EditMode 验证

用 `Temp/M1-EditModeRunner-reference.cs` 的 TestRunnerApi 只跑 `OpIdStoreTests`。测试使用自己的临时目录，递归删除前核对绝对路径。结果留在 `Docs/Acceptance/M1/20261019-T27/`。通过之后才勾选本日志和拆解表 T27。

- [x] 已执行并写入证据。证据目录：`Docs/Acceptance/M1/20261019-T27/`

本步范围：客户端按玩家持久化操作号。不含跨设备发号、游戏写接口和幂等执行。

### 遇到的问题和解决方案

- 测试第一次编译失败。`Holder` 放进 `using` 但没有实现 `IDisposable`，出现 CS1674。隔离目录的属性名叫 `Path`，遮蔽了 `System.IO.Path`，出现 CS1061。去掉 `Holder`，属性改成 `FullPath` 后重新导入，`scriptCompilationFailed` 恢复为 false。
- 正式 EditMode 总数 8，通过 8，失败 0，跳过 0。名单在 `Docs/Acceptance/M1/20261019-T27/OpIdStoreEditMode.txt`，命令在 `T27-command.txt`，Console 摘要在 `T27-console.txt`。Unity Console 里只有原有的 AI Assistant 联网错误，没有本项脚本错误。电量 100%，交流电源接通。`Temp/M1Execution.low-battery` 不存在。


### 执行命令与脚本

#### 命令：Docs/Acceptance/M1/20261019-T27/T27-command.txt

```text
Unity TestRunnerApi，EditMode，程序集 Game.Framework.EditModeTests。
过滤组：Game.Framework.EditModeTests.OpIdStoreTests。
跑测前 isCompiling=false，scriptCompilationFailed=false。
结果文件：Docs/Acceptance/M1/20261019-T27/OpIdStoreEditMode.txt。
```

#### 脚本：Assets/Framework/Network/OpIdStore.cs

```csharp
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace Game.Framework.Network
{
    /// <summary>
    /// 按玩家持久化操作号。先把新值刷到临时文件并原子提交，成功后才返回。
    /// 同一进程里指向同一目录的实例共用锁，并以磁盘上的成功值为准。
    /// </summary>
    public sealed class OpIdStore
    {
        static readonly ConcurrentDictionary<string, object> Gates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        readonly string _directory;

        /// <summary>使用这个目录保存各玩家的最后一次成功操作号。</summary>
        /// <param name="directory">持久化目录。空白时抛出异常。</param>
        public OpIdStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("操作号目录为空", nameof(directory));

            _directory = Normalize(directory);
            Directory.CreateDirectory(_directory);
        }

        /// <summary>为玩家分配下一个正数操作号。保存失败时不返回新号。</summary>
        /// <param name="playerId">玩家编号。</param>
        public long Next(long playerId)
        {
            lock (Gate(playerId))
            {
                long last = ReadCore(playerId);
                if (last == long.MaxValue)
                    throw new InvalidOperationException("操作号已到 long 上限，拒绝回绕");

                long next = last + 1;
                Commit(PlayerPath(playerId), next);
                return next;
            }
        }

        /// <summary>读取该玩家上次成功保存的操作号。没有记录时返回 0，不分配新号。</summary>
        /// <param name="playerId">玩家编号。</param>
        public long ReadLast(long playerId)
        {
            lock (Gate(playerId))
                return ReadCore(playerId);
        }

        /// <summary>同一目录、同一玩家共用一把锁。</summary>
        /// <param name="playerId">玩家编号。</param>
        object Gate(long playerId)
        {
            string key = _directory + "\n" + playerId.ToString(CultureInfo.InvariantCulture);
            return Gates.GetOrAdd(key, _ => new object());
        }

        /// <summary>返回该玩家正式操作号文件的路径。</summary>
        /// <param name="playerId">玩家编号。</param>
        string PlayerPath(long playerId)
        {
            string name = playerId.ToString(CultureInfo.InvariantCulture) + ".opid";
            return Path.Combine(_directory, name);
        }

        /// <summary>读取磁盘上的成功值。没有文件返回 0，文件损坏则抛异常。</summary>
        /// <param name="playerId">玩家编号。</param>
        long ReadCore(long playerId)
        {
            string path = PlayerPath(playerId);
            if (!File.Exists(path))
                return 0;

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 64, false))
            {
                text = reader.ReadToEnd();
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
                throw new InvalidDataException("操作号文件无法读取，拒绝从 0 重新分配");

            return value;
        }

        /// <summary>先把新值刷进临时文件，再原子替换正式文件。</summary>
        /// <param name="finalPath">正式文件路径。</param>
        /// <param name="value">要保存的操作号。</param>
        static void Commit(string finalPath, long value)
        {
            string tempPath = finalPath + ".tmp";
            byte[] payload = Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
            using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }

            if (!File.Exists(finalPath))
                File.Move(tempPath, finalPath);
            else
                File.Replace(tempPath, finalPath, null);
        }

        /// <summary>把目录收成绝对路径，并去掉末尾分隔符。</summary>
        /// <param name="directory">调用方传入的目录。</param>
        static string Normalize(string directory)
        {
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && full.Length > root.Length)
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }
    }
}
```

### 问题经过

1. 时机：测试第一次编译。原因有两处：`Holder` 放进 `using` 但没有实现 `IDisposable`，出现 CS1674；隔离目录的属性名叫 `Path`，遮蔽了 `System.IO.Path`，出现 CS1061。修改：去掉 `Holder`，属性改成 `FullPath`，再重新导入。结果：`scriptCompilationFailed` 恢复为 false。正式 EditMode 总数 8，通过 8，失败 0，跳过 0。

