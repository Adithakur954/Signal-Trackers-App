using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfMobileEnrollmentStore
{
    Task<RfMobileOwner?> FindOwnerAsync(RfMobileEnrollmentRequest request, CancellationToken ct);
    Task<RfMobileEnrollment> CreateAsync(RfMobileEnrollment enrollment, CancellationToken ct);
    Task<RfMobileEnrollment?> GetAsync(string region, string id, CancellationToken ct);
    Task<IReadOnlyList<RfMobileEnrollment>> ListAsync(string region, int companyId, CancellationToken ct);
    // Lock the enrollment and credential, validate current ownership and commit both atomically.
    Task<RfEnrollmentMutation> MutateAsync(string region, string id,
        Func<RfMobileEnrollment, RfMobileCredential?, RfEnrollmentMutation> change, CancellationToken ct);
}

public sealed class RfMobileEnrollmentService(IRfMobileEnrollmentStore store, TimeProvider clock)
{
    public static bool ValidProof(string proof) => proof.Length == 64 && proof.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void CheckProof(RfMobileEnrollment row, string proof)
    {
        if (!ValidProof(proof) || row.ProofHash.Length != 64 || !row.ProofHash.All(char.IsAsciiHexDigit)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row.ProofHash), Convert.FromHexString(Hash(proof))))
            throw new RfLogRequestException(404, "Enrollment not found.");
    }

    public async Task<RfMobileEnrollment> RequestAsync(RfMobileEnrollmentRequest request, string proof, CancellationToken ct)
    {
        if (request.EnrollmentId == Guid.Empty || !ValidProof(proof)
            || !Validator.TryValidateObject(request, new ValidationContext(request), [], true))
            throw new RfLogRequestException(400, "Supply enrollmentId, region, userId, mobile number, company code, device label and a 64-character lowercase hex enrollment secret.");
        var owner = await store.FindOwnerAsync(request, ct);
        if (owner == null || owner.UserId != request.UserId || owner.CompanyId <= 0)
            throw new RfLogRequestException(403, "No active user matches the requested company/mobile in this region.");
        var now = clock.GetUtcNow().UtcDateTime;
        var proposed = new RfMobileEnrollment
        {
            Id = request.EnrollmentId.ToString("N"), Region = request.Region, UserId = owner.UserId, CompanyId = owner.CompanyId,
            MobileNumber = request.MobileNumber.Trim(), CompanyCode = request.CompanyCode.Trim(), DeviceLabel = request.DeviceLabel.Trim(),
            ProofHash = Hash(proof), CreatedAtUtc = now, RequestExpiresAtUtc = now.AddHours(48), OwnerActive = true
        };
        var saved = await store.CreateAsync(proposed, ct);
        CheckProof(saved, proof);
        if (saved.UserId != proposed.UserId || saved.CompanyId != proposed.CompanyId || saved.MobileNumber != proposed.MobileNumber
            || saved.CompanyCode != proposed.CompanyCode || saved.DeviceLabel != proposed.DeviceLabel)
            throw new RfLogRequestException(409, "Enrollment ID is already associated with different registration details.");
        return saved;
    }

    public async Task<RfMobileEnrollment> StatusAsync(string region, Guid id, string proof, CancellationToken ct)
    {
        if (region is not ("IN" or "TW") || id == Guid.Empty || !ValidProof(proof))
            throw new RfLogRequestException(400, "A valid region, enrollment ID and enrollment secret are required.");
        var row = await store.GetAsync(region, id.ToString("N"), ct)
            ?? throw new RfLogRequestException(404, "Enrollment not found.");
        CheckProof(row, proof);
        return row;
    }

    public async Task<RfMobileEnrollment> DecideAsync(RfLogActor admin, Guid id, string decision, CancellationToken ct)
    {
        if (admin.Region is not ("IN" or "TW") || admin.CompanyId <= 0 || id == Guid.Empty || decision is not ("approve" or "deny"))
            throw new RfLogRequestException(400, "A valid enrollment, company, region and approve/deny decision are required.");
        var now = clock.GetUtcNow().UtcDateTime;
        var result = await store.MutateAsync(admin.Region, id.ToString("N"), (row, credential) =>
        {
            if (row.CompanyId != admin.CompanyId) throw new RfLogRequestException(404, "Enrollment not found.");
            if (decision == "deny") return new(row with { Status = "denied" }, credential == null ? null : credential with { RevokedAtUtc = now });
            if (!row.OwnerActive) throw new RfLogRequestException(403, "Mobile user/company association is no longer active.");
            if (row.Status == "approved" && row.ApprovalExpiresAtUtc > now && credential?.RevokedAtUtc == null) return new(row);
            if (row.Status != "pending" || row.RequestExpiresAtUtc <= now)
                throw new RfLogRequestException(409, "Enrollment is denied or expired. A new device enrollment is required.");
            return new(row with { Status = "approved", ApprovedAtUtc = now, ApprovalExpiresAtUtc = now.AddDays(365) });
        }, ct);
        return result.Enrollment;
    }

    public async Task<object> TokenAsync(Guid id, RfMobileRenewalRequest request, string proof, CancellationToken ct)
    {
        if (id == Guid.Empty || request.RenewalId == Guid.Empty || request.PreviousRenewalId == Guid.Empty || request.Region is not ("IN" or "TW") || !ValidProof(proof))
            throw new RfLogRequestException(400, "A valid enrollment ID, renewalId, region and enrollment secret are required.");
        var now = clock.GetUtcNow().UtcDateTime;
        var credentialId = id.ToString("N");
        var renewalId = request.RenewalId.ToString("N");
        // Deterministic per renewal ID permits safe recovery after a lost response,
        // while neither the enrollment secret nor raw access token is stored.
        var secret = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(proof),
            Encoding.UTF8.GetBytes($"rf-access-v1:{request.Region}:{credentialId}:{renewalId}"))).ToLowerInvariant();
        var token = $"rfm1.{request.Region}.{credentialId}.{secret}";
        var result = await store.MutateAsync(request.Region, credentialId, (row, existing) =>
        {
            CheckProof(row, proof);
            if (!row.OwnerActive || row.Status != "approved" || row.ApprovalExpiresAtUtc is not { } approvalEnd || approvalEnd <= now)
                throw new RfLogRequestException(403, "Device approval is pending, denied, inactive or expired.");
            if (existing != null && (existing.RevokedAtUtc != null || existing.UserId != row.UserId || existing.CompanyId != row.CompanyId
                || existing.MobileNumber != row.MobileNumber || existing.CompanyCode != row.CompanyCode))
                throw new RfLogRequestException(401, "Installation credential is revoked or no longer matches its approved owner.");
            if (row.LatestRenewalId == renewalId)
            {
                if (existing == null || existing.ExpiresAtUtc <= now || existing.TokenHash != Hash(token))
                    throw new RfLogRequestException(401, "This renewal has expired. Use a new renewalId.");
                return new(row, existing);
            }
            if (request.PreviousRenewalId?.ToString("N") != row.LatestRenewalId)
                throw new RfLogRequestException(409, "Another renewal has already completed. Recover the latest renewalId from enrollment status before continuing.");
            var expires = now.AddDays(30) < approvalEnd ? now.AddDays(30) : approvalEnd;
            return new(row with { LatestRenewalId = renewalId }, new RfMobileCredential
            {
                Id = row.Id, Region = row.Region, UserId = row.UserId, CompanyId = row.CompanyId,
                MobileNumber = row.MobileNumber, CompanyCode = row.CompanyCode, DeviceLabel = row.DeviceLabel,
                TokenHash = Hash(token), CreatedAtUtc = existing?.CreatedAtUtc ?? now, ExpiresAtUtc = expires
            });
        }, ct);
        return new { tokenType = "Bearer", uploadToken = token, credential = RfMobileCredentialService.Metadata(result.Credential!),
            approvalExpiresAtUtc = Utc(result.Enrollment.ApprovalExpiresAtUtc), renewalId, renewBeforeSeconds = 86400 };
    }

    private static DateTime? Utc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
    public object Metadata(RfMobileEnrollment row) => new
    {
        enrollmentId = row.Id, region = row.Region, userId = row.UserId, companyId = row.CompanyId,
        mobileNumber = row.MobileNumber, companyCode = row.CompanyCode, deviceLabel = row.DeviceLabel,
        deviceVerificationCode = row.ProofHash[..12],
        status = !row.OwnerActive ? "inactive" : row.Status == "denied" ? "denied"
            : (row.Status == "pending" ? row.RequestExpiresAtUtc : row.ApprovalExpiresAtUtc) <= clock.GetUtcNow().UtcDateTime ? "expired" : row.Status,
        createdAtUtc = Utc(row.CreatedAtUtc), requestExpiresAtUtc = Utc(row.RequestExpiresAtUtc),
        approvedAtUtc = Utc(row.ApprovedAtUtc), approvalExpiresAtUtc = Utc(row.ApprovalExpiresAtUtc),
        latestRenewalId = row.LatestRenewalId, pollAfterSeconds = 30
    };
}
