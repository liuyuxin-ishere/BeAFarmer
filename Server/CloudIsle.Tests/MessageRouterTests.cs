using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Network;
using Google.Protobuf;

namespace CloudIsle.Tests
{
    public sealed class MessageRouterTests
    {
        const string Password = "p@ss-should-not-leak";
        const string Token = "tok-should-not-leak";

        [TestCase(0L)]
        [TestCase(7L)]
        public async Task UnparsedRejectedPacket_DoesNotResetDeserializeFailures(long playerId)
        {
            int closed = 0;
            var router = new MessageRouter(
                (context, packet, ct) => Task.CompletedTask,
                (context, reason, ct) => { closed++; return Task.CompletedTask; });
            ushort heartbeatId = MsgRegistry.GetId(typeof(HeartbeatReq));
            router.Register<HeartbeatReq>(heartbeatId, (context, request, ct) => Task.CompletedTask);
            var heartbeatContext = new RequestContext(42, playerId, heartbeatId, 1);
            var malformed = new Packet(heartbeatId, 1, new byte[] { 0xff });
            for (int i = 0; i < 4; i++)
                await router.DispatchAsync(heartbeatContext, malformed, CancellationToken.None);
            var rejectedContext = new RequestContext(42, playerId, ushort.MaxValue, 2);
            await router.DispatchAsync(rejectedContext,
                new Packet(ushort.MaxValue, 2, new byte[] { 0xff }), CancellationToken.None);
            Assert.That(closed, Is.EqualTo(0));
            await router.DispatchAsync(heartbeatContext, malformed, CancellationToken.None);
            Assert.That(closed, Is.EqualTo(1));
        }

        [Test]
        public async Task PreAuthWhitelist_ReachesRegisteredHandlers()
        {
            var fixture = new RouterFixture();
            int heartbeats = 0;
            int registers = 0;
            int logins = 0;
            int reconnects = 0;
            fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) =>
            {
                heartbeats++;
                Assert.AreEqual(11, context.SessionId);
                Assert.AreEqual(0, context.PlayerId);
                Assert.AreEqual(1001, context.MsgId);
                Assert.AreEqual(1u, context.Seq);
                Assert.AreEqual(5L, request.ClientTime);
                return Task.CompletedTask;
            });
            fixture.Router.Register<RegisterReq>(Id<RegisterReq>(), (context, request, token) =>
            {
                registers++;
                Assert.AreEqual(Password, request.Password);
                return Task.CompletedTask;
            });
            fixture.Router.Register<LoginReq>(Id<LoginReq>(), (context, request, token) =>
            {
                logins++;
                Assert.AreEqual(Password, request.Password);
                return Task.CompletedTask;
            });
            fixture.Router.Register<ReconnectReq>(Id<ReconnectReq>(), (context, request, token) =>
            {
                reconnects++;
                Assert.AreEqual(Token, request.Token);
                return Task.CompletedTask;
            });

            await fixture.DispatchAsync(11, 0, new HeartbeatReq { ClientTime = 5 }, 1);
            await fixture.DispatchAsync(11, 0, new RegisterReq { Username = "u", Password = Password, Nickname = "n" }, 2);
            await fixture.DispatchAsync(11, 0, new LoginReq { Username = "u", Password = Password }, 3);
            await fixture.DispatchAsync(11, 0, new ReconnectReq { Token = Token }, 4);

