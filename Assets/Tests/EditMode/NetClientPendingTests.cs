using System;
using System.Collections.Generic;
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
    public sealed class NetClientPendingTests
    {
        [Test]
        public async Task OutOfOrderResponses_CompleteTheirOwnRequests()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            NetClient client = new NetClient(transport, packet => { });

            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            UniTask<RegisterResp> register = client.SendRequestAsync<RegisterResp>(
                new RegisterReq { Username = "bob" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(sender, 2);

            uint loginSeq = ReadSeq(sender.Frames[0]);
            uint registerSeq = ReadSeq(sender.Frames[1]);
            Assert.That(loginSeq, Is.Not.EqualTo(0));
            Assert.That(registerSeq, Is.Not.EqualTo(loginSeq));
            Assert.That(ReadMsgId(sender.Frames[0]), Is.EqualTo(MsgRegistry.GetId(typeof(LoginReq))));
            Assert.That(ReadMsgId(sender.Frames[1]), Is.EqualTo(MsgRegistry.GetId(typeof(RegisterReq))));

            Action<Packet> receive = client.CaptureReceive();
            receive(Response(new RegisterResp { Code = 7 }, registerSeq));
            receive(Response(new LoginResp { Token = "login-token" }, loginSeq));
            client.DispatchPending(10);

            LoginResp loginResult = await login;
            RegisterResp registerResult = await register;
            Assert.That(loginResult.Token, Is.EqualTo("login-token"));
            Assert.That(registerResult.Code, Is.EqualTo(7));
            Assert.That(client.PendingCount, Is.EqualTo(0));
            transport.Close();
        }

        [Test]
        public async Task WrongMsgId_LeavesPending_UntilMatchingResponse()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            List<ushort> seen = new List<ushort>();
            NetClient client = new NetClient(transport, packet => seen.Add(packet.MsgId));

            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(sender, 1);
            uint seq = ReadSeq(sender.Frames[0]);
            Action<Packet> receive = client.CaptureReceive();
            receive(Response(new HeartbeatResp(), seq));
            client.DispatchPending(10);

            Assert.That(login.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(client.PendingCount, Is.EqualTo(1));
            Assert.That(seen, Is.EqualTo(new[] { MsgRegistry.GetId(typeof(HeartbeatResp)) }));

            receive(Response(new LoginResp { Token = "later" }, seq));
            client.DispatchPending(10);
            LoginResp result = await login;
            Assert.That(result.Token, Is.EqualTo("later"));
            Assert.That(client.PendingCount, Is.EqualTo(0));
            transport.Close();
        }

        [Test]
        public async Task OldGeneration_DoesNotCompleteTheNewRequest()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            List<string> tokens = new List<string>();
            NetClient client = new NetClient(transport, packet =>
            {
                if (packet.MsgId == MsgRegistry.GetId(typeof(LoginResp)))
                    tokens.Add(LoginResp.Parser.ParseFrom(packet.Body).Token);
            });

            Action<Packet> oldReceive = client.CaptureReceive();
            client.EnqueueReceived(Response(new LoginResp { Token = "queued-old" }, 1));
            client.AdvanceConnection();
            oldReceive(Response(new LoginResp { Token = "late-old" }, 1));

            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(sender, 1);
            Assert.That(ReadSeq(sender.Frames[0]), Is.EqualTo(1));
            client.DispatchPending(10);

            Assert.That(login.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(client.PendingCount, Is.EqualTo(1));
            Assert.That(tokens, Is.Empty);
            Assert.That(client.QueuedCount, Is.EqualTo(0));

            client.EnqueueReceived(Response(new LoginResp { Token = "current" }, 1));
            client.DispatchPending(10);
            LoginResp result = await login;
            Assert.That(result.Token, Is.EqualTo("current"));
            Assert.That(tokens, Is.EqualTo(new[] { "current" }));
            transport.Close();
        }

        [Test]
        public async Task PushSeqZero_DoesNotTouchPending()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            List<ushort> seen = new List<ushort>();
            NetClient client = new NetClient(transport, packet => seen.Add(packet.MsgId));

            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(sender, 1);
            uint seq = ReadSeq(sender.Frames[0]);
            Action<Packet> receive = client.CaptureReceive();
            receive(Response(new KickNtf(), 0));
            client.DispatchPending(10);

            Assert.That(login.Status, Is.EqualTo(UniTaskStatus.Pending));
            Assert.That(client.PendingCount, Is.EqualTo(1));
            Assert.That(seen, Is.EqualTo(new[] { MsgRegistry.GetId(typeof(KickNtf)) }));

            receive(Response(new LoginResp { Token = "after-push" }, seq));
            client.DispatchPending(10);
            LoginResp result = await login;
            Assert.That(result.Token, Is.EqualTo("after-push"));
            transport.Close();
        }

        [Test]
        public async Task DuplicateResponse_CompletesOnce()
        {
            CaptureSend sender = new CaptureSend();
            TcpTransport transport = new TcpTransport();
            transport.Attach(sender);
            int deliveries = 0;
            NetClient client = new NetClient(transport, packet => deliveries++);

            UniTask<LoginResp> login = client.SendRequestAsync<LoginResp>(
                new LoginReq { Username = "ann" }, TimeSpan.FromSeconds(8), CancellationToken.None);
            await WaitForFrames(sender, 1);
            uint seq = ReadSeq(sender.Frames[0]);
            Action<Packet> receive = client.CaptureReceive();
            receive(Response(new LoginResp { Token = "first" }, seq));
            receive(Response(new LoginResp { Token = "second" }, seq));
            client.DispatchPending(10);

            LoginResp result = await login;
            Assert.That(result.Token, Is.EqualTo("first"));
            Assert.That(client.PendingCount, Is.EqualTo(0));
            Assert.That(deliveries, Is.EqualTo(2));
            transport.Close();
        }

        [Test]
        public async Task OversizeRequest_DoesNotLeavePending()
        {
            var sender = new CaptureSend();
            var transport = new TcpTransport();
            transport.Attach(sender);
            var client = new NetClient(transport, packet => { });
            bool rejected = false;
            try
            {
                await client.SendRequestAsync<LoginResp>(
                    new LoginReq { Username = new string('x', FrameCodec.MaxBodyLength + 1) },
                    TimeSpan.FromSeconds(8), CancellationToken.None);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Assert.That(rejected, Is.True);
            Assert.That(client.PendingCount, Is.EqualTo(0));
            Assert.That(sender.Count, Is.EqualTo(0));
            transport.Close();
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

        static Packet Response(IMessage message, uint seq)
        {
            return new Packet(MsgRegistry.GetId(message.GetType()), seq, message.ToByteArray());
        }

        static uint ReadSeq(byte[] frame)
        {
            return ReadPacket(frame).Seq;
        }

        static ushort ReadMsgId(byte[] frame)
        {
            return ReadPacket(frame).MsgId;
        }

        static Packet ReadPacket(byte[] frame)
        {
            DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumed);
            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            Assert.That(consumed, Is.EqualTo(frame.Length));
            return packet;
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

            public void Close()
            {
            }
        }
    }
}
