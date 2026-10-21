using System;
using System.Runtime.CompilerServices;
using System.Threading;
using XLua;

namespace Game.Framework.Lua
{
    /// <summary>
    /// 宿主在创建 LuaEnv 期间登记本作用域租约的接收方。
    /// 用于局部变量 new AsyncLease 后尚未写入 global 即抛错时，仍能被环境关闭清理。
    /// </summary>
    public interface IAsyncLeaseHost
    {
        /// <summary>登记本作用域新建的租约。lease 非 null。</summary>
        void RegisterOwnedLease(AsyncLease lease);
    }

    /// <summary>
    /// 一次可取消的异步租约。callback 为 Lua 侧生成委托，完成或释放后不得再调用。
    /// 同一实例就是生产者；关闭 LuaEnv 后仍只能推进这个实例。
    /// </summary>
    public sealed class AsyncLease : IDisposable
    {
        /// <summary>
        /// 共享 DelegateBridge 引用计数。同一 Lua 函数可能生成多个委托却共享 Target；
        /// 任一租约退订时不得 Dispose 别人仍用的桥；计数归零后再 Dispose，以便 LuaEnv.Dispose。
        /// </summary>
        static readonly ConditionalWeakTable<object, RefBox> BridgeRefCounts = new ConditionalWeakTable<object, RefBox>();

        /// <summary>当前宿主作用域；构造时若非 null 则自动登记，供失败路径清理局部租约。</summary>
        static IAsyncLeaseHost s_hostScope;

        LuaCallback _callback;
        bool _completed;
        bool _disposed;

        /// <summary>
        /// 进入宿主作用域。host 为当前 HotfixLoader（或测试替身）；创建租约前必须调用。
        /// </summary>
        public static void BeginHostScope(IAsyncLeaseHost host)
        {
            s_hostScope = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// 结束宿主作用域。仅当 host 仍是当前作用域时清空，避免错清其他宿主。
        /// </summary>
        public static void EndHostScope(IAsyncLeaseHost host)
        {
            if (ReferenceEquals(s_hostScope, host))
                s_hostScope = null;
        }

        /// <summary>仍被 C# 持有的订阅数。退订或触发后减少。</summary>
        public static int LiveSubscriptions { get; private set; }

        /// <summary>本租约身份。Dispose 前后对比它，证明推进的是同一个生产者。</summary>
        public string ProducerId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Subscribe 成功后保持为 true，退订后也不清掉。</summary>
        public bool WasRegistered { get; private set; }

        /// <summary>订阅仍有效时经历的宿主帧数。为 0 表示还在登记的同一帧。</summary>
        public int FramesWhileSubscribed { get; private set; }

        /// <summary>真正调用 Lua 回调时的托管线程。未调用过则为 0。</summary>
        public int CallbackThreadId { get; private set; }

        /// <summary>
        /// 构造即向当前宿主作用域登记。无作用域时不登记（EditMode 纯 C# 单测等）。
        /// </summary>
        public AsyncLease()
        {
            s_hostScope?.RegisterOwnedLease(this);
        }

        /// <summary>
        /// 登记回调。callback 由 CSharpCallLua 从 Lua 函数生成；其 Target 可能是共享 DelegateBridge。
        /// 本方法不调用 callback，因此不能把它当成异步完成。
        /// </summary>
        public void Subscribe(LuaCallback callback)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncLease));
            if (_callback != null)
                throw new InvalidOperationException("callback already registered");

            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            RetainBridge(_callback);
            WasRegistered = true;
            LiveSubscriptions++;
        }

        /// <summary>记录订阅仍然有效的一帧。由宿主在 yield 之后调用。</summary>
        public void NoteMainThreadFrame()
        {
            if (_disposed || _completed || _callback == null)
                return;

            FramesWhileSubscribed++;
        }

        /// <summary>退订：清本租约引用，并按共享桥引用计数决定是否 Dispose Target。</summary>
        public void Unsubscribe()
        {
            if (_callback == null)
                return;

            var callback = _callback;
            _callback = null;
            LiveSubscriptions--;
            ReleaseBridge(callback);
        }

        /// <summary>
        /// 推进这个租约。未跨过至少一帧、已取消、已完成或已释放时直接返回。
        /// 满足条件时只调用一次 callback；抛错也在 finally 释放本租约对桥的引用。
        /// </summary>
        public void Pump()
        {
            if (_disposed || _completed || _callback == null || FramesWhileSubscribed < 1)
                return;

            var callback = _callback;
            _callback = null;
            LiveSubscriptions--;
            _completed = true;
            CallbackThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                callback(1, "done");
            }
            finally
            {
                ReleaseBridge(callback);
            }
        }

        /// <summary>释放租约并退订，避免环境关闭后仍持有 Lua 回调引用。</summary>
        public void Dispose()
        {
            Unsubscribe();
            _disposed = true;
        }

        /// <summary>
        /// 为本租约增加共享桥引用计数。callback 的 Target 若不是 LuaBase（未知类型）则跳过，不假装拥有。
        /// </summary>
        static void RetainBridge(LuaCallback callback)
        {
            var target = callback.Target;
            if (target == null || !(target is LuaBase))
                return;

            var box = BridgeRefCounts.GetValue(target, _ => new RefBox());
            Interlocked.Increment(ref box.Count);
        }

        /// <summary>
        /// 释放本租约对共享桥的引用。仅当本表曾 Retain 且计数归零时 Dispose Target。
        /// 未登记的 Target 不得 Dispose：可能属于外部拥有者；偷偷释放会破坏共享桥。
        /// </summary>
        static void ReleaseBridge(LuaCallback callback)
        {
            var target = callback?.Target;
            if (target == null)
                return;

            if (!(target is LuaBase))
                return;

            if (!BridgeRefCounts.TryGetValue(target, out var box))
            {
                // 未登记：不释放，避免假称“安全 Dispose”破坏未知拥有者。
                DebugWarnUntrackedRelease();
                return;
            }

            if (Interlocked.Decrement(ref box.Count) > 0)
                return;

            try
            {
                ((LuaBase)target).Dispose();
            }
            catch
            {
                // ignore
            }
        }

        static void DebugWarnUntrackedRelease()
        {
            // 仅调试可见；正式路径不应走到（Subscribe 总会 Retain）。
            UnityEngine.Debug.LogWarning("[AsyncLease] ReleaseBridge skipped untracked Target — not disposing unknown owner");
        }

        sealed class RefBox
        {
            public int Count;
        }
    }
}
