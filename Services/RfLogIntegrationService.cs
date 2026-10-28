using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class RfLogRequestException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class RfLogIntegrationService(IRfLogUploadStore store, IRfLogStorage storage,
    IOptions<RfLogIntegrationOptions> configuration)
{
    private RfLogIntegrationOptions Options => configuration.Value;

    private RfLogActor ConfiguredActor(string clientId)
    {
        var client = Options.Clients[clientId];
        return new(clientId, client.CompanyId, client.UserId, client.Region);
    }

    public Task<RfLogUpload> RegisterAsync(string clientId, RfLogUploadRequest request, CancellationToken ct) =>
        RegisterAsync(ConfiguredActor(clientId), request, ct);

    public async Task<RfLogUpload> RegisterAsync(RfLogActor actor, RfLogUploadRequest request, CancellationToken ct)
    {
        if (actor.UserId.HasValue && request.UserId.HasValue && request.UserId != actor.UserId)
            throw new RfLogRequestException(403, "User is outside the authenticated uploader scope.");
        request = request with { UserId = actor.UserId ?? request.UserId };
        if (request.UploadId == Guid.Empty || request.FileSizeBytes < 4 || request.FileSizeBytes > Options.MaxZipBytes
            || string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 180
            || request.FileName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0
            || !request.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || request.SessionId <= 0 || request.UserId is null or <= 0 || request.ProjectId is <= 0)
            throw new RfLogRequestException(400, "Supply a stable uploadId, ZIP filename, valid size, sessionId and userId.");
        if (!await store.OwnerExistsAsync(request, actor.CompanyId, actor.Region, ct))
            throw new RfLogRequestException(403, "Session, user or project is outside the configured company scope.");

        var id = request.UploadId.ToString("D");
        await store.RegisterAsync(new RfLogUpload
        {
            Id = id, ClientId = actor.Id, CompanyId = actor.CompanyId, Region = actor.Region, Metadata = request,
            StagingKey = $"rf-logs/staging/{actor.Region}/{actor.CompanyId}/{id}.zip", CreatedAtUtc = DateTime.UtcNow
        }, ct);
        var saved = await UploaderEventAsync(actor, id, ct);
        if (saved.Metadata != request)
            throw new RfLogRequestException(409, "uploadId is already registered with different metadata. Reuse the original payload or a new ID.");
        return saved;
    }

    public Task<RfLogUpload> UploaderEventAsync(string clientId, string id, CancellationToken ct) =>
        UploaderEventAsync(ConfiguredActor(clientId), id, ct);

    public async Task<RfLogUpload> UploaderEventAsync(RfLogActor actor, string id, CancellationToken ct)
    {
        var row = await GetAsync(id, actor.Region, ct);
        if (row.ClientId != actor.Id || row.CompanyId != actor.CompanyId || row.Region != actor.Region)
            throw new RfLogRequestException(404, "Upload not found.");
        return row;
    }

    public async Task<RfLogUpload> GetAsync(string id, string region, CancellationToken ct) => await store.GetAsync(id, region, ct)
        ?? throw new RfLogRequestException(404, "Upload not found.");

    public Task<RfLogUpload> CompleteAsync(string clientId, string id, CancellationToken ct) =>
        CompleteAsync(ConfiguredActor(clientId), id, ct);

    public async Task<RfLogUpload> UploadAsync(RfLogActor actor, RfLogUploadRequest metadata, Stream zip, CancellationToken ct)
    {
        var upload = await RegisterAsync(actor, metadata, ct);
        if (upload.Status != "awaiting_upload") return upload;
        // IFormFile is buffered by ASP.NET to a seekable stream, spilling large files
        // to disk rather than allocating a ZIP-sized byte array.
        if (!zip.CanSeek || zip.Length != metadata.FileSizeBytes)
            throw new RfLogRequestException(400, "A complete ZIP file is required.");
        var signature = new byte[4];
        zip.Position = 0;
        await zip.ReadExactlyAsync(signature, ct);
        if (signature[0] != 0x50 || signature[1] != 0x4b
            || !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6)))
            throw new RfLogRequestException(400, "The file must have a ZIP signature.");
        zip.Position = 0;
        // Each request writes its own private immutable key. Concurrent retries
        // cannot overwrite the input selected by the database's completion winner.
        var key = $"rf-logs/ready/{upload.Region}/{upload.CompanyId}/{upload.Id}/{Guid.NewGuid():N}.zip";
        await storage.UploadZipAsync(key, zip, ct);
        var file = await storage.VerifyZipAsync(key, metadata.FileSizeBytes, ct);
        await store.CompleteAsync(upload.Id, upload.Region, key, file, ct);
        return await UploaderEventAsync(actor, upload.Id, ct);
    }

    public async Task<RfLogUpload> CompleteAsync(RfLogActor actor, string id, CancellationToken ct)
    {
        var upload = await UploaderEventAsync(actor, id, ct);
        if (upload.Status != "awaiting_upload") return upload; // Retries return the original event.
        var file = await storage.VerifyZipAsync(upload.StagingKey, upload.Metadata.FileSizeBytes, ct);
        // The phone's PUT URL remains valid for a short time. Snapshot to a key
        // it cannot write, so subsequent PUTs cannot change an accepted report input.
        // Each contender has its own target; only the database winner is published.
        var target = $"rf-logs/ready/{upload.Region}/{upload.CompanyId}/{id}/{Guid.NewGuid():N}.zip";
        await storage.CopyAsync(upload.StagingKey, target, file.ETag, ct);
        var snapshot = await storage.VerifyZipAsync(target, file.Size, ct);
        await store.CompleteAsync(id, actor.Region, target, snapshot with { UploadedAtUtc = file.UploadedAtUtc }, ct);
        return await UploaderEventAsync(actor, id, ct);
    }

    public async Task<object> DownloadAsync(string id, string region, CancellationToken ct)
    {
        var upload = await GetAsync(id, region, ct);
        if (upload.ObjectKey == null) throw new RfLogRequestException(409, "Upload is not complete.");
        return new { eventId = upload.Id, region = upload.Region, downloadUrl = await storage.DownloadUrlAsync(upload.ObjectKey),
            expiresAtUtc = DateTime.UtcNow.AddMinutes(Options.DownloadUrlMinutes), etag = upload.ETag,
            fileSizeBytes = upload.Metadata.FileSizeBytes };
    }

    public async Task<object> ReportDownloadAsync(RfLogUpload upload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var prefix = $"rf-logs/reports/{upload.Region}/{upload.CompanyId}/{upload.Id}/";
        if (upload.Status != "completed" || upload.ReportReference == null
            || !upload.ReportReference.StartsWith(prefix, StringComparison.Ordinal)
            || upload.ReportReference.Contains("..", StringComparison.Ordinal)
            || !upload.ReportReference.EndsWith("/report.pdf", StringComparison.Ordinal))
            throw new RfLogRequestException(409, "Automatic PDF report is not available yet.");
        return new { eventId = upload.Id, region = upload.Region, contentType = "application/pdf",
            downloadUrl = await storage.DownloadUrlAsync(upload.ReportReference),
            expiresAtUtc = DateTime.UtcNow.AddMinutes(Options.DownloadUrlMinutes) };
    }

    public object Event(RfLogUpload upload) => new
    {
        schemaVersion = 1, eventId = upload.Id, eventKey = $"{upload.Region}:{upload.Id}", eventType = "rf.zip.uploaded", uploadId = upload.Id,
        uploadStatus = upload.ObjectKey == null ? "awaiting_upload" : "completed", reportStatus = upload.Status,
        fileName = upload.Metadata.FileName, fileSizeBytes = upload.Metadata.FileSizeBytes,
        bucket = Options.Bucket, blobPath = upload.ObjectKey, etag = upload.ETag,
        uploadedAtUtc = upload.UploadedAtUtc, sessionId = upload.Metadata.SessionId,
        subSessionId = upload.Metadata.SubSessionId, projectId = upload.Metadata.ProjectId,
        userId = upload.Metadata.UserId, companyId = upload.CompanyId, region = upload.Region,
        downloadEndpoint = $"/api/rf-logs/events/{upload.Id}/download",
        reportDownloadEndpoint = $"/api/rf-logs/uploads/{upload.Id}/report",
        reportReference = upload.ReportReference, error = upload.Error,
        notificationStatus = upload.NotificationStatus, notificationAttempts = upload.NotificationAttempts
    };

    public static string SignWebhook(string secret, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();
}
