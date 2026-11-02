using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.Home;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 仅 --m1-accept-root 启用：一次性 UI 命令、持久回执、脱敏状态和完整帧截图。
    /// 不暴露网络调试接口；不直接改 Model / 不直接 Commit。
    /// </summary>
    public sealed class AcceptLocalDriver : IDisposable
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
        readonly int _processId;
        readonly string _acceptStartUtc;
        readonly string _acceptInstance;
        float _nextStateAt;
        readonly HashSet<string> _done =
            new HashSet<string>(StringComparer.Ordinal);
        int _plotObserveId;
        float _plotScreenX;
        float _plotScreenY;
        bool _plotScreenOk;
        int _nodeObserveId;
        float _nodeScreenX;
        float _nodeScreenY;
        bool _nodeScreenOk;
        int _nodeResolvedId;
        bool _nodeUiBlocked;
        /// <summary>本驱动自己按下的 OS 左键；失焦/取消/失败时必须释放，禁止泄漏按键所有权。</summary>
        bool _osLeftButtonHeldByUs;
        readonly object _osButtonGate = new object();
        CancellationTokenRegistration _osCancellation;
        bool _osClickBusy;
        bool _disposed;

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
            _processId = Process.GetCurrentProcess().Id;
            _acceptStartUtc = DateTime.UtcNow.ToString("o");
            _acceptInstance = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(_root, CommandsDirName));
            Directory.CreateDirectory(Path.Combine(_root, AckDirName));
            Directory.CreateDirectory(Path.Combine(_root, ShotsDirName));
            _osCancellation = lifetime.Register(() => { ReleaseOsLeftIfHeld(); });
        }

        public void Dispose()
        {
            _disposed = true;
            ReleaseOsLeftIfHeld();
            _osCancellation.Dispose();
        }

        public void Tick()
        {
            if (_disposed || _lifetime.IsCancellationRequested)
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

                if (op.StartsWith("bag-select-contains:", StringComparison.OrdinalIgnoreCase)
                    || op.StartsWith("shop-select-contains:", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    string windowId = op.StartsWith("bag-", StringComparison.OrdinalIgnoreCase) ? "Bag" : "Shop";
                    string prefix = windowId == "Bag" ? "bag-select-contains:" : "shop-select-contains:";
                    CompleteSelectContainsAsync(id, op, windowId, op.Substring(prefix.Length).Trim(), 64)
                        .Forget();
                    continue;
                }

                if (op.StartsWith("farm-select-seed-contains:", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    CompleteFarmSeedSelectAsync(
                        id, op, op.Substring("farm-select-seed-contains:".Length).Trim(), 64).Forget();
                    continue;
                }

                if (op.StartsWith("os-click-plot:", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    CompleteOsClickPlotAsync(
                        id, op, op.Substring("os-click-plot:".Length).Trim()).Forget();
                    continue;
                }

                if (op.StartsWith("os-click-node:", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    CompleteOsClickNodeAsync(
                        id, op, op.Substring("os-click-node:".Length).Trim()).Forget();
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

            // T027 Native U01：仅驱动生产 EventSystem 按钮/输入，不写 Model、不发旁路协议。
            if (string.Equals(op, "open-bag", StringComparison.OrdinalIgnoreCase))
                return ClickHomeChild("BagButton");
            if (string.Equals(op, "open-shop", StringComparison.OrdinalIgnoreCase))
                return ClickHomeChild("ShopButton");
            if (string.Equals(op, "close-bag", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Bag", "CloseButton");
            if (string.Equals(op, "close-shop", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Shop", "CloseButton");
            if (string.Equals(op, "bag-prev", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Bag", "PrevButton");
            if (string.Equals(op, "bag-next", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Bag", "NextButton");
            if (string.Equals(op, "shop-prev", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Shop", "PrevButton");
            if (string.Equals(op, "shop-next", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Shop", "NextButton");
            if (string.Equals(op, "sell", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Bag", "SellButton");
            if (string.Equals(op, "expand", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Bag", "ExpandButton");
            if (string.Equals(op, "buy", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Shop", "BuyButton");
            if (op.StartsWith("bag-set-count:", StringComparison.OrdinalIgnoreCase))
                return SetNamedWinInput("Bag", "CountInput", op.Substring("bag-set-count:".Length).Trim());
            if (op.StartsWith("shop-set-count:", StringComparison.OrdinalIgnoreCase))
                return SetNamedWinInput("Shop", "CountInput", op.Substring("shop-set-count:".Length).Trim());

            // T036 U02：Farm 仅生产 EventSystem 按钮 + 只读地块屏幕观测；世界命中走真实 OS 鼠标。
            // 禁止旁路 FarmService / Gameplay 写命令。
            if (string.Equals(op, "open-farm", StringComparison.OrdinalIgnoreCase))
                return ClickHomeChild("FarmButton");
            if (string.Equals(op, "close-farm", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "CloseButton");
            if (string.Equals(op, "farm-prev-seed", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "PrevSeedButton");
            if (string.Equals(op, "farm-next-seed", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "NextSeedButton");
            if (string.Equals(op, "farm-mode-plant", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "PlantModeButton");
            if (string.Equals(op, "farm-mode-water", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "WaterModeButton");
            if (string.Equals(op, "farm-mode-fertilize", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "FertilizeModeButton");
            if (string.Equals(op, "farm-mode-harvest", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Farm", "HarvestModeButton");
            if (op.StartsWith("observe-plot-screen:", StringComparison.OrdinalIgnoreCase))
                return ObservePlotScreen(op.Substring("observe-plot-screen:".Length).Trim());

            // T041 U03：Gather/Tool 仅生产 EventSystem 按钮 + 只读节点屏幕观测；世界命中走真实 OS 鼠标。
            // 禁止旁路 GatherService / ToolService / 网络写 / Model 写入。
            if (string.Equals(op, "open-gather", StringComparison.OrdinalIgnoreCase))
                return ClickHomeChild("GatherButton");
            if (string.Equals(op, "close-gather", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Gather", "CloseButton");
            if (string.Equals(op, "gather-prev", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Gather", "PrevButton");
            if (string.Equals(op, "gather-next", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Gather", "NextButton");
            if (string.Equals(op, "gather", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Gather", "GatherButton");
            if (string.Equals(op, "open-tool", StringComparison.OrdinalIgnoreCase))
                return ClickHomeChild("ToolButton");
            if (string.Equals(op, "close-tool", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Tool", "CloseButton");
            if (string.Equals(op, "tool-prev", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Tool", "PrevButton");
            if (string.Equals(op, "tool-next", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Tool", "NextButton");
            if (string.Equals(op, "tool-upgrade", StringComparison.OrdinalIgnoreCase))
                return ClickNamedWin("Tool", "UpgradeButton");
            if (string.Equals(op, "dialog-confirm", StringComparison.OrdinalIgnoreCase))
                return ClickDialogChild("OkButton");
            if (string.Equals(op, "dialog-cancel", StringComparison.OrdinalIgnoreCase))
                return ClickDialogChild("CancelButton");
            if (op.StartsWith("observe-node-screen:", StringComparison.OrdinalIgnoreCase))
                return ObserveNodeScreen(op.Substring("observe-node-screen:".Length).Trim());

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
            return TrySetInput(login, "Username", lines[0].Trim())
                   && TrySetInput(login, "Password", lines[1].Trim())
                   && TrySetInput(login, "Nickname", lines[2].Trim());
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

        bool ClickNamedWin(string windowId, string child)
        {
            GameObject win = GameObject.Find("Win_" + windowId);
            return win != null && ClickChild(win, child);
        }

        bool ClickDialogChild(string child)
        {
            GameObject dialog = GameObject.Find("Win_Dialog");
            return dialog != null && ClickChild(dialog, child);
        }

        bool SetNamedWinInput(string windowId, string child, string value)
        {
            GameObject win = GameObject.Find("Win_" + windowId);
            if (win == null)
                return false;
            return TrySetInput(win, child, value ?? "");
        }

        /// <summary>
        /// 有界异步逐帧：沿生产 Next 选择，直到 SelectedLabel 含 needle 且不含 exclude（显示名，非内部 ID）。
        /// 规格：`小麦种子` 或 `小麦|exclude:种子`（避免把「小麦种子」误当作物小麦）。
        /// </summary>
        async UniTaskVoid CompleteSelectContainsAsync(
            string id, string op, string windowId, string rawSpec, int maxSteps)
        {
            string result;
            try
            {
                _lifetime.ThrowIfCancellationRequested();
                if (!TryParseSelectSpec(rawSpec, out string needle, out string exclude))
                    throw new ArgumentException("invalid-select-spec");
                bool matched = false;
                for (int i = 0; i < maxSteps; i++)
                {
                    _lifetime.ThrowIfCancellationRequested();
                    await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
                    GameObject win = GameObject.Find("Win_" + windowId);
                    if (win == null)
                        throw new InvalidOperationException("window-missing:" + windowId);
                    string text = GetText(win, "SelectedLabel") ?? "";
                    if (LabelMatches(text, needle, exclude))
                    {
                        matched = true;
                        break;
                    }

                    if (!ClickChild(win, "NextButton"))
                        throw new InvalidOperationException("next-click-failed label=" + Sanitize(text));
                }

                if (!matched)
                {
                    GameObject win = GameObject.Find("Win_" + windowId);
                    string last = win != null ? (GetText(win, "SelectedLabel") ?? "") : "";
                    throw new TimeoutException(
                        "select-not-found needle=" + needle + " exclude=" + exclude + " last=" + Sanitize(last));
                }

                result = "ok";
            }
            catch (OperationCanceledException)
            {
                result = "error:cancelled";
            }
            catch (Exception ex)
            {
                result = "error:" + Sanitize(ex.GetBaseException().Message);
            }

            WriteAck(id, op, result);
        }

        static bool TryParseSelectSpec(string raw, out string needle, out string exclude)
        {
            needle = "";
            exclude = "";
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            const string excludeToken = "|exclude:";
            int ex = raw.IndexOf(excludeToken, StringComparison.OrdinalIgnoreCase);
            if (ex >= 0)
            {
                needle = raw.Substring(0, ex).Trim();
                exclude = raw.Substring(ex + excludeToken.Length).Trim();
            }
            else
            {
                needle = raw.Trim();
            }

            return !string.IsNullOrEmpty(needle);
        }

        static bool LabelMatches(string text, string needle, string exclude)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
                return false;
            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (!string.IsNullOrEmpty(exclude)
                && text.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
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
            sb.Append("processId=").Append(_processId.ToString()).Append('\n');
            sb.Append("acceptStartUtc=").Append(_acceptStartUtc).Append('\n');
            sb.Append("acceptInstance=").Append(_acceptInstance).Append('\n');
            sb.Append("auth=").Append(AccountBridge.IsAuthenticated()).Append('\n');
            sb.Append("hasToken=").Append(AccountBridge.HasToken()).Append('\n');
            sb.Append("mask=").Append(AccountBridge.IsDisconnectMaskVisible()).Append('\n');
            sb.Append("loginOpened=").Append(_launcher != null && _launcher.LoginOpened).Append('\n');
            sb.Append("entry=").Append(_launcher != null && _launcher.EntryReady).Append('\n');
            sb.Append("committed=").Append(_launcher != null && _launcher.Committed).Append('\n');
            sb.Append("scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Append('\n');
            sb.Append("winLogin=").Append(GameObject.Find("Win_Login") != null).Append('\n');
            sb.Append("winHome=").Append(GameObject.Find("Win_HomeHud") != null).Append('\n');
            sb.Append("winBag=").Append(GameObject.Find("Win_Bag") != null).Append('\n');
            sb.Append("winShop=").Append(GameObject.Find("Win_Shop") != null).Append('\n');
            sb.Append("winFarm=").Append(GameObject.Find("Win_Farm") != null).Append('\n');
            sb.Append("winGather=").Append(GameObject.Find("Win_Gather") != null).Append('\n');
            sb.Append("winTool=").Append(GameObject.Find("Win_Tool") != null).Append('\n');
            sb.Append("winDialog=").Append(GameObject.Find("Win_Dialog") != null).Append('\n');
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
            // T028：玩法 pending 来自 GameplayCommandClient.ActiveSnapshot / 持久 store，
            // 禁止账号传输 PendingRequestCount 冒充。只读元数据；不写 Model。
            // 本观测触及固定运行时 C# → 须 GenAll + 新 IL2CPP，旧 T027 Player 不可复用。
            AccountBridge.AppendGameplayPendingAcceptanceFields(sb);
            sb.Append("luaNick=").Append(ReadLuaNickname()).Append('\n');
            sb.Append("hudNick=").Append(ReadHudNickname()).Append('\n');
            sb.Append("hudGold=").Append(Sanitize(ReadHudText("Gold"))).Append('\n');
            sb.Append("hudLevel=").Append(Sanitize(ReadHudText("Level"))).Append('\n');
            sb.Append("hudBagSlots=").Append(Sanitize(ReadHudText("BagSlots"))).Append('\n');
            sb.Append("bagSelected=").Append(Sanitize(ReadWinText("Bag", "SelectedLabel"))).Append('\n');
            sb.Append("bagStatus=").Append(Sanitize(ReadWinText("Bag", "StatusLabel"))).Append('\n');
            sb.Append("bagGold=").Append(Sanitize(ReadWinText("Bag", "GoldLabel"))).Append('\n');
            sb.Append("bagCapacity=").Append(Sanitize(ReadWinText("Bag", "CapacityLabel"))).Append('\n');
            sb.Append("shopSelected=").Append(Sanitize(ReadWinText("Shop", "SelectedLabel"))).Append('\n');
            sb.Append("shopStatus=").Append(Sanitize(ReadWinText("Shop", "StatusLabel"))).Append('\n');
            sb.Append("shopGold=").Append(Sanitize(ReadWinText("Shop", "GoldLabel"))).Append('\n');
            // Read actual production view text;no gameplay command/model mutation.
            sb.Append("farmStatus=").Append(Sanitize(ReadWinText("Farm", "StatusLabel"))).Append('\n');
            sb.Append("farmCountdown=").Append(Sanitize(ReadWinText("Farm", "CountdownLabel"))).Append('\n');
            sb.Append("farmMode=").Append(Sanitize(ReadWinText("Farm", "ModeLabel"))).Append('\n');
            sb.Append("farmSeed=").Append(Sanitize(ReadWinText("Farm", "SeedLabel"))).Append('\n');
            sb.Append("farmClockSynchronized=").Append(Game.Framework.Gameplay.LuaGameplayFacade.IsClockSynchronized()).Append('\n');
            sb.Append("farmObservedRemainingMs=").Append(Sanitize(ReadFarmRemainingMs())).Append('\n');
            sb.Append("farmRuntimeReady=").Append(LuaHomeFarmFacade.IsRuntimeReady()).Append('\n');
            sb.Append("plotObserveId=").Append(_plotObserveId).Append('\n');
            sb.Append("plotScreenX=").Append(_plotScreenX.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("plotScreenY=").Append(_plotScreenY.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("plotScreenOk=").Append(_plotScreenOk).Append('\n');
            // T041：Gather/Tool 只读 View 字段与节点屏幕观测；不输出账号密码 Token。
            sb.Append("gatherSelected=").Append(Sanitize(ReadWinText("Gather", "SelectedLabel"))).Append('\n');
            sb.Append("gatherCooldown=").Append(Sanitize(ReadWinText("Gather", "CooldownLabel"))).Append('\n');
            sb.Append("gatherStatus=").Append(Sanitize(ReadWinText("Gather", "StatusLabel"))).Append('\n');
            sb.Append("gatherList=").Append(Sanitize(ReadWinText("Gather", "NodeListLabel"))).Append('\n');
            sb.Append("gatherButtonInteractable=").Append(ReadWinInteractable("Gather", "GatherButton")).Append('\n');
            sb.Append("gatherPrevInteractable=").Append(ReadWinInteractable("Gather", "PrevButton")).Append('\n');
            sb.Append("gatherNextInteractable=").Append(ReadWinInteractable("Gather", "NextButton")).Append('\n');
            sb.Append("toolSelected=").Append(Sanitize(ReadWinText("Tool", "SelectedLabel"))).Append('\n');
            sb.Append("toolCost=").Append(Sanitize(ReadWinText("Tool", "CostLabel"))).Append('\n');
            sb.Append("toolStatus=").Append(Sanitize(ReadWinText("Tool", "StatusLabel"))).Append('\n');
            sb.Append("toolList=").Append(Sanitize(ReadWinText("Tool", "ToolListLabel"))).Append('\n');
            sb.Append("toolUpgradeInteractable=").Append(ReadWinInteractable("Tool", "UpgradeButton")).Append('\n');
            sb.Append("toolPrevInteractable=").Append(ReadWinInteractable("Tool", "PrevButton")).Append('\n');
            sb.Append("toolNextInteractable=").Append(ReadWinInteractable("Tool", "NextButton")).Append('\n');
            sb.Append("dialogTitle=").Append(Sanitize(ReadDialogText("Title"))).Append('\n');
            sb.Append("dialogBody=").Append(Sanitize(ReadDialogText("Body"))).Append('\n');
            sb.Append("dialogConfirmInteractable=").Append(ReadDialogInteractable("OkButton")).Append('\n');
            sb.Append("dialogCancelInteractable=").Append(ReadDialogInteractable("CancelButton")).Append('\n');
            sb.Append("nodeObserveId=").Append(_nodeObserveId).Append('\n');
            sb.Append("nodeScreenX=").Append(_nodeScreenX.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("nodeScreenY=").Append(_nodeScreenY.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("nodeScreenOk=").Append(_nodeScreenOk).Append('\n');
            sb.Append("nodeResolvedId=").Append(_nodeResolvedId).Append('\n');
            sb.Append("nodeUiBlocked=").Append(_nodeUiBlocked).Append('\n');
            sb.Append("expectedNick=").Append(_expectedNick).Append('\n');
            sb.Append("luaModel=").Append(ReadLuaModelSnapshot()).Append('\n');
            sb.Append("modelStore=").Append(ReadModelStoreSnapshot()).Append('\n');
            sb.Append("modelRevision=").Append(ReadModelStoreRevision()).Append('\n');
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
            return ReadHudText("Nickname");
        }

        string ReadHudText(string child)
        {
            GameObject home = GameObject.Find("Win_HomeHud");
            return home != null ? (GetText(home, child) ?? "") : "";
        }

        string ReadWinText(string windowId, string child)
        {
            GameObject win = GameObject.Find("Win_" + windowId);
            return win != null ? (GetText(win, child) ?? "") : "";
        }

        string ReadWinInteractable(string windowId, string child)
        {
            GameObject win = GameObject.Find("Win_" + windowId);
            if (win == null)
                return "false";
            return IsChildInteractable(win, child) ? "true" : "false";
        }

        string ReadDialogText(string child)
        {
            GameObject dialog = GameObject.Find("Win_Dialog");
            return dialog != null ? (GetText(dialog, child) ?? "") : "";
        }

        string ReadDialogInteractable(string child)
        {
            GameObject dialog = GameObject.Find("Win_Dialog");
            if (dialog == null)
                return "false";
            return IsChildInteractable(dialog, child) ? "true" : "false";
        }

        static bool IsChildInteractable(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Button btn = t != null
                ? t.GetComponent<Button>() ?? t.GetComponentInChildren<Button>()
                : null;
            return btn != null && btn.isActiveAndEnabled && btn.interactable;
        }

        string ReadFarmRemainingMs()
        {
            if (_plotObserveId <= 0 || _launcher?.Lua?.Env == null) return "";
            try
            {
                object[] values = _launcher.Lua.Env.DoString(
                    "local S=require('Models/ModelStore'); local m=S.getSnapshot(); "
                    + "if m then for _,p in ipairs(m.plots or {}) do if p.plotId=="
                    + _plotObserveId.ToString(CultureInfo.InvariantCulture)
                    + " then return CS.Game.Framework.Gameplay.LuaGameplayFacade.RemainingMs(tostring(p.readyTime or '0')) end end end; return ''",
                    "accept-read-farm-clock");
                return values != null && values.Length > 0 ? Convert.ToString(values[0]) ?? "" : "";
            }
            catch { return ""; }
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

        /// <summary>
        /// 权威 ModelStore 快照（十进制 Int64 字符串原样经 Json.encode）；失败返回 {}。
        /// </summary>
        string ReadModelStoreSnapshot()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return "{}";
                object[] r = _launcher.Lua.Env.DoString(
                    "local S=require('Models/ModelStore'); local J=require('Json');\n"
                    + "local snap=S.getSnapshot and S.getSnapshot() or nil;\n"
                    + "if not snap then return '{}' end\n"
                    + "return J.encode(snap)",
                    "st-modelstore");
                return Redact((r != null && r.Length > 0 && r[0] != null) ? Convert.ToString(r[0]) : "{}");
            }
            catch { return "{}"; }
        }

        string ReadModelStoreRevision()
        {
            try
            {
                if (_launcher?.Lua?.Env == null)
                    return "";
                object[] r = _launcher.Lua.Env.DoString(
                    "local S=require('Models/ModelStore'); return tostring(S.getRevision and S.getRevision() or '')",
                    "st-modelrev");
                return (r != null && r.Length > 0 && r[0] != null) ? Convert.ToString(r[0]) ?? "" : "";
            }
            catch { return ""; }
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

        static bool TrySetInput(GameObject root, string child, string value)
        {
            Transform t = root.transform.Find(child);
            InputField field = t != null
                ? t.GetComponent<InputField>() ?? t.GetComponentInChildren<InputField>()
                : null;
            if (field == null || !field.isActiveAndEnabled || !field.interactable)
                return false;
            field.text = value ?? "";
            return true;
        }

        static string GetText(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Text tx = t != null ? t.GetComponent<Text>() ?? t.GetComponentInChildren<Text>() : null;
            return tx != null ? tx.text : null;
        }

        bool ObservePlotScreen(string rawPlotId)
        {
            _plotScreenOk = false;
            _plotObserveId = 0;
            _plotScreenX = 0f;
            _plotScreenY = 0f;
            if (!int.TryParse(rawPlotId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plotId)
                || plotId <= 0)
                return false;
            string packed = LuaHomeFarmFacade.TryGetPlotScreenPoint(plotId);
            if (string.IsNullOrEmpty(packed))
                return false;
            int comma = packed.IndexOf(',');
            if (comma <= 0)
                return false;
            if (!float.TryParse(packed.Substring(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float x)
                || !float.TryParse(packed.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float y))
                return false;
            // 只读观测也拒绝非有限/视口外；禁止事后 Clamp 成别的合法点击。
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
                return false;
            if (x < 0f || y < 0f || x > Screen.width || y > Screen.height)
                return false;
            _plotObserveId = plotId;
            _plotScreenX = x;
            _plotScreenY = y;
            _plotScreenOk = true;
            WriteState();
            return true;
        }

        bool ObserveNodeScreen(string rawNodeId)
        {
            _nodeScreenOk = false;
            _nodeObserveId = 0;
            _nodeScreenX = 0f;
            _nodeScreenY = 0f;
            _nodeResolvedId = 0;
            _nodeUiBlocked = false;
            if (!int.TryParse(rawNodeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeId)
                || nodeId <= 0)
                return false;
            string packed = LuaHomeFarmFacade.TryGetNodeScreenPoint(nodeId);
            if (string.IsNullOrEmpty(packed))
                return false;
            int comma = packed.IndexOf(',');
            if (comma <= 0)
                return false;
            if (!float.TryParse(packed.Substring(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float x)
                || !float.TryParse(packed.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float y))
                return false;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
                return false;
            if (x < 0f || y < 0f || x > Screen.width || y > Screen.height)
                return false;
            _nodeObserveId = nodeId;
            _nodeScreenX = x;
            _nodeScreenY = y;
            _nodeScreenOk = true;
            _nodeResolvedId = LuaHomeFarmFacade.TryResolveNodeAtScreen(x, y);
            _nodeUiBlocked = IsScreenBlockedByUi(x, y);
            WriteState();
            return true;
        }

        static bool IsScreenBlockedByUi(float unityX, float unityY)
        {
            if (EventSystem.current == null)
                return false;
            Canvas.ForceUpdateCanvases();
            var pointer = new PointerEventData(EventSystem.current)
            {
                position = new Vector2(unityX, unityY)
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            return hits.Count > 0;
        }

        async UniTaskVoid CompleteFarmSeedSelectAsync(string id, string op, string rawSpec, int maxSteps)
        {
            string result;
            try
            {
                _lifetime.ThrowIfCancellationRequested();
                if (!TryParseSelectSpec(rawSpec, out string needle, out string exclude))
                    throw new ArgumentException("invalid-select-spec");
                bool matched = false;
                for (int i = 0; i < maxSteps; i++)
                {
                    _lifetime.ThrowIfCancellationRequested();
                    await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
                    GameObject win = GameObject.Find("Win_Farm");
                    if (win == null)
                        throw new InvalidOperationException("window-missing:Farm");
                    string text = GetText(win, "SeedLabel") ?? "";
                    if (LabelMatches(text, needle, exclude))
                    {
                        matched = true;
                        break;
                    }

                    if (!ClickChild(win, "NextSeedButton"))
                        throw new InvalidOperationException("farm-next-seed-failed label=" + Sanitize(text));
                }

                if (!matched)
                {
                    GameObject win = GameObject.Find("Win_Farm");
                    string last = win != null ? (GetText(win, "SeedLabel") ?? "") : "";
                    throw new TimeoutException(
                        "farm-seed-not-found needle=" + needle + " last=" + Sanitize(last));
                }

                result = "ok";
            }
            catch (OperationCanceledException)
            {
                result = "error:cancelled";
            }
            catch (Exception ex)
            {
                result = "error:" + Sanitize(ex.GetBaseException().Message);
            }

            WriteAck(id, op, result);
        }

        /// <summary>
        /// 真实 OS 鼠标：MOVE(ABSOLUTE|VIRTUALDESK|NOCOALESCE)→跨帧采样→DOWN→跨帧→UP。
        /// 不直调 FarmService/网络写；不替换 InputSystem。失焦/取消时释放本驱动持有的按键。
        /// </summary>
        async UniTaskVoid CompleteOsClickPlotAsync(string id, string op, string rawPlotId)
        {
            if (_disposed || _osClickBusy)
            {
                WriteAck(id, op, "error:os-click-unavailable");
                return;
            }
            _osClickBusy = true;
            string result;
            try
            {
                _lifetime.ThrowIfCancellationRequested();
                if (!ObservePlotScreen(rawPlotId))
                    throw new InvalidOperationException("plot-screen-observe-failed id=" + rawPlotId);
                await OsClickUnityScreenAcrossFramesAsync(_plotScreenX, _plotScreenY);
                result = "ok";
            }
            catch (OperationCanceledException)
            {
                ReleaseOsLeftIfHeld();
                result = "error:cancelled";
            }
            catch (Exception ex)
            {
                ReleaseOsLeftIfHeld();
                result = "error:" + Sanitize(ex.GetBaseException().Message);
            }
            finally
            {
                ReleaseOsLeftIfHeld();
                _osClickBusy = false;
            }

            WriteAck(id, op, result);
        }

        /// <summary>
        /// 节点 OS 点击：复用 Plot 安全路径；观测真实解析 ID 与 UI 遮挡；不直调 GatherService/网络/Model。
        /// </summary>
        async UniTaskVoid CompleteOsClickNodeAsync(string id, string op, string rawNodeId)
        {
            if (_disposed || _osClickBusy)
            {
                WriteAck(id, op, "error:os-click-unavailable");
                return;
            }
            _osClickBusy = true;
            string result;
            try
            {
                _lifetime.ThrowIfCancellationRequested();
                if (!ObserveNodeScreen(rawNodeId))
                    throw new InvalidOperationException("node-screen-observe-failed id=" + rawNodeId);
                await OsClickUnityScreenAcrossFramesAsync(_nodeScreenX, _nodeScreenY);
                result = "ok";
            }
            catch (OperationCanceledException)
            {
                ReleaseOsLeftIfHeld();
                result = "error:cancelled";
            }
            catch (Exception ex)
            {
                ReleaseOsLeftIfHeld();
                result = "error:" + Sanitize(ex.GetBaseException().Message);
            }
            finally
            {
                ReleaseOsLeftIfHeld();
                _osClickBusy = false;
            }

            WriteAck(id, op, result);
        }

        async UniTask OsClickUnityScreenAcrossFramesAsync(float unityX, float unityY)
        {
            if (float.IsNaN(unityX) || float.IsNaN(unityY)
                || float.IsInfinity(unityX) || float.IsInfinity(unityY))
                throw new InvalidOperationException("os-click-non-finite");
            if (unityX < 0f || unityY < 0f || unityX > Screen.width || unityY > Screen.height)
                throw new InvalidOperationException("os-click-out-of-viewport");

            IntPtr hwnd = ResolveOwnedPlayerHwnd();
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("os-click-hwnd-missing");
            if (!GetClientRect(hwnd, out RECT client))
                throw new InvalidOperationException("os-click-clientrect-failed");
            int clientW = client.Right - client.Left;
            int clientH = client.Bottom - client.Top;
            if (clientW <= 0 || clientH <= 0)
                throw new InvalidOperationException("os-click-clientrect-empty");

            // Unity Screen：(0,0) 左下；Win32 客户区：(0,0) 左上。拒绝越界，禁止 Clamp。
            int cx = Mathf.RoundToInt(unityX);
            int cy = Mathf.RoundToInt(Screen.height - unityY);
            if (cx < 0 || cy < 0 || cx >= clientW || cy >= clientH)
                throw new InvalidOperationException(
                    "os-click-outside-client cx=" + cx + " cy=" + cy
                    + " client=" + clientW + "x" + clientH);

            var pt = new POINT { X = cx, Y = cy };
            if (!ClientToScreen(hwnd, ref pt))
                throw new InvalidOperationException("os-click-client-to-screen-failed");

            bool alreadyForeground = GetForegroundWindow() == hwnd;
            bool focusRequested = alreadyForeground || SetForegroundWindow(hwnd);
            _obs("os-focus initialForeground=" + alreadyForeground + " requestAccepted=" + focusRequested);
            await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
            _lifetime.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("os-click-foreground-not-owned");
            if (!IsWindowOwnedByCurrentProcess(hwnd))
                throw new InvalidOperationException("os-click-hwnd-pid-mismatch");

            if (!TrySendMouseAbsoluteMove(pt.X, pt.Y))
                throw new InvalidOperationException("os-click-move-failed");
            // 跨真实 PlayerLoop 采样 MOVE，再 DOWN，再跨帧 UP（禁止同批 down/up）。
            await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, _lifetime);
            _lifetime.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != hwnd || !IsWindowOwnedByCurrentProcess(hwnd))
                throw new InvalidOperationException("os-click-lost-foreground-before-down");

            lock (_osButtonGate)
            {
                _lifetime.ThrowIfCancellationRequested();
                if (_disposed || (GetAsyncKeyState(1) & 0x8000) != 0)
                    throw new InvalidOperationException("os-click-left-already-held-or-disposed");
                if (!TrySendMouseButton(MOUSEEVENTF_LEFTDOWN))
                    throw new InvalidOperationException("os-click-down-failed");
                _osLeftButtonHeldByUs = true;
            }

            await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
            await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
            _lifetime.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != hwnd)
            {
                ReleaseOsLeftIfHeld();
                throw new InvalidOperationException("os-click-lost-foreground-before-up");
            }

            if (!ReleaseOsLeftIfHeld())
                throw new InvalidOperationException("os-click-up-failed");
            await UniTask.Yield(PlayerLoopTiming.Update, _lifetime);
        }

        bool ReleaseOsLeftIfHeld()
        {
            lock (_osButtonGate)
            {
                if (!_osLeftButtonHeldByUs)
                    return true;
                try
                {
                    if (!TrySendMouseButton(MOUSEEVENTF_LEFTUP))
                        return false;
                    _osLeftButtonHeldByUs = false;
                    return true;
                }
                catch { return false; }
            }
        }

        static IntPtr ResolveOwnedPlayerHwnd()
        {
            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd != IntPtr.Zero && IsWindowOwnedByCurrentProcess(hwnd))
                return hwnd;
            hwnd = GetActiveWindow();
            if (hwnd != IntPtr.Zero && IsWindowOwnedByCurrentProcess(hwnd))
                return hwnd;
            hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero && IsWindowOwnedByCurrentProcess(hwnd))
                return hwnd;
            return IntPtr.Zero;
        }

        static bool IsWindowOwnedByCurrentProcess(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        static bool TrySendMouseAbsoluteMove(int screenX, int screenY)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 1 || vh <= 1)
                return false;
            if (screenX < vx || screenY < vy || screenX >= vx + vw || screenY >= vy + vh)
                return false;
            int absX = (int)Math.Round((screenX - vx) * 65535.0 / (vw - 1));
            int absY = (int)Math.Round((screenY - vy) * 65535.0 / (vh - 1));
            var inputs = new INPUT[1];
            inputs[0].Type = INPUT_MOUSE;
            inputs[0].Data.Mouse.Dx = absX;
            inputs[0].Data.Mouse.Dy = absY;
            inputs[0].Data.Mouse.Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                                         | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE_NOCOALESCE;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        static bool TrySendMouseButton(uint flag)
        {
            var inputs = new INPUT[1];
            inputs[0].Type = INPUT_MOUSE;
            inputs[0].Data.Mouse.Flags = flag;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        const int INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        /// <summary>
        /// 对按钮中心做真实 EventSystem.RaycastAll；命中 Graphic 可向上取 Button；
        /// 最上层可点击目标必须就是目标按钮。不创建伪 EventSystem，不直调 onClick。
        /// </summary>
        bool ClickChild(GameObject root, string child)
        {
            Transform t = root.transform.Find(child);
            Button btn = t != null
                ? t.GetComponent<Button>() ?? t.GetComponentInChildren<Button>()
                : null;
            if (btn == null || !btn.isActiveAndEnabled || !btn.interactable)
            {
                _obs("click-diag child=" + child + " missingOrDisabled=true");
                return false;
            }

            if (EventSystem.current == null)
            {
                _obs("click-diag child=" + child + " no-EventSystem");
                return false;
            }

            Canvas.ForceUpdateCanvases();
            Canvas canvas = btn.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            var rect = (RectTransform)btn.transform;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(
                camera, rect.TransformPoint(rect.rect.center));
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = screen
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            string topName = hits.Count > 0 ? hits[0].gameObject.name : "(none)";
            _obs("click-diag child=" + child
                 + " screen=" + screen.x.ToString("F1") + "," + screen.y.ToString("F1")
                 + " hits=" + hits.Count + " top=" + topName);

            if (hits.Count == 0)
                return false;

            // 命中 Graphic 子物体时向上取可点击目标；必须落在目标 Button 上（不被 modal 挡住）。
            GameObject receiver = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            if (receiver == null)
            {
                _obs("click-diag child=" + child + " no-click-handler");
                return false;
            }

            Button hitBtn = receiver.GetComponent<Button>() ?? receiver.GetComponentInParent<Button>();
            if (hitBtn == null || hitBtn.gameObject != btn.gameObject)
            {
                _obs("click-diag child=" + child + " topmost=" + receiver.name + " blocked=true");
                return false;
            }

            pointer.pointerCurrentRaycast = hits[0];
            ExecuteEvents.Execute(receiver, pointer, ExecuteEvents.pointerClickHandler);
            return true;
        }
    }
}
