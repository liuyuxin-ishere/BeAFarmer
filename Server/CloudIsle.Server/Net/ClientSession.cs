using System.Net.Sockets;
using CloudIsle.Net;
using CloudIsle.Proto;
using ItemStack = CloudIsle.Proto.Poc.ItemStack;
using LoginReq = CloudIsle.Proto.Poc.LoginReq;
using LoginResp = CloudIsle.Proto.Poc.LoginResp;
using PlayerSnapshot = CloudIsle.Proto.Poc.PlayerSnapshot;
using PocAddGoldReq = CloudIsle.Proto.Poc.PocAddGoldReq;
using PocAddGoldResp = CloudIsle.Proto.Poc.PocAddGoldResp;
using PlantReq = CloudIsle.Proto.Greybox.PlantReq;
using PlantResp = CloudIsle.Proto.Greybox.PlantResp;
using HarvestReq = CloudIsle.Proto.Greybox.HarvestReq;
using HarvestResp = CloudIsle.Proto.Greybox.HarvestResp;
using CraftStartReq = CloudIsle.Proto.Greybox.CraftStartReq;
using CraftStartResp = CloudIsle.Proto.Greybox.CraftStartResp;
using CraftCollectReq = CloudIsle.Proto.Greybox.CraftCollectReq;
using CraftCollectResp = CloudIsle.Proto.Greybox.CraftCollectResp;
using OrderSubmitReq = CloudIsle.Proto.Greybox.OrderSubmitReq;
using OrderSubmitResp = CloudIsle.Proto.Greybox.OrderSubmitResp;
using GreyboxSnapshotNtf = CloudIsle.Proto.Greybox.GreyboxSnapshotNtf;
using CloudIsle.Server.Data;
using CloudIsle.Server.Greybox;
using CloudIsle.Server.Plaza;
using Google.Protobuf;

namespace CloudIsle.Server.Net;

