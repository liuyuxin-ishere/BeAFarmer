using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Lua;
using UnityEngine;
using UnityEngine.UI;
using XLua;
using YooAsset;

namespace Game.Framework.Boot
{
    /// <summary>
    /// Lua 热更宿主。替换原 HybridCLR DLL/补元数据加载链。
    /// 默认 Editor Play / Player 使用内置 TextAsset（不依赖临时 HTTP）；
    /// CLI 可覆盖 CDN/缓存做 100→101 补丁验证。脚本 GUID 保持不变以修复场景引用。
    /// </summary>
    public sealed class HotfixLoader : MonoBehaviour, IAsyncLeaseHost
    {
        /// <summary>桥接契约名（与 ProtoBridge.Contract 整数契约不同）。</summary>
        public const string BridgeContract = BridgeApi.BridgeContractValue;

        /// <summary>宿主要求的协议版本。</summary>
        public const int HostProtoVersion = BridgeApi.RequiredProtoVersion;

        /// <summary>默认内置资源版本号（开发基包内的 Lua 基线）。</summary>
        public const string BuiltinResVersion = "100";

        /// <summary>YooAsset 包名（补丁路径专用，与 DefaultPackage 配置包分开）。</summary>
        public const string LuaPackageName = "GameLua";

        /// <summary>活动索引文件名（相对缓存根）。</summary>
        const string ActiveIndexFile = "active-index.json";

        /// <summary>批处理请求文件（跨域重载保留 report/quit，避免静态字段丢失）。</summary>
        const string CliRequestRelative = "Docs/Acceptance/xLuaMigration/20261020/MainProject/cli-boot-request.txt";

        /// <summary>0=空闲，1=本进程已占用启动门闸（Start 或 StartAsync）。</summary>
        static int s_bootGate;

        /// <summary>
        /// 默认 MonoBehaviour.Start 是否自动启动。生产侧若走公开 StartAsync，请在 Inspector/代码中设为 false，
        /// 避免与 Start 并发两次。
        /// </summary>
        [SerializeField] bool autoStart = true;

        [SerializeField] Text statusText;

        readonly Dictionary<string, byte[]> _modules = new Dictionary<string, byte[]>();
        readonly List<AsyncLease> _trackedLeases = new List<AsyncLease>();
        LuaEnv _luaEnv;
        ILuaEntry _entryDelegate;
        LuaTable _entryTable;
        AsyncLease _liveLease;
        AsyncLease _cancelLease;
        string _entryModule = "entry";
        string _entrySummary = "";
        string _flowError = "";
        string _cacheRoot = "";
        string _cdnRoot = "";
        string _reportPath = "";
        /// <summary>可选截图路径（CLI --lua-shot=）；在退出前 CaptureScreenshot。</summary>
        string _shotPath = "";
        bool _useYooHost;
        bool _quitWhenDone;
        bool _injectIndexFail;
        bool _injectNestedThrow;
        /// <summary>期望拒绝阶段（CLI --lua-expect-reject=）；空表示成功路径。</summary>
        string _expectRejectStage = "";
        string _rejectStage = "";
        string _packageVersion = "";
        string _activeIndexShaBefore = "";
        string _activeIndexShaAfter = "";
        bool _entryExecutedAtReject;
        CancellationToken _bootCt;
        bool _cancelled;
        bool _sideEffectRolledBack;
        int _panelBaseline;
        int _mainThreadId;
        int _asyncFrames;
        int _cancelWaitFrames;
        int _asyncBeforeDispose;
        int _asyncAfterDispose;
        int _entryInvocations;
        string _liveProducer = "";
        string _postDisposeLiveId = "";
        string _disposeError = "";
        bool _cancelRegistered;
        bool _handleReleased = true;
        Coroutine _bootRoutine;
        /// <summary>StartAsync 完成回调；宿主销毁时也必须触发以免永久 Pending。</summary>
        Action _startAsyncDone;
        Exception _startAsyncError;

        /// <summary>测试用：提交前额外等待帧数；&gt;0 时进入可控屏障，供中途取消/销毁验证。</summary>
        static int s_testPreCommitWaitFrames;

        /// <summary>测试用：当前是否停在提交前等待屏障（已跨帧进入待完成启动）。</summary>
        public static bool TestPreCommitWaiting { get; private set; }

        /// <summary>测试钩子：替换 File.Replace。返回 false 表示模拟写失败。null 走真实 Replace。</summary>
        public static Func<string, string, string, bool> IndexReplaceOverride;

        /// <summary>
        /// EditMode/工具用：对已有索引执行与正式路径相同的原子提交。injectFail 为真时写临时文件后抛错，原索引须保留。
        /// 返回 null 表示成功，否则为错误原因。
        /// </summary>
        public static string TryCommitActiveIndexForTest(string cacheRoot, RuntimeCatalog catalog, bool injectFail)
        {
            if (catalog == null)
                return "catalog-null";
            Directory.CreateDirectory(cacheRoot);
            var finalPath = Path.Combine(cacheRoot, ActiveIndexFile);
            var tempPath = finalPath + ".tmp";
            var backupPath = finalPath + ".bak";
            var json = "{\"activeVersion\":\"" + catalog.resVersion
                + "\",\"entry\":\"" + catalog.entryModule
                + "\",\"bridgeContract\":\"" + catalog.bridgeContract + "\"}\n";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            try
            {
                if (injectFail)
                    throw new IOException("inject-index-fail");

                if (File.Exists(finalPath))
                {
                    bool ok;
                    if (IndexReplaceOverride != null)
                        ok = IndexReplaceOverride(tempPath, finalPath, backupPath);
                    else
                    {
                        File.Replace(tempPath, finalPath, backupPath);
                        ok = true;
                    }

                    if (!ok)
                        throw new IOException("index-replace-failed");
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }

                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // ignore
                }

                return "index-commit-failed " + ex.Message;
            }
        }

        /// <summary>最近一次入口汇总，供验证报告读取。</summary>
        public string LastEntrySummary => _entrySummary;

        /// <summary>最近一次流程错误；空表示成功。</summary>
        public string LastFlowError => _flowError;

        /// <summary>异步跨帧数（live 租约）。</summary>
        public int LastAsyncFrames => _asyncFrames;

        /// <summary>取消路径等待帧数。</summary>
        public int LastCancelWaitFrames => _cancelWaitFrames;

