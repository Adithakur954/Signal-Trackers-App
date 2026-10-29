using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

[ApiController, AllowAnonymous, Route("api/rf-logs"), ServiceFilter(typeof(RfLogErrorFilter))]
[RequestSizeLimit(16384)]
public sealed class RfLogIntegrationController(RfLogIntegrationService service, IRfLogUploadStore store,
    IRfLogStorage storage, IOptions<RfLogIntegrationOptions> options) : ControllerBase
{
    private RfLogActor Actor => (RfLogActor)HttpContext.Items[RfLogApiKeyAttribute.ClientItem]!;
    private string ReportingRegion => (string)HttpContext.Items[RfLogApiKeyAttribute.RegionItem]!;

    [HttpPost("upload"), RfLogUploader, Consumes("multipart/form-data")]
    [RequestSizeLimit(524_288_000), RfLogDirectUploadLimits]
    public async Task<IActionResult> Upload([FromForm] RfLogDirectUploadRequest request, CancellationToken ct)
    {
        if (request.File.Length > options.Value.MaxZipBytes)
            throw new RfLogRequestException(413, "ZIP exceeds the configured RF upload size limit.");
        await using var zip = request.File.OpenReadStream();
        return Ok(service.Event(await service.UploadAsync(Actor, new RfLogUploadRequest
        {
            UploadId = request.UploadId, SessionId = request.SessionId, FileName = request.File.FileName,
            FileSizeBytes = request.File.Length, SubSessionId = request.SubSessionId,
            ProjectId = request.ProjectId, UserId = request.UserId
        }, zip, ct)));
    }

    [HttpPost("uploads"), RfLogUploader]
    public async Task<IActionResult> Create(RfLogUploadRequest request, CancellationToken ct)
    {
        var upload = await service.RegisterAsync(Actor, request, ct);
        return Ok(new
        {
            uploadId = upload.Id, eventId = upload.Id, eventKey = $"{upload.Region}:{upload.Id}",
            region = upload.Region, status = upload.Status, method = "PUT",
            uploadUrl = upload.Status == "awaiting_upload" ? await storage.UploadUrlAsync(upload.StagingKey) : null,
            requiredHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/zip" },
            expiresAtUtc = DateTime.UtcNow.AddMinutes(options.Value.UploadUrlMinutes),
            completeEndpoint = $"/api/rf-logs/uploads/{upload.Id}/complete"
        });
    }

    [HttpPost("uploads/{id:guid}/complete"), RfLogUploader]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) =>
        Ok(service.Event(await service.CompleteAsync(Actor, id.ToString("D"), ct)));

    [HttpGet("uploads/{id:guid}"), RfLogUploader]
    public async Task<IActionResult> UploadStatus(Guid id, CancellationToken ct) =>
        Ok(service.Event(await service.UploaderEventAsync(Actor, id.ToString("D"), ct)));

    [HttpGet("uploads/{id:guid}/report"), RfLogUploader]
    public async Task<IActionResult> UserReport(Guid id, CancellationToken ct) =>
        Ok(await service.ReportDownloadAsync(await service.UploaderEventAsync(Actor, id.ToString("D"), ct), ct));

    [HttpPost("uploads/{id:guid}/report-retry"), RfLogUploader]
    public async Task<IActionResult> UserRetryReport(Guid id, CancellationToken ct)
    {
        await service.UploaderEventAsync(Actor, id.ToString("D"), ct);
        if (!await store.RetryReportAsync(id.ToString("D"), Actor.Region, ct)) return Conflict(new { message = "Only a failed report can be retried." });
        return Ok(service.Event(await service.UploaderEventAsync(Actor, id.ToString("D"), ct)));
    }

    [HttpGet("events"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> Pending([FromQuery] int limit = 25, CancellationToken ct = default) =>
        Ok(new { region = ReportingRegion, events = (await store.PendingAsync(ReportingRegion, limit, ct)).Select(service.Event) });

    [HttpGet("events/{id:guid}"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> EventStatus(Guid id, CancellationToken ct) =>
        Ok(service.Event(await service.GetAsync(id.ToString("D"), ReportingRegion, ct)));

    [HttpGet("events/{id:guid}/download"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct) => Ok(await service.DownloadAsync(id.ToString("D"), ReportingRegion, ct));

    [HttpGet("events/{id:guid}/report"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct) =>
        Ok(await service.ReportDownloadAsync(await service.GetAsync(id.ToString("D"), ReportingRegion, ct), ct));

    [HttpPost("events/{id:guid}/claim"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> Claim(Guid id, CancellationToken ct)
    {
        await service.GetAsync(id.ToString("D"), ReportingRegion, ct);
        var token = Guid.NewGuid().ToString("N");
        if (!await store.ClaimAsync(id.ToString("D"), ReportingRegion, token, ct)) return Conflict(new { message = "Event is already claimed or terminal." });
        return Ok(new { eventId = id, region = ReportingRegion, leaseToken = token, leaseSeconds = 900 });
    }

    [HttpPost("events/{id:guid}/status"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> Result(Guid id, RfLogReportResult result, CancellationToken ct)
    {
        var token = Request.Headers["X-RF-Lease-Token"].ToString();
        if (!Guid.TryParseExact(token, "N", out _)) return BadRequest(new { message = "X-RF-Lease-Token is required." });
        if (!await store.ResultAsync(id.ToString("D"), ReportingRegion, token, result, ct)) return Conflict(new { message = "Lease expired, replaced or event is terminal." });
        return Ok(service.Event(await service.GetAsync(id.ToString("D"), ReportingRegion, ct)));
    }

    [HttpPost("events/{id:guid}/notification-retry"), RfLogApiKey(reporting: true)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        await service.GetAsync(id.ToString("D"), ReportingRegion, ct);
        await store.RetryNotificationAsync(id.ToString("D"), ReportingRegion, ct);
        return Ok(service.Event(await service.GetAsync(id.ToString("D"), ReportingRegion, ct)));
    }
}

// Runs before form model binding. Honor the existing configured HTTP body cap,
// including multipart overhead, while overriding this controller's 16 KiB JSON cap.
public sealed class RfLogDirectUploadLimitsAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var configured = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue<long?>("Security:MaxUploadBytes") ?? 104_857_600L;
        var limit = configured is > 0 and <= 524_288_000L ? configured : 104_857_600L;
        var feature = context.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        if (context.HttpContext.Request.ContentLength > limit)
        {
            // The oversized body is deliberately not read; do not reuse its HTTP/1
            // connection while Kestrel closes/drains the rejected request.
            if (context.HttpContext.Request.Protocol.StartsWith("HTTP/1.", StringComparison.Ordinal))
                context.HttpContext.Response.Headers.Connection = "close";
            context.Result = new ObjectResult(new { message = "Request exceeds Security:MaxUploadBytes, including multipart overhead." }) { StatusCode = 413 };
        }
    }
    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

public sealed class RfLogErrorFilter(ILogger<RfLogErrorFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, message) = context.Exception switch
        {
            RfLogRequestException ex => (ex.Status, ex.Message),
            InvalidDataException ex => (409, ex.Message),
            AmazonS3Exception ex when (int)ex.StatusCode == 404 => (409, "ZIP is not available in storage yet. Retry after the PUT completes."),
            AmazonS3Exception ex when (int)ex.StatusCode == 412 => (409, "ZIP changed during verification. Retry completion."),
            AmazonS3Exception => (503, "RF object storage is temporarily unavailable."),
            MySqlException => (503, "RF event database is unavailable. Verify configuration and schema migration."),
            _ => (500, "RF integration request failed.")
        };
        if (status >= 500) logger.LogError(context.Exception, "RF log integration request failed: {Path}", context.HttpContext.Request.Path);
        context.Result = new ObjectResult(new { message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
