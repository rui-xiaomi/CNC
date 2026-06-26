using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CncLoader.Common.Security;

/// <summary>
/// 基于 Windows DPAPI（CurrentUser 作用域）的加解密实现。零密钥管理，密文绑定当前用户。
/// 适用于单机/工位机部署。非 Windows 环境会抛 <see cref="PlatformNotSupportedException"/>。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    // 附加熵：与应用绑定，提升密文专属性（非密钥，可入代码）。
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CncLoader.Secret.v1");

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        var data = Encoding.UTF8.GetBytes(plainText);
        var cipher = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public string Unprotect(string cipherText)
    {
        ArgumentNullException.ThrowIfNull(cipherText);
        var cipher = Convert.FromBase64String(cipherText);
        var data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    public bool TryUnprotect(string cipherText, out string plainText)
    {
        try
        {
            plainText = Unprotect(cipherText);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            plainText = string.Empty;
            return false;
        }
    }
}
