using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using Game.Framework.Network;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class TcpTransportReceiveTests
    {
        [Test]
        public async Task Split_EveryChunkSize_DecodesSamePacket()
        {
            byte[] body = new byte[] { 1, 2, 3, 4 };
            byte[] frame = FrameCodec.Encode(7, 9, body);
            for (int chunk = 1; chunk <= frame.Length; chunk++)
            {
                ScriptReader reader = new ScriptReader { Data = frame, Chunk = chunk };
                TcpTransport transport = new TcpTransport();
                transport.Attach(reader);
                List<Packet> packets = new List<Packet>();
                await Finish(transport.ReceiveLoopAsync(packet => packets.Add(packet), CancellationToken.None));
                Assert.That(packets.Count, Is.EqualTo(1), "chunk=" + chunk);
                Assert.That(packets[0].MsgId, Is.EqualTo(7));
                Assert.That(packets[0].Seq, Is.EqualTo(9));
                CollectionAssert.AreEqual(body, packets[0].Body);
                transport.Close();
            }
        }

        [Test]
        public async Task Coalesce_AndBoundarySplit_KeepOrder()
        {
            byte[] first = FrameCodec.Encode(1, 10, new byte[] { 0xAA });
            byte[] second = FrameCodec.Encode(2, 11, new byte[] { 0xBB, 0xCC });
            byte[] both = Concat(first, second);

            List<Packet> together = await ReadAll(both, both.Length);
            Assert.That(together.Count, Is.EqualTo(2));
            Assert.That(together[0].MsgId, Is.EqualTo(1));
            Assert.That(together[1].MsgId, Is.EqualTo(2));
            CollectionAssert.AreEqual(new byte[] { 0xBB, 0xCC }, together[1].Body);

            List<Packet> split = await ReadAll(both, 6);
            Assert.That(split.Count, Is.EqualTo(2));
            Assert.That(split[0].Seq, Is.EqualTo(10));
            Assert.That(split[1].Seq, Is.EqualTo(11));
        }

        [Test]
        public async Task EmptyBody_IsOwnedEmptyArray()
        {
            byte[] frame = FrameCodec.Encode(4, 1, Array.Empty<byte>());
            List<Packet> packets = await ReadAll(frame, 3);
            Assert.That(packets.Count, Is.EqualTo(1));
            Assert.That(packets[0].Body, Is.Not.Null);
            Assert.That(packets[0].Body.Length, Is.EqualTo(0));
        }

        [Test]
        public async Task InvalidLength_RetiresConnection()
        {
            ScriptReader reader = new ScriptReader { Data = new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 }, Chunk = 2 };
            TcpTransport transport = new TcpTransport();
            transport.Attach(reader);
            try
            {
                await Finish(transport.ReceiveLoopAsync(packet => { }, CancellationToken.None));
                Assert.Fail("非法长度应结束接收");
            }
            catch (IOException)
            {
            }

            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
            transport.Close();
        }

        [Test]
        public async Task Eof_CompletesWithoutPackets()
        {
            ScriptReader reader = new ScriptReader { Data = Array.Empty<byte>(), Chunk = 1 };
            TcpTransport transport = new TcpTransport();
            transport.Attach(reader);
            int count = 0;
            await Finish(transport.ReceiveLoopAsync(packet => count++, CancellationToken.None));
            Assert.That(count, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
            transport.Close();
        }

        [Test]
        public async Task CancelAndClose_StopTheLoop()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptReader reader = new ScriptReader
            {
                Data = FrameCodec.Encode(1, 1, new byte[] { 1 }),
                Chunk = 1,
                Hold = hold
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(reader);
            CancellationTokenSource cts = new CancellationTokenSource();
            Task reading = transport.ReceiveLoopAsync(packet => { }, cts.Token);
            await WaitUntil(() => reader.Calls >= 1);
            cts.Cancel();
            try
            {
                await Finish(reading);
                Assert.Fail("取消应结束接收");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));

            TaskCompletionSource<bool> holdClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptReader second = new ScriptReader
            {
                Data = FrameCodec.Encode(2, 2, new byte[] { 2 }),
                Chunk = 1,
                Hold = holdClose
            };
            TcpTransport closed = new TcpTransport();
            closed.Attach(second);
            Task untilClose = closed.ReceiveLoopAsync(packet => { }, CancellationToken.None);
            await WaitUntil(() => second.Calls >= 1);
            closed.Close();
            await Finish(untilClose);
            closed.Close();
        }

        [Test]
        public async Task SecondReceive_WhileRunning_IsRejected()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptReader reader = new ScriptReader
            {
                Data = FrameCodec.Encode(1, 1, new byte[] { 1 }),
                Chunk = 1,
                Hold = hold
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(reader);
            Task first = transport.ReceiveLoopAsync(packet => { }, CancellationToken.None);
            await WaitUntil(() => reader.Calls >= 1);
            Assert.Throws<InvalidOperationException>(() => transport.ReceiveLoopAsync(packet => { }, CancellationToken.None));
            transport.Close();
            await Finish(Settle(first));
        }

        [Test]
        public async Task OldConnection_DoesNotDeliverIntoReplacement()
        {
            byte[] oldFrame = FrameCodec.Encode(1, 1, new byte[] { 0x11 });
            byte[] newFrame = FrameCodec.Encode(2, 2, new byte[] { 0x22 });
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptReader oldReader = new ScriptReader { Data = oldFrame, Chunk = oldFrame.Length, Hold = hold };
            TcpTransport transport = new TcpTransport();
            transport.Attach(oldReader);
            List<Packet> packets = new List<Packet>();
            Task oldLoop = transport.ReceiveLoopAsync(packet => packets.Add(packet), CancellationToken.None);
            await WaitUntil(() => oldReader.Calls >= 1);

            transport.Close();
            ScriptReader newReader = new ScriptReader { Data = newFrame, Chunk = newFrame.Length };
            transport.Attach(newReader);
            Task newLoop = transport.ReceiveLoopAsync(packet =>
            {
                packets.Add(packet);
                // Keep the replacement connection open so this test isolates old-loop cleanup.
                newReader.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }, CancellationToken.None);
            hold.TrySetResult(true);
            await Finish(Settle(oldLoop));
            await WaitUntil(() => newReader.Calls >= 2);

            Assert.That(packets.Count, Is.EqualTo(1));
            Assert.That(packets[0].MsgId, Is.EqualTo(2));
            CollectionAssert.AreEqual(new byte[] { 0x22 }, packets[0].Body);
            Assert.That(newReader.Closed, Is.False);
            transport.Close();
            await Finish(Settle(newLoop));
        }

        [Test]
        public async Task Dispatch_UpdatesModelOnlyOnOwnerThreadInOrder()
        {
            byte[] first = FrameCodec.Encode(8, 1, new byte[] { 1 });
            byte[] second = FrameCodec.Encode(9, 2, new byte[] { 2 });
            List<ushort> model = new List<ushort>();
            NetClient client = new NetClient(packet => model.Add(packet.MsgId));
            List<Packet> received = await ReadAll(Concat(first, second), 5, client.EnqueueReceived);
            Assert.That(received, Is.Null);
            Assert.That(model.Count, Is.EqualTo(0));
            Assert.That(client.QueuedCount, Is.EqualTo(2));

            Exception foreign = null;
            Task other = Task.Run(() =>
            {
                try
                {
                    client.DispatchPending(1);
                }
                catch (Exception ex)
                {
                    foreign = ex;
                }
            });
            await Finish(other);
            Assert.That(foreign, Is.InstanceOf<InvalidOperationException>());
            Assert.That(model.Count, Is.EqualTo(0));

            client.DispatchPending(1);
            CollectionAssert.AreEqual(new ushort[] { 8 }, model);
            client.DispatchPending(10);
            CollectionAssert.AreEqual(new ushort[] { 8, 9 }, model);
            Assert.That(client.QueuedCount, Is.EqualTo(0));
        }

        [Test]
        public void Dispatch_CallbackFailureKeepsUnconsumedPackets()
        {
            var delivered = new List<ushort>();
            var client = new NetClient(packet =>
            {
                if (packet.MsgId == 11)
                    throw new InvalidOperationException("consumer failed");
                delivered.Add(packet.MsgId);
            });
            client.EnqueueReceived(new Packet(11, 1, Array.Empty<byte>()));
            client.EnqueueReceived(new Packet(12, 2, Array.Empty<byte>()));
            Assert.Throws<InvalidOperationException>(() => client.DispatchPending(2));
            Assert.That(client.QueuedCount, Is.EqualTo(1));
            client.DispatchPending(2);
            CollectionAssert.AreEqual(new ushort[] { 12 }, delivered);
        }

        [Test]
        public async Task LocalTcp_DispatchesEmptyBodyThenPayload()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                TcpTransport transport = new TcpTransport();
                await transport.ConnectAsync(new ServerEndpoint("127.0.0.1", port), CancellationToken.None);
                TcpClient server = await accepted;
                byte[] emptyFrame = FrameCodec.Encode(3, 1, Array.Empty<byte>());
                byte[] payload = FrameCodec.Encode(4, 2, new byte[] { 7, 8 });
                NetworkStream stream = server.GetStream();
                await stream.WriteAsync(emptyFrame, 0, 4);
                await stream.WriteAsync(emptyFrame, 4, emptyFrame.Length - 4);
                await stream.WriteAsync(payload, 0, payload.Length);

                List<ushort> model = new List<ushort>();
                List<int> bodies = new List<int>();
                NetClient client = new NetClient(packet =>
                {
                    model.Add(packet.MsgId);
                    bodies.Add(packet.Body.Length);
                });
                Task reading = transport.ReceiveLoopAsync(client.EnqueueReceived, CancellationToken.None);
                await WaitUntil(() => client.QueuedCount >= 2);
                Assert.That(model.Count, Is.EqualTo(0));
                client.DispatchPending(10);
                CollectionAssert.AreEqual(new ushort[] { 3, 4 }, model);
                CollectionAssert.AreEqual(new int[] { 0, 2 }, bodies);

                server.Close();
                await Finish(reading);
                transport.Close();
            }
            finally
            {
                listener.Stop();
            }
        }

        static async Task<List<Packet>> ReadAll(byte[] data, int chunk)
        {
            return await ReadAll(data, chunk, null);
        }

        static async Task<List<Packet>> ReadAll(byte[] data, int chunk, Action<Packet> onPacket)
        {
            ScriptReader reader = new ScriptReader { Data = data, Chunk = chunk };
            TcpTransport transport = new TcpTransport();
            transport.Attach(reader);
            List<Packet> packets = onPacket == null ? new List<Packet>() : null;
            Action<Packet> callback = onPacket ?? (packet => packets.Add(packet));
            await Finish(transport.ReceiveLoopAsync(callback, CancellationToken.None));
            transport.Close();
            return packets;
        }

        static byte[] Concat(byte[] first, byte[] second)
        {
            byte[] all = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, all, 0, first.Length);
            Buffer.BlockCopy(second, 0, all, first.Length, second.Length);
            return all;
        }

        static async Task WaitUntil(Func<bool> ready)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (!ready())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("等待条件超时");
                await Task.Delay(10);
            }
        }

        static async Task Finish(Task task)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(3000));
            if (!ReferenceEquals(finished, task))
                Assert.Fail("操作超时");
            await task;
        }

        static async Task Settle(Task task)
        {
            try
            {
                await Finish(task);
            }
            catch (Exception)
            {
            }
        }

        sealed class ScriptReader : ITransportSend, ITransportReceive
        {
            readonly object gate = new object();
            public byte[] Data = Array.Empty<byte>();
            public int Chunk = 1;
            public int Cursor;
            public int Calls;
            public bool Closed;
            public TaskCompletionSource<bool> Hold;

            public async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Task hold;
                lock (gate)
                {
                    Calls++;
                    hold = Hold != null && !Hold.Task.IsCompleted ? Hold.Task : null;
                }

                if (hold != null)
                {
                    TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancellationToken.Register(() => canceled.TrySetResult(true)))
                    {
                        Task done = await Task.WhenAny(hold, canceled.Task).ConfigureAwait(false);
                        if (!ReferenceEquals(done, hold))
                            throw new OperationCanceledException(cancellationToken);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (Cursor >= Data.Length)
                        return 0;
                    int n = Chunk < count ? Chunk : count;
                    int left = Data.Length - Cursor;
                    if (n > left)
                        n = left;
                    Buffer.BlockCopy(Data, Cursor, buffer, offset, n);
                    Cursor += n;
                    return n;
                }
            }

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(count);
            }

            public void Close()
            {
                Closed = true;
                if (Hold != null)
                    Hold.TrySetResult(true);
            }
        }
    }
}
