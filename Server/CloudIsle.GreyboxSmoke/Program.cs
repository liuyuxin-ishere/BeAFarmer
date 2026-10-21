using System.Diagnostics;
using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using LoginReq = CloudIsle.Proto.Poc.LoginReq;
using LoginResp = CloudIsle.Proto.Poc.LoginResp;
using Google.Protobuf;

namespace CloudIsle.GreyboxSmoke;

/// <summary>控制台跑通 T02~T12 数值断言（不测 GuideUI）。</summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        string host = Get(args, "--host") ?? "127.0.0.1";
        int port = int.TryParse(Get(args, "--port"), out var p) ? p : 17777;
        string user = Get(args, "--user") ?? "greybox_smoke";
        try
        {
            await using var s = await Session.Connect(host, port);
            var sw = Stopwatch.StartNew();
            var login = await s.Login(user, "poc");
            if (login.Code != 0 || login.Data == null) throw new Exception($"login {login.Code}");
            await s.DrainSnapshot();
            int seeds = 9, wheat = 0, flour = 0;
            long gold = 100;

            // plant 0
            var plant0 = await s.Plant(0);
            Assert(plant0.Code == 0 && plant0.SeedCount == 8, "T03");
            seeds = plant0.SeedCount;
            await WaitRipe(s, 0);

            var h0 = await s.Harvest(0);
            Assert(h0.Code == 0 && h0.WheatGained == 2, "T05");
            wheat = h0.WheatCount;

            for (int i = 1; i <= 3; i++)
            {
                var pl = await s.Plant(i);
                Assert(pl.Code == 0, $"T06 plot{i}");
                seeds = pl.SeedCount;
            }
            Assert(seeds == 5, "T06 seeds");

            for (int i = 1; i <= 3; i++)
            {
                await WaitRipe(s, i);
                var h = await s.Harvest(i);
                Assert(h.Code == 0, $"T07 plot{i}");
                wheat = h.WheatCount;
            }
            Assert(wheat >= 8, "T07 wheat");

            var cs = await s.CraftStart();
            Assert(cs.Code == 0 && cs.WheatCount == wheat - 3, "T08");
            wheat = cs.WheatCount;
            await Task.Delay(8500);
            var cc = await s.CraftCollect();
            Assert(cc.Code == 0 && cc.FlourGained == 1, "T09");
            flour = cc.FlourCount;

            var os = await s.OrderSubmit();
            Assert(os.Code == 0 && os.Gold == 120 && os.Exp == 10, "T10");
            gold = os.Gold;
            var t10 = sw.Elapsed;

            var os2 = await s.OrderSubmit();
            Assert(os2.Code != 0 && os2.Gold == gold, "T11");

            // 烧掉多余小麦直到 <3，再测材料不足
            while (wheat >= GreyboxIds.RecipeWheatCost)
            {
                var burn = await s.CraftStart();
                Assert(burn.Code == 0, "T12 prep CraftStart");
                wheat = burn.WheatCount;
                await Task.Delay(8500);
                var burnC = await s.CraftCollect();
                Assert(burnC.Code == 0, "T12 prep Collect");
                flour = burnC.FlourCount;
            }

            var fail = await s.CraftStart();
            Assert(fail.Code != 0 && fail.WheatCount == wheat, "T12");

            Console.WriteLine($"[GreyboxSmoke] PASS T02-T12 loop={t10.TotalSeconds:F1}s gold={gold} wheat={wheat} flour={flour}");
            Console.WriteLine(t10.TotalSeconds <= 300 ? "[GreyboxSmoke] T13 PASS" : "[GreyboxSmoke] T13 FAIL slow");
            return t10.TotalSeconds <= 300 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GreyboxSmoke] FAIL: {ex.Message}");
            return 2;
        }
    }

    static async Task WaitRipe(Session s, int plotId)
    {
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var snap = await s.Snapshot();
            if (plotId < snap.PlotStates.Count && snap.PlotStates[plotId] == 2)
                return;
        }
        throw new Exception($"plot {plotId} not ripe");
    }

    static void Assert(bool cond, string step)
    {
        if (!cond) throw new Exception(step + " assert failed");
    }

    static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

