using System.Security.Cryptography;

namespace CloudIsle.Server.Account;

/// <summary>
/// 服务端密码派生。只接受固定的 PBKDF2-HMAC-SHA256 参数，不保存密码原文。
/// </summary>
public sealed class PasswordHasher
{
    /// <summary>注册时生成并传入的盐长度。更长或更短的盐都不参与派生。</summary>
    public const int SaltLength = 16;

    /// <summary>唯一接受的迭代次数。库存格式里的其他数字直接拒绝。</summary>
    public const int IterationCount = 600_000;

    /// <summary>派生结果的字节数。库存格式里的其他长度直接拒绝。</summary>
    public const int OutputLength = 32;

    const int MaxPasswordUtf8Bytes = 4096;
    const int MaxHashTextLength = 80;
    const string Algorithm = "pbkdf2-sha256";
    const string Version = "1";

    /// <summary>用给定盐做一次 PBKDF2，并返回带算法版本和参数的文本。</summary>
    /// <param name="password">密码原文。只在本次派生中使用，不写入日志。</param>
    /// <param name="salt">16 字节随机盐。盐本身不放进返回值。</param>
    /// <returns>形如 pbkdf2-sha256$1$600000$32$ 再接派生结果 Base64 的文本。</returns>
    public string Hash(string password, byte[] salt)
    {
        if (password == null)
            throw new ArgumentNullException(nameof(password));
        if (salt == null)
            throw new ArgumentNullException(nameof(salt));
        if (salt.Length != SaltLength)
            throw new ArgumentException("盐必须是 16 字节", nameof(salt));
        if (Utf8Length(password) > MaxPasswordUtf8Bytes)
            throw new ArgumentException("密码超出派生输入上限", nameof(password));

        byte[] derived = Derive(password, salt);
        return string.Concat(Prefix(), Convert.ToBase64String(derived));
    }

    /// <summary>按库存格式重新派生并比较。格式损坏、算法未知或参数不受支持时返回 false。</summary>
    /// <param name="password">待核对的密码原文。</param>
    /// <param name="salt">注册时保存的 16 字节盐。</param>
    /// <param name="expectedHash">先前 Hash 返回并保存的文本。</param>
    /// <returns>格式有效且派生结果一致时为 true，否则为 false。</returns>
    public bool Verify(string password, byte[] salt, string expectedHash)
    {
        if (password == null || salt == null || salt.Length != SaltLength)
            return false;
        if (Utf8Length(password) > MaxPasswordUtf8Bytes)
            return false;
        if (!TryReadDerived(expectedHash, out byte[] expected))
            return false;

        byte[] actual = Derive(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>拼出当前算法固定前缀，后面只再接派生结果。</summary>
    static string Prefix()
    {
        return string.Concat(
            Algorithm, "$",
            Version, "$",
            IterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "$",
            OutputLength.ToString(System.Globalization.CultureInfo.InvariantCulture), "$");
    }

    /// <summary>只接受当前这一组算法、版本、迭代和输出长度。不按文本里的数字执行派生。</summary>
    /// <param name="expectedHash">库存的派生文本。</param>
    /// <param name="derived">解析出的 32 字节结果。</param>
    static bool TryReadDerived(string expectedHash, out byte[] derived)
    {
        derived = Array.Empty<byte>();
        if (string.IsNullOrEmpty(expectedHash) || expectedHash.Length > MaxHashTextLength)
            return false;

        string prefix = Prefix();
        if (!expectedHash.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string encoded = expectedHash.Substring(prefix.Length);
        byte[] buffer = new byte[OutputLength];
        if (!Convert.TryFromBase64String(encoded, buffer, out int written) || written != OutputLength)
            return false;

        derived = buffer;
        return true;
    }

    /// <summary>按固定迭代和输出长度派生。调用前已经限制了密码和盐的大小。</summary>
    /// <param name="password">密码原文。</param>
    /// <param name="salt">16 字节盐。</param>
    static byte[] Derive(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            IterationCount,
            HashAlgorithmName.SHA256,
            OutputLength);
    }

    /// <summary>计算 UTF-8 字节数，用来拦住过长输入。</summary>
    /// <param name="text">密码原文。</param>
    static int Utf8Length(string text)
    {
        return System.Text.Encoding.UTF8.GetByteCount(text);
    }
}
