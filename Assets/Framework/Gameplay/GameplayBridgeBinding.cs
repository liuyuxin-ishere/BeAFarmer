using System;
using Game.Framework.Lua;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 绑定控制面：internal，不对 Lua 导出。友元测试 / 同程序集组合根。
    /// </summary>
    internal static class GameplayBridgeBinding
    {
        public const int CodeNotReady = -10;
        public const int CodeUnknownCommand = -11;
        public const int CodeInvalidDto = -12;

        static readonly object Gate = new object();
        static IMainThreadDispatcher _dispatcher;
        static GameplaySessionContext _context;
        static IGameplayCommandPort _port;
        static IGameplayModelPort _modelPort;
        static GameplayCallbackRegistry _registry;
        static Action _deltaUnsub;
        static long _bindingId;
        static Func<string> _statusJson;
        static Func<(bool writable, string reason)> _writableProbe;
        static ClientGameplayClock _displayClock;
        static IMonotonicClock _monotonic;

        public static bool HasDispatcher
        {
            get { lock (Gate) return _dispatcher != null; }
        }

        public static bool HasPort
        {
            get { lock (Gate) return _port != null; }
        }

        internal static long CurrentBindingId
        {
            get { lock (Gate) return _bindingId; }
        }

        internal static GameplayCallbackRegistry Registry
        {
            get { lock (Gate) return _registry; }
        }

        internal static GameplaySessionContext Context
        {
            get { lock (Gate) return _context; }
        }

        internal static IMainThreadDispatcher Dispatcher
        {
            get { lock (Gate) return _dispatcher; }
        }

        /// <summary>主线程组合根。清除旧 port/modelPort。</summary>
        internal static void ConfigureHost(IMainThreadDispatcher dispatcher, GameplaySessionContext context)
        {
            if (dispatcher == null)
                throw new ArgumentNullException(nameof(dispatcher));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (!dispatcher.IsMainThread)
                throw new InvalidOperationException("ConfigureHost must run on main thread");

            lock (Gate)
            {
                TearDownDelta_NoLock();
                _registry?.InvalidateEpoch(context.Epoch);
                _port = null;
                _modelPort = null;
                _statusJson = null;
                _writableProbe = null;
                _displayClock = null;
                _monotonic = null;
                _dispatcher = dispatcher;
                _context = context;
                _registry = new GameplayCallbackRegistry();
                _bindingId++;
            }
        }

        /// <summary>Runtime 注册只读状态探针与显示时钟（不导出控制面）。</summary>
        internal static void AttachRuntimeSurfaces(
            Func<string> statusJson,
            Func<(bool writable, string reason)> writableProbe,
            ClientGameplayClock displayClock,
            IMonotonicClock monotonic)
        {
            lock (Gate)
            {
                _statusJson = statusJson;
                _writableProbe = writableProbe;
                _displayClock = displayClock;
                _monotonic = monotonic;
            }
        }

        internal static string BuildStatusJson()
        {
            Func<string> probe;
            GameplaySessionContext ctx;
            lock (Gate)
            {
                probe = _statusJson;
                ctx = _context;
            }

            if (probe != null)
            {
                try { return probe() ?? "{}"; }
                catch { /* fall through */ }
            }

            if (ctx == null)
                return "{\"authReady\":false,\"writeFrozen\":true,\"busy\":false,\"writable\":false,\"reason\":\"host-not-configured\"}";

            bool ready = ctx.IsReady;
            bool frozen = ctx.IsWriteLocked;
            string freeze = ctx.WriteFreezeReason ?? "";
            string reason = !ready ? "not-ready" : (frozen ? (string.IsNullOrEmpty(freeze) ? "write-locked" : freeze) : "ok");
            bool writable = ready && !frozen;
            return "{\"authReady\":" + (ready ? "true" : "false")
                   + ",\"writeFrozen\":" + (frozen ? "true" : "false")
                   + ",\"freezeReason\":\"" + (freeze ?? "") + "\""
                   + ",\"busy\":false"
                   + ",\"writable\":" + (writable ? "true" : "false")
                   + ",\"reason\":\"" + reason + "\"}";
        }

        internal static bool TryIsWritable(out string reason)
        {
            Func<(bool writable, string reason)> probe;
            GameplaySessionContext ctx;
            lock (Gate)
            {
                probe = _writableProbe;
                ctx = _context;
            }

            if (probe != null)
            {
                try
                {
                    (bool w, string r) = probe();
                    reason = r ?? "";
                    return w;
                }
                catch (Exception ex)
                {
                    reason = ex.GetType().Name;
                    return false;
                }
            }

            // 无 Runtime 探针（纯 BindPort 测试）：回退会话 Ready/冻写
            if (ctx == null)
            {
                reason = "host-not-configured";
                return false;
            }

            if (!ctx.IsReady)
            {
                reason = "not-ready";
                return false;
            }

            if (ctx.IsWriteLocked)
            {
                reason = string.IsNullOrEmpty(ctx.WriteFreezeReason) ? "write-locked" : ctx.WriteFreezeReason;
                return false;
            }

            reason = "ok";
            return true;
        }

        internal static string EstimateNowMs()
        {
            ClientGameplayClock clock;
            IMonotonicClock mono;
            lock (Gate)
            {
                clock = _displayClock;
                mono = _monotonic;
            }

            if (clock == null || mono == null)
                return "";
            return clock.EstimateNowMs(mono.ElapsedMilliseconds);
        }

        internal static long RemainingMs(string deadlineMs)
        {
            ClientGameplayClock clock;
            IMonotonicClock mono;
            lock (Gate)
            {
                clock = _displayClock;
                mono = _monotonic;
            }

            if (clock == null || mono == null)
                return 0;
            return clock.RemainingMs(deadlineMs, mono.ElapsedMilliseconds);
        }

        internal static bool IsClockSynchronized()
        {
            ClientGameplayClock clock;
            lock (Gate) clock = _displayClock;
            return clock != null && clock.IsSynchronized;
        }

        /// <summary>
        /// 绑定命令端口。生产路径由 <see cref="GameplayDeltaInbox"/> 独占传输 Batch 订阅；
        /// subscribeTransportDelta=false 时不二次订阅（避免覆盖唯一 handler）。
        /// </summary>
        internal static void BindPort(
            IGameplayCommandPort port,
            IGameplayModelPort modelPort,
            bool subscribeTransportDelta = true)
        {
            IMainThreadDispatcher d;
            GameplaySessionContext ctx;
            lock (Gate)
            {
                d = _dispatcher;
                ctx = _context;
                if (d == null || ctx == null)
                    throw new InvalidOperationException("ConfigureHost first");
            }

            if (!d.IsMainThread)
                throw new InvalidOperationException("BindPort must run on main thread");
            if (port == null)
                throw new ArgumentNullException(nameof(port));

            lock (Gate)
            {
                TearDownDelta_NoLock();
                _registry?.InvalidateEpoch(ctx.Epoch);
                var reg = new GameplayCallbackRegistry();
                _registry = reg;
                _port = null;
                _modelPort = null;

                long bindingId = _bindingId + 1;
                Action unsub = null;
                if (subscribeTransportDelta)
                {
                    IMainThreadDispatcher capturedDisp = d;
                    GameplayCallbackRegistry capturedReg = reg;
                    GameplaySessionContext capturedCtx = ctx;
                    SessionStamp fullStamp = ctx.Capture();
                    long capturedEpoch = fullStamp.Epoch;

                    try
                    {
                        unsub = port.SubscribeDelta(json =>
                        {
                            capturedDisp.Post(() =>
                            {
                                if (CurrentBindingId != bindingId)
                                    return;
                                if (!ReferenceEquals(Registry, capturedReg))
                                    return;
                                if (!capturedCtx.Matches(fullStamp))
                                    return;
                                capturedReg.BroadcastDelta(capturedEpoch, 0, json ?? "{}");
                            });
                        });
                    }
                    catch (Exception ex)
                    {
                        _port = null;
                        _modelPort = null;
                        _deltaUnsub = null;
                        _registry = new GameplayCallbackRegistry();
                        throw new InvalidOperationException("BindPort SubscribeDelta failed", ex);
                    }
                }

                _deltaUnsub = unsub;
                _port = port;
                _modelPort = modelPort ?? new NullGameplayModelPort();
                _bindingId = bindingId;
            }
        }

        internal static void UnbindPort()
        {
            IMainThreadDispatcher d;
            lock (Gate) d = _dispatcher;
            if (d != null && !d.IsMainThread)
                throw new InvalidOperationException("UnbindPort must run on main thread");

            lock (Gate)
            {
                TearDownDelta_NoLock();
                if (_context != null)
                    _registry?.InvalidateEpoch(_context.Epoch);
                _port = null;
                _modelPort = null;
                _registry = new GameplayCallbackRegistry();
                _bindingId++;
            }
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                TearDownDelta_NoLock();
                if (_context != null)
                    _registry?.InvalidateEpoch(_context.Epoch);
                _port = null;
                _modelPort = null;
                _statusJson = null;
                _writableProbe = null;
                _displayClock = null;
                _monotonic = null;
                _dispatcher = null;
                _context = null;
                _registry = null;
                _bindingId++;
            }
        }

        internal static bool TryGetRuntime(
            out IMainThreadDispatcher dispatcher,
            out GameplaySessionContext context,
            out IGameplayCommandPort port,
            out GameplayCallbackRegistry registry,
            out string notReadyReason)
        {
            lock (Gate)
            {
                dispatcher = _dispatcher;
                context = _context;
                port = _port;
                registry = _registry;
            }

            if (dispatcher == null || context == null || registry == null)
            {
                notReadyReason = "host-not-configured";
                return false;
            }

            if (!dispatcher.IsMainThread)
            {
                notReadyReason = "not-main-thread";
                return false;
            }

            if (port == null)
            {
                notReadyReason = "not-ready";
                return false;
            }

            notReadyReason = null;
            return true;
        }

        /// <summary>仅探测 host（含工作线程）：dispatcher/context/registry 是否存在。</summary>
        internal static bool TryGetHost(
            out IMainThreadDispatcher dispatcher,
            out GameplaySessionContext context,
            out GameplayCallbackRegistry registry)
        {
            lock (Gate)
            {
                dispatcher = _dispatcher;
                context = _context;
                registry = _registry;
            }

            return dispatcher != null && context != null && registry != null;
        }

        static void TearDownDelta_NoLock()
        {
            try { _deltaUnsub?.Invoke(); } catch { /* ignore */ }
            _deltaUnsub = null;
        }
    }
}
