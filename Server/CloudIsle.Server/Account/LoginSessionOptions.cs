using System.Globalization;

namespace CloudIsle.Server.Account;

/// <summary>
/// 登录时使用的版本门槛和令牌期限。不下载补丁，也不代替 T44 的版本服务。
/// </summary>
public sealed class LoginSessionOptions
{
    /// <summary>未另行指定时，新令牌从当前 UTC 起算的有效期。</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(24);

    readonly long _lifetimeMs;
    readonly Func<long> _utcNowMs;
    readonly AppVersion? _minimumApp;

    /// <summary>
    /// 记下调用方明确给出的协议、资源和配置门槛。
    /// 最低包体版本留空时，登录不比较 app_version。
    /// </summary>
    /// <param name="expectedProtoVersion">客户端 proto_version 必须等于这个整数。不相等即不兼容。</param>
    /// <param name="requiredResVersion">允许登录的最低资源版本。不能为负。更高的客户端版本允许通过。</param>
    /// <param name="requiredConfigVersion">允许登录的最低配置版本。不能为负。更高的客户端版本允许通过。</param>
    /// <param name="tokenLifetime">新令牌的有效期。为空时用 24 小时。零、负数或不足 1 毫秒时拒绝。</param>
    /// <param name="minimumAppVersion">最低包体版本，格式为主.次.修订。空白表示本项不检查包体。</param>
    /// <param name="utcNowMs">当前 UTC Unix 毫秒。为空时读取系统时钟。返回值只用于时间和到期计算。</param>
    public LoginSessionOptions(
        int expectedProtoVersion,
        int requiredResVersion,
        int requiredConfigVersion,
        TimeSpan? tokenLifetime = null,
        string? minimumAppVersion = null,
        Func<long>? utcNowMs = null)
    {
        if (requiredResVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredResVersion), "最低资源版本不能为负");
        if (requiredConfigVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredConfigVersion), "最低配置版本不能为负");

        TimeSpan lifetime = tokenLifetime ?? DefaultTokenLifetime;
        _lifetimeMs = ToLifetimeMilliseconds(lifetime);
        if (!string.IsNullOrWhiteSpace(minimumAppVersion))
        {
            if (!AppVersion.TryParse(minimumAppVersion, out AppVersion parsed))
                throw new ArgumentException("最低包体版本必须是主.次.修订", nameof(minimumAppVersion));
            _minimumApp = parsed;
            MinimumAppVersion = parsed.Text;
        }

        ExpectedProtoVersion = expectedProtoVersion;
        RequiredResVersion = requiredResVersion;
        RequiredConfigVersion = requiredConfigVersion;
        TokenLifetime = lifetime;
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>客户端协议整数必须与它相等。</summary>
    public int ExpectedProtoVersion { get; }

    /// <summary>响应 need_res_version 使用的最低资源版本。</summary>
    public int RequiredResVersion { get; }

    /// <summary>响应 need_config_version 使用的最低配置版本。</summary>
    public int RequiredConfigVersion { get; }

    /// <summary>本次采用的令牌有效期。默认是 24 小时。</summary>
    public TimeSpan TokenLifetime { get; }

    /// <summary>已配置的最低包体版本原文。未配置时为空。</summary>
    public string? MinimumAppVersion { get; }

    /// <summary>读取当前 UTC Unix 毫秒。</summary>
    public long UtcNowMs()
    {
        return _utcNowMs();
    }

    /// <summary>
    /// 用当前时间和配置期限计算到期毫秒。
    /// 当前时间为负、相加溢出，或结果不晚于当前时间时失败，不返回可用期限。
    /// </summary>
    /// <param name="nowUnixMs">调用方刚刚读取的 UTC Unix 毫秒。</param>
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

    /// <summary>协议必须精确等于期望值。文档只把不相容变更递增为新整数，这里不做字段级兼容。</summary>
    /// <param name="protoVersion">请求里的 proto_version。</param>
    public bool IsProtoCompatible(int protoVersion)
    {
        return protoVersion == ExpectedProtoVersion;
    }

    /// <summary>低于最低资源版本则不兼容。负值不是递增版本。高于最低值允许通过。</summary>
    /// <param name="resVersion">请求里的 res_version。</param>
    public bool IsResCompatible(int resVersion)
    {
        return resVersion >= 0 && resVersion >= RequiredResVersion;
    }

    /// <summary>低于最低配置版本则不兼容。负值不是递增版本。高于最低值允许通过。</summary>
    /// <param name="configVersion">请求里的 config_version。</param>
    public bool IsConfigCompatible(int configVersion)
    {
        return configVersion >= 0 && configVersion >= RequiredConfigVersion;
    }

    /// <summary>
    /// 未配置最低包体版本时直接通过。
    /// 已配置时，客户端必须是主.次.修订，并且数值上不低于最低版本。
    /// </summary>
    /// <param name="appVersion">请求里的 app_version。</param>
    public bool IsAppCompatible(string? appVersion)
    {
        if (_minimumApp == null)
            return true;
        if (!AppVersion.TryParse(appVersion, out AppVersion client))
            return false;
        return client.CompareTo(_minimumApp.Value) >= 0;
    }

    /// <summary>把正的期限折成毫秒。不足 1 毫秒或不是正数时拒绝。</summary>
    /// <param name="lifetime">调用方给出的期限。</param>
    static long ToLifetimeMilliseconds(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "令牌期限必须为正");

        long milliseconds = lifetime.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "令牌期限必须至少为 1 毫秒");
        return milliseconds;
    }

    /// <summary>主.次.修订三个非负整数。比较时按数值，不按字符串字典序。</summary>
    readonly struct AppVersion
    {
        AppVersion(int major, int minor, int patch, string text)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Text = text;
        }

        /// <summary>主版本。</summary>
        public int Major { get; }

        /// <summary>次版本。</summary>
        public int Minor { get; }

        /// <summary>修订版本。</summary>
        public int Patch { get; }

        /// <summary>规范化后的主.次.修订原文。</summary>
        public string Text { get; }

        /// <summary>只接受三段非负整数。空串、缺段、符号和空白都不是包体版本。</summary>
        /// <param name="text">待解析的版本文本。</param>
        /// <param name="version">成功时为三个数值；失败时为默认值。</param>
        public static bool TryParse(string? text, out AppVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split('.');
            if (parts.Length != 3)
                return false;
            if (!TryPart(parts[0], out int major) || !TryPart(parts[1], out int minor) || !TryPart(parts[2], out int patch))
                return false;

            version = new AppVersion(
                major,
                minor,
                patch,
                string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", major, minor, patch));
            return true;
        }

        /// <summary>先比主版本，再比次版本，最后比修订。负数表示小于另一个。</summary>
        /// <param name="other">配置中的最低包体版本。</param>
        public int CompareTo(AppVersion other)
        {
            int major = Major.CompareTo(other.Major);
            if (major != 0)
                return major;
            int minor = Minor.CompareTo(other.Minor);
            if (minor != 0)
                return minor;
            return Patch.CompareTo(other.Patch);
        }

        /// <summary>一段必须是不带符号和空白的十进制非负整数。</summary>
        /// <param name="text">点号之间的一段。</param>
        /// <param name="value">成功时的数值。</param>
        static bool TryPart(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }
    }
}
