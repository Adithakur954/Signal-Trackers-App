using System.Globalization;
using System.Data;
using System.Data.Common;
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

            var rows = await QueryReportRowsAsync(request, sessionIdInts);

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

        private async Task<List<UnifiedMapReportRow>> QueryReportRowsAsync(UnifiedMapPdfRequest request, List<int> sessionIds)
        {
            if (sessionIds.Count == 0)
                return new List<UnifiedMapReportRow>();

            var limit = request.Limit.HasValue
                ? Math.Clamp(request.Limit.Value, 1, 200_000)
                : 200_000;
            var page = request.Page.GetValueOrDefault(1) <= 0 ? 1 : request.Page.GetValueOrDefault(1);
            var offset = Math.Max(0, (page - 1) * limit);

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(HttpContext.RequestAborted);

            await using var command = conn.CreateCommand();
            var (whereClause, parameters) = await BuildReportSqlWhereAsync(request, sessionIds, command);
            command.CommandText = $@"
                SELECT
                    id, session_id, timestamp, lat, lon, network,
                    COALESCE(
                        NULLIF(TRIM(BOTH CHAR(39) FROM TRIM(BOTH '""' FROM TRIM(m_alpha_short))), ''),
                        TRIM(BOTH CHAR(39) FROM TRIM(BOTH '""' FROM TRIM(m_alpha_long)))
                    ) AS provider_name,
                    band, pci, rssi, rsrp, rsrq, sinr, mos, jitter, latency,
                    packet_loss, dl_tpt, ul_tpt, apps, app_name, indoor_outdoor, nodeb_id, cell_id
                FROM tbl_network_log
                WHERE {whereClause}
                ORDER BY timestamp, id
                LIMIT @limit OFFSET @offset;";

            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);

            AddParam(command, "@limit", limit);
            AddParam(command, "@offset", offset);

            var rows = new List<UnifiedMapReportRow>();
            await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                rows.Add(new UnifiedMapReportRow
                {
                    Id = ReadInt(reader, "id") ?? 0,
                    SessionId = ReadInt(reader, "session_id"),
                    Timestamp = ReadDateTime(reader, "timestamp"),
                    Lat = ReadFloat(reader, "lat"),
                    Lon = ReadFloat(reader, "lon"),
                    Network = ReadString(reader, "network"),
                    Provider = CleanProvider(ReadString(reader, "provider_name")),
                    Band = ReadString(reader, "band"),
                    Pci = ReadString(reader, "pci"),
                    Rssi = ReadFloat(reader, "rssi"),
                    Rsrp = ClampKpi(ReadFloat(reader, "rsrp"), -140, -44),
                    Rsrq = ClampKpi(ReadFloat(reader, "rsrq"), -34, 3),
                    Sinr = ClampKpi(ReadFloat(reader, "sinr"), -23, 40),
                    Mos = ReadFloat(reader, "mos"),
                    Jitter = ReadFloat(reader, "jitter"),
                    Latency = ReadFloat(reader, "latency"),
                    PacketLoss = ReadFloat(reader, "packet_loss"),
                    DlTpt = ReadString(reader, "dl_tpt"),
                    UlTpt = ReadString(reader, "ul_tpt"),
                    Apps = ReadString(reader, "apps"),
                    AppName = ReadString(reader, "app_name"),
                    IndoorOutdoor = ReadString(reader, "indoor_outdoor"),
                    NodebId = ReadString(reader, "nodeb_id"),
                    CellId = ReadString(reader, "cell_id")
                });
            }

            return rows;
        }

        private async Task<(string Clause, List<DbParameter> Parameters)> BuildReportSqlWhereAsync(
            UnifiedMapPdfRequest request,
            List<int> sessionIds,
            DbCommand command)
        {
            var clauses = new List<string>();
            var parameters = new List<DbParameter>();
            var idParams = new List<string>();

            for (var i = 0; i < sessionIds.Count; i++)
            {
                var name = $"@sid{i}";
                idParams.Add(name);
                parameters.Add(CreateParam(command, name, sessionIds[i]));
            }

            clauses.Add(idParams.Count > 0 ? $"session_id IN ({string.Join(",", idParams)})" : "1 = 0");
            clauses.Add("COALESCE(NULLIF(TRIM(m_alpha_short), ''), NULLIF(TRIM(m_alpha_long), '')) IS NOT NULL");
            clauses.Add("NULLIF(TRIM(band), '') IS NOT NULL");

            var provider = request.Provider?.Trim();
            if (!string.IsNullOrWhiteSpace(provider))
            {
                clauses.Add("COALESCE(NULLIF(TRIM(m_alpha_short), ''), m_alpha_long) LIKE @provider");
                parameters.Add(CreateParam(command, "@provider", $"%{provider}%"));
            }

            const string wifiPredicate = @"(
                primary_cell_info_1 LIKE 'SSID:%'
                OR primary_cell_info_1 LIKE '%BSSID:%'
                OR EXISTS (
                    SELECT 1
                    FROM tbl_session s
                    WHERE s.id = tbl_network_log.session_id
                      AND LOWER(COALESCE(s.type, '')) = 'wifi'
                )
            )";
            const string registeredCellPredicate = "primary_cell_info_1 LIKE '%mRegistered=YES%'";
            const string fiveGCellPredicate = @"(
                UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%5G%'
                OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%NRARFCN%'
                OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%MNR%'
                OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) LIKE '%NCI%'
                OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) REGEXP '(^|[^A-Z0-9])NR([^A-Z0-9]|$)'
                OR UPPER(CONCAT_WS(' ', COALESCE(network, ''), COALESCE(band, ''), COALESCE(primary_cell_info_1, ''), COALESCE(all_neigbor_cell_info, ''))) REGEXP '(^|[^A-Z0-9])N[0-9]{1,3}([^A-Z0-9]|$)'
            )";

            var networkType = request.NetworkType?.Trim();
            if (!string.IsNullOrWhiteSpace(networkType) &&
                !networkType.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (networkType.Equals("wifi", StringComparison.OrdinalIgnoreCase) ||
                    networkType.Equals("wi-fi", StringComparison.OrdinalIgnoreCase))
                {
                    clauses.Add(wifiPredicate);
                }
                else if (networkType.Equals("5g", StringComparison.OrdinalIgnoreCase) ||
                         networkType.Equals("5g nsa", StringComparison.OrdinalIgnoreCase) ||
                         networkType.Equals("nr", StringComparison.OrdinalIgnoreCase))
                {
                    clauses.Add(fiveGCellPredicate);
                }
                else
                {
                    clauses.Add("network IS NOT NULL AND network LIKE @networkType");
                    clauses.Add(registeredCellPredicate);
                    parameters.Add(CreateParam(command, "@networkType", $"%{networkType}%"));
                }
            }
            else
            {
                clauses.Add($"({registeredCellPredicate} OR {wifiPredicate} OR {fiveGCellPredicate})");
            }

            if (request.StartDate.HasValue)
            {
                clauses.Add("timestamp >= @from");
                parameters.Add(CreateParam(command, "@from", request.StartDate.Value));
            }

            if (request.EndDate.HasValue)
            {
                clauses.Add("timestamp < @to");
                parameters.Add(CreateParam(command, "@to", request.EndDate.Value.AddDays(1)));
            }

            var projectPolygonWkt = await ResolveProjectFilterWktAsync(request.ProjectId);
            if (!string.IsNullOrWhiteSpace(projectPolygonWkt))
            {
                clauses.Add("lat IS NOT NULL AND lon IS NOT NULL");
                clauses.Add("ST_Contains(ST_GeomFromText(@projectPolygonWkt, 4326), ST_SRID(POINT(lon, lat), 4326))");
                parameters.Add(CreateParam(command, "@projectPolygonWkt", projectPolygonWkt));
            }

            return (string.Join(" AND ", clauses), parameters);
        }

        private async Task<string?> ResolveProjectFilterWktAsync(int projectId)
        {
            if (projectId <= 0)
                return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(HttpContext.RequestAborted);

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT polygon_wkt
                FROM (
                    SELECT
                        ST_AsText(p.polygon) AS polygon_wkt,
                        1 AS prio,
                        0 AS row_order
                    FROM tbl_project p
                    WHERE p.id = @pid

                    UNION ALL

                    SELECT
                        ST_AsText(mr.region) AS polygon_wkt,
                        2 AS prio,
                        mr.id AS row_order
                    FROM map_regions mr
                    WHERE mr.tbl_project_id = @pid
                ) src
                WHERE polygon_wkt IS NOT NULL AND polygon_wkt <> ''
                ORDER BY prio ASC, row_order DESC
                LIMIT 1;";
            AddParam(command, "@pid", projectId);

            var result = await command.ExecuteScalarAsync(HttpContext.RequestAborted);
            return result == null || result == DBNull.Value
                ? null
                : Convert.ToString(result, CultureInfo.InvariantCulture)?.Trim();
        }

        private static DbParameter CreateParam(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            return parameter;
        }

        private static void AddParam(DbCommand command, string name, object? value)
        {
            command.Parameters.Add(CreateParam(command, name, value));
        }

        private static string CleanProvider(string? value)
        {
            return (value ?? "").Trim().Trim('"').Trim('\'');
        }

        private static string? ReadString(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static int? ReadInt(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static float? ReadFloat(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : Convert.ToSingle(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static float? ClampKpi(float? value, float min, float max)
        {
            if (!value.HasValue) return null;
            return Math.Min(Math.Max(value.Value, min), max);
        }

        private static DateTime? ReadDateTime(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
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
        public string? NetworkType { get; set; } = "ALL";
        public string? Provider { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? Page { get; set; }
        public int? Limit { get; set; }
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
        public List<UnifiedMapReportRow> Rows { get; set; } = new();
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
                To = orderedRows.Select(x => x.Timestamp).Where(x => x.HasValue).Max(),
                Rows = orderedRows
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
        private const double PageWidth = 596;
        private const double PageHeight = 842;
        private const double Margin = 40;

        public static byte[] Build(UnifiedMapReport report)
        {
            var contents = new List<byte[]>
            {
                BuildCoverPage(report),
                BuildTableOfContentsPage(report),
                BuildIntroductionPage(report),
                BuildAreaSummaryPage(report),
                BuildDriveAndKpiSummaryPage(report),
                BuildMapViewPage(report, "a) Band", BuildBandNarrative(report), FindBarChart(report, "Band Distribution")),
                BuildMapViewPage(report, "b) RSRP", BuildMetricNarrative(report, "RSRP", "Reference Signal Received Power", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrp))), "dBm", -105, "falling below -105 dBm")),
                BuildMapViewPage(report, "c) RSRQ", BuildMetricNarrative(report, "RSRQ", "Reference Signal Received Quality", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrq))), "dB", -14, "falling below -14 dB")),
                BuildMapViewPage(report, "d) SINR", BuildMetricNarrative(report, "SINR", "Signal-to-Interference Noise Ratio", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Sinr))), "dB", 5, "falling below 5 dB")),
                BuildMapViewPage(report, "e) DL Throughput", BuildMetricNarrative(report, "DL throughput", "Downlink throughput", MetricStats(report.Rows.Select(x => ParseNumber(x.DlTpt))), "Mbps", 10, "falling below 10 Mbps")),
                BuildMapViewPage(report, "f) UL Throughput", BuildMetricNarrative(report, "UL throughput", "Uplink throughput", MetricStats(report.Rows.Select(x => ParseNumber(x.UlTpt))), "Mbps", 5, "falling below 5 Mbps")),
                BuildPciSummaryPage(report),
                BuildPciDetailsPage(report),
                BuildPerformanceSummaryPage(report)
            };

            return WritePdf(contents, report);
        }

        private static byte[] BuildCoverPage(UnifiedMapReport report)
        {
            var lines = PageBackground();
            lines.Add(FillColor(15, 23, 42));
            lines.Add(Text(178, 520, 24, "Drive Test Report"));
            lines.Add(Text(210, 490, 13, report.ProjectName));
            lines.Add(Text(202, 452, 11, $"Generated on {report.GeneratedAt:MMMM dd, yyyy}"));
            lines.Add(StrokeColor(37, 99, 235));
            lines.Add("2 w");
            lines.Add($"{Fmt(170)} {Fmt(475)} m {Fmt(426)} {Fmt(475)} l S");
            if (report.Logo != null)
            {
                var logoWidth = 82.0;
                var logoHeight = logoWidth * report.Logo.Height / Math.Max(report.Logo.Width, 1);
                lines.Add($"q {Fmt(logoWidth)} 0 0 {Fmt(logoHeight)} {Fmt((PageWidth - logoWidth) / 2)} {Fmt(610)} cm /Logo Do Q");
            }
            lines.Add(FillColor(71, 85, 105));
            lines.Add(Text(185, 95, 9, report.CompanyName));
            lines.Add("Q");
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildTableOfContentsPage(UnifiedMapReport report)
        {
            var lines = Header(report, "Table of Contents");
            var y = 710.0;
            var entries = new[]
            {
                ("1. Introduction", "3"),
                ("2. Area Summary", "4"),
                ("3. Drive Summary", "5"),
                ("4. KPI Summary", "5"),
                ("5. Map View", "6"),
                ("   a) Band", "6"),
                ("   b) RSRP", "7"),
                ("   c) RSRQ", "8"),
                ("   d) SINR", "9"),
                ("   e) DL Throughput", "10"),
                ("   f) UL Throughput", "11"),
                ("6. PCI Summary", "12"),
                ("   a) Top PCI Values", "13"),
                ("   b) PCI with Poor RSRP", "13"),
                ("   c) PCI with Poor RSRQ", "13"),
                ("7. Performance Summary", "14")
            };

            foreach (var entry in entries)
            {
                lines.Add(Text(Margin, y, 11, entry.Item1));
                lines.Add(Text(PageWidth - Margin - 20, y, 11, entry.Item2));
                y -= 28;
            }
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildIntroductionPage(UnifiedMapReport report)
        {
            var lines = Header(report, "1. Introduction");
            var y = 710.0;
            AddWrapped(lines, Margin, ref y, $"This drive test report provides insights into network performance for {report.ProjectName}. Drive testing is essential for evaluating signal strength, coverage, quality, and provider performance, enabling actionable recommendations for optimization and deployment planning.", 11, 86);
            y -= 12;
            AddWrapped(lines, Margin, ref y, "The report highlights areas of strong and weak coverage, summarizes key radio and service KPIs, and presents PCI and performance summaries to support data-driven network improvement.", 11, 86);
            AddSectionTitle(lines, "2. Area Summary", ref y);
            AddWrapped(lines, Margin, ref y, "Drive route coverage is summarized from available session samples and spatial distribution. Use this section with the map layers in Unified Map to identify operational areas, dense sample clusters, and marked route segments.", 11, 86);

            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildAreaSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "2. Area Summary");
            var y = 710.0;
            AddWrapped(lines, Margin, ref y, "Drive route covers key operational areas identified from collected GPS samples and session density.", 11, 86);
            y -= 10;
            AddSectionTitle(lines, "Hotspots & Marked Locations", ref y, 13);
            AddWrapped(lines, Margin, ref y, BuildCoordinateSummary(report), 11, 86);
            y -= 8;
            AddSectionTitle(lines, "Major Areas Covered", ref y, 13);
            AddWrapped(lines, Margin, ref y, $"The drive covered {report.TotalRows:N0} samples across {report.SessionIds.Count:N0} session(s). Latitude and longitude ranges are included when valid GPS points are available.", 11, 86);
            AddGpsRange(lines, report, ref y);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildDriveAndKpiSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "3. Drive Summary");
            var y = 710.0;
            var days = report.From.HasValue && report.To.HasValue
                ? Math.Max(1, (int)Math.Ceiling((report.To.Value.Date - report.From.Value.Date).TotalDays) + 1)
                : 0;
            var period = report.From.HasValue && report.To.HasValue
                ? $"from {report.From:yyyy-MM-dd HH:mm} to {report.To:yyyy-MM-dd HH:mm}"
                : "from an unspecified start date to an unspecified end date";
            AddWrapped(lines, Margin, ref y, $"The drive test was conducted over {(days == 0 ? "an unspecified number of" : days.ToString(CultureInfo.InvariantCulture))} day(s) {period}, with {report.TotalRows:N0} samples collected across {report.SessionIds.Count:N0} session(s).", 11, 86);
            AddSectionTitle(lines, "4. KPI Summary", ref y);
            AddWrapped(lines, Margin, ref y, "Network KPI metrics including coverage, quality, throughput, latency, jitter, and packet loss were analyzed across the drive route. Detailed KPI observations are provided in the Map View and Performance Summary sections.", 11, 86);
            y -= 12;
            DrawTable(lines, Margin, ref y, new[] { "Metric", "Average", "Minimum", "Maximum", "Samples" }, BuildKpiRows(report).Take(8).ToList(), 10);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildMapViewPage(UnifiedMapReport report, string subsection, string narrative, BarChartData? chart = null)
        {
            var lines = Header(report, $"5. Map View - {subsection}");
            var y = 710.0;
            AddWrapped(lines, Margin, ref y, narrative, 11, 86);
            if (chart != null)
            {
                y -= 16;
                DrawBarChart(lines, chart, Margin, y - 20, PageWidth - (Margin * 2), 260);
            }
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildPciSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "6. PCI Summary");
            var y = 710.0;
            var pciGroups = PciGroups(report).ToList();
            var unique = pciGroups.Count;
            var top30 = pciGroups.Take(30).Sum(x => x.Count);
            var percent = report.TotalRows == 0 ? 0 : top30 * 100.0 / report.TotalRows;
            AddWrapped(lines, Margin, ref y, $"The network utilized a total of {unique:N0} unique PCI values during the drive test. The top 30 PCI values accounted for {percent:0.##}% of samples, indicating the concentration of PCI distribution across the measured route.", 11, 86);
            y -= 20;
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Samples", "Share" }, pciGroups.Take(12).Select(x => new List<string> { x.Pci, x.Count.ToString("N0", CultureInfo.InvariantCulture), $"{(report.TotalRows == 0 ? 0 : x.Count * 100.0 / report.TotalRows):0.##}%" }).ToList(), 10);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildPciDetailsPage(UnifiedMapReport report)
        {
            var lines = Header(report, "6. PCI Summary - Details");
            var y = 710.0;
            AddSectionTitle(lines, "a) Top 30 PCI Values", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Samples" }, PciGroups(report).Take(10).Select(x => new List<string> { x.Pci, x.Count.ToString("N0", CultureInfo.InvariantCulture) }).ToList(), 9);
            y -= 14;
            AddSectionTitle(lines, "b) PCI with Poor RSRP", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Poor RSRP Samples" }, PoorPciGroups(report, x => x.Rsrp.HasValue && x.Rsrp.Value < -105).Take(8).Select(x => new List<string> { x.Pci, x.Count.ToString("N0", CultureInfo.InvariantCulture) }).ToList(), 9);
            y -= 14;
            AddSectionTitle(lines, "c) PCI with Poor RSRQ", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Poor RSRQ Samples" }, PoorPciGroups(report, x => x.Rsrq.HasValue && x.Rsrq.Value < -14).Take(8).Select(x => new List<string> { x.Pci, x.Count.ToString("N0", CultureInfo.InvariantCulture) }).ToList(), 9);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildPerformanceSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "7. Performance Summary");
            var y = 710.0;
            AddSectionTitle(lines, "a) Network Quality Metrics", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "Metric", "Average", "Poor Samples" }, BuildQualityRows(report), 9);
            y -= 16;
            AddSectionTitle(lines, "b) Speed Metrics", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "Metric", "Average", "Slow Samples" }, BuildSpeedRows(report), 9);
            y -= 16;
            AddSectionTitle(lines, "c) Latency Distribution", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "Range", "Samples" }, BuildRangeRows(report.Rows.Select(x => ToNullableDouble(x.Latency)), new[] { 50.0, 100.0, 200.0 }), 9);
            y -= 16;
            AddSectionTitle(lines, "d) Jitter Distribution", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "Range", "Samples" }, BuildRangeRows(report.Rows.Select(x => ToNullableDouble(x.Jitter)), new[] { 10.0, 30.0, 50.0 }), 9);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static List<string> PageBackground()
        {
            return new List<string>
            {
                "q",
                FillColor(255, 255, 255),
                Rect(0, 0, PageWidth, PageHeight, true)
            };
        }

        private static List<string> Header(UnifiedMapReport report, string pageTitle)
        {
            var lines = PageBackground();
            if (report.Logo != null)
            {
                var logoWidth = 52.0;
                var logoHeight = logoWidth * report.Logo.Height / Math.Max(report.Logo.Width, 1);
                lines.Add($"q {Fmt(logoWidth)} 0 0 {Fmt(logoHeight)} {Fmt(Margin)} {Fmt(764)} cm /Logo Do Q");
            }

            lines.AddRange(new[]
            {
                FillColor(15, 23, 42),
                Text(report.Logo == null ? Margin : 105, 792, 13, "Drive Test Report"),
                FillColor(71, 85, 105),
                Text(report.Logo == null ? Margin : 105, 775, 8.5, report.ProjectName),
                StrokeColor(203, 213, 225),
                $"{Fmt(Margin)} 748 m {Fmt(PageWidth - Margin)} 748 l S",
                FillColor(15, 23, 42),
                Text(Margin, 725, 16, pageTitle),
                "Q"
            });

            return lines;
        }

        private static void AddSectionTitle(List<string> lines, string title, ref double y, double size = 15)
        {
            y -= 28;
            lines.Add(FillColor(15, 23, 42));
            lines.Add(Text(Margin, y, size, title));
            y -= 24;
        }

        private static void AddWrapped(List<string> lines, double x, ref double y, string text, double size, int maxChars)
        {
            foreach (var line in Wrap(text, maxChars))
            {
                lines.Add(FillColor(30, 41, 59));
                lines.Add(Text(x, y, size, line));
                y -= size + 6;
            }
        }

        private static IEnumerable<string> Wrap(string text, int maxChars)
        {
            var words = Regex.Split(text ?? "", @"\s+").Where(x => x.Length > 0);
            var line = "";
            foreach (var word in words)
            {
                if (line.Length == 0)
                {
                    line = word;
                    continue;
                }

                if (line.Length + word.Length + 1 > maxChars)
                {
                    yield return line;
                    line = word;
                }
                else
                {
                    line += " " + word;
                }
            }

            if (line.Length > 0)
                yield return line;
        }

        private static BarChartData? FindBarChart(UnifiedMapReport report, string title)
        {
            return report.BarCharts.FirstOrDefault(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildBandNarrative(UnifiedMapReport report)
        {
            var chart = FindBarChart(report, "Band Distribution");
            var top = chart?.Items.Where(x => x.Value > 0).Take(3).ToList() ?? new List<(string Label, double Value)>();
            if (top.Count == 0)
                return "The network utilized available serving bands during the drive test. No band distribution data was available for the selected samples.";

            var parts = top.Select(x => $"{x.Label} at {(report.TotalRows == 0 ? 0 : x.Value * 100.0 / report.TotalRows):0.##}%");
            return $"The network utilized various frequency bands during the drive test. The {top[0].Label} band accounted for {(report.TotalRows == 0 ? 0 : top[0].Value * 100.0 / report.TotalRows):0.##}% of samples, followed by {string.Join(", ", parts.Skip(1))}. These bands played a significant role in maintaining network coverage and capacity.";
        }

        private static string BuildMetricNarrative(UnifiedMapReport report, string metric, string description, MetricSummary stats, string unit, double poorLimit, string poorText)
        {
            if (stats.Count == 0)
                return $"{metric} ({description}) was analyzed across the drive route, but no valid samples were available for this metric.";

            var poorCount = report.Rows.Count(x =>
            {
                var value = metric.StartsWith("DL", StringComparison.OrdinalIgnoreCase) ? ParseNumber(x.DlTpt)
                    : metric.StartsWith("UL", StringComparison.OrdinalIgnoreCase) ? ParseNumber(x.UlTpt)
                    : metric == "RSRP" ? ToNullableDouble(x.Rsrp)
                    : metric == "RSRQ" ? ToNullableDouble(x.Rsrq)
                    : metric == "SINR" ? ToNullableDouble(x.Sinr)
                    : null;

                return value.HasValue && (metric == "RSRP" || metric == "RSRQ" ? value.Value < poorLimit : value.Value < poorLimit);
            });

            var poorPercent = report.TotalRows == 0 ? 0 : poorCount * 100.0 / report.TotalRows;
            var performance = poorPercent >= 60 ? "poor" : poorPercent >= 25 ? "moderate" : "strong";
            return $"{metric} ({description}) is a key indicator for network performance. The measured values show an average of {stats.Average:0.##} {unit}, ranging from {stats.Min:0.##} to {stats.Max:0.##} {unit}. The network demonstrates {performance} {metric} performance with {poorCount:N0} samples ({poorPercent:0.##}%) {poorText}.";
        }

        private static string BuildCoordinateSummary(UnifiedMapReport report)
        {
            var points = report.Rows
                .Where(x => x.Lat.HasValue && x.Lon.HasValue && x.Lat.Value is >= -90 and <= 90 && x.Lon.Value is >= -180 and <= 180)
                .Select(x => new { Lat = x.Lat!.Value, Lon = x.Lon!.Value })
                .ToList();

            if (points.Count == 0)
                return "No valid GPS coordinates were available in the selected drive samples.";

            return $"Crowded and high-traffic locations should be reviewed around the densest measured route segments. Valid GPS samples span approximately {points.Min(x => x.Lat):0.000000} to {points.Max(x => x.Lat):0.000000} latitude and {points.Min(x => x.Lon):0.000000} to {points.Max(x => x.Lon):0.000000} longitude.";
        }

        private static void AddGpsRange(List<string> lines, UnifiedMapReport report, ref double y)
        {
            var points = report.Rows
                .Where(x => x.Lat.HasValue && x.Lon.HasValue && x.Lat.Value is >= -90 and <= 90 && x.Lon.Value is >= -180 and <= 180)
                .ToList();
            if (points.Count == 0) return;

            y -= 14;
            DrawTable(lines, Margin, ref y, new[] { "GPS Summary", "Value" }, new List<List<string>>
            {
                new() { "Valid GPS samples", points.Count.ToString("N0", CultureInfo.InvariantCulture) },
                new() { "Latitude range", $"{points.Min(x => x.Lat):0.000000} to {points.Max(x => x.Lat):0.000000}" },
                new() { "Longitude range", $"{points.Min(x => x.Lon):0.000000} to {points.Max(x => x.Lon):0.000000}" }
            }, 10);
        }

        private static void DrawBarChart(List<string> lines, BarChartData chart, double x, double y, double width, double height)
        {
            var items = chart.Items.Where(x => x.Value > 0).Take(8).ToList();
            if (items.Count == 0) return;

            var max = items.Max(x => x.Value);
            var labelWidth = 128.0;
            var barWidth = width - labelWidth - 70;
            var rowHeight = Math.Min(28, height / items.Count);
            lines.Add(FillColor(15, 23, 42));
            lines.Add(Text(x, y + 24, 12, chart.Title));

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var rowY = y - (i * rowHeight);
                var filled = Math.Max(2, barWidth * item.Value / max);
                lines.Add(FillColor(51, 102, 204));
                lines.Add(Rect(x + labelWidth, rowY - 8, filled, 12, true));
                lines.Add(FillColor(30, 41, 59));
                lines.Add(Text(x, rowY - 6, 9, Truncate(item.Label, 22)));
                lines.Add(Text(x + labelWidth + filled + 8, rowY - 6, 9, item.Value.ToString("N0", CultureInfo.InvariantCulture)));
            }
        }

        private static void DrawTable(List<string> lines, double x, ref double y, IReadOnlyList<string> headers, IReadOnlyList<List<string>> rows, double size)
        {
            if (headers.Count == 0) return;

            var rowHeight = size + 10;
            var tableWidth = PageWidth - (Margin * 2);
            var colWidth = tableWidth / headers.Count;
            lines.Add(FillColor(226, 232, 240));
            lines.Add(Rect(x, y - 6, tableWidth, rowHeight, true));
            lines.Add(FillColor(15, 23, 42));
            for (var i = 0; i < headers.Count; i++)
                lines.Add(Text(x + (i * colWidth) + 5, y, size, Truncate(headers[i], 18)));

            y -= rowHeight;
            foreach (var row in rows)
            {
                lines.Add(StrokeColor(226, 232, 240));
                lines.Add($"{Fmt(x)} {Fmt(y - 7)} m {Fmt(x + tableWidth)} {Fmt(y - 7)} l S");
                lines.Add(FillColor(30, 41, 59));
                for (var i = 0; i < headers.Count && i < row.Count; i++)
                    lines.Add(Text(x + (i * colWidth) + 5, y, size - 1, Truncate(row[i], 22)));
                y -= rowHeight;
                if (y < 70) break;
            }
        }

        private static void DrawTable(List<string> lines, double x, ref double y, IReadOnlyList<string> headers, List<List<string>> rows, double size)
        {
            DrawTable(lines, x, ref y, headers, (IReadOnlyList<List<string>>)rows, size);
        }

        private static List<List<string>> BuildKpiRows(UnifiedMapReport report)
        {
            var metrics = new List<(string Name, MetricSummary Stats, string Unit)>
            {
                ("RSRP", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrp))), "dBm"),
                ("RSRQ", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrq))), "dB"),
                ("SINR", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Sinr))), "dB"),
                ("MOS", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Mos))), ""),
                ("DL Throughput", MetricStats(report.Rows.Select(x => ParseNumber(x.DlTpt))), "Mbps"),
                ("UL Throughput", MetricStats(report.Rows.Select(x => ParseNumber(x.UlTpt))), "Mbps"),
                ("Latency", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Latency))), "ms"),
                ("Jitter", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Jitter))), "ms")
            };

            return metrics.Select(x => new List<string>
            {
                x.Name,
                FormatStat(x.Stats.Average, x.Stats.Count, x.Unit),
                FormatStat(x.Stats.Min, x.Stats.Count, x.Unit),
                FormatStat(x.Stats.Max, x.Stats.Count, x.Unit),
                x.Stats.Count.ToString("N0", CultureInfo.InvariantCulture)
            }).ToList();
        }

        private static List<List<string>> BuildQualityRows(UnifiedMapReport report)
        {
            return new List<List<string>>
            {
                QualityRow("RSRP", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrp))), "dBm", report.Rows.Count(x => x.Rsrp.HasValue && x.Rsrp.Value < -105)),
                QualityRow("RSRQ", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrq))), "dB", report.Rows.Count(x => x.Rsrq.HasValue && x.Rsrq.Value < -14)),
                QualityRow("SINR", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Sinr))), "dB", report.Rows.Count(x => x.Sinr.HasValue && x.Sinr.Value < 5)),
                QualityRow("MOS", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Mos))), "", report.Rows.Count(x => x.Mos.HasValue && x.Mos.Value < 3))
            };
        }

        private static List<string> QualityRow(string metric, MetricSummary stats, string unit, int poorCount)
        {
            return new List<string> { metric, FormatStat(stats.Average, stats.Count, unit), poorCount.ToString("N0", CultureInfo.InvariantCulture) };
        }

        private static List<List<string>> BuildSpeedRows(UnifiedMapReport report)
        {
            return new List<List<string>>
            {
                new() { "DL Throughput", FormatStat(MetricStats(report.Rows.Select(x => ParseNumber(x.DlTpt))).Average, MetricStats(report.Rows.Select(x => ParseNumber(x.DlTpt))).Count, "Mbps"), report.Rows.Count(x => ParseNumber(x.DlTpt) is < 10).ToString("N0", CultureInfo.InvariantCulture) },
                new() { "UL Throughput", FormatStat(MetricStats(report.Rows.Select(x => ParseNumber(x.UlTpt))).Average, MetricStats(report.Rows.Select(x => ParseNumber(x.UlTpt))).Count, "Mbps"), report.Rows.Count(x => ParseNumber(x.UlTpt) is < 5).ToString("N0", CultureInfo.InvariantCulture) }
            };
        }

        private static List<List<string>> BuildRangeRows(IEnumerable<double?> values, IReadOnlyList<double> cutoffs)
        {
            var list = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (list.Count == 0) return new List<List<string>> { new() { "No data", "0" } };

            return new List<List<string>>
            {
                new() { $"< {cutoffs[0]:0.##}", list.Count(x => x < cutoffs[0]).ToString("N0", CultureInfo.InvariantCulture) },
                new() { $"{cutoffs[0]:0.##} - {cutoffs[1]:0.##}", list.Count(x => x >= cutoffs[0] && x < cutoffs[1]).ToString("N0", CultureInfo.InvariantCulture) },
                new() { $"{cutoffs[1]:0.##} - {cutoffs[2]:0.##}", list.Count(x => x >= cutoffs[1] && x < cutoffs[2]).ToString("N0", CultureInfo.InvariantCulture) },
                new() { $">= {cutoffs[2]:0.##}", list.Count(x => x >= cutoffs[2]).ToString("N0", CultureInfo.InvariantCulture) }
            };
        }

        private static IEnumerable<(string Pci, int Count)> PciGroups(UnifiedMapReport report)
        {
            return report.Rows
                .Select(x => string.IsNullOrWhiteSpace(x.Pci) ? "Unknown" : x.Pci.Trim())
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => (Pci: x.Key, Count: x.Count()))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Pci);
        }

        private static IEnumerable<(string Pci, int Count)> PoorPciGroups(UnifiedMapReport report, Func<UnifiedMapReportRow, bool> predicate)
        {
            return report.Rows
                .Where(predicate)
                .Select(x => string.IsNullOrWhiteSpace(x.Pci) ? "Unknown" : x.Pci.Trim())
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => (Pci: x.Key, Count: x.Count()))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Pci);
        }

        private static MetricSummary MetricStats(IEnumerable<double?> source)
        {
            var values = source.Where(x => x.HasValue && !double.IsNaN(x.Value) && !double.IsInfinity(x.Value)).Select(x => x!.Value).ToList();
            return values.Count == 0
                ? new MetricSummary(0, 0, 0, 0)
                : new MetricSummary(values.Count, values.Average(), values.Min(), values.Max());
        }

        private static double? ToNullableDouble(float? value) => value.HasValue ? value.Value : null;

        private static double? ParseNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"-?\d+(\.\d+)?");
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }

        private static string FormatStat(double value, int count, string unit)
        {
            return count == 0 ? "N/A" : $"{value:0.##} {unit}".Trim();
        }

        private sealed record MetricSummary(int Count, double Average, double Min, double Max);

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
