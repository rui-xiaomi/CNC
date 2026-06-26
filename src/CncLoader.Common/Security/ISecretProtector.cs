namespace CncLoader.Common.Security;

/// <summary>
/// 敏感信息加解密。用于 DB / AGV / 扫码枪口令的密文存取，密钥不入代码、明文不入日志。
/// </summary>
public interface ISecretProtector
{
    /// <summary>明文 → 密文（Base64）。</summary>
    string Protect(string plainText);

    /// <summary>密文（Base64）→ 明文。失败抛 <see cref="System.FormatException"/> 或加密异常。</summary>
    string Unprotect(string cipherText);

    /// <summary>尝试解密，失败返回 false（用于兼容开发期明文）。</summary>
    bool TryUnprotect(string cipherText, out string plainText);
}
