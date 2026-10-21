using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Server.Network;

namespace CloudIsle.Tests
{
    public sealed class TcpServerSessionTests
    {
        [Test]
        public async Task TwoClients_OnLoopback_RoundTripWithDistinctSessionIds()
        {
            var sessions = new Dictionary<uint, ClientSession>();
            await using var gate = new ServerGate(async (session, packet, ct) =>
            {
                lock (sessions)
                    sessions[packet.Seq] = session;
                await session.SendAsync(new Packet(packet.MsgId, packet.Seq, packet.Body.ToArray()), ct);
            });

            await using var clientA = await ConnectAsync(gate.Port);
            await using var clientB = await ConnectAsync(gate.Port);

            Packet replyA = await RoundTripAsync(clientA.Client, new Packet(11, 10, new byte[] { 1, 2 }));
            Packet replyB = await RoundTripAsync(clientB.Client, new Packet(22, 20, new byte[] { 3, 4, 5 }));

            Assert.That(replyA.MsgId, Is.EqualTo((ushort)11));
            Assert.That(replyA.Seq, Is.EqualTo(10u));
            Assert.That(replyA.Body, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(replyB.MsgId, Is.EqualTo((ushort)22));
            Assert.That(replyB.Seq, Is.EqualTo(20u));
            Assert.That(replyB.Body, Is.EqualTo(new byte[] { 3, 4, 5 }));
            Assert.That(sessions[10].SessionId, Is.Not.EqualTo(sessions[20].SessionId));
        }

        [Test]
        public async Task CloseOneSession_OtherClientKeepsRoundTrip()
        {
            var sessions = new Dictionary<uint, ClientSession>();
            await using var gate = new ServerGate(async (session, packet, ct) =>
            {
                lock (sessions)
                    sessions[packet.Seq] = session;
                await session.SendAsync(new Packet(packet.MsgId, packet.Seq, packet.Body.ToArray()), ct);
            });

            await using var clientA = await ConnectAsync(gate.Port);
            await using var clientB = await ConnectAsync(gate.Port);
            await RoundTripAsync(clientA.Client, new Packet(1, 1, new byte[] { 9 }));
            await RoundTripAsync(clientB.Client, new Packet(2, 2, new byte[] { 8 }));

            await WithTimeout(sessions[1].CloseAsync("关闭A", CancellationToken.None), "关闭 A");
            await AssertPeerEndedAsync(clientA.Client.GetStream());

            Packet reply = await RoundTripAsync(clientB.Client, new Packet(2, 3, new byte[] { 7, 7 }));
            Assert.That(reply.Seq, Is.EqualTo(3u));
            Assert.That(reply.Body, Is.EqualTo(new byte[] { 7, 7 }));
            Assert.That(sessions[2].Completion.IsCompleted, Is.False);
        }

        [Test]
        public async Task PeerEof_DoesNotStopOtherClient()
        {
            var sessions = new Dictionary<uint, ClientSession>();
            await using var gate = new ServerGate(async (session, packet, ct) =>
            {
                lock (sessions)
                    sessions[packet.Seq] = session;
                await session.SendAsync(new Packet(packet.MsgId, packet.Seq, packet.Body.ToArray()), ct);
            });

            var clientA = await ConnectAsync(gate.Port);
            await using var clientB = await ConnectAsync(gate.Port);
            await RoundTripAsync(clientA.Client, new Packet(1, 1, new byte[] { 1 }));
            await RoundTripAsync(clientB.Client, new Packet(2, 2, new byte[] { 2 }));

            clientA.Client.Dispose();
            await WithTimeout(sessions[1].Completion, "会话 A 在对端 EOF 后结束");

            Packet reply = await RoundTripAsync(clientB.Client, new Packet(2, 4, new byte[] { 6 }));
            Assert.That(reply.Body, Is.EqualTo(new byte[] { 6 }));
            Assert.That(gate.Run.IsCompleted, Is.False);
        }

        [Test]
        public async Task HandlerException_DoesNotStopOtherClient()
        {
            await using var gate = new ServerGate((session, packet, ct) =>
            {
                if (packet.Seq == 1)
                    throw new InvalidOperationException("这个连接的业务失败");
                return session.SendAsync(new Packet(packet.MsgId, packet.Seq, packet.Body.ToArray()), ct);
            });

            await using var clientA = await ConnectAsync(gate.Port);
            await using var clientB = await ConnectAsync(gate.Port);
            await clientA.Client.GetStream().WriteAsync(FrameCodec.Encode(1, 1, new byte[] { 1 }));
            await AssertPeerEndedAsync(clientA.Client.GetStream());

            Packet reply = await RoundTripAsync(clientB.Client, new Packet(2, 2, new byte[] { 4 }));
            Assert.That(reply.Seq, Is.EqualTo(2u));
            Assert.That(reply.Body, Is.EqualTo(new byte[] { 4 }));
            Assert.That(gate.Run.IsCompleted, Is.False);
        }

        [Test]
        public async Task ConcurrentSends_AreWholeFrames_AndOwnBytes()
        {
            var transport = new ChunkTransport();
            using var stream = new IdleStream();
            ClientSession session = ClientSession.CreateForTransport(7, stream, transport, 8, null);
            Task run = session.RunAsync(CancellationToken.None);

            var bodyA = new byte[] { 1, 2, 3, 4, 5 };
            var bodyB = new byte[] { 9, 8, 7 };
            Task first = session.SendAsync(new Packet(11, 1, bodyA), CancellationToken.None);
            bodyA[0] = 0;
            Task second = session.SendAsync(new Packet(22, 2, bodyB), CancellationToken.None);
            bodyB[0] = 0;

            await WithTimeout(Task.WhenAll(first, second), "并发发送");
            await WithTimeout(session.CloseAsync("发送完成", CancellationToken.None), "关闭测试会话");
            await WithTimeout(run, "会话结束");

            var frames = new List<Packet>();
            var buffer = transport.Bytes.ToArray();
            while (buffer.Length > 0)
            {
                DecodeStatus status = FrameCodec.TryDecode(buffer, out Packet packet, out int consumed);
                Assert.That(status, Is.EqualTo(DecodeStatus.Success));
                Assert.That(consumed, Is.GreaterThan(0));
                frames.Add(packet);
                buffer = buffer.AsSpan(consumed).ToArray();
            }

            Assert.That(frames, Has.Count.EqualTo(2));
            Assert.That(frames[0].MsgId, Is.EqualTo((ushort)11));
            Assert.That(frames[0].Seq, Is.EqualTo(1u));
            Assert.That(frames[0].Body, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
            Assert.That(frames[1].MsgId, Is.EqualTo((ushort)22));
            Assert.That(frames[1].Seq, Is.EqualTo(2u));
            Assert.That(frames[1].Body, Is.EqualTo(new byte[] { 9, 8, 7 }));
        }

        [Test]
        public async Task SlowWrite_OverQueueCapacity_FinishesCurrentAndQueuedSends()
        {
            const int capacity = 2;
            var transport = new HoldingTransport();
            using var stream = new IdleStream();
            ClientSession session = ClientSession.CreateForTransport(3, stream, transport, capacity, null);
            Assert.That(session.SendQueueCapacity, Is.EqualTo(capacity));
            Task run = session.RunAsync(CancellationToken.None);

            Task current = session.SendAsync(new Packet(1, 1, new byte[] { 1 }), CancellationToken.None);
            await WithTimeout(transport.Entered, "慢写进入写出");

            var queued = new List<Task>();
            for (int i = 0; i < capacity; i++)
                queued.Add(session.SendAsync(new Packet(1, (uint)(i + 2), new byte[] { (byte)i }), CancellationToken.None));

            Assert.That(current.IsCompleted, Is.False);
            Assert.That(queued.All(task => !task.IsCompleted), Is.True);

            IOException full = Assert.ThrowsAsync<IOException>(async () =>
                await session.SendAsync(new Packet(1, 99, new byte[] { 9 }), CancellationToken.None));
            Assert.That(full!.Message, Does.Contain("已满"));

            await WithTimeout(Task.WhenAll(Finish(current), Finish(queued[0]), Finish(queued[1]), run), "超限后发送和会话结束");
            Assert.That(current.IsCompleted, Is.True);
            Assert.That(queued.All(task => task.IsCompleted), Is.True);
        }

        [Test]
        public async Task CancelServer_ReleasesConnectionsAndPort()
        {
            await using var gate = new ServerGate(async (session, packet, ct) =>
            {
                await session.SendAsync(new Packet(packet.MsgId, packet.Seq, packet.Body.ToArray()), ct);
            });

            await using var clientA = await ConnectAsync(gate.Port);
            await using var clientB = await ConnectAsync(gate.Port);
            await RoundTripAsync(clientA.Client, new Packet(1, 1, new byte[] { 1 }));

            int port = gate.Port;
            gate.Cancel();
            await WithTimeout(gate.Run, "服务器取消");
            await AssertPeerEndedAsync(clientA.Client.GetStream());
            await AssertPeerEndedAsync(clientB.Client.GetStream());

            var rebound = new TcpListener(IPAddress.Loopback, port);
            rebound.Start();
            rebound.Stop();
        }

        [Test]
        public async Task CloseAsync_Repeated_DoesNotHang()
        {
            var transport = new ChunkTransport();
            using var stream = new IdleStream();
            ClientSession session = ClientSession.CreateForTransport(4, stream, transport, 4, null);
            Task run = session.RunAsync(CancellationToken.None);
            Task first = session.CloseAsync("一次", CancellationToken.None);
            Task second = session.CloseAsync("两次", CancellationToken.None);
            await WithTimeout(Task.WhenAll(first, second, run), "重叠关闭");
            await WithTimeout(session.CloseAsync("三次", CancellationToken.None), "再次关闭");
        }

        [Test]
        public async Task CloseFromReceiveCallback_DoesNotDeadlock()
        {
            ClientSession? session = null;
            await using var gate = new ServerGate(async (current, packet, ct) =>
            {
                session = current;
                await current.CloseAsync("回调里关闭", ct);
            });

            await using var client = await ConnectAsync(gate.Port);
            await client.Client.GetStream().WriteAsync(FrameCodec.Encode(5, 5, new byte[] { 5 }));
            await AssertPeerEndedAsync(client.Client.GetStream());
            Assert.That(session, Is.Not.Null);
            await WithTimeout(session!.Completion, "回调关闭后会话结束");
            Assert.That(gate.Run.IsCompleted, Is.False);
        }

        static async Task<OwnedClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new OwnedClient(client);
        }

        static async Task<Packet> RoundTripAsync(TcpClient client, Packet packet)
        {
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(FrameCodec.Encode(packet.MsgId, packet.Seq, packet.Body));
            return await ReadFrameAsync(stream);
        }

        static async Task<Packet> ReadFrameAsync(NetworkStream stream)
        {
            byte[] header = await ReadExactAsync(stream, 4);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            Assert.That(payloadLength, Is.InRange((uint)FrameCodec.HeaderSize, (uint)FrameCodec.MaxPayloadLength));
            byte[] rest = await ReadExactAsync(stream, (int)payloadLength);
            byte[] frame = new byte[header.Length + rest.Length];
            header.CopyTo(frame, 0);
            rest.CopyTo(frame, header.Length);
            DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumed);
            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            Assert.That(consumed, Is.EqualTo(frame.Length));
            return packet;
        }

