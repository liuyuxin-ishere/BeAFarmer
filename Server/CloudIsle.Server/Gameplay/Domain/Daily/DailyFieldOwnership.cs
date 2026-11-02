namespace CloudIsle.Domain.Daily;

/// <summary>
/// 日切声明字段的唯一比较规则。短名与 assets.* 为同一字段；assets 为资产根。
/// 当前聚合的集合按整个模块声明，子路径声明不能授权覆盖整个集合。
/// </summary>
internal static class DailyFieldOwnership
{
    internal static bool Covers(string owner, string path)
    {
        string canonicalOwner = Canonical(owner);
        string canonicalPath = Canonical(path);
        return canonicalOwner.Length == 0
               || string.Equals(canonicalOwner, canonicalPath, StringComparison.Ordinal)
               || canonicalPath.StartsWith(canonicalOwner + ".", StringComparison.Ordinal);
    }

    internal static bool Overlaps(string first, string second) => Covers(first, second) || Covers(second, first);

    static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("daily field path required", nameof(path));
        if (path == "assets") return "";
        if (path.StartsWith("assets.", StringComparison.Ordinal)) path = path.Substring(7);
        if (path.Length == 0 || path.StartsWith(".", StringComparison.Ordinal)
            || path.EndsWith(".", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("invalid daily field path", nameof(path));
        return path == "order.list" ? "orders" : path;
    }
}
