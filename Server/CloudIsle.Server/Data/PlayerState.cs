using CloudIsle.Domain.Farm;
using CloudIsle.Domain.Trade;
using CloudIsle.Proto;

namespace CloudIsle.Server.Data;

/// <summary>
/// 一次已经配对的玩家快照。Dirty 只由 Revision 与 SavedRevision 是否相等决定。
/// ShopPurchases 为 typed durable sidecar（schema6）；AcceptedTasks 为 schema7；
/// 均与 PlayerFullData 同事务持久，不进 Proto。
/// </summary>
public sealed class PlayerState
{
    /// <summary>记下这一次快照的玩家、数据和两个版本。</summary>
    /// <param name="playerId">玩家主键，必须是正数。</param>
    /// <param name="data">这一版的完整玩家数据。调用方此后不要再改这同一份对象。</param>
    /// <param name="revision">这版数据的版本，不能为负。</param>
    /// <param name="savedRevision">已经确认写入数据库的版本，不能为负。</param>
    /// <param name="shopPurchases">当日商店购买计数；null 视为空。</param>
    /// <param name="acceptedTasks">已接受任务规格；null 视为空。</param>
    public PlayerState(
        long playerId,
        PlayerFullData data,
        long revision,
        long savedRevision,
        ShopPurchaseCounts? shopPurchases = null,
        AcceptedTaskState? acceptedTasks = null)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId), "玩家主键必须是正数");
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), "版本不能为负");
        if (savedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(savedRevision), "已保存版本不能为负");

        PlayerId = playerId;
        Data = data;
        Revision = revision;
        SavedRevision = savedRevision;
        ShopPurchases = shopPurchases?.Clone() ?? ShopPurchaseCounts.Empty;
        AcceptedTasks = acceptedTasks?.Clone() ?? AcceptedTaskState.Empty;
    }

    /// <summary>玩家主键。</summary>
    public long PlayerId { get; }

    /// <summary>这一版的完整玩家数据。</summary>
    public PlayerFullData Data { get; }

    /// <summary>这版数据的版本。</summary>
    public long Revision { get; }

    /// <summary>已经确认写入数据库的版本。</summary>
    public long SavedRevision { get; }

    /// <summary>当日商店购买计数 sidecar。</summary>
    public ShopPurchaseCounts ShopPurchases { get; }

    /// <summary>已接受任务规格 sidecar（收获等）。</summary>
    public AcceptedTaskState AcceptedTasks { get; }

    /// <summary>两个版本不相等时表示还有未确认的修改。</summary>
    public bool Dirty => Revision != SavedRevision;

    /// <summary>复制数据、sidecar 和两个版本。改返回值不会改原来的对象。</summary>
    public PlayerState DeepCopy()
    {
        return new PlayerState(
            PlayerId, Data.Clone(), Revision, SavedRevision, ShopPurchases, AcceptedTasks);
    }

    /// <summary>同 revision/saved 下替换商店计数（日切/购买候选）。</summary>
    public PlayerState WithShopPurchases(ShopPurchaseCounts purchases)
    {
        if (purchases == null)
            throw new ArgumentNullException(nameof(purchases));
        return new PlayerState(
            PlayerId, Data.Clone(), Revision, SavedRevision, purchases, AcceptedTasks);
    }

    /// <summary>同 revision/saved 下替换已接受任务规格。</summary>
    public PlayerState WithAcceptedTasks(AcceptedTaskState tasks)
    {
        if (tasks == null)
            throw new ArgumentNullException(nameof(tasks));
        return new PlayerState(
            PlayerId, Data.Clone(), Revision, SavedRevision, ShopPurchases, tasks);
    }
}
