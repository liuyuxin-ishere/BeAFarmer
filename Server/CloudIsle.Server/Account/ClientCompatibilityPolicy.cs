using System.Globalization;

namespace CloudIsle.Server.Account;

/// <summary>客户端上报的安装/桥/协议/资源/配置面（仅事实字段，不作权威）。</summary>
public readonly struct ClientBuildInfo
{
    public ClientBuildInfo(
        string? appVersion,
        string? bridgeContract,
        int protoVersion,
        int resVersion,
        int configVersion)
    {
        AppVersion = appVersion ?? "";
        BridgeContract = bridgeContract ?? "";
        ProtoVersion = protoVersion;
        ResVersion = resVersion;
        ConfigVersion = configVersion;
    }

    public string AppVersion { get; }
    public string BridgeContract { get; }
    public int ProtoVersion { get; }
    public int ResVersion { get; }
    public int ConfigVersion { get; }
}

/// <summary>服务端要求的生产门槛；不取客户端自报为事实。空白 MinApp 不允许跳过。</summary>
public sealed class ServerRequirements
{
    public ServerRequirements(
        string minAppVersion,
        string requiredBridgeContract,
        int expectedProtoVersion,
        int requiredResVersion,
        int requiredConfigVersion)
    {
        if (string.IsNullOrWhiteSpace(requiredBridgeContract))
            throw new ArgumentException("requiredBridgeContract required", nameof(requiredBridgeContract));
        if (requiredResVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredResVersion));
        if (requiredConfigVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredConfigVersion));

        // null/空白 → 生产 0.4.0；禁止“空字符串跳过 App 检查”旁路（不依赖 ProductionM2 静态初始化顺序）
        string minApp = string.IsNullOrWhiteSpace(minAppVersion)
            ? "0.4.0"
            : minAppVersion.Trim();
        if (!ClientCompatibilityPolicy.TryParseAppVersion(minApp, out _))
            throw new ArgumentException("minAppVersion must be major.minor.patch", nameof(minAppVersion));

        MinAppVersion = minApp;
        RequiredBridgeContract = requiredBridgeContract.Trim();
        ExpectedProtoVersion = expectedProtoVersion;
        RequiredResVersion = requiredResVersion;
        RequiredConfigVersion = requiredConfigVersion;
    }

    /// <summary>生产 M2 默认：0.4.0 / m2-bridge-0.4 / Proto1 / Res200 / Config200。</summary>
    public static ServerRequirements ProductionM2 { get; } = new(
        "0.4.0",
        "m2-bridge-0.4",
        expectedProtoVersion: 1,
        requiredResVersion: 200,
        requiredConfigVersion: 200);

    public string MinAppVersion { get; }
    public string RequiredBridgeContract { get; }
    public int ExpectedProtoVersion { get; }
    public int RequiredResVersion { get; }
    public int RequiredConfigVersion { get; }
}

/// <summary>兼容性结论。不兼容时 Code=106。</summary>
public readonly struct CompatibilityResult
{
    public CompatibilityResult(bool compatible, int code, string reason)
    {
        Compatible = compatible;
        Code = code;
        Reason = reason ?? "";
    }

    public bool Compatible { get; }
    public int Code { get; }
    public string Reason { get; }

    public static CompatibilityResult Ok() => new(true, 0, "");
    public static CompatibilityResult Reject(string reason) => new(false, 106, reason);
}

/// <summary>
/// Login 与 Reconnect 共用的唯一纯策略。失败必须在任何 Token/scope 变更之前。
/// </summary>
public static class ClientCompatibilityPolicy
{
    public static CompatibilityResult Check(ClientBuildInfo build, ServerRequirements required)
    {
        if (required == null)
            throw new ArgumentNullException(nameof(required));

        if (string.IsNullOrWhiteSpace(build.BridgeContract))
            return CompatibilityResult.Reject("missing-bridge_contract");
        if (!string.Equals(build.BridgeContract.Trim(), required.RequiredBridgeContract, StringComparison.Ordinal))
            return CompatibilityResult.Reject("bridge_contract");

        if (build.ProtoVersion != required.ExpectedProtoVersion)
            return CompatibilityResult.Reject("proto_version");

        if (build.ResVersion < 0 || build.ResVersion < required.RequiredResVersion)
            return CompatibilityResult.Reject("res_version");

        if (build.ConfigVersion < 0 || build.ConfigVersion < required.RequiredConfigVersion)
            return CompatibilityResult.Reject("config_version");

        if (!TryParseApp(required.MinAppVersion, out AppVer min))
            throw new InvalidOperationException("server MinAppVersion invalid");
        if (!TryParseApp(build.AppVersion, out AppVer client) || client.CompareTo(min) < 0)
            return CompatibilityResult.Reject("app_version");

        return CompatibilityResult.Ok();
    }

    /// <summary>供 ServerRequirements 校验版本文本；策略唯一解析入口。</summary>
    public static bool TryParseAppVersion(string? text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string[] parts = text.Split('.');
        if (parts.Length != 3)
            return false;
        if (!TryPart(parts[0], out int major) || !TryPart(parts[1], out int minor) || !TryPart(parts[2], out int patch))
            return false;
        version = (major, minor, patch);
        return true;
    }

    static bool TryParseApp(string? text, out AppVer version)
    {
        version = default;
        if (!TryParseAppVersion(text, out var t))
            return false;
        version = new AppVer(t.Major, t.Minor, t.Patch);
        return true;
    }

    static bool TryPart(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;

    readonly struct AppVer
    {
        public AppVer(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        public int CompareTo(AppVer other)
        {
            int c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            return Patch.CompareTo(other.Patch);
        }
    }
}