            Assert.AreEqual(1, heartbeats);
            Assert.AreEqual(1, registers);
            Assert.AreEqual(1, logins);
            Assert.AreEqual(1, reconnects);
            Assert.AreEqual(0, fixture.Sent.Count);
            AssertTraceHasIdsWithoutSecrets(fixture, 11, 1005, 3);
        }

        [Test]
        public async Task ProtectedRequest_WhenUnauthenticated_Rejects102WithoutHandler()
        {
            var fixture = new RouterFixture();
            int calls = 0;
            fixture.Router.Register<LogoutReq>(Id<LogoutReq>(), (context, request, token) =>
            {
                calls++;
                return Task.CompletedTask;
            });

            await fixture.DispatchAsync(4, 0, new LogoutReq(), 9);

            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, fixture.Sent.Count);
            Assert.AreEqual(0, fixture.Closes.Count);
            StringAssert.Contains("session=4 msg=1009 seq=9 code=102 action=reject", string.Join("\n", fixture.Traces));

            await fixture.Router.DispatchAsync(
                new RequestContext(4, 0, 2001, 10),
                new Packet(2001, 10, Array.Empty<byte>()),
                CancellationToken.None);

            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, fixture.Sent.Count);
            StringAssert.Contains("session=4 msg=2001 seq=10 code=102 action=reject", string.Join("\n", fixture.Traces));
        }

        [Test]
        public async Task AuthenticatedRequest_ReachesMatchingHandler()
        {
            var fixture = new RouterFixture();
            LogoutReq? seen = null;
            fixture.Router.Register<LogoutReq>(Id<LogoutReq>(), (context, request, token) =>
            {
                Assert.AreEqual(8, context.PlayerId);
                Assert.AreEqual(1009, context.MsgId);
                Assert.AreEqual(12u, context.Seq);
                seen = request;
                return Task.CompletedTask;
            });

            await fixture.DispatchAsync(3, 8, new LogoutReq(), 12);

            Assert.IsNotNull(seen);
            Assert.AreEqual(0, fixture.Sent.Count);
        }

        [Test]
        public async Task Register_DuplicateIdOrWrongType_Fails()
        {
            var fixture = new RouterFixture();
            int calls = 0;
            fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) =>
            {
                calls++;
                return Task.CompletedTask;
            });

            Assert.Throws<InvalidOperationException>(() =>
                fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) => Task.CompletedTask));
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Router.Register<LoginReq>(Id<HeartbeatReq>(), (context, request, token) => Task.CompletedTask));

            await fixture.DispatchAsync(1, 0, new HeartbeatReq { ClientTime = 1 }, 1);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public async Task RateLimit_TwentyPerSubject_RecoversOnNextWindow()
        {
            var fixture = new RouterFixture();
            int calls = 0;
            fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) =>
            {
                calls++;
                return Task.CompletedTask;
            });

            for (uint seq = 1; seq <= 20; seq++)
                await fixture.DispatchAsync(1, 0, new HeartbeatReq(), seq);

            Assert.AreEqual(20, calls);
            await fixture.DispatchAsync(1, 0, new HeartbeatReq(), 21);
            Assert.AreEqual(20, calls);
            Assert.AreEqual(1, fixture.Sent.Count);
            Assert.AreEqual(1002, fixture.Sent[0].MsgId);
            Assert.AreEqual(21u, fixture.Sent[0].Seq);
            Assert.AreEqual(104, ReadCode(fixture.Sent[0]));

            await fixture.DispatchAsync(2, 0, new HeartbeatReq(), 1);
            Assert.AreEqual(21, calls);

            fixture.Clock.UnixMilliseconds += 999;
            await fixture.DispatchAsync(1, 0, new HeartbeatReq(), 22);
            Assert.AreEqual(21, calls);
            Assert.AreEqual(104, ReadCode(fixture.Sent[fixture.Sent.Count - 1]));

            fixture.Clock.UnixMilliseconds += 1;
            await fixture.DispatchAsync(1, 0, new HeartbeatReq(), 23);
            Assert.AreEqual(22, calls);
        }

        [Test]
        public async Task RateLimit_SamePlayerSharesWindow()
        {
            var fixture = new RouterFixture();
            int calls = 0;
            fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) =>
            {
                calls++;
                return Task.CompletedTask;
            });

            for (uint seq = 1; seq <= 20; seq++)
                await fixture.DispatchAsync(10, 70, new HeartbeatReq(), seq);

            await fixture.DispatchAsync(11, 70, new HeartbeatReq(), 1);
            Assert.AreEqual(20, calls);
            Assert.AreEqual(104, ReadCode(fixture.Sent[0]));
            Assert.AreEqual(1u, fixture.Sent[0].Seq);

            await fixture.DispatchAsync(12, 71, new HeartbeatReq(), 1);
            Assert.AreEqual(21, calls);
        }

        [Test]
        public async Task DeserializeFailures_CloseOnFifth_AndLegalPacketResets()
        {
            var fixture = new RouterFixture();
            int calls = 0;
            fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) =>
            {
                calls++;
                return Task.CompletedTask;
            });

            for (uint seq = 1; seq <= 4; seq++)
                await fixture.DispatchRawAsync(6, 0, Id<HeartbeatReq>(), seq, new byte[] { 0x08 });

            Assert.AreEqual(0, fixture.Closes.Count);
            Assert.AreEqual(0, calls);

            await fixture.DispatchAsync(6, 0, new HeartbeatReq { ClientTime = 9 }, 5);
            Assert.AreEqual(1, calls);

            for (uint seq = 6; seq <= 9; seq++)
                await fixture.DispatchRawAsync(6, 0, Id<HeartbeatReq>(), seq, new byte[] { 0x08 });

            Assert.AreEqual(0, fixture.Closes.Count);
            await fixture.DispatchRawAsync(6, 0, Id<HeartbeatReq>(), 10, new byte[] { 0x08 });
            Assert.AreEqual(1, fixture.Closes.Count);
            StringAssert.Contains("6:连续反序列化失败", fixture.Closes[0]);
            Assert.AreEqual(0, fixture.Router.ParseStateCount);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public async Task ReleaseSession_DropsCounters()
        {
            var fixture = new RouterFixture();
            int calls = 0;
            fixture.Router.Register<HeartbeatReq>(Id<HeartbeatReq>(), (context, request, token) =>
            {
                calls++;
                return Task.CompletedTask;
            });

            for (uint seq = 1; seq <= 20; seq++)
                await fixture.DispatchAsync(5, 0, new HeartbeatReq(), seq);

            await fixture.DispatchAsync(5, 0, new HeartbeatReq(), 21);
            Assert.AreEqual(104, ReadCode(fixture.Sent[0]));

            fixture.Router.ReleaseSession(5);
            Assert.AreEqual(0, fixture.Router.SessionRateCount);
            Assert.AreEqual(0, fixture.Router.SessionBindingCount);

            for (uint seq = 22; seq <= 41; seq++)
                await fixture.DispatchAsync(5, 0, new HeartbeatReq(), seq);

            Assert.AreEqual(40, calls);
            fixture.Router.ReleaseSession(5);

            for (long sessionId = 100; sessionId < 130; sessionId++)
                await fixture.DispatchAsync(sessionId, sessionId, new HeartbeatReq(), 1);

            for (long sessionId = 100; sessionId < 130; sessionId++)
                fixture.Router.ReleaseSession(sessionId);

            Assert.AreEqual(0, fixture.Router.SessionRateCount);
            Assert.AreEqual(0, fixture.Router.PlayerRateCount);
            Assert.AreEqual(0, fixture.Router.ParseStateCount);
            Assert.AreEqual(0, fixture.Router.SessionBindingCount);

            for (uint seq = 1; seq <= 4; seq++)
                await fixture.DispatchRawAsync(5, 0, Id<HeartbeatReq>(), seq, new byte[] { 0x08 });

            fixture.Router.ReleaseSession(5);
            Assert.AreEqual(0, fixture.Router.ParseStateCount);
            await fixture.DispatchRawAsync(5, 0, Id<HeartbeatReq>(), 5, new byte[] { 0x08 });
            Assert.AreEqual(0, fixture.Closes.Count);
        }

        [Test]
        public async Task AuthenticatedUnknownBusiness_StaysAtRejectBoundary()
        {
            var fixture = new RouterFixture();
            await fixture.Router.DispatchAsync(
                new RequestContext(9, 15, 2001, 4),
                new Packet(2001, 4, Array.Empty<byte>()),
                CancellationToken.None);

            Assert.AreEqual(0, fixture.Sent.Count);
            Assert.AreEqual(0, fixture.Closes.Count);
            StringAssert.Contains("session=9 msg=2001 seq=4 code=101 action=reject", string.Join("\n", fixture.Traces));
            fixture.Router.ReleaseSession(9);
            Assert.AreEqual(0, fixture.Router.SessionBindingCount);
        }

        static ushort Id<T>() where T : IMessage
        {
            return MsgRegistry.GetId(typeof(T));
        }

        static int ReadCode(Packet packet)
        {
            IMessage message = MsgRegistry.Parse(packet.MsgId, packet.Body);
            switch (message)
            {
                case HeartbeatResp heartbeat:
                    return heartbeat.Code;
                case RegisterResp register:
                    return register.Code;
                case LoginResp login:
                    return login.Code;
                case ReconnectResp reconnect:
                    return reconnect.Code;
                default:
                    Assert.Fail("这条响应没有 code");
                    return -1;
            }
        }

        static void AssertTraceHasIdsWithoutSecrets(RouterFixture fixture, long sessionId, ushort msgId, uint seq)
        {
            string text = string.Join("\n", fixture.Traces);
            StringAssert.Contains("session=" + sessionId, text);
            StringAssert.Contains("msg=" + msgId, text);
            StringAssert.Contains("seq=" + seq, text);
            StringAssert.DoesNotContain(Password, text);
            StringAssert.DoesNotContain(Token, text);
        }

        sealed class RouterFixture
        {
            public RouterFixture()
            {
                Clock = new ManualClock();
                Sent = new List<Packet>();
                Closes = new List<string>();
                Traces = new List<string>();
                Router = new MessageRouter(
                    (context, packet, token) =>
                    {
                        Sent.Add(packet);
                        return Task.CompletedTask;
                    },
                    (context, reason, token) =>
                    {
                        Closes.Add(context.SessionId + ":" + reason);
                        return Task.CompletedTask;
                    },
                    Clock,
                    line => Traces.Add(line));
            }

            public ManualClock Clock { get; }

            public List<Packet> Sent { get; }

            public List<string> Closes { get; }

            public List<string> Traces { get; }

            public MessageRouter Router { get; }

            public Task DispatchAsync(long sessionId, long playerId, IMessage message, uint seq)
            {
                ushort msgId = MsgRegistry.GetId(message.GetType());
                return DispatchRawAsync(sessionId, playerId, msgId, seq, message.ToByteArray());
            }

            public Task DispatchRawAsync(long sessionId, long playerId, ushort msgId, uint seq, byte[] body)
            {
                return Router.DispatchAsync(
                    new RequestContext(sessionId, playerId, msgId, seq),
                    new Packet(msgId, seq, body),
                    CancellationToken.None);
            }
        }

        sealed class ManualClock : IRouterClock
        {
            public long UnixMilliseconds { get; set; } = 1_700_000_000_000L;
        }
    }
}
