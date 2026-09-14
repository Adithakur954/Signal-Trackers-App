using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
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
        bool pdf, int reportRows)
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
            var events = await LoadDiagnosticEventRowsAsync(conn, request.SessionIds, request.UploadId, request.Take + 1);
            var l3 = await LoadDiagnosticL3RowsAsync(conn, request.SessionIds, request.UploadId, request.Take + 1);
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
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
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
        layout.AddWrapped($"Selected Rows: {report.Messages.Count}; Sources: L3 and Event only.");
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
                layout.AddWrapped($"{value.Parameter}: {value.Result}", "F2", 10);
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
