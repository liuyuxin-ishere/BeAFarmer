using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;
using Game.Framework.Network;
using Google.Protobuf;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class NetClientLifecycleTests
    {
        [Test]
        public async Task Timeout_WhileSending_EndsRequest()
        {
            var sender = new BlockedSend();
            var transport = new TcpTransport();
            transport.Attach(sender);
            var client = new NetClient(transport, packet => { });
            Task<LoginResp> request = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "blocked" }, TimeSpan.FromMilliseconds(50), CancellationToken.None).AsTask();
            try
            {
                Task completed = await Task.WhenAny(request, Task.Delay(1500));
                Assert.That(completed, Is.SameAs(request), "超时必须覆盖发送阶段，不能挂在尚未完成的写入上");
                Assert.ThrowsAsync<TimeoutException>(async () => await request);
                Assert.That(client.PendingCount, Is.EqualTo(0));
            }
            finally
            {
                transport.Close();
                try { await request; } catch (Exception) { }
            }
        }

        [Test]
        public async Task Cancel_WhileSending_EndsRequest()
        {
            BlockedSend sender = new BlockedSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });
            CancellationTokenSource cts = new CancellationTokenSource();
            Task<LoginResp> request = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "blocked" }, TimeSpan.FromSeconds(8), cts.Token).AsTask();
            cts.Cancel();
            try
            {
                Task completed = await Task.WhenAny(request, Task.Delay(1500));
                Assert.That(completed, Is.SameAs(request), "取消必须结束仍阻塞的发送");
                try
                {
                    await request;
                    Assert.Fail("取消应结束请求");
                }
                catch (OperationCanceledException)
                {
                }

                Assert.That(client.PendingCount, Is.EqualTo(0));
            }
            finally
            {
                transport.Close();
                try { await request; } catch (Exception) { }
            }
        }

        [Test]
        public async Task ZeroTimeout_WhileSending_EndsWithTimeout()
        {
            BlockedSend sender = new BlockedSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });
            Task<LoginResp> request = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "blocked" }, TimeSpan.Zero, CancellationToken.None).AsTask();
            try
            {
                Task completed = await Task.WhenAny(request, Task.Delay(1500));
                Assert.That(completed, Is.SameAs(request), "零超时必须在资源赋值完成后仍结束请求");
                Assert.ThrowsAsync<TimeoutException>(async () => await request);
                Assert.That(client.PendingCount, Is.EqualTo(0));
            }
            finally
            {
                transport.Close();
                try { await request; } catch (Exception) { }
            }
        }

        [Test]
        public async Task OldCleanup_DoesNotCloseReplacementConnection()
        {
            TcpTransport transport = new TcpTransport();
            HoldOnClose first = new HoldOnClose();
            transport.Attach(first);
            NetClient client = new NetClient(transport, packet => { });
            client.BeginReceive(CancellationToken.None);
            Task<LoginResp> oldLogin = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "old" }, TimeSpan.FromSeconds(8), CancellationToken.None).AsTask();
            first.FinishRead();
            Task entered = await Task.WhenAny(first.Entered, Task.Delay(2000));
            Assert.That(entered, Is.SameAs(first.Entered), "旧连接应进入关闭");

            CaptureSend second = new CaptureSend();
            transport.Attach(second);
            first.Release();
            Task finished = await Task.WhenAny(oldLogin, Task.Delay(2000));
            Assert.That(finished, Is.SameAs(oldLogin), "旧会话应结束旧请求");
            Assert.ThrowsAsync<IOException>(async () => await oldLogin);
            Assert.That(second.WasClosed, Is.False, "旧会话清理不能关闭已经开始的新连接");

            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "new" }, TimeSpan.FromSeconds(5), CancellationToken.None);
            await WaitForFrames(second, 1);
            Assert.That(login.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(client.PendingCount, Is.EqualTo(1));
            client.Disconnect("收尾");
            try
            {
                await login;
            }
            catch (IOException)
            {
            }
        }

        [Test]
        public async Task DefaultTimeout_FailsWithTimeoutAndClearsPending()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                await client.SendRequestAsync<LoginResp>(
                    new LoginReq { Username = "ann" }, NetClient.DefaultTimeout, CancellationToken.None);
                Assert.Fail("超时应结束请求");
            }
            catch (TimeoutException ex)
            {
                Assert.That(ex.Message, Does.Contain("超时"));
            }

            watch.Stop();
            Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(7.5)));
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(12)));
            Assert.That(client.PendingCount, Is.EqualTo(0));
            transport.Close();
        }

        [Test]
        public async Task CancelAfterSend_ClearsPending()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });
            CancellationTokenSource cts = new CancellationTokenSource();
            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), cts.Token);
            await WaitForFrames(sender, 1);
            cts.Cancel();
            try
            {
                await login;
                Assert.Fail("取消应结束请求");
            }
            catch (OperationCanceledException)
            {
            }

            Assert.That(client.PendingCount, Is.EqualTo(0));
            transport.Close();
        }

        [Test]
        public async Task Disconnect_FailsEveryPending_AndRepeatIsSafe()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });
            client.EnqueueReceived(Response(new KickNtf(), 0));
            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            UniTask<RegisterResp> register = client.SendRequestAsync<RegisterResp>(
                new RegisterReq { Username = "bob" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(sender, 2);

            client.Disconnect("测试断线");
            client.Disconnect("测试断线");
            try
            {
                await login;
                Assert.Fail("断线应结束登录");
            }
            catch (IOException ex)
            {
                Assert.That(ex.Message, Is.EqualTo("测试断线"));
            }

            try
            {
                await register;
                Assert.Fail("断线应结束注册");
            }
            catch (IOException)
            {
            }

            Assert.That(client.PendingCount, Is.EqualTo(0));
            Assert.That(client.QueuedCount, Is.EqualTo(0));
        }

        [Test]
        public async Task Success_IsNotTimedOutLater()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });
            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            await WaitForFrames(sender, 1);
            client.CaptureReceive()(Response(new LoginResp { Token = "ok" }, ReadSeq(sender.Frames[0])));
            client.DispatchPending(4);
            LoginResp result = await login;
            await Task.Delay(500);
            Assert.That(result.Token, Is.EqualTo("ok"));
            Assert.That(client.PendingCount, Is.EqualTo(0));
            transport.Close();
        }

        [Test]
        public async Task LatePacket_AfterDisconnect_DoesNotCompleteNewRequest()
        {
            CaptureSend first = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(first);
            NetClient client = new NetClient(transport, packet => { });
            Action<Packet> oldReceive = client.CaptureReceive();
            UniTask<LoginResp> oldLogin = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(first, 1);
            client.Disconnect("换连接");
            try
            {
                await oldLogin;
            }
            catch (IOException)
            {
            }

            CaptureSend second = new CaptureSend();
            transport.Attach(second);
            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(second, 1);
            uint seq = ReadSeq(second.Frames[0]);
            oldReceive(Response(new LoginResp { Token = "stale" }, seq));
            client.DispatchPending(4);
            Assert.That(login.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(client.PendingCount, Is.EqualTo(1));

            client.CaptureReceive()(Response(new LoginResp { Token = "fresh" }, seq));
            client.DispatchPending(4);
            LoginResp result = await login;
            Assert.That(result.Token, Is.EqualTo("fresh"));
            transport.Close();
        }

        [Test]
        public async Task OldTimeout_DoesNotFailTheReplacementRequest()
        {
            CaptureSend first = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(first);
            NetClient client = new NetClient(transport, packet => { });
            UniTask<LoginResp> oldLogin = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromMilliseconds(150), CancellationToken.None);
            await WaitForFrames(first, 1);
            client.Disconnect("换连接");
            try
            {
                await oldLogin;
            }
            catch (IOException)
            {
            }

            CaptureSend second = new CaptureSend();
            transport.Attach(second);
            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "bee" }, TimeSpan.FromSeconds(5), CancellationToken.None);
            await WaitForFrames(second, 1);
            await Task.Delay(400);
            Assert.That(login.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(client.PendingCount, Is.EqualTo(1));
            client.Disconnect("收尾");
            try
            {
                await login;
            }
            catch (IOException)
            {
            }
        }

        [Test]
        public async Task RemoteClose_EndsThePendingRequest()
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
                NetClient client = new NetClient(transport, packet => { });
                client.BeginReceive(CancellationToken.None);
                UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                    new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
                byte[] seen = new byte[8];
                await server.GetStream().ReadAsync(seen, 0, seen.Length);
                server.Close();
                try
                {
                    await login;
                    Assert.Fail("对端关闭应结束请求");
                }
                catch (IOException)
                {
                }

                Assert.That(client.PendingCount, Is.EqualTo(0));
                client.Disconnect("重复");
                Assert.That(client.PendingCount, Is.EqualTo(0));
            }
            finally
            {
                listener.Stop();
            }
        }

        static Packet Response(IMessage message, uint seq)
        {
            return new Packet(MsgRegistry.GetId(message.GetType()), seq, message.ToByteArray());
        }

        static uint ReadSeq(byte[] frame)
        {
            DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumed);
            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            Assert.That(consumed, Is.EqualTo(frame.Length));
            return packet.Seq;
        }

        static async Task WaitForFrames(CaptureSend sender, int count)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(2);
            while (sender.Count < count)
            {
                if (DateTime.UtcNow > until)
                    Assert.Fail("发送没有写出帧");
                await Task.Delay(1);
            }
        }

        sealed class BlockedSend : ITransportSend
        {
            readonly TaskCompletionSource<int> blocked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => blocked.TrySetCanceled(cancellationToken));
                return blocked.Task;
            }

            public void Close()
            {
                blocked.TrySetException(new ObjectDisposedException(nameof(BlockedSend)));
            }
        }

        sealed class CaptureSend : ITransportSend
        {
            readonly List<byte[]> frames = new List<byte[]>();

            public int Count
            {
                get
                {
                    lock (frames)
                        return frames.Count;
                }
            }

            public byte[][] Frames
            {
                get
                {
                    lock (frames)
                        return frames.ToArray();
                }
            }

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                lock (frames)
                    frames.Add(copy);
                return Task.FromResult(count);
            }

            public bool WasClosed { get; private set; }

            public void Close()
            {
                WasClosed = true;
            }
        }

        sealed class HoldOnClose : ITransportSend, ITransportReceive
        {
            readonly TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource<int> read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered
            {
                get { return entered.Task; }
            }

            public void FinishRead()
            {
                read.TrySetResult(0);
            }

            public void Release()
            {
                release.TrySetResult(true);
            }

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(count);
            }

            public Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return read.Task;
            }

            public void Close()
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
        }
    }
}