sealed class Session : IAsyncDisposable
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly byte[] _recv = new byte[256 * 1024];
    int _recvLen;
    int _seq;

    Session(TcpClient c) { _client = c; _stream = c.GetStream(); }

    public static async Task<Session> Connect(string host, int port)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(host, port);
        return new Session(c);
    }

    public async Task<LoginResp> Login(string u, string p)
    {
        var req = new LoginReq
        {
            Username = u, Password = p, AppVersion = "0.0.1", ResVersion = 1,
            ProtoVersion = ProtoVersions.Current, ConfigVersion = 1, DeviceId = "smoke"
        };
        return await Request(MsgIds.LoginReq, MsgIds.LoginResp, req.ToByteArray(), b => LoginResp.Parser.ParseFrom(b));
    }

    public async Task DrainSnapshot()
    {
        try { await Wait(MsgIds.GreyboxSnapshotNtf, 0, GreyboxSnapshotNtf.ParseFrom, 2000, true); }
        catch { /* optional */ }
    }

    public Task<PlantResp> Plant(int plot) =>
        Request(MsgIds.PlantReq, MsgIds.PlantResp,
            new PlantReq { OpId = Op(), PlotId = plot, SeedId = GreyboxIds.WheatSeedId }.ToByteArray(),
            PlantResp.ParseFrom);

    public Task<HarvestResp> Harvest(int plot) =>
        Request(MsgIds.HarvestReq, MsgIds.HarvestResp,
            new HarvestReq { OpId = Op(), PlotId = plot }.ToByteArray(), HarvestResp.ParseFrom);

    public Task<CraftStartResp> CraftStart() =>
        Request(MsgIds.CraftStartReq, MsgIds.CraftStartResp,
            new CraftStartReq { OpId = Op(), FacilityId = 1, SlotId = 0, RecipeId = 1 }.ToByteArray(),
            CraftStartResp.ParseFrom);

    public Task<CraftCollectResp> CraftCollect() =>
        Request(MsgIds.CraftCollectReq, MsgIds.CraftCollectResp,
            new CraftCollectReq { OpId = Op(), FacilityId = 1, SlotId = 0 }.ToByteArray(),
            CraftCollectResp.ParseFrom);

    public Task<OrderSubmitResp> OrderSubmit() =>
        Request(MsgIds.OrderSubmitReq, MsgIds.OrderSubmitResp,
            new OrderSubmitReq { OpId = Op(), OrderId = 1 }.ToByteArray(), OrderSubmitResp.ParseFrom);

    public async Task<GreyboxSnapshotNtf> Snapshot()
    {
        int seq = ++_seq;
        await Send(MsgIds.GreyboxSnapshotReq, seq, Array.Empty<byte>());
        return await Wait(MsgIds.GreyboxSnapshotNtf, seq, GreyboxSnapshotNtf.ParseFrom, 5000, true);
    }

    long Op() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10 + _seq;

    async Task<T> Request<T>(ushort req, ushort resp, byte[] body, Func<byte[], T> parse)
    {
        int seq = ++_seq;
        await Send(req, seq, body);
        return await Wait(resp, seq, parse, 10000, false);
    }

    async Task Send(ushort id, int seq, byte[] body)
    {
        var frame = PacketCodec.Encode(id, seq, body);
        await _stream.WriteAsync(frame);
        await _stream.FlushAsync();
    }

    async Task<T> Wait<T>(ushort id, int seq, Func<byte[], T> parse, int timeoutMs, bool allowZero)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            while (PacketCodec.TryDecode(_recv.AsSpan(0, _recvLen), out ushort mid, out int mseq, out byte[] body, out int consumed))
            {
                Buffer.BlockCopy(_recv, consumed, _recv, 0, _recvLen - consumed);
                _recvLen -= consumed;
                if (mid == id && (mseq == seq || (allowZero && mseq == 0)))
                    return parse(body);
            }
            int n = await _stream.ReadAsync(_recv.AsMemory(_recvLen, _recv.Length - _recvLen));
            if (n <= 0) throw new IOException("closed");
            _recvLen += n;
        }
        throw new TimeoutException("wait " + id);
    }

    public ValueTask DisposeAsync()
    {
        _client.Close();
        return ValueTask.CompletedTask;
    }
}
