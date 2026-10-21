using System;
using System.Collections.Generic;

namespace Game.Framework.Core
{
    /// <summary>
    /// 可复用的 class 对象池。归还时调用 reset，重复归还会抛异常。
    /// </summary>
    public sealed class ObjectPool<T> where T : class
    {
        readonly Func<T> _factory;
        readonly Action<T> _reset;
        readonly int _maxRetained;
        readonly Stack<T> _idle = new Stack<T>();
        readonly HashSet<T> _rented = new HashSet<T>();

        /// <summary>
        /// 创建对象池。
        /// </summary>
        /// <param name="factory">没有闲置对象时用来新建。空引用会抛异常。</param>
        /// <param name="reset">归还时调用，负责清掉脏字段。空引用会抛异常。</param>
        /// <param name="maxRetained">闲置对象的保留上限。小于 0 会抛异常。等于 0 时归还后不保留。</param>
        public ObjectPool(Func<T> factory, Action<T> reset, int maxRetained)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            if (reset == null)
                throw new ArgumentNullException(nameof(reset));
            if (maxRetained < 0)
                throw new ArgumentOutOfRangeException(nameof(maxRetained));

            _factory = factory;
            _reset = reset;
            _maxRetained = maxRetained;
        }

        /// <summary>取出一个对象。池里有闲置对象就复用，否则调用 factory。</summary>
        public T Rent()
        {
            T item = _idle.Count > 0 ? _idle.Pop() : Create();
            _rented.Add(item);
            return item;
        }

        /// <summary>
        /// 归还对象。先执行 reset，再按上限决定是否留下。
        /// </summary>
        /// <param name="item">必须是当前借出的对象。空引用或重复归还会抛异常。</param>
        public void Return(T item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (!_rented.Remove(item))
                throw new InvalidOperationException("对象未借出或已经归还");

            _reset(item);
            if (_idle.Count < _maxRetained)
                _idle.Push(item);
        }

        /// <summary>丢弃全部闲置对象。已经借出的对象保持外借。</summary>
        public void Clear()
        {
            _idle.Clear();
        }

        T Create()
        {
            T item = _factory();
            if (item == null)
                throw new InvalidOperationException("factory 返回了空引用");
            return item;
        }
    }
}
