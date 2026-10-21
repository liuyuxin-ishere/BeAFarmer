using System.Diagnostics;
using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using LoginReq = CloudIsle.Proto.Poc.LoginReq;
using LoginResp = CloudIsle.Proto.Poc.LoginResp;
using Google.Protobuf;

namespace CloudIsle.Bot;

public sealed class BotWorker
{
    public int BotIndex { get; }
    public BotState State { get; private set; } = BotState.Idle;
    public bool EnterOk { get; private set; }
    public bool ConnectedOk { get; private set; }
    public bool Dropped { get; private set; }
    public long SyncReceived { get; private set; }
    public long CorrectReceived { get; private set; }
    public readonly List<double> RttSamples = new();

    public BotWorker(int index) => BotIndex = index;

    public async Task RunAsync(BotOptions options, CancellationToken ct)
    {
        string user = $"bot_{BotIndex:000}";
        var rng = new Random(options.Seed + BotIndex);
        TcpClient? client = null;

        try
        {
            State = BotState.Connecting;
            client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(options.Host, options.Port, ct);
            ConnectedOk = true;

            await using var session = new BotSession(client);

            State = BotState.LoggingIn;
            var sw = Stopwatch.StartNew();
            var login = await session.RequestAsync(
                MsgIds.LoginReq, MsgIds.LoginResp,
                new LoginReq
                {
                    Username = user,
                    Password = options.Password,
                    AppVersion = "0.0.1",
                    ResVersion = 1,
                    ProtoVersion = ProtoVersions.Current,
                    ConfigVersion = 1,
                    DeviceId = $"bot-{BotIndex}"
                }.ToByteArray(),
                body => LoginResp.Parser.ParseFrom(body),
                ct);
            sw.Stop();
            RttSamples.Add(sw.Elapsed.TotalMilliseconds);
            if (login.Code != ErrorCodes.Ok || login.Data == null)
            {
                State = BotState.Failed;
                return;
            }

            sw.Restart();
            var enter = await session.RequestAsync(
                MsgIds.PlazaEnterReq, MsgIds.PlazaEnterResp,
                new PlazaEnterReq { PreferLineId = 0 }.ToByteArray(),
                PlazaEnterResp.ParseFrom,
                ct);
            sw.Stop();
            RttSamples.Add(sw.Elapsed.TotalMilliseconds);

            if (enter.Code != ErrorCodes.Ok || enter.Self == null)
            {
                State = BotState.Failed;
                return;
            }

            EnterOk = true;
            State = BotState.InPlaza;

            int x = enter.Self.X;
            int y = enter.Self.Y;
            int dir = enter.Self.Dir;
            var end = options.DurationSec <= 0
                ? DateTime.MaxValue
                : DateTime.UtcNow.AddSeconds(options.DurationSec);

            var moveInterval = TimeSpan.FromSeconds(1.0 / Math.Max(0.5, options.MoveHz));
            var hbInterval = TimeSpan.FromSeconds(10);
            var nextMove = DateTime.UtcNow;
            var nextHb = DateTime.UtcNow.Add(hbInterval);

            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                while (session.TryTakePush(out ushort msgId, out byte[] body))
                {
                    if (msgId == MsgIds.PlazaSyncNtf)
                    {
                        SyncReceived++;
                        PlazaSyncNtf.ParseFrom(body);
                    }
                    else if (msgId == MsgIds.PlazaCorrectNtf)
                    {
                        CorrectReceived++;
                        var c = PlazaCorrectNtf.ParseFrom(body);
                        x = c.X;
                        y = c.Y;
                    }
                }

                var now = DateTime.UtcNow;
                if (now >= nextHb)
                {
                    await session.SendFireAndForgetAsync(
                        MsgIds.HeartbeatReq,
                        new HeartbeatReq { ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToByteArray(),
                        ct);
                    nextHb = now.Add(hbInterval);
                }

                if (now >= nextMove)
                {
                    State = BotState.Moving;
                    dir = rng.Next(0, 360);
                    double rad = dir * Math.PI / 180.0;
                    int step = rng.Next(60, 160);
                    x = Clamp(x + (int)(Math.Cos(rad) * step), 0, options.MapSizeCm);
                    y = Clamp(y + (int)(Math.Sin(rad) * step), 0, options.MapSizeCm);

                    await session.SendFireAndForgetAsync(
                        MsgIds.PlazaMoveReq,
                        new PlazaMoveReq
                        {
                            X = x,
                            Y = y,
                            Dir = dir,
                            State = 1,
                            ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }.ToByteArray(),
                        ct);
                    nextMove = now.Add(moveInterval);
                }

                await session.PumpAsync(ct, TimeSpan.FromMilliseconds(25));
            }

            try
            {
                await session.RequestAsync(
                    MsgIds.PlazaLeaveReq, MsgIds.PlazaLeaveResp,
                    new PlazaLeaveReq().ToByteArray(),
                    PlazaLeaveResp.ParseFrom,
                    CancellationToken.None);
            }
            catch
            {
                // ignore leave errors at shutdown
            }

            State = BotState.Disconnected;
        }
        catch (OperationCanceledException)
        {
            State = BotState.Disconnected;
        }
        catch
        {
            if (EnterOk) Dropped = true;
            State = BotState.Failed;
        }
        finally
        {
            try { client?.Close(); } catch { /* ignore */ }
        }
    }

    static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
}
