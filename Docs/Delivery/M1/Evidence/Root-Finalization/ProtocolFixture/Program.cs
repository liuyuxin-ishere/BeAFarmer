using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using CloudIsle.Net;
using CloudIsle.Proto;
using CloudIsle.Server.Data;
using CloudIsle.Server.Config;
using Google.Protobuf;

// Local test fixture only. No service endpoint, production protocol or user DB
// is modified. Credentials/Token values are never printed or put in reports.
try
{
    string action = args[0];
    string root = Path.GetFullPath(args[^1]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string Owned(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path outside owned fixture root");
        return full;
    }
    object report;
    string output;
    if (action == "prepare")
    {
        string database = Owned(args[1]);
        output = Owned(args[2]);
        if (!File.Exists(database)) throw new FileNotFoundException("Existing fixture DB required");
        PlayerFullData expected;
        long wantedRevision;
        using (var store = new SqliteStore())
        {
            store.Initialize(database);
            var players = new PlayerStore(store);
            PlayerState before = await players.LoadAsync(1, CancellationToken.None);
            expected = before.Data.Clone();
            expected.Info.Gold += 1; // Actual non-default committed business marker.
            expected.Bag.Clear();
            expected.Bag.Add(new ItemStack { ItemId = 1, Count = 7 });
            wantedRevision = checked(before.Revision + 1);
            await players.SaveAsync(new PlayerState(1, expected, wantedRevision, before.SavedRevision), CancellationToken.None);
        }
        using (var reopened = new SqliteStore())
        {
            reopened.Initialize(database);
            PlayerState saved = await new PlayerStore(reopened).LoadAsync(1, CancellationToken.None);
            if (!saved.Data.Equals(expected) || saved.SavedRevision != wantedRevision || saved.Dirty)
                throw new InvalidOperationException("Real production save/reopen did not preserve the full snapshot");
            report = new { passed = true, savedRevision = saved.SavedRevision, dirty = saved.Dirty,
                businessSnapshot = JsonDocument.Parse(JsonFormatter.Default.Format(saved.Data)).RootElement.Clone() };
        }
    }
    else if (action == "reconnect")
    {
        string tokenFile = Owned(args[1]);
        int port = int.Parse(args[2]);
        int protocol = int.Parse(args[3]);
        int expectedCode = int.Parse(args[4]);
        output = Owned(args[5]);
        string token = File.ReadAllText(tokenFile).Trim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, timeout.Token);
        var request = new ReconnectReq { Token = token, ProtoVersion = protocol };
        await tcp.GetStream().WriteAsync(FrameCodec.Encode(MsgRegistry.GetId(typeof(ReconnectReq)), 91, request.ToByteArray()), timeout.Token);
        byte[] prefix = new byte[4];
        await tcp.GetStream().ReadExactlyAsync(prefix, timeout.Token);
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix));
        if (length < FrameCodec.HeaderSize || length > FrameCodec.MaxPayloadLength) throw new IOException("Invalid frame length");
        byte[] bytes = new byte[4 + length];
        prefix.CopyTo(bytes, 0);
        await tcp.GetStream().ReadExactlyAsync(bytes.AsMemory(4), timeout.Token);
        if (FrameCodec.TryDecode(bytes, out Packet packet, out int consumed) != DecodeStatus.Success
            || consumed != bytes.Length || packet.MsgId != MsgRegistry.GetId(typeof(ReconnectResp)) || packet.Seq != 91)
            throw new IOException("Wrong production response frame");
        var response = ReconnectResp.Parser.ParseFrom(packet.Body);
        if (response.Code != expectedCode || response.Data != null)
            throw new InvalidOperationException("Expected a rejection without any business snapshot");
        report = new { passed = true, code = response.Code, hasSnapshot = response.Data != null, protocol };
    }
    else if (action == "config")
    {
        string directory = Owned(args[1]);
        int version = int.Parse(args[2]);
        output = Owned(args[3]);
        var service = new ServerConfigService();
        service.Load(directory, version);
        string value = service.GetGlobal("m1_patch_message");
        if (version != 101 || value != "热更新验证成功")
            throw new InvalidOperationException("Production ServerConfigService returned the wrong patch value");
        report = new { passed = true, version, key = "m1_patch_message", value };
    }
    else throw new ArgumentException("Unknown fixture action");
    if (File.Exists(output)) throw new IOException("Evidence already exists");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS fixture: " + output);
    return 0;
}
catch (Exception error)
{
    // Never print request bodies or credential-bearing arguments.
    Console.Error.WriteLine("Fixture failed: " + error.GetType().Name + ": " + error.Message);
    return 1;
}