        /// <summary>Dispose 前后 AsyncCount。</summary>
        public int AsyncBeforeDispose => _asyncBeforeDispose;

        /// <summary>Dispose 后 AsyncCount。</summary>
        public int AsyncAfterDispose => _asyncAfterDispose;

        /// <summary>入口调用次数。</summary>
        public int EntryInvocations => _entryInvocations;

        /// <summary>是否回滚了宿主跟踪面板。</summary>
        public bool SideEffectRolledBack => _sideEffectRolledBack;

        /// <summary>Dispose 错误文本；空表示无异常。</summary>
        public string DisposeError => _disposeError;

        /// <summary>live 租约 ProducerId（启动时）。</summary>
        public string LiveProducerId => _liveProducer;

        /// <summary>Dispose 后再 Pump 时的 ProducerId。</summary>
        public string PostDisposeLiveId => _postDisposeLiveId;

        /// <summary>取消租约是否曾登记。</summary>
        public bool CancelRegistered => _cancelRegistered;

        /// <summary>Yoo 句柄是否已释放（内置路径恒为 true）。</summary>
        public bool HandleReleased => _handleReleased;

        /// <summary>默认/CLI 启动协程是否已跑完（含异步推进与 Dispose）。</summary>
        public bool BootFinished { get; private set; }

        /// <summary>本进程启动门闸是否已被占用。</summary>
        public static bool BootGateTaken => s_bootGate != 0;

        /// <summary>仅测试用：重置进程启动门闸。正式运行勿调用。</summary>
        public static void ResetBootGateForTests()
        {
            Interlocked.Exchange(ref s_bootGate, 0);
        }

        /// <summary>仅测试用：关闭自动 Start，避免与 StartAsync 抢门闸。</summary>
        public void SetAutoStartForTests(bool enabled)
        {
            autoStart = enabled;
        }

        /// <summary>
        /// 仅测试用：武装提交前等待屏障。frames 为额外 yield 帧数；0 清除。
        /// 取消/销毁时须在 TestPreCommitWaiting==true 且 task 仍 Pending 时触发。
        /// </summary>
        public static void ArmPreCommitWaitForTests(int frames)
        {
            s_testPreCommitWaitFrames = frames < 0 ? 0 : frames;
            TestPreCommitWaiting = false;
        }

        /// <summary>宿主拥有权：构造期登记的租约加入跟踪表，环境关闭时一并退订。</summary>
        public void RegisterOwnedLease(AsyncLease lease)
        {
            if (lease == null)
                return;
            if (!_trackedLeases.Contains(lease))
                _trackedLeases.Add(lease);
        }

