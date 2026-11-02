using System;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 玩法会话上下文。internal，避免 xLua reflection 暴露 MarkReady 等控制面。
    /// </summary>
    internal sealed class GameplaySessionContext
    {
        // An implicit constructor is public in reflection even for an internal
        // class. xLua must not be able to construct a session control object.
        internal GameplaySessionContext()
        {
        }

        readonly object _gate = new object();
        long _epoch;
        string _environmentKey = "";
        long _connectionGeneration;
        int _authAttempt;
        string _playerId = "";
        string _scopeId = "";
        bool _ready;
        bool _writeLocked = true;
        string _writeFreezeReason = "";

        internal long Epoch
        {
            get { lock (_gate) return _epoch; }
        }

        internal bool IsReady
        {
            get { lock (_gate) return _ready; }
        }

        internal bool IsWriteLocked
        {
            get { lock (_gate) return _writeLocked; }
        }

        internal string WriteFreezeReason
        {
            get { lock (_gate) return _writeFreezeReason; }
        }

        internal AuthenticationStamp BeginAuthentication(int authAttempt, string environmentKey, long connectionGeneration)
        {
            if (authAttempt <= 0)
                throw new ArgumentOutOfRangeException(nameof(authAttempt));
            if (string.IsNullOrEmpty(environmentKey))
                throw new ArgumentException("environmentKey required", nameof(environmentKey));
            lock (_gate)
            {
                _epoch++;
                _authAttempt = authAttempt;
                _environmentKey = environmentKey;
                _connectionGeneration = connectionGeneration;
                _playerId = "";
                _scopeId = "";
                _ready = false;
                _writeLocked = true;
                return new AuthenticationStamp(_environmentKey, _epoch, _connectionGeneration, _authAttempt);
            }
        }

        internal SessionStamp Capture()
        {
            lock (_gate)
            {
                return new SessionStamp(_environmentKey, _playerId, _scopeId, _epoch, _connectionGeneration);
            }
        }

        internal SessionStamp AttachCandidate(AuthenticationStamp authStamp, string playerId, string scopeId)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("playerId required", nameof(playerId));
            if (string.IsNullOrEmpty(scopeId))
                throw new ArgumentException("scopeId required", nameof(scopeId));
            lock (_gate)
            {
                if (authStamp.Epoch != _epoch
                    || authStamp.AuthAttempt != _authAttempt
                    || authStamp.ConnectionGeneration != _connectionGeneration
                    || !string.Equals(authStamp.EnvironmentKey, _environmentKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("authStamp mismatch");
                }

                _playerId = playerId;
                _scopeId = scopeId;
                _ready = false;
                _writeLocked = true;
                return Capture();
            }
        }

        internal void MarkReady(SessionStamp stamp)
        {
            lock (_gate)
            {
                if (!Matches_NoLock(stamp))
                    throw new InvalidOperationException("sessionStamp mismatch");
                if (string.IsNullOrEmpty(_playerId) || string.IsNullOrEmpty(_scopeId))
                    throw new InvalidOperationException("candidate not attached");
                _ready = true;
                // 同步冻写期间不开放新写
                if (string.IsNullOrEmpty(_writeFreezeReason))
                    _writeLocked = false;
            }
        }

        /// <summary>Gap/Invalid/Sync：冻写；不抬 epoch。</summary>
        internal void FreezeWrites(SessionStamp stamp, string reason)
        {
            lock (_gate)
            {
                if (!Matches_NoLock(stamp))
                    return;
                _writeLocked = true;
                _writeFreezeReason = reason ?? "frozen";
            }
        }

        /// <summary>同步恢复连续后解除冻写（仍须 Ready）。</summary>
        internal void ClearWriteFreeze(SessionStamp stamp)
        {
            lock (_gate)
            {
                if (!Matches_NoLock(stamp))
                    return;
                _writeFreezeReason = "";
                if (_ready)
                    _writeLocked = false;
            }
        }

        internal void Invalidate(SessionEndReason reason)
        {
            lock (_gate)
            {
                _epoch++;
                _ready = false;
                _writeLocked = true;
                _writeFreezeReason = "";
                _playerId = "";
                _scopeId = "";
            }
        }

        internal bool Matches(SessionStamp stamp)
        {
            lock (_gate)
                return Matches_NoLock(stamp);
        }

        bool Matches_NoLock(SessionStamp stamp)
        {
            return stamp.Epoch == _epoch
                   && stamp.ConnectionGeneration == _connectionGeneration
                   && string.Equals(stamp.EnvironmentKey, _environmentKey, StringComparison.Ordinal)
                   && string.Equals(stamp.PlayerId, _playerId, StringComparison.Ordinal)
                   && string.Equals(stamp.ScopeId, _scopeId, StringComparison.Ordinal);
        }
    }
}
