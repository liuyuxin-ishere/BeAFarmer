using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using Cysharp.Threading.Tasks;
using Game.Framework.Network;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class ReconnectServiceTests
    {
        [Test]
        public async Task Backoff_Records_1_2_4_8_16_30_30()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.FailCount = int.MaxValue;
                UniTask<bool> run = fixture.Start();
                int[] expected = { 1, 2, 4, 8, 16, 30, 30 };
                for (int i = 0; i < expected.Length; i++)
                {
                    await fixture.Boundary.WaitDelays(i + 1);
                    Assert.That(fixture.Boundary.Delays[i], Is.EqualTo(expected[i]));
                    if (i + 1 < expected.Length)
                        fixture.Boundary.ReleaseNextDelay();
                }

                fixture.Cancel();
                await AwaitCancel(run);
                Assert.That(fixture.Boundary.Delays, Is.EqualTo(expected));
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(expected.Length));
            }
        }

        [Test]
        public async Task Success_StopsFurtherAttempts_AndRestoresOnce()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.FailCount = 2;
                int restores = 0;
                UniTask<bool> run = fixture.Start(ct =>
                {
                    restores++;
                    return UniTask.FromResult(true);
                });

                await fixture.Boundary.WaitDelays(1);
                fixture.Boundary.ReleaseNextDelay();
                await fixture.Boundary.WaitDelays(2);
                fixture.Boundary.ReleaseNextDelay();

                bool restored = await run;
                await Task.Delay(40);

                Assert.That(restored, Is.True);
                Assert.That(restores, Is.EqualTo(1));
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(3));
                Assert.That(fixture.Boundary.Delays, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(fixture.Boundary.Latest.Closes, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task FalseCallback_Stops_AndDropsCandidate()
        {
            using (var fixture = new Fixture())
            {
                UniTask<HeartbeatResp> leaked = default;
                UniTask<bool> run = fixture.Start(ct =>
                {
                    leaked = fixture.Client.SendRequestAsync<HeartbeatResp>(
                        new HeartbeatReq(),
                        TimeSpan.FromSeconds(8),
                        CancellationToken.None);
                    return UniTask.FromResult(false);
                });

                bool restored = await run;
                try
                {
                    await leaked;
                    Assert.Fail("失败候选上的请求应该结束");
                }
                catch (Exception)
                {
                }

                Assert.That(restored, Is.False);
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(1));
                Assert.That(fixture.Boundary.Delays.Length, Is.EqualTo(0));
                Assert.That(fixture.Client.PendingCount, Is.EqualTo(0));
                Assert.That(fixture.Boundary.Latest.Closes, Is.GreaterThan(0));
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => fixture.Client.BeginReceive(CancellationToken.None));
                Assert.That(error.Message, Does.Not.Contain("已在运行"));
            }
        }

        [Test]
        public async Task CancelDuringDelay_EndsWithoutAnotherConnect()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.FailCount = int.MaxValue;
                UniTask<bool> run = fixture.Start();
                await fixture.Boundary.WaitDelays(1);
                int opens = fixture.Boundary.Opens;
                fixture.Cancel();
                await AwaitCancel(run);
                fixture.Boundary.ReleaseNextDelay();
                await Task.Delay(40);

                Assert.That(opens, Is.EqualTo(1));
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(1));
                Assert.That(fixture.Boundary.Delays, Is.EqualTo(new[] { 1 }));
            }
        }

        [Test]
        public async Task CancelDuringConnect_EndsWithoutAnotherAttempt()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.BlockNextOpen = true;
                UniTask<bool> run = fixture.Start();
                await fixture.Boundary.WaitOpens(1);
                fixture.Cancel();
                await AwaitCancel(run);
                await Task.Delay(40);

                Assert.That(fixture.Boundary.Opens, Is.EqualTo(1));
                Assert.That(fixture.Boundary.Delays.Length, Is.EqualTo(0));
                Assert.That(fixture.Transport.CurrentChannel, Is.Null);
            }
        }

        [Test]
        public async Task SecondStart_IsRejected()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.BlockNextOpen = true;
                UniTask<bool> first = fixture.Start();
                await fixture.Boundary.WaitOpens(1);

                UniTask<bool> second = fixture.Service.RecoverAsync(
                    fixture.Endpoint,
                    ct => UniTask.FromResult(true),
                    CancellationToken.None);
                Assert.That(second.Status, Is.EqualTo(UniTaskStatus.Faulted));
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => second.GetAwaiter().GetResult());
                Assert.That(error.Message, Does.Contain("已在运行"));
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(1));

                fixture.Cancel();
                await AwaitCancel(first);
            }
        }

        [Test]
        public async Task Success_KeepsTheConnectionUsable()
        {
            using (var fixture = new Fixture())
            {
                bool restored = await fixture.Start(ct => UniTask.FromResult(true));
                HoldingChannel channel = fixture.Boundary.Latest;
                byte[] frame = { 9, 8, 7, 6 };
                await fixture.Transport.SendAsync(frame, CancellationToken.None);
                await Task.Delay(40);

                Assert.That(restored, Is.True);
                Assert.That(fixture.Transport.CurrentChannel, Is.SameAs(channel));
                Assert.That(channel.Closes, Is.EqualTo(0));
                Assert.That(channel.Frames.Count, Is.EqualTo(1));
                Assert.That(channel.Frames[0], Is.EqualTo(frame));
            }
        }

        [Test]
        public async Task LateFalseCallback_DoesNotCloseTheNewSession()
        {
            using (var fixture = new Fixture())
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                UniTask<bool> first = fixture.Start(ct => gate.Task.AsUniTask());
                await fixture.Boundary.WaitOpens(1);
                HoldingChannel abandoned = fixture.Boundary.Latest;
                fixture.Cancel();
                await AwaitCancel(first);

                bool restored = await fixture.Start(ct => UniTask.FromResult(true));
                HoldingChannel current = fixture.Boundary.Latest;
                gate.TrySetResult(false);
                await Task.Delay(40);

                Assert.That(restored, Is.True);
                Assert.That(current, Is.Not.SameAs(abandoned));
                Assert.That(fixture.Transport.CurrentChannel, Is.SameAs(current));
                Assert.That(current.Closes, Is.EqualTo(0));
                Assert.That(abandoned.Closes, Is.GreaterThan(0));
            }
        }

        [Test]
        public async Task LateOpen_DoesNotReplaceOrCloseTheNewConnection()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.HoldOneOpen = true;
                UniTask<bool> first = fixture.Start();
                await fixture.Boundary.WaitOpens(1);
                fixture.Cancel();
                await AwaitCancel(first);

                bool restored = await fixture.Start(ct => UniTask.FromResult(true));
                HoldingChannel current = fixture.Boundary.Latest;
                var late = new HoldingChannel();
                fixture.Boundary.HeldOpen.TrySetResult(late);
                await fixture.Boundary.WaitCloses(late, 1);

                Assert.That(restored, Is.True);
                Assert.That(fixture.Transport.CurrentChannel, Is.SameAs(current));
                Assert.That(current.Closes, Is.EqualTo(0));
                Assert.That(late.Closes, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ProgrammingError_StopsWithoutRetry()
        {
            using (var fixture = new Fixture())
            {
                fixture.Boundary.ProgrammingFailures = 1;
                UniTask<bool> run = fixture.Start();
                InvalidOperationException error = null;
                try
                {
                    await run;
                    Assert.Fail("编程错误应该交给调用方");
                }
                catch (InvalidOperationException ex)
                {
                    error = ex;
                }

                await Task.Delay(40);
                Assert.That(error, Is.Not.Null);
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(1));
                Assert.That(fixture.Boundary.Delays.Length, Is.EqualTo(0));
            }
        }

        [Test]
        public void RecoverAsync_FromAnotherThread_IsRejected()
        {
            using (var fixture = new Fixture())
            {
                Exception error = Assert.Catch<Exception>(() =>
                {
                    Task.Run(() => fixture.Service.RecoverAsync(
                        fixture.Endpoint,
                        ct => UniTask.FromResult(true),
                        CancellationToken.None).GetAwaiter().GetResult()).GetAwaiter().GetResult();
                });
                Assert.That(error.GetBaseException(), Is.TypeOf<InvalidOperationException>());
                Assert.That(fixture.Boundary.Opens, Is.EqualTo(0));
            }
        }

        static async Task AwaitCancel(UniTask<bool> run)
        {
            try
            {
                await run;
                Assert.Fail("取消应该结束恢复");
            }
            catch (OperationCanceledException)
            {
            }
        }

        sealed class Fixture : IDisposable
        {
            readonly CancellationTokenSource _cts = new CancellationTokenSource();

            public Fixture()
            {
                Boundary = new ScriptedBoundary();
                Transport = new TcpTransport();
                Client = new NetClient(Transport, packet => { });
                Service = new ReconnectService(Client, Transport, Boundary);
                Endpoint = new ServerEndpoint("127.0.0.1", 9);
            }

            public ScriptedBoundary Boundary { get; }

            public TcpTransport Transport { get; }

            public NetClient Client { get; }

            public ReconnectService Service { get; }

            public ServerEndpoint Endpoint { get; }

            public UniTask<bool> Start()
            {
                return Start(ct => UniTask.FromResult(true));
            }

            public UniTask<bool> Start(Func<CancellationToken, UniTask<bool>> restore)
            {
                return Service.RecoverAsync(Endpoint, restore, _cts.Token);
            }

            public void Cancel()
            {
                _cts.Cancel();
            }

            public void Dispose()
            {
                _cts.Cancel();
                Transport.Close();
                _cts.Dispose();
            }
        }

        sealed class ScriptedBoundary : IReconnectBoundary
        {
            readonly object _gate = new object();
            readonly List<int> _delays = new List<int>();
            readonly Queue<TaskCompletionSource<bool>> _delayWaiters = new Queue<TaskCompletionSource<bool>>();
            int _opens;

            public int FailCount;

            public int ProgrammingFailures;

            public bool BlockNextOpen;

            public bool HoldOneOpen;

            public TaskCompletionSource<ITransportSend> HeldOpen { get; private set; }

            public HoldingChannel Latest { get; private set; }

            public int Opens
            {
                get
                {
                    lock (_gate)
                        return _opens;
                }
            }

            public int[] Delays
            {
                get
                {
                    lock (_gate)
                        return _delays.ToArray();
                }
            }

            public void ReleaseNextDelay()
            {
                TaskCompletionSource<bool> waiter;
                lock (_gate)
                {
                    if (_delayWaiters.Count == 0)
                        throw new InvalidOperationException("没有等待中的延迟");
                    waiter = _delayWaiters.Dequeue();
                }

                waiter.TrySetResult(true);
            }

            public async Task WaitDelays(int count)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(3);
                while (Delays.Length < count)
                {
                    if (DateTime.UtcNow > until)
                        Assert.Fail("没有等到第 " + count + " 次退避，当前 " + Delays.Length);
                    await Task.Delay(1);
                }
            }

            public async Task WaitOpens(int count)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(3);
                while (Opens < count)
                {
                    if (DateTime.UtcNow > until)
                        Assert.Fail("没有等到第 " + count + " 次连接，当前 " + Opens);
                    await Task.Delay(1);
                }
            }

            public async Task WaitCloses(HoldingChannel channel, int count)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(3);
                while (channel.Closes < count)
                {
                    if (DateTime.UtcNow > until)
                        Assert.Fail("晚完成的连接没有被单独关闭");
                    await Task.Delay(1);
                }
            }

            public async UniTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _delays.Add((int)Math.Round(delay.TotalSeconds));
                    _delayWaiters.Enqueue(waiter);
                }

                using (cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)))
                    await waiter.Task;
            }

            public Task ConnectAsync(TcpTransport transport, ServerEndpoint endpoint, CancellationToken cancellationToken)
            {
                Task<ITransportSend> opening = Open(cancellationToken);
                return transport.ConnectAttemptAsync(endpoint, (ep, token) => opening, cancellationToken);
            }

            Task<ITransportSend> Open(CancellationToken cancellationToken)
            {
                lock (_gate)
                    _opens++;

                if (ProgrammingFailures > 0)
                {
                    ProgrammingFailures--;
                    return Task.FromException<ITransportSend>(new InvalidOperationException("测试编程错误"));
                }

                if (FailCount > 0)
                {
                    FailCount--;
                    return Task.FromException<ITransportSend>(new IOException("网络不可用"));
                }

                if (HoldOneOpen)
                {
                    HoldOneOpen = false;
                    var held = new TaskCompletionSource<ITransportSend>(TaskCreationOptions.RunContinuationsAsynchronously);
                    HeldOpen = held;
                    return held.Task;
                }

                if (BlockNextOpen)
                {
                    BlockNextOpen = false;
                    var blocked = new TaskCompletionSource<ITransportSend>(TaskCreationOptions.RunContinuationsAsynchronously);
                    cancellationToken.Register(() => blocked.TrySetCanceled(cancellationToken));
                    return blocked.Task;
                }

                var channel = new HoldingChannel();
                Latest = channel;
                return Task.FromResult<ITransportSend>(channel);
            }
        }

        sealed class HoldingChannel : ITransportSend, ITransportReceive
        {
            readonly List<byte[]> _frames = new List<byte[]>();
            int _closes;

            public int Closes
            {
                get { return Volatile.Read(ref _closes); }
            }

            public List<byte[]> Frames
            {
                get
                {
                    lock (_frames)
                        return new List<byte[]>(_frames);
                }
            }

            public Task<int> SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                lock (_frames)
                    _frames.Add(copy);
                return Task.FromResult(count);
            }

            public Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var waiting = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => waiting.TrySetCanceled(cancellationToken));
                return waiting.Task;
            }

            public void Close()
            {
                Interlocked.Increment(ref _closes);
            }
        }
    }
}
