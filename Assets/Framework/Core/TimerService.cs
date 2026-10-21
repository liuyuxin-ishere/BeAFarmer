using System;
using System.Collections.Generic;

namespace Game.Framework.Core
{
    /// <summary>
    /// 用调用方传入的非缩放秒数推进倒计时。
    /// </summary>
    public sealed class TimerService
    {
        readonly List<Slot> _slots = new List<Slot> { null };
        readonly Stack<int> _freeIds = new Stack<int>();

        /// <summary>
        /// 登记一个倒计时。
        /// </summary>
        /// <param name="delay">第一次触发前等待的时间。循环时必须大于 0。</param>
        /// <param name="callback">到点时在 Tick 的调用线程上执行。空引用会抛异常。</param>
        /// <param name="repeat">true 表示按 delay 重复。false 表示只触发一次。</param>
        /// <returns>用于 Cancel 的句柄。</returns>
        public TimerHandle Schedule(TimeSpan delay, Action callback, bool repeat)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            double seconds = delay.TotalSeconds;
            if (repeat)
            {
                if (seconds <= 0d)
                    throw new ArgumentOutOfRangeException(nameof(delay), "循环间隔必须大于 0");
            }
            else if (seconds < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(delay), "单次延迟不能为负");
            }

            int id = _freeIds.Count > 0 ? _freeIds.Pop() : AddSlot();
            Slot slot = _slots[id];
            if (slot.Generation == 0)
                slot.Generation = 1;
            slot.Active = true;
            slot.Repeat = repeat;
            slot.Remaining = seconds;
            slot.Interval = seconds;
            slot.Callback = callback;
            return new TimerHandle(id, slot.Generation);
        }

        /// <summary>
        /// 取消还在等待的计时器。
        /// </summary>
        /// <param name="handle">Schedule 返回的句柄。</param>
        /// <returns>确实取消了一条尚未触发完的计时器时为 true。</returns>
        public bool Cancel(TimerHandle handle)
        {
            Slot slot = FindActive(handle);
            if (slot == null)
                return false;

            Deactivate(handle.Id, slot);
            return true;
        }

        /// <summary>
        /// 把倒计时向前推进一段非缩放时间。
        /// </summary>
        /// <param name="unscaledDeltaSeconds">这段时间的真实秒数，不受 timeScale 影响。负数会抛异常。</param>
        public void Tick(double unscaledDeltaSeconds)
        {
            if (unscaledDeltaSeconds < 0d)
                throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));

            int[] dueIds = SnapshotActiveIds();
            for (int i = 0; i < dueIds.Length; i++)
                Advance(dueIds[i], unscaledDeltaSeconds);
        }

        int AddSlot()
        {
            _slots.Add(new Slot());
            return _slots.Count - 1;
        }

        int[] SnapshotActiveIds()
        {
            int count = 0;
            int[] ids = new int[_slots.Count];
            for (int id = 1; id < _slots.Count; id++)
            {
                if (_slots[id].Active)
                    ids[count++] = id;
            }

            if (count == ids.Length)
                return ids;

            int[] snapshot = new int[count];
            Array.Copy(ids, snapshot, count);
            return snapshot;
        }

        void Advance(int id, double unscaledDeltaSeconds)
        {
            Slot slot = _slots[id];
            if (!slot.Active)
                return;

            int generation = slot.Generation;
            slot.Remaining -= unscaledDeltaSeconds;
            if (slot.Repeat)
            {
                while (slot.Active && slot.Generation == generation && slot.Remaining <= 0d)
                {
                    slot.Remaining += slot.Interval;
                    slot.Callback();
                }
                return;
            }

            if (!slot.Active || slot.Generation != generation || slot.Remaining > 0d)
                return;

            Action callback = slot.Callback;
            Deactivate(id, slot);
            callback();
        }

        Slot FindActive(TimerHandle handle)
        {
            if (handle.Id <= 0 || handle.Id >= _slots.Count)
                return null;

            Slot slot = _slots[handle.Id];
            if (!slot.Active || slot.Generation != handle.Generation)
                return null;
            return slot;
        }

        void Deactivate(int id, Slot slot)
        {
            slot.Active = false;
            slot.Callback = null;
            slot.Generation++;
            _freeIds.Push(id);
        }

        sealed class Slot
        {
            public int Generation;
            public bool Active;
            public bool Repeat;
            public double Remaining;
            public double Interval;
            public Action Callback;
        }
    }
}
