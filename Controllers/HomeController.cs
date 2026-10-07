using Microsoft.AspNetCore.DataProtection;
using System.Text.Encodings.Web;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using System.Security.Claims;
using SignalTracker.Helper;
using Microsoft.Extensions.Configuration;
using SignalTracker.Security;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [Route("Home")]
    public class HomeController : Controller
    {
        private const string LegacyGlobalLoginLockKey = "auth:global-login-lock";
        private int UserLoginLockTtlSeconds => SessionSecurity.IdleSeconds(_configuration);
        private bool RequireRedisLoginLock => !HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment()
            || _configuration.GetValue<bool>("Security:RequireRedisLoginLock");

        private readonly ApplicationDbContext _db;
        private readonly CommonFunction? _cf = null;
        private readonly ILogger<HomeController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly LicenseFeatureService _licenseFeatureService;
        private readonly RedisService _redis;
        private readonly LoginLockFallbackService _loginLocks;

        public HomeController(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            ILogger<HomeController> logger,
            IConfiguration configuration,
            LicenseFeatureService licenseFeatureService,
            RedisService redis,
            LoginLockFallbackService loginLocks)
        {
            _db = context;
            _cf = new CommonFunction(context, httpContextAccessor);
            _logger = logger;
            _configuration = configuration;
            _httpContextAccessor = httpContextAccessor;
            _licenseFeatureService = licenseFeatureService;
            _redis = redis;
            _loginLocks = loginLocks;
        }

        [HttpGet("")]
        [HttpGet("Index")]
        public IActionResult Index()
        {
            if (_cf?.SessionCheck() == true)
                return Ok(new { success = true, authenticated = true, redirectTo = "Admin/Index" });
                
            return Ok(new { success = true, message = "SignalTracker API is running." });
        }

        #region Account

        // Fix: Explicit route for Login GET
        [HttpGet("Login")]
        public ActionResult Login() 
        {
            return Ok(new { message = "Please use POST /Home/UserLogin to authenticate." });
        }

        [HttpPost("GetStateIformation")]
        public JsonResult GetStateIformation()
        {
            const string src = "abcdefghijklmnopqrstuvwxyz0123456789";
            int length = 12;
            var sb = new StringBuilder(length);
            var rng = new Random();
            for (var i = 0; i < length; i++) sb.Append(src[rng.Next(src.Length)]);
            HttpContext.Session.SetString("salt", sb.ToString());
            return Json(sb.ToString());
        }

        private sealed class UserLite
        {
            public int id { get; set; }
            public string name { get; set; } = "";
            public string email { get; set; } = "";
            public int m_user_type_id { get; set; }
            public string password { get; set; } = "";
            public int? company_id { get; set; }
            public string? country_code { get; set; }
        }

        private static readonly Func<ApplicationDbContext, string, Task<UserLite?>> GetUserForLogin =
            EF.CompileAsyncQuery((ApplicationDbContext db, string emailNormalized) =>
                db.tbl_user
                  .AsNoTracking()
                  .Where(u => u.email != null && u.email.ToLower() == emailNormalized && u.isactive == 1)
                  .Select(u => new UserLite
                  {
                      id = u.id,
                      name = u.name,
                      email = u.email ?? string.Empty,
                      m_user_type_id = u.m_user_type_id,
                      password = u.password ?? string.Empty,
                      company_id = u.company_id,
                      country_code = u.country_code
                  })
                  .SingleOrDefault()
            );

        private async Task<UserLite?> GetUserForLoginTw(string emailNormalized)
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
                .Select(u => new UserLite
                {
                    id = u.id,
                    name = u.name,
                    email = u.email ?? string.Empty,
                    m_user_type_id = u.m_user_type_id,
                    password = u.password ?? string.Empty,
                    company_id = u.company_id,
                    country_code = u.country_code
                })
                .SingleOrDefaultAsync();
        }

        private async Task UpgradePasswordHashIfNeededAsync(UserLite user, string sourceDb, string submittedPassword)
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

        private static bool IsSuperAdminLoginUser(UserLite user)
        {
            return user.m_user_type_id == UserScopeService.ROLE_SUPER_ADMIN
                || string.Equals(RegionAccess.Normalize(user.country_code), "TW", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> HasActivePortalLicenseAsync(UserLite user, string sourceDb, CancellationToken ct = default)
        {
            if (IsSuperAdminLoginUser(user))
                return true;

            if (user.id <= 0)
                return false;

            var sourceConnectionName = string.Equals(sourceDb, "TW", StringComparison.OrdinalIgnoreCase)
                ? "MySqlConnection2"
                : "MySqlConnection";
            var alternateConnectionName = string.Equals(sourceConnectionName, "MySqlConnection2", StringComparison.OrdinalIgnoreCase)
                ? "MySqlConnection"
                : "MySqlConnection2";

            if (await HasActivePortalLicenseInConnectionAsync(sourceConnectionName, user.id, ct))
                return true;

            if (await HasActivePortalLicenseInConnectionAsync(alternateConnectionName, user.id, ct))
            {
                _logger.LogWarning(
                    "Portal license for user {UserId} was found in fallback DB {FallbackConnectionName}, not source DB {SourceConnectionName}.",
                    user.id, alternateConnectionName, sourceConnectionName);
                return true;
            }

            var latest = await GetLatestPortalLicenseSnapshotAsync(sourceConnectionName, user.id, ct)
                ?? await GetLatestPortalLicenseSnapshotAsync(alternateConnectionName, user.id, ct);
            _logger.LogWarning(
                "Portal license check failed for user {UserId}, email {Email}, source {SourceDb}. Latest license: {LicenseSummary}",
                user.id,
                user.email,
                sourceDb,
                latest == null
                    ? "none"
                    : $"id={latest.Id}, db={latest.ConnectionName}, status={latest.Status}, valid_till={latest.ValidTill:O}");

            return false;
        }

        private static Task<bool> HasActivePortalLicenseInDbAsync(ApplicationDbContext db, int userId, CancellationToken ct)
        {
            var today = DateTime.UtcNow.Date;
            return db.tbl_company_user_license_issued
                .AsNoTracking()
                .AnyAsync(lic => lic.tbl_user_id == userId
                    && lic.status == 1
                    && lic.valid_till.Date >= today, ct);
        }

        private async Task<bool> HasActivePortalLicenseInConnectionAsync(string connectionName, int userId, CancellationToken ct)
        {
            var db = CreateDbContext(connectionName);
            if (db == null)
                return false;

            await using (db)
            {
                return await HasActivePortalLicenseInDbAsync(db, userId, ct);
            }
        }

        private async Task<LicenseSnapshot?> GetLatestPortalLicenseSnapshotAsync(string connectionName, int userId, CancellationToken ct)
        {
            var db = CreateDbContext(connectionName);
            if (db == null)
                return null;

            await using (db)
            {
                return await db.tbl_company_user_license_issued
                    .AsNoTracking()
                    .Where(lic => lic.tbl_user_id == userId)
                    .OrderByDescending(lic => lic.valid_till)
                    .ThenByDescending(lic => lic.id)
                    .Select(lic => new LicenseSnapshot(connectionName, lic.id, lic.status, lic.valid_till))
                    .FirstOrDefaultAsync(ct);
            }
        }

        private ApplicationDbContext? CreateDbContext(string connectionName)
        {
            var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(_configuration.GetConnectionString(connectionName));
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29)), mysqlOptions =>
            {
                mysqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });
            return new ApplicationDbContext(optionsBuilder.Options);
        }

        private sealed record LicenseSnapshot(string ConnectionName, int Id, int Status, DateTime ValidTill);

        [HttpPost("UserLogin")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Auth")]
        public async Task<JsonResult> UserLogin([FromBody] LoginData obj)
        {
            var sw = Stopwatch.StartNew();
            bool lockAcquired = false;
            bool loginCompleted = false;
            string? userLockKey = null;
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(obj.Email) || string.IsNullOrWhiteSpace(obj.Password))
                    return Json(new { success = false, message = "Email and password are required." });

                var emailNormalized = obj.Email.Trim().ToLowerInvariant();

                var requestedCountry = (obj.country_code ?? string.Empty).Trim().ToUpperInvariant();
                bool preferTw = requestedCountry == "TW";

                UserLite? user = null;
                var loginSource = "IN";

                if (preferTw)
                {
                    var twUser = await GetUserForLoginTw(emailNormalized);
                    if (twUser != null && PasswordSecurity.VerifyPassword(obj.Password, twUser.password, allowPlainTextFallback: true))
                    {
                        user = twUser;
                        loginSource = "TW";
                    }
                    else
                    {
                        return Json(new { success = false, message = "Invalid TW email or password." });
                    }
                }
                else
                {
                    user = await GetUserForLogin(_db, emailNormalized);
                    var dbMs = sw.ElapsedMilliseconds;

                    if (user != null && PasswordSecurity.VerifyPassword(obj.Password, user.password, allowPlainTextFallback: true))
                    {
                        var resolvedCountry = (user.country_code ?? string.Empty).Trim().ToUpperInvariant();
                        if (resolvedCountry == "TW")
                        {
                            var twUser = await GetUserForLoginTw(emailNormalized);
                            if (twUser != null && PasswordSecurity.VerifyPassword(obj.Password, twUser.password, allowPlainTextFallback: true))
                            {
                                user = twUser;
                                loginSource = "TW";
                            }
                        }
                    }
                    else
                    {
                        var twUser = await GetUserForLoginTw(emailNormalized);
                        if (twUser == null || !PasswordSecurity.VerifyPassword(obj.Password, twUser.password, allowPlainTextFallback: true))
                            return Json(new { success = false, message = "Invalid email or password!" });

                        user = twUser;
                        loginSource = "TW";
                    }
                }

                if (!await HasActivePortalLicenseAsync(user!, loginSource, HttpContext.RequestAborted))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Your portal license is disabled or expired. Please contact your administrator.",
                        license_disabled = true
                    });
                }

                var lockValue = $"{user!.id}:{user.email}:{DateTimeOffset.UtcNow:O}";
                userLockKey = SessionSecurity.LockKey(user.id, loginSource);

                if (obj.ForceLogin == true)
                {
                    // Backward compatibility: clear old single global lock key as well.
                    await _redis.DeleteAsync(LegacyGlobalLoginLockKey);
                    await _redis.DeleteAsync(userLockKey);
                    await _loginLocks.DeleteAsync(userLockKey, HttpContext.RequestAborted);

                    var redisSet = _redis.IsConnected && await _redis.SetStringAsync(userLockKey, lockValue, UserLoginLockTtlSeconds);
                    var dbSet = await _loginLocks.SetStringAsync(userLockKey, lockValue, UserLoginLockTtlSeconds, HttpContext.RequestAborted);
                    lockAcquired = redisSet || dbSet;
                    if (!lockAcquired)
                    {
                        return Json(new { success = false, message = "Login service is temporarily unavailable. Please try again." });
                    }
                }
                else
                {
                    RedisSetWhenNotExistsResult lockResult;
                    string? existingLockValue = null;

                    if (_redis.IsConnected)
                    {
                        lockResult = await _redis.TrySetStringWhenNotExistsAsync(userLockKey, lockValue, UserLoginLockTtlSeconds);
                        if (lockResult == RedisSetWhenNotExistsResult.Set)
                            await _loginLocks.SetStringAsync(userLockKey, lockValue, UserLoginLockTtlSeconds, HttpContext.RequestAborted);
                        else if (lockResult == RedisSetWhenNotExistsResult.AlreadyExists)
                            existingLockValue = await _redis.GetStringAsync(userLockKey);
                    }
                    else
                    {
                        lockResult = RedisSetWhenNotExistsResult.Unavailable;
                    }

                    if (lockResult == RedisSetWhenNotExistsResult.Unavailable)
                    {
                        lockResult = await _loginLocks.TrySetStringWhenNotExistsAsync(userLockKey, lockValue, UserLoginLockTtlSeconds, HttpContext.RequestAborted);
                        if (lockResult == RedisSetWhenNotExistsResult.AlreadyExists)
                            existingLockValue = await _loginLocks.GetStringAsync(userLockKey, HttpContext.RequestAborted);
                    }

                    if (lockResult == RedisSetWhenNotExistsResult.AlreadyExists)
                    {
                        var activeLogin = ParseLoginLockValue(existingLockValue);
                        return Json(new
                        {
                            success = false,
                            message = "Sorry, someone is already logged in. Please logout from old devices.",
                            already_logged_in = true,
                            can_force_logout = true,
                            active_login = activeLogin
                        });
                    }

                    if (lockResult == RedisSetWhenNotExistsResult.Unavailable)
                    {
                        return Json(new { success = false, message = "Login service is temporarily unavailable. Please try again." });
                    }

                    lockAcquired = true;
                }
                var resolvedCountryCode = RegionAccess.Normalize(loginSource)!;
                await UpgradePasswordHashIfNeededAsync(user, loginSource, obj.Password);

                var enabledFeatures = await GetEnabledFeaturesSafeAsync(user.id);

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Email, user.email),
                    new Claim(ClaimTypes.Name, user.email),
                    new Claim("UserId", user.id.ToString()),
                    new Claim("UserTypeId", user.m_user_type_id.ToString()),
                    new Claim("CompanyId", user.company_id?.ToString() ?? "0"),
                    new Claim("company_id", user.company_id?.ToString() ?? "0"),
                    new Claim("country_code", resolvedCountryCode),
                    new Claim("CredentialVersion", SessionSecurity.CredentialVersion(user.password)),
                    new Claim("LoginLockValue", lockValue)
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                HttpContext.Session.Clear();

                // Using await to prevent thread blocking
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        AllowRefresh = true,
                    });

                var companyIdValue = user.company_id?.ToString() ?? "0";
                _logger.LogInformation("Creating cookie for User: {Email} with CompanyId: {CompanyId}", user.email, companyIdValue);
                
                HttpContext.Session.SetString("UserName", user.email);
                HttpContext.Session.SetInt32("UserID", user.id);
                HttpContext.Session.SetInt32("UserType", user.m_user_type_id);
                HttpContext.Session.SetInt32("CompanyId", user.company_id ?? 0);
                HttpContext.Session.SetString("country_code", resolvedCountryCode);
                loginCompleted = true;

                return Json(new
                {
                    success = true,
                    user = new
                    {
                        user.id,
                        user.name,
                        email = user.email,
                        user.m_user_type_id,
                        user.company_id,
                        user.country_code,
                        enabled_features = enabledFeatures
                    },
                    source_db = resolvedCountryCode,
                    message = "Login successful!"
                });
            }
            catch (Exception ex)
            {
                if (lockAcquired && !loginCompleted)
                {
                    try
                    {
                        if (_redis.IsConnected && !string.IsNullOrWhiteSpace(userLockKey))
                            await _redis.DeleteAsync(userLockKey);
                    }
                    catch { }
                }

                _logger.LogError(ex, "Error during Home.UserLogin for {Email}", obj?.Email);

                var writelog = new Writelog(_db);
                writelog.write_exception_log(0, "Home", "UserLogin", DateTime.Now, ex);
                return Json(new { success = false, message = "An error occurred. Please try again." });
            }
        }

        [HttpPost("GetUserForgotPassword")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("PasswordRecovery")]
        public JsonResult GetUserForgotPassword([FromBody] LoginData obj)
        {
            var message = new ReturnMessage { Status = 0, Message = DisplayMessage.ErrorMessage };

            try
            {
                var captchaOk = HttpContext.Session.GetString("CaptchaImageText") != null
                                && HttpContext.Session.GetString("CaptchaImageText") == obj.Captcha;

                if (!captchaOk)
                {
                    message.Message = "Invalid CAPTCHA Code !";
                    return Json(message);
                }

                var emailNorm = (obj.Email ?? string.Empty).Trim().ToLowerInvariant();

                var user = _db.tbl_user
                              .AsNoTracking()
                              .Where(a => a.email != null && a.email.ToLower() == emailNorm && a.isactive == 1 && a.m_user_type_id != 4)
                              .Select(a => new { a.id, a.name, a.email, a.uid })
                              .FirstOrDefault();

                if (user == null || string.IsNullOrWhiteSpace(user.email))
                {
                    message.Message = "You have entered wrong email id.";
                    return Json(message);
                }

                var uid = Guid.NewGuid().ToString();
                var token = PasswordResetTokens.Issue(HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>(), uid);

                var trackedUser = _db.tbl_user.First(a => a.id == user.id);
                trackedUser.uid = uid;
                _db.Entry(trackedUser).State = EntityState.Modified;
                _db.SaveChanges();

                var mail = new SendMail(_db, _httpContextAccessor);
                string[] send_to = new[] { user.email };
                string[] bcc_to = Array.Empty<string>();

                var baseUrl = _configuration["Security:PasswordResetBaseUrl"];
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var resetBase)
                    || resetBase.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(resetBase.UserInfo))
                {
                    message.Message = "Password recovery is temporarily unavailable.";
                    return Json(message);
                }
                var resetUrl = $"{resetBase.AbsoluteUri.TrimEnd('/')}/Home/ResetPassword?link={Uri.EscapeDataString(token)}";
                string body = $"Dear {HtmlEncoder.Default.Encode(user.name ?? string.Empty)},<br /><br />Please click the link below to reset your password:<br /><a href='{HtmlEncoder.Default.Encode(resetUrl)}' title='Click here to reset password'>Reset Password</a>";

                string subject = "Forecast - Forgot password";
                bool sent = mail.send_mail(body, send_to, bcc_to, subject, null, "");
                if (sent)
                {
                    message.Status = 1;
                    message.Message = "Reset password link has been sent on your email id and valid for 15 minutes only.";
                }
                else
                {
                    message.Message = "Email is not send, kindly contact admin.";
                }
            }
            catch (Exception ex)
            {
                Writelog writelog = new Writelog(_db);
                writelog.write_exception_log(0, "HomeController", "GetUserForgotPassword", DateTime.Now, ex);
                message.Status = 0;
                message.Message = "An unexpected server error occurred.";
            }
            return Json(message);
        }

        [HttpGet("ResetPassword")]
        public ActionResult ResetPassword(string link, string Email)
        {
            // If this is an API, return status instead of View()
            return Ok(new { message = "Redirect to your frontend reset page with token: " + link });
        }

        [HttpPost("ForgotResetPassword")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("PasswordRecovery")]
        public JsonResult ForgotResetPassword([FromBody] ResetPasswordModel model)
        {
            var ret = new ReturnMessage();
            try
            {
                var captchaOk = !string.IsNullOrWhiteSpace(model.Captcha)
                    && !string.IsNullOrWhiteSpace(HttpContext.Session.GetString("CaptchaImageText"))
                    && HttpContext.Session.GetString("CaptchaImageText") == model.Captcha;
                if (!captchaOk)
                {
                    ret.Status = 0;
                    ret.Message = "Invalid CAPTCHA Code!";
                    return Json(ret);
                }

                if (!PasswordResetTokens.TryRead(HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>(), model.Token, out var uid))
                {
                    ret.Status = 0;
                    ret.Message = "Invalid or expired reset link.";
                    return Json(ret);
                }
                var passwordHash = PasswordSecurity.HashPassword(model.NewPassword);
                // Consume the recovery identifier atomically; concurrent replays cannot both succeed.
                var updated = _db.tbl_user.Where(user => user.uid == uid && user.isactive == 1)
                    .ExecuteUpdate(setters => setters.SetProperty(user => user.password, passwordHash)
                        .SetProperty(user => user.uid, (string?)null));
                if (updated != 1)
                {
                    ret.Status = 0;
                    ret.Message = "Invalid or expired reset link.";
                    return Json(ret);
                }

                ret.Status = 1;
                ret.Message = "Password has been reset successfully.";
            }
            catch (Exception ex)
            {
                Writelog writelog = new Writelog(_db);
                writelog.write_exception_log(0, "HomeController", "ForgotResetPassword", DateTime.Now, ex);
                ret.Status = 0;
                ret.Message = "Error resetting password.";
            }
            return Json(ret);
        }

        [Authorize]
        [HttpGet("KeepAlive")]
        [HttpPost("KeepAlive")]
        public async Task<IActionResult> KeepAlive()
        {
            if (_cf?.SessionCheck() != true)
            {
                return Unauthorized(new { success = false, message = "Session is not active." });
            }

            HttpContext.Session.SetString("st.keepalive", DateTimeOffset.UtcNow.ToString("O"));

            if (_redis.IsConnected)
            {
                var claimUserId = User?.FindFirst("UserId")?.Value;
                var region = User?.FindFirst("country_code")?.Value;
                if (int.TryParse(claimUserId, out var parsedUserId) && parsedUserId > 0 && !string.IsNullOrWhiteSpace(region))
                {
                    try
                    {
                        var keepAliveKey = SessionSecurity.LockKey(parsedUserId, region);
                        await _redis.ExtendTtlAsync(keepAliveKey, UserLoginLockTtlSeconds);
                        await _loginLocks.ExtendTtlAsync(keepAliveKey, UserLoginLockTtlSeconds, HttpContext.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "KeepAlive could not refresh Redis login lock TTL.");
                    }
                }
            }

            return Ok(new
            {
                success = true,
                authenticated = true,
                utc = DateTimeOffset.UtcNow
            });
        }
        [HttpGet("Logout")]
        public async Task<IActionResult> Logout(string IP)
        {
            try
            {
                var username = HttpContext.Session.GetString("UserName");
                if (!string.IsNullOrEmpty(username))
                {
                    var objAudit = new tbl_user_login_audit_details
                    {
                        date_of_creation = DateTime.Now,
                        ip_address = IP,
                        username = username,
                        login_status = 2
                    };
                    _db.tbl_user_login_audit_details.Add(objAudit);
                    await _db.SaveChangesAsync();
                }

                if (_redis.IsConnected)
                {
                    var claimUserId = User?.FindFirst("UserId")?.Value;
                    var sessionUserId = HttpContext.Session.GetInt32("UserID")?.ToString();
                    var userIdValue = !string.IsNullOrWhiteSpace(claimUserId) ? claimUserId : sessionUserId;

                    if (int.TryParse(userIdValue, out var parsedUserId) && parsedUserId > 0)
                    {
                        var logoutLockKey = SessionSecurity.LockKey(parsedUserId, User?.FindFirst("country_code")?.Value);
                        await _redis.DeleteAsync(logoutLockKey);
                        await _loginLocks.DeleteAsync(logoutLockKey, HttpContext.RequestAborted);
                    }

                    // Backward compatibility: clear old single global lock key.
                    await _redis.DeleteAsync(LegacyGlobalLoginLockKey);
                }

            }
            catch { /* Log error */ }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();

            return Ok(new { success = true, message = "Logged out successfully." });
        }


        private static object? ParseLoginLockValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var parts = value.Split('|');
            if (parts.Length >= 6)
            {
                return new
                {
                    user_id = int.TryParse(parts[0], out var parsedUserId) ? parsedUserId : 0,
                    email = DecodeBase64(parts[1]),
                    login_time_utc = parts[2],
                    ip_address = DecodeBase64(parts[3]),
                    user_agent = DecodeBase64(parts[4]),
                    device = DecodeBase64(parts[5])
                };
            }

            var legacyParts = value.Split(':');
            return new
            {
                user_id = legacyParts.Length > 0 && int.TryParse(legacyParts[0], out var userId) ? userId : 0,
                email = legacyParts.Length > 1 ? legacyParts[1] : string.Empty,
                login_time_utc = legacyParts.Length > 2 ? string.Join(":", legacyParts.Skip(2)) : string.Empty,
                ip_address = string.Empty,
                user_agent = string.Empty,
                device = "Unknown device"
            };
        }

        private static string DecodeBase64(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion

        [HttpPost("GetLoggedUser")]
        public JsonResult GetLoggedUser(string? ip = null)
        {
            bool isAuth = User?.Identity?.IsAuthenticated == true || (_cf?.SessionCheck() ?? false);
            if (!isAuth) return Json(new { });

            var email = User?.FindFirstValue(ClaimTypes.Name) ?? HttpContext.Session.GetString("UserName") ?? string.Empty;
            var userId = User?.FindFirstValue("UserId") ?? HttpContext.Session.GetInt32("UserID")?.ToString();

            return Json(new
            {
                id = userId,
                name = email,
                email = email,
                m_user_type_id = HttpContext.Session.GetInt32("UserType")
            });
        }

        [HttpGet("Error")]
        public IActionResult Error()
        {
            var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            var traceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

            if (exceptionFeature?.Error != null)
            {
                _logger.LogError(
                    exceptionFeature.Error,
                    "Unhandled exception at {Path}. TraceId: {TraceId}",
                    exceptionFeature.Path,
                    traceId);
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "An internal error occurred.",
                traceId
            });
        }
    }
}

