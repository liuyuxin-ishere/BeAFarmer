namespace CloudIsle.Server.Account;

/// <summary>
/// 已持久化的令牌摘要。只保存摘要和状态，不保存令牌原文。
/// </summary>
public sealed class TokenRecord
{
    /// <summary>用库中的一行组成记录。</summary>
    /// <param name="tokenHash">auth_token.token_hash，SHA-256 摘要的小写十六进制。</param>
    /// <param name="playerId">auth_token.player_id，对应的玩家。</param>
    /// <param name="expiresAtMs">auth_token.expires_at_ms，到期的 UTC Unix 毫秒。</param>
    /// <param name="revoked">auth_token.revoked 非 0 时为 true。</param>
    public TokenRecord(string tokenHash, long playerId, long expiresAtMs, bool revoked)
    {
        TokenHash = tokenHash;
        PlayerId = playerId;
        ExpiresAtMs = expiresAtMs;
        Revoked = revoked;
    }

    /// <summary>令牌摘要。不是令牌原文。</summary>
    public string TokenHash { get; }

    /// <summary>这条摘要所属的玩家。</summary>
    public long PlayerId { get; }

    /// <summary>到期时刻。校验时 now 大于等于该值则拒绝。</summary>
    public long ExpiresAtMs { get; }

    /// <summary>是否已撤销。</summary>
    public bool Revoked { get; }
}
