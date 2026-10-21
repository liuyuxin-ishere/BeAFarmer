using System;
using Game.Framework.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.EditModeTests
{
    public sealed class TimerServiceTests
    {
        [Test]
        public void Tick_FiresWhileTimeScaleIsZero()
        {
            float previousScale = Time.timeScale;
            Time.timeScale = 0f;
            try
            {
                TimerService timers = new TimerService();
                int calls = 0;
                timers.Schedule(TimeSpan.FromSeconds(0.5d), () => calls++, false);

                timers.Tick(0.5d);

                Assert.That(calls, Is.EqualTo(1));
            }
            finally
            {
                Time.timeScale = previousScale;
            }
        }

        [Test]
        public void Cancel_PreventsCallback()
        {
            TimerService timers = new TimerService();
            int calls = 0;
            TimerHandle handle = timers.Schedule(TimeSpan.FromSeconds(0.25d), () => calls++, false);

            bool cancelled = timers.Cancel(handle);
            timers.Tick(1d);

            Assert.That(cancelled, Is.True);
            Assert.That(calls, Is.EqualTo(0));
        }

        [Test]
        public void Schedule_RejectsNonPositiveRepeatDelay()
        {
            TimerService timers = new TimerService();
            int calls = 0;
            Action callback = () => calls++;

            Assert.Throws<ArgumentOutOfRangeException>(
                () => timers.Schedule(TimeSpan.Zero, callback, true));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => timers.Schedule(TimeSpan.FromSeconds(-1d), callback, true));

            timers.Tick(1d);
            Assert.That(calls, Is.EqualTo(0));
        }

        [Test]
        public void Cancel_OldGenerationDoesNotStopReplacement()
        {
            TimerService timers = new TimerService();
            int firstCalls = 0;
            int secondCalls = 0;
            TimerHandle first = timers.Schedule(TimeSpan.Zero, () => firstCalls++, false);
            timers.Tick(0d);

            bool cancelledOld = timers.Cancel(first);
            TimerHandle second = timers.Schedule(TimeSpan.FromSeconds(0.2d), () => secondCalls++, false);
            timers.Tick(0.2d);

            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(cancelledOld, Is.False);
            Assert.That(second.Id, Is.EqualTo(first.Id));
            Assert.That(second.Generation, Is.Not.EqualTo(first.Generation));
            Assert.That(secondCalls, Is.EqualTo(1));
        }
    }
}
