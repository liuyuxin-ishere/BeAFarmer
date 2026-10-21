using System;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.Lua;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 正式启动场景宿主：挂 Launcher.RunAsync。
    /// 验收模式：真实 LoginWindow 输入/按钮（与产品同一 Lua AccountService），禁止 C# bypass Commit。
    /// 凭证仅读 accept-root 下私有文件，不经 CLI；日志不写密码/Token。
    /// </summary>
    public sealed class LauncherBootBehaviour : MonoBehaviour
    {
        public const string AcceptRootArg = "--m1-accept-root=";
        public const string AcceptHttpArg = "--m1-accept-http=";
        public const string AcceptTcpHostArg = "--m1-accept-tcp-host=";
        public const string AcceptTcpPortArg = "--m1-accept-tcp-port=";
        public const string AcceptAutoDropArg = "--m1-accept-auto-drop";
        public const string AcceptUiLoginArg = "--m1-accept-ui-login";
        public const string CredsFileName = "creds.private";

        Launcher _launcher;
        CancellationTokenSource _cts;
        string _acceptRoot;
        bool _acceptMode;
        bool _autoDropOnce;
        bool _uiLogin;
        bool _loginStarted;
        bool _dropArmed;
        bool _restoreWatch;
        bool _sawMaskAfterDrop;
        bool _restoreObserved;
        float _dropAt = -1f;
        string _expectedNick = "";
        int _pumpErrCount;
        float _pumpErrLastLog = -999f;
        AcceptLocalDriver _acceptDriver;
        readonly StringBuilder _obs = new StringBuilder(8192);

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ParseArgs();
            if (_acceptMode) Application.runInBackground = true;
            Obs("boot-awake app={0} bridge={1} proto={2}",
                Application.version, BridgeApi.BridgeContractValue, BridgeApi.RequiredProtoVersion);
        }

        void Start()
        {
            _cts = new CancellationTokenSource();
            RunBoot(_cts.Token).Forget();
        }

        void Update()
        {
            try
            {
                AccountBridge.Pump(64);
            }
            catch (Exception ex)
            {
                // 不可空吞：脱敏记录 + 限流，保证 Console 验收能看见真实错误。
                _pumpErrCount++;
                if (Time.realtimeSinceStartup - _pumpErrLastLog >= 2f)
                {
                    _pumpErrLastLog = Time.realtimeSinceStartup;
                    string msg = Sanitize(ex.GetBaseException().Message ?? ex.GetType().Name);
                    Obs("pump-error count={0} msg={1}", _pumpErrCount, msg);
                    Debug.LogError("[M1Accept] pump-error " + msg);
                }
            }

            if (_acceptMode && _acceptDriver != null)
            {
                try { _acceptDriver.Tick(); }
                catch (Exception ex)
                {
                    Obs("accept-driver-ex {0}", Sanitize(ex.GetBaseException().Message));
                }
            }

            if (!_acceptMode || !_autoDropOnce)
                return;

            if (_restoreWatch && !_restoreObserved)
            {
                if (!_sawMaskAfterDrop && AccountBridge.IsDisconnectMaskVisible())
                {
                    _sawMaskAfterDrop = true;
                    Obs("accept-mask-visible");
                }

                if (_sawMaskAfterDrop
                    && AccountBridge.IsAuthenticated()
                    && !AccountBridge.IsDisconnectMaskVisible())
                {
                    string luaNick = ReadLuaNickname();
                    string hudNick = ReadHudNickname();
                    string json = RedactSnapshot(AccountBridge.LastSnapshotJson() ?? "{}");
                    bool nickOk = (!string.IsNullOrEmpty(_expectedNick)
                                   && luaNick.IndexOf(_expectedNick, StringComparison.Ordinal) >= 0
                                   && hudNick.IndexOf("POLLUTED", StringComparison.Ordinal) < 0
                                   && (hudNick.IndexOf(_expectedNick, StringComparison.Ordinal) >= 0
                                       || string.Equals(hudNick, _expectedNick, StringComparison.Ordinal)));
                    Obs("accept-restore-ok auth={0} mask={1} luaNick={2} hudNick={3} nickMatch={4} snap={5}",
                        AccountBridge.IsAuthenticated(),
                        AccountBridge.IsDisconnectMaskVisible(),
                        luaNick,
                        hudNick,
                        nickOk,
                        json);
                    WriteBizSnapshot("restore", json, luaNick, hudNick);
                    _restoreObserved = true;
                    _restoreWatch = false;
                    FlushObs();
                }

                return;
            }

            if (_dropArmed)
                return;
            if (!AccountBridge.IsAuthenticated())
                return;
            if (_dropAt < 0f)
            {
                _dropAt = Time.realtimeSinceStartup + 2f;
                Obs("accept-drop-armed");
                return;
            }

            if (Time.realtimeSinceStartup < _dropAt)
                return;
            _dropArmed = true;
            // 污染 Lua 模型：恢复后须被服务端快照覆盖。
            TryPolluteLuaModel();
            Obs("accept-drop-socket maskBefore={0} pollutedNick={1}",
                AccountBridge.IsDisconnectMaskVisible(), ReadLuaNickname());
            AccountBridge.DropLiveSocketForAcceptance();
            _restoreWatch = true;
            _sawMaskAfterDrop = false;
            _restoreObserved = false;
        }

        void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { }
            try { _launcher?.Dispose(); } catch (Exception ex)
            {
                Obs("dispose-ex {0}", Sanitize(ex.GetBaseException().Message));
            }

            if (_launcher != null)
                Obs("disposeError empty={0} raw={1}",
                    string.IsNullOrEmpty(_launcher.DisposeError),
                    Sanitize(_launcher.DisposeError ?? ""));
            FlushObs();
        }

        async UniTaskVoid RunBoot(CancellationToken ct)
        {
            try
            {
                string http = ArgValue(AcceptHttpArg) ?? Launcher.DefaultHttpBase;
                string tcpHost = ArgValue(AcceptTcpHostArg) ?? Launcher.DefaultTcpHost;
                int tcpPort = ArgInt(AcceptTcpPortArg, Launcher.DefaultTcpPort);
                string cacheRoot = _acceptMode ? Path.Combine(_acceptRoot, "yoo") : null;
                string indexRoot = _acceptMode ? Path.Combine(_acceptRoot, "index") : null;
                string tokenDir = _acceptMode ? Path.Combine(_acceptRoot, "tokens") : null;
                Obs("launcher-start http={0} tcp={1}:{2}", http, tcpHost, tcpPort);

                _launcher = new Launcher(
                    httpBase: http,
                    cacheRoot: cacheRoot,
                    indexRoot: indexRoot,
                    tcpHost: tcpHost,
                    tcpPort: tcpPort,
                    tokenDir: tokenDir,
                    log: s => Obs("launcher {0}", s));
                // Boot failure can wait for Retry. The private test driver must
                // already be consuming commands while RunAsync is suspended.
                if (_acceptMode && !string.IsNullOrEmpty(_acceptRoot))
                {
                    _acceptDriver = new AcceptLocalDriver(
                        _acceptRoot, _launcher, s => Obs("{0}", s), _expectedNick, ct);
                    Obs("accept-driver-ready");
                }
                await _launcher.RunAsync(ct);
                Obs("launcher-ready loginOpened={0} entry={1} committed={2}",
                    _launcher.LoginOpened, _launcher.EntryReady, _launcher.Committed);

                if (_acceptMode && _uiLogin && !_loginStarted)
                {
                    _loginStarted = true;
                    await AcceptUiLoginAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                Obs("launcher-cancel");
            }
            catch (Exception ex)
            {
                Obs("launcher-fail {0}", Sanitize(Safe(ex)));
                Debug.LogException(ex);
            }
            finally
            {
                FlushObs();
            }
        }

        /// <summary>
        /// 真实 LoginWindow：与产品同一 SubscribeClick→AccountService.register/login→apply_and_commit→EnterHome。
        /// 凭证仅来自 accept-root/creds.private（三行 user/pass/nick），缺则进程内生成并写回私有文件。
        /// </summary>
        async UniTask AcceptUiLoginAsync(CancellationToken ct)
        {
            string user, pass, nick;
            if (!TryLoadCreds(out user, out pass, out nick))
            {
                user = "u" + Guid.NewGuid().ToString("N").Substring(0, 8);
                pass = "P" + Guid.NewGuid().ToString("N").Substring(0, 10);
                nick = "Nick" + Guid.NewGuid().ToString("N").Substring(0, 6);
                TrySaveCreds(user, pass, nick);
            }

            _expectedNick = nick ?? "";
            Obs("accept-ui-login-start user={0} nick={1}", user, nick);

            GameObject login = null;
            float until = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < until && login == null)
            {
                ct.ThrowIfCancellationRequested();
                login = GameObject.Find("Win_Login");
                if (login == null)
                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            if (login == null)
            {
                Obs("accept-ui-login-fail no-Win_Login");
                return;
            }

            SetInput(login, "Username", user);
            SetInput(login, "Password", pass);
            SetInput(login, "Nickname", nick);
            Obs("accept-ui-filled");
            await UniTask.Yield(PlayerLoopTiming.Update, ct);
            if (!ClickChild(login, "RegisterButton"))
            {
                Obs("accept-ui-login-fail no-RegisterButton");
                return;
            }

            until = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < until)
            {
                ct.ThrowIfCancellationRequested();
                string err = GetText(login, "ErrorLabel") ?? "";
                if (err.IndexOf("注册成功", StringComparison.Ordinal) >= 0
                    || err.IndexOf("请登录", StringComparison.Ordinal) >= 0)
                    break;
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            Obs("accept-ui-register-done label={0}", Trunc(GetText(login, "ErrorLabel"), 40));
            SetInput(login, "Password", pass);
            if (!ClickChild(login, "LoginButton"))
            {
                Obs("accept-ui-login-fail no-LoginButton");
                return;
            }

            until = Time.realtimeSinceStartup + 45f;
            GameObject home = null;
            while (Time.realtimeSinceStartup < until)
            {
                ct.ThrowIfCancellationRequested();
                home = GameObject.Find("Win_HomeHud");
                if (home != null && home.activeInHierarchy && AccountBridge.IsAuthenticated())
                    break;
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            string luaNick = ReadLuaNickname();
            string hudNick = ReadHudNickname();
            string snap = RedactSnapshot(AccountBridge.LastSnapshotJson() ?? "{}");
            bool ok = home != null && AccountBridge.IsAuthenticated()
                      && luaNick.IndexOf(nick, StringComparison.Ordinal) >= 0;
            Obs("accept-ui-login-ok={0} auth={1} home={2} luaNick={3} hudNick={4} snap={5}",
                ok, AccountBridge.IsAuthenticated(), home != null, luaNick, hudNick, snap);
            WriteBizSnapshot("login", snap, luaNick, hudNick);

            // 101 验收：登录进 Home 后按产品同一 UI.open 打开 PatchDemo（entry 开机窗会被 Login 覆盖）。
            TryOpenPatchDemoAfterLogin();
            await UniTask.Delay(500, cancellationToken: ct);
            FlushObs();
        }

        void TryOpenPatchDemoAfterLogin()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return;
                object[] r = _launcher.Lua.Env.DoString(
                    "if not CS.Game.Framework.Lua.BridgeApi.HasModule('UI/Windows/PatchDemoWindow') then return 'skip' end\n"
                    + "local UI=require('UI/UIManager')\n"
                    + "UI.open('PatchDemo','{}',function() end)\n"
                    + "return 'opened'",
                    "accept-open-patchdemo");
                string st = (r != null && r.Length > 0 && r[0] != null) ? Convert.ToString(r[0]) : "";
                Obs("accept-patchdemo={0}", st ?? "");
            }
            catch (Exception ex)
            {
                Obs("accept-patchdemo-fail {0}", Sanitize(ex.Message));
            }
        }

        bool TryLoadCreds(out string user, out string pass, out string nick)
        {
            user = pass = nick = null;
            if (string.IsNullOrEmpty(_acceptRoot))
                return false;
            string path = Path.Combine(_acceptRoot, CredsFileName);
            if (!File.Exists(path))
                return false;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3)
                return false;
            user = (lines[0] ?? "").Trim();
            pass = (lines[1] ?? "").Trim();
            nick = (lines[2] ?? "").Trim();
            return !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass) && !string.IsNullOrEmpty(nick);
        }

        void TrySaveCreds(string user, string pass, string nick)
        {
            if (string.IsNullOrEmpty(_acceptRoot))
                return;
            try
            {
                File.WriteAllText(
                    Path.Combine(_acceptRoot, CredsFileName),
                    user + "\n" + pass + "\n" + nick + "\n",
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        void TryPolluteLuaModel()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return;
                _launcher.Lua.Env.DoString(
                    "local M=require('PlayerModel'); M.nickname='POLLUTED_LOCAL'; M.level=999",
                    "pollute");
            }
            catch (Exception ex)
            {
                Obs("pollute-fail {0}", Sanitize(ex.Message));
            }
        }

        string ReadLuaNickname()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return "";
                object[] r = _launcher.Lua.Env.DoString(
                    "local M=require('PlayerModel'); return tostring(M.nickname or '')",
                    "read-nick");
                if (r != null && r.Length > 0 && r[0] != null)
                    return Convert.ToString(r[0]) ?? "";
            }
            catch
            {
            }

            return "";
        }

        string ReadHudNickname()
        {
            GameObject home = GameObject.Find("Win_HomeHud");
            return home != null ? (GetText(home, "Nickname") ?? "") : "";
        }

        void WriteBizSnapshot(string phase, string snapJson, string luaNick, string hudNick)
        {
            if (!_acceptMode || string.IsNullOrEmpty(_acceptRoot))
                return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("phase=").Append(phase).Append('\n');
                sb.Append("luaNick=").Append(luaNick).Append('\n');
                sb.Append("hudNick=").Append(hudNick).Append('\n');
                sb.Append("auth=").Append(AccountBridge.IsAuthenticated()).Append('\n');
                sb.Append("snapshot=").Append(snapJson).Append('\n');
                File.WriteAllText(
                    Path.Combine(_acceptRoot, "biz-" + phase + ".txt"),
                    sb.ToString(),
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        static void SetInput(GameObject root, string child, string value)
        {
            Transform t = root.transform.Find(child);
            InputField field = t != null
                ? t.GetComponent<InputField>() ?? t.GetComponentInChildren<InputField>()
                : null;
            if (field == null)
                return;
            field.text = value ?? "";
        }

        static string GetText(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Text tx = t != null
                ? t.GetComponent<Text>() ?? t.GetComponentInChildren<Text>()
                : null;
            return tx != null ? tx.text : null;
        }

        static bool ClickChild(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Button btn = t != null
                ? t.GetComponent<Button>() ?? t.GetComponentInChildren<Button>()
                : null;
            if (btn == null)
                return false;
            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            ExecuteEvents.Execute(
                btn.gameObject,
                new PointerEventData(EventSystem.current),
                ExecuteEvents.pointerClickHandler);
            return true;
        }

        void ParseArgs()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith(AcceptRootArg, StringComparison.OrdinalIgnoreCase))
                {
                    _acceptRoot = arg.Substring(AcceptRootArg.Length).Trim().Trim('"');
                    _acceptMode = !string.IsNullOrEmpty(_acceptRoot);
                }

                if (string.Equals(arg, AcceptAutoDropArg, StringComparison.OrdinalIgnoreCase))
                    _autoDropOnce = true;

                if (string.Equals(arg, AcceptUiLoginArg, StringComparison.OrdinalIgnoreCase))
                    _uiLogin = true;
            }

            if (_acceptMode)
                Directory.CreateDirectory(_acceptRoot);
        }

        static string ArgValue(string prefix)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                return arg.Substring(prefix.Length).Trim().Trim('"');
            }

            return null;
        }

        static int ArgInt(string prefix, int fallback)
        {
            string raw = ArgValue(prefix);
            return int.TryParse(raw, out int v) && v > 0 ? v : fallback;
        }

        void Obs(string fmt, params object[] args)
        {
            string line = DateTime.UtcNow.ToString("o") + " " + string.Format(fmt, args);
            Debug.Log("[M1Accept] " + line);
            lock (_obs)
                _obs.AppendLine(line);
            if (_acceptMode)
                FlushObs();
        }

        void FlushObs()
        {
            if (!_acceptMode || string.IsNullOrEmpty(_acceptRoot))
                return;
            string path = Path.Combine(_acceptRoot, "native-observe.log");
            string text;
            lock (_obs)
                text = _obs.ToString();
            try
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch
            {
            }

            try
            {
                var snap = new StringBuilder();
                snap.Append("authenticated=").Append(AccountBridge.IsAuthenticated()).Append('\n');
                snap.Append("hasToken=").Append(AccountBridge.HasToken()).Append('\n');
                snap.Append("mask=").Append(AccountBridge.IsDisconnectMaskVisible()).Append('\n');
                snap.Append("lastBiz=").Append(AccountBridge.LastBizCode()).Append('\n');
                snap.Append("lastKind=").Append(AccountBridge.LastErrorKind()).Append('\n');
                snap.Append("luaNick=").Append(ReadLuaNickname()).Append('\n');
                snap.Append("hudNick=").Append(ReadHudNickname()).Append('\n');
                string json = AccountBridge.LastSnapshotJson() ?? "{}";
                snap.Append("snapshotHasPlayerId=")
                    .Append(json.IndexOf("playerId", StringComparison.Ordinal) >= 0).Append('\n');
                snap.Append("snapshotRedacted=").Append(RedactSnapshot(json)).Append('\n');
                File.WriteAllText(Path.Combine(_acceptRoot, "native-session.txt"), snap.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        static string RedactSnapshot(string json)
        {
            if (string.IsNullOrEmpty(json))
                return "{}";
            // 去掉可能的 token 字段值，保留业务键结构。
            string s = System.Text.RegularExpressions.Regex.Replace(
                json,
                "\"(token|accessToken|refreshToken)\"\\s*:\\s*\"[^\"]*\"",
                "\"$1\":\"***\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return Trunc(s, 400);
        }

        static string Sanitize(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return "";
            string s = System.Text.RegularExpressions.Regex.Replace(
                msg, @"password\s*[:=]\s*\S+", "password=***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"\b(token|Bearer)\s*[:=]?\s*\S+", "$1=***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return Trunc(s, 160);
        }

        static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        static string Safe(Exception ex)
        {
            string m = ex.GetBaseException().Message ?? "error";
            return Trunc(m, 160);
        }
    }
}
