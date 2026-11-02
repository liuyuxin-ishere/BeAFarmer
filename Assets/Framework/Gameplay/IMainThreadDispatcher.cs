using System;

namespace Game.Framework.Gameplay
{
    /// <summary>显式主线程分发；禁止无上下文工作线程回落执行 Lua。</summary>
    public interface IMainThreadDispatcher
    {
        /// <summary>当前托管线程是否为构造时捕获的主线程。</summary>
        bool IsMainThread { get; }

        /// <summary>投递到主线程。若已在主线程则同步执行。</summary>
        void Post(Action action);
    }
}
