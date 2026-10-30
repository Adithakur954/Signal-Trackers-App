using Dapper;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class RfMobileEnrollmentStore(IConfiguration configuration) : IRfMobileEnrollmentStore
{
    private MySqlConnection Connection(string region)
    {
        var value = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region));
        if (string.IsNullOrWhiteSpace(value)) throw new RfLogRequestException(503, "RF enrollment database is not configured.");
        return new(new MySqlConnectionStringBuilder(value) { GuidFormat = MySqlGuidFormat.None }.ConnectionString);
    }
    private const string Select = """
        SELECT e.id Id,e.region Region,e.user_id UserId,e.company_id CompanyId,e.mobile_number MobileNumber,
          e.company_code CompanyCode,e.device_label DeviceLabel,e.proof_hash ProofHash,e.status Status,
          e.created_at CreatedAtUtc,e.request_expires_at RequestExpiresAtUtc,e.approved_at ApprovedAtUtc,
          e.approval_expires_at ApprovalExpiresAtUtc,e.latest_renewal_id LatestRenewalId,
          (u.id IS NOT NULL AND u.company_id=e.company_id AND u.isactive=1 AND COALESCE(u.is_deleted,0)=0
            AND c.id IS NOT NULL AND COALESCE(c.status,0)<>99
            AND TRIM(u.mobile)=e.mobile_number AND TRIM(c.company_code)=e.company_code) OwnerActive
        FROM rf_mobile_enrollments e LEFT JOIN tbl_user u ON u.id=e.user_id
          LEFT JOIN tbl_company c ON c.id=e.company_id
        """;
    private static CommandDefinition Cmd(string sql, object? args, CancellationToken ct, MySqlTransaction? tx = null) => new(sql, args, tx, cancellationToken: ct);

    public async Task<RfMobileOwner?> FindOwnerAsync(RfMobileEnrollmentRequest request, CancellationToken ct)
    {
        await using var conn = Connection(request.Region);
        return await conn.QuerySingleOrDefaultAsync<RfMobileOwner>(Cmd("""
            SELECT u.id UserId,u.company_id CompanyId FROM tbl_user u JOIN tbl_company c ON c.id=u.company_id
            WHERE u.id=@UserId AND u.isactive=1 AND COALESCE(u.is_deleted,0)=0 AND COALESCE(c.status,0)<>99
              AND TRIM(u.mobile)=@mobile AND TRIM(c.company_code)=@code
            """, new { request.UserId, mobile = request.MobileNumber.Trim(), code = request.CompanyCode.Trim() }, ct));
    }

    public async Task<RfMobileEnrollment> CreateAsync(RfMobileEnrollment row, CancellationToken ct)
    {
        await using var conn = Connection(row.Region);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Serializing enrollment creation per existing user bounds pending-request spam.
        await conn.ExecuteScalarAsync<int>(Cmd("SELECT id FROM tbl_user WHERE id=@UserId FOR UPDATE", row, ct, tx));
        var existing = await conn.QuerySingleOrDefaultAsync<RfMobileEnrollment>(Cmd(Select+" WHERE e.id=@Id AND e.region=@Region", row, ct, tx));
        if (existing != null) { await tx.CommitAsync(ct); return existing; }
        if (await conn.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM rf_mobile_upload_credentials WHERE id=@Id AND region=@Region", row, ct, tx)) != 0)
            throw new RfLogRequestException(409, "This enrollment ID is already used. Generate a new enrollment ID.");
        if (await conn.ExecuteScalarAsync<int>(Cmd("""
            SELECT COUNT(*) FROM rf_mobile_enrollments WHERE user_id=@UserId AND region=@Region
              AND status='pending' AND request_expires_at>UTC_TIMESTAMP(6)
            """, row, ct, tx)) >= 5)
            throw new RfLogRequestException(429, "Too many pending device requests for this user. Reuse the saved enrollment or contact your administrator.");
        await conn.ExecuteAsync(Cmd("""
            INSERT INTO rf_mobile_enrollments(id,region,user_id,company_id,mobile_number,company_code,device_label,proof_hash,status,created_at,request_expires_at)
            VALUES(@Id,@Region,@UserId,@CompanyId,@MobileNumber,@CompanyCode,@DeviceLabel,@ProofHash,'pending',@CreatedAtUtc,@RequestExpiresAtUtc)
            """, row, ct, tx));
        var saved = await conn.QuerySingleAsync<RfMobileEnrollment>(Cmd(Select+" WHERE e.id=@Id AND e.region=@Region", row, ct, tx));
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<RfMobileEnrollment?> GetAsync(string region, string id, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.QuerySingleOrDefaultAsync<RfMobileEnrollment>(Cmd(Select+" WHERE e.id=@id AND e.region=@region", new { id, region }, ct));
    }

    public async Task<IReadOnlyList<RfMobileEnrollment>> ListAsync(string region, int companyId, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return (await conn.QueryAsync<RfMobileEnrollment>(Cmd(Select+" WHERE e.region=@region AND e.company_id=@companyId ORDER BY e.created_at DESC LIMIT 500",
            new { region, companyId }, ct))).ToArray();
    }

    public async Task<RfEnrollmentMutation> MutateAsync(string region, string id,
        Func<RfMobileEnrollment, RfMobileCredential?, RfEnrollmentMutation> change, CancellationToken ct)
    {
        await using var conn = Connection(region);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RfMobileEnrollment>(Cmd(Select+" WHERE e.id=@id AND e.region=@region FOR UPDATE", new { id, region }, ct, tx))
            ?? throw new RfLogRequestException(404, "Enrollment not found.");
        var credential = await conn.QuerySingleOrDefaultAsync<RfMobileCredential>(Cmd("""
            SELECT id Id,region Region,company_id CompanyId,user_id UserId,mobile_number MobileNumber,company_code CompanyCode,
              device_label DeviceLabel,token_hash TokenHash,created_at CreatedAtUtc,expires_at ExpiresAtUtc,revoked_at RevokedAtUtc
            FROM rf_mobile_upload_credentials WHERE id=@id AND region=@region FOR UPDATE
            """, new { id, region }, ct, tx));
        var mutation = change(row, credential);
        await conn.ExecuteAsync(Cmd("""
            UPDATE rf_mobile_enrollments SET status=@Status,approved_at=@ApprovedAtUtc,
              approval_expires_at=@ApprovalExpiresAtUtc,latest_renewal_id=@LatestRenewalId WHERE id=@Id AND region=@Region
            """, mutation.Enrollment, ct, tx));
        if (mutation.Credential is { } result)
            await conn.ExecuteAsync(Cmd("""
                INSERT INTO rf_mobile_upload_credentials(id,region,company_id,user_id,mobile_number,company_code,device_label,token_hash,created_at,expires_at,revoked_at)
                VALUES(@Id,@Region,@CompanyId,@UserId,@MobileNumber,@CompanyCode,@DeviceLabel,@TokenHash,@CreatedAtUtc,@ExpiresAtUtc,@RevokedAtUtc)
                ON DUPLICATE KEY UPDATE token_hash=@TokenHash,expires_at=@ExpiresAtUtc,revoked_at=@RevokedAtUtc
                """, result, ct, tx));
        await tx.CommitAsync(ct);
        return mutation;
    }
}
