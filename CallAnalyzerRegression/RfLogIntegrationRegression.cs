using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SignalTracker.Controllers;
using SignalTracker.Middleware;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RfLogIntegrationRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("RF integration: " + message);
    }

    internal static async Task RunAsync()
    {
        var options = new RfLogIntegrationOptions
        {
            Enabled = true, AccessKey = "test-only-key", SecretKey = "test-only-secret",
            ReportingApiKey = new string('r', 40),
            Clients = new() { ["mobile"] = new() { ApiKey = new string('u', 40), CompanyId = 7, UserId = 10 } }
        };
        Check(options.IsConfigured, "valid configuration");
        var wrapped = Options.Create(options);
        using var services = new ServiceCollection().AddSingleton<IOptions<RfLogIntegrationOptions>>(wrapped).BuildServiceProvider();
        RfLogActor? authenticatedActor = null;
        int Authorize(bool reporting, string key, string client = "mobile", ClaimsPrincipal? user = null, string? region = "IN")
        {
            var http = new DefaultHttpContext { RequestServices = services };
            if (user != null) http.User = user;
            http.Request.Headers["X-RF-Api-Key"] = key;
            http.Request.Headers["X-RF-Client-Id"] = client;
            if (region != null) http.Request.Headers["X-RF-Region"] = region;
            var action = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
            new RfLogApiKeyAttribute(reporting).OnAuthorization(action);
            authenticatedActor = http.Items[RfLogApiKeyAttribute.ClientItem] as RfLogActor;
            return action.Result switch { ForbidResult => 403, ObjectResult result => result.StatusCode ?? 0, _ => 200 };
        }
        Check(Authorize(false, new string('u', 40)) == 200, "uploader key accepted");
        Check(Authorize(true, new string('u', 40)) == 401 && Authorize(false, new string('r', 40)) == 401,
            "reporting and uploader credentials cannot cross roles");
        Check(Authorize(false, "") == 401 && Authorize(false, new string('u', 40), "other") == 401, "missing and unknown credentials denied");
        Check(Authorize(true, options.ReportingApiKey, region: null) == 400
            && Authorize(true, options.ReportingApiKey, region: "unknown") == 400
            && Authorize(true, options.ReportingApiKey, region: "TW") == 200,
            "reporting requests require an explicit valid region");
        Check(RfLogUploadStore.ConnectionStringName("IN") == "MySqlConnection"
            && RfLogUploadStore.ConnectionStringName("TW") == "MySqlConnection2", "database routing uses separate fixed regional connections");
        options.Enabled = false;
        Check(Authorize(false, new string('u', 40)) == 503, "unconfigured integration fails closed even locally");
        options.Enabled = true;
        var loggedIn = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, "user@example.invalid"), new Claim("UserId", "10"),
            new Claim("company_id", "7"), new Claim("country_code", "IN")
        }, "test-cookie"));
        Check(Authorize(false, "", user: loggedIn) == 200 && authenticatedActor == new RfLogActor("user:IN:10", 7, 10, "IN"),
            "logged-in user and company are derived from verified claims without a shared uploader key");
        Check(Authorize(true, "", user: loggedIn) == 401, "login cookies cannot access reporting service endpoints");
        Check(Authorize(false, "", user: new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, "other@example.invalid") }, "test-cookie"))) == 403,
            "email without trusted user/company claims cannot create uploads");

        var store = new MemoryStore();
        var storage = new FakeStorage();
        var service = new RfLogIntegrationService(store, storage, wrapped);
        var request = new RfLogUploadRequest { UploadId = Guid.NewGuid(), FileName = "drive.zip", FileSizeBytes = 100,
            SessionId = 20, UserId = 10, ProjectId = 30, SubSessionId = "sub-1" };
        var loginUpload = await service.RegisterAsync(new RfLogActor("user:IN:10", 7, 10, "IN"), request with { UploadId = Guid.NewGuid(), UserId = null }, default);
        Check(loginUpload.Metadata.UserId == 10 && loginUpload.CompanyId == 7, "logged-in uploads need no userId/companyId body fields");
        store.Rows.Clear();
        var upload = await service.RegisterAsync("mobile", request, default);
        Check((await service.RegisterAsync("mobile", request, default)).Id == upload.Id && store.Rows.Count == 1,
            "registration retries produce one durable upload identity");
        async Task Reject(Func<Task> action, int status)
        {
            try { await action(); throw new Exception("Expected rejection"); }
            catch (RfLogRequestException ex) { Check(ex.Status == status, "correct rejection code"); }
        }
        await Reject(async () => { await service.RegisterAsync("mobile", request with { FileSizeBytes = 101 }, default); }, 409);
        await Reject(async () => { await service.RegisterAsync("mobile", request with { UserId = 11 }, default); }, 403);
        store.Owner = false;
        await Reject(async () => { await service.RegisterAsync("mobile", request with { UploadId = Guid.NewGuid() }, default); }, 403);
        store.Owner = true;
        await Reject(async () => { await service.RegisterAsync("mobile", request with { FileName = "../other.zip" }, default); }, 400);
        await Reject(async () => { await service.DownloadAsync(upload.Id, "IN", default); }, 409);
        var missingTwStore = new RfLogUploadStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:MySqlConnection"] = "Server=unused;Database=india;" }).Build(), wrapped);
        await Reject(async () => { await missingTwStore.GetAsync(upload.Id, "TW", default); }, 503);
        await Reject(async () => { await missingTwStore.GetAsync(upload.Id, "unknown", default); }, 400);

        storage.FailVerification = true;
        try { await service.CompleteAsync("mobile", upload.Id, default); throw new Exception("Expected failed verification"); }
        catch (InvalidDataException) { }
        Check(store.Rows[("IN", upload.Id)].Status == "awaiting_upload" && storage.Copies == 0, "unverified ZIP never queues reporting");
        storage.FailVerification = false;
        var ready = await service.CompleteAsync("mobile", upload.Id, default);
        Check(ready.Status == "ready" && ready.ObjectKey!.StartsWith("rf-logs/ready/IN/7/")
            && ready.ObjectKey != ready.StagingKey && storage.Copies == 1, "verified ZIP is snapshotted before publication");
        var duplicate = await service.CompleteAsync("mobile", upload.Id, default);
        Check(duplicate.Id == ready.Id && duplicate.ObjectKey == ready.ObjectKey && storage.Copies == 1,
            "duplicate upload-complete callbacks do not copy or queue twice");
        options.Clients["other"] = new() { CompanyId = 8, ApiKey = new string('x', 40) };
        await Reject(async () => { await service.UploaderEventAsync("other", ready.Id, default); }, 404);
        await service.DownloadAsync(ready.Id, "IN", default);
        Check(storage.LastDownloadKey == ready.ObjectKey, "download URLs only address immutable accepted objects");
        await Reject(async () => { await service.GetAsync(ready.Id, "TW", default); }, 404);
        var twActor = new RfLogActor("user:TW:10", 7, 10, "TW");
        var twUpload = await service.RegisterAsync(twActor, request, default);
        var twReady = await service.CompleteAsync(twActor, twUpload.Id, default);
        Check(twReady.ObjectKey!.Contains("/TW/7/") && store.Rows.Count == 2
            && store.Rows[("IN", ready.Id)].ObjectKey == ready.ObjectKey,
            "same UUID and overlapping user/company IDs can exist independently in both regions");
        Check((await store.PendingAsync("IN", 25, default)).All(row => row.Region == "IN")
            && (await store.PendingAsync("TW", 25, default)).All(row => row.Region == "TW"), "regional queues do not mix");
        await store.ClaimAsync(ready.Id, "IN", "in-token", default);
        Check(!await store.ResultAsync(ready.Id, "TW", "in-token", new() { Status = "completed" }, default),
            "India report lease cannot update Taiwan status");
        Check(await store.ResultAsync(ready.Id, "IN", "in-token", new() { Status = "completed" }, default)
            && store.Rows[("TW", ready.Id)].Status == "ready", "report updates stay in their owning region");
        var notice = await store.ClaimNotificationAsync("TW", default);
        await store.FinishNotificationAsync(twReady.Id, "TW", notice!.Value.Token, true, default);
        Check(store.Rows[("TW", ready.Id)].NotificationStatus == "delivered"
            && store.Rows[("IN", ready.Id)].NotificationStatus == "pending", "notification state stays regional");
        Check((await store.ClaimNotificationAsync("IN", default))?.Upload.Status == "completed",
            "fast PDF completion must not suppress its upload-complete notification");

        var sdk = new RfLogS3Storage(wrapped);
        var put = new Uri(await sdk.UploadUrlAsync(upload.StagingKey));
        var get = new Uri(await sdk.DownloadUrlAsync(ready.ObjectKey!));
        Check(put.Host == "s3-noi.aces3.ai" && put.AbsolutePath.Contains("/dcra-bucket/rf-logs/staging/")
            && put.Query.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase), "SDK signs path-style Ace Cloud PUT URLs");
        Check(Uri.UnescapeDataString(put.Query).Contains($"/{options.Region}/s3/aws4_request", StringComparison.Ordinal),
            "presigned PUT credential scope uses the configured storage signing region");
        Check(get.AbsolutePath.Contains("/rf-logs/ready/") && !get.ToString().Contains(options.SecretKey), "private GET URL contains no storage secret");
        Check(RfLogIntegrationService.SignWebhook("secret", "123", "{\"eventId\":1}")
            == "e8b98957a317dec20d5def5a996a0e5707403a5d6927b62e9019a7fd932487b4",
            "webhook HMAC matches independent cross-language test vector");
        Check(RfLogIntegrationService.SignWebhook("secret", "123", "{\"eventId\":1}")
            != RfLogIntegrationService.SignWebhook("secret", "124", "{\"eventId\":1}"), "webhook signature binds timestamp and body");
        await CheckHttpAsync(options);
        await CheckAutomaticReportsAsync(options);
        Console.WriteLine("RF log integration passed: auth roles, tenant scope, idempotency, ZIP verification, immutable input, private signed URLs and webhook signatures.");
    }

    private static async Task CheckAutomaticReportsAsync(RfLogIntegrationOptions options)
    {
        byte[] zipBytes;
        using (var bytes = new MemoryStream())
        {
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            using (var csv = new StreamWriter(archive.CreateEntry("NetworkLog_20261026.csv").Open()))
            {
                csv.WriteLine("Timestamp,Latitude,Longitude,Network Type,PCI,RSRP,RSRQ,SINR,DL THPT,UL THPT,EARFCN,BAND,Alpha Long");
                csv.WriteLine("2026-10-26 10:00:00,28.6,77.2,4G,32,-85,-10,20,25,3,315,B1,Test Network");
                csv.WriteLine("2026-10-26 10:00:01,28.601,77.201,4G,32,-90,-11,18,24,2,315,B1,Test Network");
            }
            zipBytes = bytes.ToArray();
        }
        var store = new MemoryStore();
        var storage = new FakeStorage { ZipContent = zipBytes };
        var wrapped = Options.Create(options);
        var generator = new RfLogPdfGenerator(new TestEnvironment(), wrapped);
        var wifiPlots = new RecordingWifiPlotImporter();
        var processor = new RfLogReportProcessor(store, storage, generator, wifiPlots, NullLogger<RfLogReportProcessor>.Instance);
        var id = Guid.NewGuid().ToString("D");
        foreach (var region in new[] { "IN", "TW" })
            await store.RegisterAsync(new RfLogUpload
            {
                Id = id, Region = region, CompanyId = 7, ClientId = $"user:{region}:10", Status = "ready", ETag = "accepted-etag",
                ObjectKey = $"rf-logs/ready/{region}/7/{id}/input.zip",
                Metadata = new() { UploadId = Guid.Parse(id), FileName = "drive.zip", FileSizeBytes = zipBytes.Length, SessionId = 20, UserId = 10 }
            }, default);
        Check(await processor.ProcessNextAsync("IN", default), "automatic processor claims India queue");
        var india = store.Rows[("IN", id)];
        Check(india.Status == "completed", "real ZIP-to-PDF engine completes India job: " + india.Error);
        Check(india.ReportReference!.StartsWith($"rf-logs/reports/IN/7/{id}/")
            && storage.Reports[india.ReportReference].AsSpan(0, 5).SequenceEqual("%PDF-"u8), "private report artifact is a real PDF");
        Check(store.Rows[("TW", id)].Status == "ready", "India report generation cannot complete Taiwan job with same ID");
        Check(!await processor.ProcessNextAsync("IN", default), "completed automatic reports are not generated twice");
        await processor.ProcessNextAsync("TW", default);
        Check(store.Rows[("TW", id)].Status == "completed" && storage.Reports.Count == 2, "Taiwan automatic report uses its own queue/artifact");
        Check(wifiPlots.Uploads.Count == 2 && wifiPlots.Uploads[0].Region == "IN" && wifiPlots.Uploads[1].Region == "TW"
            && wifiPlots.Uploads.All(row => row.Metadata.SessionId == 20 && row.CompanyId == 7),
            "Automatic cloud reports run Wi-Fi import in the same upload session/company and region");
        var service = new RfLogIntegrationService(store, storage, wrapped);
        await service.ReportDownloadAsync(india, default);
        Check(storage.LastDownloadKey == india.ReportReference, "report download returns only accepted artifact");
        try
        {
            await service.ReportDownloadAsync(india with { ReportReference = store.Rows[("TW", id)].ReportReference }, default);
            throw new Exception("Expected cross-region report reference rejection");
        }
        catch (RfLogRequestException ex) { Check(ex.Status == 409, "report links cannot cross region/object namespace"); }

        Check(!await store.RetryReportAsync(id, "IN", default), "completed reports cannot be requeued");
        var retryId = Guid.NewGuid().ToString("D");
        await store.RegisterAsync(india with
        {
            Id = retryId, Status = "ready", ReportReference = null,
            Metadata = india.Metadata with { UploadId = Guid.Parse(retryId) }
        }, default);
        var rowLimit = options.MaxAutomaticReportRows;
        try
        {
            options.MaxAutomaticReportRows = 1;
            await processor.ProcessNextAsync("IN", default);
            Check(store.Rows[("IN", retryId)].Status == "failed" && storage.Reports.Count == 2,
                "over-limit ZIP records failure without publishing a partial PDF");
            Check(!await processor.ProcessNextAsync("IN", default), "failed reports wait for an explicit retry");
        }
        finally { options.MaxAutomaticReportRows = rowLimit; }
        Check(await store.RetryReportAsync(retryId, "IN", default), "failed report can be requeued by owner route");
        await processor.ProcessNextAsync("IN", default);
        Check(store.Rows[("IN", retryId)].Status == "completed" && storage.Reports.Count == 3,
            "retried report completes after the input limit is corrected");
        await RfLogEmailRegression.RunAsync(india, store.Rows[("TW", id)], storage, options);
        Console.WriteLine("Automatic RF reports passed: ZIP download -> existing PDF engine -> private artifact -> regional completion, deduplication and report download.");
    }

    private sealed class RecordingWifiPlotImporter : IRfLogWifiPlotImporter
    {
        public List<RfLogUpload> Uploads { get; } = new();
        public Task ImportAsync(string zipPath, RfLogUpload upload, CancellationToken ct)
        {
            Check(File.Exists(zipPath), "Cloud Wi-Fi import must receive the downloaded archive");
            Uploads.Add(upload);
            return Task.CompletedTask;
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SignalTracker";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task CheckHttpAsync(RfLogIntegrationOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:RequireCsrfHeader"] = "true", ["Security:MaxUploadBytes"] = "65536" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(RfLogIntegrationController).Assembly)
            .AddJsonOptions(json => json.JsonSerializerOptions.PropertyNamingPolicy = null);
        builder.Services.AddSingleton<IOptions<RfLogIntegrationOptions>>(Options.Create(options));
        var httpStore = new MemoryStore();
        builder.Services.AddSingleton<IRfLogUploadStore>(httpStore);
        builder.Services.AddSingleton<IRfLogEmailStore>(new RfLogEmailRegression.MemoryEmailStore(httpStore, options));
        var httpStorage = new FakeStorage();
        builder.Services.AddSingleton<IRfLogStorage>(httpStorage);
        builder.Services.AddScoped<RfLogIntegrationService>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IRfMobileCredentialStore, RfMobileUploadRegression.MemoryCredentials>();
        builder.Services.AddScoped<RfMobileCredentialService>();
        builder.Services.AddSingleton<IRfMobileEnrollmentStore,RfMobileEnrollmentRegression.MemoryEnrollments>();
        builder.Services.AddScoped<RfMobileEnrollmentService>();
        builder.Services.AddRateLimiter(o => o.AddPolicy("PublicApi", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
        builder.Services.AddScoped<RfLogErrorFilter>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestLoginHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var metadata = new { uploadId = Guid.NewGuid(), fileName = "drive.zip", fileSizeBytes = 100, sessionId = 20, projectId = 30 };
        using var anonymous = await http.PostAsJsonAsync("/api/rf-logs/uploads", metadata);
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "HTTP upload fails closed without credentials");
        var directId = Guid.NewGuid();
        var directZip = new byte[20000];
        "PK\x03\x04"u8.CopyTo(directZip);
        MultipartFormDataContent DirectForm(Guid uuid, byte[]? bytes = null)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(uuid.ToString("D")), "uploadId");
            form.Add(new StringContent("20"), "sessionId");
            form.Add(new ByteArrayContent(bytes ?? directZip), "file", "drive.zip");
            return form;
        }
        using (var form = DirectForm(directId))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "single-call ZIP upload requires authenticated identity");
        http.DefaultRequestHeaders.Add("X-Test-Login", "true");
        using var csrfDenied = await http.PostAsJsonAsync("/api/rf-logs/uploads", metadata);
        Check(csrfDenied.StatusCode == HttpStatusCode.BadRequest, "cookie-authenticated upload still requires CSRF protection");
        using (var form = DirectForm(directId))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "single-call ZIP upload requires CSRF with cookies");
        http.DefaultRequestHeaders.Add("Cookie", "st.csrf=test-csrf");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "test-csrf");
        httpStorage.FailZipUpload = true;
        using (var form = DirectForm(directId))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.InternalServerError
                && httpStore.Rows[("IN", directId.ToString("D"))].Status == "awaiting_upload", "failed single-call storage upload leaves a retryable record and never queues PDF/email");
        httpStorage.FailZipUpload = false;
        using (var form = DirectForm(directId))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
        {
            using var directResult = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.IsSuccessStatusCode && directResult.RootElement.GetProperty("uploadStatus").GetString() == "completed"
                && directResult.RootElement.GetProperty("reportStatus").GetString() == "ready"
                && directResult.RootElement.GetProperty("userId").GetInt32() == 10,
                "one multipart call above the JSON 16 KiB cap uploads ZIP and queues report with login identity");
        }
        using (var form = DirectForm(directId))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.IsSuccessStatusCode && httpStorage.UploadedZips.Count == 1, "single-call retries return accepted event without uploading/regenerating again");
        using (var form = DirectForm(directId, new byte[19999]))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.Conflict, "changed single-call metadata cannot reuse an accepted UUID");
        using (var form = DirectForm(Guid.NewGuid(), "not-zip"u8.ToArray()))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.BadRequest && httpStorage.UploadedZips.Count == 1, "invalid ZIP signature is rejected before S3 upload");
        using (var form = DirectForm(Guid.NewGuid(), new byte[70000]))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge && httpStorage.UploadedZips.Count == 1, "single-call endpoint honors configured request body size before form binding");
        httpStore.Owner = false;
        using (var form = DirectForm(Guid.NewGuid()))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
            Check(response.StatusCode == HttpStatusCode.Forbidden && httpStorage.UploadedZips.Count == 1, "single-call endpoint checks session/company ownership before S3 upload");
        httpStore.Owner = true;
        using var created = await http.PostAsJsonAsync("/api/rf-logs/uploads", metadata);
        Check(created.IsSuccessStatusCode, "logged-in HTTP upload accepts metadata without user/company fields");
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("uploadId").GetString();
        using var completed = await http.PostAsync($"/api/rf-logs/uploads/{id}/complete", null);
        Check(completed.IsSuccessStatusCode, "completion receiver accepts a verified ZIP");
        using var eventJson = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        Check(eventJson.RootElement.GetProperty("userId").GetInt32() == 10
            && eventJson.RootElement.GetProperty("companyId").GetInt32() == 7
            && eventJson.RootElement.GetProperty("uploadStatus").GetString() == "completed", "HTTP event carries trusted identity and completed status");
        using var denied = await http.GetAsync($"/api/rf-logs/events/{id}/download");
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "user cookie cannot mint reporting download links");
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Add("X-RF-Api-Key", options.ReportingApiKey);
        using var missingRegion = await http.GetAsync($"/api/rf-logs/events/{id}/download");
        Check(missingRegion.StatusCode == HttpStatusCode.BadRequest, "reporting API never guesses a database region");
        http.DefaultRequestHeaders.Add("X-RF-Region", "TW");
        using var otherRegion = await http.GetAsync($"/api/rf-logs/events/{id}/download");
        Check(otherRegion.StatusCode == HttpStatusCode.NotFound, "reporting cannot find India records through Taiwan DB");
        http.DefaultRequestHeaders.Remove("X-RF-Region");
        http.DefaultRequestHeaders.Add("X-RF-Region", "IN");
        using var download = await http.GetAsync($"/api/rf-logs/events/{id}/download");
        Check(download.IsSuccessStatusCode, "trusted reporting service can obtain private ZIP link");
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Add("X-Test-Login", "true");
        http.DefaultRequestHeaders.Add("X-Test-Region", "TW");
        http.DefaultRequestHeaders.Add("Cookie", "st.csrf=test-csrf");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "test-csrf");
        using (var form = DirectForm(directId))
        using (var response = await http.PostAsync("/api/rf-logs/upload", form))
        {
            using var directTw = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.IsSuccessStatusCode && directTw.RootElement.GetProperty("region").GetString() == "TW"
                && httpStorage.UploadedZips.Count == 2
                && httpStore.Rows[("IN", directId.ToString("D"))].ObjectKey != httpStore.Rows[("TW", directId.ToString("D"))].ObjectKey,
                "same single-call UUID in Taiwan uses only Taiwan records/private ZIP namespace");
        }
        // Reuse the same upload UUID: the Taiwan login must select a different DB.
        using var createdTw = await http.PostAsJsonAsync("/api/rf-logs/uploads", metadata);
        Check(createdTw.IsSuccessStatusCode, "Taiwan login registers independently from India");
        using var completedTw = await http.PostAsync($"/api/rf-logs/uploads/{id}/complete", null);
        using var twEvent = JsonDocument.Parse(await completedTw.Content.ReadAsStringAsync());
        Check(completedTw.IsSuccessStatusCode && twEvent.RootElement.GetProperty("region").GetString() == "TW"
            && twEvent.RootElement.GetProperty("blobPath").GetString()!.Contains("/TW/7/"), "Taiwan completion uses only Taiwan records and storage namespace");
        await RfLogEmailRegression.CheckHttpAsync(http);
        await RfMobileUploadRegression.CheckHttpAsync(http, httpStore, httpStorage);
        await RfMobileEnrollmentRegression.CheckHttpAsync(http, httpStore);
        await app.StopAsync();
        Console.WriteLine("RF HTTP contract passed: API routes, login identity, CSRF, upload-complete callback and reporting download authorization.");
    }

    private sealed class TestLoginHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Login"] != "true") return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("UserId", "10"), new Claim("company_id", "7"),
                new Claim("UserTypeId", Request.Headers["X-Test-Admin"] == "true" ? "2" : "1"),
                new Claim("country_code", Request.Headers["X-Test-Region"] == "TW" ? "TW" : "IN")
            }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    internal sealed class FakeStorage : IRfLogStorage
    {
        public byte[]? ZipContent;
        public Dictionary<string, byte[]> Reports { get; } = new();
        public Dictionary<string, byte[]> UploadedZips { get; } = new();
        public bool FailZipUpload;
        public bool FailVerification;
        public int Copies;
        public string? LastDownloadKey;
        public Task<string> UploadUrlAsync(string key) => Task.FromResult("https://storage.invalid/upload");
        public Task<string> DownloadUrlAsync(string key) { LastDownloadKey = key; return Task.FromResult("https://storage.invalid/download"); }
        public Task<RfLogObject> VerifyZipAsync(string key, long expectedSize, CancellationToken ct)
        {
            if (FailVerification) throw new InvalidDataException("size or signature mismatch");
            return Task.FromResult(new RfLogObject(expectedSize, "etag", DateTime.UtcNow));
        }
        public Task CopyAsync(string source, string target, string etag, CancellationToken ct) { Copies++; return Task.CompletedTask; }
        public Task DownloadToFileAsync(string key, string etag, long size, string path, CancellationToken ct) =>
            File.WriteAllBytesAsync(path, ZipContent ?? throw new InvalidDataException("Missing test ZIP"), ct);
        public Task UploadReportAsync(string key, byte[] pdf, CancellationToken ct) { Reports[key] = pdf; return Task.CompletedTask; }
        public async Task UploadZipAsync(string key, Stream zip, CancellationToken ct)
        {
            if (FailZipUpload) throw new IOException("Simulated storage outage");
            using var buffer = new MemoryStream();
            await zip.CopyToAsync(buffer, ct);
            UploadedZips[key] = buffer.ToArray();
        }
        public Task<byte[]> ReadReportAsync(string key, long maxBytes, CancellationToken ct) => Task.FromResult(Reports[key]);
    }

    internal sealed class MemoryStore : IRfLogUploadStore
    {
        public Dictionary<(string Region, string Id), RfLogUpload> Rows { get; } = new();
        private readonly Dictionary<(string Region, string Id), string> reportTokens = new();
        public bool Owner = true;
        public Task<bool> OwnerExistsAsync(RfLogUploadRequest request, int companyId, string region, CancellationToken ct) => Task.FromResult(Owner);
        public Task<RfLogUpload?> GetAsync(string id, string region, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault((region, id)));
        public Task RegisterAsync(RfLogUpload upload, CancellationToken ct) { Rows.TryAdd((upload.Region, upload.Id), upload); return Task.CompletedTask; }
        public Task<bool> CompleteAsync(string id, string region, string key, RfLogObject file, CancellationToken ct)
        {
            if (Rows[(region, id)].Status != "awaiting_upload") return Task.FromResult(false);
            Rows[(region, id)] = Rows[(region, id)] with { ObjectKey = key, Status = "ready", ETag = file.ETag, UploadedAtUtc = file.UploadedAtUtc };
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<RfLogUpload>> PendingAsync(string region, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RfLogUpload>>(Rows.Values.Where(row => row.Region == region && row.Status == "ready").Take(limit).ToArray());
        public Task<bool> ClaimAsync(string id, string region, string token, CancellationToken ct)
        {
            if (!Rows.TryGetValue((region, id), out var row) || row.Status != "ready") return Task.FromResult(false);
            reportTokens[(region, id)] = token;
            Rows[(region, id)] = row with { Status = "processing" };
            return Task.FromResult(true);
        }
        public Task<bool> ResultAsync(string id, string region, string token, RfLogReportResult result, CancellationToken ct)
        {
            if (reportTokens.GetValueOrDefault((region, id)) != token) return Task.FromResult(false);
            Rows[(region, id)] = Rows[(region, id)] with { Status = result.Status, ReportReference = result.ReportReference, Error = result.Error };
            return Task.FromResult(true);
        }
        public Task<(RfLogUpload Upload, string Token)?> ClaimNotificationAsync(string region, CancellationToken ct)
        {
            var row = Rows.Values.FirstOrDefault(row => row.Region == region && row.ObjectKey != null
                && row.Status is "ready" or "processing" or "completed" or "failed" && row.NotificationStatus == "pending");
            return Task.FromResult<(RfLogUpload Upload, string Token)?>(row == null ? null : (row, "notice-token"));
        }
        public Task FinishNotificationAsync(string id, string region, string token, bool success, CancellationToken ct)
        {
            Rows[(region, id)] = Rows[(region, id)] with { NotificationStatus = success ? "delivered" : "pending" };
            return Task.CompletedTask;
        }
        public Task RetryNotificationAsync(string id, string region, CancellationToken ct)
        {
            Rows[(region, id)] = Rows[(region, id)] with { NotificationStatus = "pending" };
            return Task.CompletedTask;
        }
        public Task<bool> RetryReportAsync(string id, string region, CancellationToken ct)
        {
            if (!Rows.TryGetValue((region, id), out var row) || row.Status != "failed") return Task.FromResult(false);
            Rows[(region, id)] = row with { Status = "ready", Error = null, ReportReference = null };
            reportTokens.Remove((region, id));
            return Task.FromResult(true);
        }
    }
}
