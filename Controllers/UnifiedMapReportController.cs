using System.Collections.Concurrent;
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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using NetTopologySuite.GeometriesGraph;

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
        
        private static readonly HttpClient _httpClient = new HttpClient();

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
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Report")]
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

            var thresholds = await GetSessionNotesThresholdConfigAsync(sessionIdInts)
                ?? ReportThresholdConfig.Hardcoded();
            var companyLogo = LoadCompanyLogo();
            var productLogo = LoadProductLogo();
            var primarySessionId = sessionIds.FirstOrDefault();

            var rowsTask = QueryReportRowsAsync(request, sessionIdInts);
            var mapImagesTask = FetchMapImagesAsync(primarySessionId);

            await Task.WhenAll(rowsTask, mapImagesTask);

            var rows = rowsTask.Result;
            var mapImages = mapImagesTask.Result;

            if (rows.Count == 0)
                return NotFound(new
                {
                    Message = $"For this project we don't have data.",
                    ProjectId = request.ProjectId
                });

            var report = UnifiedMapReportFactory.Create(
                request,
                project?.project_name,
                sessionIds,
                rows,
                thresholds,
                companyLogo,
                productLogo);
                
            report.MapImages = mapImages;

            var pdf = UnifiedMapRawPdfBuilder.Build(report);
            var filename = $"UnifiedMap_Report_{request.ProjectId}_{DateTime.Now:yyyy-MM-dd}.pdf";
            return File(pdf, "application/pdf", filename);
        }

        private async Task<Dictionary<string, ReportLogo>> FetchMapImagesAsync(long sessionId)
        {
            var headers = new[] { "BAND", "RSRP", "RSRQ", "SINR", "DL_THPT", "UL_THPT", "EARFCN", "LTE_BLER","PCI", "NODEB_ID", "VOLTE_CALL","CI", "PUSCH_TX" };

            var downloadTasks = headers.Select(async header =>
            {
                var url = $"https://apistracer.vinfocom.co.in/uploaded_images/{sessionId}_{header}.png";
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var originalBytes = await response.Content.ReadAsByteArrayAsync();
                        byte[] jpegBytes;

                        try
                        {
                            using var image = Image.Load(originalBytes);
                            using var ms = new MemoryStream();
                            image.Save(ms, new JpegEncoder { Quality = 90 });
                            jpegBytes = ms.ToArray();
                        }
                        catch
                        {
                            jpegBytes = originalBytes;
                        }

                        var (width, height) = UnifiedMapRawPdfBuilder.GetJpegDimensions(jpegBytes);
                        if (width > 0 && height > 0)
                        {
                            return new KeyValuePair<string, ReportLogo>(header, new ReportLogo(jpegBytes, width, height));
                        }
                    }
                }
                catch { }
                
                return new KeyValuePair<string, ReportLogo>(header, null!); 
            });

            var results = await Task.WhenAll(downloadTasks);

            var images = new Dictionary<string, ReportLogo>();
            foreach (var res in results)
            {
                if (res.Value != null)
                {
                    images[res.Key] = res.Value;
                }
            }

            return images;
        }

        private async Task<ReportThresholdConfig?> GetSessionNotesThresholdConfigAsync(List<int> sessionIds)
        {
            if (sessionIds.Count == 0)
                return null;

            var sessions = await _db.tbl_session
                .AsNoTracking()
                .Where(s => s.id.HasValue && sessionIds.Contains(s.id.Value))
                .Select(s => new
                {
                    Id = s.id ?? 0,
                    s.notes
                })
                .ToListAsync(HttpContext.RequestAborted);

            var sessionsById = sessions
                .Where(s => s.Id > 0)
                .ToDictionary(s => s.Id, s => s.notes);

            foreach (var sessionId in sessionIds)
            {
                if (!sessionsById.TryGetValue(sessionId, out var notes))
                    continue;

                var thresholds = ReportThresholdConfig.FromSessionNotes(
                    notes,
                    $"tbl_session.notes session {sessionId}");

                if (thresholds != null)
                    return thresholds;
            }

            return null;
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
            catch { }

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
            return ReportImageHelper.LoadCompanyLogo(_env);
        }

        private ReportLogo? LoadProductLogo()
        {
            return ReportImageHelper.LoadProductLogo(_env);
        }

        private async Task<List<UnifiedMapReportRow>> QueryReportRowsAsync(
            UnifiedMapPdfRequest request,
            List<int> sessionIds,
            bool applyProjectPolygon = true)
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
            var (whereClause, parameters) = await BuildReportSqlWhereAsync(request, sessionIds, command, applyProjectPolygon);
            command.CommandText = $@"
                SELECT
                    id, session_id, timestamp, lat, lon, network,
                    COALESCE(
                        NULLIF(TRIM(BOTH CHAR(39) FROM TRIM(BOTH '""' FROM TRIM(m_alpha_short))), ''),
                        TRIM(BOTH CHAR(39) FROM TRIM(BOTH '""' FROM TRIM(m_alpha_long)))
                    ) AS provider_name,
                    band, pci, rssi, rsrp, rsrq, sinr, mos, jitter, latency,
                    packet_loss, dl_tpt, ul_tpt, apps, app_name, indoor_outdoor, nodeb_id, cell_id, earfcn, bler, volte_call,
                    primary_cell_info_1, image_path
                FROM tbl_network_log
                WHERE {whereClause}
                ORDER BY timestamp, id
                LIMIT @limit OFFSET @offset;";

            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);

            AddParam(command, "@limit", limit);
            AddParam(command, "@offset", offset);

            var rows = new List<UnifiedMapReportRow>();
            await using (var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted))
            {
                while (await reader.ReadAsync(HttpContext.RequestAborted))
                {
                    var primaryCellInfo = ReadString(reader, "primary_cell_info_1");
                    var earfcnValues = ExtractEarfcnValues(ReadString(reader, "earfcn"), primaryCellInfo);

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
                        Earfcn = earfcnValues.Count > 0 ? earfcnValues[0] : null,
                        EarfcnValues = earfcnValues,
                        Bler = ReadString(reader, "bler"), // Kept as string 
                        VolteCall = ReadInt(reader,"volte_call"), // Reverted to int?
                        DlTpt = ReadString(reader, "dl_tpt"),
                        NodebId = ReadString(reader, "nodeb_id"),
                        UlTpt = ReadString(reader, "ul_tpt"),
                        Apps = ReadString(reader, "apps"),
                        IndoorOutdoor = ReadString(reader, "indoor_outdoor"),
                        CellId = ReadString(reader, "cell_id"),
                        PuschTx = ExtractPuschTx(primaryCellInfo),
                        RawImageName = ReadString(reader, "image_path")
                    });
                }
            }

            if (rows.Count == 0 && applyProjectPolygon && request.ProjectId > 0)
                return await QueryReportRowsAsync(request, sessionIds, applyProjectPolygon: false);

            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.NodebId) ||
                    r.NodebId.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    r.NodebId.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(r.CellId))
                    {
                        var digits = Regex.Match(r.CellId, @"\d+").Value;
                        if (long.TryParse(digits, out var cidVal) && cidVal > 256)
                        {
                            r.NodebId = (cidVal >> 8).ToString();
                        }
                    }
                }
            }

            return rows;
        }

        private async Task<(string Clause, List<DbParameter> Parameters)> BuildReportSqlWhereAsync(
            UnifiedMapPdfRequest request,
            List<int> sessionIds,
            DbCommand command,
            bool applyProjectPolygon)
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
            clauses.Add("primary_cell_info_1 IS NOT NULL AND TRIM(primary_cell_info_1) <> ''");
            clauses.Add(@"(
                NULLIF(TRIM(band), '') IS NOT NULL
                OR UPPER(TRIM(COALESCE(network, ''))) LIKE '%5G%'
            )");

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

            if (applyProjectPolygon)
            {
                var projectPolygonWkt = await ResolveProjectFilterWktAsync(request.ProjectId);
                if (!string.IsNullOrWhiteSpace(projectPolygonWkt))
                {
                    clauses.Add("lat IS NOT NULL AND lon IS NOT NULL");
                    clauses.Add("ST_Contains(ST_GeomFromText(@projectPolygonWkt, 4326), ST_SRID(POINT(lon, lat), 4326))");
                    parameters.Add(CreateParam(command, "@projectPolygonWkt", projectPolygonWkt));
                }
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
            if (reader.IsDBNull(ordinal)) return null;

            var value = reader.GetValue(ordinal);
            if (value is int i) return i;
            if (value is long l) return l > int.MaxValue || l < int.MinValue ? null : (int)l;
            if (value is decimal dec) return dec > int.MaxValue || dec < int.MinValue ? null : (int)dec;
            if (value is double dbl) return dbl > int.MaxValue || dbl < int.MinValue ? null : (int)dbl;
            if (value is float f) return f > int.MaxValue || f < int.MinValue ? null : (int)f;

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) &&
                   parsedDouble <= int.MaxValue &&
                   parsedDouble >= int.MinValue
                ? (int)parsedDouble
                : null;
        }

        private static float? ReadFloat(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal)) return null;

            var value = reader.GetValue(ordinal);
            if (value is float f) return f;
            if (value is double d) return (float)d;
            if (value is decimal dec) return (float)dec;

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
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

        private static string? ExtractPuschTx(string? primaryCellInfo)
        {
            if (string.IsNullOrWhiteSpace(primaryCellInfo)) return null;

            var match = Regex.Match(
                primaryCellInfo,
                @"\bPUSCH[_\s-]*TX\s*=\s*(?<value>-?\d+(?:\.\d+)?)\s*(?<unit>dBm)?",
                RegexOptions.IgnoreCase);

            if (!match.Success) return null;

            var value = match.Groups["value"].Value;
            return match.Groups["unit"].Success ? $"{value} dBm" : value;
        }

        private static List<int> ExtractEarfcnValues(string? earfcnText, string? primaryCellInfo)
        {
            var values = new List<int>();
            values.AddRange(ParseEarfcnTextValues(earfcnText));
            values.AddRange(ParseNamedEarfcnValues(primaryCellInfo));
            return values.Distinct().ToList();
        }

        private static IEnumerable<int> ParseEarfcnTextValues(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;

            foreach (Match match in Regex.Matches(value, @"\d+"))
            {
                if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    yield return number;
            }
        }

        private static IEnumerable<int> ParseNamedEarfcnValues(string? primaryCellInfo)
        {
            if (string.IsNullOrWhiteSpace(primaryCellInfo)) yield break;

            foreach (Match match in Regex.Matches(primaryCellInfo, @"\b(?:mEarfcn|earfcn)\s*=\s*(?<value>\d+)", RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    yield return number;
            }
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
        public int? Earfcn { get; set; }
        public List<int> EarfcnValues { get; set; } = new();
        public string? Bler { get; set; }
        public int? VolteCall { get; set; } // Kept as int?
        public string? PuschTx { get; set; } 
        public string? RawImageName { get; set; }
    }

    internal sealed class UnifiedMapReport
    {
        public string Title { get; set; } = "Unified Map Detail Report";
        public ReportLogo? Logo { get; set; }
        public ReportLogo? CompanyLogo { get; set; }
        public ReportLogo? ProductLogo { get; set; }
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
        public Dictionary<string, ReportLogo> MapImages { get; set; } = new();
        public ReportThresholdConfig? Thresholds { get; set; }
    }

    internal sealed record ReportLogo(byte[] Bytes, int Width, int Height);

    internal static class ReportImageHelper
    {
        private static readonly Rgba32 PdfBackground = new(255, 255, 255);

        public static ReportLogo? LoadCompanyLogo(IWebHostEnvironment env)
        {
            var candidates = new[]
            {
                Path.Combine(env.ContentRootPath, "wwwroot", "comp.jpeg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "comp.jpg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "vinfocom-logo.jpeg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "vinfocom-logo.jpg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "vinfocom-logo.png"),
                Path.Combine(env.ContentRootPath, "..", "StraceExeFron", "public", "comp.jpeg"),
                Path.Combine(env.ContentRootPath, "..", "StraceExeFron", "src", "assets", "vinfocom.png")
            };

            return LoadLogoFromCandidates(candidates);
        }

        public static ReportLogo? LoadProductLogo(IWebHostEnvironment env)
        {
            var candidates = new[]
            {
                Path.Combine(env.ContentRootPath, "wwwroot", "stracer-logo.jpeg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "stracer-logo.jpg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "stracer-logo.png"),
                Path.Combine(env.ContentRootPath, "wwwroot", "favicon.svg"),
                Path.Combine(env.ContentRootPath, "wwwroot", "favicon.png"),
                Path.Combine(env.ContentRootPath, "..", "StraceExeFron", "public", "favicon.svg"),
                Path.Combine(env.ContentRootPath, "..", "StraceExeFron", "public", "favicon.png"),
                Path.Combine(env.ContentRootPath, "..", "StraceExeFron", "src", "assets", "stracer-logo.png")
            };

            return LoadLogoFromCandidates(candidates) ?? CreateDefaultStracerLogo();
        }

        private static ReportLogo? LoadLogoFromCandidates(IEnumerable<string> candidates)
        {
            foreach (var path in candidates)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!System.IO.File.Exists(fullPath)) continue;

                    var logo = PrepareLogo(fullPath);
                    if (logo != null) return logo;
                }
                catch
                {
                    // Try the next candidate.
                }
            }

            return null;
        }

        private static ReportLogo? PrepareLogo(string path)
        {
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = System.IO.File.ReadAllText(path);
                return PrepareSimpleSvgLogo(svg);
            }

            return PrepareLogo(System.IO.File.ReadAllBytes(path));
        }

        private static ReportLogo? PrepareLogo(byte[] bytes)
        {
            try
            {
                using var image = Image.Load<Rgba32>(bytes);
                var detectedBackground = EstimateLightBackground(image);

                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++)
                        {
                            var pixel = BlendWithPdfBackground(row[x]);
                            row[x] = IsLightBackgroundPixel(pixel, detectedBackground)
                                ? PdfBackground
                                : pixel;
                        }
                    }
                });

                using var ms = new MemoryStream();
                image.Save(ms, new JpegEncoder { Quality = 95 });
                return new ReportLogo(ms.ToArray(), image.Width, image.Height);
            }
            catch
            {
                var (width, height) = UnifiedMapRawPdfBuilder.GetJpegDimensions(bytes);
                return width > 0 && height > 0 ? new ReportLogo(bytes, width, height) : null;
            }
        }

        private static ReportLogo? CreateDefaultStracerLogo()
        {
            const string svg = """
                <svg viewBox="0 0 512 512" xmlns="http://www.w3.org/2000/svg" fill="none">
                  <circle cx="256" cy="220" r="165" fill="#1E4E8C"/>
                  <circle cx="256" cy="220" r="140" fill="#1F6FAE"/>
                  <circle cx="256" cy="220" r="115" fill="#2798C4"/>
                  <circle cx="256" cy="220" r="90" fill="#34B1CE"/>
                  <circle cx="256" cy="220" r="65" fill="#57C6D9"/>
                  <circle cx="256" cy="220" r="14" fill="#1E4E8C"/>
                  <path d="M256 280 L115 592" stroke="#57C6D9" stroke-width="28" stroke-linecap="round"/>
                  <path d="M256 280 L407 592" stroke="#57C6D9" stroke-width="28" stroke-linecap="round"/>
                  <path d="M175 468 L304 380" stroke="#57C6D9" stroke-width="26" stroke-linecap="round"/>
                  <path d="M304 380 L230 350" stroke="#57C6D9" stroke-width="26" stroke-linecap="round"/>
                </svg>
                """;

            return PrepareSimpleSvgLogo(svg);
        }

        private static ReportLogo? PrepareSimpleSvgLogo(string svg)
        {
            try
            {
                var viewBox = ParseViewBox(svg);
                const int size = 320;
                using var image = new Image<Rgba32>(size, size);
                FillImage(image, PdfBackground);

                foreach (Match match in Regex.Matches(svg, @"<circle\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var tag = match.Value;
                    var cx = ReadSvgDouble(tag, "cx");
                    var cy = ReadSvgDouble(tag, "cy");
                    var r = ReadSvgDouble(tag, "r");
                    var fill = ReadSvgColor(tag, "fill");
                    if (!cx.HasValue || !cy.HasValue || !r.HasValue || fill == null) continue;

                    var (x, y) = MapSvgPoint(cx.Value, cy.Value, viewBox, size);
                    DrawFilledCircle(image, x, y, r.Value * viewBox.Scale, fill.Value);
                }

                foreach (Match match in Regex.Matches(svg, @"<path\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var tag = match.Value;
                    var stroke = ReadSvgColor(tag, "stroke");
                    var strokeWidth = ReadSvgDouble(tag, "stroke-width") ?? 1;
                    var d = ReadSvgString(tag, "d");
                    if (stroke == null || string.IsNullOrWhiteSpace(d)) continue;

                    var values = Regex.Matches(d, @"-?\d+(?:\.\d+)?")
                        .Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture))
                        .ToList();

                    for (var i = 0; i + 3 < values.Count; i += 2)
                    {
                        var (x1, y1) = MapSvgPoint(values[i], values[i + 1], viewBox, size);
                        var (x2, y2) = MapSvgPoint(values[i + 2], values[i + 3], viewBox, size);
                        DrawStrokedLine(image, x1, y1, x2, y2, strokeWidth * viewBox.Scale, stroke.Value);
                    }
                }

                using var ms = new MemoryStream();
                image.Save(ms, new JpegEncoder { Quality = 95 });
                return new ReportLogo(ms.ToArray(), image.Width, image.Height);
            }
            catch
            {
                return null;
            }
        }

        private static (double MinX, double MinY, double Width, double Height, double Scale, double OffsetX, double OffsetY) ParseViewBox(string svg)
        {
            const int targetSize = 320;
            var match = Regex.Match(svg, @"viewBox\s*=\s*[""']\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            var minX = match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var minY = match.Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var width = match.Success ? double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 512;
            var height = match.Success ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 512;
            var scale = Math.Min(targetSize / width, targetSize / height) * 0.88;
            var offsetX = (targetSize - (width * scale)) / 2;
            var offsetY = (targetSize - (height * scale)) / 2;
            return (minX, minY, width, height, scale, offsetX, offsetY);
        }

        private static (double X, double Y) MapSvgPoint(
            double x,
            double y,
            (double MinX, double MinY, double Width, double Height, double Scale, double OffsetX, double OffsetY) viewBox,
            int targetSize)
        {
            return (
                ((x - viewBox.MinX) * viewBox.Scale) + viewBox.OffsetX,
                ((y - viewBox.MinY) * viewBox.Scale) + viewBox.OffsetY);
        }

        private static double? ReadSvgDouble(string tag, string name)
        {
            var value = ReadSvgString(tag, name);
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static string? ReadSvgString(string tag, string name)
        {
            var match = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static Rgba32? ReadSvgColor(string tag, string name)
        {
            var value = ReadSvgString(tag, name);
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;

            var hex = value.Trim().TrimStart('#');
            if (hex.Length == 3)
                hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });

            return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color)
                ? new Rgba32((byte)((color >> 16) & 0xFF), (byte)((color >> 8) & 0xFF), (byte)(color & 0xFF))
                : null;
        }

        private static void FillImage(Image<Rgba32> image, Rgba32 color)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                        row[x] = color;
                }
            });
        }

        private static void DrawFilledCircle(Image<Rgba32> image, double cx, double cy, double radius, Rgba32 color)
        {
            var minX = Math.Max(0, (int)Math.Floor(cx - radius));
            var maxX = Math.Min(image.Width - 1, (int)Math.Ceiling(cx + radius));
            var minY = Math.Max(0, (int)Math.Floor(cy - radius));
            var maxY = Math.Min(image.Height - 1, (int)Math.Ceiling(cy + radius));
            var radiusSquared = radius * radius;

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    if ((dx * dx) + (dy * dy) <= radiusSquared)
                        image[x, y] = color;
                }
            }
        }

        private static void DrawStrokedLine(Image<Rgba32> image, double x1, double y1, double x2, double y2, double strokeWidth, Rgba32 color)
        {
            var radius = Math.Max(1, strokeWidth / 2);
            var minX = Math.Max(0, (int)Math.Floor(Math.Min(x1, x2) - radius));
            var maxX = Math.Min(image.Width - 1, (int)Math.Ceiling(Math.Max(x1, x2) + radius));
            var minY = Math.Max(0, (int)Math.Floor(Math.Min(y1, y2) - radius));
            var maxY = Math.Min(image.Height - 1, (int)Math.Ceiling(Math.Max(y1, y2) + radius));
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lengthSquared = (dx * dx) + (dy * dy);
            var radiusSquared = radius * radius;

            if (lengthSquared <= 0)
            {
                DrawFilledCircle(image, x1, y1, radius, color);
                return;
            }

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5;
                    var py = y + 0.5;
                    var t = (((px - x1) * dx) + ((py - y1) * dy)) / lengthSquared;
                    t = Math.Clamp(t, 0, 1);
                    var closestX = x1 + (t * dx);
                    var closestY = y1 + (t * dy);
                    var distanceX = px - closestX;
                    var distanceY = py - closestY;
                    if ((distanceX * distanceX) + (distanceY * distanceY) <= radiusSquared)
                        image[x, y] = color;
                }
            }
        }

        private static Rgba32 EstimateLightBackground(Image<Rgba32> image)
        {
            var samples = new List<Rgba32>();
            AddSample(0, 0);
            AddSample(image.Width - 1, 0);
            AddSample(0, image.Height - 1);
            AddSample(image.Width - 1, image.Height - 1);
            AddSample(image.Width / 2, 0);
            AddSample(image.Width / 2, image.Height - 1);
            AddSample(0, image.Height / 2);
            AddSample(image.Width - 1, image.Height / 2);

            var lightSamples = samples
                .Select(BlendWithPdfBackground)
                .Where(p => p.R >= 200 && p.G >= 200 && p.B >= 200)
                .ToList();

            if (lightSamples.Count == 0) return PdfBackground;

            return new Rgba32(
                (byte)Math.Round(lightSamples.Average(p => p.R)),
                (byte)Math.Round(lightSamples.Average(p => p.G)),
                (byte)Math.Round(lightSamples.Average(p => p.B)));

            void AddSample(int x, int y)
            {
                if (image.Width <= 0 || image.Height <= 0) return;
                samples.Add(image[Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1)]);
            }
        }

        private static Rgba32 BlendWithPdfBackground(Rgba32 pixel)
        {
            if (pixel.A == 255) return new Rgba32(pixel.R, pixel.G, pixel.B);

            var alpha = pixel.A / 255.0;
            return new Rgba32(
                (byte)Math.Round((pixel.R * alpha) + (PdfBackground.R * (1 - alpha))),
                (byte)Math.Round((pixel.G * alpha) + (PdfBackground.G * (1 - alpha))),
                (byte)Math.Round((pixel.B * alpha) + (PdfBackground.B * (1 - alpha))));
        }

        private static bool IsLightBackgroundPixel(Rgba32 pixel, Rgba32 background)
        {
            if (pixel.R < 205 || pixel.G < 205 || pixel.B < 205) return false;

            return Math.Abs(pixel.R - background.R) <= 30 &&
                   Math.Abs(pixel.G - background.G) <= 30 &&
                   Math.Abs(pixel.B - background.B) <= 30;
        }
    }

    internal sealed class ReportThresholdConfig
    {
        public string Source { get; set; } = "Hardcoded fallback";
        public double CoverageHoleLimit { get; set; } = -110;
        public List<ThresholdRange> Rsrp { get; set; } = new();
        public List<ThresholdRange> Rsrq { get; set; } = new();
        public List<ThresholdRange> Sinr { get; set; } = new();
        public List<ThresholdRange> Mos { get; set; } = new();
        public List<ThresholdRange> DlTpt { get; set; } = new();
        public List<ThresholdRange> UlTpt { get; set; } = new();
        public List<ThresholdRange> Earfcn { get; set; } = new();
        public List<ThresholdRange> Bler { get; set; } = new();
        public List<ThresholdRange> VolteCall { get; set; } = new();
        public List<ThresholdRange> PuschTx { get; set; } = new();

        public static ReportThresholdConfig? FromSessionNotes(string? notes, string source)
        {
            var json = ExtractColorSettingsJson(notes);
            return FromColorSettingsJson(json, source);
        }

        public static ReportThresholdConfig? FromColorSettingsJson(string? json, string source)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                var fallback = Hardcoded();
                var config = new ReportThresholdConfig
                {
                    Source = source,
                    CoverageHoleLimit = fallback.CoverageHoleLimit,
                    Rsrp = fallback.Rsrp,
                    Rsrq = fallback.Rsrq,
                    Sinr = fallback.Sinr,
                    Mos = fallback.Mos,
                    DlTpt = fallback.DlTpt,
                    UlTpt = fallback.UlTpt,
                    Earfcn = fallback.Earfcn,
                    Bler = fallback.Bler,
                    VolteCall = fallback.VolteCall,
                    PuschTx = fallback.PuschTx
                };

                var applied = false;

                if (TryGetPropertyIgnoreCase(doc.RootElement, "range", out var rangeRoot) &&
                    rangeRoot.ValueKind == JsonValueKind.Object)
                {
                    applied |= ApplyRangeMetric(rangeRoot, "RSRP", ranges => config.Rsrp = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "RSRQ", ranges => config.Rsrq = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "SINR", ranges => config.Sinr = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "MOS", ranges => config.Mos = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "DL_THPT", ranges => config.DlTpt = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "UL_THPT", ranges => config.UlTpt = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "PUSCH_TX", ranges => config.PuschTx = ranges);
                    applied |= ApplyRangeMetric(rangeRoot, "EARFCN", ranges => config.Earfcn = ranges);
                }

                if (TryGetPropertyIgnoreCase(doc.RootElement, "value", out var valueRoot) &&
                    valueRoot.ValueKind == JsonValueKind.Object)
                {
                    applied |= ApplyValueMetric(valueRoot, "BLER", ranges => config.Bler = ranges);
                    applied |= ApplyValueMetric(valueRoot, "LTE_BLER", ranges => config.Bler = ranges);
                    applied |= ApplyValueMetric(valueRoot, "VOLTE", ranges => config.VolteCall = ranges);
                    applied |= ApplyValueMetric(valueRoot, "VOLTE_CALL", ranges => config.VolteCall = ranges);
                }

                return applied ? config : null;
            }
            catch
            {
                return null;
            }
        }

        public static ReportThresholdConfig FromDb(thresholds setting, string source)
        {
            var fallback = Hardcoded();
            return new ReportThresholdConfig
            {
                Source = source,
                CoverageHoleLimit =
                    setting.coveragehole_value ??
                    ParseCoverageHoleLimit(setting.coveragehole_json) ??
                    fallback.CoverageHoleLimit,
                Rsrp = ParseRanges(setting.rsrp_json, fallback.Rsrp),
                Rsrq = ParseRanges(setting.rsrq_json, fallback.Rsrq),
                Sinr = ParseRanges(setting.sinr_json, fallback.Sinr),
                Mos = ParseRanges(setting.mos_json, fallback.Mos),
                DlTpt = ParseRanges(setting.dl_thpt_json, fallback.DlTpt),
                UlTpt = ParseRanges(setting.ul_thpt_json, fallback.UlTpt),
                Bler = ParseRanges(setting.lte_bler_json, fallback.Bler),
                VolteCall = ParseRanges(setting.volte_call, fallback.VolteCall)
            };
        }

        public static ReportThresholdConfig Hardcoded()
        {
            return new ReportThresholdConfig
            {
                Source = "Hardcoded",
                CoverageHoleLimit = -110,

                Rsrp = new List<ThresholdRange>
                {
                    new("",-75,0,"#008000"),
                    new("",-85,-75,"#66CC66"),
                    new("",-95,-85,"#ADD8E6"),
                    new("",-105,-95,"#0000FF"),
                    new("",-115,-105,"#FFFF00"),
                    new("",-140,-115,"#FF0000")
                },
                Rsrq = new List<ThresholdRange>
                {
                    new("",-14,0,"#008000"),
                    new("",-16,-14,"#66CC66"),
                    new("",-18,-16,"#FFFF00"),
                    new("",-30,-18,"#FF0000")
                },
                Sinr = new List<ThresholdRange>
                {
                    new("",15,40,"#008000"),
                    new("",10,15,"#66CC66"),
                    new("",5,10,"#0000FF"),
                    new("",0,5,"#FFFF00"),
                    new("",-20,0,"#FF0000")
                },
                DlTpt = new List<ThresholdRange>
                {
                    new("",4,1000,"#008000"),
                    new("",3,4,"#66CC66"),
                    new("",2,3,"#FFFF00"),
                    new("",1,2,"#0000FF"),
                    new("",0,1,"#FF0000")
                },
                UlTpt = new List<ThresholdRange>
                {
                    new("",4,1000,"#008000"),
                    new("",3,4,"#66CC66"),
                    new("",2,3,"#FFFF00"),
                    new("",1,2,"#0000FF"),
                    new("",0,1,"#FF0000")
                },
                Earfcn = new List<ThresholdRange>
                {
                    new("B3 1800MHz",0,0,"#4AA3FF") { ValueMatch="B3" },
                    new("B5 850MHz",0,0,"#00AA00") { ValueMatch="B5" },
                    new("B40 2300MHz",0,0,"#FFA500") { ValueMatch="B40" },
                    new("B41 2500MHz",0,0,"#FF1493") { ValueMatch="B41" },
                    new("B8 900MHz",0,0,"#4A148C") { ValueMatch="B8" },
                    // new("n28 7000MHz",0,0, "#FFFF00") { ValueMatch="n28" },
                },
                Bler = new List<ThresholdRange>
                {
                    new("",0,0,"#008000") { ValueMatch="No Errors" },
                    new("",0,0,"#FFFF00") { ValueMatch="Low" },
                    new("",0,0,"#FFA500") { ValueMatch="Medium" },
                    new("",0,0,"#FF0000") { ValueMatch="High" }
                },
                VolteCall = new List<ThresholdRange>
                {
                    new("VoLTE Active",1,1,"#008000") { ValueMatch="1" },
                    new("No VoLTE",0,0,"#FF0000") { ValueMatch="0" }
                },
                PuschTx = new List<ThresholdRange>
                {
                    new("",21,31,"#FF0000"),
                    new("",16,21,"#FFFF00"),
                    new("",9,16,"#0000FF"),
                    new("",1,9,"#90EE90"),
                    new("",-50,1,"#006400")
                }
            };
        }

        private static List<ThresholdRange> ParseRanges(string? json, List<ThresholdRange> fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var thresholdElement = doc.RootElement;
                if (thresholdElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "default", "Default", "5g", "5G", "4g", "4G", "3g", "3G", "2g", "2G" })
                    {
                        if (thresholdElement.TryGetProperty(key, out var techElement) &&
                            techElement.ValueKind == JsonValueKind.Array)
                        {
                            thresholdElement = techElement;
                            break;
                        }
                    }
                }

                if (thresholdElement.ValueKind != JsonValueKind.Array) return fallback;

                var ranges = ParseThresholdArray(thresholdElement, valueMode: false);
                return ranges.Count > 0 ? ranges : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool ApplyRangeMetric(JsonElement root, string metric, Action<List<ThresholdRange>> apply)
        {
            if (!TryGetPropertyIgnoreCase(root, metric, out var element) || element.ValueKind != JsonValueKind.Array)
                return false;

            var ranges = ParseThresholdArray(element, valueMode: false);
            if (ranges.Count == 0)
                return false;

            apply(ranges);
            return true;
        }

        private static bool ApplyValueMetric(JsonElement root, string metric, Action<List<ThresholdRange>> apply)
        {
            if (!TryGetPropertyIgnoreCase(root, metric, out var element) || element.ValueKind != JsonValueKind.Array)
                return false;

            var ranges = ParseThresholdArray(element, valueMode: true);
            if (ranges.Count == 0)
                return false;

            apply(ranges);
            return true;
        }

        private static List<ThresholdRange> ParseThresholdArray(JsonElement element, bool valueMode)
        {
            var ranges = new List<ThresholdRange>();
            if (element.ValueKind != JsonValueKind.Array)
                return ranges;

            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var min = GetDouble(item, "min");
                var max = GetDouble(item, "max");
                var val = GetStringOrNumberFallback(item, "value");

                if (valueMode)
                {
                    if (string.IsNullOrWhiteSpace(val))
                        continue;

                    ranges.Add(new ThresholdRange(
                        GetStringOrNumberFallback(item, "label", "range", "name") ?? val,
                        0,
                        0,
                        GetColor(item))
                    {
                        ValueMatch = val
                    });
                    continue;
                }

                if (!min.HasValue && !max.HasValue && string.IsNullOrWhiteSpace(val))
                    continue;

                ranges.Add(new ThresholdRange(
                    GetStringOrNumberFallback(item, "label", "range", "name") ?? "",
                    min ?? 0,
                    max ?? 0,
                    GetColor(item))
                {
                    ValueMatch = val
                });
            }

            return ranges;
        }

        private static string? ExtractColorSettingsJson(string? notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return null;

            var markerIndex = notes.IndexOf("udid@@", StringComparison.OrdinalIgnoreCase);
            var markerLength = "udid@@".Length;
            if (markerIndex < 0)
            {
                markerIndex = notes.IndexOf("cs@@", StringComparison.OrdinalIgnoreCase);
                markerLength = "cs@@".Length;
            }

            var searchStart = markerIndex >= 0 ? markerIndex + markerLength : 0;
            var braceStart = notes.IndexOf('{', searchStart);

            if (braceStart < 0)
                return null;

            return ExtractBalancedJsonObject(notes, braceStart);
        }

        private static string? ExtractBalancedJsonObject(string text, int startIndex)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = startIndex; i < text.Length; i++)
            {
                var ch = text[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                    continue;
                }

                if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text[startIndex..(i + 1)];
                }
            }

            return null;
        }

        private static string? GetStringFallback(JsonElement item, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyIgnoreCase(item, name, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
            return null;
        }

        private static string? GetStringOrNumberFallback(JsonElement item, params string[] names)
        {
            foreach (var name in names)
            {
                if (!TryGetPropertyIgnoreCase(item, name, out var prop))
                    continue;

                if (prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }

                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetRawText();
            }

            return null;
        }

        private static double? GetDouble(JsonElement item, string name)
        {
            if (!TryGetPropertyIgnoreCase(item, name, out var prop)) return null;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var number)) return number;
            if (prop.ValueKind == JsonValueKind.String) return ParseDouble(prop.GetString());
            return null;
        }

        private static string GetColor(JsonElement item)
        {
            foreach (var name in new[] { "color", "hex", "colorCode" })
            {
                if (!TryGetPropertyIgnoreCase(item, name, out var prop))
                    continue;

                var color = NormalizeColor(prop);
                if (!string.IsNullOrWhiteSpace(color))
                    return color;
            }

            return "#808080";
        }

        private static string? NormalizeColor(JsonElement prop)
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var numeric))
                return NumericColorToHex(numeric);

            if (prop.ValueKind != JsonValueKind.String)
                return null;

            var raw = prop.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intColor))
                return NumericColorToHex(intColor);

            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw[2..];

            raw = raw.TrimStart('#');
            if (raw.Length == 8)
                raw = raw[2..];

            return raw.Length == 6 && int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
                ? $"#{raw.ToUpperInvariant()}"
                : null;
        }

        private static string NumericColorToHex(long value)
        {
            var rgb = unchecked((uint)value) & 0x00FFFFFF;
            return $"#{rgb:X6}";
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement item, string name, out JsonElement value)
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out value))
                return true;

            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static double? ParseDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"-?\d+(\.\d+)?");
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }

        private static double? ParseCoverageHoleLimit(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "default", "Default", "5g", "5G", "4g", "4G", "3g", "3G", "2g", "2G" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var element))
                        {
                            var parsed = element.ValueKind == JsonValueKind.Number
                                ? element.GetDouble()
                                : ParseDouble(element.ToString());

                            if (parsed.HasValue) return parsed;
                        }
                    }
                }
            }
            catch { }

            return ParseDouble(value);
        }
    }

    internal sealed record ThresholdRange(string Label, double Min, double Max, string ColorHex = "#808080")
    {
        public string? ValueMatch { get; set; }

        public bool Contains(double value)
        {
            if (!string.IsNullOrWhiteSpace(ValueMatch))
            {
                if (double.TryParse(ValueMatch, NumberStyles.Any, CultureInfo.InvariantCulture, out var matchVal))
                    return Math.Abs(value - matchVal) < 0.0001;
                return value.ToString(CultureInfo.InvariantCulture) == ValueMatch;
            }

            var low = Math.Min(Min, Max);
            var high = Math.Max(Min, Max);

            if (Math.Abs(low - high) < 0.0001)
                return Math.Abs(value - low) < 0.0001;

            return value >= low && value < high;
        }

        public bool ContainsInclusive(double value)
        {
            if (!string.IsNullOrWhiteSpace(ValueMatch)) return Contains(value);

            var low = Math.Min(Min, Max);
            var high = Math.Max(Min, Max);
            return value >= low && value <= high;
        }

        public string Display
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Label))
                {
                    if (Math.Abs(Min - Max) > 0.0001 && string.IsNullOrWhiteSpace(ValueMatch))
                        return $"{Label} ({Min:0.##} to {Max:0.##})";
                    return Label;
                }

                if (!string.IsNullOrWhiteSpace(ValueMatch))
                    return ValueMatch;

                return $"{Min:0.##} to {Max:0.##}";
            }
        }

        public string RangeOnlyDisplay
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ValueMatch))
                    return ValueMatch;

                if (Math.Abs(Min - Max) < 0.0001)
                    return $"{Min:0.##}";

                return $"{Min:0.##} to {Max:0.##}";
            }
        }
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

    internal static class UnifiedMapReportEarfcnHelper
    {
        public static IEnumerable<int> RowValues(UnifiedMapReportRow row)
        {
            if (row.EarfcnValues.Count > 0)
                return row.EarfcnValues;

            return row.Earfcn.HasValue
                ? new[] { row.Earfcn.Value }
                : Enumerable.Empty<int>();
        }

        public static List<int> DistinctValues(IEnumerable<UnifiedMapReportRow> rows)
        {
            var seen = new HashSet<int>();
            var values = new List<int>();

            foreach (var row in rows)
            {
                foreach (var value in RowValues(row))
                {
                    if (seen.Add(value))
                        values.Add(value);
                }
            }

            return values;
        }

        public static int CountSamples(IEnumerable<UnifiedMapReportRow> rows)
        {
            return rows.Count(row => RowValues(row).Any());
        }

        public static string FormatValues(IEnumerable<int> values)
        {
            return string.Join(", ", values.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        }
    }

    internal static class UnifiedMapReportFactory
    {
        public static UnifiedMapReport Create(
            UnifiedMapPdfRequest request,
            string? projectName,
            List<long> sessionIds,
            List<UnifiedMapReportRow> rows,
            ReportThresholdConfig thresholds,
            ReportLogo? companyLogo,
            ReportLogo? productLogo = null)
        {
            var orderedRows = rows.OrderBy(x => x.Timestamp ?? DateTime.MinValue).ThenBy(x => x.Id).ToList();

            var report = new UnifiedMapReport
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? "Drive Test Analytics Report" : request.Title.Trim(),
                Logo = companyLogo,
                CompanyLogo = companyLogo,
                ProductLogo = productLogo,
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
            report.BarCharts.Add(BuildBarChart("Band Distribution", orderedRows.Select(x => CleanGroup(x.Band, "")).Where(b => !string.IsNullOrWhiteSpace(b) && !b.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !b.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase)), 12));
            report.BarCharts.Add(BuildBarChart("PCI Distribution", orderedRows.Select(x => CleanGroup(x.Pci, "Unknown")), 5));
            report.BarCharts.Add(BuildBarChart("NodeB ID Distribution", orderedRows.Select(x => CleanGroup(x.NodebId, "Unknown")), 8));
            report.BarCharts.Add(BuildBarChart("Cell ID Distribution", orderedRows.Select(x => CleanGroup(x.CellId, "Unknown")), 8));
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
            
            report.Thresholds = thresholds;

            return report;
        }

        private static Dictionary<string, string> BuildSummary(UnifiedMapReport report, List<UnifiedMapReportRow> rows, ReportThresholdConfig thresholds)
        {
            var coverageHoleCount = rows.Count(x => x.Rsrp.HasValue && x.Rsrp.Value <= thresholds.CoverageHoleLimit);
            return new Dictionary<string, string>
            {
                ["Project"] = report.ProjectName,
                ["Sessions"] = string.Join(", ", report.SessionIds.Take(12)),
                ["Total drive logs"] = report.TotalRows.ToString("N0", CultureInfo.InvariantCulture),
                ["Date range"] = report.From.HasValue && report.To.HasValue ? $"{report.From:yyyy-MM-dd HH:mm} to {report.To:yyyy-MM-dd HH:mm}" : "N/A",
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

        private static ChartSeries BuildLineChart(string title, string unit, IEnumerable<float?> source) => BuildLineChart(title, unit, source.Select(x => x.HasValue ? (double?)x.Value : null));

        private static ChartSeries BuildLineChart(string title, string unit, IEnumerable<double?> source)
        {
            var values = source.Where(x => x.HasValue && !double.IsNaN(x.Value) && !double.IsInfinity(x.Value)).Select(x => x!.Value).ToList();
            return new ChartSeries { Title = title, Unit = unit, Values = Sample(values, 240) };
        }

        private static BarChartData BuildBarChart(string title, IEnumerable<string> groups, int take)
        {
            var items = groups.Select(x => CleanGroup(x, "Unknown")).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Select(g => (Label: g.Key, Value: (double)g.Count())).OrderByDescending(x => x.Value).ThenBy(x => x.Label).Take(take).ToList();
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
            var items = counts.Select(x => (Label: x.Key, Value: x.Value)).Where(x => x.Value > 0).ToList();
            if (unknown > 0) items.Add(("Outside configured ranges", unknown));
            return new BarChartData { Title = title, Items = items };
        }

        private static BarChartData BuildHandoverChart(List<UnifiedMapReportRow> rows)
        {
            var ordered = rows.Where(x => x.Timestamp.HasValue).OrderBy(x => x.Timestamp).ThenBy(x => x.Id).ToList();
            var tech = 0; var band = 0; var pci = 0;
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
            return new BarChartData { Title = "Handover / Change Summary", Items = new List<(string Label, double Value)> { ("Technology changes", tech), ("Band changes", band), ("PCI changes", pci) } };
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
                ("Packet Loss", rows.Select(x => x.PacketLoss.HasValue ? (double?)x.PacketLoss.Value : null), "%"),
                ("LTE BLER", rows.Select(x => ParseNumber(x.Bler)), "%"),
                ("VoLTE Call", rows.Select(x => (double?)x.VolteCall), ""),
                ("PUSCH TX", rows.Select(x => ParseNumber(x.PuschTx)), "dBm")
            };
            var table = new TableData { Title = "KPI Statistics", Headers = new List<string> { "Metric", "Average", "Minimum", "Maximum", "Samples" } };
            foreach (var metric in metrics)
            {
                var values = metric.Values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
                if (values.Count == 0) continue;
                table.Rows.Add(new List<string> { metric.Name, $"{values.Average():0.##} {metric.Unit}".Trim(), $"{values.Min():0.##} {metric.Unit}".Trim(), $"{values.Max():0.##} {metric.Unit}".Trim(), values.Count.ToString("N0", CultureInfo.InvariantCulture) });
            }

            var earfcnValues = UnifiedMapReportEarfcnHelper.DistinctValues(rows);
            if (earfcnValues.Count > 0)
                table.Rows.Add(new List<string> { "EARFCN", "N/A", UnifiedMapReportEarfcnHelper.FormatValues(earfcnValues), "N/A", UnifiedMapReportEarfcnHelper.CountSamples(rows).ToString("N0", CultureInfo.InvariantCulture) });

            return table;
        }

        private static TableData BuildThresholdTable(ReportThresholdConfig thresholds)
        {
            var table = new TableData { Title = "Configured KPI Ranges", Headers = new List<string> { "Metric","Minimum", "Maximum", "Source" } };
            AddRanges(table, "RSRP", thresholds.Rsrp, thresholds.Source);
            AddRanges(table, "RSRQ", thresholds.Rsrq, thresholds.Source);
            AddRanges(table, "SINR", thresholds.Sinr, thresholds.Source);
            AddRanges(table, "MOS", thresholds.Mos, thresholds.Source);
            table.Rows.Add(new List<string> { "Coverage Hole", thresholds.CoverageHoleLimit.ToString("0.##", CultureInfo.InvariantCulture), "", thresholds.Source });
            return table;
        }

        private static void AddRanges(TableData table, string metric, List<ThresholdRange> ranges, string source)
        {
            foreach (var range in ranges)
                table.Rows.Add(new List<string>
                {
                    metric,
                    range.Min.ToString("0.##", CultureInfo.InvariantCulture),
                    range.Max.ToString("0.##", CultureInfo.InvariantCulture),
                    source
                });
        }

        private static TableData BuildDriveLogTable(List<UnifiedMapReportRow> rows)
        {
            var table = new TableData { Title = "Drive Log Sample", Headers = new List<string> { "Time", "Session", "Lat", "Lon", "Tech", "Operator", "Band", "PCI", "RSRP", "SINR" } };
            foreach (var row in rows.Take(42)) table.Rows.Add(new List<string> { row.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "", row.SessionId?.ToString(CultureInfo.InvariantCulture) ?? "", row.Lat?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "", row.Lon?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "", ClassifyTechnology(row.Network), CleanGroup(row.Provider, ""), CleanGroup(row.Band, ""), CleanGroup(row.Pci, ""), row.Rsrp?.ToString("0.#", CultureInfo.InvariantCulture) ?? "", row.Sinr?.ToString("0.#", CultureInfo.InvariantCulture) ?? "" });
            return table;
        }

        private static TableData BuildNetworkSiteTable(List<UnifiedMapReportRow> rows)
        {
            var table = new TableData { Title = "Network Site Summary", Headers = new List<string> { "Band", "Operator", "NodeB ID", "Cell ID", "Samples" } };
            var grouped = rows.GroupBy(x => new { Band = CleanGroup(x.Band, ""), Operator = CleanGroup(x.Provider, "Unknown"), Nodeb = CleanGroup(x.NodebId, "Unknown"), Cell = CleanGroup(x.CellId, CleanGroup(x.Pci, "Unknown")) })
                .Where(g => !string.IsNullOrWhiteSpace(g.Key.Band) && !g.Key.Band.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !g.Key.Band.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase))
                .Select(g => new { g.Key.Band, g.Key.Operator, g.Key.Nodeb, g.Key.Cell, Count = g.Count() }).OrderByDescending(x => x.Count).ThenBy(x => x.Operator).Take(32);
            foreach (var item in grouped) table.Rows.Add(new List<string> { item.Band, item.Operator, item.Nodeb, item.Cell, item.Count.ToString("N0", CultureInfo.InvariantCulture) });
            return table;
        }

        private static List<double> Sample(List<double> values, int max)
        {
            if (values.Count <= max) return values;
            var sampled = new List<double>(max);
            for (var i = 0; i < max; i++) sampled.Add(values[(int)Math.Round(i * (values.Count - 1) / (double)(max - 1))]);
            return sampled;
        }

        private static IEnumerable<string> SplitApps(string? apps, string? appName) => Regex.Split(string.IsNullOrWhiteSpace(apps) ? appName ?? "" : apps, @"[,;|]+").Select(x => CleanGroup(x, "Unknown")).Where(x => !string.IsNullOrWhiteSpace(x));

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

        private static string CleanGroup(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static bool Same(string? left, string? right) => string.Equals(CleanGroup(left, ""), CleanGroup(right, ""), StringComparison.OrdinalIgnoreCase);

        private static double? ParseNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"-?\d+(\.\d+)?");
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        private static string FormatAverage(IEnumerable<float?> values, string unit) => FormatAverage(values.Select(x => x.HasValue ? (double?)x.Value : null), unit);
        private static string FormatAverage(IEnumerable<double?> values, string unit)
        {
            var list = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            return list.Count == 0 ? "N/A" : $"{list.Average():0.##} {unit}".Trim();
        }
    }

    internal sealed class LegendStatistics
    {
        public ThresholdRange Range { get; set; } = null!;
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    internal static class UnifiedMapRawPdfBuilder
    {
        private const double PageWidth = 596;
        private const double PageHeight = 842;
        private const double Margin = 40;
        private const double HeaderLogoBoxWidth = 58;
        private const double HeaderLogoBoxHeight = 38;
        private const double HeaderLogoBoxY = 764;
        private const int TopPciLimit = 5;
        private const double PoorRsrpLimit = -105;
        private const double PoorRsrqLimit = -14;
        private const string GpsOpenStreetImageKey = "GPS_OPENSTREET";
        private const string GpsSatelliteImageKey = "GPS_SATELLITE";
        private const double GpsPreviewBoundsPaddingRatio = 0.01;
        private const int GpsTileSize = 256;
        private const int GpsPreviewMinTileZoom = 14;
        private const int GpsPreviewMaxTileZoom = 20;
        private const int GpsPreviewMaxSatelliteTileZoom = 20;
        private const int GpsOpenStreetSourceMaxTileZoom = 19;
        private const int GpsSatelliteSourceMaxTileZoom = 18;
        private const double GpsPreviewTileFitRatio = 1.00;
        private const int MaxMapTileCacheEntries = 512;
        private static readonly ConcurrentDictionary<string, byte[]> MapTileCache = new();
        private static readonly ConcurrentQueue<string> MapTileCacheOrder = new();
        private static readonly HttpClient MapHttpClient = CreateMapHttpClient();

        private static HttpClient CreateMapHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SignalTrackerReport/1.0");
            return client;
        }

        public static byte[] Build(UnifiedMapReport report)
        {
            EnsureGpsPreviewImages(report);

            var contents = new List<byte[]>();
            contents.Add(BuildCoverPage(report));
            contents.Add(BuildTableOfContentsPage(report));
            contents.Add(BuildIntroductionPage(report));
            contents.Add(BuildAreaSummaryPage(report));
            contents.Add(BuildDriveAndKpiSummaryPage(report));

            contents.Add(BuildMapViewPage(report, "a) Band", BuildBandNarrative(report), FindBarChart(report, "Band Distribution"), "BAND"));
            contents.Add(BuildMapViewPage(report, "b) RSRP", BuildMetricNarrative(report, "RSRP", "Reference Signal Received Power", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrp))), "dBm", PoorRsrpLimit, "falling below -105 dBm"), null, "RSRP", report.Thresholds?.Rsrp));
            contents.Add(BuildMapViewPage(report, "c) RSRQ", BuildMetricNarrative(report, "RSRQ", "Reference Signal Received Quality", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrq))), "dB", PoorRsrqLimit, "falling below -14 dB"), null, "RSRQ", report.Thresholds?.Rsrq));
            contents.Add(BuildMapViewPage(report, "d) SINR", BuildMetricNarrative(report, "SINR", "Signal-to-Interference Noise Ratio", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Sinr))), "dB", 5, "falling below 5 dB"), null, "SINR", report.Thresholds?.Sinr));
            contents.Add(BuildMapViewPage(report, "e) DL_THPT", BuildMetricNarrative(report, "DL throughput", "Downlink throughput", MetricStats(report.Rows.Select(x => ParseNumber(x.DlTpt))), "Mbps", 10, "falling below 10 Mbps"), null, "DL_THPT", report.Thresholds?.DlTpt));
            contents.Add(BuildMapViewPage(report, "f) UL_THPT", BuildMetricNarrative(report, "UL throughput", "Uplink throughput", MetricStats(report.Rows.Select(x => ParseNumber(x.UlTpt))), "Mbps", 5, "falling below 5 Mbps"), null, "UL_THPT", report.Thresholds?.UlTpt));
            contents.Add(BuildMapViewPage(report, "g) EARFCN", BuildMetricNarrative(report, "EARFCN", "E-UTRA Absolute Radio Frequency Channel Number", new MetricSummary(0, 0, 0, 0), ""), null, "EARFCN", report.Thresholds?.Earfcn));

            if (HasLteBlerData(report))
                contents.Add(BuildMapViewPage(report, "h) LTE BLER", BuildMetricNarrative(report, "LTE BLER", "Block Error Rate", MetricStats(report.Rows.Select(x => ParseNumber(x.Bler))), "%", 10, "exceeding 10%", true), null, "LTE_BLER", report.Thresholds?.Bler));

            if (HasVolteCallData(report))
                contents.Add(BuildMapViewPage(report, "i) VoLTE Call", BuildMetricNarrative(report, "VoLTE Call", "Voice over LTE Call Status", MetricStats(report.Rows.Select(x => (double?)x.VolteCall)), "", 1, "failing or dropping", false), null, "VOLTE_CALL", report.Thresholds?.VolteCall));

            contents.Add(BuildMapViewPage(report, "j) PUSCH TX", BuildMetricNarrative(report, "PUSCH TX", "Physical Uplink Shared Channel Transmit Power", MetricStats(report.Rows.Select(x => ParseNumber(x.PuschTx))), "dBm", 10, "exceeding optimal power", true), null, "PUSCH_TX", report.Thresholds?.PuschTx));
            contents.Add(BuildMapViewPage(report, "k) NodeB ID", "NodeB ID identifies the physical site (base station) serving the drive route.", FindBarChart(report, "NodeB ID Distribution"), "NODEB_ID"));
            contents.Add(BuildMapViewPage(report, "l) Cell ID", "Cell ID identifies the specific cell / sector serving the drive route.", FindBarChart(report, "Cell ID Distribution"), "CI"));
            contents.Add(BuildMapViewPage(report, "m) PCI", "PCI (Physical Cell Identity) is used to distinguish between neighbouring cells on the same frequency.", FindBarChart(report, "PCI Distribution"), "PCI"));

            contents.Add(BuildPciSummaryPage(report));
            contents.Add(BuildPciDetailsPage(report));
            contents.Add(BuildPerformanceSummaryPage(report));

            return WritePdf(contents, report);
        }

        private static bool HasLteBlerData(UnifiedMapReport report) =>
            report.Rows.Any(x => ParseNumber(x.Bler).HasValue);

        private static bool HasVolteCallData(UnifiedMapReport report) =>
            report.Rows.Any(x => x.VolteCall.HasValue);

        private static void EnsureGpsPreviewImages(UnifiedMapReport report)
        {
            var points = GetGpsPoints(report);
            if (points.Count == 0) return;

            if (!report.MapImages.ContainsKey(GpsOpenStreetImageKey))
            {
                var openStreet = BuildGpsPreviewImage(points, satellite: false);
                if (openStreet != null) report.MapImages[GpsOpenStreetImageKey] = openStreet;
            }

            if (!report.MapImages.ContainsKey(GpsSatelliteImageKey))
            {
                var satellite = BuildGpsPreviewImage(points, satellite: true);
                if (satellite != null) report.MapImages[GpsSatelliteImageKey] = satellite;
            }
        }

        private static List<(double Lat, double Lon)> GetGpsPoints(UnifiedMapReport report)
        {
            return report.Rows
                .Where(x => x.Lat.HasValue && x.Lon.HasValue && x.Lat.Value is >= -90 and <= 90 && x.Lon.Value is >= -180 and <= 180)
                .Select(x => ((double)x.Lat!.Value, (double)x.Lon!.Value))
                .ToList();
        }

        private static ReportLogo? BuildGpsPreviewImage(List<(double Lat, double Lon)> points, bool satellite)
        {
            const int width = 520;
            const int height = 244;

            return BuildGpsTilePreviewImage(points, satellite, width, height) ??
                   BuildGpsSketchPreviewImage(points, satellite, width, height);
        }

        private static ReportLogo? BuildGpsTilePreviewImage(List<(double Lat, double Lon)> points, bool satellite, int width, int height)
        {
            var maxZoom = satellite
                ? Math.Min(GpsPreviewMaxTileZoom, GpsPreviewMaxSatelliteTileZoom)
                : GpsPreviewMaxTileZoom;

            for (var candidateMaxZoom = maxZoom; candidateMaxZoom >= GpsPreviewMinTileZoom;)
            {
                var viewport = ChooseGpsTileViewport(points, width, height, candidateMaxZoom);
                var image = TryBuildGpsTilePreviewImage(points, satellite, width, height, viewport);
                if (image != null) return image;

                candidateMaxZoom = viewport.Zoom - 1;
            }

            return null;
        }

        private static ReportLogo? TryBuildGpsTilePreviewImage(
            List<(double Lat, double Lon)> points,
            bool satellite,
            int width,
            int height,
            (int Zoom, double CenterX, double CenterY) viewport)
        {
            try
            {
                using var image = new Image<Rgb24>(width, height);
                FillRect(image, 0, 0, width, height, satellite ? new Rgb24(39, 55, 44) : new Rgb24(238, 235, 226));

                var sourceZoom = GetGpsTileSourceZoom(viewport.Zoom, satellite);
                var scale = 1 << (viewport.Zoom - sourceZoom);
                var topLeftX = viewport.CenterX - (width / 2.0);
                var topLeftY = viewport.CenterY - (height / 2.0);
                var sourceTopLeftX = topLeftX / scale;
                var sourceTopLeftY = topLeftY / scale;
                var sourceWidth = width / (double)scale;
                var sourceHeight = height / (double)scale;
                var minTileX = (int)Math.Floor(sourceTopLeftX / GpsTileSize);
                var maxTileX = (int)Math.Floor((sourceTopLeftX + sourceWidth - 1) / GpsTileSize);
                var minTileY = (int)Math.Floor(sourceTopLeftY / GpsTileSize);
                var maxTileY = (int)Math.Floor((sourceTopLeftY + sourceHeight - 1) / GpsTileSize);
                var downloadedTiles = 0;

                for (var tileY = minTileY; tileY <= maxTileY; tileY++)
                {
                    if (tileY < 0 || tileY >= (1 << sourceZoom)) continue;

                    for (var tileX = minTileX; tileX <= maxTileX; tileX++)
                    {
                        var tileBytes = TryDownloadMapTile(sourceZoom, WrapTileX(tileX, sourceZoom), tileY, satellite);
                        if (tileBytes == null) continue;

                        try
                        {
                            using var tile = Image.Load<Rgb24>(tileBytes);
                            var destX = ((tileX * GpsTileSize) - sourceTopLeftX) * scale;
                            var destY = ((tileY * GpsTileSize) - sourceTopLeftY) * scale;
                            CopyImageScaled(tile, image, destX, destY, scale);
                            downloadedTiles++;
                        }
                        catch
                        {
                            // Leave this tile blank and keep the rest of the map usable.
                        }
                    }
                }

                if (downloadedTiles == 0) return null;

                var projected = ProjectGpsPointsToTileViewport(points, viewport.Zoom, topLeftX, topLeftY);
                DrawGpsRoute(image, projected, satellite);

                using var ms = new MemoryStream();
                image.Save(ms, new JpegEncoder { Quality = 90 });
                return new ReportLogo(ms.ToArray(), width, height);
            }
            catch
            {
                return null;
            }
        }

        private static ReportLogo? BuildGpsSketchPreviewImage(List<(double Lat, double Lon)> points, bool satellite, int width, int height)
        {
            try
            {
                using var image = new Image<Rgb24>(width, height);
                DrawMapBase(image, satellite);

                var projected = ProjectGpsPoints(points, width, height);
                DrawGpsRoute(image, projected, satellite);

                using var ms = new MemoryStream();
                image.Save(ms, new JpegEncoder { Quality = 88 });
                return new ReportLogo(ms.ToArray(), width, height);
            }
            catch
            {
                return null;
            }
        }

        private static (int Zoom, double CenterX, double CenterY) ChooseGpsTileViewport(List<(double Lat, double Lon)> points, int width, int height, int maxZoom)
        {
            for (var zoom = maxZoom; zoom >= GpsPreviewMinTileZoom; zoom--)
            {
                var projected = points.Select(p => LatLonToGlobalPixel(p.Lat, p.Lon, zoom)).ToList();
                var minX = projected.Min(p => p.X);
                var maxX = projected.Max(p => p.X);
                var minY = projected.Min(p => p.Y);
                var maxY = projected.Max(p => p.Y);
                var routeWidth = maxX - minX;
                var routeHeight = maxY - minY;

                if ((routeWidth <= width * GpsPreviewTileFitRatio && routeHeight <= height * GpsPreviewTileFitRatio) ||
                    zoom == GpsPreviewMinTileZoom)
                {
                    return (zoom, (minX + maxX) / 2.0, (minY + maxY) / 2.0);
                }
            }

            var center = LatLonToGlobalPixel(points.Average(p => p.Lat), points.Average(p => p.Lon), GpsPreviewMinTileZoom);
            return (GpsPreviewMinTileZoom, center.X, center.Y);
        }

        private static (double X, double Y) LatLonToGlobalPixel(double lat, double lon, int zoom)
        {
            lat = Math.Clamp(lat, -85.05112878, 85.05112878);
            lon = Math.Clamp(lon, -180, 180);

            var sinLat = Math.Sin(lat * Math.PI / 180.0);
            var scale = GpsTileSize * Math.Pow(2, zoom);
            var x = (lon + 180.0) / 360.0 * scale;
            var y = (0.5 - Math.Log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI)) * scale;
            return (x, y);
        }

        private static List<(int X, int Y)> ProjectGpsPointsToTileViewport(List<(double Lat, double Lon)> points, int zoom, double topLeftX, double topLeftY)
        {
            return points
                .Select(p =>
                {
                    var projected = LatLonToGlobalPixel(p.Lat, p.Lon, zoom);
                    return ((int)Math.Round(projected.X - topLeftX), (int)Math.Round(projected.Y - topLeftY));
                })
                .ToList();
        }

        private static int WrapTileX(int tileX, int zoom)
        {
            var tiles = 1 << zoom;
            return ((tileX % tiles) + tiles) % tiles;
        }

        private static int GetGpsTileSourceZoom(int displayZoom, bool satellite)
        {
            var sourceMaxZoom = satellite ? GpsSatelliteSourceMaxTileZoom : GpsOpenStreetSourceMaxTileZoom;
            return Math.Min(displayZoom, sourceMaxZoom);
        }

        private static byte[]? TryDownloadMapTile(int zoom, int tileX, int tileY, bool satellite)
        {
            var key = $"{(satellite ? "sat" : "osm")}:{zoom}:{tileX}:{tileY}";
            if (MapTileCache.TryGetValue(key, out var cachedBytes))
                return cachedBytes;

            var url = satellite
                ? $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{zoom}/{tileY}/{tileX}"
                : $"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png";

            try
            {
                using var response = MapHttpClient.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return null;

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType != null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return null;

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length == 0) return null;
                if (satellite && IsUnavailableSatelliteTile(bytes)) return null;

                AddMapTileToCache(key, bytes);
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static void AddMapTileToCache(string key, byte[] bytes)
        {
            if (!MapTileCache.TryAdd(key, bytes))
                return;

            MapTileCacheOrder.Enqueue(key);
            while (MapTileCache.Count > MaxMapTileCacheEntries && MapTileCacheOrder.TryDequeue(out var oldKey))
                MapTileCache.TryRemove(oldKey, out _);
        }

        private static bool IsUnavailableSatelliteTile(byte[] bytes)
        {
            try
            {
                using var image = Image.Load<Rgb24>(bytes);
                var samples = 0;
                var saturationSum = 0.0;
                var luminanceSum = 0.0;
                var luminanceSquaredSum = 0.0;

                for (var y = 8; y < image.Height; y += 16)
                {
                    for (var x = 8; x < image.Width; x += 16)
                    {
                        var pixel = image[x, y];
                        var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                        var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                        var saturation = max == 0 ? 0 : (max - min) / (double)max;
                        var luminance = (pixel.R + pixel.G + pixel.B) / 3.0;

                        saturationSum += saturation;
                        luminanceSum += luminance;
                        luminanceSquaredSum += luminance * luminance;
                        samples++;
                    }
                }

                if (samples == 0) return false;

                var averageSaturation = saturationSum / samples;
                var averageLuminance = luminanceSum / samples;
                var luminanceVariance = (luminanceSquaredSum / samples) - (averageLuminance * averageLuminance);
                var luminanceStdDev = Math.Sqrt(Math.Max(0, luminanceVariance));

                return averageSaturation < 0.045 && luminanceStdDev < 16;
            }
            catch
            {
                return false;
            }
        }

        private static void CopyImageScaled(Image<Rgb24> source, Image<Rgb24> target, double destX, double destY, int scale)
        {
            scale = Math.Max(1, scale);
            var destWidth = source.Width * scale;
            var destHeight = source.Height * scale;
            var startX = Math.Max(0, (int)Math.Floor(destX));
            var endX = Math.Min(target.Width, (int)Math.Ceiling(destX + destWidth));
            var startY = Math.Max(0, (int)Math.Floor(destY));
            var endY = Math.Min(target.Height, (int)Math.Ceiling(destY + destHeight));

            for (var y = startY; y < endY; y++)
            {
                var sourceY = (int)Math.Floor((y - destY) / scale);
                sourceY = Math.Clamp(sourceY, 0, source.Height - 1);

                for (var x = startX; x < endX; x++)
                {
                    var sourceX = (int)Math.Floor((x - destX) / scale);
                    sourceX = Math.Clamp(sourceX, 0, source.Width - 1);
                    target[x, y] = source[sourceX, sourceY];
                }
            }
        }

        private static void CopyImage(Image<Rgb24> source, Image<Rgb24> target, int destX, int destY)
        {
            for (var y = 0; y < source.Height; y++)
            {
                var targetY = destY + y;
                if (targetY < 0 || targetY >= target.Height) continue;

                for (var x = 0; x < source.Width; x++)
                {
                    var targetX = destX + x;
                    if (targetX < 0 || targetX >= target.Width) continue;
                    target[targetX, targetY] = source[x, y];
                }
            }
        }

        private static void DrawGpsRoute(Image<Rgb24> image, List<(int X, int Y)> projected, bool satellite)
        {
            if (projected.Count > 1)
            {
                var shadow = satellite ? new Rgb24(16, 24, 39) : new Rgb24(255, 255, 255);
                var route = satellite ? new Rgb24(255, 221, 72) : new Rgb24(29, 91, 191);
                DrawPolyline(image, projected, shadow, 7);
                DrawPolyline(image, projected, route, 3);
            }

            if (projected.Count > 0)
            {
                DrawCircle(image, projected.First().X, projected.First().Y, 6, new Rgb24(46, 160, 67));
                DrawCircle(image, projected.Last().X, projected.Last().Y, 6, new Rgb24(220, 38, 38));
            }
        }

        private static List<(int X, int Y)> ProjectGpsPoints(List<(double Lat, double Lon)> points, int width, int height)
        {
            var minLat = points.Min(x => x.Lat);
            var maxLat = points.Max(x => x.Lat);
            var minLon = points.Min(x => x.Lon);
            var maxLon = points.Max(x => x.Lon);

            if (Math.Abs(maxLat - minLat) < 0.000001)
            {
                minLat -= 0.0005;
                maxLat += 0.0005;
            }

            if (Math.Abs(maxLon - minLon) < 0.000001)
            {
                minLon -= 0.0005;
                maxLon += 0.0005;
            }

            var latPadding = (maxLat - minLat) * GpsPreviewBoundsPaddingRatio;
            var lonPadding = (maxLon - minLon) * GpsPreviewBoundsPaddingRatio;
            minLat -= latPadding;
            maxLat += latPadding;
            minLon -= lonPadding;
            maxLon += lonPadding;

            const int padding = 22;
            return points.Select(p =>
            {
                var x = padding + (p.Lon - minLon) / (maxLon - minLon) * (width - (padding * 2));
                var y = padding + (maxLat - p.Lat) / (maxLat - minLat) * (height - (padding * 2));
                return ((int)Math.Round(x), (int)Math.Round(y));
            }).ToList();
        }

        private static void DrawMapBase(Image<Rgb24> image, bool satellite)
        {
            if (satellite)
            {
                for (var y = 0; y < image.Height; y++)
                {
                    for (var x = 0; x < image.Width; x++)
                    {
                        var noise = (x * 31 + y * 17 + ((x * y) % 43)) % 38;
                        image[x, y] = new Rgb24((byte)(39 + noise / 4), (byte)(73 + noise), (byte)(45 + noise / 3));
                    }
                }

                FillRect(image, 30, 25, 150, 70, new Rgb24(71, 101, 48));
                FillRect(image, 335, 42, 150, 80, new Rgb24(87, 91, 55));
                FillRect(image, 210, 135, 230, 75, new Rgb24(61, 92, 70));
                DrawLine(image, 0, 205, image.Width, 160, new Rgb24(92, 84, 72), 7);
                DrawLine(image, 10, 18, image.Width - 30, 82, new Rgb24(84, 91, 76), 5);
                return;
            }

            FillRect(image, 0, 0, image.Width, image.Height, new Rgb24(244, 241, 232));
            FillRect(image, 0, image.Height - 48, image.Width, 48, new Rgb24(207, 232, 241));

            for (var x = 32; x < image.Width; x += 70)
                DrawLine(image, x, 0, x + 30, image.Height, new Rgb24(218, 214, 204), 1);

            for (var y = 28; y < image.Height; y += 58)
                DrawLine(image, 0, y, image.Width, y + 12, new Rgb24(218, 214, 204), 1);

            DrawLine(image, 0, 190, image.Width, 122, new Rgb24(255, 255, 255), 8);
            DrawLine(image, 0, 190, image.Width, 122, new Rgb24(230, 170, 92), 3);
            DrawLine(image, 40, 0, 430, image.Height, new Rgb24(255, 255, 255), 6);
            DrawLine(image, 40, 0, 430, image.Height, new Rgb24(190, 190, 184), 2);
            DrawLine(image, 0, 88, image.Width, 75, new Rgb24(255, 255, 255), 5);
            DrawLine(image, 0, 88, image.Width, 75, new Rgb24(190, 190, 184), 2);
        }

        private static void FillRect(Image<Rgb24> image, int x, int y, int width, int height, Rgb24 color)
        {
            var minX = Math.Clamp(x, 0, image.Width);
            var maxX = Math.Clamp(x + width, 0, image.Width);
            var minY = Math.Clamp(y, 0, image.Height);
            var maxY = Math.Clamp(y + height, 0, image.Height);

            for (var yy = minY; yy < maxY; yy++)
            {
                for (var xx = minX; xx < maxX; xx++)
                    image[xx, yy] = color;
            }
        }

        private static void DrawPolyline(Image<Rgb24> image, List<(int X, int Y)> points, Rgb24 color, int thickness)
        {
            for (var i = 1; i < points.Count; i++)
                DrawLine(image, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y, color, thickness);
        }

        private static void DrawLine(Image<Rgb24> image, int x0, int y0, int x1, int y1, Rgb24 color, int thickness)
        {
            var dx = Math.Abs(x1 - x0);
            var sx = x0 < x1 ? 1 : -1;
            var dy = -Math.Abs(y1 - y0);
            var sy = y0 < y1 ? 1 : -1;
            var err = dx + dy;

            while (true)
            {
                DrawCircle(image, x0, y0, Math.Max(1, thickness / 2), color);
                if (x0 == x1 && y0 == y1) break;

                var e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private static void DrawCircle(Image<Rgb24> image, int cx, int cy, int radius, Rgb24 color)
        {
            var radiusSquared = radius * radius;
            for (var y = cy - radius; y <= cy + radius; y++)
            {
                for (var x = cx - radius; x <= cx + radius; x++)
                {
                    if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                    var dx = x - cx;
                    var dy = y - cy;
                    if ((dx * dx) + (dy * dy) <= radiusSquared)
                        image[x, y] = color;
                }
            }
        }

        private static byte[] BuildCoverPage(UnifiedMapReport report)
        {
            var lines = PageBackground();
            var companyLogo = report.CompanyLogo ?? report.Logo;
            var productLogo = report.ProductLogo ?? report.Logo;
            if (companyLogo != null)
            {
                DrawImageFit(lines, "CompanyLogo", companyLogo, Margin, 780, 118, 42);
            }

            lines.Add(FillColor(15, 23, 42));
            lines.Add(TextCenter(PageWidth / 2, 520, 24, "Drive Test Report"));
            lines.Add(TextCenter(PageWidth / 2, 490, 13, report.ProjectName));
            lines.Add(TextCenter(PageWidth / 2, 452, 11, $"Generated on {report.GeneratedAt:MMMM dd, yyyy}"));
            lines.Add(StrokeColor(37, 99, 235));
            lines.Add("2 w");
            lines.Add($"{Fmt(170)} {Fmt(475)} m {Fmt(426)} {Fmt(475)} l S");
            if (productLogo != null)
            {
                DrawImageFit(lines, "ProductLogo", productLogo, (PageWidth - 130) / 2, 600, 130, 76);
            }
            lines.Add(FillColor(71, 85, 105));
        
            lines.Add("Q");
            return Ascii(string.Join("\n", lines) + "\n");
        }
            
        private static byte[] BuildTableOfContentsPage(UnifiedMapReport report)
        {
            var lines = Header(report, "Table of Contents");
            var y = 710.0;
            var entries = new List<(string Title, string Page)>
            {
                ("1. Introduction", "3"),
                ("2. Area Summary", "4"),
                ("3. Drive Summary", "5"),
                ("4. KPI Summary", "5"),
                ("5. Map View", "6")
            };

            var page = 6;
            void AddMapEntry(string title, bool include = true)
            {
                if (!include) return;
                entries.Add((title, page.ToString(CultureInfo.InvariantCulture)));
                page++;
            }

            AddMapEntry("   a) Band");
            AddMapEntry("   b) RSRP");
            AddMapEntry("   c) RSRQ");
            AddMapEntry("   d) SINR");
            AddMapEntry("   e) DL Throughput");
            AddMapEntry("   f) UL Throughput");
            AddMapEntry("   g) Earfcn");
            AddMapEntry("   h) LTE BLER", HasLteBlerData(report));
            AddMapEntry("   i) VoLTE Call", HasVolteCallData(report));
            AddMapEntry("   j) PUSCH TX");
            AddMapEntry("   k) NodeB ID");
            AddMapEntry("   l) Cell ID");
            AddMapEntry("   m) PCI");

            var pciSummaryPage = page++;
            var pciDetailsPage = page++;
            var performancePage = page;
            entries.Add(("6. PCI Summary", pciSummaryPage.ToString(CultureInfo.InvariantCulture)));
            entries.Add(("   a) Top PCI Values", pciDetailsPage.ToString(CultureInfo.InvariantCulture)));
            entries.Add(("   b) PCI with Poor RSRP", pciDetailsPage.ToString(CultureInfo.InvariantCulture)));
            entries.Add(("   c) PCI with Poor RSRQ", pciDetailsPage.ToString(CultureInfo.InvariantCulture)));
            entries.Add(("7. Performance Summary", performancePage.ToString(CultureInfo.InvariantCulture)));

            foreach (var entry in entries)
            {
                lines.Add(Text(Margin, y, 11, entry.Title));
                lines.Add(TextRight(PageWidth - Margin, y, 11, entry.Page));
                y -= 22; 
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
            var lines = Header(report, "");
            var y = 710.0;
            // AddWrapped(lines, Margin, ref y, "Drive route covers key operational areas identified from collected GPS samples and session density.", 11, 86);
            // y -= 10;
            AddSectionTitle(lines, "Marked Locations", ref y, 13);
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
            DrawTable(lines, Margin, ref y, new[] { "Metric", "Average", "Minimum", "Maximum", "Samples" }, BuildKpiRows(report).Take(12).ToList(), 10);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildMapViewPage(UnifiedMapReport report, string subsection, string narrative, BarChartData? chart = null, string imageKey = "", List<ThresholdRange>? legendRanges = null)
        {
            var lines = Header(report, $"5. Map View - {subsection}");
            var y = 710.0;
            AddWrapped(lines, Margin, ref y, narrative, 11, 86);
            
            if (!string.IsNullOrEmpty(imageKey) && report.MapImages.TryGetValue(imageKey, out var img))
            {
                y -= 20;
                double imgWidth = PageWidth - (Margin * 2);
                double imgHeight = imgWidth * img.Height / Math.Max(img.Width, 1);
                
                if (imgHeight > 320)
                {
                    imgHeight = 320;
                    imgWidth = imgHeight * img.Width / Math.Max(img.Height, 1);
                }
                
                y -= imgHeight;
                lines.Add($"q {Fmt(imgWidth)} 0 0 {Fmt(imgHeight)} {Fmt(Margin)} {Fmt(y)} cm /Img_{imageKey} Do Q");
            }

            if (legendRanges != null && legendRanges.Count > 0)
            {
                List<LegendStatistics> legends = new List<LegendStatistics>();

                switch (imageKey.ToUpper())
                {
                    case "RSRP": legends = CalculateLegendStatistics(report.Rows.Select(x => ToNullableDouble(x.Rsrp)), legendRanges); break;
                    case "RSRQ": legends = CalculateLegendStatistics(report.Rows.Select(x => ToNullableDouble(x.Rsrq)), legendRanges); break;
                    case "SINR": legends = CalculateLegendStatistics(report.Rows.Select(x => ToNullableDouble(x.Sinr)), legendRanges); break;
                    case "DL_THPT": legends = CalculateLegendStatistics(report.Rows.Select(x => ParseNumber(x.DlTpt)), legendRanges); break;
                    case "UL_THPT": legends = CalculateLegendStatistics(report.Rows.Select(x => ParseNumber(x.UlTpt)), legendRanges); break;
                    case "EARFCN":
                        legends = CalculateLegendStatistics(report.Rows.SelectMany(x => UnifiedMapReportEarfcnHelper.RowValues(x).Select(value => (double?)value)), legendRanges);
                        break;
                    case "LTE_BLER":
                        legends = UsesNumericLegend(legendRanges)
                            ? CalculateLegendStatistics(report.Rows.Select(x => ParseNumber(x.Bler)), legendRanges)
                            : CalculateStringLegendStatistics(report.Rows.Select(x => x.Bler), legendRanges);
                        break;
                    // REVERTED: VoLTE Call passed back as an integer (double?)
                    case "VOLTE_CALL": legends = CalculateLegendStatistics(report.Rows.Select(x => (double?)x.VolteCall), legendRanges); break;
                    case "PUSCH_TX": legends = CalculateLegendStatistics(report.Rows.Select(x => ParseNumber(x.PuschTx)), legendRanges); break;
                }

                if (legends.Count > 0)
                {
                    y -= 20;
                    bool isEarfcn = string.Equals(imageKey, "EARFCN", StringComparison.OrdinalIgnoreCase);
                    DrawLegendStatistics(lines, Margin, ref y, legends, isEarfcn);
                }
            }

            if (chart != null)
            {
                y -= 40;
                DrawBarChart(lines, chart, Margin, y - 20, PageWidth - (Margin * 2), 260);
            }
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static List<LegendStatistics> CalculateLegendStatistics(IEnumerable<double?> values, List<ThresholdRange> ranges)
        {
            var validValues = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            int total = validValues.Count;

            var result = ranges.Select(r => new LegendStatistics { Range = r, Count = 0 }).ToList();

            if (total > 0 && result.Count > 0)
            {
                foreach (var val in validValues)
                {
                    var match = result.FirstOrDefault(r => r.Range.Contains(val)) ?? 
                                result.FirstOrDefault(r => r.Range.ContainsInclusive(val));

                    if (match != null)
                    {
                        match.Count++;
                    }
                }

                foreach (var item in result)
                {
                    item.Percentage = item.Count * 100.0 / total;
                }
            }

            return result;
        }

        private static bool UsesNumericLegend(List<ThresholdRange> ranges)
        {
            return ranges.Any(range =>
            {
                if (string.IsNullOrWhiteSpace(range.ValueMatch)) return true;
                return double.TryParse(range.ValueMatch, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
            });
        }
        
        private static List<LegendStatistics> CalculateStringLegendStatistics(IEnumerable<string?> values, List<ThresholdRange> ranges)
        {
            var validValues = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim().ToUpperInvariant()).ToList();
            int total = validValues.Count;

            var result = ranges.Select(r => new LegendStatistics { Range = r, Count = 0 }).ToList();

            if (total > 0 && result.Count > 0)
            {
                foreach (var val in validValues)
                {
                    var match = result.FirstOrDefault(r => 
                        !string.IsNullOrWhiteSpace(r.Range.ValueMatch) && 
                        val.Contains(r.Range.ValueMatch.ToUpperInvariant()));

                    if (match != null)
                    {
                        match.Count++;
                    }
                }

                foreach (var item in result)
                {
                    item.Percentage = item.Count * 100.0 / total;
                }
            }

            return result;
        }

        private static void DrawLegendStatistics(List<string> lines, double x, ref double y, List<LegendStatistics> legends, bool isEarfcn = false)
        {
            if (legends.Count == 0) return;

            lines.Add(FillColor(0,0,0));
            lines.Add(Text(x, y, 11, "Legend"));

            y -= 18;

            foreach (var item in legends)
            {
                var color = ParseHexColor(item.Range.ColorHex);

                lines.Add(FillColor(color.R, color.G, color.B));
                lines.Add(Rect(x, y - 7, 10, 10, true));

                lines.Add(FillColor(0,0,0));

                lines.Add(Text(x + 18, y - 6, 8.5, BuildLegendText(item, isEarfcn)));
                y -= 14;
            }
        }

        private static string BuildLegendText(LegendStatistics item, bool isEarfcn = false)
        {
            var textDisplay = isEarfcn ? item.Range.Display : item.Range.RangeOnlyDisplay;
            return $"{textDisplay}  {item.Count}   {item.Percentage:0.00}%";
        }

        private static string FormatLegendRange(ThresholdRange range)
        {
            if (Math.Abs(range.Min - range.Max) < 0.0001)
                return $"{range.Min:0.##}";

            return $"{range.Min:0.##} to {range.Max:0.##}";
        }

        private static (byte R, byte G, byte B) ParseHexColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return (128, 128, 128);
            
            hex = hex.TrimStart('#');
            
            if (hex.Length == 3) 
                hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });

            if (hex.Length == 8)
                hex = hex[2..];
                
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c))
            {
                return ((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
            }
            
            return (128, 128, 128); 
        }

        private static byte[] BuildPciSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "6. PCI Summary");
            var y = 710.0;
            var pciGroups = PciGroups(report).ToList();
            var unique = pciGroups.Count;
            var topPciSamples = pciGroups.Take(TopPciLimit).Sum(x => x.Count);
            var percent = report.TotalRows == 0 ? 0 : topPciSamples * 100.0 / report.TotalRows;
            AddWrapped(lines, Margin, ref y, $"The network utilized a total of {unique:N0} unique PCI values during the drive test. The top {TopPciLimit} PCI values accounted for {percent:0.##}% of samples, indicating the concentration of PCI distribution across the measured route.", 11, 86);
            y -= 20;
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Samples", "Share" }, pciGroups.Take(TopPciLimit).Select(x => new List<string> { x.Pci, x.Count.ToString("N0", CultureInfo.InvariantCulture), $"{(report.TotalRows == 0 ? 0 : x.Count * 100.0 / report.TotalRows):0.##}%" }).ToList(), 10);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildPciDetailsPage(UnifiedMapReport report)
        {
            var lines = Header(report, "6. PCI Summary - Details");
            var y = 710.0;
            AddSectionTitle(lines, $"a) Top {TopPciLimit} PCI Values", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Samples" }, PciGroups(report).Take(TopPciLimit).Select(x => new List<string> { x.Pci, x.Count.ToString("N0", CultureInfo.InvariantCulture) }).ToList(), 9);
            y -= 14;
            AddSectionTitle(lines, $"b) PCI with Poor RSRP (< {PoorRsrpLimit:0.#} dBm)", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Threshold", "Poor RSRP Samples" }, PoorPciGroups(report, x => x.Rsrp.HasValue && x.Rsrp.Value < PoorRsrpLimit).Take(8).Select(x => new List<string> { x.Pci, $"< {PoorRsrpLimit:0.#} dBm", x.Count.ToString("N0", CultureInfo.InvariantCulture) }).ToList(), 9);
            y -= 14;
            AddSectionTitle(lines, $"c) PCI with Poor RSRQ (< {PoorRsrqLimit:0.#} dB)", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "PCI", "Threshold", "Poor RSRQ Samples" }, PoorPciGroups(report, x => x.Rsrq.HasValue && x.Rsrq.Value < PoorRsrqLimit).Take(8).Select(x => new List<string> { x.Pci, $"< {PoorRsrqLimit:0.#} dB", x.Count.ToString("N0", CultureInfo.InvariantCulture) }).ToList(), 9);
            return Ascii(string.Join("\n", lines) + "\n");
        }

        private static byte[] BuildPerformanceSummaryPage(UnifiedMapReport report)
        {
            var lines = Header(report, "7. Performance Summary");
            var y = 710.0;
            AddSectionTitle(lines, "a) Network Quality Metrics", ref y, 13);
            DrawTable(lines, Margin, ref y, new[] { "Metric", "Average", "Threshold", "Poor Samples" }, BuildQualityRows(report), 9);
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
            var headerTextX = Margin;
            var productLogo = report.ProductLogo ?? report.Logo;
            if (productLogo != null)
            {
                DrawImageFit(lines, "ProductLogo", productLogo, Margin, HeaderLogoBoxY, HeaderLogoBoxWidth, HeaderLogoBoxHeight);
                headerTextX = Margin + HeaderLogoBoxWidth + 14;
            }

            lines.AddRange(new[]
            {
                FillColor(15, 23, 42),
                Text(headerTextX, 792, 13, "Drive Test Report"),
                FillColor(71, 85, 105),
                Text(headerTextX, 775, 8.5, report.ProjectName),
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

        private static string BuildMetricNarrative(UnifiedMapReport report, string metric, string description, MetricSummary stats, string unit, double poorLimit = 0, string poorText = "", bool poorIsHigher = false)
        {
            if (metric.Equals("EARFCN", StringComparison.OrdinalIgnoreCase))
                return BuildEarfcnNarrative(report, description);

            if (stats.Count == 0)
                return $"{metric} ({description}) was analyzed across the drive route, but no valid samples were available for this metric.";

            var poorCount = report.Rows.Count(x =>
            {
                var value = metric.Equals("DL throughput", StringComparison.OrdinalIgnoreCase) ? ParseNumber(x.DlTpt)
                    : metric.Equals("UL throughput", StringComparison.OrdinalIgnoreCase) ? ParseNumber(x.UlTpt)
                    : metric.Equals("LTE BLER", StringComparison.OrdinalIgnoreCase) ? ParseNumber(x.Bler)
                    : metric.Equals("PUSCH TX", StringComparison.OrdinalIgnoreCase) ? ParseNumber(x.PuschTx)
                    : metric.Equals("VoLTE Call", StringComparison.OrdinalIgnoreCase) ? (double?)x.VolteCall
                    : metric == "RSRP" ? ToNullableDouble(x.Rsrp)
                    : metric == "RSRQ" ? ToNullableDouble(x.Rsrq)
                    : metric == "SINR" ? ToNullableDouble(x.Sinr)
                    : null;

                return value.HasValue && (poorIsHigher ? value.Value > poorLimit : value.Value < poorLimit);
            });

            var poorPercent = report.TotalRows == 0 ? 0 : poorCount * 100.0 / report.TotalRows;
            var performance = poorPercent >= 60 ? "poor" : poorPercent >= 25 ? "moderate" : "strong";
            
            var narrative = $"{metric} ({description}) is a key indicator for network performance. The measured values show an average of {stats.Average:0.##} {unit}, ranging from {stats.Min:0.##} to {stats.Max:0.##} {unit}.";
            
            if (poorLimit != 0 || !string.IsNullOrEmpty(poorText))
            {
                narrative += $" The network demonstrates {performance} {metric} performance with {poorCount:N0} samples ({poorPercent:0.##}%) {poorText}.";
            }
            
            return narrative.Replace("  ", " ").Trim();
        }

        private static string BuildEarfcnNarrative(UnifiedMapReport report, string description)
        {
            var values = UnifiedMapReportEarfcnHelper.DistinctValues(report.Rows);
            var sampleCount = UnifiedMapReportEarfcnHelper.CountSamples(report.Rows);

            if (values.Count == 0)
                return $"EARFCN ({description}) was analyzed across the drive route, but no valid EARFCN values were available.";

            return $"EARFCN ({description}) identifies the serving channel number. Observed EARFCN values: {UnifiedMapReportEarfcnHelper.FormatValues(values)}. Valid EARFCN samples: {sampleCount:N0}.";
        }

        private static string BuildCoordinateSummary(UnifiedMapReport report)
        {
            var points = report.Rows
                .Where(x => x.Lat.HasValue && x.Lon.HasValue && x.Lat.Value is >= -90 and <= 90 && x.Lon.Value is >= -180 and <= 180)
                .Select(x => new { Lat = x.Lat!.Value, Lon = x.Lon!.Value })
                .ToList();

            if (points.Count == 0)
                return "No valid GPS coordinates were available in the selected drive samples.";

            return $"Valid GPS samples span approximately {points.Min(x => x.Lat):0.000000} to {points.Max(x => x.Lat):0.000000} latitude and {points.Min(x => x.Lon):0.000000} to {points.Max(x => x.Lon):0.000000} longitude.";
        }

        private static void AddGpsRange(List<string> lines, UnifiedMapReport report, ref double y)
        {
            var points = report.Rows
                .Where(x => x.Lat.HasValue && x.Lon.HasValue && x.Lat.Value is >= -90 and <= 90 && x.Lon.Value is >= -180 and <= 180)
                .ToList();
            if (points.Count == 0) return;

            y -= 14;
            var gpsPoints = points.Select(x => ((double)x.Lat!.Value, (double)x.Lon!.Value)).ToList();
            var detectedArea = ResolveGpsArea(gpsPoints);
            if (!string.IsNullOrWhiteSpace(detectedArea))
            {
                AddWrapped(lines, Margin, ref y, $"Detected drive area: {detectedArea}", 10, 92);
                y -= 4;
            }

            DrawTable(lines, Margin, ref y, new[] { "GPS Summary", "Value" }, new List<List<string>>
            {
                new() { "Valid GPS samples", points.Count.ToString("N0", CultureInfo.InvariantCulture) },
                new() { "Latitude range", $"{points.Min(x => x.Lat):0.000000} to {points.Max(x => x.Lat):0.000000}" },
                new() { "Longitude range", $"{points.Min(x => x.Lon):0.000000} to {points.Max(x => x.Lon):0.000000}" }
            }, 10);

            DrawGpsPreviewImages(lines, report, ref y);
        }

        private static string ResolveGpsArea(List<(double Lat, double Lon)> points)
        {
            if (points.Count == 0) return "";

            var centerLat = points.Average(x => x.Lat);
            var centerLon = points.Average(x => x.Lon);
            var fallback = $"route center {centerLat:0.000000}, {centerLon:0.000000}";

            try
            {
                var lat = centerLat.ToString("0.000000", CultureInfo.InvariantCulture);
                var lon = centerLon.ToString("0.000000", CultureInfo.InvariantCulture);
                var url = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={lat}&lon={lon}&zoom=14&addressdetails=1";

                using var response = MapHttpClient.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return fallback;

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("address", out var address))
                {
                    var parts = new List<string>();
                    AddAddressPart(address, parts, "neighbourhood");
                    AddAddressPart(address, parts, "suburb");
                    AddAddressPart(address, parts, "city_district");
                    AddAddressPart(address, parts, "city");
                    AddAddressPart(address, parts, "town");
                    AddAddressPart(address, parts, "village");
                    AddAddressPart(address, parts, "county");
                    AddAddressPart(address, parts, "state");
                    AddAddressPart(address, parts, "country");

                    if (parts.Count > 0)
                        return string.Join(", ", parts.Take(4));
                }

                if (root.TryGetProperty("display_name", out var displayName) && displayName.ValueKind == JsonValueKind.String)
                {
                    var parts = (displayName.GetString() ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Take(4)
                        .ToList();

                    if (parts.Count > 0)
                        return string.Join(", ", parts);
                }
            }
            catch
            {
                return fallback;
            }

            return fallback;
        }

        private static void AddAddressPart(JsonElement address, List<string> parts, string key)
        {
            if (!address.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                return;

            var text = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            if (!parts.Any(x => x.Equals(text, StringComparison.OrdinalIgnoreCase)))
                parts.Add(text);
        }

        private static void DrawGpsPreviewImages(List<string> lines, UnifiedMapReport report, ref double y)
        {
            var hasOpenStreet = report.MapImages.TryGetValue(GpsOpenStreetImageKey, out var openStreet);
            var hasSatellite = report.MapImages.TryGetValue(GpsSatelliteImageKey, out var satellite);
            if (!hasOpenStreet && !hasSatellite) return;

            y -= 18;
            lines.Add(FillColor(15, 23, 42));
            lines.Add(Text(Margin, y, 11, "GPS Map Preview"));
            y -= 16;

            var gap = 16.0;
            var previewWidth = (PageWidth - (Margin * 2) - gap) / 2;
            var previewHeight = 122.0;
            var imageY = y - previewHeight;

            if (hasOpenStreet && openStreet != null)
            {
                DrawImage(lines, GpsOpenStreetImageKey, openStreet, Margin, imageY, previewWidth, previewHeight);
                lines.Add(FillColor(30, 41, 59));
                lines.Add(Text(Margin, imageY - 13, 8.5, "OpenStreetMap view"));
            }

            if (hasSatellite && satellite != null)
            {
                var x = Margin + previewWidth + gap;
                DrawImage(lines, GpsSatelliteImageKey, satellite, x, imageY, previewWidth, previewHeight);
                lines.Add(FillColor(30, 41, 59));
                lines.Add(Text(x, imageY - 13, 8.5, "Satellite view (Esri imagery)"));
            }

            y = imageY - 28;
        }

        private static void DrawImage(List<string> lines, string imageKey, ReportLogo image, double x, double y, double maxWidth, double maxHeight)
        {
            DrawImageFit(lines, $"Img_{imageKey}", image, x, y, maxWidth, maxHeight);
        }

        private static void DrawImageFit(List<string> lines, string resourceName, ReportLogo image, double x, double y, double maxWidth, double maxHeight)
        {
            var width = maxWidth;
            var height = width * image.Height / Math.Max(image.Width, 1);

            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * image.Width / Math.Max(image.Height, 1);
            }

            var drawX = x + ((maxWidth - width) / 2);
            var drawY = y + ((maxHeight - height) / 2);
            lines.Add($"q {Fmt(width)} 0 0 {Fmt(height)} {Fmt(drawX)} {Fmt(drawY)} cm /{resourceName} Do Q");
        }

        private static void DrawBarChart(List<string> lines, BarChartData chart, double x, double y, double width, double height)
        {
            var items = chart.Items.Where(item => item.Value > 0).Take(8).ToList();
            if (items.Count == 0) return;

            var max = items.Max(item => item.Value);
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
            var rightAlignedColumns = GetRightAlignedColumns(headers, rows);
            lines.Add(FillColor(226, 232, 240));
            lines.Add(Rect(x, y - 6, tableWidth, rowHeight, true));
            lines.Add(FillColor(15, 23, 42));
            for (var i = 0; i < headers.Count; i++)
                lines.Add(TableText(x + (i * colWidth), colWidth, y, size, Truncate(headers[i], 18), rightAlignedColumns.Contains(i)));

            y -= rowHeight;
            foreach (var row in rows)
            {
                lines.Add(StrokeColor(226, 232, 240));
                lines.Add($"{Fmt(x)} {Fmt(y - 7)} m {Fmt(x + tableWidth)} {Fmt(y - 7)} l S");
                lines.Add(FillColor(30, 41, 59));
                for (var i = 0; i < headers.Count && i < row.Count; i++)
                    lines.Add(TableText(x + (i * colWidth), colWidth, y, size - 1, Truncate(row[i], 22), rightAlignedColumns.Contains(i)));
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
                ("Jitter", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Jitter))), "ms"),
                ("LTE BLER", MetricStats(report.Rows.Select(x => ParseNumber(x.Bler))), "%"),
                ("VoLTE Call", MetricStats(report.Rows.Select(x => (double?)x.VolteCall)), ""), // Reverted
                ("PUSCH TX", MetricStats(report.Rows.Select(x => ParseNumber(x.PuschTx))), "dBm")
            };

            var rows = metrics
                .Where(x => x.Stats.Count > 0)
                .Select(x => new List<string>
                {
                    x.Name,
                    FormatStat(x.Stats.Average, x.Stats.Count, x.Unit),
                    FormatStat(x.Stats.Min, x.Stats.Count, x.Unit),
                    FormatStat(x.Stats.Max, x.Stats.Count, x.Unit),
                    x.Stats.Count.ToString("N0", CultureInfo.InvariantCulture)
                })
                .ToList();

            var earfcnValues = UnifiedMapReportEarfcnHelper.DistinctValues(report.Rows);
            if (earfcnValues.Count > 0)
                rows.Add(new List<string> { "EARFCN", "N/A", UnifiedMapReportEarfcnHelper.FormatValues(earfcnValues), "N/A", UnifiedMapReportEarfcnHelper.CountSamples(report.Rows).ToString("N0", CultureInfo.InvariantCulture) });

            return rows;
        }

        private static List<List<string>> BuildQualityRows(UnifiedMapReport report)
        {
            var metrics = new List<(string Metric, MetricSummary Stats, string Unit, string Threshold, int PoorCount)>
            {
                ("RSRP", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrp))), "dBm", $"< {PoorRsrpLimit:0.#} dBm", report.Rows.Count(x => x.Rsrp.HasValue && x.Rsrp.Value < PoorRsrpLimit)),
                ("RSRQ", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Rsrq))), "dB", $"< {PoorRsrqLimit:0.#} dB", report.Rows.Count(x => x.Rsrq.HasValue && x.Rsrq.Value < PoorRsrqLimit)),
                ("SINR", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Sinr))), "dB", "< 5 dB", report.Rows.Count(x => x.Sinr.HasValue && x.Sinr.Value < 5)),
                ("MOS", MetricStats(report.Rows.Select(x => ToNullableDouble(x.Mos))), "", "< 3", report.Rows.Count(x => x.Mos.HasValue && x.Mos.Value < 3)),
                ("LTE BLER", MetricStats(report.Rows.Select(x => ParseNumber(x.Bler))), "%", "> 10%", report.Rows.Count(x => ParseNumber(x.Bler) > 10)),
                ("VoLTE Call", MetricStats(report.Rows.Select(x => (double?)x.VolteCall)), "", "< 1", report.Rows.Count(x => x.VolteCall.HasValue && x.VolteCall.Value < 1)),
                ("PUSCH TX", MetricStats(report.Rows.Select(x => ParseNumber(x.PuschTx))), "dBm", "> 10 dBm", report.Rows.Count(x => ParseNumber(x.PuschTx) > 10))
            };

            return metrics
                .Where(x => x.Stats.Count > 0)
                .Select(x => QualityRow(x.Metric, x.Stats, x.Unit, x.Threshold, x.PoorCount))
                .ToList();
        }

        private static List<string> QualityRow(string metric, MetricSummary stats, string unit, string threshold, int poorCount)
        {
            return new List<string> { metric, FormatStat(stats.Average, stats.Count, unit), threshold, poorCount.ToString("N0", CultureInfo.InvariantCulture) };
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

            int nextObjId = 4;
            var imageIds = new Dictionary<string, int>();
            var companyLogo = report.CompanyLogo ?? report.Logo;
            var productLogo = report.ProductLogo ?? report.Logo;
            
            if (companyLogo != null) imageIds["CompanyLogo"] = nextObjId++;
            if (productLogo != null) imageIds["ProductLogo"] = nextObjId++;
            foreach (var key in report.MapImages.Keys) imageIds[$"Img_{key}"] = nextObjId++;

            var firstPageObjectId = nextObjId;
            var objectCount = 3 + imageIds.Count + (pageContents.Count * 2);
            var offsets = new long[objectCount + 1];
            
            var pageIds = Enumerable.Range(0, pageContents.Count)
                .Select(i => firstPageObjectId + (i * 2) + 1)
                .ToList();

            WriteObj(stream, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
            WriteObj(stream, offsets, 2, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageContents.Count} >>");
            WriteObj(stream, offsets, 3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

            var xObjBuilder = new StringBuilder();
            if (imageIds.Count > 0)
            {
                xObjBuilder.Append(" /XObject << ");
                foreach(var kvp in imageIds)
                {
                    xObjBuilder.Append($"/{kvp.Key} {kvp.Value} 0 R ");
                }
                xObjBuilder.Append(">> ");
            }
            var xObjectResources = xObjBuilder.ToString();

            if (companyLogo != null)
            {
                WriteStreamObj(stream, offsets, imageIds["CompanyLogo"], 
                    $"<< /Type /XObject /Subtype /Image /Width {companyLogo.Width} /Height {companyLogo.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {companyLogo.Bytes.Length} >>", 
                    companyLogo.Bytes);
            }

            if (productLogo != null)
            {
                WriteStreamObj(stream, offsets, imageIds["ProductLogo"], 
                    $"<< /Type /XObject /Subtype /Image /Width {productLogo.Width} /Height {productLogo.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {productLogo.Bytes.Length} >>", 
                    productLogo.Bytes);
            }

            foreach (var kvp in report.MapImages)
            {
                var imgId = imageIds[$"Img_{kvp.Key}"];
                WriteStreamObj(stream, offsets, imgId, 
                    $"<< /Type /XObject /Subtype /Image /Width {kvp.Value.Width} /Height {kvp.Value.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {kvp.Value.Bytes.Length} >>", 
                    kvp.Value.Bytes);
            }

            for (var i = 0; i < pageContents.Count; i++)
            {
                var contentId = firstPageObjectId + (i * 2);
                var pageId = contentId + 1;
                var content = pageContents[i];
                
                WriteStreamObj(stream, offsets, contentId, $"<< /Length {content.Length} >>", content);
                WriteObj(stream, offsets, pageId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(PageWidth)} {Fmt(PageHeight)}] /Resources << /Font << /F1 3 0 R >>{xObjectResources}>> /Contents {contentId} 0 R >>");
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

        private static string TextCenter(double centerX, double y, double size, string value)
        {
            return Text(centerX - (EstimateTextWidth(value, size) / 2), y, size, value);
        }

        private static string TextRight(double rightX, double y, double size, string value)
        {
            return Text(rightX - EstimateTextWidth(value, size), y, size, value);
        }

        private static string TableText(double cellX, double cellWidth, double y, double size, string value, bool rightAlign)
        {
            const double cellPadding = 5;
            return rightAlign
                ? TextRight(cellX + cellWidth - cellPadding, y, size, value)
                : Text(cellX + cellPadding, y, size, value);
        }

        private static HashSet<int> GetRightAlignedColumns(IReadOnlyList<string> headers, IReadOnlyList<List<string>> rows)
        {
            var result = new HashSet<int>();
            for (var i = 1; i < headers.Count; i++)
            {
                var header = headers[i];
                var hasNumericHeader = Regex.IsMatch(header, @"average|minimum|maximum|samples|share|threshold|poor|slow", RegexOptions.IgnoreCase);
                var hasNumericValues = rows.Any(row => row.Count > i && LooksNumeric(row[i]));
                if (hasNumericHeader || hasNumericValues)
                    result.Add(i);
            }

            return result;
        }

        private static bool LooksNumeric(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var text = value.Trim()
                .Replace(",", "", StringComparison.Ordinal)
                .Replace("%", "", StringComparison.Ordinal);

            if (text.StartsWith("< ", StringComparison.Ordinal) || text.StartsWith("> ", StringComparison.Ordinal))
                text = text[2..].Trim();

            var firstPart = Regex.Split(text, @"\s+").FirstOrDefault() ?? "";
            return double.TryParse(firstPart, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }

        private static double EstimateTextWidth(string? value, double size)
        {
            if (string.IsNullOrEmpty(value)) return 0;

            var width = 0.0;
            foreach (var ch in value)
            {
                width += ch switch
                {
                    ' ' => 0.28,
                    'i' or 'l' or 'I' or '.' or ',' or ':' or ';' or '\'' => 0.25,
                    'm' or 'w' or 'M' or 'W' => 0.82,
                    >= '0' and <= '9' => 0.56,
                    >= 'A' and <= 'Z' => 0.64,
                    _ => 0.52
                };
            }

            return width * size;
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
