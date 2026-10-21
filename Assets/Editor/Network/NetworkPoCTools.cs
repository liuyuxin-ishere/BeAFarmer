using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using CloudIsle.Net;
using CloudIsle.Proto.Poc;
using Google.Protobuf;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Editor.NetworkTools
{
    /// <summary>
    /// PoC #6：TCP 登录 + SQLite 读写验证（编辑器侧）。
    /// </summary>
    public static class NetworkPoCTools
    {
        const string DefaultHost = "127.0.0.1";
        const int DefaultPort = 17777;

        [MenuItem("Farming/Network PoC/1. Verify TCP Login+SQLite (dotnet script)")]
        public static void VerifyTcpScript()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string script = Path.Combine(projectRoot, "Tools", "verify_tcp.ps1");
            if (!File.Exists(script))
            {
                Debug.LogError($"[TCP PoC] Missing {script}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Debug.LogError($"[TCP PoC] verify FAILED ({p.ExitCode})\n{stderr}\n{stdout}");
                    return;
                }

                Debug.Log($"[TCP PoC] verify OK\n{stdout}");
            }
        }

        [MenuItem("Farming/Network PoC/2. Client Login+AddGold (server must be running)")]
        public static void ClientLoginAddGold()
        {
            _ = RunClientAsync();
        }

        static async Task RunClientAsync()
        {
            string user = $"unity_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            const string pass = "unity-poc";
            const long delta = 7;

            try
            {
                using (var session = await EditorTcpSession.ConnectAsync(DefaultHost, DefaultPort))
                {
                    var login = await session.LoginAsync(user, pass);
                    if (login.Code != ErrorCodes.Ok || login.Data == null)
                        throw new InvalidOperationException($"login failed code={login.Code}");

                    long gold1 = login.Data.Gold;
                    var add = await session.AddGoldAsync(delta);
                    if (add.Code != ErrorCodes.Ok || add.Gold != gold1 + delta)
                        throw new InvalidOperationException($"addGold failed code={add.Code} gold={add.Gold}");

                    Debug.Log(
                        $"[TCP PoC] Client OK user={user} player={login.Data.PlayerId} " +
                        $"goldBefore={gold1} goldAfter={add.Gold} serverTime={login.ServerTime}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[TCP PoC] Client FAILED: {e.GetType().Name}: {e.Message}\n" +
                    "提示：先手动启动服务端，或点菜单 1 跑完整脚本。\n" +
                    $"dotnet run --project Server/CloudIsle.Server -- --host {DefaultHost} --port {DefaultPort}");
            }
        }

        sealed class EditorTcpSession : IDisposable
        {
            readonly TcpClient _client;
            readonly NetworkStream _stream;
            readonly byte[] _recv = new byte[128 * 1024];
            int _recvLen;
            int _seq;

            EditorTcpSession(TcpClient client)
            {
                _client = client;
                _stream = client.GetStream();
            }

            public static async Task<EditorTcpSession> ConnectAsync(string host, int port)
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(host, port);
                return new EditorTcpSession(client);
            }

            public Task<LoginResp> LoginAsync(string user, string pass)
            {
                var req = new LoginReq
                {
                    Username = user,
                    Password = pass,
                    AppVersion = "0.0.1",
                    ResVersion = 1,
                    ProtoVersion = ProtoVersions.Current,
                    ConfigVersion = 1,
                    DeviceId = "unity-editor"
                };
                return RequestAsync(MsgIds.LoginReq, MsgIds.LoginResp, req, LoginResp.Parser);
            }

            public Task<PocAddGoldResp> AddGoldAsync(long delta)
            {
                var req = new PocAddGoldReq
                {
                    OpId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Delta = delta
                };
                return RequestAsync(MsgIds.PocAddGoldReq, MsgIds.PocAddGoldResp, req, PocAddGoldResp.Parser);
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
                await _stream.WriteAsync(frame, 0, frame.Length);
                await _stream.FlushAsync();

                while (true)
                {
                    if (PacketCodec.TryDecode(
                            new ReadOnlySpan<byte>(_recv, 0, _recvLen),
                            out ushort msgId,
                            out int gotSeq,
                            out byte[] body,
                            out int consumed))
                    {
                        Buffer.BlockCopy(_recv, consumed, _recv, 0, _recvLen - consumed);
                        _recvLen -= consumed;
                        if (msgId != respId || gotSeq != seq)
                            throw new InvalidOperationException($"unexpected resp msgId={msgId} seq={gotSeq}");
                        return parser.ParseFrom(body);
                    }

                    int n = await _stream.ReadAsync(_recv, _recvLen, _recv.Length - _recvLen);
                    if (n <= 0)
                        throw new IOException("connection closed");
                    _recvLen += n;
                }
            }

            public void Dispose() => _client.Close();
        }
    }
}
