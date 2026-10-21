using System.Net.Sockets;
using CloudIsle.Net;

namespace CloudIsle.Bot;

internal sealed class BotSession : IAsyncDisposable
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly byte[] _recv = new byte[512 * 1024];
    int _recvLen;
    int _seq;
    readonly List<(ushort msgId, int seq, byte[] body)> _inbox = new();

    public BotSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public async Task SendFireAndForgetAsync(ushort msgId, byte[] body, CancellationToken ct)
    {
        int seq = Interlocked.Increment(ref _seq);
        byte[] frame = PacketCodec.Encode(msgId, seq, body);
        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);
    }

    public async Task<T> RequestAsync<T>(
        ushort reqId,
        ushort respId,
        byte[] body,
        Func<byte[], T> parse,
        CancellationToken ct)
    {
        int seq = Interlocked.Increment(ref _seq);
        byte[] frame = PacketCodec.Encode(reqId, seq, body);
        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            for (int i = 0; i < _inbox.Count; i++)
            {
                var item = _inbox[i];
                if (item.msgId == respId && item.seq == seq)
                {
                    _inbox.RemoveAt(i);
                    return parse(item.body);
                }
            }

            await ReadMoreAsync(ct, TimeSpan.FromMilliseconds(50));
        }
        throw new TimeoutException($"timeout waiting resp {respId} seq={seq}");
    }

    public bool TryTakePush(out ushort msgId, out byte[] body)
    {
        for (int i = 0; i < _inbox.Count; i++)
        {
            var item = _inbox[i];
            bool isPush = item.msgId == MsgIds.PlazaSyncNtf
                          || item.msgId == MsgIds.PlazaCorrectNtf
                          || item.msgId == MsgIds.HeartbeatResp
                          || item.msgId == MsgIds.PlazaEntityEnterNtf
                          || item.msgId == MsgIds.PlazaEntityLeaveNtf
                          || item.seq == 0;
            if (!isPush) continue;
            _inbox.RemoveAt(i);
            msgId = item.msgId;
            body = item.body;
            return true;
        }
        msgId = 0;
        body = Array.Empty<byte>();
        return false;
    }

    public Task PumpAsync(CancellationToken ct, TimeSpan wait) => ReadMoreAsync(ct, wait);

    async Task ReadMoreAsync(CancellationToken ct, TimeSpan wait)
    {
        DecodeAvailable();
        if (_inbox.Count > 0 && !_stream.DataAvailable)
            return;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(wait);
        try
        {
            if (_recvLen >= _recv.Length - 4096)
                throw new InvalidOperationException("recv buffer overflow");
            int n = await _stream.ReadAsync(_recv.AsMemory(_recvLen, _recv.Length - _recvLen), timeoutCts.Token);
            if (n <= 0) throw new IOException("connection closed");
            _recvLen += n;
            DecodeAvailable();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DecodeAvailable();
        }
    }

    void DecodeAvailable()
    {
        while (PacketCodec.TryDecode(_recv.AsSpan(0, _recvLen), out ushort msgId, out int seq, out byte[] body, out int consumed))
        {
            Buffer.BlockCopy(_recv, consumed, _recv, 0, _recvLen - consumed);
            _recvLen -= consumed;
            _inbox.Add((msgId, seq, body));
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Close();
        return ValueTask.CompletedTask;
    }
}
