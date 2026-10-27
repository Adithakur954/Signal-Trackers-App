using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SignalTracker.Models;

public sealed class RfLogIntegrationOptions
{
    public const string Section = "RfLogIntegration";
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "https://s3-noi.aces3.ai";
    public string Bucket { get; set; } = "dcra-bucket";
    public string Region { get; set; } = "ap-south-noi-1";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool ForcePathStyle { get; set; } = true;
    public long MaxZipBytes { get; set; } = 1073741824;
    public int UploadUrlMinutes { get; set; } = 30;
    public int DownloadUrlMinutes { get; set; } = 10;
    public string ReportingApiKey { get; set; } = "";
    public string? ReportingWebhookUrl { get; set; }
    public string WebhookSigningSecret { get; set; } = "";
    public int MaxNotificationAttempts { get; set; } = 12;
    public bool AutomaticReportsEnabled { get; set; } = true;
    public long MaxExpandedZipBytes { get; set; } = 2147483648;
    public int MaxAutomaticReportRows { get; set; } = 250000;
    public bool ReportEmailsEnabled { get; set; } = true;
    public int MaxEmailAttempts { get; set; } = 12;
    public long MaxEmailAttachmentBytes { get; set; } = 20971520;
    public Dictionary<string, RfLogSmtpOptions> Smtp { get; set; } = new();
    public Dictionary<string, RfLogClient> Clients { get; set; } = new();

    public bool IsConfigured => Enabled && Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(Region)
        && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey)
        && MaxZipBytes is > 0 and <= 5368709120 && UploadUrlMinutes is >= 1 and <= 60
        && DownloadUrlMinutes is >= 1 and <= 60 && MaxNotificationAttempts is >= 1 and <= 100
        && MaxExpandedZipBytes > 0 && MaxAutomaticReportRows > 0
        && MaxEmailAttempts is >= 1 and <= 100 && MaxEmailAttachmentBytes is > 0 and <= 104857600;
}

public sealed class RfLogSmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}

public sealed record RfLogRecipientRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";
    [StringLength(100)] public string Name { get; init; } = "";
}

public sealed class RfLogRecipient
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("companyDefault")] public bool CompanyDefault { get; set; }
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
}

public sealed class RfLogEmailDelivery
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("uploadId")] public string UploadId { get; set; } = "";
    [JsonPropertyName("recipientId")] public string RecipientId { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("companyId")] public int CompanyId { get; set; }
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("reportReference")] public string ReportReference { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("sentAtUtc")] public DateTime? SentAtUtc { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed record RfLogEmailClaim(RfLogEmailDelivery Delivery, string Token);

public sealed class RfLogClient
{
    public string ApiKey { get; set; } = "";
    public int CompanyId { get; set; }
    public int? UserId { get; set; }
    public string Region { get; set; } = "IN";
}

public sealed record RfLogActor(string Id, int CompanyId, int? UserId, string Region);

public sealed record RfLogUploadRequest
{
    // Generate once on the app and reuse across every retry of this upload.
    public Guid UploadId { get; init; }
    [Required, StringLength(180)] public string FileName { get; init; } = "";
    [Range(1, 5368709120)] public long FileSizeBytes { get; init; }
    [Range(1, int.MaxValue)] public int SessionId { get; init; }
    [StringLength(100)] public string? SubSessionId { get; init; }
    [Range(1, int.MaxValue)] public int? ProjectId { get; init; }
    [Range(1, int.MaxValue)] public int? UserId { get; init; }
}

public sealed record RfLogReportResult
{
    [Required, RegularExpression("^(processing|completed|failed)$")] public string Status { get; init; } = "";
    [StringLength(2048)] public string? ReportReference { get; init; }
    [StringLength(2000)] public string? Error { get; init; }
}

public sealed record RfLogUpload
{
    public string Id { get; init; } = "";
    public string ClientId { get; init; } = "";
    public int CompanyId { get; init; }
    public string Region { get; init; } = "IN";
    public RfLogUploadRequest Metadata { get; init; } = new();
    public string StagingKey { get; init; } = "";
    public string? ObjectKey { get; init; }
    public string Status { get; init; } = "awaiting_upload";
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UploadedAtUtc { get; init; }
    public string? ETag { get; init; }
    public string? ReportReference { get; init; }
    public string? Error { get; init; }
    public int NotificationAttempts { get; init; }
    public string NotificationStatus { get; init; } = "pending";
}

public sealed record RfLogObject(long Size, string ETag, DateTime UploadedAtUtc);
