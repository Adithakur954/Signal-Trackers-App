using System.Data.Common;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Models;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
    private static string BuildNetworkDashboardSessionSql(DbCommand cmd, IReadOnlyList<int> sessionIds, int? uploadId, bool hasHistory)
    {
        var clauses = new List<string>();
        if (sessionIds.Count > 0)
        {
            var names = new List<string>();
            AddParams(cmd, "networkSummarySession", sessionIds, names);
            clauses.Add($"s.id IN ({string.Join(",", names)})");
        }
        if (uploadId.HasValue)
        {
            AddParam(cmd, "@networkSummaryUpload", uploadId.Value);
            AddParam(cmd, "@networkSummaryUploadText", uploadId.Value.ToString(CultureInfo.InvariantCulture));
            clauses.Add(hasHistory ? @"(s.tbl_upload_id = @networkSummaryUploadText OR EXISTS (
                SELECT 1 FROM tbl_l3_event_history h WHERE h.session_id = s.id
                AND (h.id = @networkSummaryUpload OR h.tbl_upload_id = @networkSummaryUpload)))"
                : "s.tbl_upload_id = @networkSummaryUploadText");
        }
        return "SELECT DISTINCT s.id FROM tbl_session s WHERE " + (clauses.Count == 0 ? "1=0" : string.Join(" AND ", clauses));
    }

    private async Task<(List<NetworkDashboardSample> Rows, IActionResult? Error)> LoadNetworkDashboardFallbackAsync(
        DbConnection conn, DiagnosticQueryRequest request, L3SummaryFilters filters, L3EventController access)
    {
        var rows = new List<NetworkDashboardSample>();
        var cancellation = HttpContext.RequestAborted;
        if (filters.FailuresOnly || !string.IsNullOrWhiteSpace(filters.Search)
            || (!string.IsNullOrWhiteSpace(filters.Interface) && !filters.Interface.Equals("all", StringComparison.OrdinalIgnoreCase))) return (rows, null);
        if (!await DiagnosticTableExistsAsync(conn, "tbl_network_log")) return (rows, null);
        var sessionIds = new List<int>();
        var hasHistory = await DiagnosticTableExistsAsync(conn, "tbl_l3_event_history");
        await using (var resolve = conn.CreateCommand())
        {
            resolve.CommandText = BuildNetworkDashboardSessionSql(resolve, request.SessionIds, request.UploadId, hasHistory);
            await using var reader = await resolve.ExecuteReaderAsync(cancellation);
            while (await reader.ReadAsync(cancellation)) sessionIds.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (sessionIds.Count == 0) return (rows, null);
        // Resolving an upload must never grant access to another company's linked session.
        var denied = await access.ValidateDiagnosticAccessAsync(null, string.Join(",", sessionIds), null, null, cancellation);
        if (denied != null) return (rows, denied);
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var schema = conn.CreateCommand())
        {
            schema.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='tbl_network_log'";
            await using var reader = await schema.ExecuteReaderAsync(cancellation);
            while (await reader.ReadAsync(cancellation)) available.Add(reader.GetString(0));
        }
        var fields = new[] { "id", "session_id", "timestamp", "network", "band", "pci", "mci", "cell_id", "earfcn", "tac", "primary",
            "mcc", "mnc", "m_mcc", "m_mnc", "rsrp", "rsrq", "sinr", "cqi", "dl_tpt", "ul_tpt",
            "dls", "uls", "direction", "channel", "extra_json" };
        await using var command = conn.CreateCommand();
        var parameters = new List<string>();
        AddParams(command, "networkDataSession", sessionIds, parameters);
        AddParam(command, "@networkDataTake", request.Take + 1);
        command.CommandTimeout = 180;
        command.CommandText = $"SELECT {string.Join(",", fields.Select(f => available.Contains(f) ? $"`{f}`" : $"NULL AS `{f}`"))} "
            + $"FROM tbl_network_log WHERE session_id IN ({string.Join(",", parameters)}) ORDER BY session_id,id LIMIT @networkDataTake";
        await using (var reader = await command.ExecuteReaderAsync(cancellation))
        {
            var loaded = 0;
            while (await reader.ReadAsync(cancellation))
            {
                if (++loaded > request.Take)
                {
                    rows.Add(new NetworkDashboardSample
                    {
                        SessionId = -1,
                        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["__warning"] = $"Network Log fallback was limited to the first {request.Take} rows. L3/Event summary is still generated; choose fewer sessions for full Network Log fallback."
                        }
                    });
                    break;
                }
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++) values[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                var sample = NetworkDashboardSample.FromFields(int.Parse(values["session_id"]!, CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2), values);
                if (NetworkLogDashboardFallback.Matches(sample, filters)) rows.Add(sample);
            }
        }
        return (rows, null);
    }
}
