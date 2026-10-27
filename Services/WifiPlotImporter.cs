using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class WifiPlotImportResult
{
    public int Inserted { get; set; }
    public int Duplicates { get; set; }
    public int InvalidRows { get; set; }
}

// Shared by the synchronous network upload and asynchronous ZIP/cloud importer.
// One session lock and one lookup/INSERT per batch; no database access per CSV row.
public static class WifiPlotImporter
{
    public const int BatchSize = 500;
    private static readonly string[] Columns = ["session_id", "company_id", "timestamp", "lat", "lon", "network",
        "primary_cell_info_1", "primary", "band", "rssi", "level", "dl_tpt", "ul_tpt", "m_alpha_short", "m_alpha_long", "channel"];

    public static async Task<WifiPlotImportResult> ImportAsync(DbConnection connection, DbTransaction? transaction,
        TextReader reader, int sessionId, int companyId, Action<tbl_network_log>? onRow = null, CancellationToken ct = default)
    {
        if (sessionId <= 0 || companyId <= 0) throw new InvalidDataException("A valid target session and company are required for Wi-Fi import.");
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        DbTransaction? owned = null;
        try
        {
            owned = transaction == null ? await connection.BeginTransactionAsync(ct) : null;
            var activeTransaction = transaction ?? owned ?? throw new InvalidOperationException("Wi-Fi import transaction is missing.");
            // Serialize imports of the same session, including retries, without a global ingest lock.
            await using (var session = connection.CreateCommand())
            {
                session.Transaction = activeTransaction;
                session.CommandText = """
                    SELECT id FROM tbl_session
                    WHERE id=@sessionId AND EXISTS (
                        SELECT 1 FROM tbl_user u WHERE u.id=tbl_session.user_id AND u.company_id=@companyId
                    ) FOR UPDATE
                    """;
                Add(session, "@sessionId", sessionId);
                Add(session, "@companyId", companyId);
                if (await session.ExecuteScalarAsync(ct) == null) throw new InvalidDataException("Wi-Fi target session was not found in the authorized company.");
            }
            var result = new WifiPlotImportResult();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var batch = new List<tbl_network_log>(BatchSize);
            var batchBytes = 0L;
            foreach (var row in WifiPlotCsv.Read(reader, sessionId, companyId, () => result.InvalidRows++))
            {
                ct.ThrowIfCancellationRequested();
                onRow?.Invoke(row);
                if (!seen.Add(Key(row.primary_cell_info_1!))) { result.Duplicates++; continue; }
                var primaryBytes = Encoding.UTF8.GetByteCount(row.primary_cell_info_1!);
                if (primaryBytes > 65535) throw new InvalidDataException("Wi-Fi plot row exceeds the primary-cell text size limit.");
                var rowBytes = 2L * primaryBytes + 1024;
                if (batch.Count > 0 && batchBytes + rowBytes > 1024 * 1024)
                {
                    await FlushAsync(connection, activeTransaction, batch, sessionId, result, ct);
                    batchBytes = 0;
                }
                batch.Add(row);
                batchBytes += rowBytes;
                if (batch.Count == BatchSize)
                {
                    await FlushAsync(connection, activeTransaction, batch, sessionId, result, ct);
                    batchBytes = 0;
                }
            }
            await FlushAsync(connection, activeTransaction, batch, sessionId, result, ct);
            if (owned != null) await owned.CommitAsync(ct);
            return result;
        }
        finally
        {
            // Disposing an uncommitted owned transaction rolls it back. Caller-owned transactions remain caller-owned.
            if (owned != null) await owned.DisposeAsync();
            if (opened) await connection.CloseAsync();
        }
    }

    private static async Task FlushAsync(DbConnection connection, DbTransaction transaction, List<tbl_network_log> batch,
        int sessionId, WifiPlotImportResult result, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT primary_cell_info_1 FROM tbl_network_log
                WHERE session_id=@sessionId AND timestamp>=@from AND timestamp<@to
                  AND network='WiFi' AND primary_cell_info_1 LIKE 'SSID:%'
                FOR UPDATE
                """;
            Add(query, "@sessionId", sessionId);
            // Existing DATETIME columns can round/truncate subsecond values; source text retains exact CSV time.
            var from = batch.Min(row => row.timestamp)!.Value;
            var to = batch.Max(row => row.timestamp)!.Value;
            Add(query, "@from", new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerSecond, from.Kind));
            Add(query, "@to", new DateTime(to.Ticks - to.Ticks % TimeSpan.TicksPerSecond, to.Kind).AddSeconds(2));
            await using var rows = await query.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) if (!rows.IsDBNull(0)) existing.Add(Key(rows.GetString(0)));
        }
        var insert = batch.Where(row => !existing.Contains(Key(row.primary_cell_info_1!))).ToList();
        result.Duplicates += batch.Count - insert.Count;
        if (insert.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            var values = new List<string>(insert.Count);
            for (var index = 0; index < insert.Count; index++)
            {
                var row = insert[index];
                object?[] data = [row.session_id, row.company_id, row.timestamp, row.lat, row.lon, row.network,
                    row.primary_cell_info_1, row.primary, row.band, row.rssi, row.level, row.dl_tpt, row.ul_tpt,
                    row.m_alpha_short, row.m_alpha_long, row.channel];
                var placeholders = new string[data.Length];
                for (var field = 0; field < data.Length; field++)
                {
                    placeholders[field] = $"@w{index}_{field}";
                    Add(command, placeholders[field], data[field]);
                }
                values.Add("(" + string.Join(",", placeholders) + ")");
            }
            command.CommandText = "INSERT INTO tbl_network_log (" + string.Join(",", Columns.Select(column => "`" + column + "`"))
                + ") VALUES " + string.Join(",", values);
            await command.ExecuteNonQueryAsync(ct);
            result.Inserted += insert.Count;
        }
        batch.Clear();
    }

    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
