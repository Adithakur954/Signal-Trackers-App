using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace SignalTracker.Services;

public sealed record UploadInsightRecord(
    string Severity,
    string Title,
    string? InsightTime,
    double? Latitude,
    double? Longitude,
    string Description,
    string DetailsJson,
    string RawText);

public static class UploadInsightStore
{
    private static readonly Regex HeaderRegex = new(
        @"^\s*\[(?<severity>[^\]]+)\]\s*(?:@\s*(?<time>\d{2}:\d{2}:\d{2})\s+)?(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CoordinateRegex = new(
        @"(?<lat>-?\d{1,3}(?:\.\d+)?)\s*,\s*(?<lon>-?\d{1,3}(?:\.\d+)?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void EnsureTable(IDbConnection connection, IDbTransaction? transaction = null)
    {
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
            CREATE TABLE IF NOT EXISTS tbl_upload_insight (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                upload_id INT NULL,
                session_id INT NULL,
                source VARCHAR(32) NOT NULL,
                source_file_name VARCHAR(255) NULL,
                severity VARCHAR(32) NULL,
                title VARCHAR(512) NOT NULL,
                insight_time VARCHAR(64) NULL,
                latitude DOUBLE NULL,
                longitude DOUBLE NULL,
                description LONGTEXT NULL,
                details_json LONGTEXT NULL,
                raw_text LONGTEXT NOT NULL,
                uploaded_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                INDEX ix_upload_insight_upload (upload_id),
                INDEX ix_upload_insight_session (session_id),
                INDEX ix_upload_insight_severity (severity)
            );";
            command.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    public static int StoreFiles(
        IDbConnection connection,
        IDbTransaction? transaction,
        int? uploadId,
        int? sessionId,
        string source,
        IEnumerable<(string Path, string FileName)> files)
    {
        var records = files.SelectMany(file => ParseFile(file.Path).Select(record => (file.FileName, record))).ToList();
        if (records.Count == 0)
            return 0;

        foreach (var item in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO tbl_upload_insight
                    (upload_id, session_id, source, source_file_name, severity, title, insight_time,
                     latitude, longitude, description, details_json, raw_text)
                VALUES
                    (@uploadId, @sessionId, @source, @fileName, @severity, @title, @insightTime,
                     @latitude, @longitude, @description, @detailsJson, @rawText);";
            Add(command, "@uploadId", uploadId);
            Add(command, "@sessionId", sessionId);
            Add(command, "@source", source);
            Add(command, "@fileName", item.FileName);
            Add(command, "@severity", item.record.Severity);
            Add(command, "@title", item.record.Title);
            Add(command, "@insightTime", item.record.InsightTime);
            Add(command, "@latitude", item.record.Latitude);
            Add(command, "@longitude", item.record.Longitude);
            Add(command, "@description", item.record.Description);
            Add(command, "@detailsJson", item.record.DetailsJson);
            Add(command, "@rawText", item.record.RawText);
            command.ExecuteNonQuery();
        }

        return records.Count;
    }

    public static IReadOnlyList<UploadInsightRecord> ParseFile(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<UploadInsightRecord>();

        var lines = File.ReadAllLines(path);
        var records = new List<UploadInsightRecord>();
        var currentHeader = default(Match);
        var body = new List<string>();

        void Flush()
        {
            if (currentHeader == null)
                return;

            var description = string.Join(" ", body.Select(line => line.Trim()).Where(line => line.Length > 0)).Trim();
            var rawText = string.Join(Environment.NewLine, body.Prepend(currentHeader.Value.Trim())).Trim();
            var coordinates = CoordinateRegex.Match(description);
            double? latitude = null;
            double? longitude = null;
            if (coordinates.Success
                && double.TryParse(coordinates.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLatitude)
                && double.TryParse(coordinates.Groups["lon"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLongitude))
            {
                latitude = parsedLatitude;
                longitude = parsedLongitude;
                description = description[..coordinates.Index].TrimEnd(" ,;".ToCharArray());
            }

            var severity = currentHeader.Groups["severity"].Value.Trim().ToUpperInvariant();
            var title = currentHeader.Groups["title"].Value.Trim();
            var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["severity"] = severity,
                ["title"] = title,
                ["time"] = currentHeader.Groups["time"].Success ? currentHeader.Groups["time"].Value : null,
                ["latitude"] = latitude,
                ["longitude"] = longitude,
                ["description"] = description
            };
            records.Add(new UploadInsightRecord(
                severity,
                title,
                currentHeader.Groups["time"].Success ? currentHeader.Groups["time"].Value : null,
                latitude,
                longitude,
                description,
                JsonSerializer.Serialize(details),
                rawText));
            body.Clear();
        }

        foreach (var line in lines)
        {
            var header = HeaderRegex.Match(line);
            if (header.Success)
            {
                Flush();
                currentHeader = header;
            }
            else if (currentHeader != null)
            {
                body.Add(line);
            }
        }

        Flush();
        return records;
    }

    private static void Add(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
