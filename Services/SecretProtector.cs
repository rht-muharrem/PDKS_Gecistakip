using System.Security.Cryptography;
using System.Text;

namespace ServerMonitor.Services;


public static class SecretProtector
{
    private const string Prefix = "enc:";

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return plain;


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
