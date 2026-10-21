using System;
using CloudIsle.Net;

namespace CloudIsle.Tests
{
    public sealed class FrameCodecTests
    {
        [Test]
        public void Encode_EmptyBody_IsTenBytes()
        {
            byte[] frame = FrameCodec.Encode(1001, 0, Array.Empty<byte>());
            byte[] expected =
            {
                0x06, 0x00, 0x00, 0x00,
                0xE9, 0x03,
                0x00, 0x00, 0x00, 0x00
            };

            Assert.That(frame, Is.EqualTo(expected));
        }

        [Test]
        public void Encode_WritesLengthAndLittleEndianFields()
        {
            byte[] frame = FrameCodec.Encode(1005, 0x01020304, new byte[] { 0x01, 0x02 });
            byte[] expected =
            {
                0x08, 0x00, 0x00, 0x00,
                0xED, 0x03,
                0x04, 0x03, 0x02, 0x01,
                0x01, 0x02
            };

            Assert.That(frame, Is.EqualTo(expected));
        }

        [Test]
        public void Encode_MaxBody_ReturnsMaxFrame()
        {
            byte[] body = new byte[FrameCodec.MaxBodyLength];
            body[0] = 0xAB;
            body[body.Length - 1] = 0xCD;

            byte[] frame = FrameCodec.Encode(1001, 7, body);

            Assert.That(frame.Length, Is.EqualTo(FrameCodec.MaxFrameLength));
            Assert.That(frame[0], Is.EqualTo(0x06));
            Assert.That(frame[1], Is.EqualTo(0x00));
            Assert.That(frame[2], Is.EqualTo(0x01));
            Assert.That(frame[3], Is.EqualTo(0x00));
            Assert.That(frame[10], Is.EqualTo(0xAB));
            Assert.That(frame[frame.Length - 1], Is.EqualTo(0xCD));
        }

        [Test]
        public void Encode_OversizeBody_ThrowsBeforeReturningFrame()
        {
            byte[] body = new byte[FrameCodec.MaxBodyLength + 1];

            Assert.Throws<ArgumentOutOfRangeException>(() => FrameCodec.Encode(1001, 0, body));
        }

