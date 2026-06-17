using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Helper;
using SignalTracker.Models;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UnifiedMapReportController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly CommonFunction _cf;
        private readonly IWebHostEnvironment _env;

        public UnifiedMapReportController(
            ApplicationDbContext db,
            IHttpContextAccessor httpContextAccessor,
            IWebHostEnvironment env)
        {
            _db = db;
            _cf = new CommonFunction(db, httpContextAccessor);
            _env = env;
        }

        [HttpPost("Generate")]
        public async Task<IActionResult> Generate([FromBody] UnifiedMapPdfRequest request)
        {
            if (request == null)
                return BadRequest(new { Message = "Report request is required." });

            if (request.ProjectId <= 0)
                return BadRequest(new { Message = "ProjectId is required." });

            var project = await _db.tbl_project
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.id == request.ProjectId);

            var sessionIds = ResolveSessionIds(request.SessionIds, project?.ref_session_id);
            if (sessionIds.Count == 0)
                return BadRequest(new { Message = "No valid session IDs are available for this report." });

            var sessionIdInts = sessionIds
                .Where(x => x <= int.MaxValue && x >= int.MinValue)
                .Select(x => (int)x)
                .Distinct()
                .ToList();

            var rows = await _db.tbl_network_log
                .AsNoTracking()
                .Where(x => x.session_id.HasValue && sessionIdInts.Contains(x.session_id.Value))
                .OrderBy(x => x.timestamp)
                .Select(x => new UnifiedMapReportRow
                {
                    Id = x.id,
                    SessionId = x.session_id,
                    Timestamp = x.timestamp,
                    Lat = x.lat,
                    Lon = x.lon,
                    Network = x.network,
                    Provider = x.m_alpha_long,
                    Band = x.band,
                    Pci = x.pci,
                    Rssi = x.rssi,
                    Rsrp = x.rsrp,
                    Rsrq = x.rsrq,
                    Sinr = x.sinr,
                    Mos = x.mos,
                    Jitter = x.jitter,
                    Latency = x.latency,
                    PacketLoss = x.packet_loss,
                    DlTpt = x.dl_tpt,
                    UlTpt = x.ul_tpt,
                    Apps = x.apps,
                    AppName = x.app_name,
                    IndoorOutdoor = x.indoor_outdoor,
                    NodebId = x.nodeb_id,
                    CellId = x.cell_id
                })
                .Take(200_000)
                .ToListAsync();

            if (rows.Count == 0)
                return BadRequest(new { Message = "No drive logs found for the selected sessions." });

            var thresholds = await GetThresholdConfigAsync();
            var logo = LoadCompanyLogo();
            var report = UnifiedMapReportFactory.Create(
                request,
                project?.project_name,
                sessionIds,
                rows,
                thresholds,
                logo);

            var pdf = UnifiedMapRawPdfBuilder.Build(report);
            var filename = $"UnifiedMap_Report_{request.ProjectId}_{DateTime.Now:yyyy-MM-dd}.pdf";
            return File(pdf, "application/pdf", filename);
        }

        private async Task<ReportThresholdConfig> GetThresholdConfigAsync()
        {
            try
            {
                _cf.SessionCheck();
                var uid = _cf.UserId;

                var userSetting = await _db.thresholds
                    .AsNoTracking()
                    .Where(x => x.user_id == uid && x.is_default == 0)
                    .OrderByDescending(x => x.id)
                    .FirstOrDefaultAsync();

                if (userSetting != null)
                    return ReportThresholdConfig.FromDb(userSetting, "User custom settings");
            }
            catch
            {
                // If session/user resolution fails, continue with default settings.
            }

            var defaultSetting = await _db.thresholds
                .AsNoTracking()
                .Where(x => x.is_default == 1 && (x.user_id == null || x.user_id == 0))
                .OrderByDescending(x => x.id)
                .FirstOrDefaultAsync();

            if (defaultSetting != null)
                return ReportThresholdConfig.FromDb(defaultSetting, "Default DB settings");

            var fallback = await _db.thresholds
                .AsNoTracking()
                .OrderBy(x => x.id)
                .FirstOrDefaultAsync();

            return fallback != null
                ? ReportThresholdConfig.FromDb(fallback, "Fallback DB settings")
                : ReportThresholdConfig.Hardcoded();
        }

        private ReportLogo? LoadCompanyLogo()
        {
            var candidates = new[]
            {
                Path.Combine(_env.ContentRootPath, "wwwroot", "comp.jpeg"),
                Path.Combine(_env.ContentRootPath, "..", "StraceExeFron", "public", "comp.jpeg"),
                Path.Combine(_env.ContentRootPath, "..", "StraceExeFron", "src", "assets", "vinfocom.png")
            };

            foreach (var path in candidates)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!System.IO.File.Exists(fullPath)) continue;

                    var bytes = System.IO.File.ReadAllBytes(fullPath);
                    var (width, height) = UnifiedMapRawPdfBuilder.GetJpegDimensions(bytes);
                    if (width <= 0 || height <= 0) continue;
                    return new ReportLogo(bytes, width, height);
                }
                catch
                {
                    // Try next candidate.
                }
            }

            return null;
        }

        private static List<long> ResolveSessionIds(IEnumerable<long>? requestSessionIds, string? projectSessionIds)
        {
            var ids = new List<long>();

            if (requestSessionIds != null)
                ids.AddRange(requestSessionIds.Where(x => x > 0));

            if (!string.IsNullOrWhiteSpace(projectSessionIds))
            {
                ids.AddRange(
                    Regex.Split(projectSessionIds, @"[,\s;|]+")
                        .Select(x => long.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                        .Where(x => x > 0));
            }

            return ids.Distinct().ToList();
        }
    }

    public sealed class UnifiedMapPdfRequest
    {
        public int ProjectId { get; set; }
        public string? Title { get; set; }
        public string? GeneratedBy { get; set; }
        public List<long>? SessionIds { get; set; }
        public Dictionary<string, object?>? Summary { get; set; }
    }

    internal sealed class UnifiedMapReportRow
    {
        public int Id { get; set; }
        public int? SessionId { get; set; }
        public DateTime? Timestamp { get; set; }
        public float? Lat { get; set; }
        public float? Lon { get; set; }
        public string? Network { get; set; }
        public string? Provider { get; set; }
        public string? Band { get; set; }
        public string? Pci { get; set; }
        public float? Rssi { get; set; }
        public float? Rsrp { get; set; }
        public float? Rsrq { get; set; }
        public float? Sinr { get; set; }
        public float? Mos { get; set; }
        public float? Jitter { get; set; }
        public float? Latency { get; set; }
        public float? PacketLoss { get; set; }
        public string? DlTpt { get; set; }
        public string? UlTpt { get; set; }
        public string? Apps { get; set; }
        public string? AppName { get; set; }
        public string? IndoorOutdoor { get; set; }
        public string? NodebId { get; set; }
        public string? CellId { get; set; }
    }

    internal sealed class UnifiedMapReport
    {
        public string Title { get; set; } = "Unified Map Detail Report";
        public string CompanyName { get; set; } = "Vinfocom";
        public ReportLogo? Logo { get; set; }
        public int ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string GeneratedBy { get; set; } = "";
        public DateTimeOffset GeneratedAt { get; set; }
        public List<long> SessionIds { get; set; } = new();
        public int TotalRows { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public Dictionary<string, string> Summary { get; set; } = new();
        public List<ChartSeries> LineCharts { get; set; } = new();
        public List<BarChartData> BarCharts { get; set; } = new();
        public List<TableData> Tables { get; set; } = new();
    }

    internal sealed record ReportLogo(byte[] Bytes, int Width, int Height);

    internal sealed class ReportThresholdConfig
    {
        public string Source { get; set; } = "Hardcoded fallback";
        public double CoverageHoleLimit { get; set; } = -110;
        public List<ThresholdRange> Rsrp { get; set; } = new();
        public List<ThresholdRange> Rsrq { get; set; } = new();
        public List<ThresholdRange> Sinr { get; set; } = new();
        public List<ThresholdRange> Mos { get; set; } = new();

        public static ReportThresholdConfig FromDb(thresholds setting, string source)
        {
            var fallback = Hardcoded();
            return new ReportThresholdConfig
            {
                Source = source,
                CoverageHoleLimit =
                    setting.coveragehole_value ??
                    ParseDouble(setting.coveragehole_json) ??
                    fallback.CoverageHoleLimit,
                Rsrp = ParseRanges(setting.rsrp_json, fallback.Rsrp),
                Rsrq = ParseRanges(setting.rsrq_json, fallback.Rsrq),
                Sinr = ParseRanges(setting.sinr_json, fallback.Sinr),
                Mos = ParseRanges(setting.mos_json, fallback.Mos)
            };
        }

        public static ReportThresholdConfig Hardcoded()
        {
            return new ReportThresholdConfig
            {
                Source = "Hardcoded fallback",
                CoverageHoleLimit = -110,
                Rsrp = new List<ThresholdRange>
                {
                    new("Excellent", -80, -44),
                    new("Good", -90, -80),
                    new("Fair", -100, -90),
                    new("Poor", -110, -100),
                    new("Coverage Hole", -140, -110)
                },
                Rsrq = new List<ThresholdRange>
                {
                    new("Excellent", -10, -3),
                    new("Good", -15, -10),
                    new("Fair", -20, -15),
                    new("Poor", -34, -20)
                },
                Sinr = new List<ThresholdRange>
                {
                    new("Excellent", 20, 40),
                    new("Good", 10, 20),
                    new("Fair", 0, 10),
                    new("Poor", -20, 0)
                },
                Mos = new List<ThresholdRange>
                {
                    new("Excellent", 4, 5),
                    new("Good", 3, 4),
                    new("Fair", 2, 3),
                    new("Poor", 1, 2)
                }
            };
        }

        private static List<ThresholdRange> ParseRanges(string? json, List<ThresholdRange> fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return fallback;

                var ranges = new List<ThresholdRange>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var min = GetDouble(item, "min");
                    var max = GetDouble(item, "max");
                    if (!min.HasValue || !max.HasValue) continue;

                    var label =
                        GetString(item, "label") ??
                        GetString(item, "range") ??
                        GetString(item, "name") ??
                        $"{min:0.##} to {max:0.##}";

                    ranges.Add(new ThresholdRange(label, min.Value, max.Value));
                }

                return ranges.Count > 0 ? ranges : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static double? GetDouble(JsonElement item, string name)
        {
            if (!item.TryGetProperty(name, out var prop)) return null;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var number)) return number;
            if (prop.ValueKind == JsonValueKind.String) return ParseDouble(prop.GetString());
            return null;
        }

        private static string? GetString(JsonElement item, string name)
        {
            return item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }

        private static double? ParseDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"-?\d+(\.\d+)?");
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }
    }

    internal sealed record ThresholdRange(string Label, double Min, double Max)
    {
        public bool Contains(double value)
        {
            var low = Math.Min(Min, Max);
            var high = Math.Max(Min, Max);
            return value >= low && value <= high;
        }

        public string Display => $"{Label} ({Min:0.##} to {Max:0.##})";
    }

    internal sealed class ChartSeries
    {
        public string Title { get; set; } = "";
        public string Unit { get; set; } = "";
        public List<double> Values { get; set; } = new();
    }

    internal sealed class BarChartData
    {
        public string Title { get; set; } = "";
        public List<(string Label, double Value)> Items { get; set; } = new();
    }

    internal sealed class TableData
    {
        public string Title { get; set; } = "";
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
    }

    internal static class UnifiedMapReportFactory
    {
        public static UnifiedMapReport Create(
            UnifiedMapPdfRequest request,
            string? projectName,
            List<long> sessionIds,
            List<UnifiedMapReportRow> rows,
            ReportThresholdConfig thresholds,
            ReportLogo? logo)
        {
            var orderedRows = rows
                .OrderBy(x => x.Timestamp ?? DateTime.MinValue)
                .ThenBy(x => x.Id)
                .ToList();

            var report = new UnifiedMapReport
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? "Drive Test Analytics Report" : request.Title.Trim(),
                CompanyName = "Vinfocom",
                Logo = logo,
                ProjectId = request.ProjectId,
                ProjectName = string.IsNullOrWhiteSpace(projectName) ? $"Project {request.ProjectId}" : projectName.Trim(),
                GeneratedBy = request.GeneratedBy?.Trim() ?? "",
                GeneratedAt = DateTimeOffset.Now,
                SessionIds = sessionIds,
                TotalRows = orderedRows.Count,
                From = orderedRows.Select(x => x.Timestamp).Where(x => x.HasValue).Min(),
                To = orderedRows.Select(x => x.Timestamp).Where(x => x.HasValue).Max()
            };

            report.Summary = BuildSummary(report, orderedRows, thresholds);
            report.LineCharts.Add(BuildLineChart("RSRP Trend", "dBm", orderedRows.Select(x => x.Rsrp)));
            report.LineCharts.Add(BuildLineChart("RSRQ Trend", "dB", orderedRows.Select(x => x.Rsrq)));
            report.LineCharts.Add(BuildLineChart("SINR Trend", "dB", orderedRows.Select(x => x.Sinr)));
            report.LineCharts.Add(BuildLineChart("MOS Trend", "", orderedRows.Select(x => x.Mos)));
            report.LineCharts.Add(BuildLineChart("Downlink Throughput Trend", "", orderedRows.Select(x => ParseNumber(x.DlTpt))));
            report.LineCharts.Add(BuildLineChart("Uplink Throughput Trend", "", orderedRows.Select(x => ParseNumber(x.UlTpt))));

            report.BarCharts.Add(BuildBarChart("Technology Distribution", orderedRows.Select(x => ClassifyTechnology(x.Network)), 10));
            report.BarCharts.Add(BuildBarChart("Operator Distribution", orderedRows.Select(x => CleanGroup(x.Provider, "Unknown")), 10));
            report.BarCharts.Add(BuildBarChart("Band Distribution", orderedRows.Select(x => CleanGroup(x.Band, "Unknown")), 12));
            report.BarCharts.Add(BuildBarChart("PCI Distribution", orderedRows.Select(x => CleanGroup(x.Pci, "Unknown")), 12));
            report.BarCharts.Add(BuildBarChart("Indoor / Outdoor Distribution", orderedRows.Select(x => CleanGroup(x.IndoorOutdoor, "Unknown")), 6));
            report.BarCharts.Add(BuildBarChart("Application Distribution", orderedRows.SelectMany(x => SplitApps(x.Apps, x.AppName)), 12));
            report.BarCharts.Add(BuildHandoverChart(orderedRows));
            report.BarCharts.Add(BuildRangeChart("RSRP Quality Distribution", orderedRows.Select(x => x.Rsrp.HasValue ? (double?)x.Rsrp.Value : null), thresholds.Rsrp));
            report.BarCharts.Add(BuildRangeChart("RSRQ Quality Distribution", orderedRows.Select(x => x.Rsrq.HasValue ? (double?)x.Rsrq.Value : null), thresholds.Rsrq));
            report.BarCharts.Add(BuildRangeChart("SINR Quality Distribution", orderedRows.Select(x => x.Sinr.HasValue ? (double?)x.Sinr.Value : null), thresholds.Sinr));
            report.BarCharts.Add(BuildRangeChart("MOS Quality Distribution", orderedRows.Select(x => x.Mos.HasValue ? (double?)x.Mos.Value : null), thresholds.Mos));

            report.Tables.Add(BuildThresholdTable(thresholds));
            report.Tables.Add(BuildKpiTable(orderedRows));
            report.Tables.Add(BuildDriveLogTable(orderedRows));
            report.Tables.Add(BuildNetworkSiteTable(orderedRows));

            return report;
        }

        private static Dictionary<string, string> BuildSummary(
            UnifiedMapReport report,
            List<UnifiedMapReportRow> rows,
            ReportThresholdConfig thresholds)
        {
            var coverageHoleCount = rows.Count(x => x.Rsrp.HasValue && x.Rsrp.Value <= thresholds.CoverageHoleLimit);
            return new Dictionary<string, string>
            {
                ["Project"] = report.ProjectName,
                ["Sessions"] = string.Join(", ", report.SessionIds.Take(12)),
                ["Total drive logs"] = report.TotalRows.ToString("N0", CultureInfo.InvariantCulture),
                ["Date range"] = report.From.HasValue && report.To.HasValue
                    ? $"{report.From:yyyy-MM-dd HH:mm} to {report.To:yyyy-MM-dd HH:mm}"
                    : "N/A",
                ["Average RSRP"] = FormatAverage(rows.Select(x => x.Rsrp), "dBm"),
                ["Average RSRQ"] = FormatAverage(rows.Select(x => x.Rsrq), "dB"),
                ["Average SINR"] = FormatAverage(rows.Select(x => x.Sinr), "dB"),
                ["Average MOS"] = FormatAverage(rows.Select(x => x.Mos), ""),
                ["Average DL TPT"] = FormatAverage(rows.Select(x => ParseNumber(x.DlTpt)), ""),
                ["Average UL TPT"] = FormatAverage(rows.Select(x => ParseNumber(x.UlTpt)), ""),
                ["Threshold source"] = thresholds.Source,
                ["Coverage hole limit"] = $"{thresholds.CoverageHoleLimit:0.##} dBm",
                ["Coverage hole samples"] = $"{coverageHoleCount:N0} ({(rows.Count == 0 ? 0 : coverageHoleCount * 100.0 / rows.Count):0.##}%)"
            };
        }

        private static ChartSeries BuildLineChart(string title, string unit, IEnumerable<float?> source)
        {
            return BuildLineChart(title, unit, source.Select(x => x.HasValue ? (double?)x.Value : null));
        }

        private static ChartSeries BuildLineChart(string title, string unit, IEnumerable<double?> source)
        {
            var values = source
                .Where(x => x.HasValue && !double.IsNaN(x.Value) && !double.IsInfinity(x.Value))
                .Select(x => x!.Value)
                .ToList();

            return new ChartSeries
            {
                Title = title,
                Unit = unit,
                Values = Sample(values, 240)
            };
        }

        private static BarChartData BuildBarChart(string title, IEnumerable<string> groups, int take)
        {
            var items = groups
                .Select(x => CleanGroup(x, "Unknown"))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Label: g.Key, Value: (double)g.Count()))
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Label)
                .Take(take)
                .ToList();

            return new BarChartData { Title = title, Items = items };
        }

        private static BarChartData BuildRangeChart(string title, IEnumerable<double?> values, List<ThresholdRange> ranges)
        {
            var counts = ranges.ToDictionary(x => x.Display, _ => 0.0);
            var unknown = 0.0;

            foreach (var value in values.Where(x => x.HasValue).Select(x => x!.Value))
            {
                var match = ranges.FirstOrDefault(x => x.Contains(value));
                if (match == null) unknown++;
                else counts[match.Display] += 1;
            }

            var items = counts
                .Select(x => (Label: x.Key, Value: x.Value))
                .Where(x => x.Value > 0)
                .ToList();

            if (unknown > 0) items.Add(("Outside configured ranges", unknown));
            return new BarChartData { Title = title, Items = items };
        }

        private static BarChartData BuildHandoverChart(List<UnifiedMapReportRow> rows)
        {
            var ordered = rows
                .Where(x => x.Timestamp.HasValue)
                .OrderBy(x => x.Timestamp)
                .ThenBy(x => x.Id)
                .ToList();

            var tech = 0;
            var band = 0;
            var pci = 0;
            UnifiedMapReportRow? previous = null;

            foreach (var row in ordered)
            {
                if (previous != null)
                {
                    if (!Same(ClassifyTechnology(previous.Network), ClassifyTechnology(row.Network))) tech++;
                    if (!Same(previous.Band, row.Band)) band++;
                    if (!Same(previous.Pci, row.Pci)) pci++;
                }
                previous = row;
            }

            return new BarChartData
            {
                Title = "Handover / Change Summary",
                Items = new List<(string Label, double Value)>
                {
                    ("Technology changes", tech),
                    ("Band changes", band),
                    ("PCI changes", pci)
                }
            };
        }

        private static TableData BuildKpiTable(List<UnifiedMapReportRow> rows)
        {
            var metrics = new List<(string Name, IEnumerable<double?> Values, string Unit)>
            {
                ("RSRP", rows.Select(x => x.Rsrp.HasValue ? (double?)x.Rsrp.Value : null), "dBm"),
                ("RSRQ", rows.Select(x => x.Rsrq.HasValue ? (double?)x.Rsrq.Value : null), "dB"),
                ("SINR", rows.Select(x => x.Sinr.HasValue ? (double?)x.Sinr.Value : null), "dB"),
                ("MOS", rows.Select(x => x.Mos.HasValue ? (double?)x.Mos.Value : null), ""),
                ("Latency", rows.Select(x => x.Latency.HasValue ? (double?)x.Latency.Value : null), "ms"),
                ("Jitter", rows.Select(x => x.Jitter.HasValue ? (double?)x.Jitter.Value : null), "ms"),
                ("Packet Loss", rows.Select(x => x.PacketLoss.HasValue ? (double?)x.PacketLoss.Value : null), "%")
            };

            var table = new TableData
            {
                Title = "KPI Statistics",
                Headers = new List<string> { "Metric", "Average", "Minimum", "Maximum", "Samples" }
            };

            foreach (var metric in metrics)
            {
                var values = metric.Values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
                table.Rows.Add(new List<string>
                {
                    metric.Name,
                    values.Count == 0 ? "N/A" : $"{values.Average():0.##} {metric.Unit}".Trim(),
                    values.Count == 0 ? "N/A" : $"{values.Min():0.##} {metric.Unit}".Trim(),
                    values.Count == 0 ? "N/A" : $"{values.Max():0.##} {metric.Unit}".Trim(),
                    values.Count.ToString("N0", CultureInfo.InvariantCulture)
                });
            }

            return table;
        }

        private static TableData BuildThresholdTable(ReportThresholdConfig thresholds)
        {
            var table = new TableData
            {
                Title = "Configured KPI Ranges",
                Headers = new List<string> { "Metric", "Label", "Minimum", "Maximum", "Source" }
            };

            AddRanges(table, "RSRP", thresholds.Rsrp, thresholds.Source);
            AddRanges(table, "RSRQ", thresholds.Rsrq, thresholds.Source);
            AddRanges(table, "SINR", thresholds.Sinr, thresholds.Source);
            AddRanges(table, "MOS", thresholds.Mos, thresholds.Source);
            table.Rows.Add(new List<string> { "Coverage Hole", "Limit", thresholds.CoverageHoleLimit.ToString("0.##", CultureInfo.InvariantCulture), "dBm", thresholds.Source });
            return table;
        }

        private static void AddRanges(TableData table, string metric, List<ThresholdRange> ranges, string source)
        {
            foreach (var range in ranges)
            {
                table.Rows.Add(new List<string>
                {
                    metric,
                    range.Label,
                    range.Min.ToString("0.##", CultureInfo.InvariantCulture),
                    range.Max.ToString("0.##", CultureInfo.InvariantCulture),
                    source
                });
            }
        }

        private static TableData BuildDriveLogTable(List<UnifiedMapReportRow> rows)
        {
            var table = new TableData
            {
                Title = "Drive Log Sample",
                Headers = new List<string> { "Time", "Session", "Lat", "Lon", "Tech", "Operator", "Band", "PCI", "RSRP", "SINR" }
            };

            foreach (var row in rows.Take(42))
            {
                table.Rows.Add(new List<string>
                {
                    row.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    row.SessionId?.ToString(CultureInfo.InvariantCulture) ?? "",
                    row.Lat?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "",
                    row.Lon?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "",
                    ClassifyTechnology(row.Network),
                    CleanGroup(row.Provider, ""),
                    CleanGroup(row.Band, ""),
                    CleanGroup(row.Pci, ""),
                    row.Rsrp?.ToString("0.#", CultureInfo.InvariantCulture) ?? "",
                    row.Sinr?.ToString("0.#", CultureInfo.InvariantCulture) ?? ""
                });
            }

            return table;
        }

        private static TableData BuildNetworkSiteTable(List<UnifiedMapReportRow> rows)
        {
            var table = new TableData
            {
                Title = "Network Site Summary",
                Headers = new List<string> { "Band", "Operator", "NodeB ID", "Cell ID", "Samples" }
            };

            var grouped = rows
                .GroupBy(x => new
                {
                    Band = CleanGroup(x.Band, "Unknown"),
                    Operator = CleanGroup(x.Provider, "Unknown"),
                    Nodeb = CleanGroup(x.NodebId, "Unknown"),
                    Cell = CleanGroup(x.CellId, CleanGroup(x.Pci, "Unknown"))
                })
                .Select(g => new { g.Key.Band, g.Key.Operator, g.Key.Nodeb, g.Key.Cell, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Operator)
                .Take(32);

            foreach (var item in grouped)
            {
                table.Rows.Add(new List<string>
                {
                    item.Band,
                    item.Operator,
                    item.Nodeb,
                    item.Cell,
                    item.Count.ToString("N0", CultureInfo.InvariantCulture)
                });
            }

            return table;
        }

        private static List<double> Sample(List<double> values, int max)
        {
            if (values.Count <= max) return values;
            var sampled = new List<double>(max);
            for (var i = 0; i < max; i++)
            {
                var index = (int)Math.Round(i * (values.Count - 1) / (double)(max - 1));
                sampled.Add(values[index]);
            }
            return sampled;
        }

        private static IEnumerable<string> SplitApps(string? apps, string? appName)
        {
            var value = string.IsNullOrWhiteSpace(apps) ? appName : apps;
            if (string.IsNullOrWhiteSpace(value))
                return new[] { "Unknown" };

            return Regex.Split(value, @"[,;|]+")
                .Select(x => CleanGroup(x, "Unknown"))
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static string ClassifyTechnology(string? value)
        {
            var text = (value ?? "").Trim().ToUpperInvariant();
            if (text.Length == 0) return "Unknown";
            if (text.Contains("5G") || Regex.IsMatch(text, @"(^|[^A-Z0-9])NR([^A-Z0-9]|$)") || text.Contains("NSA") || text.Contains("ENDC")) return "5G";
            if (text.Contains("4G") || text.Contains("LTE")) return "4G";
            if (text.Contains("3G") || text.Contains("WCDMA") || text.Contains("UMTS") || text.Contains("HSPA")) return "3G";
            if (text.Contains("2G") || text.Contains("GSM") || text.Contains("EDGE") || text.Contains("GPRS")) return "2G";
            return value?.Trim() ?? "Unknown";
        }

        private static string CleanGroup(string? value, string fallback)
        {
            var text = (value ?? "").Trim();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static bool Same(string? left, string? right)
        {
            return string.Equals(CleanGroup(left, ""), CleanGroup(right, ""), StringComparison.OrdinalIgnoreCase);
        }

        private static double? ParseNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"-?\d+(\.\d+)?");
            if (!match.Success) return null;
            return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }

        private static string FormatAverage(IEnumerable<float?> values, string unit)
        {
            return FormatAverage(values.Select(x => x.HasValue ? (double?)x.Value : null), unit);
        }

        private static string FormatAverage(IEnumerable<double?> values, string unit)
        {
            var list = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            return list.Count == 0 ? "N/A" : $"{list.Average():0.##} {unit}".Trim();
        }
    }

    internal static class UnifiedMapRawPdfBuilder
    {
        private const double PageWidth = 842;
        private const double PageHeight = 595;
        private const double Margin = 40;

        public static byte[] Build(UnifiedMapReport report)
        {
            var contents = new List<byte[]>
            {
                BuildSummaryPage(report)
            };

            contents.AddRange(report.BarCharts.Select(chart => BuildBarChartPage(report, chart)));
            contents.AddRange(report.LineCharts.Where(x => x.Values.Count > 1).Select(chart => BuildLineChartPage(report, chart)));
            contents.AddRange(report.Tables.Select(table => BuildTablePage(report, table)));

            return WritePdf(contents, report);
        }

        private static byte[] BuildSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "Executive Summary", 1);
            var y = 480.0;
            foreach (var item in report.Summary.Take(14))
            {
                lines.Add(Text(60, y, 11, $"{item.Key}: {item.Value}"));
                y -= 24;
            }
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildBarChartPage(UnifiedMapReport report, BarChartData chart)
        {
            var lines = Header(report, chart.Title, 1);
            var items = chart.Items.Where(x => x.Value > 0).Take(14).ToList();

            if (items.Count == 0)
            {
                lines.Add(Text(60, 300, 12, "No data available."));
                return Ascii(string.Join("\n", lines) + "\n");
            }

            var max = items.Max(x => x.Value);
            var chartX = 230.0;
            var chartY = 455.0;
            var barMaxWidth = 500.0;
            var barHeight = 18.0;
            var gap = 13.0;

            foreach (var item in items)
            {
                var width = Math.Max(2, barMaxWidth * item.Value / max);
                lines.Add(FillColor(37, 99, 235));
                lines.Add(Rect(chartX, chartY - barHeight + 3, width, barHeight, true));
                lines.Add(FillColor(15, 23, 42));
                lines.Add(Text(60, chartY - 10, 9, Truncate(item.Label, 26)));
                lines.Add(Text(chartX + width + 8, chartY - 10, 9, item.Value.ToString("N0", CultureInfo.InvariantCulture)));
                chartY -= barHeight + gap;
            }

            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildLineChartPage(UnifiedMapReport report, ChartSeries chart)
        {
            var lines = Header(report, chart.Title, 1);
            var values = chart.Values;
            var min = values.Min();
            var max = values.Max();
            if (Math.Abs(max - min) < 0.0001)
            {
                max += 1;
                min -= 1;
            }

            var x = 70.0;
            var y = 95.0;
            var width = 700.0;
            var height = 360.0;
            lines.Add(StrokeColor(203, 213, 225));
            lines.Add(Rect(x, y, width, height, false));
            lines.Add(Text(x, y + height + 20, 9, $"Max: {max:0.##} {chart.Unit}".Trim()));
            lines.Add(Text(x, y - 20, 9, $"Min: {min:0.##} {chart.Unit}".Trim()));

            var points = new List<string>();
            for (var i = 0; i < values.Count; i++)
            {
                var px = x + (i * width / Math.Max(values.Count - 1, 1));
                var py = y + ((values[i] - min) / (max - min) * height);
                points.Add($"{Fmt(px)} {Fmt(py)}");
            }

            if (points.Count > 1)
            {
                lines.Add(StrokeColor(14, 165, 233));
                lines.Add("1.4 w");
                lines.Add($"{points[0]} m");
                for (var i = 1; i < points.Count; i++)
                    lines.Add($"{points[i]} l");
                lines.Add("S");
            }

            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildTablePage(UnifiedMapReport report, TableData table)
        {
            var lines = Header(report, table.Title, 1);
            var x = 35.0;
            var y = 485.0;
            var rowHeight = 18.0;
            var colWidth = (PageWidth - 70) / Math.Max(table.Headers.Count, 1);

            lines.Add(FillColor(226, 232, 240));
            lines.Add(Rect(x, y - 4, PageWidth - 70, rowHeight + 4, true));
            lines.Add(FillColor(15, 23, 42));

            for (var i = 0; i < table.Headers.Count; i++)
                lines.Add(Text(x + (i * colWidth) + 4, y + 1, 7.5, Truncate(table.Headers[i], 18)));

            y -= rowHeight;
            foreach (var row in table.Rows.Take(44))
            {
                for (var i = 0; i < table.Headers.Count && i < row.Count; i++)
                    lines.Add(Text(x + (i * colWidth) + 4, y + 1, 7, Truncate(row[i], 18)));
                lines.Add(StrokeColor(226, 232, 240));
                lines.Add($"{Fmt(x)} {Fmt(y - 4)} m {Fmt(PageWidth - 35)} {Fmt(y - 4)} l S");
                y -= rowHeight;
                if (y < 50) break;
            }

            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static List<string> Header(UnifiedMapReport report, string pageTitle, int pageNumber)
        {
            var lines = new List<string>
            {
                "q",
                FillColor(248, 250, 252),
                Rect(0, 0, PageWidth, PageHeight, true),
                FillColor(15, 23, 42),
            };

            if (report.Logo != null)
            {
                var logoWidth = 58.0;
                var logoHeight = logoWidth * report.Logo.Height / Math.Max(report.Logo.Width, 1);
                lines.Add($"q {Fmt(logoWidth)} 0 0 {Fmt(logoHeight)} {Fmt(Margin)} {Fmt(533)} cm /Logo Do Q");
            }

            lines.AddRange(new[]
            {
                Text(report.Logo == null ? Margin : 108, 558, 20, report.CompanyName),
                Text(report.Logo == null ? Margin : 108, 538, 9, "Drive Test Analytics Report"),
                Text(520, 552, 9, $"{report.ProjectName}"),
                Text(520, 536, 8, $"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm}" + (string.IsNullOrWhiteSpace(report.GeneratedBy) ? "" : $" by {report.GeneratedBy}")),
                StrokeColor(203, 213, 225),
                $"{Fmt(Margin)} 522 m {Fmt(PageWidth - Margin)} 522 l S",
                FillColor(30, 41, 59),
                Text(Margin, 500, 15, pageTitle),
                "Q"
            });

            return lines;
        }

        private static byte[] WritePdf(IReadOnlyList<byte[]> pageContents, UnifiedMapReport report)
        {
            using var stream = new MemoryStream();
            WriteAscii(stream, "%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");

            var hasLogo = report.Logo != null;
            var logoId = hasLogo ? 4 : 0;
            var firstPageObjectId = hasLogo ? 5 : 4;
            var objectCount = 3 + (hasLogo ? 1 : 0) + (pageContents.Count * 2);
            var offsets = new long[objectCount + 1];
            var pageIds = Enumerable.Range(0, pageContents.Count)
                .Select(i => firstPageObjectId + (i * 2) + 1)
                .ToList();

            WriteObj(stream, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
            WriteObj(stream, offsets, 2, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageContents.Count} >>");
            WriteObj(stream, offsets, 3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

            if (hasLogo && report.Logo != null)
            {
                WriteStreamObj(
                    stream,
                    offsets,
                    logoId,
                    $"<< /Type /XObject /Subtype /Image /Width {report.Logo.Width} /Height {report.Logo.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {report.Logo.Bytes.Length} >>",
                    report.Logo.Bytes);
            }

            for (var i = 0; i < pageContents.Count; i++)
            {
                var contentId = firstPageObjectId + (i * 2);
                var pageId = contentId + 1;
                var content = pageContents[i];
                var xObjectResources = hasLogo ? $" /XObject << /Logo {logoId} 0 R >>" : "";
                WriteStreamObj(stream, offsets, contentId, $"<< /Length {content.Length} >>", content);
                WriteObj(
                    stream,
                    offsets,
                    pageId,
                    $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(PageWidth)} {Fmt(PageHeight)}] /Resources << /Font << /F1 3 0 R >>{xObjectResources} >> /Contents {contentId} 0 R >>");
            }

            var xrefOffset = stream.Position;
            WriteAscii(stream, $"xref\n0 {offsets.Length}\n");
            WriteAscii(stream, "0000000000 65535 f \n");
            for (var i = 1; i < offsets.Length; i++)
                WriteAscii(stream, $"{offsets[i]:0000000000} 00000 n \n");
            WriteAscii(stream, $"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
            return stream.ToArray();
        }

        private static void WriteObj(Stream stream, long[] offsets, int id, string content)
        {
            offsets[id] = stream.Position;
            WriteAscii(stream, $"{id} 0 obj\n{content}\nendobj\n");
        }

        private static void WriteStreamObj(Stream stream, long[] offsets, int id, string dictionary, byte[] content)
        {
            offsets[id] = stream.Position;
            WriteAscii(stream, $"{id} 0 obj\n{dictionary}\nstream\n");
            stream.Write(content, 0, content.Length);
            WriteAscii(stream, "\nendstream\nendobj\n");
        }

        private static string Text(double x, double y, double size, string value)
        {
            return $"BT /F1 {Fmt(size)} Tf {Fmt(x)} {Fmt(y)} Td {PdfText(value)} Tj ET";
        }

        private static string Rect(double x, double y, double width, double height, bool fill)
        {
            return $"{Fmt(x)} {Fmt(y)} {Fmt(width)} {Fmt(height)} re {(fill ? "f" : "S")}";
        }

        private static string FillColor(byte r, byte g, byte b) => $"{Fmt(r / 255.0)} {Fmt(g / 255.0)} {Fmt(b / 255.0)} rg";

        private static string StrokeColor(byte r, byte g, byte b) => $"{Fmt(r / 255.0)} {Fmt(g / 255.0)} {Fmt(b / 255.0)} RG";

        private static string PdfText(string value)
        {
            var text = Regex.Replace(value ?? "", @"[^\u0020-\u007E]", " ");
            text = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            return $"({text})";
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? "";
            return value[..Math.Max(0, max - 3)] + "...";
        }

        private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

        private static void WriteAscii(Stream stream, string value)
        {
            var bytes = Ascii(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public static (int Width, int Height) GetJpegDimensions(byte[] bytes)
        {
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                return (0, 0);

            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                var marker = bytes[index + 1];
                index += 2;

                while (marker == 0xFF && index < bytes.Length)
                    marker = bytes[index++];

                if (marker == 0xD9 || marker == 0xDA)
                    break;

                if (index + 1 >= bytes.Length)
                    break;

                var length = (bytes[index] << 8) + bytes[index + 1];
                if (length < 2 || index + length > bytes.Length)
                    break;

                if (IsStartOfFrame(marker) && length >= 7)
                {
                    var height = (bytes[index + 3] << 8) + bytes[index + 4];
                    var width = (bytes[index + 5] << 8) + bytes[index + 6];
                    return (width, height);
                }

                index += length;
            }

            return (0, 0);
        }

        private static bool IsStartOfFrame(byte marker)
        {
            return marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
        }
    }
}
