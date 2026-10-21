using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine.UI;
using XLua;

namespace Game.Framework.UI
{
    /// <summary>
    /// 按钮点击订阅租约。Dispose 时退订并按共享桥引用计数释放 DelegateBridge。
    /// 窗口局部创建但尚未导出的租约须由拥有者（ViewHost/UiManager）清理。
    /// </summary>
    public sealed class ClickSubscription : IDisposable
    {
        static readonly ConditionalWeakTable<object, RefBox> BridgeRefCounts =
            new ConditionalWeakTable<object, RefBox>();

        readonly Button _button;
        LuaClickHandler _handler;
        UnityEngine.Events.UnityAction _unityAction;
        bool _disposed;

        /// <summary>仍有效的点击订阅数。</summary>
        public static int LiveCount { get; private set; }

        public ClickSubscription(Button button, LuaClickHandler handler)
        {
            _button = button ?? throw new ArgumentNullException(nameof(button));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            RetainBridge(_handler);
            _unityAction = () =>
            {
                if (_disposed || _handler == null)
                    return;
                _handler();
            };
            _button.onClick.AddListener(_unityAction);
            LiveCount++;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_button != null && _unityAction != null)
                _button.onClick.RemoveListener(_unityAction);
            var h = _handler;
            _handler = null;
            _unityAction = null;
            LiveCount--;
            ReleaseBridge(h);
        }

        static void RetainBridge(LuaClickHandler handler)
        {
            var target = handler.Target;
            if (target == null || !(target is LuaBase))
                return;
            var box = BridgeRefCounts.GetValue(target, _ => new RefBox());
            Interlocked.Increment(ref box.Count);
        }

        static void ReleaseBridge(LuaClickHandler handler)
        {
            var target = handler?.Target;
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

        sealed class RefBox
        {
            public int Count;
        }
    }
}
