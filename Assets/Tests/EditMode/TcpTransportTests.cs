using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Game.Framework.Network;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class TcpTransportTests
    {
        [Test]
        public void Endpoint_RejectsEmptyHostAndOutOfRangePort()
        {
            Assert.Throws<ArgumentException>(() => new ServerEndpoint(" ", 80));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerEndpoint("127.0.0.1", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerEndpoint("127.0.0.1", 65536));
        }

        [Test]
        public async Task PartialSend_KeepsEveryByte()
        {
            ChunkSender sender = new ChunkSender { Limit = 1 };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            byte[] first = new byte[] { 1, 2, 3, 4, 5, 6 };
            byte[] second = new byte[] { 7, 8, 9 };

            await Finish(transport.SendAsync(first, CancellationToken.None));
            await Finish(transport.SendAsync(second, CancellationToken.None));

            CollectionAssert.AreEqual(Concat(first, second), sender.Bytes);
            Assert.That(sender.Calls, Is.EqualTo(first.Length + second.Length));
            transport.Close();
        }

        [Test]
        public async Task ConcurrentSend_WritesWholeFramesInOrder()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender
            {
                Limit = 1,
                Hold = hold,
                HoldAfter = 1
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            byte[] first = new byte[] { 0x10, 0x01, 0x00, 0x00, 0xAA, 0xBB };
            byte[] second = new byte[] { 0x20, 0x02, 0x00, 0x00, 0xCC, 0xDD };

            Task firstTask = transport.SendAsync(first, CancellationToken.None);
            await WaitUntil(() => sender.Bytes.Count >= 1);
            Task secondTask = null;
            Task enqueue = Task.Run(() => { secondTask = transport.SendAsync(second, CancellationToken.None); });
            Task enqueueDone = await Task.WhenAny(enqueue, Task.Delay(1000));
            Assert.That(enqueueDone, Is.SameAs(enqueue), "第二帧入队不应等到第一帧发完");
            await enqueue;
            Assert.That(firstTask.IsCompleted, Is.False);
            Assert.That(secondTask.IsCompleted, Is.False);
            hold.TrySetResult(true);

            await Finish(Task.WhenAll(firstTask, secondTask));
            CollectionAssert.AreEqual(Concat(first, second), sender.Bytes);
            transport.Close();
        }

        [Test]
        public async Task Cancel_AfterPartialSend_RetiresConnection()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender
            {
                Limit = 1,
                Hold = hold,
                HoldAfter = 2
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            CancellationTokenSource cts = new CancellationTokenSource();
            byte[] frame = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            Task sending = transport.SendAsync(frame, cts.Token);
            await WaitUntil(() => sender.Bytes.Count >= 2);
            cts.Cancel();

            try
            {
                await Finish(sending);
                Assert.Fail("半帧取消后发送应结束");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.That(sender.Bytes.Count, Is.LessThan(frame.Length));
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 9 }, CancellationToken.None));
            transport.Close();
            transport.Close();
        }

        [Test]
        public async Task Cancel_BeforeAnyByte_KeepsConnection()
        {
            ChunkSender sender = new ChunkSender { Limit = 1 };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            Task canceled = transport.SendAsync(new byte[] { 1, 2, 3 }, cts.Token);
            try
            {
                await Finish(canceled);
                Assert.Fail("已取消的发送应结束");
            }
            catch (OperationCanceledException)
            {
            }

            await Finish(transport.SendAsync(new byte[] { 4, 5 }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 4, 5 }, sender.Bytes);
            transport.Close();
        }

        [Test]
        public async Task Failure_AfterPartialSend_DropsQueuedFrame()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender
            {
                Limit = 1,
                FailAfter = 3,
                Hold = hold,
                HoldAfter = 1
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            byte[] first = new byte[] { 1, 2, 3, 4, 5 };
            byte[] second = new byte[] { 6, 7, 8, 9 };

            Task firstTask = transport.SendAsync(first, CancellationToken.None);
            await WaitUntil(() => sender.Bytes.Count >= 1);
            Task secondTask = transport.SendAsync(second, CancellationToken.None);
            hold.TrySetResult(true);

            try
            {
                await Finish(firstTask);
                Assert.Fail("半帧失败后当前发送应失败");
            }
            catch (IOException)
            {
            }

            try
            {
                await Finish(secondTask);
                Assert.Fail("尚未开始的下一帧也应失败");
            }
            catch (IOException)
            {
            }

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, sender.Bytes);
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
            transport.Close();
        }

        [Test]
        public async Task Close_IsIdempotentAndFinishesPending()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender
            {
                Limit = 1,
                Hold = hold,
                HoldAfter = 1
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            Task first = transport.SendAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
            await WaitUntil(() => sender.Bytes.Count >= 1);
            Task second = transport.SendAsync(new byte[] { 5, 6, 7, 8 }, CancellationToken.None);

            transport.Close();
            transport.Close();

            await Finish(Task.WhenAll(Settle(first), Settle(second)));
            Assert.That(first.IsCompleted, Is.True);
            Assert.That(second.IsCompleted, Is.True);
            Assert.That(second.IsFaulted, Is.True);
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 9 }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 1 }, sender.Bytes);
        }

        [Test]
        public async Task OldConnection_DoesNotWriteIntoReplacement()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender firstSender = new ChunkSender
            {
                Limit = 1,
                Hold = hold,
                HoldAfter = 1
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(firstSender);
            Task oldSend = transport.SendAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
            await WaitUntil(() => firstSender.Bytes.Count >= 1);

            transport.Close();
            ChunkSender secondSender = new ChunkSender { Limit = 3 };
            transport.Attach(secondSender);
            await Finish(transport.SendAsync(new byte[] { 9, 8, 7 }, CancellationToken.None));
            hold.TrySetResult(true);
            await Finish(Settle(oldSend));
            await Task.Delay(50);

            CollectionAssert.AreEqual(new byte[] { 1 }, firstSender.Bytes);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, secondSender.Bytes);
            transport.Close();
        }

        [Test]
        public async Task Connect_SendsToLocalListener_AndCloseBlocksFurtherSend()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                TcpTransport transport = new TcpTransport();
                await transport.ConnectAsync(new ServerEndpoint("127.0.0.1", port), CancellationToken.None);
                byte[] first = new byte[] { 1, 2, 3, 4, 5 };
                byte[] second = new byte[] { 9, 8, 7 };
                await transport.SendAsync(first, CancellationToken.None);
                await transport.SendAsync(second, CancellationToken.None);

                TcpClient client = await accepted;
                byte[] received = await ReadExact(client.GetStream(), first.Length + second.Length);
                CollectionAssert.AreEqual(Concat(first, second), received);

                transport.Close();
                transport.Close();
                Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
                client.Close();
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public async Task Close_DuringConnect_DoesNotRevive()
        {
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TcpTransport transport = new TcpTransport();
            Task connecting = transport.ConnectAttemptAsync(new ServerEndpoint("127.0.0.1", 9), async (endpoint, token) =>
            {
                entered.TrySetResult(true);
                TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => canceled.TrySetResult(true)))
                {
                    Task done = await Task.WhenAny(release.Task, canceled.Task).ConfigureAwait(false);
                    if (!ReferenceEquals(done, release.Task))
                        throw new OperationCanceledException(token);
                }

                token.ThrowIfCancellationRequested();
                return new ChunkSender();
            }, CancellationToken.None);

            await entered.Task;
            transport.Close();
            release.TrySetResult(true);
            try
            {
                await Finish(connecting);
                Assert.Fail("关闭后的连接尝试应取消");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
        }

        [Test]
        public async Task LateConnect_DoesNotReplaceNewConnection()
        {
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender late = new ChunkSender { Limit = 8 };
            ChunkSender current = new ChunkSender { Limit = 8 };
            TcpTransport transport = new TcpTransport();
            Task first = transport.ConnectAttemptAsync(new ServerEndpoint("127.0.0.1", 9), async (endpoint, token) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return late;
            }, CancellationToken.None);

            await entered.Task;
            transport.Close();
            await transport.ConnectAttemptAsync(
                new ServerEndpoint("127.0.0.1", 9),
                (endpoint, token) => Task.FromResult<ITransportSend>(current),
                CancellationToken.None);
            release.TrySetResult(true);
            try
            {
                await Finish(first);
                Assert.Fail("晚返回的旧连接不应成功");
            }
            catch (OperationCanceledException)
            {
            }

            await Finish(transport.SendAsync(new byte[] { 9 }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 9 }, current.Bytes);
            Assert.That(late.Closed, Is.True);
            Assert.That(late.Bytes.Count, Is.EqualTo(0));
            transport.Close();
        }

        [Test]
        public async Task Connect_CanceledCallerCannotAttachLateSocket()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new ChunkSender();
            var transport = new TcpTransport();
            using (var canceled = new CancellationTokenSource())
            {
                Task connecting = transport.ConnectAttemptAsync(
                    new ServerEndpoint("127.0.0.1", 9),
                    async (endpoint, token) =>
                    {
                        await release.Task.ConfigureAwait(false);
                        return late;
                    }, canceled.Token);
                canceled.Cancel();
                release.TrySetResult(true);
                try
                {
                    await Finish(connecting);
                    Assert.Fail("取消后的调用不能接纳晚到的连接");
                }
                catch (OperationCanceledException)
                {
                }
                Assert.That(late.Closed, Is.True);
                Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
            }
            transport.Close();
        }

        [Test]
        public async Task SecondConnect_WhileConnecting_IsRejected()
        {
            TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender { Limit = 8 };
            TcpTransport transport = new TcpTransport();
            Task first = transport.ConnectAttemptAsync(new ServerEndpoint("127.0.0.1", 9), async (endpoint, token) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return sender;
            }, CancellationToken.None);

            await entered.Task;
            Assert.Throws<InvalidOperationException>(() => transport.ConnectAttemptAsync(
                new ServerEndpoint("127.0.0.1", 9),
                (endpoint, token) => Task.FromResult<ITransportSend>(new ChunkSender()),
                CancellationToken.None));
            release.TrySetResult(true);
            await Finish(first);
            await Finish(transport.SendAsync(new byte[] { 4 }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 4 }, sender.Bytes);
            transport.Close();
        }

        [Test]
        public async Task Send_ZeroBytes_RetiresConnection()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender { FixedReturn = 0, Hold = hold };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            Task first = transport.SendAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
            await WaitUntil(() => sender.Calls >= 1);
            Task second = transport.SendAsync(new byte[] { 4, 5 }, CancellationToken.None);
            hold.TrySetResult(true);

            try
            {
                await Finish(first);
                Assert.Fail("返回 0 的发送应失败");
            }
            catch (IOException)
            {
            }

            try
            {
                await Finish(second);
                Assert.Fail("队列中的下一帧也应失败");
            }
            catch (IOException)
            {
            }

            Assert.That(sender.Bytes.Count, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 6 }, CancellationToken.None));
            transport.Close();
        }

        [Test]
        public async Task Send_CountAboveRequest_RetiresConnection()
        {
            ChunkSender sender = new ChunkSender { FixedReturn = 50 };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            try
            {
                await Finish(transport.SendAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None));
                Assert.Fail("超过请求长度的发送应失败");
            }
            catch (IOException)
            {
            }

            Assert.That(sender.Bytes.Count, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
            transport.Close();
        }

        [Test]
        public async Task Cancel_DuringSend_ObjectDisposed_IsCancellation()
        {
            TaskCompletionSource<bool> hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChunkSender sender = new ChunkSender
            {
                Limit = 1,
                Hold = hold,
                HoldAfter = 1,
                ThrowDisposedWhenCanceled = true
            };
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            CancellationTokenSource cts = new CancellationTokenSource();
            Task sending = transport.SendAsync(new byte[] { 1, 2, 3, 4, 5 }, cts.Token);
            await WaitUntil(() => sender.Bytes.Count >= 1);
            cts.Cancel();
            await Finish(Settle(sending));
            Assert.That(sending.IsCanceled, Is.True);
            Assert.That(sending.IsFaulted, Is.False);
            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 9 }, CancellationToken.None));
            transport.Close();
        }

        [Test]
        public async Task Connect_Refused_LeavesTransportClosed()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            TcpTransport transport = new TcpTransport();
            try
            {
                await transport.ConnectAsync(new ServerEndpoint("127.0.0.1", port), CancellationToken.None);
                Assert.Fail("关闭的端口应连接失败");
            }
            catch (SocketException)
            {
            }

            Assert.Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }, CancellationToken.None));
            transport.Close();
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

        static async Task<byte[]> ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int got = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (got < count)
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("读取超时");
                int n = await stream.ReadAsync(buffer, got, count - got);
                if (n == 0)
                    throw new IOException("对端提前关闭");
                got += n;
            }

            return buffer;
        }

        sealed class ChunkSender : ITransportSend
        {
            readonly object gate = new object();
            public int Limit = 1;
            public int FailAfter = -1;
            public int HoldAfter = -1;
            public int FixedReturn = int.MinValue;
            public bool ThrowDisposedWhenCanceled;
            public TaskCompletionSource<bool> Hold;
            public List<byte> Bytes = new List<byte>();
            public int Calls;
            public bool Closed;

            public async Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Closed)
                    throw new ObjectDisposedException(nameof(ChunkSender));

                if (FixedReturn != int.MinValue)
                {
                    Task fixedHold;
                    lock (gate)
                    {
                        Calls++;
                        fixedHold = Hold != null && !Hold.Task.IsCompleted ? Hold.Task : null;
                    }

                    if (fixedHold != null)
                        await fixedHold.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return FixedReturn;
                }
                Task hold;
                int n;
                lock (gate)
                {
                    if (FailAfter >= 0 && Bytes.Count >= FailAfter)
                        throw new IOException("脚本要求在半帧后失败");
                    n = count < Limit ? count : Limit;
                    for (int i = 0; i < n; i++)
                        Bytes.Add(buffer[offset + i]);
                    Calls++;
                    hold = Hold != null && !Hold.Task.IsCompleted && Bytes.Count >= HoldAfter ? Hold.Task : null;
                }

                if (hold != null)
                {
                    TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancellationToken.Register(() => canceled.TrySetResult(true)))
                    {
                        Task done = await Task.WhenAny(hold, canceled.Task).ConfigureAwait(false);
                        if (!ReferenceEquals(done, hold))
                        {
                            if (ThrowDisposedWhenCanceled)
                                throw new ObjectDisposedException(nameof(ChunkSender));
                            throw new OperationCanceledException(cancellationToken);
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Closed)
                    throw new ObjectDisposedException(nameof(ChunkSender));
                return n;
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
