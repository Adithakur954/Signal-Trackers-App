using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace SignalTracker.Security;

public static class PasswordResetTokens
{
    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector("SignalTracker.PasswordReset.v2").ToTimeLimitedDataProtector();
    public static string Issue(IDataProtectionProvider provider, string uid) =>
        Protector(provider).Protect(uid, TimeSpan.FromMinutes(15));
    public static bool TryRead(IDataProtectionProvider provider, string? token, out string uid)
    {
        uid = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            uid = Protector(provider).Unprotect(token);
            return Guid.TryParse(uid, out _);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }
}
