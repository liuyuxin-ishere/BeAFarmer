using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto.Poc;
using Google.Protobuf;

namespace CloudIsle.PocClient;

/// <summary>
/// PoC #6 控制台客户端：TCP 登录 → 读 gold → AddGold 写库 → 重连校验落库。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        string host = GetArg(args, "--host") ?? "127.0.0.1";
        int port = int.TryParse(GetArg(args, "--port"), out int p) ? p : 17777;
        string user = GetArg(args, "--user") ?? $"poc_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        string pass = GetArg(args, "--pass") ?? "poc-pass";
        long delta = long.TryParse(GetArg(args, "--delta"), out long d) ? d : 10;

        try
        {
            Console.WriteLine($"[PocClient] connect {host}:{port} user={user}");

            await using var session1 = await TcpPocSession.ConnectAsync(host, port);
            var login1 = await session1.LoginAsync(user, pass);
            if (login1.Code != ErrorCodes.Ok || login1.Data == null)
                throw new InvalidOperationException($"login1 failed code={login1.Code}");

            long gold1 = login1.Data.Gold;
            Console.WriteLine($"[PocClient] login1 OK player={login1.Data.PlayerId} gold={gold1} token={login1.Token[..8]}...");

            var add = await session1.AddGoldAsync(delta);
            if (add.Code != ErrorCodes.Ok)
                throw new InvalidOperationException($"addGold failed code={add.Code}");
            if (add.Gold != gold1 + delta)
                throw new InvalidOperationException($"addGold mismatch expect={gold1 + delta} actual={add.Gold}");
            Console.WriteLine($"[PocClient] addGold OK gold={add.Gold}");

            await session1.DisposeAsync();

            await using var session2 = await TcpPocSession.ConnectAsync(host, port);
            var login2 = await session2.LoginAsync(user, pass);
            if (login2.Code != ErrorCodes.Ok || login2.Data == null)
                throw new InvalidOperationException($"login2 failed code={login2.Code}");
            if (login2.Data.Gold != add.Gold)
                throw new InvalidOperationException(
                    $"persist mismatch expect={add.Gold} actual={login2.Data.Gold}");

            Console.WriteLine(
                $"[TCP PoC] Client OK user={user} goldBefore={gold1} goldAfterWrite={add.Gold} goldAfterRelogin={login2.Data.Gold}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TCP PoC] Client FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}

internal sealed class TcpPocSession : IAsyncDisposable
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly byte[] _recv = new byte[128 * 1024];
    int _recvLen;
    int _seq;

    TcpPocSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<TcpPocSession> ConnectAsync(string host, int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port);
        return new TcpPocSession(client);
    }

    public async Task<LoginResp> LoginAsync(string user, string pass)
    {
        var req = new LoginReq
        {
            Username = user,
            Password = pass,
            AppVersion = "0.0.1",
            ResVersion = 1,
            ProtoVersion = ProtoVersions.Current,
            ConfigVersion = 1,
            DeviceId = "poc-client"
        };
        return await RequestAsync(MsgIds.LoginReq, MsgIds.LoginResp, req, LoginResp.Parser);
    }

    public async Task<PocAddGoldResp> AddGoldAsync(long delta)
    {
        var req = new PocAddGoldReq { OpId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Delta = delta };
        return await RequestAsync(MsgIds.PocAddGoldReq, MsgIds.PocAddGoldResp, req, PocAddGoldResp.Parser);
    }

    async Task<TResp> RequestAsync<TReq, TResp>(
        ushort reqId,
        ushort respId,
        TReq req,
        MessageParser<TResp> parser)
        where TReq : IMessage
        where TResp : IMessage<TResp>
    {
        int seq = ++_seq;
        byte[] frame = PacketCodec.Encode(reqId, seq, req.ToByteArray());
        await _stream.WriteAsync(frame);
        await _stream.FlushAsync();

        while (true)
        {
            if (PacketCodec.TryDecode(_recv.AsSpan(0, _recvLen), out ushort msgId, out int gotSeq, out byte[] body, out int consumed))
            {
                Buffer.BlockCopy(_recv, consumed, _recv, 0, _recvLen - consumed);
                _recvLen -= consumed;
                if (msgId != respId || gotSeq != seq)
                    throw new InvalidOperationException($"unexpected resp msgId={msgId} seq={gotSeq}");
                return parser.ParseFrom(body);
            }

            int n = await _stream.ReadAsync(_recv.AsMemory(_recvLen, _recv.Length - _recvLen));
            if (n <= 0)
                throw new IOException("connection closed while waiting response");
            _recvLen += n;
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Close();
        return ValueTask.CompletedTask;
    }
}
