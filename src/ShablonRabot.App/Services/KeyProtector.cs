using System.Security.Cryptography;
using System.Text;

namespace ShablonRabot.App.Services;

/// <summary>Шифрование API-ключа средствами Windows (DPAPI): файл настроек с другого компьютера или пользователя ключ не раскроет.</summary>
public static class KeyProtector
{
    static readonly byte[] Entropy = "Chistovik.Ai.Key"u8.ToArray();

    public static string Protect(string key) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}
