using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class RfMobileEnrollmentRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("RF auto enrollment: " + message); }
    private static RfMobileEnrollmentRequest Request(string region = "IN") => new()
    {
        EnrollmentId = Guid.NewGuid(), Region = region, UserId = 10, MobileNumber = "9000000000",
        CompanyCode = "TEST-COMPANY", DeviceLabel = "Automatic enrollment test"
    };
    private static async Task Reject(Func<Task> action, int status)
    {
        try { await action(); throw new Exception("Expected rejection"); }
        catch (RfLogRequestException ex) { Check(ex.Status == status, "expected rejection code " + status); }
    }
    private static string Token(object response) => JsonSerializer.SerializeToElement(response).GetProperty("uploadToken").GetString()!;
    internal static async Task RunAsync()
    {
        var clock = new TestClock();
        var credentials = new RfMobileUploadRegression.MemoryCredentials();
        var store = new MemoryEnrollments(credentials);
        var service = new RfMobileEnrollmentService(store, clock);
        var auth = new RfMobileCredentialService(credentials, clock);
        var request = Request(); var proof = new string('a', 64);
        var admin = new RfLogActor("admin", 7, 1, "IN");
        await Reject(async () => { await service.RequestAsync(request, "short", default); }, 400);
        var pending = await service.RequestAsync(request, proof, default);
        Check(pending.Status == "pending" && credentials.rows.Count == 0, "request alone never grants upload access");
        Check((await service.RequestAsync(request, proof, default)).Id == pending.Id && store.Rows.Count == 1, "creation retries reuse enrollment");
        await Reject(async () => { await service.RequestAsync(request, new string('b',64), default); }, 404);
        await Reject(async () => { await service.StatusAsync("TW", request.EnrollmentId, proof, default); }, 404);
        var renewal = new RfMobileRenewalRequest { Region = "IN", RenewalId = Guid.NewGuid() };
        await Reject(async () => { await service.TokenAsync(request.EnrollmentId, renewal, proof, default); }, 403);
        await Reject(async () => { await service.DecideAsync(admin with { CompanyId = 8 }, request.EnrollmentId, "approve", default); }, 404);
        await service.DecideAsync(admin, request.EnrollmentId, "approve", default);
        var first = Token(await service.TokenAsync(request.EnrollmentId, renewal, proof, default));
        var actor = await auth.AuthenticateAsync(first, default);
        Check(actor?.UserId == 10 && actor.CompanyId == 7, "app automatically obtains a scoped usable token after approval");
        var expiry = credentials.rows[("IN", pending.Id)].ExpiresAtUtc;
        Check(Token(await service.TokenAsync(request.EnrollmentId, renewal, proof, default)) == first
            && credentials.rows[("IN", pending.Id)].ExpiresAtUtc == expiry, "lost response retry returns same token without extending expiry");
        await Reject(async () => { await service.TokenAsync(request.EnrollmentId, renewal, new string('b',64), default); }, 404);
        clock.Now = clock.Now.AddDays(31);
        Check(await auth.AuthenticateAsync(first, default) == null, "access token expires independently of approval");
        await Reject(async () => { await service.TokenAsync(request.EnrollmentId, renewal, proof, default); }, 401);
        renewal.PreviousRenewalId = renewal.RenewalId;
        renewal.RenewalId = Guid.NewGuid();
        var next = Token(await service.TokenAsync(request.EnrollmentId, renewal, proof, default));
        Check(first != next && (await auth.AuthenticateAsync(next, default))?.Id == actor?.Id,
            "renewal works after access expiry and keeps upload actor stable");
        credentials.ActiveOwner = false;
        await Reject(async () => { await service.TokenAsync(request.EnrollmentId, renewal, proof, default); }, 403);
        credentials.ActiveOwner = true;
        await credentials.RevokeAsync("IN", 7, pending.Id, default);
        renewal.RenewalId = Guid.NewGuid();
        await Reject(async () => { await service.TokenAsync(request.EnrollmentId, renewal, proof, default); }, 401);
        var other = Request(); var second = await service.RequestAsync(other, proof, default);
        await service.DecideAsync(admin, other.EnrollmentId, "approve", default);
        var otherRenewal = new RfMobileRenewalRequest { Region = "IN", RenewalId = Guid.NewGuid() };
        await service.TokenAsync(other.EnrollmentId, otherRenewal, proof, default);
        await service.DecideAsync(admin, other.EnrollmentId, "deny", default);
        Check(credentials.rows[("IN", second.Id)].RevokedAtUtc != null, "denying an approved device revokes existing bearer too");
        await Reject(async () => { await service.TokenAsync(other.EnrollmentId, renewal, proof, default); }, 403);
        var late = Request(); await service.RequestAsync(late, proof, default);
        clock.Now = clock.Now.AddHours(49);
        await Reject(async () => { await service.DecideAsync(admin, late.EnrollmentId, "approve", default); }, 409);
        var expired = Request(); await service.RequestAsync(expired, proof, default);
        var approved = await service.DecideAsync(admin, expired.EnrollmentId, "approve", default);
        clock.Now = approved.ApprovalExpiresAtUtc!.Value;
        await Reject(async () => { await service.TokenAsync(expired.EnrollmentId, renewal, proof, default); }, 403);
        // Concurrent workers may propose different rotations. Exactly one wins;
        // retrying the stale request must not silently roll access back.
        clock.Now = clock.Now.AddSeconds(-1);
        var concurrentRequest = Request();
        await service.RequestAsync(concurrentRequest, proof, default);
        await service.DecideAsync(admin, concurrentRequest.EnrollmentId, "approve", default);
        var a = new RfMobileRenewalRequest { Region="IN", RenewalId=Guid.NewGuid() };
        var b = new RfMobileRenewalRequest { Region="IN", RenewalId=Guid.NewGuid() };
        async Task<bool> Rotate(RfMobileRenewalRequest r)
        {
            try { await service.TokenAsync(concurrentRequest.EnrollmentId,r,proof,default);return true; }
            catch (RfLogRequestException ex) when (ex.Status==409) {return false;}
        }
        var races=await Task.WhenAll(Rotate(a),Rotate(b));
        Check(races.Count(success=>success)==1,"concurrent different renewals cannot overwrite each other");
        var winning=races[0]?a:b;
        var responseRetries=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>service.TokenAsync(concurrentRequest.EnrollmentId,winning,proof,default)));
        Check(responseRetries.Select(Token).Distinct().Count()==1,"parallel retries of the latest renewal recover one stable token");
        var safe = JsonSerializer.Serialize(service.Metadata(pending));
        Check(!safe.Contains(proof) && !safe.Contains(pending.ProofHash), "metadata reveals neither installation secret nor full proof digest");
        Console.WriteLine("RF automatic enrollment passed: approval gate, proof/region isolation, response recovery, renewal, revocation, expired requests and absolute approval expiry.");
    }

    internal static async Task CheckHttpAsync(HttpClient http, RfLogIntegrationRegression.MemoryStore uploads)
    {
        http.DefaultRequestHeaders.Clear();
        var request = Request(); var proof = new string('d',64);
        http.DefaultRequestHeaders.Add("X-RF-Enrollment-Secret", proof);
        using (var created = await http.PostAsJsonAsync("/api/rf-logs/mobile-enrollments", request))
        {
            Check(created.StatusCode == HttpStatusCode.Accepted && created.Headers.CacheControl?.NoStore == true, "native app creates an uncached pending request without portal login");
            Check(!(await created.Content.ReadAsStringAsync()).Contains("uploadToken"), "public registration does not return access credentials");
        }
        var path = $"/api/rf-logs/mobile-enrollments/{request.EnrollmentId}";
        var renewal = new RfMobileRenewalRequest { Region = "IN", RenewalId = Guid.NewGuid() };
        using (var status = await http.GetAsync(path+"?region=IN")) Check(status.IsSuccessStatusCode, "proof-authorized status polling works");
        using (var response = await http.PostAsJsonAsync(path+"/token", renewal)) Check(response.StatusCode == HttpStatusCode.Forbidden, "pending devices cannot obtain tokens");
        using (var response = await http.PostAsJsonAsync($"/api/rf-logs/mobile-enrollment-requests/{request.EnrollmentId}/decision", new { decision="approve" }))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "device cannot approve itself");
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Add("X-Test-Login","true");http.DefaultRequestHeaders.Add("X-Test-Admin","true");
        using (var response = await http.PostAsJsonAsync($"/api/rf-logs/mobile-enrollment-requests/{request.EnrollmentId}/decision", new { decision="approve" }))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "approval preserves administrator CSRF requirement");
        http.DefaultRequestHeaders.Add("Cookie","st.csrf=test-csrf");http.DefaultRequestHeaders.Add("X-CSRF-TOKEN","test-csrf");
        using (var response = await http.GetAsync("/api/rf-logs/mobile-enrollment-requests")) Check(response.IsSuccessStatusCode, "administrator can review company requests");
        using (var response = await http.PostAsJsonAsync($"/api/rf-logs/mobile-enrollment-requests/{request.EnrollmentId}/decision", new { decision="approve" }))
            Check(response.IsSuccessStatusCode, "administrator approves recognized device");
        http.DefaultRequestHeaders.Clear();http.DefaultRequestHeaders.Add("X-RF-Enrollment-Secret",proof);
        string first;
        using (var response = await http.PostAsJsonAsync(path+"/token",renewal))
        {
            Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore==true,"app retrieves bearer automatically without portal cookies");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());first=json.RootElement.GetProperty("uploadToken").GetString()!;
        }
        var uploadId=Guid.NewGuid();
        http.DefaultRequestHeaders.Clear();http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",first);
        using (var form=new MultipartFormDataContent())
        {
            form.Add(new StringContent(uploadId.ToString()),"uploadId");form.Add(new StringContent("20"),"sessionId");
            form.Add(new ByteArrayContent("PK\u0003\u0004auto enrollment ZIP"u8.ToArray()),"file","auto.zip");
            using var response=await http.PostAsync("/api/rf-logs/upload",form);
            Check(response.IsSuccessStatusCode,"automatically issued token uploads ZIP through actual RF endpoint (HTTP " + (int)response.StatusCode + ")");
        }
        http.DefaultRequestHeaders.Clear();http.DefaultRequestHeaders.Add("X-RF-Enrollment-Secret",proof);
        renewal.PreviousRenewalId=renewal.RenewalId;renewal.RenewalId=Guid.NewGuid();string second;
        using (var response=await http.PostAsJsonAsync(path+"/token",renewal))
        {
            Check(response.IsSuccessStatusCode,"automatic renewal succeeds");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());second=json.RootElement.GetProperty("uploadToken").GetString()!;
        }
        http.DefaultRequestHeaders.Clear();http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",first);
        using (var response=await http.GetAsync($"/api/rf-logs/uploads/{uploadId}")) Check(response.StatusCode==HttpStatusCode.Unauthorized,"old token is invalidated after renewal");
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",second);
        using (var response=await http.GetAsync($"/api/rf-logs/uploads/{uploadId}")) Check(response.IsSuccessStatusCode,"renewed token retains access to prior uploads");
        using (var response=await http.GetAsync("/api/rf-logs/mobile-enrollment-requests")) Check(response.StatusCode==HttpStatusCode.Unauthorized,"mobile bearer cannot manage device approvals");
        http.DefaultRequestHeaders.Clear();http.DefaultRequestHeaders.Add("X-Test-Login","true");http.DefaultRequestHeaders.Add("X-Test-Admin","true");
        http.DefaultRequestHeaders.Add("Cookie","st.csrf=test-csrf");http.DefaultRequestHeaders.Add("X-CSRF-TOKEN","test-csrf");
        using (var response=await http.DeleteAsync($"/api/rf-logs/mobile-credentials/{request.EnrollmentId}")) Check(response.StatusCode==HttpStatusCode.NoContent,"existing credential revocation applies to enrolled devices");
        http.DefaultRequestHeaders.Clear();http.DefaultRequestHeaders.Add("X-RF-Enrollment-Secret",proof);renewal.RenewalId=Guid.NewGuid();
        using (var response=await http.PostAsJsonAsync(path+"/token",renewal)) Check(response.StatusCode==HttpStatusCode.Unauthorized,"refresh cannot revive a revoked device");
        http.DefaultRequestHeaders.Clear();
        Console.WriteLine("RF automatic enrollment HTTP passed: native registration/polling, administrator approval, token retrieval, actual upload, refresh continuity and revocation.");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTime Now=new(2026,10,30,0,0,0,DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow()=>new(Now);
    }

    internal sealed class MemoryEnrollments(IRfMobileCredentialStore credentialStore) : IRfMobileEnrollmentStore
    {
        internal readonly Dictionary<(string Region,string Id),RfMobileEnrollment> Rows=new();
        private readonly RfMobileUploadRegression.MemoryCredentials credentials=(RfMobileUploadRegression.MemoryCredentials)credentialStore;
        private readonly SemaphoreSlim gate=new(1,1);
        public Task<RfMobileOwner?> FindOwnerAsync(RfMobileEnrollmentRequest request,CancellationToken ct)=>
            Task.FromResult<RfMobileOwner?>(credentials.ActiveOwner && request.UserId==10 && request.CompanyCode.Trim()=="TEST-COMPANY" && request.MobileNumber.Trim()=="9000000000" ? new(10,7):null);
        public Task<RfMobileEnrollment> CreateAsync(RfMobileEnrollment row,CancellationToken ct)
        {
            if (Rows.TryGetValue((row.Region,row.Id),out var existing)) return Task.FromResult(existing);
            if (credentials.rows.ContainsKey((row.Region,row.Id))) throw new RfLogRequestException(409,"Credential ID collision");
            Rows.Add((row.Region,row.Id),row);return Task.FromResult(row);
        }
        public Task<RfMobileEnrollment?> GetAsync(string region,string id,CancellationToken ct)=>Task.FromResult(Rows.GetValueOrDefault((region,id)));
        public Task<IReadOnlyList<RfMobileEnrollment>> ListAsync(string region,int companyId,CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<RfMobileEnrollment>>(Rows.Values.Where(e=>e.Region==region && e.CompanyId==companyId).ToArray());
        public async Task<RfEnrollmentMutation> MutateAsync(string region,string id,Func<RfMobileEnrollment,RfMobileCredential?,RfEnrollmentMutation> change,CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!Rows.TryGetValue((region,id),out var row)) throw new RfLogRequestException(404,"Enrollment not found");
                var mutation=change(row with { OwnerActive=credentials.ActiveOwner },credentials.rows.GetValueOrDefault((region,id)));
                Rows[(region,id)]=mutation.Enrollment;
                if (mutation.Credential!=null) credentials.rows[(region,id)]=mutation.Credential;
                return mutation;
            }
            finally {gate.Release();}
        }
    }
}
