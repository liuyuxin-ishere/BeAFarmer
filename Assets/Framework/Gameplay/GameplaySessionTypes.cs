using System;

namespace Game.Framework.Gameplay
{
    /// <summary>会话结束原因。</summary>
    public enum SessionEndReason
    {
        None = 0,
        Logout = 1,
        Kick = 2,
        AuthFailed = 3,
        VersionRejected = 4,
        Replaced = 5,
        Shutdown = 6,
    }

    /// <summary>
    /// BeginAuthentication 阶段 stamp：玩家/scope 尚未知。
    /// </summary>
    public readonly struct AuthenticationStamp : IEquatable<AuthenticationStamp>
    {
        public AuthenticationStamp(string environmentKey, long epoch, long connectionGeneration, int authAttempt)
        {
            EnvironmentKey = environmentKey ?? "";
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
            AuthAttempt = authAttempt;
        }

        public string EnvironmentKey { get; }
        public long Epoch { get; }
        public long ConnectionGeneration { get; }
        public int AuthAttempt { get; }

        public bool Equals(AuthenticationStamp other)
        {
            return Epoch == other.Epoch
                   && ConnectionGeneration == other.ConnectionGeneration
                   && AuthAttempt == other.AuthAttempt
                   && string.Equals(EnvironmentKey, other.EnvironmentKey, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is AuthenticationStamp o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Epoch.GetHashCode();
                h = (h * 397) ^ ConnectionGeneration.GetHashCode();
                h = (h * 397) ^ AuthAttempt;
                h = (h * 397) ^ (EnvironmentKey != null ? EnvironmentKey.GetHashCode() : 0);
                return h;
            }
        }
    }

    /// <summary>
    /// 含玩家/scope 的会话 stamp；每回调与模型确认携带。
    /// </summary>
    public readonly struct SessionStamp : IEquatable<SessionStamp>
    {
        public SessionStamp(
            string environmentKey,
            string playerId,
            string scopeId,
            long epoch,
            long connectionGeneration)
        {
            EnvironmentKey = environmentKey ?? "";
            PlayerId = playerId ?? "";
            ScopeId = scopeId ?? "";
            Epoch = epoch;
            ConnectionGeneration = connectionGeneration;
        }

        public string EnvironmentKey { get; }
        public string PlayerId { get; }
        public string ScopeId { get; }
        public long Epoch { get; }
        public long ConnectionGeneration { get; }

        public bool IsEmpty => Epoch == 0 && string.IsNullOrEmpty(PlayerId);

        public bool Equals(SessionStamp other)
        {
            return Epoch == other.Epoch
                   && ConnectionGeneration == other.ConnectionGeneration
                   && string.Equals(EnvironmentKey, other.EnvironmentKey, StringComparison.Ordinal)
                   && string.Equals(PlayerId, other.PlayerId, StringComparison.Ordinal)
                   && string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is SessionStamp o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Epoch.GetHashCode();
                h = (h * 397) ^ ConnectionGeneration.GetHashCode();
                h = (h * 397) ^ (EnvironmentKey != null ? EnvironmentKey.GetHashCode() : 0);
                h = (h * 397) ^ (PlayerId != null ? PlayerId.GetHashCode() : 0);
                h = (h * 397) ^ (ScopeId != null ? ScopeId.GetHashCode() : 0);
                return h;
            }
        }
    }
}
