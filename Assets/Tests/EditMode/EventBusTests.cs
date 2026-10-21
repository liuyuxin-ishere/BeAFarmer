using System;
using System.Threading;
using Game.Framework.Core;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class EventBusTests
    {
        struct SampleEvent
        {
            public int Value;
        }

        [Test]
        public void SubscribeOnce_ReceivesEachPublish()
        {
            EventBus bus = new EventBus();
            int calls = 0;
            int first = 0;
            int second = 0;
            bus.Subscribe<SampleEvent>(eventData =>
            {
                calls++;
                if (calls == 1)
                    first = eventData.Value;
                if (calls == 2)
                    second = eventData.Value;
            });

            bus.Publish(new SampleEvent { Value = 10 });
            bus.Publish(new SampleEvent { Value = 20 });

            Assert.That(calls, Is.EqualTo(2));
            Assert.That(first, Is.EqualTo(10));
            Assert.That(second, Is.EqualTo(20));
        }

        [Test]
        public void Dispose_StopsOnlyThatSubscription()
        {
            EventBus bus = new EventBus();
            int firstCalls = 0;
            int secondCalls = 0;
            IDisposable first = bus.Subscribe<SampleEvent>(_ => firstCalls++);
            bus.Subscribe<SampleEvent>(_ => secondCalls++);

            bus.Publish(new SampleEvent { Value = 1 });
            first.Dispose();
            bus.Publish(new SampleEvent { Value = 2 });

            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.EqualTo(2));
        }

        [Test]
        public void Dispose_Twice_DoesNotThrow()
        {
            EventBus bus = new EventBus();
            int calls = 0;
            IDisposable subscription = bus.Subscribe<SampleEvent>(_ => calls++);

            subscription.Dispose();
            Assert.DoesNotThrow(() => subscription.Dispose());
            bus.Publish(new SampleEvent { Value = 1 });

            Assert.That(calls, Is.EqualTo(0));
        }

        [Test]
        public void Publish_OnAnotherThread_Throws()
        {
            EventBus bus = new EventBus();
            int calls = 0;
            bus.Subscribe<SampleEvent>(_ => calls++);

            Exception caught = null;
            Thread thread = new Thread(() =>
            {
                try
                {
                    bus.Publish(new SampleEvent { Value = 1 });
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });
            thread.Start();
            thread.Join();

            Assert.That(caught, Is.InstanceOf<InvalidOperationException>());
            Assert.That(calls, Is.EqualTo(0));
        }
    }
}
