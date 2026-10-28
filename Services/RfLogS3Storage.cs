using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfLogStorage
{
    Task<string> UploadUrlAsync(string key);
    Task<string> DownloadUrlAsync(string key);
    Task<RfLogObject> VerifyZipAsync(string key, long expectedSize, CancellationToken ct);
    Task CopyAsync(string source, string target, string etag, CancellationToken ct);
    Task DownloadToFileAsync(string key, string etag, long size, string path, CancellationToken ct);
    Task UploadReportAsync(string key, byte[] pdf, CancellationToken ct);
    Task UploadZipAsync(string key, Stream zip, CancellationToken ct);
    Task<byte[]> ReadReportAsync(string key, long maxBytes, CancellationToken ct);
}

public sealed class RfLogS3Storage(IOptions<RfLogIntegrationOptions> configuration) : IRfLogStorage
{
    private RfLogIntegrationOptions Options => configuration.Value;
    internal AmazonS3Client CreateClient() => new(new BasicAWSCredentials(Options.AccessKey, Options.SecretKey),
        new AmazonS3Config { ServiceURL = Options.Endpoint, AuthenticationRegion = Options.Region, ForcePathStyle = Options.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED });

    public async Task<string> UploadUrlAsync(string key)
    {
        using var client = CreateClient();
        return await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Options.Bucket, Key = key, Verb = HttpVerb.PUT,
            ContentType = "application/zip", Expires = DateTime.UtcNow.AddMinutes(Options.UploadUrlMinutes)
        });
    }

    public async Task<string> DownloadUrlAsync(string key)
    {
        using var client = CreateClient();
        return await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Options.Bucket, Key = key, Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(Options.DownloadUrlMinutes)
        });
    }

    public async Task<RfLogObject> VerifyZipAsync(string key, long expectedSize, CancellationToken ct)
    {
        using var client = CreateClient();
        var head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = Options.Bucket, Key = key }, ct);
        if (head.ContentLength != expectedSize || string.IsNullOrWhiteSpace(head.ETag))
            throw new InvalidDataException("ZIP size does not match the registered upload.");
        using var zip = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = Options.Bucket, Key = key, ByteRange = new ByteRange(0, 3), EtagToMatch = head.ETag
        }, ct);
        var signature = new byte[4];
        await zip.ResponseStream.ReadExactlyAsync(signature, ct);
        if (signature[0] != 0x50 || signature[1] != 0x4b
            || !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6)))
            throw new InvalidDataException("Uploaded object does not have a ZIP signature.");
        return new(head.ContentLength, head.ETag, (head.LastModified ?? DateTime.UtcNow).ToUniversalTime());
    }

    public async Task CopyAsync(string source, string target, string etag, CancellationToken ct)
    {
        using var client = CreateClient();
        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Options.Bucket, SourceKey = source, DestinationBucket = Options.Bucket,
            DestinationKey = target, ETagToMatch = etag
        }, ct);
    }

    public async Task DownloadToFileAsync(string key, string etag, long size, string path, CancellationToken ct)
    {
        using var client = CreateClient();
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = Options.Bucket, Key = key, EtagToMatch = etag
        }, ct);
        if (response.ContentLength != size) throw new InvalidDataException("Accepted ZIP size changed.");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        long copied = 0;
        int count;
        while ((count = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
        {
            copied += count;
            if (copied > size) throw new InvalidDataException("ZIP download exceeded expected size.");
            await file.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (copied != size) throw new InvalidDataException("ZIP download was incomplete.");
    }

    public async Task UploadReportAsync(string key, byte[] pdf, CancellationToken ct)
    {
        using var client = CreateClient();
        await using var stream = new MemoryStream(pdf, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Options.Bucket, Key = key, InputStream = stream, ContentType = "application/pdf",
            UseChunkEncoding = false
        }, ct);
    }

    public async Task UploadZipAsync(string key, Stream zip, CancellationToken ct)
    {
        using var client = CreateClient();
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Options.Bucket, Key = key, InputStream = zip, ContentType = "application/zip",
            UseChunkEncoding = false, AutoCloseStream = false
        }, ct);
    }

    public async Task<byte[]> ReadReportAsync(string key, long maxBytes, CancellationToken ct)
    {
        using var client = CreateClient();
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = Options.Bucket, Key = key }, ct);
        if (response.ContentLength < 5 || response.ContentLength > maxBytes)
            throw new InvalidDataException("Report PDF exceeds the configured email attachment size or is empty.");
        using var output = new MemoryStream((int)response.ContentLength);
        var buffer = new byte[131072];
        int count;
        while ((count = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > maxBytes) throw new InvalidDataException("Report PDF exceeds the configured email attachment size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var pdf = output.ToArray();
        if (pdf.Length < 5 || pdf.Length != response.ContentLength || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Report attachment is not a complete PDF.");
        return pdf;
    }
}
