using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 账号链 → 玩法会话：BeginAuth → 候选快照安装（ready=false）→ Token 代次提交 → Recover → MarkReady。
    /// Token 仍由 Account 拥有；安装/恢复失败不 Ready。
    /// </summary>
    internal sealed class AccountGameplayAdapter
    {
        readonly GameplaySessionContext _context;
        readonly GameplayCommandClient _client;
        readonly IGameplayModelPort _model;
        readonly string _environmentKey;
        long _modelRevision;
        AuthenticationStamp _authStamp;
        SessionStamp _sessionStamp;
        bool _tokenCommitted;
        bool _recoverDone;
        bool _candidateInstalled;

        public AccountGameplayAdapter(
            GameplaySessionContext context,
            GameplayCommandClient client,
            IGameplayModelPort modelPort,
            string environmentKey)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _model = modelPort ?? new NullGameplayModelPort();
            _environmentKey = environmentKey ?? "default";
        }

        public long ModelRevision => _modelRevision;

        public AuthenticationStamp BeginAuthentication(int authAttempt, long connectionGeneration)
        {
            _tokenCommitted = false;
            _recoverDone = false;
            _candidateInstalled = false;
            _modelRevision = 0;
            _authStamp = _context.BeginAuthentication(authAttempt, _environmentKey, connectionGeneration);
            return _authStamp;
        }

        /// <summary>
        /// 安装候选快照（不 MarkReady）。经 <see cref="IGameplayModelPort"/> 唯一写入 PlayerModel。
        /// 失败清候选与模型，返回 false。
        /// </summary>
        public bool AttachAndInstallCandidate(string playerId, string scopeId, string snapshotJson)
        {
            try
            {
                _sessionStamp = _context.AttachCandidate(_authStamp, playerId, scopeId);
                _model.Clear(_sessionStamp, "pre-install");
                if (!_model.TryInstallSnapshot(_sessionStamp, snapshotJson, out long revision, out string error))
                {
                    _context.Invalidate(SessionEndReason.AuthFailed);
                    _candidateInstalled = false;
                    _ = error;
                    return false;
                }

                _modelRevision = revision;
                _candidateInstalled = true;
                return true;
            }
            catch
            {
                try { _model.Clear(_sessionStamp, "install-exception"); } catch { /* ignore */ }
                _context.Invalidate(SessionEndReason.AuthFailed);
                _candidateInstalled = false;
                return false;
            }
        }

        /// <summary>Token 提交成功且 attempt 匹配后调用。</summary>
        public bool OnAuthenticationCommitted(int authAttempt)
        {
            if (authAttempt != _authStamp.AuthAttempt)
                return false;
            if (!_candidateInstalled)
                return false;
            _tokenCommitted = true;
            return true;
        }

        /// <summary>三门：模型候选 + Token + pending 恢复 通过才 MarkReady。</summary>
        public async UniTask<bool> CompleteReadyAsync(
            string accountKey,
            bool previousScopeClosed,
            CancellationToken ct)
        {
            return await CompleteReadyAsync(accountKey, _authStamp.AuthAttempt, previousScopeClosed, ct);
        }

        public async UniTask<bool> CompleteReadyAsync(
            string accountKey,
            int authAttempt,
            bool previousScopeClosed,
            CancellationToken ct)
        {
            if (!_tokenCommitted || !_candidateInstalled)
                return false;
            if (authAttempt != _authStamp.AuthAttempt)
                return false;
            try
            {
                await _client.RecoverAsync(
                    _sessionStamp, _environmentKey, accountKey, authAttempt, previousScopeClosed, ct);
                _recoverDone = true;
                if (!string.IsNullOrEmpty(_client.LockReason)
                    && (_client.LockReason.StartsWith("pending-locked", StringComparison.Ordinal)
                        || _client.LockReason.StartsWith("payload-conflict", StringComparison.Ordinal)))
                {
                    _context.Invalidate(SessionEndReason.AuthFailed);
                    return false;
                }

                // 有 Unknown/Prepared 占槽：允许 Ready 只读，但保持写锁（IsBusy）
                // MarkReady 打开写门；Client.Send 仍因 busy/no-next 拒绝新写
                _context.MarkReady(_sessionStamp);
                return true;
            }
            catch
            {
                _context.Invalidate(SessionEndReason.AuthFailed);
                return false;
            }
        }

        public void OnSessionEnded(SessionEndReason reason)
        {
            _context.Invalidate(reason);
            _tokenCommitted = false;
            _recoverDone = false;
            _candidateInstalled = false;
        }

        public void ObserveModelRevision(long revision)
        {
            if (revision > _modelRevision)
                _modelRevision = revision;
            _client.ObserveModelRevision(_sessionStamp, revision);
        }

        public bool IsRecoverDone => _recoverDone;
        public bool IsCandidateInstalled => _candidateInstalled;
        public SessionStamp SessionStamp => _sessionStamp;
    }
}
