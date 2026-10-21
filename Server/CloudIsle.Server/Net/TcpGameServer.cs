using System.Net;
using System.Net.Sockets;
using CloudIsle.Server.Data;
using CloudIsle.Server.Greybox;
using CloudIsle.Server.Plaza;

namespace CloudIsle.Server.Net;

public sealed class TcpGameServer
{
    readonly IPAddress _bind;
    readonly int _port;
    readonly PlayerDb _db;
    readonly IPlazaLineManager? _plaza;
    readonly GreyboxService _greybox;

    public TcpGameServer(string host, int port, PlayerDb db, IPlazaLineManager? plaza = null, GreyboxService? greybox = null)
    {
        _bind = IPAddress.Parse(host);
        _port = port;
        _db = db;
        _plaza = plaza;
        _greybox = greybox ?? new GreyboxService(db);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_bind, _port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start(backlog: 512);
        Console.WriteLine($"[TCP] listening {_bind}:{_port} plaza={_plaza != null} greybox=True");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                client.NoDelay = true;
                client.ReceiveBufferSize = 256 * 1024;
                client.SendBufferSize = 256 * 1024;
                var session = new ClientSession(client, _db, _plaza, _greybox, ct);
                _ = Task.Run(() => session.RunAsync(), ct);
            }
        }
        finally
        {
            listener.Stop();
            Console.WriteLine("[TCP] stopped");
        }
    }
}
