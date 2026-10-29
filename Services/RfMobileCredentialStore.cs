using Dapper;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfMobileCredentialStore
{
    Task<IReadOnlyList<RfMobileOwner>> FindOwnersAsync(string region, int companyId, RfMobileCredentialRequest request, CancellationToken ct);
    Task InsertAsync(RfMobileCredential credential, CancellationToken ct);
    // Returns only live credentials whose user/company association still matches.
    Task<RfMobileCredential?> FindActiveAsync(string region, string id, CancellationToken ct);
    Task<IReadOnlyList<RfMobileCredential>> ListAsync(string region, int companyId, CancellationToken ct);
    Task<bool> RevokeAsync(string region, int companyId, string id, CancellationToken ct);
}

public sealed class RfMobileCredentialStore(IConfiguration configuration) : IRfMobileCredentialStore
{
    private MySqlConnection Connection(string region)
    {
        var value = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region));
        if (string.IsNullOrWhiteSpace(value)) throw new RfLogRequestException(503, "RF mobile credential database is not configured.");
        return new(new MySqlConnectionStringBuilder(value) { GuidFormat = MySqlGuidFormat.None }.ConnectionString);
    }

    private static CommandDefinition Sql(string sql, object args, CancellationToken ct) => new(sql, args, cancellationToken: ct);

    public async Task<IReadOnlyList<RfMobileOwner>> FindOwnersAsync(string region, int companyId, RfMobileCredentialRequest request, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return (await conn.QueryAsync<RfMobileOwner>(Sql("""
            SELECT u.id UserId,u.company_id CompanyId FROM tbl_user u JOIN tbl_company c ON c.id=u.company_id
            WHERE u.company_id=@companyId AND u.isactive=1 AND COALESCE(u.is_deleted,0)=0
              AND COALESCE(c.status,0)<>99 AND TRIM(u.mobile)=@mobile AND TRIM(c.company_code)=@code
              AND (@UserId IS NULL OR u.id=@UserId) LIMIT 2
            """, new { companyId, mobile = request.MobileNumber.Trim(), code = request.CompanyCode.Trim(), request.UserId }, ct))).ToArray();
    }

    public async Task InsertAsync(RfMobileCredential credential, CancellationToken ct)
    {
        await using var conn = Connection(credential.Region);
        await conn.ExecuteAsync(Sql("""
            INSERT INTO rf_mobile_upload_credentials
              (id,region,company_id,user_id,mobile_number,company_code,device_label,token_hash,created_at,expires_at)
            VALUES(@Id,@Region,@CompanyId,@UserId,@MobileNumber,@CompanyCode,@DeviceLabel,@TokenHash,@CreatedAtUtc,@ExpiresAtUtc)
            """, credential, ct));
    }

    private const string Columns = """
        k.id Id,k.region Region,k.company_id CompanyId,k.user_id UserId,k.mobile_number MobileNumber,
        k.company_code CompanyCode,k.device_label DeviceLabel,k.token_hash TokenHash,
        k.created_at CreatedAtUtc,k.expires_at ExpiresAtUtc,k.revoked_at RevokedAtUtc
        """;

    public async Task<RfMobileCredential?> FindActiveAsync(string region, string id, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.QuerySingleOrDefaultAsync<RfMobileCredential>(Sql($"""
            SELECT {Columns} FROM rf_mobile_upload_credentials k
            JOIN tbl_user u ON u.id=k.user_id AND u.company_id=k.company_id
            JOIN tbl_company c ON c.id=k.company_id
            WHERE k.id=@id AND k.region=@region AND k.revoked_at IS NULL AND k.expires_at>UTC_TIMESTAMP(6)
              AND u.isactive=1 AND COALESCE(u.is_deleted,0)=0 AND COALESCE(c.status,0)<>99
              AND TRIM(u.mobile)=k.mobile_number AND TRIM(c.company_code)=k.company_code
            """, new { id, region }, ct));
    }

    public async Task<IReadOnlyList<RfMobileCredential>> ListAsync(string region, int companyId, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return (await conn.QueryAsync<RfMobileCredential>(Sql($"SELECT {Columns} FROM rf_mobile_upload_credentials k WHERE k.region=@region AND k.company_id=@companyId ORDER BY k.created_at DESC LIMIT 500",
            new { region, companyId }, ct))).ToArray();
    }

    public async Task<bool> RevokeAsync(string region, int companyId, string id, CancellationToken ct)
    {
        await using var conn = Connection(region);
        return await conn.ExecuteAsync(Sql("""
            UPDATE rf_mobile_upload_credentials SET revoked_at=COALESCE(revoked_at,UTC_TIMESTAMP(6))
            WHERE id=@id AND region=@region AND company_id=@companyId
            """, new { id, region, companyId }, ct)) > 0;
    }
}
