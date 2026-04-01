using System.Security.Cryptography;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// DPAPI-based credential encryption tied to the current Windows user.
/// Passwords are encrypted with DataProtectionScope.CurrentUser — only
/// the same Windows account on the same machine can decrypt them.
/// </summary>
public static class CredentialManager
{
    public static string Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string base64Encrypted)
    {
        var encrypted = Convert.FromBase64String(base64Encrypted);
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
