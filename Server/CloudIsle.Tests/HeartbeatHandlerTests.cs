using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Account;
using CloudIsle.Server.Network;
using Google.Protobuf;

namespace CloudIsle.Tests
{
    public sealed class HeartbeatHandlerTests
    {
        [Test]
        public async Task ReleaseSession_DropsLivenessRecord_AndIsIdempotent()
        {
            var handler = new HeartbeatHandler((context, packet, ct) => Task.CompletedTask);
            var heartbeatContext = new RequestContext(77, 0, MsgRegistry.GetId(typeof(HeartbeatReq)), 1);
            await handler.HandleAsync(heartbeatContext, new HeartbeatReq(), CancellationToken.None);
            Assert.That(handler.LastAliveUnixMs(77), Is.GreaterThan(0));
            handler.ReleaseSession(77);
            Assert.That(handler.LastAliveUnixMs(77), Is.EqualTo(0));
            handler.ReleaseSession(77);
            Assert.That(handler.LastAliveUnixMs(77), Is.EqualTo(0));
        }

        [Test]
        public async Task Handle_UsesRequestSeqAndRealUtcMilliseconds()
        {
            var sent = new List<Packet>();
            var handler = new HeartbeatHandler((context, packet, token) =>
            {
                sent.Add(packet);
                return Task.CompletedTask;
            });
            var router = new MessageRouter(
                (context, packet, token) =>
                {
                    sent.Add(packet);
                    return Task.CompletedTask;
                },
                (context, reason, token) => Task.CompletedTask);
            handler.Register(router);

            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await router.DispatchAsync(
                new RequestContext(8, 0, MsgRegistry.GetId(typeof(HeartbeatReq)), 17),
                new Packet(
                    MsgRegistry.GetId(typeof(HeartbeatReq)),
                    17,
                    new HeartbeatReq { ClientTime = -123456789 }.ToByteArray()),
                CancellationToken.None);
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Assert.That(sent, Has.Count.EqualTo(1));
            Packet reply = sent[0];
            Assert.That(reply.MsgId, Is.EqualTo(MsgRegistry.GetId(typeof(HeartbeatResp))));
            Assert.That(reply.Seq, Is.EqualTo(17));
            HeartbeatResp response = HeartbeatResp.Parser.ParseFrom(reply.Body);
            Assert.That(response.Code, Is.EqualTo(0));
            Assert.That(response.ServerTime, Is.InRange(before, after));
            Assert.That(response.ServerTime, Is.Not.EqualTo(-123456789L));
            Assert.That(handler.LastAliveUnixMs(8), Is.EqualTo(response.ServerTime));
            Assert.That(handler.LastAliveUnixMs(9), Is.EqualTo(0));
        }

        [Test]
        public async Task Handle_KeepsEachSessionAliveTime()
        {
            var sent = new List<Packet>();
            var handler = new HeartbeatHandler((context, packet, token) =>
            {
                sent.Add(packet);
                return Task.CompletedTask;
            });
            await handler.HandleAsync(
                new RequestContext(3, 0, MsgRegistry.GetId(typeof(HeartbeatReq)), 1),
                new HeartbeatReq { ClientTime = 10 },
                CancellationToken.None);
            await Task.Delay(2);
            await handler.HandleAsync(
                new RequestContext(4, 20, MsgRegistry.GetId(typeof(HeartbeatReq)), 2),
                new HeartbeatReq { ClientTime = 10 },
                CancellationToken.None);

            Assert.That(sent, Has.Count.EqualTo(2));
            Assert.That(sent[0].Seq, Is.EqualTo(1));
            Assert.That(sent[1].Seq, Is.EqualTo(2));
            long first = handler.LastAliveUnixMs(3);
            long second = handler.LastAliveUnixMs(4);
            Assert.That(first, Is.GreaterThan(0));
            Assert.That(second, Is.GreaterThanOrEqualTo(first));
            Assert.That(HeartbeatResp.Parser.ParseFrom(sent[0].Body).ServerTime, Is.EqualTo(first));
            Assert.That(HeartbeatResp.Parser.ParseFrom(sent[1].Body).ServerTime, Is.EqualTo(second));
        }
    }
}
