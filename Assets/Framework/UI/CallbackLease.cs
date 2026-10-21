using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Game.Framework.Lua;
using XLua;

namespace Game.Framework.UI
{
    /// <summary>
    /// 主线程至多一次的 done(code,payload) 租约。关闭/取消也会结束等待，避免关窗后悬挂。
    /// 可绑定真实操作 CTS：Cancel/Dispose 取消连接/RPC，不只抑制 done。
    /// 共享 DelegateBridge 引用计数与 AsyncLease 同规则。
    /// Dispose 必须先完成/取消再标 disposed，否则 Cancel→Complete 会被 _disposed 短路。
    /// </summary>
    public sealed class CallbackLease : IDisposable
    {
        static readonly ConditionalWeakTable<object, RefBox> BridgeRefCounts =
            new ConditionalWeakTable<object, RefBox>();

        LuaCallback _callback;
        CancellationTokenSource _opCts;
        Action _cancelSideEffect;
        bool _completed;
        bool _disposed;

        /// <summary>仍登记未完成的租约数（测试用）。</summary>
        public static int LiveCount { get; private set; }

        /// <summary>是否已 Complete（含 Cancel）。</summary>
        public bool IsCompleted => _completed;

        public CallbackLease(LuaCallback callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            RetainBridge(_callback);
            LiveCount++;
        }

        /// <summary>绑定本租约拥有的操作 CTS；Cancel/Dispose 时取消。</summary>
        public void BindOperationCts(CancellationTokenSource cts)
        {
            if (_disposed || _completed)
            {
                try { cts?.Cancel(); } catch { }
                try { cts?.Dispose(); } catch { }
                return;
            }

            _opCts = cts;
        }

        /// <summary>
        /// Cancel/Dispose 且尚未 Complete 时额外副作用（如放弃本代次 pending Token）。
        /// Complete(成功) 之后不会触发。
        /// </summary>
        public void BindCancelSideEffect(Action sideEffect)
        {
            _cancelSideEffect = sideEffect;
        }

        /// <summary>主线程完成。重复调用忽略。</summary>
        public void Complete(int code, string payload)
        {
            if (_completed || _callback == null)
                return;

            var cb = _callback;
            _callback = null;
            _completed = true;
            LiveCount--;
            ReleaseOperationCts(cancel: false);
            try
            {
                cb(code, payload ?? "");
            }
            finally
            {
                ReleaseBridge(cb);
            }
        }

        /// <summary>取消：先取消绑定 CTS，再以 code=-1 结束等待（若尚未完成）。</summary>
        public void Cancel(string reason = "cancelled")
        {
            if (_completed || _disposed)
            {
                ReleaseOperationCts(cancel: true);
                return;
            }

            ReleaseOperationCts(cancel: true);
            Action side = _cancelSideEffect;
            _cancelSideEffect = null;
            try { side?.Invoke(); } catch { }
            Complete(-1, reason ?? "cancelled");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            try
            {
                if (!_completed && _callback != null)
                    Cancel("disposed");
                else
                    ReleaseOperationCts(cancel: true);
            }
            finally
            {
                _disposed = true;
            }
        }

        void ReleaseOperationCts(bool cancel)
        {
            var cts = Interlocked.Exchange(ref _opCts, null);
            if (cts == null)
                return;
            try
            {
                if (cancel)
                    cts.Cancel();
            }
            catch
            {
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
            }
        }

        static void RetainBridge(LuaCallback callback)
        {
            var target = callback.Target;
            if (target == null || !(target is LuaBase))
                return;
            var box = BridgeRefCounts.GetValue(target, _ => new RefBox());
            Interlocked.Increment(ref box.Count);
        }

        static void ReleaseBridge(LuaCallback callback)
        {
            var target = callback?.Target;
            if (target == null || !(target is LuaBase))
                return;
            if (!BridgeRefCounts.TryGetValue(target, out var box))
                return;
            if (Interlocked.Decrement(ref box.Count) > 0)
                return;
            try
            {
                ((LuaBase)target).Dispose();
            }
            catch
            {
            }
        }

        /// <summary>供 AccountBridge 等对长期事件回调做相同引用计数。</summary>
        public static void RetainLuaCallback(LuaCallback callback) => RetainBridge(callback);

        /// <summary>释放长期事件回调；计数归零时 Dispose DelegateBridge。异常上抛，不吞。</summary>
        public static void ReleaseLuaCallback(LuaCallback callback, bool swallowDisposeErrors = false)
        {
            var target = callback?.Target;
            if (target == null || !(target is LuaBase))
                return;
            if (!BridgeRefCounts.TryGetValue(target, out var box))
                return;
            if (Interlocked.Decrement(ref box.Count) > 0)
                return;
            if (swallowDisposeErrors)
            {
                try { ((LuaBase)target).Dispose(); } catch { }
                return;
            }

            ((LuaBase)target).Dispose();
        }

        sealed class RefBox
        {
            public int Count;
        }
    }
}
