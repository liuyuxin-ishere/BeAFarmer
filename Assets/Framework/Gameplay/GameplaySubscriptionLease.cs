using System;
using Game.Framework.Lua;
using Game.Framework.UI;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 独立增量订阅租约。Dispose 只停本观察者。
    /// 引用计数复用 <see cref="CallbackLease.RetainLuaCallback"/>。
    /// </summary>
    public sealed class GameplaySubscriptionLease : IDisposable
    {
        readonly object _gate = new object();
        readonly long _id;
        readonly SessionStamp _stamp;
        readonly long _epoch;
        readonly Action _unregister;
        LuaCallback _handler;
        bool _retained;
        bool _disposed;
        bool _invalidated;

        internal GameplaySubscriptionLease(
            long id,
            SessionStamp stamp,
            long epoch,
            LuaCallback handler,
            Action unregister)
        {
            _id = id;
            _stamp = stamp;
            _epoch = epoch;
            _unregister = unregister;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            CallbackLease.RetainLuaCallback(_handler);
            _retained = true;
        }

        public long Id => _id;
        public SessionStamp Stamp => _stamp;
        public long Epoch => _epoch;

        public bool IsDisposed
        {
            get { lock (_gate) return _disposed; }
        }

        internal bool TryNotify(long currentEpoch, int code, string json)
        {
            LuaCallback h;
            lock (_gate)
            {
                if (_disposed || _invalidated)
                    return false;
                if (currentEpoch != _epoch)
                    return false;
                h = _handler;
            }

            if (h == null)
                return false;
            h(code, json ?? "{}");
            return true;
        }

        internal void InvalidateSilent()
        {
            LuaCallback h;
            bool release;
            lock (_gate)
            {
                if (_invalidated || _disposed)
                    return;
                _invalidated = true;
                h = _handler;
                _handler = null;
                release = _retained;
                _retained = false;
            }

            if (release && h != null)
                CallbackLease.ReleaseLuaCallback(h, swallowDisposeErrors: true);
        }

        public void Dispose()
        {
            LuaCallback h;
            bool release;
            Action unreg;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                h = _handler;
                _handler = null;
                release = _retained;
                _retained = false;
                unreg = _unregister;
            }

            if (release && h != null)
                CallbackLease.ReleaseLuaCallback(h, swallowDisposeErrors: true);
            try { unreg?.Invoke(); } catch { /* ignore */ }
        }
    }
}
