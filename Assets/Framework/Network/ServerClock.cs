using System;
using System.Threading;

namespace Game.Framework.Network
{
    /// <summary>
    /// 保存服务端时间与本机 UTC 的差值，供界面显示。
    /// 服务端的发奖和超时不读取这里的结果。
    /// </summary>
    public sealed class ServerClock
    {
        long _offsetMs;

        /// <summary>
        /// 用一对同时取得的时间戳记下偏移。偏移等于服务端毫秒减去本机毫秒。
        /// </summary>
        /// <param name="serverUnixMs">响应里的服务端 UTC Unix 毫秒。</param>
        /// <param name="localUnixMs">收到该响应时的本机 UTC Unix 毫秒。</param>
        public void Synchronize(long serverUnixMs, long localUnixMs)
        {
            Interlocked.Exchange(ref _offsetMs, serverUnixMs - localUnixMs);
        }

        /// <summary>
        /// 返回校时后的 UTC Unix 毫秒。还没校时过时，结果就是本机 UTC。
        /// </summary>
        public long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Read(ref _offsetMs);
        }
    }
}