        /// <summary>
        /// 由 Editor 批处理在进入 Play 前调用。写入请求文件，避免 EnterPlaymode 域重载丢掉静态字段。
        /// reportPath 为报告文件；quitWhenDone 为真时结束后退出进程。
        /// </summary>
        public static void RequestCliReport(string reportPath, bool quitWhenDone)
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", CliRequestRelative));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,
                "report=" + (reportPath ?? "") + "\nquit=" + (quitWhenDone ? "1" : "0") + "\n",
                new UTF8Encoding(false));
        }

        void Start()
        {
            if (!autoStart)
                return;
            // 正式 LuaHost / 进度 UI 验收占用进程时，跳过旧宿主自动启动。
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "--m1-lua-host", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "--m1-patch-ui-review", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[Lua] autoStart skipped — m1 review host owns process");
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref s_bootGate, 1, 0) != 0)
            {
                Debug.Log("[Lua] autoStart skipped — boot gate already taken");
                return;
            }

            _bootRoutine = StartCoroutine(BootDefault(CancellationToken.None));
        }

        /// <summary>
        /// 默认启动：解析 CLI 覆盖；无覆盖则加载 Assets/Lua 或 StreamingAssets/Lua 内置模块。
        /// ct 在中途取消时停止候选流程并清理，不提交索引。
        /// </summary>
        IEnumerator BootDefault(CancellationToken ct)
        {
            _bootCt = ct;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            ParseCliOverrides();
            _activeIndexShaBefore = HashActiveIndexFile();
            Exception caught = null;
            // 嵌套异常注入在 ActivateCandidate 提交之前，不得包住整个 body（否则可能已 Commit）。
            IEnumerator body = _useYooHost ? BootFromYooHost() : BootFromBuiltin();
            yield return DriveStack(body, e => caught = e);
            _activeIndexShaAfter = HashActiveIndexFile();
            _entryExecutedAtReject = BridgeApi.EntryExecuted;

            if (_cancelled || ct.IsCancellationRequested)
            {
                Fail("boot-cancelled");
                CleanupAfterFailure();
            }
            else if (caught != null)
            {
                Fail(caught.Message);
                CleanupAfterFailure();
            }

            if (string.IsNullOrEmpty(_flowError))
                SetStatus(BuildStatusOk());
            else
                SetStatus("[Lua] FAILED: " + _flowError);

            WriteCliReportIfNeeded();
            if (!string.IsNullOrEmpty(_shotPath))
            {
                // CaptureScreenshot 在帧末写入；退出前多等两帧确保文件落盘。
                try
                {
                    var dir = Path.GetDirectoryName(_shotPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    ScreenCapture.CaptureScreenshot(_shotPath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Lua] screenshot failed: " + ex.Message);
                }

                yield return new WaitForEndOfFrame();
                yield return null;
            }

            BootFinished = true;
            var exitOk = string.IsNullOrEmpty(_expectRejectStage)
                ? string.IsNullOrEmpty(_flowError)
                : (!string.IsNullOrEmpty(_reportPath) && File.Exists(_reportPath) &&
                   File.ReadAllText(_reportPath).IndexOf("pass=True", StringComparison.Ordinal) >= 0);
            if (_quitWhenDone)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.Exit(exitOk ? 0 : 4);
#else
                Application.Quit(exitOk ? 0 : 4);
#endif
            }
        }

        /// <summary>
        /// 生产入口：校验 BootContext/Versions、取消、本进程只初始化一次，然后走与默认相同的宿主逻辑。
        /// 未实现的 Launcher/登录 UI 仍未实现。请将 autoStart=false 以免与 Start 双启动。
        /// </summary>
        /// <param name="context">基包准备好的资源、网络、事件和版本。</param>
        /// <param name="ct">启动取消令牌。</param>
        public async UniTask StartAsync(BootContext context, CancellationToken ct)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (context.Versions == null)
                throw new ArgumentException("BootContext.Versions is required.", nameof(context));

            ct.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref s_bootGate, 1, 0) != 0)
                throw new InvalidOperationException("HotfixLoader boot already started in this process.");

            autoStart = false;
            Debug.Log(
                "[Lua] StartAsync OK — app=" + Application.version +
                " bridgeContract=" + BridgeContract +
                ", Res=" + context.Versions.ResVersion +
                ", Proto=" + context.Versions.ProtoVersion +
                ", Config=" + context.Versions.ConfigVersion);

            // TODO T42: await ConfigService.LoadAsync(...)
            // TODO T48/T54/T56: UI / Launcher — 仍未实现

            var done = false;
            Exception caught = null;
            _startAsyncDone = () => done = true;
            _startAsyncError = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.GetCancellationTokenOnDestroy()))
            {
                _bootRoutine = StartCoroutine(FinishWhenDone(BootDefault(linked.Token), () =>
                {
                    done = true;
                    _startAsyncDone = null;
                }, e =>
                {
                    caught = e;
                    _startAsyncError = e;
                }));
                try
                {
                    await UniTask.WaitUntil(() => done, cancellationToken: linked.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!done)
                        AbortBootForDestroyOrCancel();
                    throw;
                }
                finally
                {
                    if (!done && _bootRoutine != null)
                        AbortBootForDestroyOrCancel();
                }
            }

            ct.ThrowIfCancellationRequested();
            if (caught != null)
                throw caught;
            if (_startAsyncError != null)
                throw _startAsyncError;
            if (!string.IsNullOrEmpty(_flowError))
                throw new InvalidOperationException(_flowError);

            SetStatus("[Lua] StartAsync completed — " + _entrySummary);
            await UniTask.CompletedTask;
        }

        /// <summary>取消或宿主销毁时停止协程、清理资源并结束等待。</summary>
        void AbortBootForDestroyOrCancel()
        {
            _cancelled = true;
            if (_bootRoutine != null)
            {
                try
                {
                    StopCoroutine(_bootRoutine);
                }
                catch
                {
                    // 对象已销毁时 Stop 可能无效
                }

                _bootRoutine = null;
            }

            CleanupAfterFailure();
            BootFinished = true;
            TestPreCommitWaiting = false;
            ArmPreCommitWaitForTests(0);
            var notify = _startAsyncDone;
            _startAsyncDone = null;
            notify?.Invoke();
            Interlocked.Exchange(ref s_bootGate, 0);
        }

        void OnDestroy()
        {
            // 宿主销毁：结束 StartAsync 等待并释放 Lua/租约，避免永久 Pending。
            if (!BootFinished || _bootRoutine != null)
                AbortBootForDestroyOrCancel();
            else
            {
                CleanupAfterFailure();
                AsyncLease.EndHostScope(this);
            }
        }

        /// <summary>包装 BootDefault，把异常托出并保证 onDone。</summary>
        static IEnumerator FinishWhenDone(IEnumerator body, Action onDone, Action<Exception> onError)
        {
            Exception caught = null;
            yield return DriveStack(body, e => caught = e);
            if (caught != null)
                onError(caught);
            onDone();
        }

        /// <summary>
        /// 栈式驱动嵌套 IEnumerator。任一 MoveNext 异常向宿主托出，并 Dispose 栈上剩余枚举器。
        /// </summary>
        /// <param name="root">根协程。</param>
        /// <param name="onError">捕获到的异常；可为 null。</param>
        public static IEnumerator DriveStack(IEnumerator root, Action<Exception> onError)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                object current = null;
                bool moved;
                try
                {
                    moved = top.MoveNext();
                    if (moved)
                        current = top.Current;
                }
                catch (Exception ex)
                {
                    DisposeStack(stack);
                    onError?.Invoke(ex);
                    yield break;
                }

                if (!moved)
                {
                    DisposeEnumerator(top);
                    stack.Pop();
                    continue;
                }

                if (current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }

                yield return current;
            }
        }

        /// <summary>
        /// EditMode 低层单测用：外层跑完 prefix 后再抛内层异常。
        /// 真实事务边界验证不得用此包住整个启动体（可能已 Commit）；提交前注入见 ActivateCandidate。
        /// </summary>
        public static IEnumerator NestedThrowProbe(IEnumerator prefix)
        {
            if (prefix != null)
                yield return prefix;
            yield return NestedThrowInner();
        }

        /// <summary>提交前嵌套 yield 后抛错；由 DriveStack 捕获，证明未提交。</summary>
        static IEnumerator NestedThrowInner()
        {
            yield return null;
            throw new InvalidOperationException("inject-nested-enumerator");
        }

        static void DisposeStack(Stack<IEnumerator> stack)
        {
            while (stack.Count > 0)
                DisposeEnumerator(stack.Pop());
        }

        static void DisposeEnumerator(IEnumerator enumerator)
        {
            if (enumerator is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // finally 失败不掩盖主异常
                }
            }
        }

        /// <summary>
        /// 解析命令行。支持 --lua-cdn= --lua-cache= --lua-report= --lua-quit
        /// --lua-inject-index-fail --lua-inject-nested-throw。
        /// </summary>
        void ParseCliOverrides()
        {
            _useYooHost = false;
            _quitWhenDone = false;
            _injectIndexFail = false;
            _injectNestedThrow = false;
            _expectRejectStage = "";
            _rejectStage = "";
            _cdnRoot = "";
            _reportPath = "";
            _cacheRoot = Path.Combine(Application.persistentDataPath, "lua-cache-default");
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--lua-cdn=", StringComparison.OrdinalIgnoreCase))
                {
                    _cdnRoot = arg.Substring("--lua-cdn=".Length).TrimEnd('/');
                    _useYooHost = !string.IsNullOrEmpty(_cdnRoot);
                }
                else if (arg.StartsWith("--lua-cache=", StringComparison.OrdinalIgnoreCase))
                {
                    _cacheRoot = arg.Substring("--lua-cache=".Length);
                }
                else if (arg.StartsWith("--lua-report=", StringComparison.OrdinalIgnoreCase))
                {
                    _reportPath = arg.Substring("--lua-report=".Length);
                    _quitWhenDone = true;
                }
                else if (string.Equals(arg, "--lua-quit", StringComparison.OrdinalIgnoreCase))
                {
                    _quitWhenDone = true;
                }
                else if (string.Equals(arg, "--lua-inject-index-fail", StringComparison.OrdinalIgnoreCase))
                {
                    _injectIndexFail = true;
                }
                else if (string.Equals(arg, "--lua-inject-nested-throw", StringComparison.OrdinalIgnoreCase))
                {
                    // 在 CommitActiveIndex 之前注入嵌套异常（见 ActivateCandidate）。
                    _injectNestedThrow = true;
                }
                else if (arg.StartsWith("--lua-expect-reject=", StringComparison.OrdinalIgnoreCase))
                {
                    // 期望拒绝阶段名；用于候选失败用例，避免把任意早期错误当通过。
                    _expectRejectStage = arg.Substring("--lua-expect-reject=".Length);
                }
                else if (arg.StartsWith("--lua-shot=", StringComparison.OrdinalIgnoreCase))
                {
                    // 真实 Player 窗口截图路径；不得用独立 PoC 截图替代。
                    _shotPath = arg.Substring("--lua-shot=".Length);
                }
            }

            // 文件请求优先（Editor 批处理跨域重载）。
            var req = Path.GetFullPath(Path.Combine(Application.dataPath, "..", CliRequestRelative));
            if (File.Exists(req))
            {
                foreach (var line in File.ReadAllLines(req))
                {
                    if (line.StartsWith("report=", StringComparison.OrdinalIgnoreCase))
                        _reportPath = line.Substring("report=".Length);
                    else if (line.StartsWith("quit=", StringComparison.OrdinalIgnoreCase))
                        _quitWhenDone = line.Substring("quit=".Length).Trim() == "1";
                }

                try
                {
                    File.Delete(req);
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>写完整 editor/player 证明报告（含异步生命周期字段）。</summary>
        void WriteProofReport(string path, bool pass)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("pass=").Append(pass).Append('\n');
            sb.Append("error=").Append(_flowError).Append('\n');
            sb.Append("rejectStage=").Append(_rejectStage).Append('\n');
            sb.Append("expectReject=").Append(_expectRejectStage).Append('\n');
            sb.Append("packageVersion=").Append(_packageVersion).Append('\n');
            sb.Append("entry=").Append(_entrySummary).Append('\n');
            sb.Append("entryInvocations=").Append(_entryInvocations).Append('\n');
            sb.Append("entryExecuted=").Append(BridgeApi.EntryExecuted).Append('\n');
            sb.Append("entryExecutedAtReject=").Append(_entryExecutedAtReject).Append('\n');
            sb.Append("sideEffectRolledBack=").Append(_sideEffectRolledBack).Append('\n');
            sb.Append("asyncFrames=").Append(_asyncFrames).Append('\n');
            sb.Append("cancelRegistered=").Append(_cancelRegistered).Append('\n');
            sb.Append("cancelWaitFrames=").Append(_cancelWaitFrames).Append('\n');
            sb.Append("asyncBeforeDispose=").Append(_asyncBeforeDispose).Append('\n');
            sb.Append("asyncAfterDispose=").Append(_asyncAfterDispose).Append('\n');
            sb.Append("liveSubscriptions=").Append(AsyncLease.LiveSubscriptions).Append('\n');
            sb.Append("trackedLeases=").Append(_trackedLeases.Count).Append('\n');
            sb.Append("liveProducer=").Append(_liveProducer).Append('\n');
            sb.Append("postDisposeLiveId=").Append(_postDisposeLiveId).Append('\n');
            sb.Append("disposeError=").Append(_disposeError).Append('\n');
            sb.Append("activeIndexShaBefore=").Append(_activeIndexShaBefore).Append('\n');
            sb.Append("activeIndexShaAfter=").Append(_activeIndexShaAfter).Append('\n');
            sb.Append("indexUnchanged=").Append(_activeIndexShaBefore == _activeIndexShaAfter).Append('\n');
            sb.Append("panels=").Append(BridgeApi.PanelDump()).Append('\n');
            sb.Append("appVersion=").Append(Application.version).Append('\n');
            sb.Append("bridgeContract=").Append(BridgeContract).Append('\n');
            sb.Append("protoVersion=").Append(HostProtoVersion).Append('\n');
            sb.Append("integerContract=").Append(ProtoBridge.Contract).Append('\n');
            sb.Append("transport=UnityWebRequestBackend").Append('\n');
            sb.Append("sandbox=host-tracked-panels-only").Append('\n');
            sb.Append("gcForced=0").Append('\n');
            sb.Append("note=vendor LuaEnv.Dispose may still GC internally; project does not force GC").Append('\n');
            sb.Append("ENABLE_IL2CPP=")
#if ENABLE_IL2CPP
                .Append('1')
#else
                .Append('0')
#endif
                .Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>CLI/批处理报告：供解析，不替代编辑器截图。</summary>
        void WriteCliReportIfNeeded()
        {
            if (string.IsNullOrEmpty(_reportPath))
                return;

            bool pass;
            if (!string.IsNullOrEmpty(_expectRejectStage))
            {
                // 拒绝用例：阶段必须匹配；索引不得因失败候选而改变；任意早期错误不算通过。
                pass = string.Equals(_rejectStage, _expectRejectStage, StringComparison.Ordinal)
                       && !string.IsNullOrEmpty(_flowError)
                       && _activeIndexShaBefore == _activeIndexShaAfter
                       && string.IsNullOrEmpty(_disposeError)
                       && AsyncLease.LiveSubscriptions == 0;
            }
            else
            {
                pass = string.IsNullOrEmpty(_flowError)
                       && BridgeApi.EntryExecuted
                       && _asyncFrames >= 1
                       && _cancelRegistered
                       && _asyncBeforeDispose == 1
                       && _asyncAfterDispose == 1
                       && _liveProducer == _postDisposeLiveId
                       && string.IsNullOrEmpty(_disposeError);
            }

            WriteProofReport(_reportPath, pass);
        }

        /// <summary>活动索引文件 SHA256；不存在返回 empty。</summary>
        string HashActiveIndexFile()
        {
            try
            {
                var path = Path.Combine(_cacheRoot, ActiveIndexFile);
                if (!File.Exists(path))
                    return "empty";
                return HashUtil.Sha256(File.ReadAllBytes(path));
            }
            catch
            {
                return "error";
            }
        }

        /// <summary>从 Assets/Lua（编辑器）或 StreamingAssets/Lua（Player）加载并跑通入口。</summary>
        IEnumerator BootFromBuiltin()
        {
            if (IsCancel())
                yield break;

            var catalog = BuildBuiltinCatalog();
            var loaded = new List<LoadedModule>();
            foreach (var module in catalog.modules)
            {
                if (IsCancel())
                    yield break;
                byte[] bytes = ReadBuiltinBytes(module.address);
                if (bytes == null)
                {
                    Fail("missing module " + module.name + " address " + module.address);
                    yield break;
                }

                var hash = HashUtil.Sha256(bytes);
                if (!string.Equals(hash, module.sha256, StringComparison.OrdinalIgnoreCase))
                    module.sha256 = hash;

                loaded.Add(new LoadedModule { Name = module.name, Bytes = bytes });
            }

            byte[] configBytes = ReadBuiltinBytes(catalog.configAddress);
            if (configBytes == null)
            {
                Fail("config load failed");
                yield break;
            }

            catalog.configSha256 = HashUtil.Sha256(configBytes);
            yield return ActivateCandidate(catalog, loaded, configBytes);
        }

        /// <summary>Yoo Host：预加载→复制校验→CustomLoader→入口→成功才提交活动索引。</summary>
        IEnumerator BootFromYooHost()
        {
            Directory.CreateDirectory(_cacheRoot);
            if (!YooAssets.IsInitialized)
                YooAssets.Initialize();

            ResourcePackage package;
            if (!YooAssets.TryGetPackage(LuaPackageName, out package) || package == null)
                package = YooAssets.CreatePackage(LuaPackageName);

            var options = new HostPlayModeOptions();
            options.BuiltinFileSystemParameters = null;
            options.CacheFileSystemParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                new LoopbackRemote(_cdnRoot),
                _cacheRoot);
            var init = package.InitializePackageAsync(options);
            yield return WaitOp(init);
            if (IsCancel())
                yield break;
            if (init.Status != EOperationStatus.Succeeded)
            {
                Fail("initialize failed: " + init.Error);
                yield break;
            }

            var versionOp = package.RequestPackageVersionAsync(new RequestPackageVersionOptions(false, 30));
            yield return WaitOp(versionOp);
            if (IsCancel())
                yield break;
            if (versionOp.Status != EOperationStatus.Succeeded)
            {
                Fail("request-version failed: " + versionOp.Error);
                yield break;
            }

            var manifest = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(versionOp.PackageVersion, 30));
            yield return WaitOp(manifest);
            if (IsCancel())
                yield break;
            if (manifest.Status != EOperationStatus.Succeeded)
            {
                Fail("load-manifest failed: " + manifest.Error);
                yield break;
            }

            var downloader = package.CreateResourceDownloader(new ResourceDownloaderOptions(4, 2));
            if (downloader.TotalDownloadCount > 0)
            {
                downloader.StartDownload();
                yield return WaitOp(downloader);
                if (IsCancel())
                    yield break;
                if (downloader.Status != EOperationStatus.Succeeded)
                {
                    Fail("download failed: " + downloader.Error);
                    yield break;
                }
            }

            var catalogHandle = package.LoadAssetAsync<TextAsset>("catalog");
            yield return WaitHandle(catalogHandle);
            if (IsCancel())
            {
                if (catalogHandle != null && catalogHandle.IsValid)
                    catalogHandle.Release();
                yield break;
            }

            if (!catalogHandle.IsValid || catalogHandle.Status != EOperationStatus.Succeeded || catalogHandle.AssetObject == null)
            {
                Fail("catalog load failed");
                catalogHandle.Release();
                yield break;
            }

            var catalogBytes = CopyBytes(catalogHandle.GetAssetObject<TextAsset>().bytes);
            catalogHandle.Release();
            _handleReleased = !catalogHandle.IsValid;
            var catalog = JsonUtility.FromJson<RuntimeCatalog>(Encoding.UTF8.GetString(catalogBytes));
            if (catalog == null || catalog.modules == null)
            {
                Fail("catalog json invalid");
                yield break;
            }

            var loaded = new List<LoadedModule>();
            foreach (var module in catalog.modules)
            {
                if (IsCancel())
                    yield break;
                var handle = package.LoadAssetAsync<TextAsset>(module.address);
                yield return WaitHandle(handle);
                if (IsCancel())
                {
                    if (handle != null && handle.IsValid)
                        handle.Release();
                    yield break;
                }

                if (!handle.IsValid || handle.Status != EOperationStatus.Succeeded || handle.AssetObject == null)
                {
                    handle.Release();
                    Fail("missing module " + module.name + " address " + module.address);
                    yield break;
                }

                var bytes = CopyBytes(handle.GetAssetObject<TextAsset>().bytes);
                handle.Release();
                if (handle.IsValid)
                {
                    Fail("handle still valid after release " + module.address);
                    yield break;
                }

                var hash = HashUtil.Sha256(bytes);
                if (!string.Equals(hash, module.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Fail("hash mismatch " + module.name + " actual " + hash);
                    yield break;
                }

                loaded.Add(new LoadedModule { Name = module.name, Bytes = bytes });
            }

            var configHandle = package.LoadAssetAsync<TextAsset>(catalog.configAddress);
            yield return WaitHandle(configHandle);
            if (IsCancel())
            {
                if (configHandle != null && configHandle.IsValid)
                    configHandle.Release();
                yield break;
            }

            if (!configHandle.IsValid || configHandle.Status != EOperationStatus.Succeeded || configHandle.AssetObject == null)
            {
                configHandle.Release();
                Fail("config load failed");
                yield break;
            }

            var configBytes = CopyBytes(configHandle.GetAssetObject<TextAsset>().bytes);
            configHandle.Release();
            if (!string.Equals(HashUtil.Sha256(configBytes), catalog.configSha256, StringComparison.OrdinalIgnoreCase))
            {
                Fail("config hash mismatch");
                yield break;
            }

            yield return ActivateCandidate(catalog, loaded, configBytes);
        }

        /// <summary>校验入口/契约→语法→跑 Lua→成功才提交活动索引。</summary>
        IEnumerator ActivateCandidate(RuntimeCatalog catalog, List<LoadedModule> loaded, byte[] configBytes)
        {
            if (IsCancel())
                yield break;

            var entryError = ValidateCandidate(catalog, loaded, configBytes);
            if (entryError != null)
            {
                Fail(entryError);
                yield break;
            }

            var syntaxError = CompileOnly(loaded);
            if (syntaxError != null)
            {
                Fail("syntax " + syntaxError);
                yield break;
            }

            if (IsCancel())
                yield break;

            _entryModule = catalog.entryModule;
            BridgeApi.Reset(catalog.resVersion);
            _modules.Clear();
            foreach (var module in loaded)
            {
                _modules[module.Name] = module.Bytes;
                BridgeApi.AddModule(module.Name);
            }

            BridgeApi.ApplyConfig(Encoding.UTF8.GetString(configBytes));
            EnsurePanelHost();
            _packageVersion = catalog.resVersion ?? "";
            yield return RunLua();
            if (!string.IsNullOrEmpty(_flowError) || IsCancel())
                yield break;

            // 提交前可控等待：中途取消/宿主销毁验证必须在此仍 Pending。
            var waitFrames = s_testPreCommitWaitFrames;
            if (waitFrames > 0)
            {
                TestPreCommitWaiting = true;
                for (var i = 0; i < waitFrames; i++)
                {
                    if (IsCancel())
                    {
                        TestPreCommitWaiting = false;
                        yield break;
                    }

                    yield return null;
                }

                TestPreCommitWaiting = false;
            }

            // 真实事务边界：入口已跑完、尚未 Commit 时注入嵌套异常。
            if (_injectNestedThrow)
            {
                yield return NestedThrowInner();
                yield break;
            }

            if (_useYooHost)
                CommitActiveIndex(catalog);
        }

        /// <summary>
        /// 校验候选 catalog：委托正式 <see cref="LuaLoader.ValidateCandidate"/>（结构级）。
        /// 返回 null 表示可继续。
        /// </summary>
        public static string ValidateCandidate(RuntimeCatalog catalog, List<LoadedModule> loaded, byte[] configBytes)
        {
            return LuaLoader.ValidateCandidate(catalog, loaded, configBytes);
        }

        /// <summary>只编译不执行。返回 null 表示语法通过。</summary>
        static string CompileOnly(List<LoadedModule> loaded)
        {
            var checkEnv = new LuaEnv();
            try
            {
                foreach (var module in loaded)
                {
                    LuaFunction compiled = null;
                    try
                    {
                        compiled = checkEnv.LoadString(Encoding.UTF8.GetString(module.Bytes), module.Name);
                    }
                    catch (Exception ex)
                    {
                        return module.Name + ": " + ex.Message;
                    }
                    finally
                    {
                        if (compiled != null)
                            compiled.Dispose();
                    }
                }
            }
            finally
            {
                checkEnv.Tick();
                checkEnv.Dispose();
            }

            return null;
        }

        /// <summary>创建正式 LuaEnv，执行入口，并在至少一帧后推进租约，再关闭环境并验证原生产者。</summary>
        IEnumerator RunLua()
        {
            AsyncLease.BeginHostScope(this);
            _luaEnv = new LuaEnv();
            try
            {
                BootEntry();
            }
            catch (Exception ex)
            {
                Fail(ex.Message.StartsWith("runtime-", StringComparison.Ordinal)
                    ? ex.Message
                    : "runtime-start " + ex.Message);
                CleanupLeasesAndOwned(rollbackPanels: true);
            }

            if (string.IsNullOrEmpty(_flowError) && !IsCancel())
            {
                yield return null;
                if (IsCancel())
                {
                    CleanupLeasesAndOwned(rollbackPanels: false);
                    yield break;
                }

                try
                {
                    AdvanceAsync();
                }
                catch (Exception ex)
                {
                    Fail("async " + ex.Message);
                    CleanupLeasesAndOwned(rollbackPanels: true);
                }
            }

            DisposeEnv();
            AsyncLease.EndHostScope(this);
        }

        /// <summary>require 入口并调用 Start。失败时回滚面板并收集已登记租约。</summary>
        void BootEntry()
        {
            _panelBaseline = BridgeApi.PanelCount;
            try
            {
                _luaEnv.AddLoader(LoadModule);
                _luaEnv.DoString("Entry = require('" + _entryModule + "')", "boot");
            }
            catch (Exception ex)
            {
                CaptureLeasesFromGlobal();
                BridgeApi.RollbackPanels(_panelBaseline);
                _sideEffectRolledBack = true;
                throw new InvalidOperationException("runtime-require " + ex.Message, ex);
            }

            _entryDelegate = _luaEnv.Global.Get<ILuaEntry>("Entry");
            _entryTable = _luaEnv.Global.Get<LuaTable>("Entry");
            _entryInvocations++;
            try
            {
                _entrySummary = _entryDelegate.Start("boot");
            }
            catch (Exception ex)
            {
                CaptureLeasesFromGlobal();
                BridgeApi.RollbackPanels(_panelBaseline);
                _sideEffectRolledBack = true;
                throw new InvalidOperationException("runtime-start " + ex.Message, ex);
            }
            finally
            {
                CaptureLeasesFromGlobal();
            }

            Debug.Log("entry " + _entrySummary);
            _liveProducer = _liveLease == null ? "" : _liveLease.ProducerId;
        }

        /// <summary>
        /// 从 Lua global 取命名租约引用（供成功路径 AdvanceAsync）。
        /// 不得 Clear 跟踪表：局部 new AsyncLease 已在构造时 RegisterOwnedLease。
        /// </summary>
        void CaptureLeasesFromGlobal()
        {
            if (_luaEnv == null)
                return;
            try
            {
                _liveLease = _luaEnv.Global.Get<AsyncLease>("async_live_lease");
                _cancelLease = _luaEnv.Global.Get<AsyncLease>("async_cancel_lease");
            }
            catch
            {
                // global 尚未写入时忽略
            }

            if (_liveLease != null && !_trackedLeases.Contains(_liveLease))
                _trackedLeases.Add(_liveLease);
            if (_cancelLease != null
                && !ReferenceEquals(_cancelLease, _liveLease)
                && !_trackedLeases.Contains(_cancelLease))
                _trackedLeases.Add(_cancelLease);
        }

        /// <summary>跨帧后取消一份共享回调租约，另一份仍可完成；关闭后再推进无回调。</summary>
        void AdvanceAsync()
        {
            if (_cancelLease == null || !_cancelLease.WasRegistered || _liveLease == null || !_liveLease.WasRegistered)
            {
                Fail("async lease was not registered");
                return;
            }

            // 同函数两租约：Target 可能共享；取消一份不得弄坏另一份。
            if (!ReferenceEquals(_cancelLease, _liveLease)
                && _cancelLease.WasRegistered && _liveLease.WasRegistered)
            {
                // 仅用于报告：共享策略已在 AsyncLease 中落实（不 Dispose Target）。
            }

            _cancelLease.NoteMainThreadFrame();
            _liveLease.NoteMainThreadFrame();
            _cancelWaitFrames = _cancelLease.FramesWhileSubscribed;
            _asyncFrames = _liveLease.FramesWhileSubscribed;
            if (_cancelWaitFrames < 1 || _asyncFrames < 1)
            {
                Fail("async did not cross a frame");
                return;
            }

            _cancelRegistered = true;
            _cancelLease.Unsubscribe();
            var before = BridgeApi.AsyncCount;
            _cancelLease.Pump();
            if (BridgeApi.AsyncCount != before)
            {
                Fail("cancelled lease invoked callback");
                return;
            }

            _liveLease.Pump();
            if (BridgeApi.AsyncCount != 1 || _liveLease.CallbackThreadId != _mainThreadId)
            {
                Fail("async count " + BridgeApi.AsyncCount + " thread " + _liveLease.CallbackThreadId);
                return;
            }

            _liveLease.Pump();
            if (BridgeApi.AsyncCount != 1)
                Fail("callback repeated");
        }

        /// <summary>失败路径：回滚面板（可选）、退订租约、释放表/接口、Dispose 环境。</summary>
        void CleanupLeasesAndOwned(bool rollbackPanels)
        {
            if (rollbackPanels)
            {
                BridgeApi.RollbackPanels(_panelBaseline);
                _sideEffectRolledBack = true;
            }

            foreach (var lease in _trackedLeases)
            {
                try
                {
                    lease?.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            _trackedLeases.Clear();
            DisposeOwnedLuaObjects();
        }

        void CleanupAfterFailure()
        {
            CleanupLeasesAndOwned(rollbackPanels: true);
            if (_luaEnv != null)
            {
                try
                {
                    _luaEnv.Tick();
                    _luaEnv.Dispose();
                }
                catch (Exception ex)
                {
                    _disposeError = ex.GetType().Name + ": " + ex.Message;
                }

                _luaEnv = null;
            }

            AsyncLease.EndHostScope(this);
            TestPreCommitWaiting = false;
        }

        void DisposeOwnedLuaObjects()
        {
            if (_entryDelegate != null)
            {
                try
                {
                    _entryDelegate.Shutdown();
                }
                catch
                {
                    // ignore Shutdown errors; still release LuaBase
                }
            }

            if (_entryDelegate is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // ignore
                }

                _entryDelegate = null;
            }

            if (_entryTable != null)
            {
                try
                {
                    _entryTable.Dispose();
                }
                catch
                {
                    // ignore
                }

                _entryTable = null;
            }
        }

        /// <summary>
        /// 先退订已登记生产者、清理拥有的表/生成接口，再 Dispose 环境。
        /// 关闭后再 Pump 同一对租约证明无回调。gcForced=0：项目未额外 GC；vendor Dispose 可能 GC。
        /// </summary>
        void DisposeEnv()
        {
            try
            {
                foreach (var lease in _trackedLeases)
                {
                    try
                    {
                        lease?.Unsubscribe();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                DisposeOwnedLuaObjects();

                if (_luaEnv != null)
                {
                    _luaEnv.Tick();
                    _asyncBeforeDispose = BridgeApi.AsyncCount;
                    _luaEnv.Dispose();
                    _disposeError = "";
                }
            }
            catch (Exception ex)
            {
                _disposeError = ex.GetType().Name + ": " + ex.Message;
                Fail("lua dispose " + _disposeError);
            }

            _luaEnv = null;
            if (_liveLease != null)
            {
                _liveLease.Pump();
                _postDisposeLiveId = _liveLease.ProducerId;
                _liveLease.Dispose();
            }

            if (_cancelLease != null)
            {
                _cancelLease.Pump();
                _cancelLease.Dispose();
            }

            _trackedLeases.Clear();
            _asyncAfterDispose = BridgeApi.AsyncCount;
        }

        /// <summary>CustomLoader。filepath 是 require 名。</summary>
        byte[] LoadModule(ref string filepath)
        {
            var key = filepath.Replace('\\', '/');
            if (key.EndsWith(".lua", StringComparison.Ordinal))
                key = key.Substring(0, key.Length - 4);

            if (_modules.TryGetValue(key, out var bytes))
                return bytes;
            return null;
        }

        /// <summary>
        /// 成功后才写活动索引。已有索引用同卷 File.Replace，不先删旧好索引。
        /// 写失败保留原索引。--lua-inject-index-fail 或 IndexReplaceOverride 可注入可控失败。
        /// </summary>
        void CommitActiveIndex(RuntimeCatalog catalog)
        {
            if (!string.Equals(catalog.bridgeContract, BridgeContract, StringComparison.Ordinal)
                || catalog.entryModule != _entryModule)
            {
                Fail("contract mismatch before commit");
                return;
            }

            var finalPath = Path.Combine(_cacheRoot, ActiveIndexFile);
            var tempPath = finalPath + ".tmp";
            var backupPath = finalPath + ".bak";
            var json = "{\"activeVersion\":\"" + catalog.resVersion
                + "\",\"entry\":\"" + catalog.entryModule
                + "\",\"bridgeContract\":\"" + catalog.bridgeContract
                + "\",\"requiredAppVersion\":\"" + catalog.requiredAppVersion
                + "\",\"protoVersion\":" + catalog.protoVersion
                + ",\"integerContract\":\"" + ProtoBridge.Contract + "\"}\n";

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            try
            {
                if (_injectIndexFail)
                    throw new IOException("inject-index-fail");

                if (File.Exists(finalPath))
                {
                    bool ok;
                    if (IndexReplaceOverride != null)
                        ok = IndexReplaceOverride(tempPath, finalPath, backupPath);
                    else
                    {
                        File.Replace(tempPath, finalPath, backupPath);
                        ok = true;
                    }

                    if (!ok)
                        throw new IOException("index-replace-failed");
                    try
                    {
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                    }
                    catch
                    {
                        // 备份删不掉不影响已提交索引
                    }
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }

                Debug.Log("committed active " + catalog.resVersion);
            }
            catch (Exception ex)
            {
                Fail("index-commit-failed " + ex.Message);
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>确保场景里有状态文本时也能挂面板根；无 Canvas 则创建最小宿主 UI。</summary>
        void EnsurePanelHost()
        {
            if (statusText != null)
            {
                BridgeApi.BindPanelRoot(statusText.transform.parent != null
                    ? statusText.transform.parent
                    : statusText.transform, statusText.font);
                return;
            }

            var canvasObject = new GameObject("LuaHostCanvas");
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();
            var root = new GameObject("Panels");
            root.transform.SetParent(canvasObject.transform, false);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BridgeApi.BindPanelRoot(root.transform, font);
        }

        /// <summary>构建内置 100 基线目录（地址为 Assets/Lua 下文件名不含扩展）。</summary>
        RuntimeCatalog BuildBuiltinCatalog()
        {
            var modules = new[] { "entry", "panel_base", "proto_case", "async_case" };
            var list = new List<CatalogModule>();
            foreach (var name in modules)
            {
                var bytes = ReadBuiltinBytes(name);
                list.Add(new CatalogModule
                {
                    name = name,
                    address = name,
                    sha256 = bytes == null ? "" : HashUtil.Sha256(bytes)
                });
            }

            var cfg = ReadBuiltinBytes("cfg_runtime");
            return new RuntimeCatalog
            {
                resVersion = BuiltinResVersion,
                entryModule = "entry",
                configAddress = "cfg_runtime",
                configSha256 = cfg == null ? "" : HashUtil.Sha256(cfg),
                bridgeContract = BridgeContract,
                requiredAppVersion = Application.version,
                protoVersion = HostProtoVersion,
                modules = list.ToArray()
            };
        }

        /// <summary>读取内置 TextAsset 字节。address 是不含扩展的文件名。</summary>
        static byte[] ReadBuiltinBytes(string address)
        {
#if UNITY_EDITOR
            var editorPath = Path.Combine(Application.dataPath, "Lua", address + ".txt");
            if (File.Exists(editorPath))
                return File.ReadAllBytes(editorPath);
#endif
            var streaming = Path.Combine(Application.streamingAssetsPath, "Lua", address + ".txt");
            if (File.Exists(streaming))
                return File.ReadAllBytes(streaming);

            var resource = Resources.Load<TextAsset>("Lua/" + address);
            if (resource != null)
                return CopyBytes(resource.bytes);
            return null;
        }

        /// <summary>拼成功状态文本。</summary>
        string BuildStatusOk()
        {
            return "[Lua] Entry OK — " + _entrySummary + "\n" +
                   "appVersion=" + Application.version +
                   " bridgeContract=" + BridgeContract +
                   " asyncFrames=" + _asyncFrames +
                   " cancel=" + _cancelRegistered +
                   " panels=" + BridgeApi.PanelDump();
        }

        bool IsCancel()
        {
            if (_cancelled || _bootCt.IsCancellationRequested)
            {
                _cancelled = true;
                return true;
            }

            return false;
        }

        void Fail(string reason)
        {
            if (!string.IsNullOrEmpty(_flowError))
                return;
            _flowError = reason;
            _rejectStage = ClassifyReject(reason);
            _entryExecutedAtReject = BridgeApi.EntryExecuted;
            Debug.LogError("[Lua] " + reason);
        }

        /// <summary>将失败原因归类为可断言的拒绝阶段；未知早期错误归 early-environment。</summary>
        public static string ClassifyReject(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return "";
            if (reason.StartsWith("missing module", StringComparison.Ordinal)
                || reason.StartsWith("invalid-entry missing", StringComparison.Ordinal))
                return "missing-module";
            if (reason.StartsWith("syntax ", StringComparison.Ordinal))
                return "syntax";
            if (reason.StartsWith("catalog-bridgeContract", StringComparison.Ordinal))
                return "bad-bridge";
            if (reason.StartsWith("duplicate-module", StringComparison.Ordinal))
                return "duplicate-module";
            if (reason.StartsWith("runtime-require", StringComparison.Ordinal)
                || reason.StartsWith("runtime-start", StringComparison.Ordinal))
                return "runtime-start";
            if (reason.StartsWith("async ", StringComparison.Ordinal))
                return "async-callback";
            if (reason.IndexOf("inject-nested-enumerator", StringComparison.Ordinal) >= 0)
                return "iterator-fault";
            if (reason.StartsWith("index-commit-failed", StringComparison.Ordinal))
                return "index-write-fail";
            if (reason.IndexOf("DataFileCrcError", StringComparison.Ordinal) >= 0
                || reason.StartsWith("hash mismatch", StringComparison.Ordinal))
                return "corrupt-bundle";
            if (reason.StartsWith("boot-cancelled", StringComparison.Ordinal))
                return "cancelled";
            return "early-environment";
        }

        void SetStatus(string message)
        {
            Debug.Log(message);
            if (statusText != null)
                statusText.text = message;
        }

        /// <summary>等待 Yoo 操作完成；取消时提前结束等待。</summary>
        IEnumerator WaitOp(AsyncOperationBase op)
        {
            while (op != null && !op.IsDone)
            {
                if (IsCancel())
                    yield break;
                yield return null;
            }
        }

        /// <summary>等待资源句柄完成。</summary>
        IEnumerator WaitHandle(HandleBase handle)
        {
            while (handle != null && !handle.IsDone)
            {
                if (IsCancel())
                    yield break;
                yield return null;
            }
        }

        /// <summary>复制字节，避免释放句柄后缓冲失效。</summary>
        static byte[] CopyBytes(byte[] source)
        {
            if (source == null)
                return null;
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        /// <summary>已加载模块字节。</summary>
        public sealed class LoadedModule
        {
            public string Name;
            public byte[] Bytes;
        }

        /// <summary>把 YooAsset 文件名映射到环回 HTTP。传输仍是默认 UnityWebRequest。</summary>
        sealed class LoopbackRemote : IRemoteService
        {
            readonly string _cdn;

            /// <param name="cdn">不带末尾斜杠的资源根。</param>
            public LoopbackRemote(string cdn)
            {
                _cdn = (cdn ?? "").TrimEnd('/');
            }

            /// <param name="fileName">清单或 bundle 文件名。</param>
            public IReadOnlyList<string> GetRemoteUrls(string fileName)
            {
                return new[] { _cdn + "/" + fileName };
            }
        }
    }
}
