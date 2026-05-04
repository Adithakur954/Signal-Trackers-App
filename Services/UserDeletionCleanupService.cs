using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services
{
    public sealed class UserDeletionCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<UserDeletionCleanupService> _logger;

        public UserDeletionCleanupService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<UserDeletionCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCleanupAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "User deletion cleanup failed");
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }

        private async Task RunCleanupAsync(CancellationToken ct)
        {
            var connectionNames = new[] { "MySqlConnection", "MySqlConnection2" };

            foreach (var connectionName in connectionNames)
            {
                var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(_configuration.GetConnectionString(connectionName));
                if (string.IsNullOrWhiteSpace(connectionString))
                    continue;

                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29)), mysqlOptions =>
                    {
                        mysqlOptions.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                })
                    .Options;

                await using var db = new ApplicationDbContext(options);
                db.Database.SetCommandTimeout(300);

                var dueUserIds = await db.tbl_user
                    .AsNoTracking()
                    .Where(u => u.is_deleted && u.deletion_requested_at != null && u.deletion_requested_at <= DateTime.UtcNow.AddDays(-59))
                    .OrderBy(u => u.deletion_requested_at)
                    .ThenBy(u => u.id)
                    .Select(u => u.id)
                    .Take(100)
                    .ToListAsync(ct);

                foreach (var userId in dueUserIds)
                {
                    try
                    {
                        await HardDeleteUserAsync(db, userId, ct);
                        _logger.LogInformation("Permanently deleted user {UserId} from {ConnectionName}", userId, connectionName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed permanent deletion for user {UserId} from {ConnectionName}", userId, connectionName);
                    }
                }
            }
        }

        public static async Task HardDeleteUserAsync(ApplicationDbContext db, int userId, CancellationToken ct = default)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE n
                FROM tbl_network_log_neighbour n
                INNER JOIN tbl_session s ON s.id = n.session_id
                WHERE s.user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE l
                FROM tbl_network_log l
                INNER JOIN tbl_session s ON s.id = l.session_id
                WHERE s.user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE spo
                FROM site_prediction_optimized spo
                INNER JOIN tbl_project p ON p.id = spo.tbl_project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE sp
                FROM site_prediction sp
                INNER JOIN tbl_project p ON p.id = sp.tbl_project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE pd
                FROM tbl_prediction_data pd
                INNER JOIN tbl_project p ON p.id = pd.tbl_project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE r
                FROM tbl_lte_prediction_results r
                INNER JOIN tbl_project p ON p.id = r.project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE rr
                FROM tbl_lte_prediction_results_refined rr
                INNER JOIN tbl_project p ON p.id = rr.project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE br
                FROM lte_prediction_baseline_results br
                INNER JOIN tbl_project p ON p.id = br.project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE opr
                FROM lte_prediction_optimised_results opr
                INNER JOIN tbl_project p ON p.id = opr.project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE mr
                FROM map_regions mr
                INNER JOIN tbl_project p ON p.id = mr.tbl_project_id
                WHERE p.created_by_user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_project WHERE created_by_user_id = {userId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_upload_history WHERE uploaded_by = {userId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM thresholds WHERE user_id = {userId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_company_user_license_issued WHERE tbl_user_id = {userId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_session WHERE user_id = {userId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_user_deletion_otp WHERE user_id = {userId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_user_deletion_token WHERE user_id = {userId};", ct);

            // Keep audit history but remove direct identifiers after permanent deletion.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE tbl_user_deletion_audit
                SET user_id = NULL,
                    phone_number = NULL,
                    message = CONCAT('[anonymized] ', COALESCE(message, ''))
                WHERE user_id = {userId};", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM tbl_user WHERE id = {userId} AND is_deleted = 1;", ct);

            await tx.CommitAsync(ct);
        }
    }
}
