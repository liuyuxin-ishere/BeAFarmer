namespace CloudIsle.Server.Account;

/// <summary>
/// 令牌期限与时钟；版本门槛委托 <see cref="ServerRequirements"/> / <see cref="ClientCompatibilityPolicy"/>。
/// null/空白 minimumApp → ProductionM2（禁止旁路）。
/// </summary>
public sealed class LoginSessionOptions
{
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(24);

    readonly long _lifetimeMs;
    readonly Func<long> _utcNowMs;
    readonly ServerRequirements _requirements;

    /// <param name="expectedProtoVersion">客户端 proto 必须等于此值。</param>
    /// <param name="requiredResVersion">最低资源版本。</param>
    /// <param name="requiredConfigVersion">最低配置版本（应与已加载 GameConfigSnapshot.Version 一致）。</param>
    /// <param name="tokenLifetime">令牌有效期；默认 24h。</param>
    /// <param name="minimumAppVersion">最低 App；null/空白→0.4.0；非法显式空白抛错。</param>
    /// <param name="utcNowMs">UTC 毫秒时钟。</param>
    /// <param name="requiredBridgeContract">桥契约；空白→m2-bridge-0.4。</param>
    public LoginSessionOptions(
        int expectedProtoVersion,
        int requiredResVersion,
        int requiredConfigVersion,
        TimeSpan? tokenLifetime = null,
        string? minimumAppVersion = null,
        Func<long>? utcNowMs = null,
        string? requiredBridgeContract = null)
    {
        if (requiredResVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredResVersion), "最低资源版本不能为负");
        if (requiredConfigVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredConfigVersion), "最低配置版本不能为负");

        TimeSpan lifetime = tokenLifetime ?? DefaultTokenLifetime;
        _lifetimeMs = ToLifetimeMilliseconds(lifetime);

        // 显式空白串（非 null）视为非法，防止测试/调用方误用旁路
        if (minimumAppVersion != null && string.IsNullOrWhiteSpace(minimumAppVersion))
            throw new ArgumentException("minimumAppVersion blank is not allowed; omit or pass ProductionM2", nameof(minimumAppVersion));

        string minApp = string.IsNullOrWhiteSpace(minimumAppVersion)
            ? ServerRequirements.ProductionM2.MinAppVersion
            : minimumAppVersion.Trim();
        string bridge = string.IsNullOrWhiteSpace(requiredBridgeContract)
            ? ServerRequirements.ProductionM2.RequiredBridgeContract
            : requiredBridgeContract.Trim();

        _requirements = new ServerRequirements(
            minApp, bridge, expectedProtoVersion, requiredResVersion, requiredConfigVersion);

        ExpectedProtoVersion = expectedProtoVersion;
        RequiredResVersion = requiredResVersion;
        RequiredConfigVersion = requiredConfigVersion;
        RequiredBridgeContract = bridge;
        MinimumAppVersion = minApp;
        TokenLifetime = lifetime;
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public int ExpectedProtoVersion { get; }
    public int RequiredResVersion { get; }
    public int RequiredConfigVersion { get; }
    public string RequiredBridgeContract { get; }
    public TimeSpan TokenLifetime { get; }
    public string MinimumAppVersion { get; }

    public ServerRequirements ToRequirements() => _requirements;

    public long UtcNowMs() => _utcNowMs();

    public long ExpiresAtUtcMs(long nowUnixMs)
    {
        if (nowUnixMs < 0)
            throw new InvalidOperationException("当前 UTC 毫秒无效");

        long expires;
        try
        {
            expires = checked(nowUnixMs + _lifetimeMs);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("令牌到期时间溢出", ex);
        }

        if (expires <= nowUnixMs)
            throw new InvalidOperationException("令牌到期时间无效");
        return expires;
    }

    static long ToLifetimeMilliseconds(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "令牌期限必须为正");

        long milliseconds = lifetime.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "令牌期限必须至少为 1 毫秒");
        return milliseconds;
    }
}
