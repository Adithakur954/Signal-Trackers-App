using System.Security.Cryptography;
using System.Text;
using SignalTracker.Models;
using SignalTracker.Security;

namespace SignalTracker.Services;

public sealed class RfMobileCredentialService(IRfMobileCredentialStore store, TimeProvider clock)
{
    public async Task<(RfMobileCredential Credential, string Token)> IssueAsync(RfLogActor admin, RfMobileCredentialRequest request, CancellationToken ct)
    {
        if (RegionAccess.Normalize(admin.Region) != admin.Region || admin.CompanyId <= 0
            || string.IsNullOrWhiteSpace(request.MobileNumber) || request.MobileNumber.Length > 40
            || string.IsNullOrWhiteSpace(request.CompanyCode) || request.CompanyCode.Length > 100
            || string.IsNullOrWhiteSpace(request.DeviceLabel) || request.DeviceLabel.Length > 100
            || request.ValidDays is < 1 or > 365 || request.UserId is <= 0)
            throw new RfLogRequestException(400, "Supply a mobile number, company code, device label and validity of 1 to 365 days.");
        var owners = await store.FindOwnersAsync(admin.Region, admin.CompanyId, request, ct);
        if (owners.Count == 0) throw new RfLogRequestException(403, "No active mobile user matches this company and mobile number.");
        if (owners.Count != 1) throw new RfLogRequestException(409, "Multiple users match; supply the intended userId.");
        var owner = owners[0];
        if (owner.UserId <= 0 || owner.CompanyId != admin.CompanyId)
            throw new RfLogRequestException(403, "Mobile user is outside the administrator's company.");
        var id = Guid.NewGuid().ToString("N");
        var token = $"rfm1.{admin.Region}.{id}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var now = clock.GetUtcNow().UtcDateTime;
        var credential = new RfMobileCredential
        {
            Id = id, Region = admin.Region, CompanyId = owner.CompanyId, UserId = owner.UserId,
            MobileNumber = request.MobileNumber.Trim(), CompanyCode = request.CompanyCode.Trim(), DeviceLabel = request.DeviceLabel.Trim(),
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(request.ValidDays)
        };
        await store.InsertAsync(credential, ct);
        return (credential, token);
    }

    public async Task<RfLogActor?> AuthenticateAsync(string token, CancellationToken ct)
    {
        // Fixed-size format rejects arbitrary input before accessing a database.
        if (token.Length != 105) return null;
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != "rfm1" || parts[1] is not ("IN" or "TW")
            || parts[2].Length != 32 || !Guid.TryParseExact(parts[2], "N", out _)
            || parts[3].Length != 64 || !parts[3].All(char.IsAsciiHexDigit)) return null;
        var credential = await store.FindActiveAsync(parts[1], parts[2], ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (credential == null || credential.Id != parts[2] || credential.Region != parts[1]
            || credential.CompanyId <= 0 || credential.UserId <= 0 || credential.RevokedAtUtc != null
            || credential.ExpiresAtUtc <= now || credential.TokenHash.Length != 64
            || !credential.TokenHash.All(char.IsAsciiHexDigit)) return null;
        var incoming = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        if (!CryptographicOperations.FixedTimeEquals(incoming, Convert.FromHexString(credential.TokenHash))) return null;
        return new RfLogActor($"mobile:{credential.Region}:{credential.Id}", credential.CompanyId, credential.UserId, credential.Region);
    }

    // Do not serialize TokenHash (or raw tokens) from list/revoke endpoints.
    public static object Metadata(RfMobileCredential credential) => new
    {
        credentialId = credential.Id, region = credential.Region, companyId = credential.CompanyId,
        userId = credential.UserId, mobileNumber = credential.MobileNumber, deviceLabel = credential.DeviceLabel,
        createdAtUtc = DateTime.SpecifyKind(credential.CreatedAtUtc, DateTimeKind.Utc),
        expiresAtUtc = DateTime.SpecifyKind(credential.ExpiresAtUtc, DateTimeKind.Utc),
        revokedAtUtc = credential.RevokedAtUtc is { } revoked ? (DateTime?)DateTime.SpecifyKind(revoked, DateTimeKind.Utc) : null
    };
}
