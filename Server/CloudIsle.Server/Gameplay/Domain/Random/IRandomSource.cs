namespace CloudIsle.Domain.Random
{
    /// <summary>
    /// 玩法抽签源（Harvest / Gather）：区间为 [minInclusive, maxExclusive)。
    /// 生产用 <see cref="ServerRandomSource"/>；测试可替换或装饰，不计生产调试路由。
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>返回 [minInclusive, maxExclusive) 内均匀整数；maxExclusive 须严格大于 minInclusive。</summary>
        int NextInt(int minInclusive, int maxExclusive);
    }
}
