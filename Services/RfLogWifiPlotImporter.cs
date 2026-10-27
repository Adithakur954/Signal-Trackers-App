using System.IO.Compression;
using System.Text;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfLogWifiPlotImporter
{
    Task ImportAsync(string zipPath, RfLogUpload upload, CancellationToken ct);
}

// Runs in the report worker after the existing generator has validated/read the archive.
// Upload registration/completion stay small; only Wi-Fi plots are imported into the upload's existing session.
public sealed class RfLogWifiPlotImporter(IConfiguration configuration, IRfLogUploadStore store) : IRfLogWifiPlotImporter
{
    public async Task ImportAsync(string zipPath, RfLogUpload upload, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(entry => entry.Length > 0 && WifiPlotCsv.IsFile(entry.Name)).ToList();
        if (entries.Count == 0) return;
        var name = RfLogUploadStore.ConnectionStringName(upload.Region);
        if (!await store.OwnerExistsAsync(upload.Metadata, upload.CompanyId, upload.Region, ct))
            throw new InvalidDataException("Wi-Fi target session no longer matches the upload's user/company scope.");
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Regional Wi-Fi database is not configured.");
        await using var connection = new MySqlConnection(value);
        foreach (var entry in entries)
        {
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);
            await WifiPlotImporter.ImportAsync(connection, null, reader, upload.Metadata.SessionId, upload.CompanyId, ct: ct);
        }
    }
}
