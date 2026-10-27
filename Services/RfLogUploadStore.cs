using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfLogUploadStore
{
    Task<bool> OwnerExistsAsync(RfLogUploadRequest request, int companyId, string region, CancellationToken ct);
    Task<RfLogUpload?> GetAsync(string id, string region, CancellationToken ct);
    Task RegisterAsync(RfLogUpload upload, CancellationToken ct);
    Task<bool> CompleteAsync(string id, string region, string objectKey, RfLogObject file, CancellationToken ct);
    Task<IReadOnlyList<RfLogUpload>> PendingAsync(string region, int limit, CancellationToken ct);
    Task<bool> ClaimAsync(string id, string region, string token, CancellationToken ct);
    Task<bool> ResultAsync(string id, string region, string token, RfLogReportResult result, CancellationToken ct);
    Task<(RfLogUpload Upload, string Token)?> ClaimNotificationAsync(string region, CancellationToken ct);
    Task FinishNotificationAsync(string id, string region, string token, bool success, CancellationToken ct);
    Task RetryNotificationAsync(string id, string region, CancellationToken ct);
    Task<bool> RetryReportAsync(string id, string region, CancellationToken ct);
}

// The database is the durable event queue. No in-memory queue or IIS-local files.
public sealed class RfLogUploadStore(IConfiguration configuration, IOptions<RfLogIntegrationOptions> options) : IRfLogUploadStore
{
    public static string ConnectionStringName(string region) => region switch
    {
        "IN" => "MySqlConnection",
        "TW" => "MySqlConnection2",
        _ => throw new RfLogRequestException(400, "RF region must be IN or TW.")
    };

