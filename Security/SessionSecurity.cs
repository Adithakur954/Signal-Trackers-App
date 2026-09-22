using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;
using SignalTracker.Services;

namespace SignalTracker.Security;

public static class SessionSecurity
{
    public const string StartedProperty = "st.sessionStarted";
    public static int IdleSeconds(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue("Security:SessionIdleMinutes", 30), 5, 120) * 60;
    public static string LockKey(int userId, string? region) =>
        $"auth:login-lock:v2:{RegionAccess.Normalize(region) ?? throw new InvalidOperationException("A trusted region is required.")}:{userId}";
    public static string CredentialVersion(string? passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? string.Empty)));

    public static bool IsCurrent(System.Security.Claims.ClaimsPrincipal principal, tbl_user? user)
    {
        return user != null && user.isactive == 1 && ResourceAccess.UserId(principal) == user.id
            && principal.FindFirst("UserTypeId")?.Value == user.m_user_type_id.ToString(CultureInfo.InvariantCulture)
            && principal.FindFirst("CompanyId")?.Value == (user.company_id ?? 0).ToString(CultureInfo.InvariantCulture)
            && !string.IsNullOrEmpty(user.password)
            && string.Equals(principal.FindFirst("CredentialVersion")?.Value, CredentialVersion(user.password), StringComparison.Ordinal);
    }

    public static bool WithinAbsoluteLifetime(AuthenticationProperties properties, TimeSpan lifetime, DateTimeOffset now) =>
        properties.Items.TryGetValue(StartedProperty, out var raw)
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
        && started <= now && now - started < lifetime;

    public static async Task ValidateAsync(CookieValidatePrincipalContext context, bool requireRedis, TimeSpan absoluteLifetime, int idleSeconds)
    {
        var principal = context.Principal;
        try
        {
            var userId = principal == null ? 0 : ResourceAccess.UserId(principal);
            var region = RegionAccess.Normalize(principal?.FindFirst("country_code")?.Value);
            var lockValue = principal?.FindFirst("LoginLockValue")?.Value;
            if (userId == 0 || region == null || string.IsNullOrWhiteSpace(lockValue)
                || !WithinAbsoluteLifetime(context.Properties, absoluteLifetime, DateTimeOffset.UtcNow))
            {
                await RejectAsync(context);
                return;
            }

            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(
                configuration.GetConnectionString(region == "TW" ? "MySqlConnection2" : "MySqlConnection"));
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                await RejectAsync(context);
                return;
            }
            // Authentication runs before HttpContext.User is assigned: use the ticket's
            // trusted region directly, never a request-selected scoped DbContext.
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29))).Options;
            await using var db = new ApplicationDbContext(options);
            var user = await db.tbl_user.AsNoTracking().FirstOrDefaultAsync(row => row.id == userId, context.HttpContext.RequestAborted);
            if (!IsCurrent(principal!, user))
            {
                await RejectAsync(context);
                return;
            }

            var redis = context.HttpContext.RequestServices.GetService<RedisService>();
            if (redis?.IsConnected != true)
            {
                if (requireRedis) await RejectAsync(context);
                return;
            }
            var key = LockKey(userId, region);
            var currentLock = await redis.GetStringAsync(key);
            if (string.IsNullOrWhiteSpace(currentLock) || !string.Equals(currentLock, lockValue, StringComparison.Ordinal))
            {
                await RejectAsync(context);
                return;
            }
            if (!await redis.ExtendTtlAsync(key, idleSeconds) && requireRedis)
                await RejectAsync(context);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.RejectPrincipal();
        }
        catch (Exception)
        {
            // Do not accept a stale principal when the authoritative state cannot be checked.
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SessionSecurity").LogWarning("Session validation failed; authentication rejected.");
            await RejectAsync(context);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.HttpContext.Session.Clear();
        context.HttpContext.Response.Headers["X-Session-Invalidated"] = "session-invalid";
    }
}
