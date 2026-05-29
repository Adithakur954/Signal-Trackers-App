using System.Security.Cryptography;
using System.Text;

namespace SignalTracker.Security;

public static class PasswordSecurity
{
    public static string HashPassword(string password)
        => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

    public static bool VerifyPassword(string submittedPassword, string? storedPassword, bool allowPlainTextFallback = false)
    {
        if (string.IsNullOrWhiteSpace(storedPassword)) return false;

        if (IsBcryptHash(storedPassword))
        {
            if (BCrypt.Net.BCrypt.Verify(submittedPassword, storedPassword))
            {
                return true;
            }

            // Legacy frontend versions sent SHA-256(password), and the backend
            // then bcrypt-hashed that value during user/company creation.
            if (allowPlainTextFallback && !IsSha256Hex(submittedPassword))
            {
                return BCrypt.Net.BCrypt.Verify(Sha256Hex(submittedPassword), storedPassword);
            }

            return false;
        }

        var storedPasswordTrimmed = storedPassword.Trim();

        if (IsSha256Hex(storedPasswordTrimmed))
        {
            if (allowPlainTextFallback
                && string.Equals(submittedPassword, storedPasswordTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(Sha256Hex(submittedPassword), storedPasswordTrimmed, StringComparison.OrdinalIgnoreCase);
        }

        if (!allowPlainTextFallback) return false;

        if (string.Equals(submittedPassword, storedPassword, StringComparison.Ordinal)
            || string.Equals(submittedPassword, storedPasswordTrimmed, StringComparison.Ordinal))
        {
            return true;
        }

        if (TryDecodeBase64(storedPasswordTrimmed, out var decodedPassword)
            && string.Equals(submittedPassword, decodedPassword, StringComparison.Ordinal))
        {
            return true;
        }

        var decryptedPassword = TryDecryptLegacyAes(storedPasswordTrimmed);
        return !string.IsNullOrEmpty(decryptedPassword)
            && string.Equals(submittedPassword, decryptedPassword, StringComparison.Ordinal);
    }

    public static bool NeedsUpgrade(string? storedPassword)
        => !string.IsNullOrWhiteSpace(storedPassword) && !IsBcryptHash(storedPassword);

    private static bool IsBcryptHash(string value)
        => value.StartsWith("$2a$", StringComparison.Ordinal)
        || value.StartsWith("$2b$", StringComparison.Ordinal)
        || value.StartsWith("$2y$", StringComparison.Ordinal);

    private static bool IsSha256Hex(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = string.Empty;

        if (value.Length == 0 || value.Length % 4 != 0) return false;

        try
        {
            var bytes = Convert.FromBase64String(value);
            decoded = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrEmpty(decoded);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string TryDecryptLegacyAes(string value)
    {
        try
        {
            return SignalTracker.AESEncrytDecry.Decrypt(value);
        }
        catch
        {
            return string.Empty;
        }
    }
}