        [Test]
        public void Encode_NullBody_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => FrameCodec.Encode(1001, 0, null));
        }

        [Test]
        public void TryDecode_PartialFrame_ConsumesZero()
        {
            byte[] frame = FrameCodec.Encode(1001, 0, Array.Empty<byte>());
            int[] lengths = { 0, 3, 4, 9 };

            foreach (int length in lengths)
            {
                DecodeStatus status = FrameCodec.TryDecode(
                    frame.AsSpan(0, length),
                    out Packet packet,
                    out int consumedBytes);

                Assert.That(status, Is.EqualTo(DecodeStatus.NeedMoreData), "length=" + length);
                Assert.That(consumedBytes, Is.EqualTo(0), "length=" + length);
                Assert.That(packet.Body, Is.Null, "length=" + length);
            }
        }

        [Test]
        public void TryDecode_TwoFrames_ConsumesOnlyFirst()
        {
            byte[] frame = FrameCodec.Encode(1001, 0, Array.Empty<byte>());
            byte[] buffer = new byte[frame.Length * 2];
            frame.CopyTo(buffer, 0);
            frame.CopyTo(buffer, frame.Length);

            DecodeStatus first = FrameCodec.TryDecode(buffer, out Packet firstPacket, out int firstConsumed);
            Assert.That(first, Is.EqualTo(DecodeStatus.Success));
            Assert.That(firstConsumed, Is.EqualTo(10));
            Assert.That(firstPacket.MsgId, Is.EqualTo((ushort)1001));
            Assert.That(firstPacket.Seq, Is.EqualTo(0u));
            Assert.That(firstPacket.Body, Is.Empty);

            DecodeStatus second = FrameCodec.TryDecode(
                buffer.AsSpan(firstConsumed),
                out Packet secondPacket,
                out int secondConsumed);
            Assert.That(second, Is.EqualTo(DecodeStatus.Success));
            Assert.That(secondConsumed, Is.EqualTo(10));
            Assert.That(secondPacket.MsgId, Is.EqualTo((ushort)1001));
        }

        [Test]
        public void TryDecode_IllegalLength_ReturnsInvalid()
        {
            byte[] tooSmall = { 0x05, 0x00, 0x00, 0x00 };
            byte[] tooLarge = { 0x07, 0x00, 0x01, 0x00 };

            DecodeStatus small = FrameCodec.TryDecode(tooSmall, out _, out int smallConsumed);
            DecodeStatus large = FrameCodec.TryDecode(tooLarge, out _, out int largeConsumed);

            Assert.That(small, Is.EqualTo(DecodeStatus.Invalid));
            Assert.That(smallConsumed, Is.EqualTo(0));
            Assert.That(large, Is.EqualTo(DecodeStatus.Invalid));
            Assert.That(largeConsumed, Is.EqualTo(0));
        }

        [Test]
        public void TryDecode_CopiesBody()
        {
            byte[] frame = FrameCodec.Encode(1005, 0x01020304, new byte[] { 0x01, 0x02 });

            DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumedBytes);
            frame[10] = 0;
            frame[11] = 0;

            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            Assert.That(consumedBytes, Is.EqualTo(12));
            Assert.That(packet.MsgId, Is.EqualTo((ushort)1005));
            Assert.That(packet.Seq, Is.EqualTo(0x01020304u));
            Assert.That(packet.Body, Is.EqualTo(new byte[] { 0x01, 0x02 }));
        }

        /// <summary>
        /// 一帧的每个前缀都视为半包；两帧粘在一起时先只消费第一帧。
        /// </summary>
        [Test]
        public void Decode_HandlesSplitAndCoalescedFrames()
        {
            byte[] emptyFrame = FrameCodec.Encode(1001, 0, Array.Empty<byte>());
            byte[] shortFrame = FrameCodec.Encode(1005, 9, new byte[] { 1, 2, 3, 4 });
            byte[] maxBody = new byte[FrameCodec.MaxBodyLength];
            maxBody[0] = 0x11;
            maxBody[maxBody.Length - 1] = 0x22;
            byte[] maxFrame = FrameCodec.Encode(1001, 3, maxBody);

            AssertEverySplitNeedsMoreData(emptyFrame);
            AssertEverySplitNeedsMoreData(shortFrame);
            AssertEverySplitNeedsMoreData(maxFrame);
            AssertFullFrame(emptyFrame, 1001, 0, Array.Empty<byte>());
            AssertFullFrame(shortFrame, 1005, 9, new byte[] { 1, 2, 3, 4 });
            AssertFullFrame(maxFrame, 1001, 3, maxBody);

            byte[] first = FrameCodec.Encode(1001, 1, new byte[] { 0x0A });
            byte[] second = FrameCodec.Encode(1005, 2, new byte[] { 0x0B, 0x0C });
            byte[] both = new byte[first.Length + second.Length];
            first.CopyTo(both, 0);
            second.CopyTo(both, first.Length);

            DecodeStatus firstStatus = FrameCodec.TryDecode(both, out Packet firstPacket, out int firstConsumed);
            Assert.That(firstStatus, Is.EqualTo(DecodeStatus.Success));
            Assert.That(firstConsumed, Is.EqualTo(first.Length));
            Assert.That(firstPacket.MsgId, Is.EqualTo((ushort)1001));
            Assert.That(firstPacket.Seq, Is.EqualTo(1u));
            Assert.That(firstPacket.Body, Is.EqualTo(new byte[] { 0x0A }));

            DecodeStatus secondStatus = FrameCodec.TryDecode(
                both.AsSpan(firstConsumed),
                out Packet secondPacket,
                out int secondConsumed);
            Assert.That(secondStatus, Is.EqualTo(DecodeStatus.Success));
            Assert.That(secondConsumed, Is.EqualTo(second.Length));
            Assert.That(secondPacket.MsgId, Is.EqualTo((ushort)1005));
            Assert.That(secondPacket.Seq, Is.EqualTo(2u));
            Assert.That(secondPacket.Body, Is.EqualTo(new byte[] { 0x0B, 0x0C }));

            byte[] partialSecond = new byte[first.Length + 3];
            first.CopyTo(partialSecond, 0);
            partialSecond[first.Length] = second[0];
            partialSecond[first.Length + 1] = second[1];
            partialSecond[first.Length + 2] = second[2];

            DecodeStatus partialFirst = FrameCodec.TryDecode(
                partialSecond,
                out Packet partialPacket,
                out int partialConsumed);
            Assert.That(partialFirst, Is.EqualTo(DecodeStatus.Success));
            Assert.That(partialConsumed, Is.EqualTo(first.Length));
            Assert.That(partialPacket.MsgId, Is.EqualTo((ushort)1001));

            DecodeStatus partialRest = FrameCodec.TryDecode(
                partialSecond.AsSpan(partialConsumed),
                out _,
                out int restConsumed);
            Assert.That(partialRest, Is.EqualTo(DecodeStatus.NeedMoreData));
            Assert.That(restConsumed, Is.EqualTo(0));
        }

        /// <summary>
        /// 0 与最大正文可以往返；小于 6 或大于 65542 的 len 返回 Invalid。
        /// </summary>
        [Test]
        public void Decode_RejectsInvalidLength()
        {
            byte[] empty = FrameCodec.Encode(1001, 4, Array.Empty<byte>());
            AssertFullFrame(empty, 1001, 4, Array.Empty<byte>());

            byte[] maxBody = new byte[FrameCodec.MaxBodyLength];
            maxBody[0] = 0xAB;
            maxBody[maxBody.Length - 1] = 0xCD;
            byte[] maxFrame = FrameCodec.Encode(1009, 8, maxBody);
            Assert.That(maxFrame.Length, Is.EqualTo(FrameCodec.MaxFrameLength));
            AssertFullFrame(maxFrame, 1009, 8, maxBody);

            AssertInvalidLength(new byte[] { 0x00, 0x00, 0x00, 0x00 });
            AssertInvalidLength(new byte[] { 0x05, 0x00, 0x00, 0x00 });
            byte[] oversize = new byte[12];
            oversize[0] = 0x07;
            oversize[1] = 0x00;
            oversize[2] = 0x01;
            oversize[3] = 0x00;
            AssertInvalidLength(oversize);

            byte[] maxPayloadHeader = { 0x06, 0x00, 0x01, 0x00 };
            DecodeStatus needMore = FrameCodec.TryDecode(maxPayloadHeader, out _, out int consumed);
            Assert.That(needMore, Is.EqualTo(DecodeStatus.NeedMoreData));
            Assert.That(consumed, Is.EqualTo(0));
        }

        /// <summary>
        /// 一帧的每个前缀都视为半包。
        /// </summary>
        /// <param name="frame">Encode 得到的完整一帧。</param>
        static void AssertEverySplitNeedsMoreData(byte[] frame)
        {
            for (int length = 0; length < frame.Length; length++)
            {
                DecodeStatus status = FrameCodec.TryDecode(
                    frame.AsSpan(0, length),
                    out _,
                    out int consumedBytes);
                Assert.That(status, Is.EqualTo(DecodeStatus.NeedMoreData), "length=" + length);
                Assert.That(consumedBytes, Is.EqualTo(0), "length=" + length);
            }
        }

        static void AssertFullFrame(byte[] frame, ushort msgId, uint seq, byte[] body)
        {
            DecodeStatus status = FrameCodec.TryDecode(frame, out Packet packet, out int consumedBytes);
            Assert.That(status, Is.EqualTo(DecodeStatus.Success));
            Assert.That(consumedBytes, Is.EqualTo(frame.Length));
            Assert.That(packet.MsgId, Is.EqualTo(msgId));
            Assert.That(packet.Seq, Is.EqualTo(seq));
            Assert.That(packet.Body, Is.EqualTo(body));
        }

        static void AssertInvalidLength(byte[] buffer)
        {
            DecodeStatus status = FrameCodec.TryDecode(buffer, out _, out int consumedBytes);
            Assert.That(status, Is.EqualTo(DecodeStatus.Invalid));
            Assert.That(consumedBytes, Is.EqualTo(0));
        }
    }
}
