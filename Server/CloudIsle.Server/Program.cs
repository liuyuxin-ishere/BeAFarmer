using CloudIsle.Config;
using CloudIsle.LubanCfg;
using CloudIsle.Proto;
using LoginReq = CloudIsle.Proto.Poc.LoginReq;
using CloudIsle.Rules;
using CloudIsle.Server.Data;
using CloudIsle.Server.Greybox;
using CloudIsle.Server.Net;
using CloudIsle.Server.Plaza;
using Google.Protobuf;
using Luban.SimpleJSON;

namespace CloudIsle.Server;

/// <summary>
/// PoC #5：`--shared-smoke` 同源编译冒烟。
/// PoC #6：默认启动 TCP 监听 + 登录 + SQLite。
/// PoC #7：`--config-smoke` 读取自写导表 JSON。
/// PoC #7b：`--luban-smoke` 读取 Luban 导出。
/// PoC #10：广场模块默认开启（可用 `--no-plaza` 关闭）。
/// </summary>
internal static class Program
{
    const string DefaultHost = "127.0.0.1";
    const int DefaultPort = 17777;

    private static async Task<int> Main(string[] args)
    {
        // 正式M1组合入口由run_server.ps1明确选择；不复用旧PoC消息/数据库。
        if (args.Any(a => string.Equals(a, "--m1", StringComparison.OrdinalIgnoreCase)))
            return await Hosting.M1ServerCommand.RunAsync(args);
        if (args.Any(a => string.Equals(a, "--shared-smoke", StringComparison.OrdinalIgnoreCase)))
            return RunSharedSmoke();
        if (args.Any(a => string.Equals(a, "--config-smoke", StringComparison.OrdinalIgnoreCase)))
            return RunConfigSmoke(GetArg(args, "--config-dir"));
        if (args.Any(a => string.Equals(a, "--luban-smoke", StringComparison.OrdinalIgnoreCase)))
            return RunLubanSmoke(GetArg(args, "--luban-dir"));

        string host = GetArg(args, "--host") ?? DefaultHost;
        int port = int.TryParse(GetArg(args, "--port"), out int p) ? p : DefaultPort;
        string dbPath = GetArg(args, "--db") ?? Path.Combine(AppContext.BaseDirectory, "data", "poc.db");
        bool plazaEnabled = !args.Any(a => string.Equals(a, "--no-plaza", StringComparison.OrdinalIgnoreCase));
        int tickMs = int.TryParse(GetArg(args, "--plaza-tick-ms"), out int t) ? t : 100;
        int lineCap = int.TryParse(GetArg(args, "--plaza-line-cap"), out int c) ? c : 100;

        Console.WriteLine(
            $"[Server] CloudIsle PoC host={host} port={port} db={dbPath} plaza={plazaEnabled} tick={tickMs}ms cap={lineCap}");

        using var db = new PlayerDb(dbPath);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        PlazaLineManager? plaza = null;
        if (plazaEnabled)
            plaza = new PlazaLineManager(lineCap, tickMs);
        var greybox = new GreyboxService(db);

        try
        {
            var server = new TcpGameServer(host, port, db, plaza, greybox);
            await server.RunAsync(cts.Token);
            return 0;
        }
        finally
        {
            plaza?.Dispose();
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

    static int RunSharedSmoke()
    {
        try
        {
            var req = new LoginReq
            {
                Username = "shared_poc_server",
                Password = "n/a",
                AppVersion = "0.0.1",
                ResVersion = 1,
                ProtoVersion = 1,
                ConfigVersion = 1,
                DeviceId = "server-console"
            };
            byte[] reqBytes = req.ToByteArray();
            var req2 = LoginReq.Parser.ParseFrom(reqBytes);
            if (req2.Username != req.Username)
                throw new InvalidOperationException("LoginReq round-trip failed");

            const long plant = 1_700_000_000_000L;
            long ready = TimeRules.CalcReadyTimeMs(plant, growSeconds: 60);
            if (ready != plant + 60_000L || !TimeRules.IsReady(ready, ready))
                throw new InvalidOperationException("TimeRules failed");

            Console.WriteLine(
                $"[Shared PoC] Server OK protoBytes={reqBytes.Length} user={req2.Username} readyMs={ready}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Shared PoC] Server FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static int RunConfigSmoke(string? configDir)
    {
        try
        {
            string dir = string.IsNullOrEmpty(configDir)
                ? ConfigPaths.ResolveFromBaseDir(AppContext.BaseDirectory)
                : configDir;
            var tables = ConfigTables.LoadFromDirectory(dir);
            int bag = tables.Global.GetInt("bag_init_slots");
            var wheat = tables.Item.Get(1001);
            var crop = tables.Crop.Get("C01");
            if (wheat.Name != "小麦")
                throw new InvalidOperationException($"Item 1001 name={wheat.Name}");
            if (crop.GrowSec <= 0)
                throw new InvalidOperationException("Crop C01 growSec invalid");

            Console.WriteLine(
                $"[Config PoC] Server OK dir={dir} tables={tables.Item.Count}+… " +
                $"bag_init_slots={bag} item1001={wheat.Name}/{wheat.SellPrice} cropC01.growSec={crop.GrowSec}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config PoC] Server FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static int RunLubanSmoke(string? configDir)
    {
        try
        {
            string dir = string.IsNullOrEmpty(configDir)
                ? ResolveLubanDir()
                : configDir;
            var tables = new Tables(name =>
            {
                string path = Path.Combine(dir, name + ".json");
                string text = File.ReadAllText(path);
                return JSON.Parse(text);
            });

            int bag = tables.TbGlobal.BagInitSlots;
            var wheat = tables.TbItem.Get(1001);
            var crop = tables.TbCrop.Get(1);
            int toolCount = tables.TbTool.DataList.Count;
            if (wheat.Name != "小麦")
                throw new InvalidOperationException($"Item 1001 name={wheat.Name}");
            if (crop.GrowSec <= 0 || crop.StageRatio.Count != 4)
                throw new InvalidOperationException("Crop id=1 stageRatio invalid");

            Console.WriteLine(
                $"[Luban PoC] Server OK dir={dir} bag_init_slots={bag} " +
                $"item1001={wheat.Name}/{wheat.SellPrice} crop1.growSec={crop.GrowSec} " +
                $"stageRatio={crop.StageRatio.Count} tools={toolCount}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Luban PoC] Server FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static string ResolveLubanDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string cand = Path.Combine(dir.FullName, "ConfigLuban");
            if (File.Exists(Path.Combine(cand, "tbglobal.json")))
                return cand;
            string fromRoot = Path.Combine(dir.FullName, "Server", "ConfigLuban");
            if (File.Exists(Path.Combine(fromRoot, "tbglobal.json")))
                return fromRoot;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("ConfigLuban not found");
    }
}
