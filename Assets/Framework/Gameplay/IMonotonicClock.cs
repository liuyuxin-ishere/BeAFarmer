namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 单调经过时间源。显示时钟唯一本地依据；可注入测试，不读 OS UTC。
    /// </summary>
    public interface IMonotonicClock
    {
        /// <summary>自启动以来的单调毫秒（非墙钟）。</summary>
        long ElapsedMilliseconds { get; }
    }
}
