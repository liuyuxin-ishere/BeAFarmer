using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class HeartbeatServiceTests
    {
        [Test]
        public async Task Interval_FollowsSendStart_WhileRequestStaysPending()
        {
            using (var fixture = Start())
            {
                await fixture.WaitFrames(1);
                Assert.That(fixture.Sender.Count, Is.EqualTo(1));
                Assert.That(fixture.Client.PendingCount, Is.EqualTo(1));

                fixture.Clock.Advance(TimeSpan.FromSeconds(10));
                await fixture.WaitFrames(2);
                fixture.Clock.Advance(TimeSpan.FromSeconds(10));
                await fixture.WaitFrames(3);

                Assert.That(fixture.Client.PendingCount, Is.EqualTo(3));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(0));
                Assert.That(ReadMsgId(fixture.Sender.Frames[0]), Is.EqualTo(MsgRegistry.GetId(typeof(HeartbeatReq))));
                Assert.That(ReadSeq(fixture.Sender.Frames[1]), Is.Not.EqualTo(ReadSeq(fixture.Sender.Frames[0])));
            }
        }

        [Test]
        public async Task Silence_DisconnectsOnce_WithoutWaitingForTheOpenRequest()
        {
            using (var fixture = Start())
            {
                await fixture.WaitFrames(1);
                fixture.Clock.Advance(TimeSpan.FromSeconds(29));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(0));
                Assert.That(fixture.Client.PendingCount, Is.GreaterThan(0));

                int owner = Thread.CurrentThread.ManagedThreadId;
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(1));
                Assert.That(fixture.Sender.CloseThreadId, Is.EqualTo(owner));
                Assert.That(fixture.Client.PendingCount, Is.EqualTo(0));

                fixture.Clock.Advance(TimeSpan.FromSeconds(60));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(1));
                await fixture.Finished;
            }
        }

        [Test]
        public async Task ValidResponse_ResetsSilence_AndRejectCodeDoesNot()
        {
            var clock = new ServerClock();
            long before = clock.NowUnixMs();
            using (var rejected = Start(clock))
            {
                await rejected.WaitFrames(1);
                rejected.Reply(rejected.Sender.Frames[0], 104, 5);
                rejected.Clock.Advance(TimeSpan.FromSeconds(30));
                Assert.That(rejected.Sender.Closes, Is.EqualTo(1));
                long afterReject = clock.NowUnixMs();
                Assert.That(afterReject - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Is.InRange(-2000L, 2000L));
                Assert.That(afterReject, Is.Not.EqualTo(5));
                Assert.That(before, Is.Not.EqualTo(5));
            }

            using (var accepted = Start(new ServerClock()))
            {
                await accepted.WaitFrames(1);
                accepted.Clock.Advance(TimeSpan.FromSeconds(20));
                await accepted.WaitFrames(3);
                int framesAtResponse = accepted.Sender.Count;
                accepted.Reply(accepted.Sender.Frames[framesAtResponse - 1], 0, 5_000_000_000_000L);
                await accepted.WaitUntilSynced(5_000_000_000_000L);

                accepted.Clock.Advance(TimeSpan.FromSeconds(29));
                Assert.That(accepted.Sender.Closes, Is.EqualTo(0));
                int owner = Thread.CurrentThread.ManagedThreadId;
                accepted.Clock.Advance(TimeSpan.FromSeconds(1));
                Assert.That(accepted.Sender.Closes, Is.EqualTo(1));
                Assert.That(accepted.Sender.CloseThreadId, Is.EqualTo(owner));
            }
        }

        [Test]
        public async Task Cancel_StopsFurtherSends_AndClearsPending()
        {
            using (var fixture = Start())
            {
                await fixture.WaitFrames(1);
                fixture.Cancel();
                await fixture.Finished;
                Assert.That(fixture.Client.PendingCount, Is.EqualTo(0));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(0));

                fixture.Clock.Advance(TimeSpan.FromSeconds(100));
                await Task.Delay(20);
                Assert.That(fixture.Sender.Count, Is.EqualTo(1));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task SecondStart_IsRejected_UntilTheFirstLoopIsCanceled()
        {
            using (var fixture = Start())
            {
                await fixture.WaitFrames(1);
                UniTask second = fixture.Service.RunAsync(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
                Assert.That(second.Status, Is.EqualTo(UniTaskStatus.Faulted));
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => second.GetAwaiter().GetResult());
                Assert.That(error.Message, Does.Contain("已在运行"));
                Assert.That(fixture.Sender.Count, Is.EqualTo(1));

                fixture.Cancel();
                await fixture.Finished;
                fixture.StartAgain();
                await fixture.WaitFrames(2);
                Assert.That(fixture.Sender.Count, Is.EqualTo(2));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task Restart_IgnoresTheOldLoopClockAndLateResponse()
        {
            using (var fixture = Start())
            {
                await fixture.WaitFrames(1);
                byte[] oldFrame = fixture.Sender.Frames[0];
                fixture.Clock.Advance(TimeSpan.FromSeconds(25));
                await fixture.WaitFrames(3);
                fixture.Cancel();
                await fixture.Finished;
                Assert.That(fixture.Client.PendingCount, Is.EqualTo(0));

                fixture.Client.AdvanceConnection();
                int framesBeforeRestart = fixture.Sender.Count;
                fixture.StartAgain();
                await fixture.WaitFrames(framesBeforeRestart + 1);
                fixture.Reply(oldFrame, 0, 9_000_000_000_000L);
                long shown = fixture.ClockDisplay.NowUnixMs();
                Assert.That(Math.Abs(shown - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Is.LessThan(2000L));
                Assert.That(fixture.Client.PendingCount, Is.EqualTo(1));

                fixture.Clock.Advance(TimeSpan.FromSeconds(25));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(0));
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                Assert.That(fixture.Sender.Closes, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task SendFailure_DoesNotLeakPending_OrUnobservedExceptions()
        {
            int unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> taskHandler = (sender, args) =>
            {
                unobserved++;
                args.SetObserved();
            };
            Action<Exception> unitaskHandler = exception => unobserved++;
            TaskScheduler.UnobservedTaskException += taskHandler;
            UniTaskScheduler.UnobservedTaskException += unitaskHandler;
            try
            {
                var sender = new FaultSend();
                var transport = new TcpTransport();
                transport.Attach(sender);
                var client = new NetClient(transport, packet => { });
                var service = new HeartbeatService(client, new ServerClock(), new ManualHeartbeatClock());
                using (var cts = new CancellationTokenSource())
                {
                    UniTask run = service.RunAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), cts.Token);
                    DateTime until = DateTime.UtcNow.AddSeconds(2);
                    while (client.PendingCount != 0 || sender.Attempts == 0)
                    {
                        if (DateTime.UtcNow > until)
                            Assert.Fail("失败的心跳没有结束等待");
                        await Task.Delay(1);
                    }

                    cts.Cancel();
                    await run;
                }

                Assert.That(client.PendingCount, Is.EqualTo(0));
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20);
                Assert.That(unobserved, Is.EqualTo(0));
                transport.Close();
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= taskHandler;
                UniTaskScheduler.UnobservedTaskException -= unitaskHandler;
            }
        }

        [Test]
        public void RunAsync_FromAnotherThread_IsRejected()
        {
            var transport = new TcpTransport();
            transport.Attach(new CaptureSend());
            var client = new NetClient(transport, packet => { });
            var service = new HeartbeatService(client, new ServerClock(), new ManualHeartbeatClock());
            Exception error = Assert.Catch<Exception>(() =>
            {
                Task.Run(() => service.RunAsync(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None).GetAwaiter().GetResult()).GetAwaiter().GetResult();
            });
            Assert.That(error.GetBaseException(), Is.TypeOf<InvalidOperationException>());
            transport.Close();
        }

        static Fixture Start()
        {
            return Start(new ServerClock());
        }

        static Fixture Start(ServerClock clock)
        {
            var fixture = new Fixture(clock);
            fixture.StartAgain();
            return fixture;
        }

        static ushort ReadMsgId(byte[] frame)
        {
            return ReadPacket(frame).MsgId;
        }

        static uint ReadSeq(byte[] frame)
        {
            return ReadPacket(frame).Seq;
        }

        static Packet ReadPacket(byte[] frame)
        {
            DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumed);
            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            Assert.That(consumed, Is.EqualTo(frame.Length));
            return packet;
        }

        sealed class Fixture : IDisposable
        {
            CancellationTokenSource _cts = new CancellationTokenSource();
            UniTask _run;

            public Fixture(ServerClock clock)
            {
                ClockDisplay = clock;
                Clock = new ManualHeartbeatClock();
                Sender = new CaptureSend();
                Transport = new TcpTransport();
                Transport.Attach(Sender);
                Client = new NetClient(Transport, packet => { });
                Service = new HeartbeatService(Client, clock, Clock);
                Receive = Client.CaptureReceive();
            }

            public ServerClock ClockDisplay { get; }

            public ManualHeartbeatClock Clock { get; }

            public CaptureSend Sender { get; }

            public TcpTransport Transport { get; }

            public NetClient Client { get; }

            public HeartbeatService Service { get; }

            public Action<Packet> Receive { get; }

            public UniTask Finished
            {
                get { return _run; }
            }

            public void StartAgain()
            {
                if (_cts.IsCancellationRequested)
                {
                    _cts.Dispose();
                    _cts = new CancellationTokenSource();
                }

                _run = Service.RunAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), _cts.Token);
            }

            public void Cancel()
            {
                _cts.Cancel();
            }

            public async Task WaitFrames(int count)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(2);
                while (Sender.Count < count)
                {
                    if (DateTime.UtcNow > until)
                        Assert.Fail("心跳没有按节奏写出帧，当前 " + Sender.Count + " / " + count);
                    await Task.Delay(1);
                }
            }

            public async Task WaitUntilSynced(long serverUnixMs)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(2);
                while (Math.Abs(ClockDisplay.NowUnixMs() - serverUnixMs) > 2000L)
                {
                    if (DateTime.UtcNow > until)
                        Assert.Fail("合法心跳响应没有完成校时，当前 " + ClockDisplay.NowUnixMs());
                    await Task.Delay(1);
                }
            }

            public void Reply(byte[] requestFrame, int code, long serverUnixMs)
            {
                uint seq = ReadSeq(requestFrame);
                var response = new HeartbeatResp { Code = code, ServerTime = serverUnixMs };
                Receive(new Packet(MsgRegistry.GetId(typeof(HeartbeatResp)), seq, response.ToByteArray()));
                Client.DispatchPending(8);
            }

            public void Dispose()
            {
                _cts.Cancel();
                Transport.Close();
                _cts.Dispose();
            }
        }

        sealed class CaptureSend : ITransportSend
        {
            readonly List<byte[]> _frames = new List<byte[]>();

            public int Count
            {
                get
                {
                    lock (_frames)
                        return _frames.Count;
                }
            }

            public int Closes { get; private set; }

            public int CloseThreadId { get; private set; }

            public byte[][] Frames
            {
                get
                {
                    lock (_frames)
                        return _frames.ToArray();
                }
            }

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                lock (_frames)
                    _frames.Add(copy);
                return Task.FromResult(count);
            }

            public void Close()
            {
                Closes++;
                CloseThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        }

        sealed class FaultSend : ITransportSend
        {
            public int Attempts;

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Attempts);
                return Task.FromException<int>(new IOException("发送失败"));
            }

            public void Close()
            {
            }
        }
    }
}
