using System.Data;
using System.Globalization;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class InsightService
{
    private const long MaxRemoteZipBytes = 512L * 1024 * 1024;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public InsightService(
        ApplicationDbContext db,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<int> CountBySessionAsync(int sessionId, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM tbl_upload_insight WHERE session_id = @sessionId;";
            AddParameter(command, "@sessionId", sessionId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null || value == DBNull.Value
                ? 0
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    public async Task<int> ImportFromRemoteZipAsync(int sessionId, CancellationToken cancellationToken)
    {
        var sessionUploadId = await _db.tbl_session
            .AsNoTracking()
            .Where(session => session.id == sessionId)
            .Select(session => session.tbl_upload_id)
            .FirstOrDefaultAsync(cancellationToken);
        var logId = int.TryParse(sessionUploadId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLogId)
            && parsedLogId > 0
            ? parsedLogId
            : sessionId;
        var template = _configuration["L3EventImport:RemoteLogZipUrlTemplate"];
        if (string.IsNullOrWhiteSpace(template))
            return 0;

        var remoteUrl = template.Replace("{logId}", logId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        var tempRoot = Path.Combine(Path.GetTempPath(), "signaltracker_l3_event_insights", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "remote.zip");
        try
        {
            using var response = await _httpClientFactory.CreateClient().GetAsync(
                remoteUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return 0;

            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await CopyWithLimitAsync(input, output, cancellationToken);

            var files = new List<(string Path, string FileName)>();
            using (var archive = ZipFile.OpenRead(zipPath))
                ExtractInsightEntries(archive, tempRoot, files, 0);
            if (files.Count == 0)
                return 0;

            var uploadId = int.TryParse(sessionUploadId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUploadId)
                && parsedUploadId > 0
                ? parsedUploadId
                : (int?)null;
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var inserted = UploadInsightStore.StoreFiles(
                _db.Database.GetDbConnection(),
                _db.Database.CurrentTransaction?.GetDbTransaction(),
                uploadId,
                sessionId,
                "L3Event",
                files);
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Temporary cleanup must not hide a successful import.
            }
        }
    }

    private static void ExtractInsightEntries(
        ZipArchive archive,
        string tempRoot,
        List<(string Path, string FileName)> files,
        int depth)
    {
        if (depth > 3)
            return;

        foreach (var entry in archive.Entries.Where(entry => entry.Length > 0 && !string.IsNullOrEmpty(entry.Name)))
        {
            if (Path.GetExtension(entry.Name).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(entry.Name).Contains("insight", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.txt");
                using var input = entry.Open();
                using var output = System.IO.File.Create(filePath);
                input.CopyTo(output);
                files.Add((filePath, Path.GetFileName(entry.Name)));
                continue;
            }

            if (!Path.GetExtension(entry.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            using var nestedStream = new MemoryStream();
            using (var input = entry.Open())
                input.CopyTo(nestedStream);
            nestedStream.Position = 0;
            using var nested = new ZipArchive(nestedStream, ZipArchiveMode.Read, true);
            ExtractInsightEntries(nested, tempRoot, files, depth + 1);
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task CopyWithLimitAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long totalBytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            totalBytes = checked(totalBytes + read);
            if (totalBytes > MaxRemoteZipBytes)
                throw new InvalidDataException("Remote ZIP file exceeds the 512 MB limit.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
