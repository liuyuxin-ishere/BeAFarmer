using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto;
using LoginReq = CloudIsle.Proto.Poc.LoginReq;
using LoginResp = CloudIsle.Proto.Poc.LoginResp;
using Google.Protobuf;
using UnityEngine;

namespace Game.Demo.Greybox
{
    /// <summary>
    /// 灰盒 TCP：Connect 后启动独立收包循环；请求只发+等 inbox，不再每帧 Pump 排队。
    /// </summary>
    public sealed class GreyboxNetClient : IDisposable
    {
        TcpClient _client;
        NetworkStream _stream;
        readonly byte[] _recv = new byte[256 * 1024];
        int _recvLen;
        int _seq;
        readonly List<(ushort msgId, int seq, byte[] body)> _inbox = new();
        readonly object _gate = new();
        readonly object _sendGate = new();
        CancellationTokenSource _recvCts;
        Task _recvLoop;

        public bool IsConnected => _client != null && _client.Connected;
        public long PlayerId { get; private set; }
        public string LastError { get; private set; } = "";
        public string DebugPhase { get; private set; } = "";

        public async Task ConnectAsync(string host, int port)
        {
            Dispose();
            DebugPhase = $"Connect {host}:{port}";
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _client.GetStream();
            _recvLen = 0;
            _seq = 0;
            _recvCts = new CancellationTokenSource();
            _recvLoop = Task.Run(() => RecvLoopAsync(_recvCts.Token));
            DebugPhase = "Connected+RecvLoop";
            Debug.Log($"[GreyboxNet] connected {host}:{port}");
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
                DeviceId = "greybox-unity"
            };
            DebugPhase = "wait LoginResp";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await RequestAsync(MsgIds.LoginReq, MsgIds.LoginResp, req.ToByteArray(),
                b => LoginResp.Parser.ParseFrom(b)).ConfigureAwait(false);
            if (resp.Code == ErrorCodes.Ok && resp.Data != null)
                PlayerId = resp.Data.PlayerId;
            DebugPhase = $"LoginResp code={resp.Code} {sw.ElapsedMilliseconds}ms";
            Debug.Log($"[GreyboxNet] LoginResp code={resp.Code} id={PlayerId} {sw.ElapsedMilliseconds}ms");
            return resp;
        }

        public Task<PlantResp> PlantAsync(int plotId, int seedId) =>
            TimedRequest("Plant", MsgIds.PlantReq, MsgIds.PlantResp,
                new PlantReq { OpId = NextOp(), PlotId = plotId, SeedId = seedId }.ToByteArray(),
                PlantResp.ParseFrom);

        public Task<HarvestResp> HarvestAsync(int plotId) =>
            TimedRequest("Harvest", MsgIds.HarvestReq, MsgIds.HarvestResp,
                new HarvestReq { OpId = NextOp(), PlotId = plotId }.ToByteArray(),
                HarvestResp.ParseFrom);

        public Task<CraftStartResp> CraftStartAsync() =>
            TimedRequest("CraftStart", MsgIds.CraftStartReq, MsgIds.CraftStartResp,
                new CraftStartReq
                {
                    OpId = NextOp(),
                    FacilityId = GreyboxIds.MillFacilityId,
                    SlotId = 0,
                    RecipeId = GreyboxIds.FlourRecipeId
                }.ToByteArray(),
                CraftStartResp.ParseFrom);

        public Task<CraftCollectResp> CraftCollectAsync() =>
            TimedRequest("CraftCollect", MsgIds.CraftCollectReq, MsgIds.CraftCollectResp,
                new CraftCollectReq
                {
                    OpId = NextOp(),
                    FacilityId = GreyboxIds.MillFacilityId,
                    SlotId = 0
                }.ToByteArray(),
                CraftCollectResp.ParseFrom);

        public Task<OrderSubmitResp> OrderSubmitAsync() =>
            TimedRequest("OrderSubmit", MsgIds.OrderSubmitReq, MsgIds.OrderSubmitResp,
                new OrderSubmitReq { OpId = NextOp(), OrderId = GreyboxIds.OrderId }.ToByteArray(),
                OrderSubmitResp.ParseFrom);

