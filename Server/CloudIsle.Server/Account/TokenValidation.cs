namespace CloudIsle.Server.Account;

/// <summary>
/// 令牌校验结果。有效时 Code 为 0 并带玩家编号；无效时 Code 为 204 且玩家编号为 0。
/// </summary>
public sealed class TokenValidation
{
    /// <summary>摘要仍有效。</summary>
    public const int ValidCode = 0;

    /// <summary>空白、坏格式、未知、到期或已撤销。</summary>
    public const int InvalidCode = 204;

    /// <summary>记录一次校验结论。</summary>
    /// <param name="code">0 表示有效，204 表示无效。</param>
    /// <param name="playerId">有效时为玩家编号，无效时必须为 0。</param>
    public TokenValidation(int code, long playerId)
    {
        Code = code;
        PlayerId = playerId;
    }

    /// <summary>0 有效，204 无效。</summary>
    public int Code { get; }

    /// <summary>有效时的玩家编号。无效时为 0，不带回玩家快照。</summary>
    public long PlayerId { get; }

    /// <summary>组成一条有效结果。</summary>
    /// <param name="playerId">库中读到的正数玩家编号。</param>
    public static TokenValidation Valid(long playerId)
    {
        return new TokenValidation(ValidCode, playerId);
    }

    /// <summary>组成一条无效结果，玩家编号固定为 0。</summary>
    public static TokenValidation Invalid()
    {
        return new TokenValidation(InvalidCode, 0);
    }
}
