using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class RfTrustedMobileLoginOptions
{
    public const string Section = "RfLogIntegration:TrustedMobileLogin";
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "stracer-rf-enrollment";
    public List<RfTrustedMobileLoginKey> Keys { get; set; } = [];
}

public sealed class RfTrustedMobileLoginKey
{
    public string KeyId { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public string Region { get; set; } = "";
    public int[] CompanyIds { get; set; } = [];
}

// This is a short-lived signed login assertion, not an RF access token or a JWT.
// The private signing key belongs ONLY to the trusted login server, never the APK.
public sealed class RfVerifiedMobileLogin
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
    public Guid EnrollmentId { get; set; }
    public string Region { get; set; } = "";
    public int UserId { get; set; }
    public int CompanyId { get; set; }
    public string MobileNumber { get; set; } = "";
    public string CompanyCode { get; set; } = "";
    public string ProofHash { get; set; } = "";
}

public sealed class RfTrustedMobileLoginService(
    IOptions<RfTrustedMobileLoginOptions> options, RfMobileEnrollmentService enrollments, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static RfLogRequestException Invalid() => new(401, "Invalid or expired trusted mobile login assertion.");

    public async Task<RfMobileEnrollment> EnrollAsync(RfMobileEnrollmentRequest request, string proof,
        string assertion, CancellationToken ct)
    {
        var config = options.Value;
        if (!config.Enabled) throw new RfLogRequestException(503, "Trusted mobile login integration is not enabled.");
        if (string.IsNullOrWhiteSpace(config.Issuer) || string.IsNullOrWhiteSpace(config.Audience))
            throw new RfLogRequestException(503, "Trusted mobile login integration is not configured.");
        var identity = Verify(assertion, request, proof, config);
        // Signature, scope and proof are verified before creating any database row.
        var row = await enrollments.RequestAsync(request, proof, ct);
        if (row.CompanyId != identity.CompanyId) throw Invalid();
        return await enrollments.ApproveTrustedAsync(identity, proof, ct);
    }

    private RfVerifiedMobileLogin Verify(string assertion, RfMobileEnrollmentRequest request,
        string proof, RfTrustedMobileLoginOptions config)
    {
        if (!RfMobileEnrollmentService.ValidProof(proof) || assertion.Length is 0 or > 8192) throw Invalid();
        var parts = assertion.Split('.');
        if (parts.Length != 4 || parts[0] != "rfl1" || parts[1].Length is 0 or > 64
            || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw Invalid();
        var keys = config.Keys.Where(k => k.KeyId == parts[1]).ToArray();
        if (keys.Length != 1) throw Invalid();
        var key = keys[0];
        byte[] payload, signature;
        try
        {
            payload = WebEncoders.Base64UrlDecode(parts[2]);
            signature = WebEncoders.Base64UrlDecode(parts[3]);
            if (payload.Length > 4096 || WebEncoders.Base64UrlEncode(payload) != parts[2]
                || WebEncoders.Base64UrlEncode(signature) != parts[3]) throw Invalid();
        }
        catch (FormatException) { throw Invalid(); }
        try
        {
            using var rsa = RSA.Create();
            // Only public keys are accepted at the RF server.
            if (!key.PublicKeyPem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
                || key.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new RfLogRequestException(503, "Trusted mobile login public key is not configured correctly.");
            rsa.ImportFromPem(key.PublicKeyPem);
            if (rsa.KeySize < 2048) throw new RfLogRequestException(503, "Trusted mobile login RSA key must be at least 2048 bits.");
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(string.Join('.', parts.Take(3))), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw Invalid();
        }
        catch (ArgumentException) { throw new RfLogRequestException(503, "Trusted mobile login public key is invalid."); }
        catch (CryptographicException) { throw Invalid(); }
        RfVerifiedMobileLogin identity;
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) throw Invalid();
            identity = json.RootElement.Deserialize<RfVerifiedMobileLogin>(JsonOptions) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (identity.Issuer != config.Issuer || identity.Audience != config.Audience
            || identity.IssuedAt < now - 120 || identity.IssuedAt > now + 5
            || identity.ExpiresAt <= now || identity.ExpiresAt <= identity.IssuedAt
            || identity.ExpiresAt > identity.IssuedAt + 120
            || identity.EnrollmentId == Guid.Empty || identity.EnrollmentId != request.EnrollmentId
            || identity.Region is not ("IN" or "TW") || identity.Region != request.Region || key.Region != identity.Region
            || identity.UserId <= 0 || identity.UserId != request.UserId || identity.CompanyId <= 0
            || !key.CompanyIds.Contains(identity.CompanyId)
            || identity.MobileNumber != request.MobileNumber?.Trim() || identity.CompanyCode != request.CompanyCode?.Trim()
            || string.IsNullOrEmpty(identity.MobileNumber) || string.IsNullOrEmpty(identity.CompanyCode)
            || identity.ProofHash?.Length != 64 || !identity.ProofHash.All(char.IsAsciiHexDigit)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(identity.ProofHash), SHA256.HashData(Encoding.UTF8.GetBytes(proof))))
            throw Invalid();
        return identity;
    }
}