        async Task<T> TimedRequest<T>(string name, ushort reqId, ushort respId, byte[] body, Func<byte[], T> parse)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log($"[GreyboxNet] >> {name} send");
            var resp = await RequestAsync(reqId, respId, body, parse).ConfigureAwait(false);
            Debug.Log($"[GreyboxNet] << {name} done {sw.ElapsedMilliseconds}ms");
            return resp;
        }

        public async Task<GreyboxSnapshotNtf> RefreshSnapshotAsync()
        {
            int seq = Interlocked.Increment(ref _seq);
            SendRaw(MsgIds.GreyboxSnapshotReq, seq, Array.Empty<byte>());
            return await WaitInboxAsync(MsgIds.GreyboxSnapshotNtf, seq, GreyboxSnapshotNtf.ParseFrom, allowSeqZero: true)
                .ConfigureAwait(false);
        }

        public bool TryTakeSnapshot(out GreyboxSnapshotNtf ntf)
        {
            lock (_gate)
            {
                for (int i = 0; i < _inbox.Count; i++)
                {
                    if (_inbox[i].msgId != MsgIds.GreyboxSnapshotNtf) continue;
                    ntf = GreyboxSnapshotNtf.ParseFrom(_inbox[i].body);
                    _inbox.RemoveAt(i);
                    return true;
                }
            }
            ntf = null;
            return false;
        }

        /// <summary>已由 RecvLoop 收包；保留空实现以免旧调用编译失败。</summary>
        public Task PumpAsync(float timeoutSec = 0.05f) => Task.CompletedTask;

        long NextOp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + (Interlocked.Increment(ref _seq) % 1000);

        async Task<T> RequestAsync<T>(ushort reqId, ushort respId, byte[] body, Func<byte[], T> parse)
        {
            int seq = Interlocked.Increment(ref _seq);
            SendRaw(reqId, seq, body);
            return await WaitInboxAsync(respId, seq, parse, allowSeqZero: false).ConfigureAwait(false);
        }

        void SendRaw(ushort msgId, int seq, byte[] body)
        {
            if (_stream == null) throw new IOException("not connected");
            byte[] frame = PacketCodec.Encode(msgId, seq, body);
            lock (_sendGate)
            {
                _stream.Write(frame, 0, frame.Length);
                _stream.Flush();
            }
            Debug.Log($"[GreyboxNet] send msgId={msgId} seq={seq} len={frame.Length}");
        }

        async Task<T> WaitInboxAsync<T>(ushort respId, int seq, Func<byte[], T> parse, bool allowSeqZero)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _inbox.Count; i++)
                    {
                        var item = _inbox[i];
                        bool match = item.msgId == respId && (item.seq == seq || (allowSeqZero && item.seq == 0));
                        if (!match) continue;
                        _inbox.RemoveAt(i);
                        return parse(item.body);
                    }
                }
                await Task.Delay(5).ConfigureAwait(false);
            }
            int n;
            lock (_gate) n = _inbox.Count;
            throw new TimeoutException($"timeout waiting {respId} seq={seq} inbox={n}");
        }

        async Task RecvLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    int space = _recv.Length - _recvLen;
                    if (space <= 0)
                    {
                        LastError = "recv buffer full";
                        break;
                    }
                    int n = await _stream.ReadAsync(_recv, _recvLen, space, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    _recvLen += n;
                    DecodeAll();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogWarning("[GreyboxNet] recv loop end: " + ex.Message);
            }
        }

        void DecodeAll()
        {
            while (PacketCodec.TryDecode(_recv.AsSpan(0, _recvLen), out ushort msgId, out int seq, out byte[] body, out int consumed))
            {
                Buffer.BlockCopy(_recv, consumed, _recv, 0, _recvLen - consumed);
                _recvLen -= consumed;
                lock (_gate) _inbox.Add((msgId, seq, body));
                Debug.Log($"[GreyboxNet] recv msgId={msgId} seq={seq} body={body.Length}");
            }
        }

        public void Dispose()
        {
            try { _recvCts?.Cancel(); } catch { /* ignore */ }
            try { _client?.Close(); } catch { /* ignore */ }
            _recvCts = null;
            _recvLoop = null;
            _client = null;
            _stream = null;
        }
    }
}
