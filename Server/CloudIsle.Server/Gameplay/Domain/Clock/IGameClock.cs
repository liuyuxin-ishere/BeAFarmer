using System.Threading;
using System.Threading.Tasks;

namespace CloudIsle.Domain.Clock
{
    /// <summary>
    /// 服务端有效时间。异步捕获；调用方在进入同步 SQLite 事务前取一次，事务内不得再 await 时钟。
    /// </summary>
    public interface IGameClock
    {
        /// <summary>
        /// 有效 now = max(rawUtc, persistedFloor)；若 floor 需抬升则<strong>先持久化再返回</strong>。
        /// DB 失败时不得返回未持久的时间。多实例不得压低已持久 floor。
        /// </summary>
        Task<long> GetEffectiveNowMsAsync(CancellationToken ct);
    }
}
