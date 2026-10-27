using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.S3;
using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Services;

// Explicit deployment check. Uses disposable objects only; no sessions, events or reports are queued.
public static class RfLogStorageCheck
{
    public static async Task<int> RunAsync(IConfiguration configuration)
    {
        var options = configuration.GetSection(RfLogIntegrationOptions.Section).Get<RfLogIntegrationOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.AccessKey)) options.AccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? "";
        if (string.IsNullOrWhiteSpace(options.SecretKey)) options.SecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? "";
        if (!options.IsConfigured)
        {
            Console.Error.WriteLine("RF storage is disabled or not configured. Set enabled, endpoint, bucket, signing region and rotated credentials on this server.");
            return 1;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var storage = new RfLogS3Storage(Options.Create(options));
        using var client = storage.CreateClient();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var prefix = $"rf-logs/checks/{Guid.NewGuid():N}";
        var keys = new[] { $"{prefix}/input.zip", $"{prefix}/snapshot.zip", $"{prefix}/report.pdf" };
        var possibleObjects = new HashSet<string>();
        var failed = false;
        try
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("check.txt").Open());
                writer.Write("SignalTracker storage deployment check");
            }
            var bytes = buffer.ToArray();
            using var body = new ByteArrayContent(bytes);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            possibleObjects.Add(keys[0]);
            using var put = await http.PutAsync(await storage.UploadUrlAsync(keys[0]), body, ct);
            if (!put.IsSuccessStatusCode)
            {
                // A rejected PUT did not create the object; do not issue misleading cleanup failures.
                possibleObjects.Remove(keys[0]);
                var response = await put.Content.ReadAsStringAsync(ct);
                var code = Regex.Match(response, "<Code>([A-Za-z0-9._-]{1,80})</Code>").Groups[1].Value;
                var region = Regex.Match(response, "<Region>([A-Za-z0-9._-]{1,40})</Region>").Groups[1].Value;
                throw new InvalidOperationException($"Presigned ZIP PUT returned HTTP {(int)put.StatusCode}"
                    + (code.Length == 0 ? "" : $"; S3 code {code}")
                    + (region.Length == 0 ? "" : $"; expected signing region {region}"));
            }
            Console.WriteLine("RF storage: presigned ZIP PUT passed.");

            var input = await storage.VerifyZipAsync(keys[0], bytes.Length, ct);
            possibleObjects.Add(keys[1]);
            await storage.CopyAsync(keys[0], keys[1], input.ETag, ct);
            await storage.VerifyZipAsync(keys[1], bytes.Length, ct);
            using var get = await http.GetAsync(await storage.DownloadUrlAsync(keys[1]), ct);
            if (!get.IsSuccessStatusCode || !(await get.Content.ReadAsByteArrayAsync(ct)).SequenceEqual(bytes))
                throw new InvalidOperationException("Presigned snapshot GET did not return the uploaded ZIP.");
            Console.WriteLine("RF storage: HEAD, ZIP range GET, conditional snapshot copy and presigned GET passed.");

            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% storage check\n%%EOF\n");
            possibleObjects.Add(keys[2]);
            await storage.UploadReportAsync(keys[2], pdf, ct);
            using var report = await http.GetAsync(await storage.DownloadUrlAsync(keys[2]), ct);
            if (!report.IsSuccessStatusCode || !(await report.Content.ReadAsByteArrayAsync(ct)).SequenceEqual(pdf))
                throw new InvalidOperationException("Private report PUT/GET verification failed.");
            Console.WriteLine("RF storage: private report PUT and presigned GET passed.");
        }
        catch (Exception ex)
        {
            failed = true;
            // Never print SDK/HTTP exceptions containing request URLs or authorization details.
            var detail = ex switch
            {
                AmazonS3Exception s3 => $"S3 HTTP {(int)s3.StatusCode}; code "
                    + (Regex.IsMatch(s3.ErrorCode ?? "", "^[A-Za-z0-9._-]{1,80}$") ? s3.ErrorCode : "unavailable"),
                OperationCanceledException => "operation timed out",
                InvalidOperationException => ex.Message,
                _ => ex.GetType().Name
            };
            Console.Error.WriteLine($"RF storage check failed: {detail}.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var key in possibleObjects)
            {
                try { await client.DeleteObjectAsync(options.Bucket, key, cleanup.Token); }
                catch
                {
                    failed = true;
                    Console.Error.WriteLine($"Storage check cleanup failed for {key}. Remove this check prefix and grant DeleteObject on rf-logs/checks/*.");
                }
            }
        }
        if (!failed) Console.WriteLine("RF storage round trip passed; check objects removed.");
        return failed ? 1 : 0;
    }
}
