using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Server.Network;

namespace CloudIsle.Tests
{
    public sealed class PipelineFrameReaderTests
    {
        [Test]
        public async Task CrossSegment_LengthHeader_DecodesPacket()
        {
            byte[] frame = FrameCodec.Encode(1001, 7, new byte[] { 0x11, 0x22 });
            ReadOnlySequence<byte> sequence = Split(frame, 2);
            Assert.That(sequence.IsSingleSegment, Is.False);

            using var reader = new SegmentPipeReader(sequence, writerCompleted: true);
            List<Packet> packets = await ReadAll(reader);

            Assert.That(packets, Has.Count.EqualTo(1));
            Assert.That(packets[0].MsgId, Is.EqualTo((ushort)1001));
            Assert.That(packets[0].Seq, Is.EqualTo(7u));
            Assert.That(packets[0].Body, Is.EqualTo(new byte[] { 0x11, 0x22 }));
            Assert.That(reader.Advances[0].Consumed, Is.EqualTo(frame.Length));
            Assert.That(reader.Advances[0].Examined, Is.EqualTo(frame.Length));
        }

        [Test]
        public async Task CrossSegment_MessageHeader_DecodesMsgIdAndSeq()
        {
            byte[] frame = FrameCodec.Encode(0xBEEF, 0x11223344, new byte[] { 0x01 });
            ReadOnlySequence<byte> sequence = Split(frame, 4, 7);
            Assert.That(sequence.IsSingleSegment, Is.False);

            using var reader = new SegmentPipeReader(sequence, writerCompleted: true);
            List<Packet> packets = await ReadAll(reader);

            Assert.That(packets[0].MsgId, Is.EqualTo((ushort)0xBEEF));
            Assert.That(packets[0].Seq, Is.EqualTo(0x11223344u));
            Assert.That(packets[0].Body, Is.EqualTo(new byte[] { 0x01 }));
        }

        [Test]
        public async Task CrossSegment_Body_KeepsBytes()
        {
            byte[] body = { 0x0A, 0x0B, 0x0C, 0x0D, 0x0E };
            byte[] frame = FrameCodec.Encode(8, 3, body);
            ReadOnlySequence<byte> sequence = Split(frame, 10, 12);
            Assert.That(sequence.IsSingleSegment, Is.False);

            using var reader = new SegmentPipeReader(sequence, writerCompleted: true);
            List<Packet> packets = await ReadAll(reader);

            Assert.That(packets[0].Body, Is.EqualTo(body));
            Assert.That(reader.CompleteError, Is.Null);
        }

