using System;

namespace CloudIsle.Domain.Random
{
    /// <summary>生产安全随机源；单例默认供 Harvest 正式规则使用。测试勿改共享实例状态。</summary>
    public sealed class ServerRandomSource : IRandomSource
    {
        public static ServerRandomSource Shared { get; } = new ServerRandomSource();

        readonly object _gate = new();
        readonly System.Random _rng;

        public ServerRandomSource()
            : this(new System.Random())
        {
        }

        public ServerRandomSource(int seed)
            : this(new System.Random(seed))
        {
        }

        ServerRandomSource(System.Random rng)
        {
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            lock (_gate)
                return _rng.Next(minInclusive, maxExclusive);
        }
    }

    /// <summary>
    /// 确定性抽签：始终返回区间下界。供故障矩阵/可控夹具注入，不作为生产默认工厂。
    /// Harvest 对 NextInt(0,span) 得 0 → inclusive 产量取 YieldMin。
    /// </summary>
    public sealed class MinInclusiveRandomSource : IRandomSource
    {
        public static MinInclusiveRandomSource Shared { get; } = new MinInclusiveRandomSource();

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return minInclusive;
        }
    }

    /// <summary>
    /// 单次命令求值用计数装饰器：只累加本实例调用，禁止跨命令共享可变计数。
    /// 异常/拒绝路径仍可读 <see cref="Calls"/>，与默认生产 Shared 分离。
    /// </summary>
    public sealed class CallCountingRandomSource : IRandomSource
    {
        readonly IRandomSource _inner;
        int _calls;

        public CallCountingRandomSource(IRandomSource inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public int Calls => _calls;

        public int NextInt(int minInclusive, int maxExclusive)
        {
            _calls = checked(_calls + 1);
            return _inner.NextInt(minInclusive, maxExclusive);
        }
    }

    /// <summary>
    /// Gather 故障/夹具确定性源：产量取区间下界；稀有 Scale（1e6）取上界-1 落空。
    /// 仅测试 DI / AcceptanceHost / FaultWorker 注入；不作为生产默认，不冒充真实分布。
    /// </summary>
    public sealed class MinYieldMissRareRandomSource : IRandomSource
    {
        public static MinYieldMissRareRandomSource Shared { get; } = new MinYieldMissRareRandomSource();

        public const int RareScalePpm = 1_000_000;

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            long span = (long)maxExclusive - minInclusive;
            if (span >= RareScalePpm)
                return checked(maxExclusive - 1);
            return minInclusive;
        }
    }

    /// <summary>
    /// 任意抽签即抛；供重放零增量夹具证明已提交路径不得再调 RNG。
    /// 仅测试 DI / FaultWorker 注入，不入生产默认表。
    /// </summary>
    public sealed class ThrowingRandomSource : IRandomSource
    {
        public static ThrowingRandomSource Shared { get; } = new ThrowingRandomSource();

        public int NextInt(int minInclusive, int maxExclusive)
        {
            throw new InvalidOperationException(
                "ThrowingRandomSource: committed replay must not draw");
        }
    }
}