public sealed class ClientSession : IPlazaOutbound
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly PlayerDb _db;
    readonly IPlazaLineManager? _plaza;
    readonly GreyboxService _greybox;
    readonly byte[] _recvBuffer = new byte[256 * 1024];
    int _recvLen;
    readonly CancellationToken _ct;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    PlayerRecord? _player;
    bool _inPlaza;

    public long PlayerId { get; private set; }
    public bool IsAuthed => PlayerId > 0;

    public ClientSession(
        TcpClient client,
        PlayerDb db,
        IPlazaLineManager? plaza,
        GreyboxService greybox,
        CancellationToken ct)
    {
        _client = client;
        _stream = client.GetStream();
        _db = db;
        _plaza = plaza;
        _greybox = greybox;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        string remote = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        Console.WriteLine($"[TCP] +session {remote}");
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(_recvBuffer.AsMemory(_recvLen, _recvBuffer.Length - _recvLen), _ct);
                if (n <= 0)
                    break;
                _recvLen += n;

                while (true)
                {
                    try
                    {
                        if (!PacketCodec.TryDecode(
                                _recvBuffer.AsSpan(0, _recvLen),
                                out ushort msgId,
                                out int seq,
                                out byte[] body,
                                out int consumed))
                            break;

                        Buffer.BlockCopy(_recvBuffer, consumed, _recvBuffer, 0, _recvLen - consumed);
                        _recvLen -= consumed;
                        await HandleAsync(msgId, seq, body);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"[TCP] bad frame from {remote}: {ex.Message}");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] session error {remote}: {ex.Message}");
        }
        finally
        {
            if (_inPlaza && _plaza != null)
                _plaza.Leave(PlayerId);
            Console.WriteLine($"[TCP] -session {remote} player={PlayerId}");
            _client.Close();
            _sendLock.Dispose();
        }
    }

    async Task HandleAsync(ushort msgId, int seq, byte[] body)
    {
        switch (msgId)
        {
            case MsgIds.HeartbeatReq:
                await HandleHeartbeatAsync(seq, HeartbeatReq.Parser.ParseFrom(body));
                break;
            case MsgIds.LoginReq:
                await HandleLoginAsync(seq, LoginReq.Parser.ParseFrom(body));
                break;
            case MsgIds.PocAddGoldReq:
                await HandleAddGoldAsync(seq, PocAddGoldReq.Parser.ParseFrom(body));
                break;
            case MsgIds.PlantReq:
                await HandlePlantAsync(seq, PlantReq.ParseFrom(body));
                break;
            case MsgIds.HarvestReq:
                await HandleHarvestAsync(seq, HarvestReq.ParseFrom(body));
                break;
            case MsgIds.CraftStartReq:
                await HandleCraftStartAsync(seq, CraftStartReq.ParseFrom(body));
                break;
            case MsgIds.CraftCollectReq:
                await HandleCraftCollectAsync(seq, CraftCollectReq.ParseFrom(body));
                break;
            case MsgIds.OrderSubmitReq:
                await HandleOrderSubmitAsync(seq, OrderSubmitReq.ParseFrom(body));
                break;
            case MsgIds.GreyboxSnapshotReq:
                await HandleSnapshotAsync(seq);
                break;
            case MsgIds.PlazaEnterReq:
                await HandlePlazaEnterAsync(seq, PlazaEnterReq.ParseFrom(body));
                break;
            case MsgIds.PlazaLeaveReq:
                await HandlePlazaLeaveAsync(seq);
                break;
            case MsgIds.PlazaMoveReq:
                HandlePlazaMove(PlazaMoveReq.ParseFrom(body));
                break;
            default:
                break;
        }
    }

    async Task HandleHeartbeatAsync(int seq, HeartbeatReq req)
    {
        await SendBytesAsync(MsgIds.HeartbeatResp, seq, new HeartbeatResp
        {
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }.ToByteArray());
    }

    async Task HandleLoginAsync(int seq, LoginReq req)
    {
        if (req.ProtoVersion != 0 && req.ProtoVersion != ProtoVersions.Current)
        {
            await SendAsync(MsgIds.LoginResp, seq, new LoginResp
            {
                Code = ErrorCodes.Version,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(req.Username))
        {
            await SendAsync(MsgIds.LoginResp, seq, new LoginResp
            {
                Code = ErrorCodes.AuthFailed,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            return;
        }

        var player = _db.FindByUsername(req.Username);
        if (player == null)
            player = _db.Create(req.Username, req.Password ?? "");
        else if (!string.Equals(player.Password, req.Password ?? "", StringComparison.Ordinal))
        {
            await SendAsync(MsgIds.LoginResp, seq, new LoginResp
            {
                Code = ErrorCodes.AuthFailed,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            return;
        }

        string token = Guid.NewGuid().ToString("N");
        _db.UpdateToken(player.PlayerId, token);
        _greybox.ResetForLogin(player.PlayerId);
        player = _db.FindById(player.PlayerId);
        PlayerId = player.PlayerId;
        _player = player;

        var gb = _greybox.Ensure(player.PlayerId);
        var resp = new LoginResp
        {
            Code = ErrorCodes.Ok,
            Token = token,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = new PlayerSnapshot
            {
                PlayerId = player.PlayerId,
                Nickname = player.Nickname,
                Level = Math.Max(player.Level, 1),
                Gold = gb.Gold
            }
        };
        resp.Data.Bag.Add(new ItemStack { ItemId = GreyboxIds.WheatSeedId, Count = gb.SeedCount });
        resp.Data.Flags["greybox"] = 1;

        await SendAsync(MsgIds.LoginResp, seq, resp);
        await SendBytesAsync(MsgIds.GreyboxSnapshotNtf, 0, _greybox.BuildSnapshot(gb).ToByteArray());
        Console.WriteLine($"[Login] greybox OK user={player.Username} id={player.PlayerId} seeds={gb.SeedCount} gold={gb.Gold}");
    }

    async Task HandleAddGoldAsync(int seq, PocAddGoldReq req)
    {
        if (!IsAuthed)
        {
            await SendAsync(MsgIds.PocAddGoldResp, seq, new PocAddGoldResp { Code = ErrorCodes.NotLogin });
            return;
        }
        long delta = req.Delta == 0 ? 1 : req.Delta;
        long gold = _db.AddGold(PlayerId, delta);
        await SendAsync(MsgIds.PocAddGoldResp, seq, new PocAddGoldResp { Code = ErrorCodes.Ok, Gold = gold });
    }

    async Task HandlePlantAsync(int seq, PlantReq req)
    {
        if (!IsAuthed)
        {
            await SendBytesAsync(MsgIds.PlantResp, seq, new PlantResp { Code = ErrorCodes.NotLogin }.ToByteArray());
            return;
        }
        var resp = _greybox.Plant(PlayerId, req);
        await SendBytesAsync(MsgIds.PlantResp, seq, resp.ToByteArray());
    }

    async Task HandleHarvestAsync(int seq, HarvestReq req)
    {
        if (!IsAuthed)
        {
            await SendBytesAsync(MsgIds.HarvestResp, seq, new HarvestResp { Code = ErrorCodes.NotLogin }.ToByteArray());
            return;
        }
        var resp = _greybox.Harvest(PlayerId, req);
        await SendBytesAsync(MsgIds.HarvestResp, seq, resp.ToByteArray());
    }

    async Task HandleCraftStartAsync(int seq, CraftStartReq req)
    {
        if (!IsAuthed)
        {
            await SendBytesAsync(MsgIds.CraftStartResp, seq, new CraftStartResp { Code = ErrorCodes.NotLogin }.ToByteArray());
            return;
        }
        var resp = _greybox.CraftStart(PlayerId, req);
        await SendBytesAsync(MsgIds.CraftStartResp, seq, resp.ToByteArray());
    }

    async Task HandleCraftCollectAsync(int seq, CraftCollectReq req)
    {
        if (!IsAuthed)
        {
            await SendBytesAsync(MsgIds.CraftCollectResp, seq, new CraftCollectResp { Code = ErrorCodes.NotLogin }.ToByteArray());
            return;
        }
        var resp = _greybox.CraftCollect(PlayerId, req);
        await SendBytesAsync(MsgIds.CraftCollectResp, seq, resp.ToByteArray());
    }

    async Task HandleOrderSubmitAsync(int seq, OrderSubmitReq req)
    {
        if (!IsAuthed)
        {
            await SendBytesAsync(MsgIds.OrderSubmitResp, seq, new OrderSubmitResp { Code = ErrorCodes.NotLogin }.ToByteArray());
            return;
        }
        var resp = _greybox.OrderSubmit(PlayerId, req);
        await SendBytesAsync(MsgIds.OrderSubmitResp, seq, resp.ToByteArray());
    }

    async Task HandleSnapshotAsync(int seq)
    {
        if (!IsAuthed)
            return;
        var st = _greybox.Ensure(PlayerId);
        await SendBytesAsync(MsgIds.GreyboxSnapshotNtf, seq, _greybox.BuildSnapshot(st).ToByteArray());
    }

    async Task HandlePlazaEnterAsync(int seq, PlazaEnterReq req)
    {
        if (!IsAuthed || _player == null)
        {
            await SendBytesAsync(MsgIds.PlazaEnterResp, seq, new PlazaEnterResp { Code = ErrorCodes.NotLogin }.ToByteArray());
            return;
        }
        if (_plaza == null)
        {
            await SendBytesAsync(MsgIds.PlazaEnterResp, seq, new PlazaEnterResp { Code = ErrorCodes.NotInPlaza }.ToByteArray());
            return;
        }

        var plazaPlayer = new PlazaPlayer
        {
            PlayerId = PlayerId,
            Nickname = _player.Nickname,
            Level = Math.Max(_player.Level, 7),
            Outbound = this
        };

        var result = _plaza.Enter(plazaPlayer, req.PreferLineId);
        _inPlaza = result.Code == ErrorCodes.Ok;

        var resp = new PlazaEnterResp
        {
            Code = result.Code,
            LineId = result.LineId,
            Self = result.Self
        };
        if (result.Entities != null)
            resp.Entities.AddRange(result.Entities);

        await SendBytesAsync(MsgIds.PlazaEnterResp, seq, resp.ToByteArray());
    }

    async Task HandlePlazaLeaveAsync(int seq)
    {
        if (_plaza != null && _inPlaza)
        {
            _plaza.Leave(PlayerId);
            _inPlaza = false;
        }
        await SendBytesAsync(MsgIds.PlazaLeaveResp, seq, new PlazaLeaveResp { Code = ErrorCodes.Ok }.ToByteArray());
    }

    void HandlePlazaMove(PlazaMoveReq req)
    {
        if (!_inPlaza || _plaza == null || !IsAuthed)
            return;
        _plaza.OnMove(PlayerId, new PlazaMoveInput
        {
            X = req.X,
            Y = req.Y,
            Dir = req.Dir,
            State = req.State,
            ClientTime = req.ClientTime
        });
    }

    public async ValueTask SendAsync(ushort msgId, byte[] body)
    {
        await SendBytesAsync(msgId, 0, body);
    }

    async Task SendAsync(ushort msgId, int seq, IMessage message)
    {
        await SendBytesAsync(msgId, seq, message.ToByteArray());
    }

    async Task SendBytesAsync(ushort msgId, int seq, byte[] body)
    {
        byte[] frame = PacketCodec.Encode(msgId, seq, body);
        await _sendLock.WaitAsync(_ct);
        try
        {
            await _stream.WriteAsync(frame, _ct);
            await _stream.FlushAsync(_ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
