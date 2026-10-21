using System;
using Game.Framework.Core;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class ObjectPoolTests
    {
        sealed class Sample
        {
            public int Value;
        }

        [Test]
        public void Return_RunsReset()
        {
            int resets = 0;
            ObjectPool<Sample> pool = new ObjectPool<Sample>(
                () => new Sample(),
                item =>
                {
                    resets++;
                    item.Value = 0;
                },
                4);

            Sample sample = pool.Rent();
            sample.Value = 7;
            pool.Return(sample);

            Assert.That(resets, Is.EqualTo(1));
            Assert.That(sample.Value, Is.EqualTo(0));
        }

        [Test]
        public void Rent_AfterReturn_HasNoDirtyState()
        {
            ObjectPool<Sample> pool = new ObjectPool<Sample>(
                () => new Sample(),
                item => item.Value = 0,
                4);

            Sample first = pool.Rent();
            first.Value = 7;
            pool.Return(first);

            Sample second = pool.Rent();

            Assert.That(second, Is.SameAs(first));
            Assert.That(second.Value, Is.EqualTo(0));
        }

        [Test]
        public void Return_Twice_Throws()
        {
            ObjectPool<Sample> pool = new ObjectPool<Sample>(
                () => new Sample(),
                item => item.Value = 0,
                4);
            Sample sample = pool.Rent();
            pool.Return(sample);

            Assert.Throws<InvalidOperationException>(() => pool.Return(sample));
        }

        [Test]
        public void Return_BeyondMaxRetained_DropsExtraInstance()
        {
            int created = 0;
            ObjectPool<Sample> pool = new ObjectPool<Sample>(
                () =>
                {
                    created++;
                    return new Sample();
                },
                item => item.Value = 0,
                1);

            Sample first = pool.Rent();
            Sample second = pool.Rent();
            first.Value = 1;
            second.Value = 2;
            pool.Return(first);
            pool.Return(second);

            Sample reused = pool.Rent();
            Sample fresh = pool.Rent();

            Assert.That(reused, Is.SameAs(first));
            Assert.That(reused.Value, Is.EqualTo(0));
            Assert.That(fresh, Is.Not.SameAs(first));
            Assert.That(fresh, Is.Not.SameAs(second));
            Assert.That(created, Is.EqualTo(3));
        }
    }
}
