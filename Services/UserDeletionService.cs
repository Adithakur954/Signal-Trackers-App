using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services
{
    public sealed class DeletionPreviewResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public object? Preview { get; init; }
    }

    public sealed class DeletionRequestResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public interface IUserDeletionService
    {
        Task<DeletionPreviewResult> GetPreviewAsync(string deletionToken, HttpContext httpContext, CancellationToken ct = default);
        Task<DeletionRequestResult> RequestDeletionAsync(string deletionToken, bool confirmed, HttpContext httpContext, CancellationToken ct = default);
    }

    public sealed class UserDeletionService : IUserDeletionService
    {
        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _configuration;

        public UserDeletionService(ApplicationDbContext db, IConfiguration configuration)
        {
            _db = db;
            _configuration = configuration;
        }

        public async Task<DeletionPreviewResult> GetPreviewAsync(string deletionToken, HttpContext httpContext, CancellationToken ct = default)
        {
            await using var lease = await ValidateTokenAsync(deletionToken, ct);
            var db = lease.Db;
            var token = lease.Token;
            if (token == null)
                return new DeletionPreviewResult { Message = "Invalid or expired deletion token" };

            var user = await db.tbl_user.AsNoTracking().FirstOrDefaultAsync(u => u.id == token.user_id, ct);
            if (user == null)
                return new DeletionPreviewResult { Message = "User not found" };

            if (user.is_deleted)
                return new DeletionPreviewResult { Message = "Account already scheduled for deletion" };

            var sessionIds = await db.tbl_session
                .AsNoTracking()
                .Where(s => s.user_id == user.id && s.id != null)
                .Select(s => s.id!.Value)
                .ToListAsync(ct);

            var projectIds = await db.tbl_project
                .AsNoTracking()
                .Where(p => p.created_by_user_id == user.id)
                .Select(p => p.id)
                .ToListAsync(ct);

            var preview = new
            {
                user = new
                {
                    user.id,
                    user.name,
                    mobile = MaskPhone(user.mobile),
                    email = MaskEmail(user.email),
                    user.deletion_requested_at
                },
                data_summary = new
                {
                    sessions = sessionIds.Count,
                    network_logs = await db.tbl_network_log.CountAsync(l => l.session_id != null && sessionIds.Contains(l.session_id.Value), ct),
                    network_log_neighbours = await db.tbl_network_log_neighbour.CountAsync(n => sessionIds.Contains(n.session_id), ct),
                    projects = projectIds.Count,
                    map_regions = await db.map_regions.CountAsync(m => m.tbl_project_id != null && projectIds.Contains(m.tbl_project_id.Value), ct),
                    prediction_data = await db.tbl_prediction_data.CountAsync(p => p.tbl_project_id != null && projectIds.Contains(p.tbl_project_id.Value), ct),
                    site_predictions = await db.site_prediction.CountAsync(p => projectIds.Contains(p.tbl_project_id), ct),
                    optimized_site_predictions = await db.site_prediction_optimized.CountAsync(p => projectIds.Contains(p.tbl_project_id), ct),
                    upload_history = await db.tbl_upload_history.CountAsync(u => u.uploaded_by == user.id, ct),
                    thresholds = await db.thresholds.CountAsync(t => t.user_id == user.id, ct),
                    issued_licenses = await db.tbl_company_user_license_issued.CountAsync(l => l.tbl_user_id == user.id, ct)
                }
            };

            await AuditAsync(db, user.id, token.phone_number, "data_preview", "success", "Deletion data preview viewed", httpContext, ct);

            return new DeletionPreviewResult
            {
                Success = true,
                Message = "Data preview fetched successfully",
                Preview = preview
            };
        }

        public async Task<DeletionRequestResult> RequestDeletionAsync(string deletionToken, bool confirmed, HttpContext httpContext, CancellationToken ct = default)
        {
            if (!confirmed)
                return new DeletionRequestResult { Message = "Deletion confirmation is required" };

            await using var lease = await ValidateTokenAsync(deletionToken, ct);
            var db = lease.Db;
            var token = lease.Token;
            if (token == null)
                return new DeletionRequestResult { Message = "Invalid or expired deletion token" };

            var user = await db.tbl_user.FirstOrDefaultAsync(u => u.id == token.user_id, ct);
            if (user == null)
                return new DeletionRequestResult { Message = "User not found" };

            if (user.is_deleted)
                return new DeletionRequestResult { Message = "Account already scheduled for deletion" };

            var now = DateTime.UtcNow;
            user.is_deleted = true;
            user.deletion_requested_at = now;
            user.isactive = 0;
            user.token = null;
            token.used_at = now;

            await AuditAsync(db, user.id, token.phone_number, "deletion_requested", "success", "Deletion scheduled for 59-day cleanup", httpContext, ct);
            await db.SaveChangesAsync(ct);

            return new DeletionRequestResult
            {
                Success = true,
                Message = "Your data deletion request has been submitted. Your data will be permanently deleted after 59 days."
            };
        }

        private async Task<DbLease> ValidateTokenAsync(string deletionToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(deletionToken)) return new DbLease(_db, false, null);

            var token = await FindTokenAsync(_db, deletionToken, ct);
            if (token != null) return new DbLease(_db, false, token);

            var twDb = CreateDbContext("MySqlConnection2");
            if (twDb == null) return new DbLease(_db, false, null);

            token = await FindTokenAsync(twDb, deletionToken, ct);
            return token != null
                ? new DbLease(twDb, true, token)
                : new DbLease(_db, false, null, twDb);
        }

        private static async Task<tbl_user_deletion_token?> FindTokenAsync(ApplicationDbContext db, string deletionToken, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var candidates = await db.tbl_user_deletion_token
                .Where(t => t.used_at == null && t.expires_at > now)
                .OrderByDescending(t => t.created_at)
                .Take(20)
                .ToListAsync(ct);

            return candidates.FirstOrDefault(t => BCrypt.Net.BCrypt.Verify(deletionToken, t.token_hash));
        }

        private ApplicationDbContext? CreateDbContext(string connectionName)
        {
            var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(_configuration.GetConnectionString(connectionName));
            if (string.IsNullOrWhiteSpace(connectionString)) return null;

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29)), mysqlOptions =>
                {
                    mysqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
                })
                .Options;

            return new ApplicationDbContext(options);
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

        private sealed class DbLease : IAsyncDisposable
        {
            private readonly bool _ownsDb;
            private readonly ApplicationDbContext? _unusedContextToDispose;

            public DbLease(ApplicationDbContext db, bool ownsDb, tbl_user_deletion_token? token, ApplicationDbContext? unusedContextToDispose = null)
            {
                Db = db;
                _ownsDb = ownsDb;
                Token = token;
                _unusedContextToDispose = unusedContextToDispose;
            }

            public ApplicationDbContext Db { get; }
            public tbl_user_deletion_token? Token { get; }

            public async ValueTask DisposeAsync()
            {
                if (_ownsDb)
                    await Db.DisposeAsync();

                if (_unusedContextToDispose != null)
                    await _unusedContextToDispose.DisposeAsync();
            }
        }

        private static string? MaskEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return email;
            var parts = email.Split('@', 2);
            var prefix = parts[0].Length <= 2 ? parts[0][0] + "*" : parts[0][..2] + "***";
            return prefix + "@" + parts[1];
        }

        private static string? MaskPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone) || phone.Length <= 4) return phone;
            return new string('*', Math.Max(0, phone.Length - 4)) + phone[^4..];
        }
    }
}


