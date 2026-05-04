using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using SignalTracker.Helper;
using Microsoft.Extensions.Configuration;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [Route("Home")]
    public class HomeController : Controller
    {
        private const string LegacyGlobalLoginLockKey = "auth:global-login-lock";
        private const string UserLoginLockKeyPrefix = "auth:login-lock:user:";
        private const int UserLoginLockTtlSeconds = 18000;

        private readonly ApplicationDbContext _db;
        private readonly CommonFunction? _cf = null;
        private readonly ILogger<HomeController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly LicenseFeatureService _licenseFeatureService;
        private readonly RedisService _redis;

        public HomeController(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            ILogger<HomeController> logger,
            IConfiguration configuration,
            LicenseFeatureService licenseFeatureService,
            RedisService redis)
        {
            _db = context;
            _cf = new CommonFunction(context, httpContextAccessor);
            _logger = logger;
            _configuration = configuration;
            _httpContextAccessor = httpContextAccessor;
            _licenseFeatureService = licenseFeatureService;
            _redis = redis;
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
                  .Where(u => u.email.ToLower() == emailNormalized && u.isactive == 1)
                  .Select(u => new UserLite
                  {
                      id = u.id,
                      name = u.name,
                      email = u.email,
                      m_user_type_id = u.m_user_type_id,
                      password = u.password,
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
            optionsBuilder.UseMySql(twConnectionString, new MySqlServerVersion(new Version(8, 0, 29)));

            using var twDb = new ApplicationDbContext(optionsBuilder.Options);

            return await twDb.tbl_user
                .AsNoTracking()
                .Where(u => u.email.ToLower() == emailNormalized && u.isactive == 1)
                .Select(u => new UserLite
                {
                    id = u.id,
                    name = u.name,
                    email = u.email,
                    m_user_type_id = u.m_user_type_id,
                    password = u.password,
                    company_id = u.company_id,
                    country_code = u.country_code
                })
                .SingleOrDefaultAsync();
        }

        [HttpPost("UserLogin")]
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
                    if (twUser != null && twUser.password == obj.Password)
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

                    if (user != null && user.password == obj.Password)
                    {
                        var resolvedCountry = (user.country_code ?? string.Empty).Trim().ToUpperInvariant();
                        if (resolvedCountry == "TW")
                        {
                            var twUser = await GetUserForLoginTw(emailNormalized);
                            if (twUser != null && twUser.password == obj.Password)
                            {
                                user = twUser;
                                loginSource = "TW";
                            }
                        }
                    }
                    else
                    {
                        var twUser = await GetUserForLoginTw(emailNormalized);
                        if (twUser == null || twUser.password != obj.Password)
                            return Json(new { success = false, message = "Invalid email or password!" });

                        user = twUser;
                        loginSource = "TW";
                    }
                }

                if (_redis?.IsConnected != true)
                {
                    return Json(new { success = false, message = "Login service is temporarily unavailable. Please try again." });
                }

                var lockValue = $"{user!.id}:{user.email}:{DateTimeOffset.UtcNow:O}";
                userLockKey = BuildUserLoginLockKey(user.id);

                if (obj.ForceLogin == true)
                {
                    await _redis.DeleteAsync(userLockKey);
                    // Backward compatibility: clear old single global lock key as well.
                    await _redis.DeleteAsync(LegacyGlobalLoginLockKey);
                }

                lockAcquired = await _redis.TrySetStringAsync(userLockKey, lockValue, UserLoginLockTtlSeconds);
                if (!lockAcquired)
                {
                    return Json(new { success = false, message = "Sorry, someone is already logged in. Please try again later." });
                }

                var resolvedCountryCode = (string.IsNullOrWhiteSpace(user.country_code) ? loginSource : user.country_code).Trim().ToUpperInvariant();

                var enabledFeatures = await _licenseFeatureService.GetEnabledFeaturesForUserAsync(user.id);

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, user.email),
                    new Claim("UserId", user.id.ToString()),
                    new Claim("UserTypeId", user.m_user_type_id.ToString()),
                    new Claim("CompanyId", user.company_id?.ToString() ?? "0"),
                    new Claim("company_id", user.company_id?.ToString() ?? "0"),
                    new Claim("country_code", resolvedCountryCode)
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
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(300),
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
                        if (_redis?.IsConnected == true && !string.IsNullOrWhiteSpace(userLockKey))
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
                              .Where(a => a.email.ToLower() == emailNorm && a.isactive == 1 && a.m_user_type_id != 4)
                              .Select(a => new { a.id, a.name, a.email, a.uid })
                              .FirstOrDefault();

                if (user == null)
                {
                    message.Message = "You have entered wrong email id.";
                    return Json(message);
                }

                var uid = Guid.NewGuid().ToString();
                var unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var token = $"{uid}.{unixNow}";

                var trackedUser = _db.tbl_user.First(a => a.id == user.id);
                trackedUser.uid = uid;
                _db.Entry(trackedUser).State = EntityState.Modified;
                _db.SaveChanges();

                var mail = new SendMail(_db, _httpContextAccessor);
                string[] send_to = new[] { user.email };
                string[] bcc_to = Array.Empty<string>();

                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                if (baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                    send_to = new[] { "baghel3349@gmail.com" };

                var resetUrl = $"{baseUrl}/Home/ResetPassword?link={token}";
                string body = $"Dear {user.name},<br /><br />Please click the link below to reset your password:<br /><a href='{resetUrl}' title='Click here to reset password'>Reset Password</a>";

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
        public JsonResult ForgotResetPassword([FromBody] ResetPasswordModel model)
        {
            var ret = new ReturnMessage();
            try
            {
                var captchaOk = HttpContext.Session.GetString("CaptchaImageText") == model.Captcha;
                if (!captchaOk)
                {
                    ret.Status = 0;
                    ret.Message = "Invalid CAPTCHA Code!";
                    return Json(ret);
                }

                var parts = model.Token.Split('.', 2);
                var uid = parts[0];
                var tsStr = parts.Length > 1 ? parts[1] : "0";

                if (!long.TryParse(tsStr, out var sentAt) || (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt) > 15 * 60)
                {
                    ret.Status = 0;
                    ret.Message = "Invalid or expired reset link.";
                    return Json(ret);
                }

                var user = _db.tbl_user.FirstOrDefault(a => a.uid == uid);
                if (user == null)
                {
                    ret.Status = 0;
                    ret.Message = "Invalid user.";
                    return Json(ret);
                }

                user.password = model.NewPassword;
                user.uid = null;
                _db.SaveChanges();

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

        [HttpGet("Logout")]
        public async Task<IActionResult> Logout(string IP)
        {
            try
            {
                var username = HttpContext?.Session.GetString("UserName");
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

            }
            catch { /* Log error */ }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();

            return Ok(new { success = true, message = "Logged out successfully." });
        }

        private static string BuildUserLoginLockKey(int userId)
            => $"{UserLoginLockKeyPrefix}{userId}";

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
            return BadRequest(new { error = "An internal error occurred." });
        }
    }
}
