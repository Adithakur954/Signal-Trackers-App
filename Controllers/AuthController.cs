using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;

using SignalTracker.Models; 
using SignalTracker.Security;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private const string LegacyGlobalLoginLockKey = "auth:global-login-lock";
        private const string UserLoginLockKeyPrefix = "auth:login-lock:user:";
        private const int UserLoginLockTtlSeconds = 18000;

        private readonly ApplicationDbContext _db;
        private readonly ILogger<AuthController> _logger;
        private readonly IConfiguration _configuration;
        private readonly LicenseFeatureService _licenseFeatureService;
        private readonly RedisService _redis;

        public AuthController(
            ApplicationDbContext db,
            ILogger<AuthController> logger,
            IConfiguration configuration,
            LicenseFeatureService licenseFeatureService,
            RedisService redis)
        {
            _db = db;
            _logger = logger;
            _configuration = configuration;
            _licenseFeatureService = licenseFeatureService;
            _redis = redis;
        }

        private sealed class LoginUserDto
        {
            public int id { get; set; }
            public string email { get; set; } = string.Empty;
            public string? name { get; set; }
            public string password { get; set; } = string.Empty;
            public int m_user_type_id { get; set; }
            public string? country_code { get; set; }
            public int? company_id { get; set; }
        }

        private async Task<LoginUserDto?> FindTwUserAsync(string emailNormalized)
        {
            var twConnectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(_configuration.GetConnectionString("MySqlConnection2"));
            if (string.IsNullOrWhiteSpace(twConnectionString))
                return null;

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder.UseMySql(twConnectionString, new MySqlServerVersion(new Version(8, 0, 29)), mysqlOptions =>
            {
                mysqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });

            using var twDb = new ApplicationDbContext(optionsBuilder.Options);

            return await twDb.tbl_user
                .AsNoTracking()
                .Where(u => u.email != null && u.email.ToLower() == emailNormalized && u.isactive == 1)
                .Select(u => new LoginUserDto
                {
                    id = u.id,
                    email = u.email,
                    name = u.name,
                    password = u.password,
                    m_user_type_id = u.m_user_type_id,
                    country_code = u.country_code,
                    company_id = u.company_id
                })
                .FirstOrDefaultAsync();
        }

        private async Task UpgradePasswordHashIfNeededAsync(LoginUserDto user, string sourceDb, string submittedPassword)
        {
            if (!PasswordSecurity.NeedsUpgrade(user.password)) return;

            try
            {
                var upgradedHash = PasswordSecurity.HashPassword(submittedPassword);
                if (string.Equals(sourceDb, "TW", StringComparison.OrdinalIgnoreCase))
                {
                    var twConnectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(_configuration.GetConnectionString("MySqlConnection2"));
                    if (string.IsNullOrWhiteSpace(twConnectionString)) return;

                    var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                    optionsBuilder.UseMySql(twConnectionString, new MySqlServerVersion(new Version(8, 0, 29)), mysqlOptions =>
                    {
                        mysqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
                    });

                    await using var twDb = new ApplicationDbContext(optionsBuilder.Options);
                    var trackedUser = await twDb.tbl_user.FirstOrDefaultAsync(u => u.id == user.id);
                    if (trackedUser == null) return;

                    trackedUser.password = upgradedHash;
                    await twDb.SaveChangesAsync();
                }
                else
                {
                    var trackedUser = await _db.tbl_user.FirstOrDefaultAsync(u => u.id == user.id);
                    if (trackedUser == null) return;

                    trackedUser.password = upgradedHash;
                    await _db.SaveChangesAsync();
                }

                user.password = upgradedHash;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Password hash upgrade failed for user {UserId} in {SourceDb}", user.id, sourceDb);
            }
        }

        private async Task<List<string>> GetEnabledFeaturesSafeAsync(int userId, CancellationToken ct = default)
        {
            try
            {
                return await _licenseFeatureService.GetEnabledFeaturesForUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load enabled features for user {UserId}", userId);
                return new List<string>();
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Password))
            {
                return Unauthorized(new { message = "Invalid email or password" });
            }

            var emailNormalized = model.Email.Trim().ToLowerInvariant();
            var requestedCountry = (model.country_code ?? string.Empty).Trim().ToUpperInvariant();
            bool preferTw = requestedCountry == "TW";

            LoginUserDto? user = null;
            var loginSource = "IN";

            if (preferTw)
            {
                var twUser = await FindTwUserAsync(emailNormalized);
                if (twUser != null && PasswordSecurity.VerifyPassword(model.Password, twUser.password, allowPlainTextFallback: true))
                {
                    user = twUser;
                    loginSource = "TW";
                }
            }
            else
            {
                // Default authentication on main DB; fallback to TW
                user = await _db.tbl_user
                    .AsNoTracking()
                    .Where(u => u.email != null && u.email.ToLower() == emailNormalized && u.isactive == 1)
                    .Select(u => new LoginUserDto
                    {
                        id = u.id,
                        email = u.email,
                        name = u.name,
                        password = u.password,
                        m_user_type_id = u.m_user_type_id,
                        country_code = u.country_code,
                        company_id = u.company_id
                    })
                    .FirstOrDefaultAsync();

                if (user != null && PasswordSecurity.VerifyPassword(model.Password, user.password, allowPlainTextFallback: true))
                {
                    var resolvedCountry = (user.country_code ?? string.Empty).Trim().ToUpperInvariant();
                    if (resolvedCountry == "TW")
                    {
                        var twUser = await FindTwUserAsync(emailNormalized);
                        if (twUser != null && PasswordSecurity.VerifyPassword(model.Password, twUser.password, allowPlainTextFallback: true))
                        {
                            user = twUser;
                            loginSource = "TW";
                        }
                    }
                }
                else
                {
                    var twUser = await FindTwUserAsync(emailNormalized);
                    if (twUser != null && PasswordSecurity.VerifyPassword(model.Password, twUser.password, allowPlainTextFallback: true))
                    {
                        user = twUser;
                        loginSource = "TW";
                    }
                    else
                    {
                        user = null;
                    }
                }
            }

            if (user == null)
            {
                return Unauthorized(new { message = "Invalid email or password" });
            }

            var lockValue = $"{user.id}:{user.email}:{DateTimeOffset.UtcNow:O}";
            var userLockKey = BuildUserLoginLockKey(user.id);
            var loginLockAcquired = false;
            if (_redis?.IsConnected == true)
            {
                if (model.ForceLogin == true)
                {
                    // Backward compatibility: clear old single global lock key as well.
                    await _redis.DeleteAsync(LegacyGlobalLoginLockKey);
                    var forcedLockAcquired = await _redis.SetStringAsync(userLockKey, lockValue, UserLoginLockTtlSeconds);
                    if (!forcedLockAcquired)
                    {
                        if (_configuration.GetValue<bool>("Security:RequireRedisLoginLock"))
                        {
                            return StatusCode(503, new { message = "Login service is temporarily unavailable. Please try again." });
                        }

                        _logger.LogWarning("Redis force login lock unavailable for {Email}; allowing login because RequireRedisLoginLock is false.", user.email);
                    }
                    else
                    {
                        loginLockAcquired = true;
                    }
                }
                else
                {
                    var lockResult = await _redis.TrySetStringWhenNotExistsAsync(userLockKey, lockValue, UserLoginLockTtlSeconds);
                    if (lockResult == RedisSetWhenNotExistsResult.AlreadyExists)
                    {
                        return Unauthorized(new { message = "Sorry, someone is already logged in. Please try again later." });
                    }
                    if (lockResult == RedisSetWhenNotExistsResult.Unavailable)
                    {
                        if (_configuration.GetValue<bool>("Security:RequireRedisLoginLock"))
                        {
                            return StatusCode(503, new { message = "Login service is temporarily unavailable. Please try again." });
                        }

                        _logger.LogWarning("Redis login lock unavailable for {Email}; allowing login because RequireRedisLoginLock is false.", user.email);
                    }
                    else
                    {
                        loginLockAcquired = true;
                    }
                }
            }
            else if (_configuration.GetValue<bool>("Security:RequireRedisLoginLock"))
            {
                return StatusCode(503, new { message = "Login service is temporarily unavailable. Please try again." });
            }

            var resolvedCountryCode = (string.IsNullOrWhiteSpace(user.country_code) ? loginSource : user.country_code).Trim().ToUpperInvariant();
            var loginCompleted = false;
            await UpgradePasswordHashIfNeededAsync(user, loginSource, model.Password);

            // 2. Create claims. CRITICAL: Include 'country_code' so the provider knows which DB to use next.
            try
            {
                var enabledFeatures = await GetEnabledFeaturesSafeAsync(user.id);

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Email, user.email),
                    new Claim(ClaimTypes.Name, user.name ?? ""),
                    new Claim("country_code", resolvedCountryCode), // This drives the dynamic switch
                    new Claim("m_user_type_id", user.m_user_type_id.ToString()),
                    new Claim("UserId", user.id.ToString()),
                    new Claim("UserTypeId", user.m_user_type_id.ToString()),
                    new Claim("CompanyId", user.company_id?.ToString() ?? "0"),
                    new Claim("company_id", user.company_id?.ToString() ?? "0")
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                // 3. Sign in the user with the claims
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                HttpContext.Session.Clear();

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    new AuthenticationProperties { IsPersistent = true });

                HttpContext.Session.SetString("country_code", resolvedCountryCode);
                HttpContext.Session.SetString("UserName", user.email ?? string.Empty);
                HttpContext.Session.SetInt32("UserID", user.id);
                HttpContext.Session.SetInt32("UserType", user.m_user_type_id);
                HttpContext.Session.SetInt32("CompanyId", user.company_id ?? 0);
                loginCompleted = true;

                return Ok(new
                {
                    message = "Login successful",
                    country = resolvedCountryCode,
                    source_db = resolvedCountryCode,
                    user = new
                    {
                        user.id,
                        user.email,
                        user.name,
                        user.m_user_type_id,
                        user.company_id,
                        user.country_code,
                        enabled_features = enabledFeatures
                    }
                });
            }
            catch (Exception ex)
            {
                if (loginLockAcquired && !loginCompleted)
                {
                    try
                    {
                        await _redis.DeleteAsync(userLockKey);
                    }
                    catch { }
                }

                _logger.LogError(ex, "Error during login for {Email}", model.Email);
                return StatusCode(500, new { message = "An error occurred. Please try again." });
            }
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            try
            {
                if (_redis?.IsConnected == true)
                {
                    var claimUserId = User?.FindFirst("UserId")?.Value;
                    var sessionUserId = HttpContext?.Session.GetInt32("UserID")?.ToString();
                    var userIdValue = !string.IsNullOrWhiteSpace(claimUserId) ? claimUserId : sessionUserId;

                    if (int.TryParse(userIdValue, out var parsedUserId) && parsedUserId > 0)
                    {
                        await _redis.DeleteAsync(BuildUserLoginLockKey(parsedUserId));
                    }

                    // Backward compatibility: clear old single global lock key.
                    await _redis.DeleteAsync(LegacyGlobalLoginLockKey);
                }

                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                HttpContext.Session.Clear();

                return Ok(new { success = true, message = "Logged out successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during logout");
                return StatusCode(500, new { success = false, message = "An error occurred. Please try again." });
            }
        }

        private static string BuildUserLoginLockKey(int userId)
            => $"{UserLoginLockKeyPrefix}{userId}";

        [AllowAnonymous]
        [HttpGet("status")]
        public async Task<ActionResult<AuthStatusResponse>> GetAuthStatus(CancellationToken ct)
        {
            if (User?.Identity?.IsAuthenticated != true)
            {
                return Ok(new AuthStatusResponse
                {
                    authenticated = false,
                    user = null
                });
            }

            var email = GetEmailFromClaims(User);
            if (string.IsNullOrWhiteSpace(email))
            {
                return Ok(new AuthStatusResponse
                {
                    authenticated = false,
                    user = null
                });
            }

            UserSummaryDto? user;
            try
            {
                // This query will now automatically run against the user's specific database
                user = await _db.tbl_user
                    .AsNoTracking()
                    .Where(u => u.email == email)
                    .Select(u => new UserSummaryDto
                    {
                        id = u.id,
                        name = u.name,
                        email = u.email,
                        m_user_type_id = u.m_user_type_id,
                        country_code = u.country_code, // Added to DTO
                        company_id = u.company_id
                    })
                    .FirstOrDefaultAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auth status check failed for {Email}", email);
                return StatusCode(503, new
                {
                    authenticated = false,
                    user = (object?)null,
                    message = "Database is busy. Please try again shortly."
                });
            }

            if (user is null)
            {
                return Ok(new AuthStatusResponse
                {
                    authenticated = false,
                    user = null
                });
            }

            user.enabled_features = await GetEnabledFeaturesSafeAsync(user.id, ct);

            return Ok(new AuthStatusResponse
            {
                authenticated = true,
                user = user
            });
        }

        private static string? GetEmailFromClaims(ClaimsPrincipal user)
        {
            var candidates = new[] { ClaimTypes.Email, "email", ClaimTypes.Name };
            foreach (var type in candidates)
            {
                var value = user.FindFirst(type)?.Value;
                if (!string.IsNullOrWhiteSpace(value) && value.Contains('@')) return value;
            }
            return null;
        }
    }

    public class LoginRequest 
    {
        public string Email { get; set; } = default!;
        public string Password { get; set; } = default!;
        public string? country_code { get; set; }
        public bool? ForceLogin { get; set; }
    }

    public sealed class AuthStatusResponse
    {
        public bool authenticated { get; set; }
        public UserSummaryDto? user { get; set; }
    }

    public sealed class UserSummaryDto
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string email { get; set; } = default!;
        public int m_user_type_id { get; set; }
        public string? country_code { get; set; } // Added this field
        public int? company_id { get; set; }
        public List<string> enabled_features { get; set; } = new();
    }
}

