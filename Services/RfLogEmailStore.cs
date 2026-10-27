using System.Net.Mail;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfLogEmailStore
{
    Task<IReadOnlyList<RfLogRecipient>> RecipientsAsync(string region, int companyId, CancellationToken ct);
    Task<RfLogRecipient> RegisterAsync(string region, int companyId, RfLogRecipientRequest request, CancellationToken ct);
    Task<bool> DisableAsync(string region, int companyId, string id, CancellationToken ct);
    Task EnqueueAsync(string region, CancellationToken ct);
    Task<RfLogEmailClaim?> ClaimAsync(string region, CancellationToken ct);
    Task<bool> CanSendAsync(RfLogEmailClaim claim, CancellationToken ct);
    Task FinishAsync(RfLogEmailClaim claim, bool success, string? error, CancellationToken ct);
    Task<IReadOnlyList<RfLogEmailDelivery>> DeliveriesAsync(string region, int companyId, string? uploadId, CancellationToken ct);
    Task<bool> RetryAsync(string region, int companyId, string id, CancellationToken ct);
}

public sealed class RfLogEmailStore(IConfiguration configuration, IOptions<RfLogIntegrationOptions> options) : IRfLogEmailStore
{
    private MySqlConnection Connection(string region)
    {
        var value = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region));
        if (string.IsNullOrWhiteSpace(value)) throw new RfLogRequestException(503, $"RF database for {region} is not configured.");
        return new(new MySqlConnectionStringBuilder(value) { DateTimeKind = MySqlDateTimeKind.Utc, GuidFormat = MySqlGuidFormat.None }.ConnectionString);
    }
    private static CommandDefinition Sql(string sql, object? args, CancellationToken ct) => new(sql, args, cancellationToken: ct);
    private const string RecipientColumns = "id Id,email Email,name Name,enabled Enabled,company_default CompanyDefault,created_at CreatedAtUtc";
    // A changed/deleted company profile must not deliver a queued company report to its former address.
    private const string ActiveRecipient = """
        r.enabled=1 AND (r.company_default=0 OR EXISTS(
          SELECT 1 FROM tbl_company c WHERE c.id=r.company_id AND COALESCE(c.status,0)<>99
            AND LOWER(TRIM(c.email))=r.email))
        """;
    private const string EnqueueSql = $"""
        INSERT INTO rf_log_email_deliveries(id,upload_id,recipient_id,region,company_id,report_reference,status,next_attempt,created_at)
        SELECT UUID(),u.id,r.id,u.region,u.company_id,u.report_reference,'pending',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)
        FROM rf_log_uploads u JOIN rf_log_report_recipients r ON r.region=u.region AND r.company_id=u.company_id
        WHERE u.region=@region AND u.status='completed' AND {ActiveRecipient} AND r.created_at<=u.created_at
          AND u.report_reference LIKE CONCAT('rf-logs/reports/',u.region,'/',u.company_id,'/',u.id,'/%/report.pdf')
          AND NOT EXISTS(SELECT 1 FROM rf_log_email_deliveries d WHERE d.region=u.region AND d.upload_id=u.id AND d.recipient_id=r.id)
        ORDER BY u.created_at,r.id LIMIT 100
        ON DUPLICATE KEY UPDATE id=rf_log_email_deliveries.id
        """;
    private const string CancelInactiveSql = $"""
        UPDATE rf_log_email_deliveries d SET status='cancelled',lease_token=NULL,lease_until=NULL
        WHERE region=@region AND status='pending' AND (lease_until IS NULL OR lease_until<UTC_TIMESTAMP(6))
          AND NOT EXISTS(SELECT 1 FROM rf_log_report_recipients r WHERE r.id=d.recipient_id AND r.region=d.region AND r.company_id=d.company_id AND {ActiveRecipient})
        """;
    private const string CanSendSql = $"""
        SELECT EXISTS(SELECT 1 FROM rf_log_email_deliveries d JOIN rf_log_report_recipients r ON r.id=d.recipient_id AND r.region=d.region AND r.company_id=d.company_id
          WHERE d.id=@Id AND d.region=@Region AND d.company_id=@CompanyId AND d.lease_token=@Token
            AND d.lease_until>UTC_TIMESTAMP(6) AND d.status='pending' AND r.email=@Email AND {ActiveRecipient})
        """;
    private const string DeliveryColumns = """
        d.id Id,d.upload_id UploadId,d.recipient_id RecipientId,d.region Region,d.company_id CompanyId,
        r.email Email,r.name Name,d.report_reference ReportReference,d.status Status,d.attempts Attempts,
        d.sent_at SentAtUtc,d.last_error Error
        """;

    public static string NormalizeEmail(RfLogRecipientRequest request)
    {
        var email = request.Email?.Trim() ?? "";
        if (email.Length is < 3 or > 254 || email.Any(c => c > 127 || char.IsControl(c))
            || !MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
            || request.Name == null || request.Name.Length > 100 || request.Name.Any(char.IsControl))
            throw new RfLogRequestException(400, "Supply one valid email address and a name of at most 100 characters.");
        return email.ToLowerInvariant();
    }

    public static async Task EnsureCompanyRecipientAsync(MySqlConnection connection, MySqlTransaction transaction,
        string region, int companyId, CancellationToken ct)
    {
        _ = RfLogUploadStore.ConnectionStringName(region);
        var company = await connection.QuerySingleOrDefaultAsync<CompanyMailbox>(new CommandDefinition("""
            SELECT email Email,company_name Name FROM tbl_company WHERE id=@companyId AND COALESCE(status,0)<>99 FOR UPDATE
            """, new { companyId }, transaction, cancellationToken: ct));
        string? email = null;
        if (company != null)
        {
            try { email = NormalizeEmail(new() { Email = company.Email ?? "" }); }
            catch (RfLogRequestException) { /* A missing/invalid company mailbox never prevents ZIP/PDF processing. */ }
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE rf_log_report_recipients SET enabled=0
            WHERE region=@region AND company_id=@companyId AND company_default=1 AND (@email IS NULL OR email<>@email)
            """, new { region, companyId, email }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE rf_log_email_deliveries d JOIN rf_log_report_recipients r ON r.id=d.recipient_id AND r.region=d.region AND r.company_id=d.company_id
            SET d.status='cancelled',d.lease_token=NULL,d.lease_until=NULL
            WHERE d.region=@region AND d.company_id=@companyId AND d.status='pending' AND r.company_default=1 AND r.enabled=0
            """, new { region, companyId }, transaction, cancellationToken: ct));
        if (email == null) return;
        var name = new string((company!.Name ?? "").Where(c => !char.IsControl(c)).Take(100).ToArray());
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO rf_log_report_recipients(id,region,company_id,email,name,enabled,company_default,created_at)
            VALUES(@id,@region,@companyId,@email,@name,1,1,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE name=VALUES(name),created_at=IF(enabled=0,UTC_TIMESTAMP(6),created_at),enabled=1,company_default=1
            """, new { id = Guid.NewGuid().ToString("D"), region, companyId, email, name }, transaction, cancellationToken: ct));
    }

    private sealed class CompanyMailbox
    {
        public string? Email { get; set; }
        public string? Name { get; set; }
    }

    public async Task<IReadOnlyList<RfLogRecipient>> RecipientsAsync(string region, int companyId, CancellationToken ct)
    {
        await using var connection = Connection(region);
        return (await connection.QueryAsync<RfLogRecipient>(Sql($"SELECT {RecipientColumns} FROM rf_log_report_recipients WHERE region=@region AND company_id=@companyId ORDER BY email", new { region, companyId }, ct))).ToArray();
    }

    public async Task<RfLogRecipient> RegisterAsync(string region, int companyId, RfLogRecipientRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request);
        await using var connection = Connection(region);
        await connection.ExecuteAsync(Sql("""
            INSERT INTO rf_log_report_recipients(id,region,company_id,email,name,enabled,created_at)
            VALUES(@id,@region,@companyId,@email,@Name,1,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE name=VALUES(name),created_at=IF(enabled=0,UTC_TIMESTAMP(6),created_at),enabled=1
            """, new { id = Guid.NewGuid().ToString("D"), region, companyId, email, request.Name }, ct));
        return await connection.QuerySingleAsync<RfLogRecipient>(Sql($"SELECT {RecipientColumns} FROM rf_log_report_recipients WHERE region=@region AND company_id=@companyId AND email=@email", new { region, companyId, email }, ct));
    }

    public async Task<bool> DisableAsync(string region, int companyId, string id, CancellationToken ct)
    {
        await using var connection = Connection(region);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var recipient = await connection.QuerySingleOrDefaultAsync<RfLogRecipient>(new CommandDefinition($"SELECT {RecipientColumns} FROM rf_log_report_recipients WHERE id=@id AND region=@region AND company_id=@companyId FOR UPDATE",
            new { id, region, companyId }, transaction, cancellationToken: ct));
        if (recipient is { CompanyDefault: true, Enabled: true })
            throw new RfLogRequestException(409, "The automatic company recipient is managed by the company profile email. Update the company profile to change it.");
        var count = await connection.ExecuteAsync(new CommandDefinition("UPDATE rf_log_report_recipients SET enabled=0 WHERE id=@id AND region=@region AND company_id=@companyId", new { id, region, companyId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE rf_log_email_deliveries SET status='cancelled',lease_token=NULL,lease_until=NULL
            WHERE recipient_id=@id AND region=@region AND company_id=@companyId AND status='pending'
            """, new { id, region, companyId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return count > 0;
    }

    public async Task EnqueueAsync(string region, CancellationToken ct)
    {
        await using var connection = Connection(region);
        await connection.ExecuteAsync(Sql(EnqueueSql, new { region }, ct));
    }

    public async Task<RfLogEmailClaim?> ClaimAsync(string region, CancellationToken ct)
    {
        await using var connection = Connection(region);
        await connection.ExecuteAsync(Sql(CancelInactiveSql, new { region }, ct));
        await connection.ExecuteAsync(Sql("""
            UPDATE rf_log_email_deliveries SET status='dead_letter',lease_token=NULL,lease_until=NULL
            WHERE region=@region AND status='pending' AND attempts>=@max AND (lease_until IS NULL OR lease_until<UTC_TIMESTAMP(6))
            """, new { region, max = options.Value.MaxEmailAttempts }, ct));
        var token = Guid.NewGuid().ToString("N");
        var count = await connection.ExecuteAsync(Sql("""
            UPDATE rf_log_email_deliveries SET lease_token=@token,lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 5 MINUTE),attempts=attempts+1
            WHERE region=@region AND status='pending' AND next_attempt<=UTC_TIMESTAMP(6) AND attempts<@max
              AND (lease_until IS NULL OR lease_until<UTC_TIMESTAMP(6))
            ORDER BY created_at,id LIMIT 1
            """, new { region, token, max = options.Value.MaxEmailAttempts }, ct));
        if (count == 0) return null;
        var job = await connection.QuerySingleAsync<RfLogEmailDelivery>(Sql($"""
            SELECT {DeliveryColumns} FROM rf_log_email_deliveries d JOIN rf_log_report_recipients r ON r.id=d.recipient_id AND r.region=d.region AND r.company_id=d.company_id
            WHERE d.region=@region AND d.lease_token=@token
            """, new { region, token }, ct));
        return new(job, token);
    }

    public async Task<bool> CanSendAsync(RfLogEmailClaim claim, CancellationToken ct)
    {
        await using var connection = Connection(claim.Delivery.Region);
        return await connection.ExecuteScalarAsync<int>(Sql(CanSendSql, new { claim.Delivery.Id, claim.Delivery.Region,
            claim.Delivery.CompanyId, claim.Delivery.Email, claim.Token }, ct)) == 1;
    }

    public async Task FinishAsync(RfLogEmailClaim claim, bool success, string? error, CancellationToken ct)
    {
        await using var connection = Connection(claim.Delivery.Region);
        await connection.ExecuteAsync(Sql("""
            UPDATE rf_log_email_deliveries SET status=CASE WHEN @success THEN 'sent' WHEN attempts>=@max THEN 'dead_letter' ELSE 'pending' END,
              sent_at=IF(@success,UTC_TIMESTAMP(6),NULL),last_error=@error,
              next_attempt=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL LEAST(3600,30*POW(2,LEAST(attempts-1,7))) SECOND),lease_token=NULL,lease_until=NULL
            WHERE id=@Id AND region=@Region AND lease_token=@Token AND lease_until>UTC_TIMESTAMP(6)
            """, new { claim.Delivery.Id, claim.Delivery.Region, claim.Token, success, error, max = options.Value.MaxEmailAttempts }, ct));
    }

    public async Task<IReadOnlyList<RfLogEmailDelivery>> DeliveriesAsync(string region, int companyId, string? uploadId, CancellationToken ct)
    {
        await using var connection = Connection(region);
        return (await connection.QueryAsync<RfLogEmailDelivery>(Sql($"""
            SELECT {DeliveryColumns} FROM rf_log_email_deliveries d JOIN rf_log_report_recipients r ON r.id=d.recipient_id AND r.region=d.region AND r.company_id=d.company_id
            WHERE d.region=@region AND d.company_id=@companyId AND (@uploadId IS NULL OR d.upload_id=@uploadId)
            ORDER BY d.created_at DESC LIMIT 100
            """, new { region, companyId, uploadId }, ct))).ToArray();
    }

    public async Task<bool> RetryAsync(string region, int companyId, string id, CancellationToken ct)
    {
        await using var connection = Connection(region);
        return await connection.ExecuteAsync(Sql($"""
            UPDATE rf_log_email_deliveries d SET status='pending',attempts=0,next_attempt=UTC_TIMESTAMP(6),last_error=NULL
            WHERE d.id=@id AND d.region=@region AND d.company_id=@companyId AND d.status='dead_letter'
              AND EXISTS(SELECT 1 FROM rf_log_report_recipients r WHERE r.id=d.recipient_id AND r.region=d.region AND r.company_id=d.company_id AND {ActiveRecipient})
            """, new { region, companyId, id }, ct)) > 0;
    }
}
