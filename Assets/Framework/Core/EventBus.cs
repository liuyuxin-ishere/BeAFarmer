using System;
using System.Collections.Generic;
using System.Threading;

namespace Game.Framework.Core
{
    /// <summary>
    /// 按事件类型分发通知。Subscribe 与 Publish 都在构造时所在的线程上调用。
    /// </summary>
    public sealed class EventBus
    {
        readonly int _threadId;
        readonly Dictionary<Type, object> _channels = new Dictionary<Type, object>();

        /// <summary>记下创建时所在的线程。游戏启动时要在主线程构造。</summary>
        public EventBus()
        {
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 注册处理函数。返回的对象 Dispose 后，该函数不再收到新的 Publish。
        /// </summary>
        /// <typeparam name="TEvent">事件类型。结构体和类都可以。</typeparam>
        /// <param name="handler">收到事件时同步调用的函数。空引用会抛异常。</param>
        /// <returns>只退订这一次注册的句柄。重复 Dispose 不抛异常。</returns>
        public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            EnsureOwnerThread();
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Channel<TEvent> channel = GetOrCreate<TEvent>();
            channel.Handlers.Add(handler);
            return new Subscription<TEvent>(this, handler);
        }

        /// <summary>
        /// 按订阅顺序调用当前已注册的 TEvent 处理函数。
        /// </summary>
        /// <typeparam name="TEvent">要发布的事件类型。</typeparam>
        /// <param name="eventData">传给每个处理函数的数据。</param>
        public void Publish<TEvent>(TEvent eventData)
        {
            EnsureOwnerThread();
            Channel<TEvent> channel;
            if (!TryGet(out channel))
                return;

            Action<TEvent>[] snapshot = channel.Handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i](eventData);
        }

        void Remove<TEvent>(Action<TEvent> handler)
        {
            EnsureOwnerThread();
            Channel<TEvent> channel;
            if (!TryGet(out channel))
                return;
            channel.Handlers.Remove(handler);
        }

        void EnsureOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
                throw new InvalidOperationException("EventBus.Subscribe 与 Publish 要在创建它所在的线程上调用");
        }

        Channel<TEvent> GetOrCreate<TEvent>()
        {
            object existing;
            if (_channels.TryGetValue(typeof(TEvent), out existing))
                return (Channel<TEvent>)existing;

            Channel<TEvent> created = new Channel<TEvent>();
            _channels.Add(typeof(TEvent), created);
            return created;
        }

        bool TryGet<TEvent>(out Channel<TEvent> channel)
        {
            object existing;
            if (_channels.TryGetValue(typeof(TEvent), out existing))
            {
                channel = (Channel<TEvent>)existing;
                return true;
            }

            channel = null;
            return false;
        }

        /// <summary>保存某一种事件的处理函数，避免把事件值装进 object。</summary>
        sealed class Channel<TEvent>
        {
            public readonly List<Action<TEvent>> Handlers = new List<Action<TEvent>>();
        }

        /// <summary>只移除构造时登记的那一次订阅。</summary>
        sealed class Subscription<TEvent> : IDisposable
        {
            EventBus _bus;
            Action<TEvent> _handler;

            public Subscription(EventBus bus, Action<TEvent> handler)
            {
                _bus = bus;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_handler == null)
                    return;

                Action<TEvent> handler = _handler;
                EventBus bus = _bus;
                _handler = null;
                _bus = null;
                bus.Remove(handler);
            }
        }
    }
}
