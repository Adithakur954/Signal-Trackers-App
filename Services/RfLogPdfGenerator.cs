using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SignalTracker.Controllers;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfLogReportGenerator
{
    Task<byte[]> GenerateAsync(string zipPath, RfLogUpload upload, CancellationToken ct);
}

// Adapter to the existing manual ZIP report engine: calculations and PDF layout
// stay identical. ZIP bytes are never imported into unrelated session tables.
public sealed class RfLogPdfGenerator(IWebHostEnvironment environment, IOptions<RfLogIntegrationOptions> configuration)
    : IRfLogReportGenerator
{
    public async Task<byte[]> GenerateAsync(string zipPath, RfLogUpload upload, CancellationToken ct)
    {
        await ValidateArchiveAsync(zipPath, ct);
        await using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var context = new DefaultHttpContext { RequestAborted = ct };
        context.Request.QueryString = new QueryString("?FilterByImageName=false");
        var controller = new UnifiedMapZipReportController(environment)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var result = await controller.GenerateFromZip(new ZipReportUploadRequest
        {
            LogZip = new FormFile(file, 0, file.Length, "LogZip", upload.Metadata.FileName)
            {
                Headers = new HeaderDictionary(), ContentType = "application/zip"
            },
            Title = "Automated RF Drive Test Report", GeneratedBy = "SignalTracker automatic reporting",
            ProjectName = upload.Metadata.ProjectId.HasValue ? $"Project {upload.Metadata.ProjectId}" : $"Session {upload.Metadata.SessionId}",
            SessionIdOverride = upload.Metadata.SessionId
        });
        ct.ThrowIfCancellationRequested();
        if (result is not FileContentResult pdf || pdf.ContentType != "application/pdf"
            || pdf.FileContents.Length < 5 || !pdf.FileContents.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("ZIP contains no usable network-log data for the automatic PDF report.");
        return pdf.FileContents;
    }

    private async Task ValidateArchiveAsync(string path, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 5000) throw new InvalidDataException("ZIP contains too many entries.");
        long expanded = 0;
        long rows = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            expanded = checked(expanded + entry.Length);
            if (expanded > configuration.Value.MaxExpandedZipBytes || entry.Length > 268435456)
                throw new InvalidDataException("ZIP exceeds automatic-report decompression limits.");
            if (!entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) != null)
                if (++rows > configuration.Value.MaxAutomaticReportRows)
                    throw new InvalidDataException("ZIP exceeds the configured automatic-report row limit; no partial report was generated.");
        }
    }
}
