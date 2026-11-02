using System;
using System.Threading;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 仅在已有 <see cref="SynchronizationContext"/> 的线程上构造（Unity 主线程组合根）。
    /// 不把首次任意工作线程认作主线程。
    /// </summary>
    public sealed class MainThreadDispatcher : IMainThreadDispatcher
    {
        readonly int _threadId;
        readonly SynchronizationContext _ctx;

        /// <param name="context">必须非 null，且调用线程的 Current 与之相同。</param>
        public MainThreadDispatcher(SynchronizationContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (!ReferenceEquals(SynchronizationContext.Current, context))
                throw new InvalidOperationException("MainThreadDispatcher must be constructed on the context thread");
            _ctx = context;
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <inheritdoc />
        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _threadId;

        /// <inheritdoc />
        public void Post(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            if (IsMainThread)
            {
                action();
                return;
            }

            _ctx.Post(_ => action(), null);
        }
    }
}
