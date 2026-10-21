using System.Security.Cryptography;
using System.Text;

namespace ServerMonitor.Services;

/// <summary>
/// WMI parolalarini servers.json icinde duz metin tutmamak icin Windows DPAPI ile sifreler.
/// Sifreleme uygulamayi calistiran KULLANICIYA baglidir: uygulamayi baska bir hesapla
/// calistirirsan parolalar cozulemez ve panelden yeniden girilmeleri gerekir.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:";

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return plain;

        // Zaten sifreliyse tekrar sifreleme.
        if (plain.StartsWith(Prefix, StringComparison.Ordinal))
            return plain;

        if (!OperatingSystem.IsWindows())
            return plain;

        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(bytes);
        }
        catch (CryptographicException)
        {
            return plain;
        }
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        // Prefix yoksa elle yazilmis duz metin paroladir.
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
