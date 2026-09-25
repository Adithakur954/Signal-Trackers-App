using System.Globalization;
using System.Data.Common;
using MySqlConnector;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
    [HttpGet("GetDiagnosticL3Summary")]
    public Task<IActionResult> GetDiagnosticL3Summary(
        [FromQuery] int? sessionId = null,
        [FromQuery] string? sessionIds = null,
        [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
        [FromQuery] int? uploadId = null,
        [FromQuery] int take = 50000,
        [FromQuery] string? sourceFileName = null,
        [FromQuery] L3SummaryFilters? filters = null,
        [FromQuery] bool includeRows = true) =>
        GenerateCombinedL3SummaryAsync(sessionId, sessionIds, sessionIdsAlt, uploadId, take, sourceFileName, filters, false, 100000, json: true, includeRows: includeRows);

    [HttpGet("GenerateDiagnosticL3SummaryExcel")]
    public Task<IActionResult> GenerateDiagnosticL3SummaryExcel(
        [FromQuery] int? sessionId = null,
        [FromQuery] string? sessionIds = null,
        [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
        [FromQuery] int? uploadId = null,
        [FromQuery] int take = 50000,
        [FromQuery] string? sourceFileName = null,
        [FromQuery] L3SummaryFilters? filters = null) =>
        GenerateCombinedL3SummaryAsync(sessionId, sessionIds, sessionIdsAlt, uploadId, take, sourceFileName, filters, false, 100000);

    private async Task<IActionResult> GenerateCombinedL3SummaryAsync(int? sessionId, string? sessionIds,
        string? sessionIdsAlt, int? uploadId, int take, string? sourceFileName, L3SummaryFilters? filters,
        bool pdf, int reportRows, bool json = false, bool includeRows = true)
    {
        try
        {
            if (uploadId.HasValue && uploadId <= 0)
                return BadRequest(new { status = 0, message = "uploadId must be positive." });
            var request = ParseDiagnosticQuery(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
            if (request.Error != null) return request.Error;
            filters ??= new L3SummaryFilters();
            if (filters.Validate() is { } error) return BadRequest(new { status = 0, message = error });
            // Both controller routes enforce the same session/upload access checks.
            var access = new L3EventController(db, _httpContextAccessor, _env, _redis, _userScope,
                _connectionProvider, _networkLogData, _configuration) { ControllerContext = ControllerContext };
            var denied = await access.ValidateDiagnosticAccessAsync(sessionId, sessionIds, sessionIdsAlt, uploadId, HttpContext.RequestAborted);
            if (denied != null) return denied;

            var conn = await OpenDiagnosticConnectionAsync();
            // Event and L3 tables are independent. Read them concurrently so
            // a large upload does not make the summary wait for two scans in
            // sequence. Keep the original connection for the fallback query.
            await using var l3Conn = new MySqlConnection(_connectionProvider.GetConnectionString());
            await l3Conn.OpenAsync(HttpContext.RequestAborted);
            var eventsTask = LoadDiagnosticEventRowsAsync(conn, request.SessionIds, request.UploadId, request.Take + 1);
            var l3Task = LoadDiagnosticL3RowsAsync(l3Conn, request.SessionIds, request.UploadId, request.Take + 1);
            await Task.WhenAll(eventsTask, l3Task);
            var events = await eventsTask;
            var l3 = await l3Task;
            if (events.Count > request.Take || l3.Count > request.Take)
                return UnprocessableEntity(new { status = 0, message = $"The selected scope exceeds take={request.Take} rows per source. Increase take (maximum 50000) or select fewer sessions. No partial dashboard was generated." });
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var calls = BuildDiagnosticCallRows(events, l3);
            var timeline = BuildDiagnosticTimelineRows(events, l3, calls);
            var selected = timeline.Select(row => (Row: row, Message: ToL3ReportMessage(row)))
                .Where(pair => filters.Matches(pair.Message, pair.Row.Severity.Equals("failure", StringComparison.OrdinalIgnoreCase)
                    || pair.Row.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)
                    || pair.Row.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase))).ToList();
            var callIds = selected.Select(p => p.Row.CallId).Where(id => id != null).ToHashSet();
            var selectedCalls = calls.Where(c => callIds.Contains(c.FrontendId)).Select(c => new L3ReportCall(c.Call,
                c.Technology, c.Start ?? "", c.End ?? "", c.Result, c.SetupTimeSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                c.CallDurationSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "", c.Reason)).ToList();
            var source = string.IsNullOrWhiteSpace(sourceFileName)
                ? request.UploadId.HasValue ? $"l3-upload-{request.UploadId}" : $"l3-session-{string.Join("-", request.SessionIds)}"
                : sourceFileName;
            var scope = $"Sessions: {string.Join(",", request.SessionIds)}; Upload: {request.UploadId?.ToString() ?? "all selected"}; " +
                (filters.ToString().Length == 0 ? "All selected L3/Event messages" : filters.ToString());
            var report = L3SummaryReportBuilder.Build(source, scope, selected.Select(p => p.Message).ToList(), selectedCalls);
            var detectedServices = await LoadDiagnosticServiceSummaryAsync(conn, request, selected.Select(pair => pair.Row).ToList());
            var fallback = await LoadNetworkDashboardFallbackAsync(conn, request, filters, access);
            if (fallback.Error != null) return fallback.Error;
            var fallbackWarnings = fallback.Rows
                .Select(row => row.Get("__warning"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            NetworkLogDashboardFallback.Apply(report, fallback.Rows.Where(row => row.SessionId > 0).ToList());
            report.Warnings.AddRange(fallbackWarnings);
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            if (json) return Json(BuildL3SummaryJson(report, includeRows ? selected.Select(pair => pair.Row).ToList() : Array.Empty<DiagnosticTimelineRow>(), detectedServices));
            var stem = SanitizeDiagnosticFileStem(source);
            return pdf ? File(BuildCombinedL3SummaryPdf(report, Math.Clamp(reportRows, 1, 100000)), "application/pdf", $"call-summary-{stem}.pdf")
                : File(L3SummaryReportBuilder.WriteExcel(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"call-summary-{stem}.xlsx");
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return StatusCode(500, new { status = 0, message = "An error occurred while generating the L3 summary report.", details = SafeException.Get(ex) });
        }
    }

    private static object BuildL3SummaryJson(L3SummaryReport report, IReadOnlyList<DiagnosticTimelineRow> timeline, DiagnosticServiceSummary detectedServices)
    {
        // Explicit camel-case properties: the application's global JSON naming policy is null.
        static object[] Values(IEnumerable<L3DashboardValue> rows) => rows.Select(row => (object)new
        {
            parameter = row.Parameter, result = row.Result, observation = row.Observation, source = row.Source
        }).ToArray();

        static double? Milliseconds(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds >= 0 ? Math.Round(seconds * 1000, MidpointRounding.AwayFromZero) : null;
        static double Average(IEnumerable<double?> values)
        {
            var valid = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            return valid.Length == 0 ? 0 : Math.Round(valid.Average(), MidpointRounding.AwayFromZero);
        }
        var connectedCalls = report.Calls.Where(c => c.Result.Equals("Connected", StringComparison.OrdinalIgnoreCase)).ToArray();
        var duration = report.Calls.Sum(c => Milliseconds(c.Duration) ?? 0);
        static object Observed(L3ObservedEvents value) => new { endcSetupRows = value.EndcSetupRows, handoverRows = value.HandoverRows };

        return new
        {
            status = 1,
            data = new
            {
                summaryVersion = 1,
                totalCalls = report.Calls.Count,
                connected = connectedCalls.Length,
                dropped = report.Calls.Count(c => c.Result.Equals("Dropped", StringComparison.OrdinalIgnoreCase)),
                notConnected = report.Calls.Count(c => c.Result.Equals("Not Connected", StringComparison.OrdinalIgnoreCase)),
                averageSetupTime = Average(connectedCalls.Select(c => Milliseconds(c.SetupTime))),
                averageTalkTime = Average(connectedCalls.Select(c => Milliseconds(c.Duration))),
                totalDurationMs = duration,
                totalConnectedDurationMs = duration,
                observedEvents = Observed(report.ObservedEvents),
                detectedServices,
                sourceFile = report.SourceFile,
                scope = report.Scope,
                generatedAt = report.GeneratedAt,
                hasData = report.Messages.Count > 0 || report.HasNetworkLogFallback,
                networkLogRows = report.NetworkLogRows,
                hasNetworkLogFallback = report.HasNetworkLogFallback,
                dashboardSources = report.DashboardSources,
                warnings = report.Warnings,
                totalRows = report.Messages.Count,
                l3Rows = report.Messages.Count(row => row.Source.Equals("l3", StringComparison.OrdinalIgnoreCase)),
                eventRows = report.Messages.Count(row => row.Source.Equals("event", StringComparison.OrdinalIgnoreCase)),
                rows = timeline,
                kpis = Values(report.Kpis),
                mobility = Values(report.Mobility),
                parameters = Values(report.Parameters),
                technologies = report.Technologies.Select(row => new
                {
                    technology = row.Technology, rows = row.Rows, interfaces = row.Interfaces,
                    observedEvents = Observed(report.ObservedEventsByTechnology.GetValueOrDefault(row.Technology) ?? new(0, 0))
                }).ToArray(),
                calls = report.Calls.Select(row => new
                {
                    call = row.Call, technology = row.Technology, start = row.Start, end = row.End,
                    result = row.Result, setupTime = row.SetupTime, duration = row.Duration, reason = row.Reason
                }).ToArray()
            }
        };
    }

    private sealed class DiagnosticServiceSummary
    {
        public bool HasVolte { get; set; }
        public bool HasVonr { get; set; }
        public bool HasTmsi { get; set; }
        public bool HasRrcSibParameters { get; set; }
        public long VolteTextRows { get; set; }
        public long VonrTextRows { get; set; }
        public long TmsiRows { get; set; }
        public long RrcSibParameterRows { get; set; }
        public long NetworkLogRows { get; set; }
        public long VolteNetworkRows { get; set; }
        public long VolteCallMinusOneRows { get; set; }
        public long VolteCallActiveRows { get; set; }
        public long VolteCallBlankRows { get; set; }
        public List<DiagnosticServiceEvidence> Evidence { get; set; } = new();
        public Dictionary<string, long> VolteCallValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DiagnosticServiceEvidence
    {
        public string Service { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? Timestamp { get; set; }
        public int? SessionId { get; set; }
        public string Data { get; set; } = string.Empty;
    }

    private async Task<DiagnosticServiceSummary> LoadDiagnosticServiceSummaryAsync(
        DbConnection conn,
        DiagnosticQueryRequest request,
        IReadOnlyList<DiagnosticTimelineRow> timeline)
    {
        var summary = new DiagnosticServiceSummary();
        foreach (var row in timeline)
        {
            if (row.ServiceIndicators.Count == 0)
                continue;

            if (row.ServiceIndicators.Contains("VoLTE", StringComparer.OrdinalIgnoreCase)) summary.VolteTextRows++;
            if (row.ServiceIndicators.Contains("VoNR", StringComparer.OrdinalIgnoreCase)) summary.VonrTextRows++;
            if (row.ServiceIndicators.Contains("TMSI", StringComparer.OrdinalIgnoreCase)) summary.TmsiRows++;
            if (row.ServiceIndicators.Contains("RRC/SIB Parameters", StringComparer.OrdinalIgnoreCase)) summary.RrcSibParameterRows++;

            foreach (var service in row.ServiceIndicators)
            {
                if (summary.Evidence.Count >= 50)
                    break;
                summary.Evidence.Add(new DiagnosticServiceEvidence
                {
                    Service = service,
                    Source = row.SourceType,
                    Timestamp = row.TimestampLabel,
                    SessionId = row.SessionId,
                    Data = FirstNonEmpty(row.RawMessage, row.Summary, row.Message)
                });
            }
        }

        await AddNetworkLogServiceCountsAsync(conn, request, summary);
        summary.HasVolte = summary.VolteTextRows > 0 || summary.VolteNetworkRows > 0;
        summary.HasVonr = summary.VonrTextRows > 0;
        summary.HasTmsi = summary.TmsiRows > 0;
        summary.HasRrcSibParameters = summary.RrcSibParameterRows > 0;
        return summary;
    }

    private async Task AddNetworkLogServiceCountsAsync(DbConnection conn, DiagnosticQueryRequest request, DiagnosticServiceSummary summary)
    {
        if (!await DiagnosticTableExistsAsync(conn, "tbl_network_log"))
            return;

        var hasHistory = await DiagnosticTableExistsAsync(conn, "tbl_l3_event_history");
        var sessionIds = new List<int>();
        await using (var resolve = conn.CreateCommand())
        {
            resolve.CommandText = BuildNetworkDashboardSessionSql(resolve, request.SessionIds, request.UploadId, hasHistory);
            await using var reader = await resolve.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
                sessionIds.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (sessionIds.Count == 0)
            return;

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var schema = conn.CreateCommand())
        {
            schema.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='tbl_network_log'";
            await using var reader = await schema.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
                available.Add(reader.GetString(0));
        }
        if (!available.Contains("volte_call"))
            return;

        await using var cmd = conn.CreateCommand();
        var names = new List<string>();
        AddParams(cmd, "serviceSession", sessionIds, names);
        cmd.CommandText = $@"
            SELECT COALESCE(NULLIF(TRIM(CAST(volte_call AS CHAR)), ''), '<blank>') AS volte_value, COUNT(*)
            FROM tbl_network_log
            WHERE session_id IN ({string.Join(",", names)})
            GROUP BY COALESCE(NULLIF(TRIM(CAST(volte_call AS CHAR)), ''), '<blank>');";

        await using var valueReader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await valueReader.ReadAsync(HttpContext.RequestAborted))
        {
            var value = Convert.ToString(valueReader.GetValue(0), CultureInfo.InvariantCulture) ?? "<blank>";
            var count = Convert.ToInt64(valueReader.GetValue(1), CultureInfo.InvariantCulture);
            summary.VolteCallValues[value] = count;
            summary.NetworkLogRows += count;
            if (value.Equals("<blank>", StringComparison.OrdinalIgnoreCase)) summary.VolteCallBlankRows += count;
            else summary.VolteNetworkRows += count;
            if (value.Equals("-1", StringComparison.OrdinalIgnoreCase)) summary.VolteCallMinusOneRows += count;
            if (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("active", StringComparison.OrdinalIgnoreCase))
                summary.VolteCallActiveRows += count;
        }
    }

    private static L3ReportMessage ToL3ReportMessage(DiagnosticTimelineRow row)
    {
        var detail = FirstNonEmpty(row.RawMessage, row.Summary, "");
        var fields = DiagnosticFieldParser.Resolve(new Dictionary<string, object?>
        { ["direction"] = row.Direction, ["channel"] = row.Channel, ["cause"] = row.Cause }, detail);
        return new(row.Id, row.SessionId, row.SourceType, row.TimestampLabel ?? "", row.Technology,
            fields.Direction ?? "", fields.Channel ?? "", row.Interface ?? row.Category ?? "", row.Message, detail);
    }

    private static byte[] BuildCombinedL3SummaryPdf(L3SummaryReport report, int reportRows)
    {
        var layout = new DiagnosticPdfLayout();
        layout.AddLine("L3 / Event Call Summary Report", "F2", 18);
        layout.AddWrapped($"Source File: {report.SourceFile}");
        layout.AddWrapped($"Report Scope: {report.Scope}");
        layout.AddWrapped($"Generated At: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        layout.AddWrapped($"Selected Rows: {report.Messages.Count}; Dashboard sources: {report.DashboardSources}; Network Log samples: {report.NetworkLogRows}.");
        layout.AddWrapped($"Exported message rows: {Math.Min(reportRows, report.Messages.Count)}. Dashboard uses all {report.Messages.Count} selected rows.");
        layout.AddSpacer();
        layout.AddLine("Call Summary", "F2", 14);
        layout.AddWrapped("Calls associated with filtered rows; outcomes use the complete loaded session window.");
        if (report.Calls.Count == 0) layout.AddWrapped("No call session is associated with the filtered signaling rows.");
        foreach (var c in report.Calls)
            layout.AddWrapped($"{c.Call} | {c.Technology} | {c.Start} - {c.End} | {c.Result} | Setup: {c.SetupTime}s | Duration: {c.Duration}s | {c.Reason}");
        layout.AddSpacer();
        layout.AddLine("Technology Summary", "F2", 14);
        foreach (var t in report.Technologies) layout.AddWrapped($"{t.Technology}: {t.Rows} rows | {t.Interfaces}");
        layout.AddSpacer();
        layout.AddLine("L3 Dashboard", "F2", 16);
        foreach (var (title, values) in new[] { ("KPI / Parameter", report.Kpis), ("Mobility KPI", report.Mobility), ("Decoded Parameters", report.Parameters) })
        {
            layout.AddLine(title, "F2", 12);
            foreach (var value in values)
            {
                layout.AddWrapped($"{value.Parameter}: {value.Result} [Source: {value.Source}]", "F2", 10);
                if (value.Observation.Length > 0) layout.AddWrapped(value.Observation, "F1", 9);
            }
            layout.AddSpacer();
        }
        layout.AddLine("Sheet Messages", "F2", 14);
        foreach (var m in report.Messages.Take(reportRows))
        {
            layout.AddWrapped($"{m.Timestamp} | {m.Direction} | {m.Channel} | {m.Interface} | {m.Message}", "F2", 9);
            layout.AddWrapped(m.Detail, "F1", 9);
            layout.AddSpacer(4);
        }
        return BuildFrontendStylePdf(layout);
    }
}


