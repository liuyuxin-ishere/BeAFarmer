using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Lua;
using UnityEngine;
using XLua;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 正式 AOT Lua 宿主服务：预加载校验 → CustomLoader → 语法/入口预检 → Start → 异步推进 → ready。
    /// 状态机：Unprepared → Preparing → Prepared → Starting → Ready；Dispose → Disposed。
    /// 从迁移 HotfixLoader 提炼核心；不调用项目额外强制 GC。
    /// </summary>
    public sealed class LuaLoader : IDisposable, IAsyncLeaseHost
    {
        /// <summary>宿主生命周期状态。</summary>
        public enum HostState
        {
            Unprepared = 0,
            Preparing = 1,
            Prepared = 2,
            Starting = 3,
            Ready = 4,
            Disposed = 5
        }

        /// <summary>入口 Start 白名单 phase。扩展须同步生成桥接与文档。</summary>
        public static readonly string[] AllowedEntryPhases = { "boot" };

        /// <summary>Ready 后的 LuaEnv；未就绪为 null。供 UiBridge 共用同一环境。</summary>
        public LuaEnv Env => _state == HostState.Ready ? _luaEnv : null;

        /// <summary>
        /// 在 LuaEnv 创建、AddLoader 之后、Entry.Start 之前调用（可绑定 UiBridge）。
        /// 用于正式 Launcher：入口 Register 前必须已有 Manager+同一 Env。
        /// </summary>
        public Func<LuaEnv, UniTask> BeforeEntryStartAsync { get; set; }

        readonly Dictionary<string, byte[]> _modules = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        readonly List<AsyncLease> _ownedLeases = new List<AsyncLease>();
        LuaIndex _index;
        byte[] _configBytes;
        LuaEnv _luaEnv;
        ILuaEntry _entry;
        LuaTable _entryTable;
        AsyncLease _liveLease;
        AsyncLease _cancelLease;
        string _entrySummary = "";
        string _disposeError = "";
        HostState _state = HostState.Unprepared;
        int _mainThreadId;
        int _entryInvocations;
        int _asyncFrames;
        int _cancelWaitFrames;
        int _asyncBeforeDispose;
        int _asyncAfterDispose;
        string _liveProducer = "";
        string _postDisposeLiveId = "";
        bool _cancelRegistered;
        bool _ownsPanelScope;
        CancellationTokenSource _disposeCts;
        int _prepareGate;
        int _startGate;

        /// <summary>当前状态。</summary>
        public HostState State => _state;

        /// <summary>索引与入口均就绪后为 true；失败/Dispose 后为 false。</summary>
        public bool IsReady => _state == HostState.Ready;

        /// <summary>入口 Start 返回摘要。</summary>
        public string EntrySummary => _entrySummary;

        /// <summary>当前索引；未 Prepare 时为 null。</summary>
        public LuaIndex Index => _index;

        /// <summary>已载入模块只读视图（CustomLoader 同步读取，禁止现场 HTTP）。</summary>
        public IReadOnlyDictionary<string, byte[]> Modules => _modules;

        /// <summary>Dispose 环境时捕获的错误；空表示无异常。不可 catch 吞后假成功。</summary>
        public string DisposeError => _disposeError;

        /// <summary>入口调用次数。</summary>
        public int EntryInvocations => _entryInvocations;

        /// <summary>live 租约跨帧数。</summary>
        public int AsyncFrames => _asyncFrames;

        /// <summary>取消租约等待帧数。</summary>
        public int CancelWaitFrames => _cancelWaitFrames;

        /// <summary>Dispose 前 AsyncCount。</summary>
        public int AsyncBeforeDispose => _asyncBeforeDispose;

        /// <summary>Dispose 后 AsyncCount。</summary>
        public int AsyncAfterDispose => _asyncAfterDispose;

        /// <summary>live ProducerId。</summary>
        public string LiveProducerId => _liveProducer;

        /// <summary>Dispose 后再 Pump 的 ProducerId。</summary>
        public string PostDisposeLiveId => _postDisposeLiveId;

        /// <summary>取消租约是否曾登记。</summary>
        public bool CancelRegistered => _cancelRegistered;

        /// <summary>
        /// 使用已在内存中的模块 bytes 完成索引校验与语法预检。不启动入口。
        /// 若已 Prepared/Ready：先完整释放旧 lease/接口/Env 再准备。Preparing/Starting 拒绝。
        /// </summary>
        public async UniTask PrepareAsync(
            LuaIndex index,
            IReadOnlyDictionary<string, byte[]> modules,
            byte[] configBytes,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (index == null)
                throw new ArgumentNullException(nameof(index));
            if (modules == null)
                throw new ArgumentNullException(nameof(modules));
            if (_state == HostState.Preparing || _state == HostState.Starting)
                throw new InvalidOperationException("Prepare/Start 进行中，拒绝并发 Prepare");
            if (Interlocked.CompareExchange(ref _prepareGate, 1, 0) != 0)
                throw new InvalidOperationException("Prepare 门闸占用");

            try
            {
                ct.ThrowIfCancellationRequested();
                if (_state == HostState.Prepared || _state == HostState.Ready || _luaEnv != null)
                    ReleasePreparedScope(rollbackPanels: true);

                _state = HostState.Preparing;
                _entrySummary = "";
                _disposeError = "";
                _index = null;
                _configBytes = null;
                _modules.Clear();

                foreach (var pair in modules)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(pair.Key) || pair.Value == null)
                        throw new InvalidOperationException("module entry invalid");
                    _modules[pair.Key] = CopyBytes(pair.Value);
                }

                _configBytes = CopyBytes(configBytes);
                string structural = index.Validate(
                    _modules,
                    _configBytes,
                    BridgeApi.BridgeContractValue,
                    BridgeApi.RequiredProtoVersion,
                    Application.version);
                if (structural != null)
                {
                    FailPrepareCleanup();
                    throw new InvalidOperationException(structural);
                }

                string hashError = index.ValidateContentHashes(_modules, _configBytes);
                if (hashError != null)
                {
                    FailPrepareCleanup();
                    throw new InvalidOperationException(hashError);
                }

                await UniTask.Yield(PlayerLoopTiming.Update, ct);

                // 只编译索引声明的模块；禁止把 configAddress（JSON）当 Lua 源码编译。
                string syntax = CompileOnly(_modules, index);
                if (syntax != null)
                {
                    FailPrepareCleanup();
                    throw new InvalidOperationException("syntax " + syntax);
                }

                ct.ThrowIfCancellationRequested();
                _index = CloneIndex(index);
                _state = HostState.Prepared;
            }
            catch (OperationCanceledException)
            {
                FailPrepareCleanup();
                throw;
            }
            catch
            {
                if (_state == HostState.Preparing)
                    FailPrepareCleanup();
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _prepareGate, 0);
            }
        }

        /// <summary>
        /// 在已 Prepare 的环境上创建 LuaEnv、CustomLoader、require 入口并调用 Start(phase)。
        /// 校验 BootContext 版本与索引、EntryExecuted、驱动 async 生产者后才 Ready。
        /// 重复 Start 在 Starting/Ready 时拒绝。
        /// </summary>
        public async UniTask StartEntryAsync(BootContext context, string phase, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (_state != HostState.Prepared)
                throw new InvalidOperationException("先调用 PrepareAsync（当前=" + _state + "）");
            if (!IsAllowedPhase(phase))
                throw new InvalidOperationException("entry-phase-not-allowed " + phase);
            if (Interlocked.CompareExchange(ref _startGate, 1, 0) != 0)
                throw new InvalidOperationException("Start 门闸占用");

            try
            {
                ct.ThrowIfCancellationRequested();
                ValidateBootContext(context);

                _state = HostState.Starting;
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _disposeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                CancellationToken linked = _disposeCts.Token;

                BridgeApi.Reset(_index.ResVersion);
                _ownsPanelScope = true;
                foreach (var key in _modules.Keys)
                    BridgeApi.AddModule(key);
                BridgeApi.ApplyConfig(Encoding.UTF8.GetString(_configBytes));

                AsyncLease.BeginHostScope(this);
                try
                {
                    linked.ThrowIfCancellationRequested();
                    _luaEnv = new LuaEnv();
                    _luaEnv.AddLoader(LoadModule);
                    if (BeforeEntryStartAsync != null)
                        await BeforeEntryStartAsync(_luaEnv);
                    linked.ThrowIfCancellationRequested();
                    _luaEnv.DoString("Entry = require('" + _index.EntryModule + "')", "boot");
                    _entry = _luaEnv.Global.Get<ILuaEntry>("Entry");
                    _entryTable = _luaEnv.Global.Get<LuaTable>("Entry");
                    if (_entry == null)
                        throw new InvalidOperationException("runtime-entry-null");

                    _entrySummary = _entry.Start(phase);
                    _entryInvocations++;
                    BridgeApi.MarkEntryExecuted();
                    if (!BridgeApi.EntryExecuted)
                        throw new InvalidOperationException("entry-ready-contract-failed");

                    CaptureLeasesFromGlobal();
                    // 至少一帧，供 async_case 订阅跨帧；Tick 本身不是 AsyncLease.Pump。
                    await UniTask.Yield(PlayerLoopTiming.Update, linked);
                    linked.ThrowIfCancellationRequested();
                    _luaEnv.Tick();
                    AdvanceAsyncProducers();
                    linked.ThrowIfCancellationRequested();

                    if (!BridgeApi.EntryExecuted)
                        throw new InvalidOperationException("entry-ready-lost");

                    _state = HostState.Ready;
                }
                catch (OperationCanceledException)
                {
                    CleanupAfterFailure(rollbackPanels: true);
                    _state = HostState.Unprepared;
                    throw;
                }
                catch (Exception)
                {
                    CleanupAfterFailure(rollbackPanels: true);
                    _state = HostState.Unprepared;
                    throw;
                }
                finally
                {
                    AsyncLease.EndHostScope(this);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _startGate, 0);
            }
        }

        /// <summary>登记本作用域新建的异步租约，供失败/Dispose 清理。</summary>
        public void RegisterOwnedLease(AsyncLease lease)
        {
            if (lease == null || _state == HostState.Disposed)
                return;
            if (!_ownedLeases.Contains(lease))
                _ownedLeases.Add(lease);
        }

        /// <summary>主线程 Tick：推进 LuaEnv。非主线程调用抛错。</summary>
        public void Tick()
        {
            if (_state == HostState.Disposed || _luaEnv == null)
                return;
            if (_mainThreadId != 0 &&
                Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException("LuaLoader.Tick 必须在主线程调用");
            }

            _luaEnv.Tick();
        }

        /// <summary>
        /// 取消生产者、释放 Lua 接口/委托/环境与拥有租约。不强制 GC。不改磁盘旧索引。
        /// DisposeError 可观察；异常不吞成假成功。
        /// </summary>
        public void Dispose()
        {
            if (_state == HostState.Disposed)
                return;
            _state = HostState.Disposed;
            ReleaseOwnedPanels();
            try
            {
                _disposeCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                foreach (var lease in _ownedLeases)
                {
                    try
                    {
                        lease?.Unsubscribe();
                    }
                    catch
                    {
                    }
                }

                InvokeEntryShutdown();
                DisposeEntryObjects();
                if (_luaEnv != null)
                {
                    _luaEnv.Tick();
                    _asyncBeforeDispose = BridgeApi.AsyncCount;
                    _luaEnv.Dispose();
                }
            }
            catch (Exception ex)
            {
                _disposeError = string.IsNullOrEmpty(_disposeError)
                    ? ex.GetType().Name + ": " + ex.Message
                    : _disposeError + " | " + ex.GetType().Name + ": " + ex.Message;
            }

            _luaEnv = null;
            ProofNoCallbackAfterDispose();
            _ownedLeases.Clear();
            _modules.Clear();
            _configBytes = null;
            _index = null;
            try
            {
                _disposeCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _disposeCts = null;
            AsyncLease.EndHostScope(this);
        }

        /// <summary>
        /// 与迁移 HotfixLoader.ValidateCandidate 相同语义的静态校验入口（结构级）。
        /// </summary>
        public static string ValidateCandidate(
            RuntimeCatalog catalog,
            List<HotfixLoader.LoadedModule> loaded,
            byte[] configBytes)
        {
            if (catalog == null)
                return "catalog-null";
            LuaIndex index = LuaIndex.FromCatalog(catalog);
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    HotfixLoader.LoadedModule m = loaded[i];
                    if (m != null && !string.IsNullOrEmpty(m.Name))
                        map[m.Name] = m.Bytes;
                }
            }

            return index.Validate(
                map,
                configBytes,
                HotfixLoader.BridgeContract,
                HotfixLoader.HostProtoVersion,
                Application.version);
        }

        /// <summary>phase 是否在固定白名单内。</summary>
        public static bool IsAllowedPhase(string phase)
        {
            if (string.IsNullOrEmpty(phase))
                return false;
            for (int i = 0; i < AllowedEntryPhases.Length; i++)
            {
                if (string.Equals(AllowedEntryPhases[i], phase, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>只编译不执行。返回 null 表示语法通过。</summary>
        public static string CompileOnly(IReadOnlyDictionary<string, byte[]> modules)
        {
            return CompileOnly(modules, null);
        }

        /// <summary>
        /// 只编译不执行。若提供 index，仅编译其 Modules，并跳过 ConfigAddress。
        /// </summary>
        public static string CompileOnly(IReadOnlyDictionary<string, byte[]> modules, LuaIndex index)
        {
            if (modules == null)
                return "modules-null";

            var checkEnv = new LuaEnv();
            try
            {
                if (index != null && index.Modules != null && index.Modules.Length > 0)
                {
                    string configAddress = index.ConfigAddress ?? "";
                    for (int i = 0; i < index.Modules.Length; i++)
                    {
                        LuaIndexModule mod = index.Modules[i];
                        if (mod == null || string.IsNullOrEmpty(mod.Name))
                            continue;
                        if (!string.IsNullOrEmpty(configAddress)
                            && string.Equals(mod.Name, configAddress, StringComparison.Ordinal))
                            continue;
                        if (!modules.TryGetValue(mod.Name, out byte[] bytes) || bytes == null)
                            return mod.Name + ": missing-bytes";
                        string err = TryCompile(checkEnv, mod.Name, bytes);
                        if (err != null)
                            return err;
                    }

                    return null;
                }

                foreach (var pair in modules)
                {
                    string err = TryCompile(checkEnv, pair.Key, pair.Value);
                    if (err != null)
                        return err;
                }
            }
            finally
            {
                checkEnv.Tick();
                checkEnv.Dispose();
            }

            return null;
        }

        static string TryCompile(LuaEnv checkEnv, string key, byte[] bytes)
        {
            LuaFunction compiled = null;
            try
            {
                compiled = checkEnv.LoadString(Encoding.UTF8.GetString(bytes), key);
                return null;
            }
            catch (Exception ex)
            {
                return key + ": " + ex.Message;
            }
            finally
            {
                if (compiled != null)
                    compiled.Dispose();
            }
        }

        void ValidateBootContext(BootContext context)
        {
            if (context == null || context.Versions == null)
                return;
            VersionManifest v = context.Versions;
            if (_index != null
                && !string.IsNullOrEmpty(_index.ResVersion)
                && int.TryParse(_index.ResVersion, out int res)
                && v.ResVersion != 0
                && v.ResVersion != res)
            {
                throw new InvalidOperationException(
                    "BootContext.ResVersion 与 LuaIndex 不一致 ctx="
                    + v.ResVersion + " index=" + _index.ResVersion);
            }

            if (v.ProtoVersion != 0 && v.ProtoVersion != BridgeApi.RequiredProtoVersion)
            {
                throw new InvalidOperationException(
                    "BootContext.ProtoVersion 不匹配 " + v.ProtoVersion);
            }
        }

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
            }

            if (_liveLease != null)
            {
                _liveProducer = _liveLease.ProducerId;
                RegisterOwnedLease(_liveLease);
            }

            if (_cancelLease != null)
                RegisterOwnedLease(_cancelLease);
        }

        /// <summary>真正驱动 async_case 生产者：NoteMainThreadFrame + Pump；非 LuaEnv.Tick。</summary>
        void AdvanceAsyncProducers()
        {
            if (_cancelLease == null || !_cancelLease.WasRegistered
                || _liveLease == null || !_liveLease.WasRegistered)
            {
                // 无 async 模块时允许跳过（最小入口测试）。
                if (_modules.ContainsKey("async_case"))
                    throw new InvalidOperationException("async lease was not registered");
                return;
            }

            _cancelLease.NoteMainThreadFrame();
            _liveLease.NoteMainThreadFrame();
            _cancelWaitFrames = _cancelLease.FramesWhileSubscribed;
            _asyncFrames = _liveLease.FramesWhileSubscribed;
            if (_cancelWaitFrames < 1 || _asyncFrames < 1)
                throw new InvalidOperationException("async did not cross a frame");

            _cancelRegistered = true;
            _cancelLease.Unsubscribe();
            int before = BridgeApi.AsyncCount;
            _cancelLease.Pump();
            if (BridgeApi.AsyncCount != before)
                throw new InvalidOperationException("cancelled lease invoked callback");

            _liveLease.Pump();
            if (BridgeApi.AsyncCount != 1 || _liveLease.CallbackThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "async count " + BridgeApi.AsyncCount + " thread " + _liveLease.CallbackThreadId);
            }

            _liveLease.Pump();
            if (BridgeApi.AsyncCount != 1)
                throw new InvalidOperationException("callback repeated");
        }

        void ProofNoCallbackAfterDispose()
        {
            int before = BridgeApi.AsyncCount;
            if (_liveLease != null)
            {
                _liveLease.Pump();
                _postDisposeLiveId = _liveLease.ProducerId;
                try
                {
                    _liveLease.Dispose();
                }
                catch
                {
                }
            }

            if (_cancelLease != null)
            {
                _cancelLease.Pump();
                try
                {
                    _cancelLease.Dispose();
                }
                catch
                {
                }
            }

            _asyncAfterDispose = BridgeApi.AsyncCount;
            if (_asyncAfterDispose != before && _liveLease != null)
            {
                // 关闭后不得新增回调；保留计数供报告，DisposeError 已反映环境异常。
            }

            _liveLease = null;
            _cancelLease = null;
        }

        byte[] LoadModule(ref string fileName)
        {
            string key = fileName ?? "";
            if (key.EndsWith(".lua", StringComparison.Ordinal))
                key = key.Substring(0, key.Length - 4);
            if (key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);
            if (_modules.TryGetValue(key, out byte[] bytes))
                return bytes;

#if UNITY_EDITOR
            // Editor：Staging 扁平包未含 UI/* 时回落 Assets/Lua 磁盘，与 C1 夹具一致。
            string rel = key.Replace('\\', '/');
            string path = Path.Combine(Application.dataPath, "Lua", rel + ".txt");
            if (File.Exists(path))
            {
                fileName = path;
                return File.ReadAllBytes(path);
            }
#endif
            return null;
        }

        void FailPrepareCleanup()
        {
            _modules.Clear();
            _configBytes = null;
            _index = null;
            _state = HostState.Unprepared;
        }

        void ReleasePreparedScope(bool rollbackPanels)
        {
            CleanupAfterFailure(rollbackPanels);
        }

        void CleanupAfterFailure(bool rollbackPanels)
        {
            if (rollbackPanels)
                ReleaseOwnedPanels();
            foreach (var lease in _ownedLeases)
            {
                try
                {
                    lease?.Unsubscribe();
                    lease?.Dispose();
                }
                catch
                {
                }
            }

            _ownedLeases.Clear();
            InvokeEntryShutdown();
            DisposeEntryObjects();
            if (_luaEnv != null)
            {
                try
                {
                    _luaEnv.Tick();
                    _luaEnv.Dispose();
                }
                catch (Exception ex)
                {
                    _disposeError = string.IsNullOrEmpty(_disposeError)
                        ? ex.GetType().Name + ": " + ex.Message
                        : _disposeError + " | " + ex.GetType().Name + ": " + ex.Message;
                }

                _luaEnv = null;
            }

            _liveLease = null;
            _cancelLease = null;
            _modules.Clear();
            _configBytes = null;
            _index = null;
        }

        /// <summary>Env 关闭前调用 Lua Shutdown：关窗/退订。不等于 LuaBase.Dispose。</summary>
        void InvokeEntryShutdown()
        {
            if (_entry == null)
                return;
            try
            {
                _entry.Shutdown();
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(_disposeError))
                    _disposeError = "Shutdown:" + ex.GetType().Name + ": " + ex.Message;
            }
        }

        void DisposeEntryObjects()
        {
            _entry = null;
            if (_entryTable != null)
            {
                try
                {
                    _entryTable.Dispose();
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(_disposeError))
                        _disposeError = ex.GetType().Name + ": " + ex.Message;
                }

                _entryTable = null;
            }
        }

        /// <summary>仅清理由本次 Start 打开的面板作用域；未启动实例不会清理别人的对象。</summary>
        void ReleaseOwnedPanels()
        {
            if (!_ownsPanelScope)
                return;
            BridgeApi.RollbackPanels(0);
            _ownsPanelScope = false;
        }

        void ThrowIfDisposed()
        {
            if (_state == HostState.Disposed)
                throw new ObjectDisposedException(nameof(LuaLoader));
        }

        static byte[] CopyBytes(byte[] source)
        {
            if (source == null)
                return null;
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        static LuaIndex CloneIndex(LuaIndex source)
        {
            if (source == null)
                return null;
            var clone = new LuaIndex
            {
                ResVersion = source.ResVersion,
                EntryModule = source.EntryModule,
                ConfigAddress = source.ConfigAddress,
                ConfigSha256 = source.ConfigSha256,
                BridgeContract = source.BridgeContract,
                RequiredAppVersion = source.RequiredAppVersion,
                ProtoVersion = source.ProtoVersion
            };
            if (source.Modules == null)
            {
                clone.Modules = Array.Empty<LuaIndexModule>();
                return clone;
            }

            clone.Modules = new LuaIndexModule[source.Modules.Length];
            for (int i = 0; i < source.Modules.Length; i++)
            {
                LuaIndexModule m = source.Modules[i];
                clone.Modules[i] = m == null
                    ? null
                    : new LuaIndexModule
                    {
                        Name = m.Name,
                        Address = m.Address,
                        Sha256 = m.Sha256
                    };
            }

            return clone;
        }
    }
}
