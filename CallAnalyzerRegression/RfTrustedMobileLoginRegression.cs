using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class RfTrustedMobileLoginRegression
{
    private static readonly RSA SigningKey = RSA.Create(2048);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string Proof = "abababababababababababababababababababababababababababababababab";
    private static RfTrustedMobileLoginOptions Options() => new()
    {
        Enabled = true, Issuer = "test-login-server",
        Keys = [new() { KeyId = "in-key", PublicKeyPem = SigningKey.ExportSubjectPublicKeyInfoPem(), Region = "IN", CompanyIds = [7] }]
    };
    private static RfMobileEnrollmentRequest Request() => new()
    {
        EnrollmentId = Guid.NewGuid(), Region = "IN", UserId = 10,
        MobileNumber = "9000000000", CompanyCode = "TEST-COMPANY", DeviceLabel = "Verified installation"
    };
    private static RfVerifiedMobileLogin Identity(RfMobileEnrollmentRequest request) => new()
    {
        Issuer = "test-login-server", Audience = "stracer-rf-enrollment",
        IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds(),
        EnrollmentId = request.EnrollmentId, Region = request.Region, UserId = request.UserId,
        CompanyId = 7, MobileNumber = request.MobileNumber, CompanyCode = request.CompanyCode,
        ProofHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Proof)))
    };
    private static string Sign(RfVerifiedMobileLogin identity, RSA? key = null)
    {
        return SignPayload(JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions), key);
    }
    private static string SignPayload(byte[] payload, RSA? key = null)
    {
        var data = "rfl1.in-key." + WebEncoders.Base64UrlEncode(payload);
        return data + "." + WebEncoders.Base64UrlEncode((key ?? SigningKey).SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("RF trusted mobile login: " + message);
    }
    private static async Task Reject(Func<Task> action, int status)
    {
        try { await action(); throw new Exception("Expected rejection " + status); }
        catch (RfLogRequestException ex) { Check(ex.Status == status, $"expected {status}, got {ex.Status}"); }
    }
    internal static void ConfigureHttp(IServiceCollection services)
    {
        services.AddSingleton<IOptions<RfTrustedMobileLoginOptions>>(Microsoft.Extensions.Options.Options.Create(Options()));
        services.AddScoped<RfTrustedMobileLoginService>();
    }
    internal static async Task RunAsync()
    {
        var credentials = new RfMobileUploadRegression.MemoryCredentials();
        var store = new RfMobileEnrollmentRegression.MemoryEnrollments(credentials);
        var enrollment = new RfMobileEnrollmentService(store, TimeProvider.System);
        var config = Options();
        var service = new RfTrustedMobileLoginService(Microsoft.Extensions.Options.Options.Create(config), enrollment, TimeProvider.System);
        var request = Request();
        var identity = Identity(request);
        config.Enabled = false;
        await Reject(async () => { await service.EnrollAsync(request, Proof, Sign(identity), default); }, 503);
        config.Enabled = true;
        foreach (var malformed in new[] { "", "rfl1.in-key.!.!", "rfl1.unknown.e30.e30", new string('x', 8193) })
            await Reject(async () => { await service.EnrollAsync(request, Proof, malformed, default); }, 401);
        await Reject(async () => { await service.EnrollAsync(request, Proof, SignPayload("{\"issuer\":\"a\",\"issuer\":\"b\"}"u8.ToArray()), default); }, 401);
        await Reject(async () => { await service.EnrollAsync(request, Proof, SignPayload("{\"algorithm\":\"none\"}"u8.ToArray()), default); }, 401);
        await Reject(async () => { await service.EnrollAsync(request, Proof, SignPayload("null"u8.ToArray()), default); }, 401);
        using var attacker = RSA.Create(2048);
        await Reject(async () => { await service.EnrollAsync(request, Proof, Sign(identity, attacker), default); }, 401);
        foreach (var mutate in new Action<RfVerifiedMobileLogin>[]
        {
            x => x.Audience = "wrong-api", x => x.Issuer = "untrusted",
            x => x.ExpiresAt = x.IssuedAt - 1, x => x.ExpiresAt = x.IssuedAt + 121,
            x => x.IssuedAt += 100, x => { x.IssuedAt -= 200; x.ExpiresAt -= 200; },
            x => x.EnrollmentId = Guid.NewGuid(), x => x.Region = "TW", x => x.UserId = 11,
            x => x.CompanyId = 8, x => x.MobileNumber = "other", x => x.CompanyCode = "other",
            x => x.ProofHash = new string('a', 64), x => x.ProofHash = null!
        })
        {
            var invalid = Identity(request); mutate(invalid);
            await Reject(async () => { await service.EnrollAsync(request, Proof, Sign(invalid), default); }, 401);
        }
        await Reject(async () => { await service.EnrollAsync(request, new string('c', 64), Sign(identity), default); }, 401);
        Check(store.Rows.Count == 0, "invalid assertions never create enrollments");
        var signed = Sign(identity);
        var parts = signed.Split('.');
        parts[2] = WebEncoders.Base64UrlEncode("{}"u8.ToArray());
        await Reject(async () => { await service.EnrollAsync(request, Proof, string.Join('.', parts), default); }, 401);
        var approved = await service.EnrollAsync(request, Proof, signed, default);
        Check(approved.Status == "approved" && credentials.rows.Count == 0, "trusted login approves only the bound installation without returning an access token");
        var retried = await service.EnrollAsync(request, Proof, signed, default);
        Check(retried.ApprovalExpiresAtUtc == approved.ApprovalExpiresAtUtc, "replay cannot extend approval");
        var renewal = new RfMobileRenewalRequest { Region = "IN", RenewalId = Guid.NewGuid() };
        var issued = JsonSerializer.SerializeToElement(await enrollment.TokenAsync(request.EnrollmentId, renewal, Proof, default));
        var auth = new RfMobileCredentialService(credentials, TimeProvider.System);
        Check((await auth.AuthenticateAsync(issued.GetProperty("uploadToken").GetString()!, default))?.CompanyId == 7,
            "trusted enrollment yields the existing scoped bearer through the token endpoint");
        await credentials.RevokeAsync("IN", 7, approved.Id, default);
        await Reject(async () => { await service.EnrollAsync(request, Proof, signed, default); }, 401);
        request = Request(); identity = Identity(request); signed = Sign(identity);
        await enrollment.RequestAsync(request, Proof, default);
        await enrollment.DecideAsync(new RfLogActor("admin", 7, 1, "IN"), request.EnrollmentId, "deny", default);
        await Reject(async () => { await service.EnrollAsync(request, Proof, signed, default); }, 409);
        request = Request(); identity = Identity(request); signed = Sign(identity);
        credentials.ActiveOwner = false;
        await Reject(async () => { await service.EnrollAsync(request, Proof, signed, default); }, 403);
        credentials.ActiveOwner = true;
        // An explicitly allowed TW signer must remain scoped to TW.
        config.Keys[0].Region = "TW";
        await Reject(async () => { await service.EnrollAsync(request, Proof, signed, default); }, 401);
        request.Region = "TW"; identity = Identity(request);
        Check((await service.EnrollAsync(request, Proof, Sign(identity), default)).Region == "TW", "TW works only with a TW-scoped key");
        Console.WriteLine("RF trusted login passed: signed proof binding, expiry, issuer/audience, regional/company scope, idempotency, automatic bearer issuance and revocation/denial protection.");
    }

    internal static async Task CheckHttpAsync(HttpClient http)
    {
        var request = Request();
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Add("X-RF-Enrollment-Secret", Proof);
        const string path = "/api/rf-logs/mobile-enrollments/trusted";
        using (var response = await http.PostAsJsonAsync(path, request))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "HTTP requires a signed assertion");
        http.DefaultRequestHeaders.Add("X-RF-Login-Assertion", Sign(Identity(request)));
        using (var response = await http.PostAsJsonAsync(path, request))
        {
            Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "HTTP accepts verified login without portal cookies or CSRF");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("status").GetString() == "approved", "no manual approval is required for verified installation");
        }
        http.DefaultRequestHeaders.Remove("X-RF-Login-Assertion");
        using (var response = await http.PostAsJsonAsync($"/api/rf-logs/mobile-enrollments/{request.EnrollmentId}/token",
            new RfMobileRenewalRequest { Region = "IN", RenewalId = Guid.NewGuid() }))
        {
            Check(response.IsSuccessStatusCode, "HTTP automatically approved installation retrieves upload token");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var token = json.RootElement.GetProperty("uploadToken").GetString();
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "uploadId");
        form.Add(new StringContent("20"), "sessionId");
        form.Add(new ByteArrayContent("PK\u0003\u0004trusted ZIP"u8.ToArray()), "file", "verified.zip");
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.IsSuccessStatusCode, "trusted login bearer uploads through actual RF endpoint");
        http.DefaultRequestHeaders.Clear();
        Console.WriteLine("RF trusted login HTTP passed: assertion enrollment, automatic token retrieval and ZIP upload.");
    }
}
