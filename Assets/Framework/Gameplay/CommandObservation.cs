using System;
using Game.Framework.Lua;
using Game.Framework.UI;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 单次命令完成观察。Dispose 只取消观察，不取消已持久命令（T013）。
    /// 引用计数复用 <see cref="CallbackLease.RetainLuaCallback"/> / <see cref="CallbackLease.ReleaseLuaCallback"/>。
    /// </summary>
    public sealed class CommandObservation : IDisposable
    {
        readonly object _gate = new object();
        readonly Action _unregister;
        long _id;
        SessionStamp _stamp;
        long _epoch;
        LuaCallback _callback;
        bool _retained;
        bool _delivered;
        bool _disposed;
        bool _invalidated;
        bool _rejected;
        int _code = int.MinValue;
        string _reason;

        CommandObservation(Action unregister)
        {
            _unregister = unregister;
        }

        /// <summary>正常登记：立即 Retain（须已在可安全持有 Lua 的线程，通常为主线程）。</summary>
        internal static CommandObservation CreateArmed(
            long id,
            SessionStamp stamp,
            long epoch,
            LuaCallback callback,
            Action unregister)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            var obs = new CommandObservation(unregister)
            {
                _id = id,
                _stamp = stamp,
                _epoch = epoch,
                _callback = callback,
            };
            CallbackLease.RetainLuaCallback(callback);
            obs._retained = true;
            return obs;
        }

        /// <summary>无 dispatcher：明确拒绝；不 Retain/调用/释放 Lua。</summary>
        internal static CommandObservation CreateRejected(int code, string reason)
        {
            return new CommandObservation(null)
            {
                _id = -1,
                _rejected = true,
                _code = code,
                _reason = reason ?? "rejected",
            };
        }

        /// <summary>工作线程 Deferred：暂存回调但不 Retain；主线程 <see cref="ArmDeferredOnMain"/>。</summary>
        internal static CommandObservation CreateDeferred(LuaCallback callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            return new CommandObservation(null)
            {
                _id = -1,
                _callback = callback,
                _retained = false,
            };
        }

        /// <summary>主线程：写入身份、Retain、挂上本 Registry 注销闭包。</summary>
        internal bool ArmDeferredOnMain(long id, SessionStamp stamp, long epoch, Action unregister)
        {
            lock (_gate)
            {
                if (_disposed || _invalidated || _rejected || _delivered || _retained)
                    return false;
                if (_callback == null)
                    return false;
                _id = id;
                _stamp = stamp;
                _epoch = epoch;
                // unregister 只能在构造时设；Deferred 用字段补挂
                _unregisterDeferred = unregister;
                CallbackLease.RetainLuaCallback(_callback);
                _retained = true;
                return true;
            }
        }

        Action _unregisterDeferred;

        public long Id
        {
            get { lock (_gate) return _id; }
        }

        public SessionStamp Stamp
        {
            get { lock (_gate) return _stamp; }
        }

        public long Epoch
        {
            get { lock (_gate) return _epoch; }
        }

        public int Code
        {
            get { lock (_gate) return _code; }
        }

        public string Reason
        {
            get { lock (_gate) return _reason; }
        }

        public bool IsRejected
        {
            get { lock (_gate) return _rejected; }
        }

        public bool IsDelivered
        {
            get { lock (_gate) return _delivered; }
        }

        public bool IsDisposed
        {
            get { lock (_gate) return _disposed; }
        }

        internal bool TryDeliver(long currentEpoch, int code, string payload)
        {
            LuaCallback cb;
            bool release;
            lock (_gate)
            {
                if (_disposed || _invalidated || _delivered || _rejected)
                    return false;
                if (currentEpoch != _epoch)
                    return false;
                cb = _callback;
                _callback = null;
                _delivered = true;
                _code = code;
                _reason = payload ?? "";
                release = _retained;
                _retained = false;
            }

            if (cb == null)
                return false;
            try
            {
                cb(code, payload ?? "");
            }
            finally
            {
                if (release)
                    CallbackLease.ReleaseLuaCallback(cb, swallowDisposeErrors: true);
            }

            return true;
        }

        internal void InvalidateSilent()
        {
            LuaCallback cb;
            bool release;
            lock (_gate)
            {
                if (_delivered || _invalidated || _rejected)
                    return;
                _invalidated = true;
                cb = _callback;
                _callback = null;
                release = _retained;
                _retained = false;
            }

            if (release && cb != null)
                CallbackLease.ReleaseLuaCallback(cb, swallowDisposeErrors: true);
        }

        public void Dispose()
        {
            LuaCallback cb;
            bool release;
            Action unreg;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                cb = _callback;
                _callback = null;
                release = _retained;
                _retained = false;
                unreg = _unregister ?? _unregisterDeferred;
            }

            if (release && cb != null)
                CallbackLease.ReleaseLuaCallback(cb, swallowDisposeErrors: true);
            try { unreg?.Invoke(); } catch { /* ignore */ }
        }
    }
}