    private MySqlConnection Connection(string region)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName(region));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new RfLogRequestException(503, $"RF database for {region} is not configured.");
        return new(new MySqlConnectionStringBuilder(connectionString) { GuidFormat = MySqlGuidFormat.None }.ConnectionString);
    }
    private static CommandDefinition Sql(string sql, object? args, CancellationToken ct) => new(sql, args, cancellationToken: ct);

    public async Task<bool> OwnerExistsAsync(RfLogUploadRequest request, int companyId, string region, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.ExecuteScalarAsync<int>(Sql("""
            SELECT EXISTS(SELECT 1 FROM tbl_session s JOIN tbl_user u ON u.id=s.user_id
              WHERE s.id=@SessionId AND u.id=@UserId AND u.company_id=@companyId
                AND u.isactive=1 AND (@ProjectId IS NULL OR EXISTS(
                  SELECT 1 FROM tbl_project p WHERE p.id=@ProjectId AND p.company_id=@companyId)))
            """, new { request.SessionId, request.UserId, request.ProjectId, companyId }, ct)) == 1;
    }

    private sealed class Stored
    {
        public string Id { get; set; } = "";
        public string ClientId { get; set; } = "";
        public int CompanyId { get; set; }
        public string Region { get; set; } = "IN";
        public string Metadata { get; set; } = "";
        public string StagingKey { get; set; } = "";
        public string? ObjectKey { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public string? ETag { get; set; }
        public string? ReportReference { get; set; }
        public string? Error { get; set; }
        public int NotificationAttempts { get; set; }
        public string NotificationStatus { get; set; } = "";
        public RfLogUpload Upload() => new()
        {
            Id = Id, ClientId = ClientId, CompanyId = CompanyId, Region = Region,
            Metadata = JsonSerializer.Deserialize<RfLogUploadRequest>(Metadata)!, StagingKey = StagingKey,
            ObjectKey = ObjectKey, Status = Status, CreatedAtUtc = DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc),
            UploadedAtUtc = UploadedAtUtc.HasValue ? DateTime.SpecifyKind(UploadedAtUtc.Value, DateTimeKind.Utc) : null,
            ETag = ETag, ReportReference = ReportReference, Error = Error,
            NotificationAttempts = NotificationAttempts, NotificationStatus = NotificationStatus
        };
    }
    private const string Columns = """
        id Id, client_id ClientId, company_id CompanyId, region Region, metadata Metadata, staging_key StagingKey,
        object_key ObjectKey, status Status, created_at CreatedAtUtc, uploaded_at UploadedAtUtc,
        etag ETag, report_reference ReportReference, report_error Error,
        notify_attempts NotificationAttempts, notify_status NotificationStatus
        """;

    public async Task<RfLogUpload?> GetAsync(string id, string region, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return (await conn.QuerySingleOrDefaultAsync<Stored>(Sql($"SELECT {Columns} FROM rf_log_uploads WHERE id=@id AND region=@region", new { id, region }, ct)))?.Upload();
    }

    public async Task RegisterAsync(RfLogUpload upload, CancellationToken ct)
    {
        await using var conn = Connection(upload.Region);
        await conn.OpenAsync(ct);
        await RegisterOnConnectionAsync(conn, upload, ct);
    }

    // Registration and the default company subscription commit together; a fast report
    // worker can never complete this upload before its company's recipient exists.
    public static async Task RegisterOnConnectionAsync(MySqlConnection conn, RfLogUpload upload, CancellationToken ct)
    {
        await using var transaction = await conn.BeginTransactionAsync(ct);
        await RfLogEmailStore.EnsureCompanyRecipientAsync(conn, transaction, upload.Region, upload.CompanyId, ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO rf_log_uploads(id,client_id,company_id,region,metadata,staging_key,status,created_at)
            VALUES(@Id,@ClientId,@CompanyId,@Region,@metadata,@StagingKey,'awaiting_upload',UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE id=id
            """, new { upload.Id, upload.ClientId, upload.CompanyId, upload.Region, metadata = JsonSerializer.Serialize(upload.Metadata), upload.StagingKey }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> CompleteAsync(string id, string region, string objectKey, RfLogObject file, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET object_key=@objectKey, etag=@ETag, uploaded_at=@UploadedAtUtc,
              status='ready', notify_status='pending', notify_after=UTC_TIMESTAMP(6)
            WHERE id=@id AND region=@region AND status='awaiting_upload'
            """, new { id, region, objectKey, file.ETag, file.UploadedAtUtc }, ct)) == 1;
    }

    public async Task<IReadOnlyList<RfLogUpload>> PendingAsync(string region, int limit, CancellationToken ct)
    {
        await using var conn = Connection(region);
        var rows = await conn.QueryAsync<Stored>(Sql($"""
            SELECT {Columns} FROM rf_log_uploads WHERE region=@region AND (status='ready'
                OR (status='processing' AND report_lease_until<UTC_TIMESTAMP(6)))
            ORDER BY created_at LIMIT @limit
            """, new { region, limit = Math.Clamp(limit, 1, 100) }, ct));
        return rows.Select(row => row.Upload()).ToArray();
    }

    public async Task<bool> ClaimAsync(string id, string region, string token, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET status='processing', report_token=@token,
              report_lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 15 MINUTE)
            WHERE id=@id AND region=@region AND (status='ready' OR (status='processing' AND report_lease_until<UTC_TIMESTAMP(6)))
            """, new { id, region, token }, ct)) == 1;
    }

    public async Task<bool> ResultAsync(string id, string region, string token, RfLogReportResult result, CancellationToken ct)
    {
        await using var conn = Connection(region);
        // Tokens fence stale workers after a lease expires or is claimed again.
        return await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET status=@Status,report_reference=@ReportReference,report_error=@Error,
              report_lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 15 MINUTE)
            WHERE id=@id AND region=@region AND report_token=@token AND report_lease_until>UTC_TIMESTAMP(6)
              AND (status='processing' OR status=@Status)
            """, new { id, region, token, result.Status, result.ReportReference, result.Error }, ct)) > 0;
    }

    public async Task<(RfLogUpload Upload, string Token)?> ClaimNotificationAsync(string region, CancellationToken ct)
    {
        await using var conn = Connection(region);
        await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET notify_status='dead_letter',notify_until=NULL,notify_token=NULL
            WHERE region=@region AND notify_status='pending' AND notify_attempts>=@max
              AND (notify_until IS NULL OR notify_until<UTC_TIMESTAMP(6))
            """, new { region, max = options.Value.MaxNotificationAttempts }, ct));
        var token = Guid.NewGuid().ToString("N");
        var count = await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET notify_token=@token, notify_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 2 MINUTE),
              notify_attempts=notify_attempts+1
            WHERE region=@region AND object_key IS NOT NULL AND status IN ('ready','processing','completed','failed') AND notify_status='pending'
              AND notify_after<=UTC_TIMESTAMP(6) AND (notify_until IS NULL OR notify_until<UTC_TIMESTAMP(6))
              AND notify_attempts<@max ORDER BY created_at LIMIT 1
            """, new { region, token, max = options.Value.MaxNotificationAttempts }, ct));
        if (count == 0) return null;
        var row = await conn.QuerySingleAsync<Stored>(Sql($"SELECT {Columns} FROM rf_log_uploads WHERE region=@region AND notify_token=@token", new { region, token }, ct));
        return (row.Upload(), token);
    }

    public async Task FinishNotificationAsync(string id, string region, string token, bool success, CancellationToken ct)
    {
        await using var conn = Connection(region);
        await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET notify_status=CASE WHEN @success THEN 'delivered'
                WHEN notify_attempts>=@max THEN 'dead_letter' ELSE 'pending' END,
              notify_after=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL LEAST(3600,30*POW(2,LEAST(notify_attempts-1,7))) SECOND),
              notify_until=NULL, notify_token=NULL
            WHERE id=@id AND region=@region AND notify_token=@token
            """, new { id, region, token, success, max = options.Value.MaxNotificationAttempts }, ct));
    }

    public async Task RetryNotificationAsync(string id, string region, CancellationToken ct)
    {
        await using var conn = Connection(region);
        await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET notify_status='pending',notify_attempts=0,notify_after=UTC_TIMESTAMP(6)
            WHERE id=@id AND region=@region AND object_key IS NOT NULL AND (notify_until IS NULL OR notify_until<UTC_TIMESTAMP(6))
            """, new { id, region }, ct));
    }

    public async Task<bool> RetryReportAsync(string id, string region, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.ExecuteAsync(Sql("""
            UPDATE rf_log_uploads SET status='ready', report_error=NULL, report_token=NULL,
              report_lease_until=NULL, report_reference=NULL
            WHERE id=@id AND region=@region AND status='failed' AND object_key IS NOT NULL
            """, new { id, region }, ct)) == 1;
    }
}
