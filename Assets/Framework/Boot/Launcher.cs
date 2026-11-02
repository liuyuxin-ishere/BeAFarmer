using System;
using System.IO;
using System.Text;
using System.Threading;
using CloudIsle.Contracts;
using CloudIsle.Diagnostics;
using Cysharp.Threading.Tasks;
using Game.Framework.Account;
using Game.Framework.Config;
using Game.Framework.Core;
using Game.Framework.Gameplay;
using Game.Framework.Lua;
using Game.Framework.Resource;
using Game.Framework.Scenes;
using Game.Framework.UI;
using UnityEngine;
using YooAsset;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 正式启动串：版本检查 → 候选补丁 → 配置 → EnsureManager → Lua 入口 ready →
    /// 打开 Login 成功 → 提交活动索引 → login-ready。
    /// 不得在入口前缺 manager；Lua 只注册一次；取消/打开失败恢复磁盘索引字节。
    /// HTTP 默认 localhost，避免系统代理把 127.0.0.1 打成 502。
    /// </summary>
    public sealed class Launcher : IDisposable
    {
        public const string DefaultHttpBase = "http://localhost:18080/";
        public const string DefaultTcpHost = "127.0.0.1";
        public const int DefaultTcpPort = 17780;

        readonly string _httpBase;
        readonly string _cacheRoot;
        readonly string _indexRoot;
        readonly string _tcpHost;
        readonly int _tcpPort;
        readonly string _tokenDir;
        readonly string _pendingRoot;
        Action<string> _log;
        PatchService _patch;
        LuaLoader _lua;
        ResMgr _resources;
        ConfigService _config;
        SceneService _scenes;
        LoadingView _loading;
        PatchProgressView _patchView;
        FileLogger _fileLogger;
        CancellationTokenSource _cts;
        int _runGate;
        bool _disposed;
        bool _entryReady;
        bool _committed;
        bool _loginOpened;
        byte[] _indexSnapshotBeforeCommit;

        public bool EntryReady => _entryReady;
        public bool Committed => _committed;
        public bool LoginOpened => _loginOpened;
        public LuaLoader Lua => _lua;
        public PatchProgressView ActivePatchView => _patchView;
        public string LastError { get; private set; }
        /// <summary>关闭路径 Env/桥 Dispose 错误；空=验收通过。不可仅凭无异常判成功。</summary>
        public string DisposeError { get; private set; } = "";
        public string ActiveIndexPath => _patch?.ActiveIndexPath;
        /// <summary>最近一次 Prepare 差异文件数（验收 state）。</summary>
        public int LastPatchDownloadFiles { get; private set; }
        /// <summary>验收：外部命令在错误可见且按钮可交互时点真实 Retry（禁止默认自动点）。</summary>
        public bool TryAcceptClickPatchRetry()
        {
            if (_patchView == null || _patchView.RetryButton == null)
                return false;
            if (!_patchView.RetryButton.gameObject.activeInHierarchy || !_patchView.RetryButton.interactable)
                return false;
            return _patchView.TryClickRetryViaEventSystem();
        }

        public Launcher(
            string httpBase = null,
            string cacheRoot = null,
            string indexRoot = null,
            string tcpHost = null,
            int tcpPort = 0,
            string tokenDir = null,
            Action<string> log = null,
            string pendingRoot = null)
        {
            _httpBase = string.IsNullOrWhiteSpace(httpBase) ? DefaultHttpBase : EnsureSlash(httpBase);
            _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot)
                ? Path.Combine(Application.persistentDataPath, "m1-yoo")
                : cacheRoot;
            _indexRoot = string.IsNullOrWhiteSpace(indexRoot)
                ? Path.Combine(Application.persistentDataPath, "m1-index")
                : indexRoot;
            _tcpHost = string.IsNullOrWhiteSpace(tcpHost) ? DefaultTcpHost : tcpHost;
            _tcpPort = tcpPort > 0 ? tcpPort : DefaultTcpPort;
            _tokenDir = string.IsNullOrWhiteSpace(tokenDir)
                ? Path.Combine(Application.persistentDataPath, "m1-account")
                : tokenDir;
            // 验收隔离：pending 必须落在 accept-root，否则退出重开会串到机器默认 persistent 目录。
            _pendingRoot = string.IsNullOrWhiteSpace(pendingRoot)
                ? Path.Combine(Application.persistentDataPath, "m2-pending")
                : pendingRoot;
            _log = log ?? (s => Debug.Log("[Launcher] " + s));
        }

        /// <summary>串行启动至 Login。重复调用拒绝。</summary>
        public async UniTask RunAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _runGate, 1, 0) != 0)
                throw new InvalidOperationException("launcher-already-running");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationToken linked = _cts.Token;
            try
            {
                EnsureFileLogger();
                // T007：显式主线程 Dispatcher + SessionContext；无 port（T013）时 Lua 发送仍 NotReady。
                EnsureGameplayHost();
                _log("version-check " + _httpBase);
                var versions = new VersionService();
                VersionManifest manifest = await versions.FetchAsync(new Uri(_httpBase + "version.json"), linked);
                // 隔离验收常用动态 HTTP 端口；version.json 内 BaseUrl 可能仍写死 18080。
                // 仅在双方皆为 loopback 时把包 CDN 主机/端口对齐到本 Launcher HTTP 基址，保留路径。
                manifest = AlignLoopbackPackageBaseUrl(manifest, _httpBase);

                _log("patch-prepare");
                Directory.CreateDirectory(_cacheRoot);
                Directory.CreateDirectory(_indexRoot);
                // 编辑器：CDN 常仅有 GameLua（DefaultPackage Host 对齐在 T58）；Player 双包 HostHttp。
                _patch = new PatchService(
                    _indexRoot,
                    _cacheRoot,
#if UNITY_EDITOR
                    new[] { HotfixLoader.LuaPackageName }
#else
                    null
#endif
                );
                _indexSnapshotBeforeCommit = _patch.TryReadActiveIndexBytes();
                await PreparePatchWithUiAsync(manifest, linked);

                _log("resources-init");
                _resources = new ResMgr { PlayMode = ResolvePlayMode() };
                await _resources.InitializeAsync(manifest, linked);
                await EnsureDefaultPackageManifestAsync(linked);

                _log("config");
                _config = new ConfigService(_resources);
                await _config.LoadAsync(ConfigAddresses.Manifest, manifest.ConfigVersion, linked);
                BridgeApi.BindConfigService(_config);

                _log("lua-prepare");
                if (_lua != null)
                    throw new InvalidOperationException("lua-env-already");
                _lua = new LuaLoader();
#if UNITY_EDITOR
                // 编辑器：CDN GameLua 可能仍为旧 bridge；从 Assets/Lua 装载（CustomLoader 回落补全 UI/*）。
                // Player 正式包仍走 Yoo GameLua（T58 冻结 m1-bridge-0.3）。
                await PrepareEditorLuaFromDiskAsync(_lua, linked);
#else
                if (!YooAssets.TryGetPackage(HotfixLoader.LuaPackageName, out ResourcePackage luaPackage) || luaPackage == null)
                    luaPackage = YooAssets.GetPackage(HotfixLoader.LuaPackageName);
                var preload = await LuaModulePreloader.LoadFromPackageAsync(luaPackage, linked);
                await _lua.PrepareAsync(preload.Index, preload.Modules, preload.ConfigBytes, linked);
#endif

                _loading = LoadingView.Ensure();
                _scenes = new SceneService(_resources, _loading, "M1_Login");
                UiBridge.BindSceneService(_scenes);
                AccountFlow.Bind(_scenes);

                // Env 在 StartEntry 内创建；须在 Entry.Start（Lua Register）前 EnsureManager+Configure。
                VersionManifest captured = manifest;
                _lua.BeforeEntryStartAsync = env =>
                {
                    UiBridge.EnsureManager(_resources, env);
                    AccountBridge.Configure(
                        _tcpHost,
                        _tcpPort,
                        Application.version,
                        captured.ResVersion > 0 ? captured.ResVersion : 100,
                        captured.ConfigVersion > 0 ? captured.ConfigVersion : 200,
                        "m1-device",
                        _tokenDir);
                    // LuaEnv 已创建：挂载玩法 Runtime（Store/Transport/Client/Adapter/ModelPort）
                    AttachGameplayRuntime(env);
                    return UniTask.CompletedTask;
                };

                var boot = new BootContext
                {
                    Resources = _resources,
                    Events = new EventBus(),
                    Versions = manifest
                };
                await _lua.StartEntryAsync(boot, "boot", linked);
                if (!BridgeApi.EntryExecuted || _lua.Env == null)
                    throw new InvalidOperationException("entry-not-ready");
                _entryReady = true;

                try
                {
                    await _scenes.SwitchAsync("M1_Login", linked);
                }
                catch (Exception ex)
                {
                    // 编辑器无 Host 场景包时允许仅开窗；Player 必须有场景。
#if !UNITY_EDITOR
                    throw;
#else
                    _log("scene-switch-soft-fail " + ex.Message);
#endif
                }

                // Lua entry 已 Register；此处只打开并等待真实成功。
                var open = await OpenLoginAwait(linked);
                if (open.code != 0)
                    throw new InvalidOperationException("open-login-failed:" + open.code + ":" + open.payload);
                _loginOpened = true;

                _log("commit-index");
                _patch.CommitVersion(manifest);
                _committed = true;

                _log("login-ready");
            }
            catch (OperationCanceledException)
            {
                LastError = "cancelled";
                RollbackAfterFailure();
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
                RollbackAfterFailure();
                throw;
            }
            finally
            {
                // 保持 _runGate=1 直到 Dispose，避免双启。
            }
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Cancel();
            ReleaseOwningScope(disconnectStatics: true);
            Interlocked.Exchange(ref _runGate, 0);
        }

        /// <summary>
        /// 正式启动前补丁：进度 + 可见错误 + 单门闸重试。
        /// 普通模式：失败后保持错误 UI，仅等待用户真实点击 Retry（禁止默认自动点）。
        /// 验收：仅通过 TryAcceptClickPatchRetry（外部命令）点同一按钮。串行 Discard 后重下，无双下载。
        /// </summary>
        async UniTask PreparePatchWithUiAsync(VersionManifest manifest, CancellationToken ct)
        {
            if (_patchView == null)
                _patchView = PatchProgressView.CreateRuntime();
            PatchProgressView view = _patchView;
            bool success = false;
            try
            {
                var progress = new Progress<PatchProgress>(p =>
                {
                    try { view.Render(p); }
                    catch (Exception ex) { _log("patch-ui-render " + ex.Message); }
                });

                int attempt = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    attempt++;
                    try
                    {
                        _log("patch-prepare-attempt " + attempt);
                        await _patch.PrepareAsync(manifest, progress, ct);
                        LastPatchDownloadFiles = _patch.Candidate?.TotalDownloadFiles ?? 0;
                        success = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log("patch-prepare-fail attempt=" + attempt + " " + ex.GetBaseException().Message);
                        var retryDone = new UniTaskCompletionSource();
                        bool armed = false;
                        view.ShowError(ex.GetBaseException().Message ?? "download-failed", () =>
                        {
                            if (armed)
                                return;
                            armed = true;
                            retryDone.TrySetResult();
                        });
                        // 禁止 DelayFrame 自动点击：只等真实按钮（人或 accept 命令）。
                        using (ct.Register(() => retryDone.TrySetCanceled(ct)))
                            await retryDone.Task;

                        view.ReleaseRetryGate();
                        view.ClearError();
                        try { _patch.DiscardCandidate(); } catch { }
                    }
                }
            }
            finally
            {
                // 成功才销毁；失败/取消时保留错误视图直到 Dispose，便于用户看见。
                if (success && _patchView != null)
                {
                    try { UnityEngine.Object.Destroy(_patchView.gameObject); } catch { }
                    _patchView = null;
                }
            }
        }

        async UniTask<(int code, string payload)> OpenLoginAwait(CancellationToken ct)
        {
            var tcs = new UniTaskCompletionSource<(int code, string payload)>();
            CallbackLease lease = UiBridge.Open("Login", "", (code, payload) =>
            {
                tcs.TrySetResult((code, payload ?? ""));
            });
            using (ct.Register(() =>
                   {
                       try { lease.Cancel("cancelled"); } catch { }
                       tcs.TrySetCanceled(ct);
                   }))
            {
                return await tcs.Task;
            }
        }

        void RollbackAfterFailure()
        {
            try { _patch?.DiscardCandidate(); } catch { }
            // 若已 Commit，按启动前字节恢复；未 Commit 则磁盘本就未改。
            if (_committed && _patch != null)
            {
                try
                {
                    _patch.RestoreActiveIndexSnapshot(_indexSnapshotBeforeCommit);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Launcher] index restore failed: " + ex.Message);
                }
            }

            _committed = false;
            _entryReady = false;
            _loginOpened = false;
            // 与 Dispose 同一拥有者释放顺序；失败路径也须可观察 DisposeError。
            ReleaseOwningScope(disconnectStatics: true);
        }

        /// <summary>
        /// 统一释放：停账号操作与场景切换 → UI 控制器/点击/租约 → AccountBridge 全局回调 →
        /// 同一 LuaEnv → 断开 UiBridge.Scenes / AccountFlow 静态绑定。
        /// </summary>
        void EnsureFileLogger()
        {
            if (_fileLogger != null)
                return;
            try
            {
                string dir = Path.Combine(_cacheRoot, "..", "client-logs");
                dir = Path.GetFullPath(dir);
                _fileLogger = new FileLogger();
                _fileLogger.Initialize(dir, 2 * 1024 * 1024, 5);
                Action<string> prev = _log;
                _log = s =>
                {
                    try
                    {
                        _fileLogger.Write(
                            LogLevel.Info,
                            s ?? "",
                            new LogContext(0, 0, 0, 0, 0d));
                    }
                    catch { /* 日志失败不阻断启动 */ }
                    prev?.Invoke(s);
                };
                _log("file-logger " + dir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Launcher] FileLogger init failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 组合根注入玩法宿主：仅 Unity 主线程 SynchronizationContext，不另造 NetClient。
        /// </summary>
        void EnsureGameplayHost()
        {
            SynchronizationContext sync = SynchronizationContext.Current;
            if (sync == null)
                throw new InvalidOperationException("launcher-requires-synchronization-context");
            var dispatcher = new MainThreadDispatcher(sync);
            var context = new GameplaySessionContext();
            GameplayBridgeBinding.ConfigureHost(dispatcher, context);
            _log("gameplay-host-configured");
        }

        /// <summary>
        /// AccountBridge.Configure 之后：绑定实际 Store/Transport/Client/Adapter 与 Lua PlayerModel 端口。
        /// </summary>
        void AttachGameplayRuntime(XLua.LuaEnv env)
        {
            if (env == null)
                throw new ArgumentNullException(nameof(env));
            if (!GameplayBridgeBinding.TryGetHost(
                    out IMainThreadDispatcher dispatcher,
                    out GameplaySessionContext context,
                    out _))
            {
                throw new InvalidOperationException("gameplay-host-missing");
            }

            Directory.CreateDirectory(_pendingRoot);
            AccountBridge.AttachGameplayRuntime(dispatcher, context, env, _pendingRoot);
            _log("gameplay-runtime-attached pending=" + _pendingRoot);
        }

        void ReleaseOwningScope(bool disconnectStatics)
        {
            // 先卸 Runtime（解绑 port/观察者），再 Shutdown host，再 Dispose Session/Lua
            try { AccountBridge.DetachGameplayRuntime(); } catch { /* 取消路径尽力 */ }
            try { GameplayBridgeBinding.Shutdown(); } catch { /* 取消路径尽力 */ }
            try { AccountBridge.CancelAll(); } catch { /* 取消路径尽力 */ }
            try { AccountBridge.StopReconnect(); } catch { }

            if (_patchView != null)
            {
                try { UnityEngine.Object.Destroy(_patchView.gameObject); } catch { }
                _patchView = null;
            }

            if (_fileLogger != null)
            {
                try { _fileLogger.Dispose(); } catch { }
                _fileLogger = null;
            }

            try { AccountFlow.Shutdown(); }
            catch (Exception ex) { AppendDisposeError("AccountFlow.Shutdown", ex); }

            try
            {
                var mgr = UiBridge.Manager;
                if (mgr != null)
                {
                    mgr.Dispose();
                    UiBridge.ClearManager(mgr);
                }
            }
            catch (Exception ex)
            {
                AppendDisposeError("UiManager", ex);
            }

            try { AccountBridge.Dispose(); }
            catch (Exception ex) { AppendDisposeError("AccountBridge", ex); }

            var lua = _lua;
            _lua = null;
            if (lua != null)
            {
                try
                {
                    lua.Dispose();
                    if (!string.IsNullOrEmpty(lua.DisposeError))
                        AppendDisposeErrorMessage("LuaEnv", lua.DisposeError);
                }
                catch (Exception ex)
                {
                    AppendDisposeError("LuaEnv", ex);
                }
            }

            if (disconnectStatics)
            {
                try { UiBridge.BindSceneService(null); } catch { }
                // AccountFlow.Shutdown 已清空自身绑定。
            }
        }

        void AppendDisposeError(string where, Exception ex)
        {
            string msg = where + ": " + (ex.GetBaseException().Message ?? ex.GetType().Name);
            AppendDisposeErrorMessage(where, msg);
            Debug.LogError("[Launcher] " + msg);
        }

        void AppendDisposeErrorMessage(string where, string message)
        {
            string line = string.IsNullOrEmpty(where) ? (message ?? "") : (where + ": " + (message ?? ""));
            if (string.IsNullOrEmpty(DisposeError))
                DisposeError = line;
            else
                DisposeError = DisposeError + " | " + line;
        }

        /// <summary>
        /// 编辑器：ResMgr 用 EditorSimulate 加载 Prefab/场景（DefaultPackage Host 待 T58）；
        /// Player：与 PatchService 同为 HostHttp。
        /// </summary>
        static ResourcePlayMode ResolvePlayMode()
        {
#if UNITY_EDITOR
            return ResourcePlayMode.EditorSimulate;
#else
            return ResourcePlayMode.HostHttp;
#endif
        }

#if UNITY_EDITOR
        /// <summary>编辑器加载正式Staging目录，校验声明SHA、完整依赖及ABI。</summary>
        static async UniTask PrepareEditorLuaFromDiskAsync(LuaLoader lua, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!LuaModulePreloader.TryLoadFormalFromDisk(
                    out LuaIndex index, out System.Collections.Generic.Dictionary<string, byte[]> modules,
                    out byte[] configBytes, out _))
                throw new InvalidOperationException("editor-formal-catalog-invalid");
            await lua.PrepareAsync(index, modules, configBytes, ct);
        }

        static byte[] ReadEditorLuaBytes(string address)
        {
            string rel = address.Replace('\\', '/');
            string path = Path.Combine(Application.dataPath, "Lua", rel + ".txt");
            if (File.Exists(path))
                return File.ReadAllBytes(path);
            path = Path.Combine(Application.dataPath, "Lua", "Staging", Path.GetFileName(rel) + ".txt");
            if (File.Exists(path))
                return File.ReadAllBytes(path);
            return null;
        }
#endif

        /// <summary>EditorSimulate 初始化后仍需请求版本并加载活动清单，否则 LoadAsync 报 Active package manifest not found。</summary>
        static async UniTask EnsureDefaultPackageManifestAsync(CancellationToken ct)
        {
            if (!YooAssets.TryGetPackage(ResMgr.PackageName, out ResourcePackage package) || package == null)
                package = YooAssets.GetPackage(ResMgr.PackageName);
            if (package == null)
                throw new InvalidOperationException("DefaultPackage missing after ResMgr init");

            RequestPackageVersionOperation ver = package.RequestPackageVersionAsync(
                new RequestPackageVersionOptions(false, 30));
            await ver.ToUniTask(cancellationToken: ct);
            if (ver.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException("DefaultPackage RequestPackageVersion: " + ver.Error);

            LoadPackageManifestOperation man = package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(ver.PackageVersion, 60));
            await man.ToUniTask(cancellationToken: ct);
            if (man.Status != EOperationStatus.Succeeded)
                throw new InvalidOperationException("DefaultPackage LoadPackageManifest: " + man.Error);
        }

        /// <summary>
        /// 若 version HTTP 与清单 BaseUrl 都是 loopback，则把 BaseUrl 的 Host/Port 改成与 httpBase 一致。
        /// </summary>
        internal static VersionManifest AlignLoopbackPackageBaseUrl(VersionManifest manifest, string httpBase)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.BaseUrl) || string.IsNullOrWhiteSpace(httpBase))
                return manifest;
            try
            {
                var httpUri = new Uri(EnsureSlash(httpBase));
                var packageUri = new Uri(manifest.BaseUrl);
                if (!IsLoopback(httpUri) || !IsLoopback(packageUri))
                    return manifest;
                if (string.Equals(httpUri.Host, packageUri.Host, StringComparison.OrdinalIgnoreCase)
                    && httpUri.Port == packageUri.Port)
                    return manifest;

                var rebuilt = new UriBuilder(packageUri)
                {
                    Host = httpUri.Host,
                    Port = httpUri.Port
                };
                return new VersionManifest
                {
                    MinAppVersion = manifest.MinAppVersion,
                    ResVersion = manifest.ResVersion,
                    ConfigVersion = manifest.ConfigVersion,
                    ProtoVersion = manifest.ProtoVersion,
                    PackageVersion = manifest.PackageVersion,
                    BaseUrl = rebuilt.Uri.AbsoluteUri
                };
            }
            catch
            {
                return manifest;
            }
        }

        static bool IsLoopback(Uri uri) =>
            uri != null
            && (uri.IsLoopback
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase));

        static string EnsureSlash(string url) =>
            url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Launcher));
        }
    }
}
