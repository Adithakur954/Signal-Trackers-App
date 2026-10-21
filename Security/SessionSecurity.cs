using System.Globalization;
using System.Diagnostics;
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
    public static bool KeepSignedIn(IConfiguration configuration) =>
        configuration.GetValue("Security:KeepSignedInUntilLogout", true);
    public static TimeSpan CookieLifetime(IConfiguration configuration) => KeepSignedIn(configuration)
        ? TimeSpan.FromDays(365) : TimeSpan.FromSeconds(IdleSeconds(configuration));
    public static int IdleSeconds(IConfiguration configuration) =>
        KeepSignedIn(configuration) ? 365 * 24 * 60 * 60 :
        Math.Clamp(configuration.GetValue("Security:SessionIdleMinutes", 360), 5, 720) * 60;
    public static string LockKey(int userId, string? region) =>
        $"auth:login-lock:v2:{RegionAccess.Normalize(region) ?? throw new InvalidOperationException("A trusted region is required.")}:{userId}";
    public static string CredentialVersion(string? passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? string.Empty)));

    public sealed class SessionAccountSnapshot
    {
        public tbl_user User { get; set; } = null!;
        public bool HasPortalLicense { get; set; }
    }

    public static IQueryable<SessionAccountSnapshot> AccountQuery(ApplicationDbContext db, int userId, DateTime today)
    {
        return db.tbl_user.AsNoTracking().Where(user => user.id == userId)
            .Select(user => new SessionAccountSnapshot
            {
                User = user,
                HasPortalLicense = user.m_user_type_id == UserScopeService.ROLE_SUPER_ADMIN
                    || (user.country_code != null && new[] { "TW", "TWN", "TAIWAN" }.Contains(user.country_code.Trim().ToUpper()))
                    || db.tbl_company_user_license_issued.Any(license => license.tbl_user_id == user.id
                        && license.status == 1 && license.valid_till.Date >= today)
            });
    }

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
        var authenticationWatch = Stopwatch.StartNew();
        var principal = context.Principal;
        try
        {
            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var keepSignedIn = KeepSignedIn(configuration);
            var userId = principal == null ? 0 : ResourceAccess.UserId(principal);
            var region = RegionAccess.Normalize(principal?.FindFirst("country_code")?.Value);
            var lockValue = principal?.FindFirst("LoginLockValue")?.Value;
            if (userId == 0 || region == null || string.IsNullOrWhiteSpace(lockValue)
                || !WithinAbsoluteLifetime(context.Properties, keepSignedIn ? TimeSpan.MaxValue : absoluteLifetime, DateTimeOffset.UtcNow))
            {
                await RejectAsync(context, "missing-or-expired-ticket");
                return;
            }

            var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(
                configuration.GetConnectionString(region == "TW" ? "MySqlConnection2" : "MySqlConnection"));
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new SessionValidationUnavailableException();
            }
            // Authentication runs before HttpContext.User is assigned: use the ticket's
            // trusted region directly, never a request-selected scoped DbContext.
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29))).Options;
            await using var db = new ApplicationDbContext(options);
            var account = await AccountQuery(db, userId, DateTime.UtcNow.Date)
                .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
            var user = account?.User;
            if (!IsCurrent(principal!, user))
            {
                await RejectAsync(context, "stale-user-or-credential");
                return;
            }

            // Keep active sessions tied to the authoritative license state.
            // Disabling/revoking an issued license should invalidate the cookie too.
            if (user == null || account?.HasPortalLicense != true)
            {
                await RejectAsync(context, "inactive-or-expired-license");
                return;
            }

            var dbLocks = context.HttpContext.RequestServices.GetService<LoginLockFallbackService>();
            var key = LockKey(userId, region);
            await ValidateLoginSessionAsync(context, dbLocks, key, lockValue!, idleSeconds, keepSignedIn);
            if (context.Principal != null)
            {
                // IIS may recycle its in-memory session while the protected auth cookie remains valid.
                var session = context.HttpContext.Session;
                session.SetString("country_code", region);
                session.SetString("UserName", user.email ?? string.Empty);
                session.SetInt32("UserID", user.id);
                session.SetInt32("UserType", user.m_user_type_id);
                session.SetInt32("CompanyId", user.company_id ?? 0);
                if (keepSignedIn) RenewPersistentTicket(context);
            }
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Closing a tab/modal can abort in-flight API requests. Do not turn that
            // cancelled request into a logout for the still-valid browser session.
            return;
        }
        catch (SessionValidationUnavailableException) { throw; }
        catch (Exception ex)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SessionSecurity").LogWarning(ex, "Session validation temporarily unavailable; cookie retained.");
            throw new SessionValidationUnavailableException(ex);
        }
        finally
        {
            context.HttpContext.Items["st.authentication.ms"] = authenticationWatch.Elapsed.TotalMilliseconds;
        }
    }

    public static async Task ValidateLoginSessionAsync(CookieValidatePrincipalContext context, ILoginSessionStore? store,
        string key, string lockValue, int ttlSeconds, bool keepSignedIn)
    {
        var state = store == null ? LoginSessionState.Unavailable :
            await store.ValidateAndRenewAsync(key, lockValue, ttlSeconds, keepSignedIn, context.HttpContext.RequestAborted);
        switch (state)
        {
            case LoginSessionState.Current: return;
            case LoginSessionState.Missing:
                await RejectAsync(context, "db-login-lock-missing"); return;
            case LoginSessionState.Replaced:
                await RejectAsync(context, "login-lock-mismatch"); return;
            default: throw new SessionValidationUnavailableException();
        }
    }

    public static void RenewPersistentTicket(CookieValidatePrincipalContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (!context.Properties.ExpiresUtc.HasValue || context.Properties.ExpiresUtc < now.AddDays(364))
        {
            context.Properties.IssuedUtc = now;
            context.Properties.ExpiresUtc = now.AddDays(365);
            context.Properties.AllowRefresh = true;
            context.Properties.IsPersistent = true;
            context.ShouldRenew = true;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, string reason = "session-invalid")
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.HttpContext.Session.Clear();
        context.HttpContext.Response.Headers["X-Session-Invalidated"] = reason;
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SessionSecurity").LogInformation("Session rejected: {Reason}", reason);
    }
}
