using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class RfMobileUploadRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("RF mobile: " + message);
    }

    internal static async Task RunAsync()
    {
        CheckLocalOperatorArguments();
        var clock = new TestClock();
        var store = new MemoryCredentials();
        var service = new RfMobileCredentialService(store, clock);
        var admin = new RfLogActor("user:IN:1", 7, 1, "IN");
        var request = Request();
        async Task Reject(Func<Task> action, int status)
        {
            try { await action(); throw new Exception("Expected rejection"); }
            catch (RfLogRequestException ex) { Check(ex.Status == status, "rejection status"); }
        }
        await Reject(async () => { await service.IssueAsync(admin, withDays(0), default); }, 400);
        RfMobileCredentialRequest withDays(int days) => new()
        { MobileNumber = request.MobileNumber, CompanyCode = request.CompanyCode, DeviceLabel = request.DeviceLabel, ValidDays = days };
        store.Owners = [];
        await Reject(async () => { await service.IssueAsync(admin, request, default); }, 403);
        store.Owners = [new(10, 7), new(11, 7)];
        await Reject(async () => { await service.IssueAsync(admin, request, default); }, 409);
        store.Owners = [new(10, 8)];
        await Reject(async () => { await service.IssueAsync(admin, request, default); }, 403);
        store.Owners = [new(10, 7)];
        var issued = await service.IssueAsync(admin, request, default);
        Check(issued.Token.Length == 105 && issued.Credential.TokenHash != issued.Token
            && !issued.Credential.TokenHash.Contains(issued.Token.Split('.')[3]), "only a digest is persisted");
        var actor = await service.AuthenticateAsync(issued.Token, default);
        Check(actor?.UserId == 10 && actor.CompanyId == 7 && actor.Region == "IN", "identity is bound to provisioned scope");
        Check(await service.AuthenticateAsync(issued.Token.Replace(".IN.", ".TW."), default) == null, "region tampering rejected");
        Check(await service.AuthenticateAsync(issued.Token[..^1] + (issued.Token[^1] == '0' ? '1' : '0'), default) == null, "secret tampering rejected");
        foreach (var invalid in new[] { "", "Bearer " + issued.Token, new string('x', 2000), issued.Token[..10] })
            Check(await service.AuthenticateAsync(invalid, default) == null, "malformed credentials rejected");
        var safe = JsonSerializer.Serialize(RfMobileCredentialService.Metadata(issued.Credential));
        Check(!safe.Contains(issued.Token) && !safe.Contains(issued.Credential.TokenHash), "metadata does not expose token/hash");
        store.ActiveOwner = false;
        Check(await service.AuthenticateAsync(issued.Token, default) == null, "disabled/reassigned owner loses access");
        store.ActiveOwner = true;
        clock.Now = issued.Credential.ExpiresAtUtc;
        Check(await service.AuthenticateAsync(issued.Token, default) == null, "expired credential rejected at boundary");
        clock.Now = issued.Credential.CreatedAtUtc;
        Check(!await store.RevokeAsync("IN", 8, issued.Credential.Id, default), "cross-company revocation denied");
        await store.RevokeAsync("IN", 7, issued.Credential.Id, default);
        Check(await service.AuthenticateAsync(issued.Token, default) == null, "revocation takes effect without restart");
        Console.WriteLine("RF mobile credentials passed: scoped issuance, tamper rejection, expiration, revocation and secret-safe metadata.");
    }

    private static void CheckLocalOperatorArguments()
    {
        var values = new Dictionary<string, string?>
        {
            ["rf-mobile-number"] = "9000000000", ["rf-mobile-company-code"] = "TEST-COMPANY",
            ["rf-mobile-device-label"] = "Test installation", ["rf-mobile-user-id"] = "10"
        };
        var request = RfMobileCredentialConsole.ReadRequest(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Check(request.UserId == 10 && request.ValidDays == 90, "local provisioning accepts identifiers without portal credentials");
        foreach (var (key, badValue) in new[]
        {
            ("rf-mobile-user-id", "0"), ("rf-mobile-user-id", "-1"), ("rf-mobile-user-id", "invalid"),
            ("rf-mobile-valid-days", "366"), ("rf-mobile-valid-days", "0"), ("rf-mobile-valid-days", "9999999999999"),
            ("rf-mobile-number", ""), ("rf-mobile-company-code", ""), ("rf-mobile-device-label", "")
        })
        {
            var invalid = new Dictionary<string, string?>(values) { [key] = badValue };
            try
            {
                RfMobileCredentialConsole.ReadRequest(new ConfigurationBuilder().AddInMemoryCollection(invalid).Build());
                throw new Exception("Expected invalid local provisioning arguments to fail.");
            }
            catch (RfLogRequestException ex) { Check(ex.Status == 400, "invalid local arguments rejected before database access"); }
        }
        Console.WriteLine("RF local operator argument validation passed; no email/password fields required.");
    }

    internal static async Task CheckHttpAsync(HttpClient http, RfLogIntegrationRegression.MemoryStore uploads,
        RfLogIntegrationRegression.FakeStorage storage)
    {
        http.DefaultRequestHeaders.Clear();
        using (var response = await http.PostAsJsonAsync("/api/rf-logs/mobile-credentials", Request()))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous phone/code cannot mint credentials");
        http.DefaultRequestHeaders.Add("X-Test-Login", "true");
        http.DefaultRequestHeaders.Add("Cookie", "st.csrf=test-csrf");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "test-csrf");
        using (var response = await http.PostAsJsonAsync("/api/rf-logs/mobile-credentials", Request()))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin cannot provision installation credentials");
        http.DefaultRequestHeaders.Add("X-Test-Admin", "true");
        http.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using (var response = await http.PostAsJsonAsync("/api/rf-logs/mobile-credentials", Request()))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "administrator issuance still requires CSRF");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "test-csrf");
        string token;
        string credentialId;
        using (var response = await http.PostAsJsonAsync("/api/rf-logs/mobile-credentials", Request()))
        {
            Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "admin provisioning is not cacheable");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            token = body.RootElement.GetProperty("uploadToken").GetString()!;
            credentialId = body.RootElement.GetProperty("credential").GetProperty("credentialId").GetString()!;
        }
        using (var response = await http.GetAsync("/api/rf-logs/mobile-credentials"))
        {
            var json = await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode && !json.Contains(token) && !json.Contains("TokenHash"), "list cannot recover raw credentials");
        }
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var id = Guid.NewGuid();
        MultipartFormDataContent Form(int? user = null)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(id.ToString("D")), "uploadId");
            form.Add(new StringContent("20"), "sessionId");
            if (user.HasValue) form.Add(new StringContent(user.ToString()!), "userId");
            form.Add(new ByteArrayContent("PK\x03\x04test mobile zip"u8.ToArray()), "file", "mobile.zip");
            return form;
        }
        var previousWrites = storage.UploadedZips.Count;
        using (var form = Form(11))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "bearer cannot impersonate another user");
        uploads.Owner = false;
        using (var form = Form())
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.Forbidden && storage.UploadedZips.Count == previousWrites, "ownership rejection precedes storage writes");
        uploads.Owner = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var form = Form();
            using var response = await http.PostAsync("/api/rf-logs/upload", form);
            Check(response.IsSuccessStatusCode, "single-call mobile upload/retry succeeds without portal cookies or CSRF");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("userId").GetInt32() == 10
                && body.RootElement.GetProperty("companyId").GetInt32() == 7
                && body.RootElement.GetProperty("uploadStatus").GetString() == "completed", "mobile scope drives upload identity");
        }
        Check(storage.UploadedZips.Count == previousWrites + 1, "mobile retry uploads only once");
        using (var response = await http.GetAsync($"/api/rf-logs/uploads/{id}"))
            Check(response.IsSuccessStatusCode, "mobile can poll its own event");
        http.DefaultRequestHeaders.Add("x-country-code", "TW");
        using (var response = await http.GetAsync($"/api/rf-logs/uploads/{id}?region=TW"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.IsSuccessStatusCode && body.RootElement.GetProperty("region").GetString() == "IN",
                "untrusted request region cannot override the credential's database scope");
        }
        http.DefaultRequestHeaders.Remove("x-country-code");
        http.DefaultRequestHeaders.Add("X-Test-Login", "true");
        using (var form = Form())
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "bearer header does not bypass cookie CSRF");
        http.DefaultRequestHeaders.Remove("X-Test-Login");
        using (var response = await http.GetAsync("/api/rf-logs/events"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "mobile cannot access reporting service endpoints");
        using (var response = await http.PostAsJsonAsync("/api/rf-logs/mobile-credentials", Request()))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "mobile cannot mint more credentials");
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Add("X-Test-Login", "true");
        http.DefaultRequestHeaders.Add("X-Test-Admin", "true");
        http.DefaultRequestHeaders.Add("Cookie", "st.csrf=test-csrf");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "test-csrf");
        using (var response = await http.DeleteAsync($"/api/rf-logs/mobile-credentials/{credentialId}"))
            Check(response.StatusCode == HttpStatusCode.NoContent, "administrator revokes mobile credential");
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var response = await http.GetAsync($"/api/rf-logs/uploads/{id}"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "revoked credential rejected by actual uploader route");
        http.DefaultRequestHeaders.Clear();
        Console.WriteLine("RF mobile HTTP passed: admin-only provisioning, cookieless ZIP upload, ownership enforcement, deduplication, role isolation and revocation.");
    }

    private static RfMobileCredentialRequest Request() => new()
    { MobileNumber = "9000000000", CompanyCode = "TEST-COMPANY", DeviceLabel = "Test installation" };

    private sealed class TestClock : TimeProvider
    {
        public DateTime Now = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    internal sealed class MemoryCredentials : IRfMobileCredentialStore
    {
        internal List<RfMobileOwner> Owners = [new(10, 7)];
        internal bool ActiveOwner = true;
        private readonly Dictionary<(string Region, string Id), RfMobileCredential> rows = new();
        public Task<IReadOnlyList<RfMobileOwner>> FindOwnersAsync(string region, int companyId, RfMobileCredentialRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<RfMobileOwner>>(Owners);
        public Task InsertAsync(RfMobileCredential credential, CancellationToken ct)
        { rows.Add((credential.Region, credential.Id), credential); return Task.CompletedTask; }
        public Task<RfMobileCredential?> FindActiveAsync(string region, string id, CancellationToken ct) => Task.FromResult(ActiveOwner ? rows.GetValueOrDefault((region, id)) : null);
        public Task<IReadOnlyList<RfMobileCredential>> ListAsync(string region, int companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RfMobileCredential>>(rows.Values.Where(c => c.Region == region && c.CompanyId == companyId).ToArray());
        public Task<bool> RevokeAsync(string region, int companyId, string id, CancellationToken ct)
        {
            if (!rows.TryGetValue((region, id), out var row) || row.CompanyId != companyId) return Task.FromResult(false);
            rows[(region, id)] = row with { RevokedAtUtc = DateTime.UtcNow };
            return Task.FromResult(true);
        }
    }
}