        static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(got, count - got));
                if (read == 0)
                    throw new EndOfStreamException("对端提前结束");
                got += read;
            }

            return buffer;
        }

        static async Task AssertPeerEndedAsync(NetworkStream stream)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                int read = await stream.ReadAsync(new byte[8], timeout.Token);
                Assert.That(read, Is.EqualTo(0));
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                Assert.That(timeout.IsCancellationRequested, Is.False, "等待对端结束超时");
            }
        }

        static async Task WithTimeout(Task task, string name)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.That(finished, Is.SameAs(task), name + " 超时");
            await task;
        }

        static Task Finish(Task task)
        {
            return task.ContinueWith(static finished =>
            {
                _ = finished.Exception;
            }, TaskScheduler.Default);
        }

        sealed class ServerGate : IAsyncDisposable
        {
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly TcpServer _server;

            public ServerGate(Func<ClientSession, Packet, CancellationToken, Task> onPacket)
            {
                _server = new TcpServer(onPacket, 8);
                Run = _server.RunAsync(new IPEndPoint(IPAddress.Loopback, 0), _cts.Token);
            }

            public Task Run { get; }

            public int Port
            {
                get
                {
                    _server.Started.GetAwaiter().GetResult();
                    return _server.LocalEndpoint!.Port;
                }
            }

            public void Cancel() => _cts.Cancel();

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                await WithTimeout(Run, "停止服务器");
                _cts.Dispose();
            }
        }

        sealed class OwnedClient : IAsyncDisposable
        {
            public OwnedClient(TcpClient client) => Client = client;

            public TcpClient Client { get; }

            public ValueTask DisposeAsync()
            {
                Client.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        sealed class IdleStream : Stream
        {
            readonly TaskCompletionSource _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                using var _ = cancellationToken.Register(() => _done.TrySetResult());
                await _done.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return 0;
            }

            protected override void Dispose(bool disposing)
            {
                _done.TrySetResult();
                base.Dispose(disposing);
            }
        }

        sealed class ChunkTransport : IFrameTransport
        {
            public List<byte> Bytes { get; } = new List<byte>();

            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (Bytes)
                    Bytes.Add(buffer.Span[0]);
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return 1;
            }
        }

        sealed class HoldingTransport : IFrameTransport
        {
            readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int _seen;

            public Task Entered => _entered.Task;

            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref _seen, 1) == 0)
                    _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return 1;
            }
        }
    }
}
