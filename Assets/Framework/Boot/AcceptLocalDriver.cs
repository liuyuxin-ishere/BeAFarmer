using System;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 仅 --m1-accept-root 启用：一次性 UI 命令、持久回执、脱敏状态和完整帧截图。
    /// 不暴露网络调试接口；不直接改 Model / 不直接 Commit。
    /// </summary>
    public sealed class AcceptLocalDriver
    {
        public const string CommandsDirName = "commands";
        public const string AckDirName = "acks";
        public const string StateFileName = "accept-state.txt";
        public const string ShotsDirName = "shots";

        readonly string _root;
        readonly Launcher _launcher;
        readonly Action<string> _obs;
        readonly string _expectedNick;
        readonly CancellationToken _lifetime;
        float _nextStateAt;
        readonly System.Collections.Generic.HashSet<string> _done =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <param name="acceptRoot">本次隔离状态根；命令、回执和截图均限定在此根内。</param>
        /// <param name="launcher">同一个正式启动流程；启动失败前也可处理 Retry。</param>
        /// <param name="obs">脱敏诊断出口。</param>
        /// <param name="expectedNick">可选昵称校验信息；不包含密码或令牌。</param>
        /// <param name="lifetime">宿主生命周期；退出时取消仍在等待帧的截图或开窗操作。</param>
        public AcceptLocalDriver(string acceptRoot, Launcher launcher, Action<string> obs, string expectedNick,
            CancellationToken lifetime = default)
        {
            _root = Path.GetFullPath(acceptRoot);
            _launcher = launcher;
            _obs = obs ?? (_ => { });
            _expectedNick = expectedNick ?? "";
            _lifetime = lifetime;
            Directory.CreateDirectory(Path.Combine(_root, CommandsDirName));
            Directory.CreateDirectory(Path.Combine(_root, AckDirName));
            Directory.CreateDirectory(Path.Combine(_root, ShotsDirName));
        }

        public void Tick()
        {
            if (_lifetime.IsCancellationRequested)
                return;
            if (Time.realtimeSinceStartup >= _nextStateAt)
            {
                _nextStateAt = Time.realtimeSinceStartup + 0.5f;
                try { WriteState(); } catch { }
            }

            string dir = Path.Combine(_root, CommandsDirName);
            if (!Directory.Exists(dir))
                return;
            foreach (string file in Directory.GetFiles(dir, "*.cmd"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(id) || _done.Contains(id))
                    continue;
                // Persisted acknowledgements prevent replay after a process restart.
                if (File.Exists(Path.Combine(_root, AckDirName, id + ".ack")))
                {
                    _done.Add(id);
                    try { File.Delete(file); } catch { }
                    continue;
                }
                string body;
                try
                {
                    body = File.ReadAllText(file);
                }
                catch
                {
                    // 读失败不记 done，下帧重试（避免与外部写文件竞态后永远丢命令）
                    continue;
                }

                if (!_done.Add(id))
                    continue;

                string op = FirstLine(body).Trim();
                if (op.StartsWith("screenshot:", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    CompleteScreenshotAsync(id, op).Forget();
                    continue;
                }
                if (string.Equals(op, "open-patchdemo", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    CompleteOpenPatchAsync(id, op).Forget();
                    continue;
                }
                string result = "ok";
                bool quitAfterAck = false;
                try
                {
                    if (string.Equals(op, "quit", StringComparison.OrdinalIgnoreCase))
                    {
                        // 先落 ack 再 Quit：否则进程退出后外部永远等不到 ack。
                        quitAfterAck = true;
                        result = "ok";
                    }
                    else
                    {
                        result = Execute(op, body) ? "ok" : "fail";
                    }
                }
                catch (Exception ex)
                {
                    result = "error:" + Sanitize(ex.GetBaseException().Message);
                }

                WriteAck(id, op, result);
                try { File.Delete(file); } catch { /* 防止重启后重放旧 quit */ }
                if (quitAfterAck)
                {
                    Application.Quit();
                    return;
                }
            }
        }

        bool Execute(string op, string body)
        {
            if (string.Equals(op, "register", StringComparison.OrdinalIgnoreCase))
                return ClickLoginChild("RegisterButton");
            if (string.Equals(op, "login", StringComparison.OrdinalIgnoreCase))
                return ClickLoginChild("LoginButton");
            if (string.Equals(op, "logout", StringComparison.OrdinalIgnoreCase))
                return ClickHomeChild("LogoutButton");
            if (string.Equals(op, "fill-creds", StringComparison.OrdinalIgnoreCase))
                return FillCredsFromPrivate();
            if (string.Equals(op, "click-patch-retry", StringComparison.OrdinalIgnoreCase))
                return _launcher != null && _launcher.TryAcceptClickPatchRetry();
            if (string.Equals(op, "drop-socket", StringComparison.OrdinalIgnoreCase))
            {
                if (!AccountBridge.IsAuthenticated()) return false;
                AccountBridge.DropLiveSocketForAcceptance();
                return true;
            }

            if (string.Equals(op, "write-state", StringComparison.OrdinalIgnoreCase))
            {
                WriteState();
                return true;
            }

            return false;
        }

        bool FillCredsFromPrivate()
        {
            string path = Path.Combine(_root, LauncherBootBehaviour.CredsFileName);
            if (!File.Exists(path))
                return false;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3)
                return false;
            GameObject login = GameObject.Find("Win_Login");
            if (login == null)
                return false;
            SetInput(login, "Username", lines[0].Trim());
            SetInput(login, "Password", lines[1].Trim());
            SetInput(login, "Nickname", lines[2].Trim());
            return true;
        }

        bool ClickLoginChild(string child)
        {
            GameObject login = GameObject.Find("Win_Login");
            return login != null && ClickChild(login, child);
        }

        bool ClickHomeChild(string child)
        {
            GameObject home = GameObject.Find("Win_HomeHud");
            return home != null && ClickChild(home, child);
        }

        /// <summary>Only acknowledge after the production UI service finishes opening the real Prefab.</summary>
        async UniTaskVoid CompleteOpenPatchAsync(string id, string op)
        {
            string result;
            try
            {
                if (_launcher?.Lua?.Env == null
                    || !Game.Framework.Lua.BridgeApi.HasModule("UI/Windows/PatchDemoWindow"))
                    throw new InvalidOperationException("patch-module-missing");
                var done = new UniTaskCompletionSource<int>();
                var lease = Game.Framework.UI.UiBridge.Open("PatchDemo", "{}",
                    (code, payload) => done.TrySetResult(code));
                using (_lifetime.Register(() =>
                       {
                           lease.Cancel("host-cancelled");
                           done.TrySetCanceled(_lifetime);
                       }))
                {
                    int code = await done.Task;
                    result = code == 0 ? "ok" : "error:open-code-" + code;
                }
            }
            catch (Exception ex) { result = "error:" + Sanitize(ex.GetBaseException().Message); }
            WriteAck(id, op, result);
        }

        /// <summary>Capture the rendered Native frame, including ScreenSpaceOverlay UI; never draw substitute UI.</summary>
        async UniTaskVoid CompleteScreenshotAsync(string id, string op)
        {
            string result;
            try
            {
                string name = op.Substring("screenshot:".Length).Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || Path.GetFileName(name) != name
                    || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("invalid-shot-name");
                string path = Path.Combine(_root, ShotsDirName, name + ".png");
                if (File.Exists(path)) throw new IOException("shot-already-exists");
                // Native only: scheduling a full-frame capture keeps Overlay canvases.
                // No WaitForEndOfFrame is used in Editor batch tests.
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, _lifetime);
                ScreenCapture.CaptureScreenshot(path);
                float until = Time.realtimeSinceStartup + 15f;
                bool complete = false;
                while (Time.realtimeSinceStartup < until)
                {
                    _lifetime.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(path))
                        {
                            byte[] png = File.ReadAllBytes(path);
                            int p = png.Length - 8;
                            complete = p > 32 && png[p] == 'I' && png[p + 1] == 'E'
                                       && png[p + 2] == 'N' && png[p + 3] == 'D';
                            if (complete) break;
                        }
                    }
                    catch (IOException) { }
                    await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
                }
                if (!complete) throw new TimeoutException("full-frame-capture-timeout");
                result = "ok";
            }
            catch (Exception ex) { result = "error:" + Sanitize(ex.GetBaseException().Message); }
            WriteAck(id, op, result);
        }

        void WriteAck(string id, string op, string result)
        {
            WriteAtomic(Path.Combine(_root, AckDirName, id + ".ack"),
                "id=" + id + "\nop=" + op + "\nresult=" + result + "\n");
            _obs("accept-cmd id=" + id + " op=" + op + " result=" + result);
        }

        static void WriteAtomic(string path, string text)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        void WriteState()
        {
            var sb = new StringBuilder(2048);
            sb.Append("utc=").Append(DateTime.UtcNow.ToString("o")).Append('\n');
            sb.Append("auth=").Append(AccountBridge.IsAuthenticated()).Append('\n');
            sb.Append("hasToken=").Append(AccountBridge.HasToken()).Append('\n');
            sb.Append("mask=").Append(AccountBridge.IsDisconnectMaskVisible()).Append('\n');
            sb.Append("loginOpened=").Append(_launcher != null && _launcher.LoginOpened).Append('\n');
            sb.Append("entry=").Append(_launcher != null && _launcher.EntryReady).Append('\n');
            sb.Append("committed=").Append(_launcher != null && _launcher.Committed).Append('\n');
            sb.Append("scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Append('\n');
            sb.Append("winLogin=").Append(GameObject.Find("Win_Login") != null).Append('\n');
            sb.Append("winHome=").Append(GameObject.Find("Win_HomeHud") != null).Append('\n');
            sb.Append("winPatch=").Append(GameObject.Find("Win_PatchDemo") != null
                                           || GameObject.Find("PatchDemoWindow") != null).Append('\n');
            var login = GameObject.Find("Win_Login");
            sb.Append("registerMessage=").Append(Sanitize(login != null ? GetText(login, "ErrorLabel") : "")).Append('\n');
            sb.Append("lastBiz=").Append(AccountBridge.LastBizCode()).Append('\n');
            sb.Append("lastKind=").Append(Sanitize(AccountBridge.LastErrorKind())).Append('\n');
            sb.Append("globalPatch=").Append(Sanitize(Game.Framework.Lua.BridgeApi.GetGlobal("m1_patch_message"))).Append('\n');
            sb.Append("patchModule=").Append(Game.Framework.Lua.BridgeApi.HasModule("UI/Windows/PatchDemoWindow")).Append('\n');
            sb.Append("patchRetryVisible=").Append(IsPatchRetryVisible()).Append('\n');
            sb.Append("resVersion=").Append(ReadResVersion()).Append('\n');
            sb.Append("downloadFiles=").Append(ReadDownloadFiles()).Append('\n');
            sb.Append("reconnectAttempt=").Append(AccountBridge.ReconnectAttempt).Append('\n');
            sb.Append("reconnectRunning=").Append(AccountBridge.IsReconnectRunning()).Append('\n');
            sb.Append("luaNick=").Append(ReadLuaNickname()).Append('\n');
            sb.Append("hudNick=").Append(ReadHudNickname()).Append('\n');
            sb.Append("expectedNick=").Append(_expectedNick).Append('\n');
            sb.Append("luaModel=").Append(ReadLuaModelSnapshot()).Append('\n');
            sb.Append("bizSnap=").Append(Redact(AccountBridge.LastSnapshotJson() ?? "{}")).Append('\n');
            sb.Append("disposeError=").Append(Sanitize(_launcher?.DisposeError ?? "")).Append('\n');
            var scenes = new System.Collections.Generic.List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isLoaded) scenes.Add(scene.name);
            }
            sb.Append("loadedScenes=").Append(string.Join(",", scenes)).Append('\n');
            WriteAtomic(Path.Combine(_root, StateFileName), sb.ToString());
        }

        string ReadResVersion()
        {
            try
            {
                string path = _launcher?.ActiveIndexPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return "0";
                string json = File.ReadAllText(path);
                int i = json.IndexOf("\"resVersion\"", StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                    return "0";
                int colon = json.IndexOf(':', i);
                if (colon < 0)
                    return "0";
                int end = colon + 1;
                while (end < json.Length && (char.IsWhiteSpace(json[end]) || json[end] == '"'))
                    end++;
                int start = end;
                while (end < json.Length && (char.IsDigit(json[end])))
                    end++;
                return end > start ? json.Substring(start, end - start) : "0";
            }
            catch { return "0"; }
        }

        int ReadDownloadFiles()
        {
            try
            {
                return _launcher?.LastPatchDownloadFiles ?? 0;
            }
            catch { return 0; }
        }

        bool IsPatchRetryVisible()
        {
            var v = _launcher?.ActivePatchView;
            return v != null && v.RetryButton != null && v.RetryButton.gameObject.activeInHierarchy
                   && v.RetryButton.interactable;
        }

        string ReadLuaNickname()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return "";
                object[] r = _launcher.Lua.Env.DoString(
                    "local M=require('PlayerModel'); return tostring(M.nickname or '')", "st-nick");
                return (r != null && r.Length > 0 && r[0] != null) ? Convert.ToString(r[0]) ?? "" : "";
            }
            catch { return ""; }
        }

        string ReadHudNickname()
        {
            GameObject home = GameObject.Find("Win_HomeHud");
            return home != null ? (GetText(home, "Nickname") ?? "") : "";
        }

        string ReadLuaModelSnapshot()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return "{}";
                object[] r = _launcher.Lua.Env.DoString(
                    "local M=require('PlayerModel'); local J=require('Json');\n"
                    + "return J.encode({info={playerId=M.playerId,nickname=M.nickname,level=M.level,exp=M.exp,"
                    + "gold=M.gold,gem=M.gem,ticket=M.ticket,shard=M.shard,bagSlots=M.bagSlots,guideStep=M.guideStep},"
                    + "bag=M.bag,plots=M.plots,nodes=M.nodes,tools=M.tools,facilities=M.facilities,orders=M.orders,"
                    + "orderProgress=M.orderProgress,home=M.home,avatar=M.avatar,gachaStates=M.gachaStates,activities=M.activities})",
                    "st-model");
                return Redact((r != null && r.Length > 0 && r[0] != null) ? Convert.ToString(r[0]) : "{}");
            }
            catch { return "{}"; }
        }

        static string FirstLine(string body)
        {
            if (string.IsNullOrEmpty(body))
                return "";
            int i = body.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? body : body.Substring(0, i);
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace('\n', ' ').Replace('\r', ' ');
        }

        static string Redact(string json)
        {
            if (string.IsNullOrEmpty(json))
                return "{}";
            // 粗脱敏：去掉 token/password 字段值
            return System.Text.RegularExpressions.Regex.Replace(
                json,
                "\"(token|password|Password|Token)\"\\s*:\\s*\"[^\"]*\"",
                "\"$1\":\"***\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        static void SetInput(GameObject root, string child, string value)
        {
            Transform t = root.transform.Find(child);
            InputField field = t != null
                ? t.GetComponent<InputField>() ?? t.GetComponentInChildren<InputField>()
                : null;
            if (field != null)
                field.text = value ?? "";
        }

        static string GetText(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Text tx = t != null ? t.GetComponent<Text>() ?? t.GetComponentInChildren<Text>() : null;
            return tx != null ? tx.text : null;
        }

        static bool ClickChild(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Button btn = t != null
                ? t.GetComponent<Button>() ?? t.GetComponentInChildren<Button>()
                : null;
            if (btn == null || !btn.isActiveAndEnabled || !btn.interactable)
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
    }
}
