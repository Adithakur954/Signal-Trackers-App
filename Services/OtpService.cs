using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services
{
    public sealed class SendDeletionOtpResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class VerifyDeletionOtpResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? DeletionToken { get; init; }
    }

    public interface IOtpService
    {
        Task<SendDeletionOtpResult> SendDeletionOtpAsync(string phoneNumber, HttpContext httpContext, CancellationToken ct = default);
        Task<VerifyDeletionOtpResult> VerifyDeletionOtpAsync(string phoneNumber, string otp, HttpContext httpContext, CancellationToken ct = default);
    }

    public sealed class OtpService : IOtpService
    {
        private readonly ApplicationDbContext _db;
        private readonly ISmsService _smsService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<OtpService> _logger;
        private readonly IWebHostEnvironment _environment;

        public OtpService(
            ApplicationDbContext db,
            ISmsService smsService,
            IConfiguration configuration,
            ILogger<OtpService> logger,
            IWebHostEnvironment environment)
        {
            _db = db;
            _smsService = smsService;
            _configuration = configuration;
            _logger = logger;
            _environment = environment;
        }

        public async Task<SendDeletionOtpResult> SendDeletionOtpAsync(string phoneNumber, HttpContext httpContext, CancellationToken ct = default)
        {
            var normalizedPhone = NormalizePhone(phoneNumber);
            if (string.IsNullOrWhiteSpace(normalizedPhone))
                return new SendDeletionOtpResult { Message = "Phone number is required" };

            await using var lease = await ResolveDbByPhoneAsync(normalizedPhone, ct);
            var db = lease.Db;
            var user = lease.User;

            if (user == null)
            {
                await AuditAsync(db, null, normalizedPhone, "otp_requested", "failed", "User not found", httpContext, ct);
                return new SendDeletionOtpResult { Message = "User not found" };
            }

            if (user.is_deleted)
            {
                await AuditAsync(db, user.id, normalizedPhone, "otp_requested", "blocked", "Account already scheduled for deletion", httpContext, ct);
                return new SendDeletionOtpResult { Message = "Account already scheduled for deletion" };
            }

            var now = DateTime.UtcNow;
            var latestOtp = await db.tbl_user_deletion_otp
                .Where(x => x.user_id == user.id)
                .OrderByDescending(x => x.created_at)
                .FirstOrDefaultAsync(ct);

            if (latestOtp != null && latestOtp.resend_available_at > now)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling((latestOtp.resend_available_at - now).TotalSeconds));
                await AuditAsync(db, user.id, normalizedPhone, "otp_requested", "rate_limited", $"Retry after {seconds} seconds", httpContext, ct);
                return new SendDeletionOtpResult { Message = $"Please wait {seconds} seconds before requesting another OTP" };
            }

            var otp = GenerateNumericOtp(GetInt("OTP_LENGTH", 6));
            var expiryMinutes = GetInt("OTP_EXPIRY_MINUTES", 10);
            var cooldownSeconds = GetInt("OTP_RESEND_COOLDOWN_SECONDS", 30);
            var maxAttempts = GetInt("OTP_MAX_ATTEMPTS", 5);

            db.tbl_user_deletion_otp.Add(new tbl_user_deletion_otp
            {
                user_id = user.id,
                phone_number = normalizedPhone,
                otp_hash = BCrypt.Net.BCrypt.HashPassword(otp),
                expires_at = now.AddMinutes(expiryMinutes),
                attempt_count = 0,
                max_attempts = maxAttempts,
                resend_available_at = now.AddSeconds(cooldownSeconds),
                created_at = now
            });

            await db.SaveChangesAsync(ct);

            try
            {
                await _smsService.SendOtpAsync(normalizedPhone, otp, ct);
                await AuditAsync(db, user.id, normalizedPhone, "otp_requested", "success", "OTP sent", httpContext, ct);
                return new SendDeletionOtpResult { Success = true, Message = "OTP sent successfully" };
            }
            catch (SmsDeliveryException ex)
            {
                _logger.LogError(ex, "Failed to send deletion OTP for user {UserId}. Provider response: {ProviderResponse}", user.id, ex.ProviderResponse);
                await AuditAsync(db, user.id, normalizedPhone, "otp_requested", "failed", "SMS delivery failed", httpContext, ct);

                var message = _environment.IsDevelopment() && !string.IsNullOrWhiteSpace(ex.ProviderResponse)
                    ? $"Unable to send OTP SMS. Provider response: {ex.ProviderResponse}"
                    : "Unable to send OTP SMS";

                return new SendDeletionOtpResult { Message = message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send deletion OTP for user {UserId}", user.id);
                await AuditAsync(db, user.id, normalizedPhone, "otp_requested", "failed", "SMS delivery failed", httpContext, ct);
                return new SendDeletionOtpResult { Message = "Unable to send OTP SMS" };
            }
        }

        public async Task<VerifyDeletionOtpResult> VerifyDeletionOtpAsync(string phoneNumber, string otp, HttpContext httpContext, CancellationToken ct = default)
        {
            var normalizedPhone = NormalizePhone(phoneNumber);
            if (string.IsNullOrWhiteSpace(normalizedPhone) || string.IsNullOrWhiteSpace(otp))
                return new VerifyDeletionOtpResult { Message = "Phone number and OTP are required" };

            var now = DateTime.UtcNow;
            await using var lease = await ResolveDbByActiveOtpAsync(normalizedPhone, ct);
            var db = lease.Db;
            var otpRecord = lease.Otp;

            if (otpRecord == null)
                return new VerifyDeletionOtpResult { Message = "Invalid OTP" };

            if (otpRecord.expires_at <= now)
            {
                await AuditAsync(db, otpRecord.user_id, normalizedPhone, "otp_verified", "expired", "OTP expired", httpContext, ct);
                return new VerifyDeletionOtpResult { Message = "OTP expired" };
            }

            if (otpRecord.attempt_count >= otpRecord.max_attempts)
            {
                otpRecord.blocked_at = now;
                await db.SaveChangesAsync(ct);
                await AuditAsync(db, otpRecord.user_id, normalizedPhone, "otp_verified", "blocked", "Max attempts exceeded", httpContext, ct);
                return new VerifyDeletionOtpResult { Message = "Max attempts exceeded" };
            }

            if (!BCrypt.Net.BCrypt.Verify(otp, otpRecord.otp_hash))
            {
                otpRecord.attempt_count += 1;
                if (otpRecord.attempt_count >= otpRecord.max_attempts)
                    otpRecord.blocked_at = now;

                await db.SaveChangesAsync(ct);
                await AuditAsync(db, otpRecord.user_id, normalizedPhone, "otp_verified", "failed", "Invalid OTP", httpContext, ct);
                return new VerifyDeletionOtpResult { Message = "Invalid OTP" };
            }

            var user = await db.tbl_user.FirstOrDefaultAsync(u => u.id == otpRecord.user_id, ct);
            if (user == null)
                return new VerifyDeletionOtpResult { Message = "User not found" };

            if (user.is_deleted)
                return new VerifyDeletionOtpResult { Message = "Account already scheduled for deletion" };

            var plainToken = GenerateToken();
            otpRecord.consumed_at = now;
            db.tbl_user_deletion_token.Add(new tbl_user_deletion_token
            {
                user_id = user.id,
                phone_number = normalizedPhone,
                token_hash = BCrypt.Net.BCrypt.HashPassword(plainToken),
                expires_at = now.AddMinutes(GetInt("OTP_VERIFICATION_TOKEN_TTL_MINUTES", 10)),
                created_at = now
            });

            await db.SaveChangesAsync(ct);
            await AuditAsync(db, user.id, normalizedPhone, "otp_verified", "success", "OTP verified", httpContext, ct);

            return new VerifyDeletionOtpResult
            {
                Success = true,
                Message = "OTP verified",
                DeletionToken = plainToken
            };
        }

        private async Task AuditAsync(ApplicationDbContext db, int? userId, string? phoneNumber, string eventType, string status, string? message, HttpContext httpContext, CancellationToken ct)
        {
            db.tbl_user_deletion_audit.Add(new tbl_user_deletion_audit
            {
                user_id = userId,
                phone_number = phoneNumber,
                event_type = eventType,
                event_status = status,
                ip_address = httpContext.Connection.RemoteIpAddress?.ToString(),
                user_agent = httpContext.Request.Headers.UserAgent.ToString(),
                message = message,
                created_at = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }

        private async Task<DbLease> ResolveDbByPhoneAsync(string normalizedPhone, CancellationToken ct)
        {
            var user = await _db.tbl_user.FirstOrDefaultAsync(u => u.mobile == normalizedPhone, ct);
            if (user != null) return new DbLease(_db, false, user, null);

            var twDb = CreateDbContext("MySqlConnection2");
            if (twDb == null) return new DbLease(_db, false, null, null);

            user = await twDb.tbl_user.FirstOrDefaultAsync(u => u.mobile == normalizedPhone, ct);
            return user != null
                ? new DbLease(twDb, true, user, null)
                : new DbLease(_db, false, null, null, twDb);
        }

        private async Task<DbLease> ResolveDbByActiveOtpAsync(string normalizedPhone, CancellationToken ct)
        {
            var otp = await FindActiveOtpAsync(_db, normalizedPhone, ct);
            if (otp != null) return new DbLease(_db, false, null, otp);

            var twDb = CreateDbContext("MySqlConnection2");
            if (twDb == null) return new DbLease(_db, false, null, null);

            otp = await FindActiveOtpAsync(twDb, normalizedPhone, ct);
            return otp != null
                ? new DbLease(twDb, true, null, otp)
                : new DbLease(_db, false, null, null, twDb);
        }

        private static Task<tbl_user_deletion_otp?> FindActiveOtpAsync(ApplicationDbContext db, string normalizedPhone, CancellationToken ct)
            => db.tbl_user_deletion_otp
                .Where(x => x.phone_number == normalizedPhone && x.consumed_at == null && x.blocked_at == null)
                .OrderByDescending(x => x.created_at)
                .FirstOrDefaultAsync(ct);

        private ApplicationDbContext? CreateDbContext(string connectionName)
        {
            var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(_configuration.GetConnectionString(connectionName));
            if (string.IsNullOrWhiteSpace(connectionString)) return null;

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29)))
                .Options;

            return new ApplicationDbContext(options);
        }

        private sealed class DbLease : IAsyncDisposable
        {
            private readonly ApplicationDbContext? _unusedContextToDispose;
            private readonly bool _ownsDb;

            public DbLease(ApplicationDbContext db, bool ownsDb, tbl_user? user, tbl_user_deletion_otp? otp, ApplicationDbContext? unusedContextToDispose = null)
            {
                Db = db;
                _ownsDb = ownsDb;
                User = user;
                Otp = otp;
                _unusedContextToDispose = unusedContextToDispose;
            }

            public ApplicationDbContext Db { get; }
            public tbl_user? User { get; }
            public tbl_user_deletion_otp? Otp { get; }

            public async ValueTask DisposeAsync()
            {
                if (_ownsDb)
                    await Db.DisposeAsync();

                if (_unusedContextToDispose != null)
                    await _unusedContextToDispose.DisposeAsync();
            }
        }

        internal static string NormalizePhone(string? phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber)) return string.Empty;
            var trimmed = phoneNumber.Trim();
            var leadingPlus = trimmed.StartsWith("+", StringComparison.Ordinal) ? "+" : string.Empty;
            var digits = Regex.Replace(trimmed, "[^0-9]", string.Empty);
            return leadingPlus + digits;
        }

        private int GetInt(string key, int fallback)
        {
            var value = _configuration[key] ?? Environment.GetEnvironmentVariable(key);
            return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        private static string GenerateNumericOtp(int length)
        {
            var min = (int)Math.Pow(10, length - 1);
            var max = (int)Math.Pow(10, length) - 1;
            return RandomNumberGenerator.GetInt32(min, max + 1).ToString();
        }

        private static string GenerateToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-", StringComparison.Ordinal)
                .Replace("/", "_", StringComparison.Ordinal)
                .TrimEnd('=');
        }
    }
}


