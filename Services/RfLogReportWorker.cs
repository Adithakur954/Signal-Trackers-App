using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class RfLogReportProcessor(IRfLogUploadStore store, IRfLogStorage storage,
    IRfLogReportGenerator generator, IRfLogWifiPlotImporter wifiPlots, ILogger<RfLogReportProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(string region, CancellationToken ct)
    {
        var pending = await store.PendingAsync(region, 1, ct);
        var upload = pending.FirstOrDefault();
        if (upload == null) return false;
        var token = Guid.NewGuid().ToString("N");
        if (!await store.ClaimAsync(upload.Id, region, token, ct)) return false;
        var temporaryZip = Path.Combine(Path.GetTempPath(), $"rf-report-{Guid.NewGuid():N}.zip");
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewLeaseAsync(upload, region, token, job, heartbeatStop.Token);
        try
        {
            if (upload.ObjectKey == null || upload.ETag == null) throw new InvalidDataException("Accepted ZIP reference is missing.");
            await storage.DownloadToFileAsync(upload.ObjectKey, upload.ETag, upload.Metadata.FileSizeBytes, temporaryZip, job.Token);
            // The existing PDF renderer performs CPU work synchronously; allow
            // lease renewals and the other region to run while it builds charts.
            var pdf = await Task.Run(() => generator.GenerateAsync(temporaryZip, upload, job.Token), job.Token);
            job.Token.ThrowIfCancellationRequested();
            await wifiPlots.ImportAsync(temporaryZip, upload, job.Token);
            job.Token.ThrowIfCancellationRequested();
            var reportKey = $"rf-logs/reports/{region}/{upload.CompanyId}/{upload.Id}/{token}/report.pdf";
            await storage.UploadReportAsync(reportKey, pdf, job.Token);
            // Stop heartbeat before terminal status so it cannot race a successful completion.
            heartbeatStop.Cancel();
            await heartbeat;
            job.Token.ThrowIfCancellationRequested();
            if (!await store.ResultAsync(upload.Id, region, token,
                new() { Status = "completed", ReportReference = reportKey }, job.Token))
                logger.LogWarning("RF report {Region}:{Id} finished after its lease was replaced; artifact was not published.", region, upload.Id);
            else logger.LogInformation("Automatic RF PDF completed for {Region}:{Id}.", region, upload.Id);
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested) { /* Lease expires for recovery; never publish stale output. */ }
        catch (Exception ex)
        {
            heartbeatStop.Cancel();
            await heartbeat;
            logger.LogError(ex, "Automatic RF report failed for {Region}:{Id}.", region, upload.Id);
            var error = ex is InvalidDataException ? ex.Message : "Automatic report failed; inspect backend logs and retry the report.";
            await store.ResultAsync(upload.Id, region, token, new() { Status = "failed", Error = error }, ct);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
            try { if (File.Exists(temporaryZip)) File.Delete(temporaryZip); }
            catch (IOException) { logger.LogWarning("Temporary RF report ZIP could not be removed."); }
        }
        return true;
    }

    private async Task RenewLeaseAsync(RfLogUpload upload, string region, string token,
        CancellationTokenSource job, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
                if (!await store.ResultAsync(upload.Id, region, token, new() { Status = "processing" }, stop))
                {
                    job.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("RF report lease could not be renewed ({Type}); cancelling stale work.", ex.GetType().Name);
            job.Cancel();
        }
    }
}

public sealed class RfLogReportWorker(IServiceScopeFactory scopes, IOptions<RfLogIntegrationOptions> options,
    ILogger<RfLogReportWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(RunRegionAsync("IN", ct), RunRegionAsync("TW", ct));
    private async Task RunRegionAsync(string region, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (options.Value.IsConfigured && options.Value.AutomaticReportsEnabled)
                {
                    using var scope = scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<RfLogReportProcessor>().ProcessNextAsync(region, ct)) continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "RF report queue {Region} unavailable; other regions continue independently.", region);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }
}
