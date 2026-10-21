using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;

namespace CloudIsle.Server.Network
{
    /// <summary>
    /// 从 PipeReader 里每次取出一帧。正文在 AdvanceTo 之前拷贝进 Packet。
    /// </summary>
    public static class PipelineFrameReader
    {
        /// <summary>
        /// 持续读帧并调用业务回调。半包等待新数据；长度非法或结束时仍有半帧则失败。
        /// 取消和回调异常都会 CompleteAsync，并把同一个异常传出去。
        /// </summary>
        /// <param name="reader">管道读取端。</param>
        /// <param name="onPacket">每取出一帧后调用。返回后才继续读下一帧。</param>
        /// <param name="ct">取消读取。</param>
        public static async Task ReadLoopAsync(
            PipeReader reader,
            Func<Packet, Task> onPacket,
            CancellationToken ct)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (onPacket == null)
                throw new ArgumentNullException(nameof(onPacket));

            Exception? error = null;
            try
            {
                while (true)
                {
                    ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    if (result.IsCanceled || ct.IsCancellationRequested)
                    {
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        throw new OperationCanceledException(ct);
                    }

                    FrameTake take = TryTakeOne(buffer, out Packet packet, out SequencePosition frameEnd);
                    if (take == FrameTake.Success)
                    {
                        reader.AdvanceTo(frameEnd, frameEnd);
                        await onPacket(packet).ConfigureAwait(false);
                        continue;
                    }

                    bool hasPartialFrame = buffer.Length > 0;
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (take == FrameTake.Invalid)
                        throw new InvalidDataException("帧长度非法");

                    if (result.IsCompleted)
                    {
                        if (hasPartialFrame)
                            throw new InvalidDataException("连接结束时仍有未完成的帧");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                await reader.CompleteAsync(error).ConfigureAwait(false);
            }
        }

        /// <summary>只看缓冲最前面的一帧。长度不够 4 字节或整帧未到齐时不分配正文。</summary>
        /// <param name="buffer">当前已经读到的字节。</param>
        /// <param name="packet">成功时得到复制出来的包。</param>
        /// <param name="frameEnd">成功时指向这一帧的结尾，供 AdvanceTo 使用。</param>
        private static FrameTake TryTakeOne(
            ReadOnlySequence<byte> buffer,
            out Packet packet,
            out SequencePosition frameEnd)
        {
            packet = default;
            frameEnd = buffer.Start;

            if (buffer.Length < 4)
                return FrameTake.NeedMore;

            Span<byte> lengthBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(lengthBytes);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (payloadLength < FrameCodec.HeaderSize || payloadLength > FrameCodec.MaxPayloadLength)
                return FrameTake.Invalid;

            long frameLength = 4L + payloadLength;
            if (buffer.Length < frameLength)
                return FrameTake.NeedMore;

            Span<byte> header = stackalloc byte[FrameCodec.HeaderSize];
            buffer.Slice(4, FrameCodec.HeaderSize).CopyTo(header);
            ushort msgId = BinaryPrimitives.ReadUInt16LittleEndian(header);
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(2));

            int bodyLength = (int)payloadLength - FrameCodec.HeaderSize;
            byte[] body = bodyLength == 0 ? Array.Empty<byte>() : new byte[bodyLength];
            if (bodyLength > 0)
                buffer.Slice(4 + FrameCodec.HeaderSize, bodyLength).CopyTo(body);

            packet = new Packet(msgId, seq, body);
            frameEnd = buffer.GetPosition(frameLength);
            return FrameTake.Success;
        }

        private enum FrameTake
        {
            NeedMore,
            Success,
            Invalid
        }
    }
}