        [Test]
        public async Task CoalescedFrames_AdvancesOneFrameAtATime()
        {
            byte[] first = FrameCodec.Encode(1, 10, new byte[] { 0x01 });
            byte[] second = FrameCodec.Encode(2, 11, new byte[] { 0x02, 0x03 });
            byte[] both = new byte[first.Length + second.Length];
            first.CopyTo(both, 0);
            second.CopyTo(both, first.Length);
            ReadOnlySequence<byte> sequence = Split(both, 3, first.Length + 1);

            using var reader = new SegmentPipeReader(sequence, writerCompleted: true);
            var seen = new List<(int Reads, int Advances, Packet Packet)>();
            await PipelineFrameReader.ReadLoopAsync(reader, packet =>
            {
                seen.Add((reader.ReadCount, reader.Advances.Count, packet));
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.That(seen, Has.Count.EqualTo(2));
            Assert.That(seen[0].Reads, Is.EqualTo(1));
            Assert.That(seen[0].Advances, Is.EqualTo(1));
            Assert.That(seen[0].Packet.Seq, Is.EqualTo(10u));
            Assert.That(reader.Advances[0].Consumed, Is.EqualTo(first.Length));
            Assert.That(seen[1].Reads, Is.EqualTo(2));
            Assert.That(seen[1].Advances, Is.EqualTo(2));
            Assert.That(seen[1].Packet.Seq, Is.EqualTo(11u));
            Assert.That(reader.Advances[1].Consumed, Is.EqualTo(second.Length));
            Assert.That(seen[0].Packet.Body, Is.EqualTo(new byte[] { 0x01 }));
            Assert.That(seen[1].Packet.Body, Is.EqualTo(new byte[] { 0x02, 0x03 }));
        }

        [Test]
        public async Task PartialFrame_OnPipe_WaitsWithoutBusyLoop()
        {
            byte[] frame = FrameCodec.Encode(42, 9, new byte[] { 0x55, 0x66 });
            var pipe = new Pipe();
            using var reader = new RecordingPipeReader(pipe.Reader);
            var packets = new List<Packet>();
            Task loop = PipelineFrameReader.ReadLoopAsync(reader, packet =>
            {
                packets.Add(packet);
                return Task.CompletedTask;
            }, CancellationToken.None);

            await pipe.Writer.WriteAsync(frame.AsMemory(0, 5));
            await pipe.Writer.FlushAsync();
            await reader.WaitForReadsAsync(2);

            await Task.Delay(150);
            Assert.That(packets, Is.Empty);
            Assert.That(reader.ReadCount, Is.LessThan(8));
            Assert.That(reader.Advances[0].Consumed, Is.EqualTo(0));
            Assert.That(reader.Advances[0].Examined, Is.EqualTo(5));

            int readsWhileWaiting = reader.ReadCount;
            await pipe.Writer.WriteAsync(frame.AsMemory(5));
            await pipe.Writer.FlushAsync();
            await pipe.Writer.CompleteAsync();
            await loop;

            Assert.That(packets, Has.Count.EqualTo(1));
            Assert.That(packets[0].MsgId, Is.EqualTo((ushort)42));
            Assert.That(packets[0].Body, Is.EqualTo(new byte[] { 0x55, 0x66 }));
            Assert.That(reader.ReadCount - readsWhileWaiting, Is.LessThan(8));
            Assert.That(reader.CompleteCalls, Is.EqualTo(1));
            Assert.That(reader.CompleteError, Is.Null);
        }

        [Test]
        public async Task AdvanceTo_RewritesBuffer_PacketKeepsOwnBytes()
        {
            byte[] body = { 0x10, 0x20, 0x30 };
            byte[] frame = FrameCodec.Encode(77, 5, body);
            byte[] storage = (byte[])frame.Clone();
            var first = new BufferSegment(storage.AsMemory(0, 6));
            BufferSegment last = first.Append(storage.AsMemory(6));
            var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            Assert.That(sequence.IsSingleSegment, Is.False);

            using var reader = new SegmentPipeReader(sequence, writerCompleted: true, rewrite: storage);
            Packet captured = default;
            await PipelineFrameReader.ReadLoopAsync(reader, packet =>
            {
                captured = packet;
                Assert.That(storage, Is.All.EqualTo((byte)0xA5));
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.That(captured.MsgId, Is.EqualTo((ushort)77));
            Assert.That(captured.Seq, Is.EqualTo(5u));
            Assert.That(captured.Body, Is.EqualTo(body));
            Assert.That(captured.Body, Is.Not.SameAs(storage));
        }

        [Test]
        public async Task MaxFrame_AcrossSegments_Succeeds()
        {
            byte[] body = new byte[FrameCodec.MaxBodyLength];
            body[0] = 0x01;
            body[FrameCodec.MaxBodyLength - 1] = 0x02;
            byte[] frame = FrameCodec.Encode(9, 1, body);
            Assert.That(frame.Length, Is.EqualTo(FrameCodec.MaxFrameLength));

            ReadOnlySequence<byte> sequence = Split(frame, 3, 10, 100);
            using var reader = new SegmentPipeReader(sequence, writerCompleted: true);
            List<Packet> packets = await ReadAll(reader);

            Assert.That(packets[0].Body.Length, Is.EqualTo(FrameCodec.MaxBodyLength));
            Assert.That(packets[0].Body[0], Is.EqualTo((byte)0x01));
            Assert.That(packets[0].Body[FrameCodec.MaxBodyLength - 1], Is.EqualTo((byte)0x02));
        }

        [Test]
        public void InvalidLength_FailsImmediately()
        {
            byte[] tooSmall = { 0x05, 0x00, 0x00, 0x00, 0xFF };
            byte[] tooLarge = { 0x07, 0x00, 0x01, 0x00 };

            AssertInvalidLength(tooSmall);
            AssertInvalidLength(tooLarge);
        }

        [Test]
        public async Task Eof_WithPartialFrame_Fails()
        {
            var pipe = new Pipe();
            using var reader = new RecordingPipeReader(pipe.Reader);
            await pipe.Writer.WriteAsync(new byte[] { 0x06, 0x00, 0x00 });
            await pipe.Writer.CompleteAsync();

            InvalidDataException error = Assert.ThrowsAsync<InvalidDataException>(async () =>
                await PipelineFrameReader.ReadLoopAsync(reader, _ => Task.CompletedTask, CancellationToken.None));

            Assert.That(error.Message, Does.Contain("未完成"));
            Assert.That(reader.CompleteCalls, Is.EqualTo(1));
            Assert.That(reader.CompleteError, Is.SameAs(error));
            Assert.That(reader.Advances[0].Consumed, Is.EqualTo(0));
            Assert.That(reader.Advances[0].Examined, Is.EqualTo(3));
        }

        [Test]
        public async Task Cancel_WhileWaiting_CompletesReader()
        {
            var pipe = new Pipe();
            using var reader = new RecordingPipeReader(pipe.Reader);
            using var cts = new CancellationTokenSource();
            Task loop = PipelineFrameReader.ReadLoopAsync(reader, _ => Task.CompletedTask, cts.Token);

            await reader.WaitForReadsAsync(1);
            cts.Cancel();

            OperationCanceledException error = Assert.ThrowsAsync<OperationCanceledException>(async () => await loop);
            Assert.That(reader.CompleteCalls, Is.EqualTo(1));
            Assert.That(reader.CompleteError, Is.InstanceOf<OperationCanceledException>());
            Assert.That(reader.CompleteError, Is.SameAs(error));
        }

        [Test]
        public void CallbackFault_CompletesReaderAndPropagates()
        {
            byte[] frame = FrameCodec.Encode(1, 2, new byte[] { 0x09 });
            using var reader = new SegmentPipeReader(FromParts(frame), writerCompleted: true);
            var error = new InvalidOperationException("回调失败");

            InvalidOperationException caught = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await PipelineFrameReader.ReadLoopAsync(reader, _ => throw error, CancellationToken.None));

            Assert.That(caught, Is.SameAs(error));
            Assert.That(reader.CompleteCalls, Is.EqualTo(1));
            Assert.That(reader.CompleteError, Is.SameAs(error));
        }

        private static void AssertInvalidLength(byte[] bytes)
        {
            using var reader = new SegmentPipeReader(FromParts(bytes), writerCompleted: false);
            InvalidDataException error = Assert.ThrowsAsync<InvalidDataException>(async () =>
                await PipelineFrameReader.ReadLoopAsync(reader, _ =>
                {
                    Assert.Fail("非法长度不应回调");
                    return Task.CompletedTask;
                }, CancellationToken.None));

            Assert.That(error.Message, Does.Contain("非法"));
            Assert.That(reader.ReadCount, Is.EqualTo(1));
            Assert.That(reader.Advances, Has.Count.EqualTo(1));
            Assert.That(reader.Advances[0].Consumed, Is.EqualTo(0));
            Assert.That(reader.CompleteError, Is.SameAs(error));
        }

        private static async Task<List<Packet>> ReadAll(SegmentPipeReader reader)
        {
            var packets = new List<Packet>();
            await PipelineFrameReader.ReadLoopAsync(reader, packet =>
            {
                packets.Add(packet);
                return Task.CompletedTask;
            }, CancellationToken.None);
            return packets;
        }

        private static ReadOnlySequence<byte> Split(byte[] data, params int[] ends)
        {
            var parts = new List<byte[]>();
            int start = 0;
            foreach (int end in ends)
            {
                parts.Add(data.AsSpan(start, end - start).ToArray());
                start = end;
            }

            if (start < data.Length)
                parts.Add(data.AsSpan(start).ToArray());
            return FromParts(parts.ToArray());
        }

        private static ReadOnlySequence<byte> FromParts(params byte[][] parts)
        {
            var first = new BufferSegment(parts[0]);
            BufferSegment last = first;
            for (int i = 1; i < parts.Length; i++)
                last = last.Append(parts[i]);
            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
        {
            public BufferSegment(ReadOnlyMemory<byte> data)
            {
                Memory = data;
            }

            public BufferSegment Append(ReadOnlyMemory<byte> data)
            {
                var next = new BufferSegment(data)
                {
                    RunningIndex = RunningIndex + Memory.Length
                };
                Next = next;
                return next;
            }
        }

        private sealed class SegmentPipeReader : PipeReader, IDisposable
        {
            private ReadOnlySequence<byte> _remaining;
            private readonly bool _writerCompleted;
            private readonly byte[]? _rewrite;

            public SegmentPipeReader(ReadOnlySequence<byte> sequence, bool writerCompleted, byte[]? rewrite = null)
            {
                _remaining = sequence;
                _writerCompleted = writerCompleted;
                _rewrite = rewrite;
            }

            public int ReadCount { get; private set; }

            public List<(long Consumed, long Examined)> Advances { get; } = new List<(long, long)>();

            public int CompleteCalls { get; private set; }

            public Exception? CompleteError { get; private set; }

            public override void AdvanceTo(SequencePosition consumed)
            {
                AdvanceTo(consumed, consumed);
            }

            public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCount++;
                return new ValueTask<ReadResult>(new ReadResult(_remaining, false, _writerCompleted));
            }

            public override bool TryRead(out ReadResult result)
            {
                result = default;
                return false;
            }

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            {
                long consumedBytes = _remaining.Slice(0, consumed).Length;
                long examinedBytes = _remaining.Slice(0, examined).Length;
                Advances.Add((consumedBytes, examinedBytes));
                if (_rewrite != null)
                    Array.Fill<byte>(_rewrite, 0xA5);
                _remaining = _remaining.Slice(consumed);
            }

            public override void CancelPendingRead()
            {
            }

            public override void Complete(Exception? exception = null)
            {
                CompleteCalls++;
                CompleteError = exception;
            }

            public void Dispose()
            {
            }
        }

        private sealed class RecordingPipeReader : PipeReader, IDisposable
        {
            private readonly PipeReader _inner;
            private ReadOnlySequence<byte> _last;
            private int _observedReads;
            private TaskCompletionSource<bool> _readsReached = NewGate();

            public RecordingPipeReader(PipeReader inner)
            {
                _inner = inner;
            }

            public int ReadCount { get; private set; }

            public List<(long Consumed, long Examined)> Advances { get; } = new List<(long, long)>();

            public int CompleteCalls { get; private set; }

            public Exception? CompleteError { get; private set; }

            public override void AdvanceTo(SequencePosition consumed)
            {
                AdvanceTo(consumed, consumed);
            }

            public Task WaitForReadsAsync(int count)
            {
                lock (this)
                {
                    if (ReadCount >= count)
                        return Task.CompletedTask;
                    _observedReads = count;
                    _readsReached = NewGate();
                    return _readsReached.Task;
                }
            }

            public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<bool>? gate;
                lock (this)
                {
                    ReadCount++;
                    gate = ReadCount >= _observedReads ? _readsReached : null;
                }

                gate?.TrySetResult(true);
                ReadResult result = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                _last = result.Buffer;
                return result;
            }

            public override bool TryRead(out ReadResult result)
            {
                result = default;
                return false;
            }

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            {
                Advances.Add((_last.Slice(0, consumed).Length, _last.Slice(0, examined).Length));
                _inner.AdvanceTo(consumed, examined);
            }

            public override void CancelPendingRead()
            {
                _inner.CancelPendingRead();
            }

            public override void Complete(Exception? exception = null)
            {
                CompleteCalls++;
                CompleteError = exception;
                _inner.Complete(exception);
            }

            public void Dispose()
            {
            }

            private static TaskCompletionSource<bool> NewGate()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
