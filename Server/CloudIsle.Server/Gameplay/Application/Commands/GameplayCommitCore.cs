using CloudIsle.Server.Data;
using CloudIsle.Server.Players;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 公共提交入口：委托 ICommandCommitStore；提交后经 <see cref="CommittedPlayerCache"/> 发布。
/// 发布失败不回滚 DB：先 Invalidate，可选 Reload 观察，钩异常不掩盖 Durable。
/// </summary>
public sealed class GameplayCommitCore
{
    readonly ICommandCommitStore _store;
    readonly CommittedPlayerCache? _committedCache;
    readonly Action<long, string>? _onPublishFault;

    public GameplayCommitCore(
        ICommandCommitStore store,
        CommittedPlayerCache? committedCache = null,
        Action<long, string>? onPublishFault = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _committedCache = committedCache;
        _onPublishFault = onPublishFault;
    }

    public async Task<CommitOutcome> CommitAsync(CommitRequest request, CancellationToken completionCt)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (_committedCache != null && !PlayerQueue.IsCurrentPlayer(request.PlayerId))
            throw new InvalidOperationException("Configured commit core requires player lane before commit");
        CommitOutcome outcome = await _store.CommitAsync(request, completionCt).ConfigureAwait(false);
        if (!outcome.DurableCommitted)
            return outcome;

        bool publishFault = outcome.PostCommitFault;
        string faultReason = "post-commit-fault";

        if (_committedCache != null && !publishFault)
        {
            try
            {
                _committedCache.PublishCommitted(request.PlayerId, outcome);
            }
            catch (Exception ex)
            {
                publishFault = true;
                faultReason = ex.GetType().Name + ":" + ex.Message;
            }
        }

        if (publishFault)
        {
            try { _committedCache?.Invalidate(request.PlayerId, faultReason); }
            catch { /* invalidate best-effort */ }

            try { _onPublishFault?.Invoke(request.PlayerId, faultReason); }
            catch
            {
                // 观察钩异常：不抛出，调用方仍拿 DurableCommitted
            }
        }

        if (publishFault && !outcome.PostCommitFault)
        {
            return new CommitOutcome(
                outcome.DurableCommitted,
                outcome.CommittedRevision,
                outcome.NextOpId,
                outcome.ScopeOpen,
                outcome.ReceiptDetails,
                outcome.CommittedState,
                postCommitFault: true);
        }

        return outcome;
    }
}
