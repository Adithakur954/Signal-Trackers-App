using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SignalTracker.Models;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ExcelReportController : ControllerBase
    {
        private const string ImageBaseUrl = "https://apistracer.vinfocom.co.in/uploaded_images";
        private static readonly string[] ImageHeaders =
        {
            "BAND", "RSRP", "RSRQ", "SINR", "DL_THPT", "UL_THPT",
            "EARFCN", "LTE_BLER", "PCI", "NODEB_ID", "VOLTE_CALL", "PUSCH_TX"
        };

        private readonly ApplicationDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;

        public ExcelReportController(ApplicationDbContext db, IHttpClientFactory httpClientFactory)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
        }

        [HttpGet("Generate")]
        public Task<IActionResult> GenerateFromQuery(
            [FromQuery] int projectId,
            [FromQuery] string? sessionIds = null,
            [FromQuery] string? provider = null,
            [FromQuery] string? networkType = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] int? limit = null)
        {
            var request = new WalkTestExcelReportRequest
            {
                ProjectId = projectId,
                SessionIds = ParseSessionIds(sessionIds),
                Provider = provider,
                NetworkType = networkType,
                StartDate = startDate,
                EndDate = endDate,
                Limit = limit
            };

            return Generate(request);
        }

        [HttpPost("Generate")]
        public async Task<IActionResult> Generate([FromBody] WalkTestExcelReportRequest request)
        {
            if (request == null)
                return BadRequest(new { Message = "Report request is required." });

            if (request.ProjectId <= 0)
                return BadRequest(new { Message = "ProjectId is required." });

            var project = await _db.tbl_project
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.id == request.ProjectId, HttpContext.RequestAborted);

            if (project == null)
                return BadRequest(new { Message = "Project not found." });

            var sessionIds = ResolveSessionIds(request.SessionIds, project.ref_session_id);
            if (sessionIds.Count == 0)
                return BadRequest(new { Message = "No valid session IDs are available for this report." });

            var rows = await QueryWalkTestRowsAsync(request, sessionIds);
            if (rows.Count == 0)
                return BadRequest(new { Message = "No network logs found for the selected sessions." });

            var siteRows = await QuerySiteSummaryRowsAsync(request.ProjectId);

            // Fetch session notes threshold config (exclusively from tbl_session.notes, no db.thresholds fallback)
            var thresholds = await GetSessionNotesThresholdConfigAsync(sessionIds, HttpContext.RequestAborted);

            // Download the chart/report images once so they can be embedded directly in the workbook.
            var imageBytesByUrl = await FetchReportImagesAsync(sessionIds, HttpContext.RequestAborted);

            var workbook = BuildWorkbook(
                project.project_name ?? $"Project {project.id}",
                sessionIds,
                rows,
                siteRows,
                imageBytesByUrl,
                thresholds);

            var bytes = SimpleXlsxWriter.Write(workbook);
            var filename = $"Walk_Test_Report_{request.ProjectId}_{DateTime.Now:yyyy-MM-dd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }

        // POST api/ExcelReport/GenerateFromZip
        // multipart/form-data:
        //   LogZip          -> the .zip file (required)
        //   ProjectName     -> optional (defaults to zip file name)
        //   SessionIdOverride -> optional, forces the session id used for image lookup
        //   BandFilter/Bands -> optional, one or more selected bands; ALL/empty = no filter
        [HttpPost("GenerateFromZip")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(200_000_000)]
        public async Task<IActionResult> GenerateFromZip([FromForm] ZipReportUploadRequest request)
        {
            if (request.LogZip == null || request.LogZip.Length == 0)
                return BadRequest(new { Message = "A log zip file is required." });

            using var zipStream = new MemoryStream();
            await request.LogZip.CopyToAsync(zipStream, HttpContext.RequestAborted);
            zipStream.Position = 0;

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

            var mapImages = ExtractMapImagesFromZip(archive, out var detectedSessionId);
            var sessionId = (int)(request.SessionIdOverride ?? detectedSessionId ?? 0);

            var rawRows = ExtractNetworkRowsFromZip(archive, sessionId);
            var rows = CleanZipRows(rawRows);
            if (rows.Count == 0)
                return BadRequest(new { Message = "No usable network log rows were found inside the zip." });

            var bandsPresent = rows
                .Select(r => r.BandSheetName)
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Response.Headers["X-Available-Bands"] = string.Join(",", bandsPresent);

            var selectedBands = ResolveSelectedBands(request, Request.HasFormContentType ? Request.Form : null);
            if (selectedBands.Count > 0)
            {
                rows = FilterZipRowsByBands(rows, selectedBands);

                if (rows.Count == 0)
                    return BadRequest(new
                    {
                        Message = $"No samples found for band(s): {string.Join(", ", selectedBands)}. Check the band value (e.g. B3, B8, B40, n78).",
                        AvailableBands = bandsPresent
                    });
            }

            var thresholds = ExtractThresholdConfigFromZip(archive);
            var siteRows = ExtractSiteSummaryRowsFromZip(archive);

            var sessionIds = sessionId > 0 ? new List<int> { sessionId } : new List<int>();

            var projectName = string.IsNullOrWhiteSpace(request.ProjectName)
                ? Path.GetFileNameWithoutExtension(request.LogZip.FileName)
                : request.ProjectName;

            if (selectedBands.Count > 0)
            {
                projectName += $" ({(selectedBands.Count == 1 ? "Band" : "Bands")}: {string.Join(", ", selectedBands)})";
            }

            var imageBytesByUrl = BuildImageBytesByUrlFromZip(mapImages, sessionIds);

            var workbook = BuildWorkbook(
                projectName,
                sessionIds,
                rows,
                siteRows,
                imageBytesByUrl,
                thresholds);

            var bytes = SimpleXlsxWriter.Write(workbook);
            var filename = $"Walk_Test_Report_Zip_{sessionId}_{DateTime.Now:yyyy-MM-dd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }

        // POST api/ExcelReport/DiscoverBands
        [HttpPost("DiscoverBands")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(200_000_000)]
        public async Task<IActionResult> DiscoverBands([FromForm] ZipBandDiscoveryRequest request)
        {
            if (request.LogZip == null || request.LogZip.Length == 0)
                return BadRequest(new { Message = "A log zip file is required." });

            using var zipStream = new MemoryStream();
            await request.LogZip.CopyToAsync(zipStream, HttpContext.RequestAborted);
            zipStream.Position = 0;

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

            ExtractMapImagesFromZip(archive, out var detectedSessionId);
            var sessionId = (int)(request.SessionIdOverride ?? detectedSessionId ?? 0);

            var rawRows = ExtractNetworkRowsFromZip(archive, sessionId);
            var rows = CleanZipRows(rawRows);

            var bandSummary = rows
                .GroupBy(r => r.BandSheetName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Band = g.Key,
                    Count = g.Count(),
                    Percentage = rows.Count == 0 ? 0 : Math.Round(g.Count() * 100.0 / rows.Count, 2)
                })
                .OrderByDescending(x => x.Count)
                .ToList();

            return Ok(new
            {
                SessionId = sessionId,
                TotalRows = rows.Count,
                AvailableBands = bandSummary
            });
        }

        private static IReadOnlyDictionary<string, byte[]?> BuildImageBytesByUrlFromZip(
            Dictionary<string, byte[]> mapImages,
            List<int> sessionIds)
        {
            var result = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in mapImages)
            {
                var header = kvp.Key.ToUpperInvariant();
                result[header] = kvp.Value;

                foreach (var sid in sessionIds)
                {
                    var url = BuildImageUrl(sid, header);
                    result[url] = kvp.Value;
                }

                var fallbackUrl = BuildImageUrl(0, header);
                result[fallbackUrl] = kvp.Value;
            }

            return result;
        }

        private Dictionary<string, byte[]> ExtractMapImagesFromZip(ZipArchive archive, out long? detectedSessionId)
        {
            var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var sessionCounts = new Dictionary<long, int>();

            foreach (var entry in archive.Entries)
            {
                var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                var fileName = Path.GetFileNameWithoutExtension(entry.FullName);
                if (fileName.StartsWith("legend_", StringComparison.OrdinalIgnoreCase)) continue;

                var match = Regex.Match(entry.FullName, @"(?:^|[\\/])(?:map_)?(\d+)_([A-Za-z0-9_]+)\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase);
                string header;
                if (match.Success)
                {
                    if (long.TryParse(match.Groups[1].Value, out var sid))
                        sessionCounts[sid] = sessionCounts.TryGetValue(sid, out var c) ? c + 1 : 1;

                    header = match.Groups[2].Value.ToUpperInvariant();
                }
                else
                {
                    header = fileName.Replace("map_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
                }

                try
                {
                    using var entryStream = entry.Open();
                    using var ms = new MemoryStream();
                    entryStream.CopyTo(ms);
                    images[header] = ms.ToArray();
                }
                catch { }
            }

            detectedSessionId = sessionCounts.Count > 0
                ? sessionCounts.OrderByDescending(x => x.Value).First().Key
                : (long?)null;

            return images;
        }

        private List<WalkTestLogRow> ExtractNetworkRowsFromZip(ZipArchive archive, long sessionId)
        {
            var rows = new List<WalkTestLogRow>();
            var nextId = 1;

            var csvEntries = archive.Entries
                .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .Where(e => !Path.GetFileName(e.FullName).StartsWith("ColorSettings", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(e.FullName).StartsWith("SiteSummary", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(e.FullName).StartsWith("sites", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var entry in csvEntries)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                if (lines.Count < 2) continue;

                var headers = ParseCsvLine(lines[0]).Select(h => h.Trim()).ToList();
                var map = BuildZipColumnMap(headers);

                for (var i = 1; i < lines.Count; i++)
                {
                    var cols = ParseCsvLine(lines[i]);
                    if (cols.Count < 2) continue;

                    var row = ParseZipRow(cols, map, sessionId, ref nextId);
                    if (row != null) rows.Add(row);
                }
            }

            return rows;
        }

        private sealed class ZipColumnMap
        {
            public int Timestamp = -1, Lat = -1, Lon = -1, Network = -1, IndoorOutdoor = -1,
                Mos = -1, CellId = -1, Pci = -1,
                Rsrp = -1, Rsrq = -1, Sinr = -1, DlTpt = -1, UlTpt = -1, Earfcn = -1,
                VolteCall = -1, Band = -1, Bler = -1, AlphaLong = -1, AlphaShort = -1,
                NodebId = -1, Apps = -1, PuschTx = -1, Ta = -1, Cqi = -1, Level = -1, Primary = -1,
                PrimaryCellInfo = -1;
        }

        private static ZipColumnMap BuildZipColumnMap(List<string> headers)
        {
            return new ZipColumnMap
            {
                Timestamp = FindZipColumn(headers, "timestamp"),
                Lat = FindZipColumn(headers, "latitude"),
                Lon = FindZipColumn(headers, "longitude"),
                Network = FindZipColumn(headers, "network type"),
                IndoorOutdoor = FindZipColumn(headers, "indoor/outdoor"),
                Mos = FindZipColumn(headers, "mos"),
                CellId = FindZipColumn(headers, "cell id"),
                Pci = FindZipColumn(headers, "pci / psc"),
                Rsrp = FindZipColumn(headers, "ssrsrp", "rsrp"),
                Rsrq = FindZipColumn(headers, "ssrsrq", "rsrq"),
                Sinr = FindZipColumn(headers, "rxqual", "sinr"),
                DlTpt = FindZipColumn(headers, "dl thpt", "dl_tpt"),
                UlTpt = FindZipColumn(headers, "ul thpt", "ul_tpt"),
                Earfcn = FindZipColumn(headers, "earfcn"),
                VolteCall = FindZipColumn(headers, "volte call", "volte_call"),
                Band = FindZipColumn(headers, "band"),
                Bler = FindZipColumn(headers, "bler"),
                AlphaLong = FindZipColumn(headers, "alpha long", "m_alpha_long"),
                AlphaShort = FindZipColumn(headers, "alpha short", "m_alpha_short"),
                NodebId = FindZipColumn(headers, "nodeb id", "nodeb_id"),
                Apps = FindZipColumn(headers, "running apps", "apps"),
                PuschTx = FindZipColumn(headers, "pusch tx", "pusch_tx", "pusch"),
                Ta = FindZipColumn(headers, "ta"),
                Cqi = FindZipColumn(headers, "cqi"),
                Level = FindZipColumn(headers, "level"),
                Primary = FindZipColumnByName(headers, "primary"),
                PrimaryCellInfo = FindZipColumnByName(headers, "cellinfo_1", "primary_cell_info_1")
            };
        }

        private static int FindZipColumnByName(List<string> headers, params string[] candidates)
        {
            var candidateNames = candidates
                .Select(NormalizeZipColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < headers.Count; i++)
            {
                if (candidateNames.Contains(NormalizeZipColumnName(headers[i])))
                    return i;
            }

            return -1;
        }

        private static int FindZipColumn(List<string> headers, params string[] candidates)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                var h = headers[i].ToLowerInvariant();
                foreach (var c in candidates)
                {
                    if (h.Contains(c.ToLowerInvariant())) return i;
                }
            }
            return -1;
        }

        private static string NormalizeZipColumnName(string value) =>
            Regex.Replace(value.Trim(), @"[^a-z0-9]+", "", RegexOptions.IgnoreCase);

        private WalkTestLogRow? ParseZipRow(List<string> cols, ZipColumnMap map, long sessionId, ref int nextId)
        {
            var tsRaw = GetZipCol(cols, map.Timestamp);
            if (!DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                return null;

            if (!IsZipPrimaryRegisteredRow(cols, map))
                return null;

            var provider = GetZipCol(cols, map.AlphaShort);
            if (string.IsNullOrWhiteSpace(provider)) provider = GetZipCol(cols, map.AlphaLong);

            var band = GetZipCol(cols, map.Band);
            var network = GetZipCol(cols, map.Network);

            return new WalkTestLogRow
            {
                Id = nextId++,
                SessionId = (int)sessionId,
                Timestamp = ts,
                Lat = ParseFloatSafe(GetZipCol(cols, map.Lat)),
                Lon = ParseFloatSafe(GetZipCol(cols, map.Lon)),
                Network = network,
                Provider = CleanZipProvider(provider),
                Band = band,
                BandSheetName = ToBandSheetName(band, network),
                Pci = GetZipCol(cols, map.Pci),
                Rsrp = ClampKpiFloat(ParseFloatSafe(GetZipCol(cols, map.Rsrp)), -140, -44),
                Rsrq = ClampKpiFloat(ParseFloatSafe(GetZipCol(cols, map.Rsrq)), -34, 3),
                Sinr = ClampKpiFloat(ParseFloatSafe(GetZipCol(cols, map.Sinr)), -23, 40),
                Mos = ParseFloatSafe(GetZipCol(cols, map.Mos)),
                Earfcn = GetZipCol(cols, map.Earfcn),
                Bler = GetZipCol(cols, map.Bler),
                VolteCall = GetZipCol(cols, map.VolteCall),
                DlTpt = GetZipCol(cols, map.DlTpt),
                UlTpt = GetZipCol(cols, map.UlTpt),
                NodeBId = GetZipCol(cols, map.NodebId),
                Apps = GetZipCol(cols, map.Apps),
                IndoorOutdoor = GetZipCol(cols, map.IndoorOutdoor),
                CellId = GetZipCol(cols, map.CellId),
                Ta = map.PuschTx >= 0 ? GetZipCol(cols, map.PuschTx) : GetZipCol(cols, map.Ta),
                Cqi = ParseFloatSafe(GetZipCol(cols, map.Cqi)),
                Level = ParseIntSafe(GetZipCol(cols, map.Level)),
                Primary = GetZipCol(cols, map.Primary)
            };
        }

        private static bool IsZipPrimaryRegisteredRow(List<string> cols, ZipColumnMap map)
        {
            var primary = GetZipCol(cols, map.Primary);
            if (!string.IsNullOrWhiteSpace(primary) && !primary.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                return false;

            var primaryCellInfo = GetZipCol(cols, map.PrimaryCellInfo);
            if (!string.IsNullOrWhiteSpace(primaryCellInfo))
                return primaryCellInfo.Contains("mRegistered=YES", StringComparison.OrdinalIgnoreCase);

            return true;
        }

        private static List<WalkTestLogRow> CleanZipRows(List<WalkTestLogRow> rows)
        {
            var cleaned = new List<WalkTestLogRow>(rows.Count);
            var seen = new HashSet<string>();

            foreach (var r in rows)
            {
                if (!r.Timestamp.HasValue) continue;

                r.Provider = NormalizeZipText(r.Provider);
                r.Band = NormalizeZipText(r.Band);
                r.Network = NormalizeZipText(r.Network);
                r.Pci = NormalizeZipText(r.Pci);
                r.NodeBId = NormalizeZipText(r.NodeBId);
                r.CellId = NormalizeZipText(r.CellId);
                r.IndoorOutdoor = NormalizeZipText(r.IndoorOutdoor);
                r.Apps = NormalizeZipText(r.Apps);
                r.Bler = NormalizeZipText(r.Bler);

                if (r.Lat is < -90 or > 90) r.Lat = null;
                if (r.Lon is < -180 or > 180) r.Lon = null;
                if (r.Lat == 0 && r.Lon == 0) { r.Lat = null; r.Lon = null; }

                var dedupeKey = string.Join('|',
                    r.SessionId, r.Timestamp?.Ticks, r.Pci, r.Rsrp, r.Rsrq, r.Band);
                if (!seen.Add(dedupeKey)) continue;

                cleaned.Add(r);
            }

            for (var i = 0; i < cleaned.Count; i++) cleaned[i].Id = i + 1;

            return cleaned;
        }

        private static string? NormalizeZipText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim().Trim('"').Trim('\'').Trim();
            if (v.Length == 0) return null;
            if (v.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;
            return v;
        }

        private static List<string> ResolveSelectedBands(ZipReportUploadRequest request, IFormCollection? form)
        {
            var values = new List<string>();
            AddRawBandValues(values, request.BandFilter);

            if (request.Bands != null)
            {
                foreach (var band in request.Bands)
                    AddRawBandValues(values, band);
            }

            if (form != null)
            {
                AddFormBandValues(values, form, "BandFilter");
                AddFormBandValues(values, form, "BandFilters");
                AddFormBandValues(values, form, "Bands");
            }

            return values
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Where(x => !x.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddFormBandValues(List<string> values, IFormCollection form, string key)
        {
            if (!form.TryGetValue(key, out var formValues)) return;

            foreach (var value in formValues)
                AddRawBandValues(values, value);
        }

        private static void AddRawBandValues(List<string> values, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            values.AddRange(raw
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0));
        }

        private static List<WalkTestLogRow> FilterZipRowsByBands(
            IEnumerable<WalkTestLogRow> rows,
            IReadOnlyCollection<string> selectedBands)
        {
            var wanted = selectedBands
                .Select(CanonicalZipBandKey)
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return rows
                .Where(row => wanted.Contains(CanonicalZipBandKey(row.BandSheetName)) || wanted.Contains(CanonicalZipBandKey(row.Band)))
                .ToList();
        }

        private static string CanonicalZipBandKey(string? value)
        {
            var text = NormalizeZipText(value);
            if (text == null) return "";

            var key = Regex.Replace(text, @"\s+", "", RegexOptions.CultureInvariant).ToUpperInvariant();
            if (key.StartsWith("BAND", StringComparison.Ordinal))
                key = "B" + key[4..];
            if (Regex.IsMatch(key, @"^\d{1,3}$"))
                key = "B" + key;
            return key;
        }

        private static ReportThresholdConfig ExtractThresholdConfigFromZip(ZipArchive archive)
        {
            var thresholds = ReportThresholdConfig.Hardcoded();
            var entry = archive.Entries
                .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(e => Path.GetFileName(e.FullName).StartsWith("ColorSettings", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(e.FullName).StartsWith("colorsetting", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(e.FullName).StartsWith("thresholds", StringComparison.OrdinalIgnoreCase));

            if (entry == null) return thresholds;

            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = ReportThresholdConfig.FromColorSettingsJson(text, $"Zip color settings ({entry.Name})");
                    if (cfg != null) return cfg;
                }

                var lines = text
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count < 2) return thresholds;

                var headers = ParseCsvLine(lines[0]);
                var metricIndex = FindHeaderIndex(headers, "Metric");
                var typeIndex = FindHeaderIndex(headers, "Type");
                var minIndex = FindHeaderIndex(headers, "Min");
                var maxIndex = FindHeaderIndex(headers, "Max");
                var valueIndex = FindHeaderIndex(headers, "Value");
                var colorIndex = FindHeaderIndex(headers, "Color");
                var labelIndex = FindHeaderIndex(headers, "Label");

                if (metricIndex < 0 || typeIndex < 0 || colorIndex < 0) return thresholds;

                var rangesByMetric = new Dictionary<string, List<ThresholdRange>>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in lines.Skip(1))
                {
                    var cols = ParseCsvLine(line);
                    var metric = GetZipCol(cols, metricIndex).Trim().ToUpperInvariant();
                    var type = GetZipCol(cols, typeIndex).Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(type)) continue;

                    var color = NormalizeColorHex(GetZipCol(cols, colorIndex));
                    var label = GetZipCol(cols, labelIndex);
                    ThresholdRange? range = null;

                    if (type == "RANGE")
                    {
                        var min = ParseDoubleSafe(GetZipCol(cols, minIndex));
                        var max = ParseDoubleSafe(GetZipCol(cols, maxIndex));
                        if (!min.HasValue || !max.HasValue) continue;
                        range = new ThresholdRange(label, min.Value, max.Value, color);
                    }
                    else if (type == "VALUE")
                    {
                        var value = GetZipCol(cols, valueIndex);
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        range = new ThresholdRange(label, 0, 0, color) { ValueMatch = value.Trim() };
                    }

                    if (range == null) continue;

                    if (!rangesByMetric.TryGetValue(metric, out var ranges))
                    {
                        ranges = new List<ThresholdRange>();
                        rangesByMetric[metric] = ranges;
                    }

                    ranges.Add(range);
                }

                if (rangesByMetric.Count == 0) return thresholds;

                ApplyZipMetricRanges(rangesByMetric, "RSRP", ranges => thresholds.Rsrp = ranges);
                ApplyZipMetricRanges(rangesByMetric, "RSRQ", ranges => thresholds.Rsrq = ranges);
                ApplyZipMetricRanges(rangesByMetric, "SINR", ranges => thresholds.Sinr = ranges);
                ApplyZipMetricRanges(rangesByMetric, "DL_THPT", ranges => thresholds.DlTpt = ranges);
                ApplyZipMetricRanges(rangesByMetric, "UL_THPT", ranges => thresholds.UlTpt = ranges);
                ApplyZipMetricRanges(rangesByMetric, "EARFCN", ranges => thresholds.Earfcn = ranges);
                ApplyZipMetricRanges(rangesByMetric, "BLER", ranges => thresholds.Bler = ranges);
                ApplyZipMetricRanges(rangesByMetric, "LTE_BLER", ranges => thresholds.Bler = ranges);
                ApplyZipMetricRanges(rangesByMetric, "VOLTE", ranges => thresholds.VolteCall = ranges);
                ApplyZipMetricRanges(rangesByMetric, "VOLTE_CALL", ranges => thresholds.VolteCall = ranges);
                ApplyZipMetricRanges(rangesByMetric, "PUSCH_TX", ranges => thresholds.PuschTx = ranges);

                thresholds.Source = $"Log zip color settings ({Path.GetFileName(entry.FullName)})";
            }
            catch
            {
                return thresholds;
            }

            return thresholds;
        }

        private static void ApplyZipMetricRanges(
            Dictionary<string, List<ThresholdRange>> rangesByMetric,
            string metric,
            Action<List<ThresholdRange>> apply)
        {
            if (rangesByMetric.TryGetValue(metric, out var ranges) && ranges.Count > 0)
                apply(ranges);
        }

        private static int FindHeaderIndex(List<string> headers, string name)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static List<WalkTestSiteSummaryRow> ExtractSiteSummaryRowsFromZip(ZipArchive archive)
        {
            var list = new List<WalkTestSiteSummaryRow>();
            var entry = archive.Entries
                .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(e => Path.GetFileName(e.FullName).StartsWith("SiteSummary", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(e.FullName).StartsWith("site_summary", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(e.FullName).StartsWith("sites", StringComparison.OrdinalIgnoreCase));

            if (entry == null) return list;

            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                if (lines.Count < 2) return list;

                var headers = ParseCsvLine(lines[0]).Select(h => h.Trim()).ToList();

                for (var i = 1; i < lines.Count; i++)
                {
                    var cols = ParseCsvLine(lines[i]);
                    if (cols.Count < 2) continue;

                    list.Add(new WalkTestSiteSummaryRow
                    {
                        Source = GetZipColByHeader(cols, headers, "Source", "Optimized"),
                        Site = ParseIntSafe(GetZipColByHeader(cols, headers, "Site")),
                        SiteName = ParseIntSafe(GetZipColByHeader(cols, headers, "Site Name", "SiteName")),
                        Sector = GetZipColByHeader(cols, headers, "Sector"),
                        CellId = ParseIntSafe(GetZipColByHeader(cols, headers, "Cell ID", "CellId")),
                        SecId = ParseIntSafe(GetZipColByHeader(cols, headers, "Sec ID", "SecId")),
                        Latitude = ParseDoubleSafe(GetZipColByHeader(cols, headers, "Latitude", "Lat")),
                        Longitude = ParseDoubleSafe(GetZipColByHeader(cols, headers, "Longitude", "Lon")),
                        Tac = ParseIntSafe(GetZipColByHeader(cols, headers, "TAC")),
                        Pci = ParseIntSafe(GetZipColByHeader(cols, headers, "PCI")),
                        Azimuth = ParseIntSafe(GetZipColByHeader(cols, headers, "Azimuth")),
                        Height = ParseIntSafe(GetZipColByHeader(cols, headers, "Height")),
                        Band = ParseIntSafe(GetZipColByHeader(cols, headers, "Band")),
                        Earfcn = ParseIntSafe(GetZipColByHeader(cols, headers, "EARFCN")),
                        Bw = ParseIntSafe(GetZipColByHeader(cols, headers, "BW")),
                        MTilt = ParseIntSafe(GetZipColByHeader(cols, headers, "M Tilt", "MTilt")),
                        ETilt = ParseIntSafe(GetZipColByHeader(cols, headers, "E Tilt", "ETilt")),
                        TxPower = ParseDoubleSafe(GetZipColByHeader(cols, headers, "Tx Power", "TxPower")),
                        ReferenceSignalPower = ParseDoubleSafe(GetZipColByHeader(cols, headers, "Reference Signal Power")),
                        Frequency = GetZipColByHeader(cols, headers, "Frequency"),
                        Cluster = GetZipColByHeader(cols, headers, "Cluster"),
                        Technology = GetZipColByHeader(cols, headers, "Technology")
                    });
                }
            }
            catch { }

            return list;
        }

        private static string GetZipColByHeader(List<string> cols, List<string> headers, params string[] names)
        {
            foreach (var name in names)
            {
                var idx = FindHeaderIndex(headers, name);
                if (idx >= 0 && idx < cols.Count)
                    return cols[idx].Trim();
            }
            return "";
        }

        private static string GetZipCol(List<string> cols, int idx) =>
            idx >= 0 && idx < cols.Count ? cols[idx].Trim() : "";

        private static string CleanZipProvider(string? value) =>
            (value ?? "").Trim().Trim('"').Trim('\'');

        private static float? ParseFloatSafe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"-?\d+(\.\d+)?");
            return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : (float?)null;
        }

        private static float? ClampKpiFloat(float? value, float min, float max)
        {
            if (!value.HasValue) return null;
            return Math.Min(Math.Max(value.Value, min), max);
        }

        private static string NormalizeColorHex(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return "#808080";

            var hex = color.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];
            hex = hex.TrimStart('#');

            if (hex.Length == 8)
                hex = hex[2..];

            return hex.Length == 6 ? $"#{hex}" : "#808080";
        }

        private static double? ParseDoubleSafe(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static int? ParseIntSafe(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"-?\d+");
            return m.Success && int.TryParse(m.Value, out var v) ? v : (int?)null;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else sb.Append(c);
                }
            }

            result.Add(sb.ToString());
            return result;
        }

        /// <summary>
        /// Downloads every distinct chart image (one per session x header) that the report can
        /// reference, so they can be embedded as real pictures instead of hyperlinked / _xlfn.IMAGE()
        /// formulas. A failed or missing image simply results in a null entry - callers fall back to
        /// a text placeholder rather than failing the whole report.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, byte[]?>> FetchReportImagesAsync(
            List<int> sessionIds,
            CancellationToken cancellationToken)
        {
            var urls = sessionIds
                .SelectMany(sessionId => ImageHeaders.SelectMany(header => new[]
                {
                    BuildImageUrl(sessionId, header),
                    BuildLegendImageUrl(sessionId, header),
                    BuildGlobalLegendImageUrl(header)
                }))
                .Distinct()
                .ToList();

            var result = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            var client = _httpClientFactory.CreateClient("WalkTestReportImages");
            client.Timeout = TimeSpan.FromSeconds(20);

            using var throttle = new SemaphoreSlim(8);

            var downloadTasks = urls.Select(async url =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                        result[url] = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    else
                        result[url] = null; // e.g. 404 - image not generated for this session/header yet
                }
                catch
                {
                    result[url] = null; // network/timeout failure - don't fail the whole report
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(downloadTasks);
            return result;
        }

        private async Task<List<WalkTestLogRow>> QueryWalkTestRowsAsync(
            WalkTestExcelReportRequest request,
            List<int> sessionIds)
        {
            var limit = request.Limit.HasValue
                ? Math.Clamp(request.Limit.Value, 1, 500_000)
                : 200_000;

            var query = _db.tbl_network_log
                .AsNoTracking()
                .Where(x =>
                    x.session_id.HasValue &&
                    sessionIds.Contains(x.session_id.Value) &&
                    x.timestamp != null);

            if (request.StartDate.HasValue)
                query = query.Where(x => x.timestamp >= request.StartDate.Value);

            if (request.EndDate.HasValue)
                query = query.Where(x => x.timestamp < request.EndDate.Value.AddDays(1));

            var provider = request.Provider?.Trim();
            if (!string.IsNullOrWhiteSpace(provider))
            {
                query = query.Where(x =>
                    (x.m_alpha_short != null && EF.Functions.Like(x.m_alpha_short, $"%{provider}%")) ||
                    (x.m_alpha_long != null && EF.Functions.Like(x.m_alpha_long, $"%{provider}%")));
            }

            var networkType = request.NetworkType?.Trim();
            if (!string.IsNullOrWhiteSpace(networkType) &&
                !networkType.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => x.network != null && EF.Functions.Like(x.network, $"%{networkType}%"));
            }

            var rows = await query
                .OrderBy(x => x.timestamp)
                .ThenBy(x => x.id)
                .Take(limit)
                .Select(x => new WalkTestLogRow
                {
                    Id = x.id,
                    SessionId = x.session_id ?? 0,
                    Timestamp = x.timestamp,
                    Lat = x.lat,
                    Lon = x.lon,
                    IndoorOutdoor = x.indoor_outdoor,
                    Network = x.network,
                    Provider = x.m_alpha_short ?? x.m_alpha_long,
                    Band = x.band,
                    Pci = x.pci,
                    Rsrp = x.rsrp,
                    Rsrq = x.rsrq,
                    Sinr = x.sinr,
                    DlTpt = x.dl_tpt,
                    UlTpt = x.ul_tpt,
                    Mos = x.mos,
                    Apps = x.apps ?? x.app_name,
                    NodeBId = x.nodeb_id,
                    CellId = x.cell_id,
                    Earfcn = x.earfcn,
                    Bler = x.bler,
                    VolteCall = x.volte_call,
                    Cqi = x.cqi,
                    Ta = x.ta ?? ExtractPuschTxFromPrimaryCellInfo(x.primary_cell_info_1),
                    Level = x.level,
                    Primary = x.primary
                })
                .ToListAsync(HttpContext.RequestAborted);

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Ta))
                    row.Ta = ExtractPuschTxFromPrimaryCellInfo(row.Primary);

                row.BandSheetName = ToBandSheetName(row.Band, row.Network);
            }

            return rows;
        }

        private static string? ExtractPuschTxFromPrimaryCellInfo(string? primaryCellInfo)
        {
            if (string.IsNullOrWhiteSpace(primaryCellInfo)) return null;

            var match = Regex.Match(primaryCellInfo, @"(?:mPuschTx|pusch_tx|mTxPower|txPower)\s*=\s*(-?\d+(\.\d+)?)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private async Task<List<WalkTestSiteSummaryRow>> QuerySiteSummaryRowsAsync(int projectId)
        {
            var optimizedRows = await _db.site_prediction_optimized
                .AsNoTracking()
                .Where(x => x.tbl_project_id == projectId)
                .OrderByDescending(x => x.updated_at ?? x.created_at)
                .ThenBy(x => x.id)
                .Select(x => new WalkTestSiteSummaryRow
                {
                    Source = "Optimized",
                    SourceId = x.site_prediction_id,
                    Version = x.version,
                    Status = x.status,
                    Site = x.site,
                    SiteName = x.site_name,
                    Sector = x.sector,
                    CellId = x.cell_id,
                    SecId = x.sec_id,
                    Latitude = x.latitude,
                    Longitude = x.longitude,
                    Tac = x.tac,
                    Pci = x.pci,
                    Azimuth = x.azimuth,
                    Height = x.height,
                    Band = x.band,
                    Earfcn = x.earfcn,
                    Bw = x.bw,
                    MTilt = x.m_tilt,
                    ETilt = x.e_tilt,
                    TxPower = x.maximum_transmission_power_of_resource,
                    ReferenceSignalPower = x.reference_signal_power,
                    Frequency = x.frequency,
                    Cluster = x.cluster,
                    Technology = x.technology
                })
                .ToListAsync(HttpContext.RequestAborted);

            var optimizedSourceIds = optimizedRows
                .Select(x => x.SourceId)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            var baseQuery = _db.site_prediction
                .AsNoTracking()
                .Where(x => x.tbl_project_id == projectId);

            if (optimizedSourceIds.Count > 0)
                baseQuery = baseQuery.Where(x => !optimizedSourceIds.Contains(x.id));

            var baseRows = await baseQuery
                .OrderBy(x => x.site)
                .ThenBy(x => x.sector)
                .ThenBy(x => x.cell_id)
                .Select(x => new WalkTestSiteSummaryRow
                {
                    Source = "Original",
                    SourceId = x.id,
                    Site = x.site,
                    SiteName = x.site_name,
                    Sector = x.sector,
                    CellId = x.cell_id,
                    SecId = x.sec_id,
                    Latitude = x.latitude,
                    Longitude = x.longitude,
                    Tac = x.tac,
                    Pci = x.pci,
                    Azimuth = x.azimuth,
                    Height = x.height,
                    Band = x.band,
                    Earfcn = x.earfcn,
                    Bw = x.bw,
                    MTilt = x.m_tilt,
                    ETilt = x.e_tilt,
                    TxPower = x.maximum_transmission_power_of_resource,
                    ReferenceSignalPower = x.reference_signal_power,
                    Frequency = x.frequency,
                    Cluster = x.cluster,
                    Technology = x.technology
                })
                .ToListAsync(HttpContext.RequestAborted);

            return optimizedRows
                .Concat(baseRows)
                .OrderBy(x => x.Site ?? int.MaxValue)
                .ThenBy(x => x.Sector)
                .ThenBy(x => x.CellId ?? int.MaxValue)
                .ToList();
        }

        private static XlsxWorkbook BuildWorkbook(
            string projectName,
            List<int> sessionIds,
            List<WalkTestLogRow> rows,
            List<WalkTestSiteSummaryRow> siteRows,
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            ReportThresholdConfig thresholds)
        {
            var workbook = new XlsxWorkbook();
            workbook.Sheets.Add(BuildSiteSummarySheet(projectName, sessionIds, rows, siteRows));

            var bandGroups = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.BandSheetName) ? "Unknown Band" : x.BandSheetName)
                .OrderBy(x => BandSortKey(x.Key), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in bandGroups)
                workbook.Sheets.Add(BuildBandSheet(group.Key, group.ToList(), rows, imageBytesByUrl, thresholds));

            return workbook;
        }

        private static XlsxSheet BuildSiteSummarySheet(
            string projectName,
            List<int> sessionIds,
            List<WalkTestLogRow> rows,
            List<WalkTestSiteSummaryRow> siteRows)
        {
            var sheet = new XlsxSheet("Site Summary")
            {
                ColumnWidths = new double[] { 14, 14, 12, 12, 12, 14, 14, 11, 11, 12, 12, 10, 10, 10, 12, 12, 12, 16, 18, 14, 14, 12 }
            };

            sheet.Rows.Add(XlsxRow.Title($"Walk Test Excel Report - {projectName}", 22));
            sheet.Rows.Add(XlsxRow.FromText("Generated On", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sheet.Rows.Add(XlsxRow.FromText("Total Log Samples", rows.Count.ToString("N0", CultureInfo.InvariantCulture)));
            sheet.Rows.Add(XlsxRow.FromText("Band Sheets", string.Join(", ", rows.Select(x => x.BandSheetName).Distinct().OrderBy(x => x))));
            sheet.Rows.Add(XlsxRow.Blank());

            sheet.Rows.Add(XlsxRow.Header(
                "Source", "Site", "Site Name", "Sector", "Cell ID", "Sec ID", "Latitude", "Longitude",
                "TAC", "PCI", "Band", "EARFCN", "Azimuth", "Height", "BW", "M Tilt", "E Tilt",
                "Tx Power", "Reference Signal Power", "Frequency", "Cluster", "Technology"));

            foreach (var row in siteRows)
            {
                sheet.Rows.Add(XlsxRow.Data(
                    row.Source,
                    FormatValue(row.Site),
                    FormatValue(row.SiteName),
                    row.Sector,
                    FormatValue(row.CellId),
                    FormatValue(row.SecId),
                    FormatValue(row.Latitude),
                    FormatValue(row.Longitude),
                    FormatValue(row.Tac),
                    FormatValue(row.Pci),
                    FormatBandValue(row.Band),
                    FormatValue(row.Earfcn),
                    FormatValue(row.Azimuth),
                    FormatValue(row.Height),
                    FormatValue(row.Bw),
                    FormatValue(row.MTilt),
                    FormatValue(row.ETilt),
                    FormatValue(row.TxPower),
                    FormatValue(row.ReferenceSignalPower),
                    row.Frequency,
                    row.Cluster,
                    row.Technology));
            }

            if (siteRows.Count == 0)
                sheet.Rows.Add(XlsxRow.Data("No site prediction rows found for this project."));

            return sheet;
        }

        /// <summary>
        /// Builds a band sheet placing images in a 2-column grid. Each image spans 6 Excel columns horizontally,
        /// separated by a 3-column gap between image columns and 3 blank rows vertically between image rows.
        /// </summary>
        private async Task<ReportThresholdConfig> GetSessionNotesThresholdConfigAsync(
            List<int> sessionIds,
            CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0)
                return ReportThresholdConfig.Hardcoded();

            var sessions = await _db.tbl_session
                .AsNoTracking()
                .Where(s => s.id.HasValue && sessionIds.Contains(s.id.Value))
                .Select(s => new { Id = s.id ?? 0, s.notes })
                .ToListAsync(cancellationToken);

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

            return ReportThresholdConfig.Hardcoded();
        }

        private static XlsxSheet BuildBandSheet(
            string bandName,
            List<WalkTestLogRow> bandRows,
            List<WalkTestLogRow> allRows,
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            ReportThresholdConfig thresholds)
        {
            // 15 columns total:
            // Cols 0..5 (A-F): 6 cells for Left Image
            // Cols 6..8 (G-I): 3 cells gap between image columns
            // Cols 9..14 (J-O): 6 cells for Right Image
            var columnWidths = new double[15];
            for (int c = 0; c < 15; c++)
            {
                columnWidths[c] = 13.5; // ~100px per cell
            }

            var sheet = new XlsxSheet(bandName)
            {
                ColumnWidths = columnWidths
            };

            const int colIndexLeft = 0;   // Column A (spans A-F: 6 cells)
            const int colIndexRight = 9;  // Column J (spans J-O: 6 cells)
            const int imageCellSpan = 6;  // Spans 6 cells horizontally

            // 6 columns * ~100px * 9525 EMU/px = 5,715,000 EMUs
            const int maxWidthEmu = 5715000; 

            var primarySessionId = bandRows.Select(x => x.SessionId).FirstOrDefault(x => x > 0);

            // Filter to only those headers that successfully returned an image
            var validImages = ImageHeaders
                .Select(header => new { Header = header, Url = BuildImageUrl(primarySessionId, header) })
                .Where(x => imageBytesByUrl.TryGetValue(x.Url, out var bytes) && bytes != null)
                .ToList();

            // Process images in chunks of 2 for side-by-side layout
            for (int i = 0; i < validImages.Count; i += 2)
            {
                var leftItem = validImages[i];
                var rightItem = (i + 1 < validImages.Count) ? validImages[i + 1] : null;

                // 1. Create Title Row for the plots
                var titleRow = new XlsxRow(22);
                
                // Left plot title at Col 0 (A)
                titleRow.Cells.Add(XlsxCell.Text($"{bandName} - {leftItem.Header} Plot", 4));
                for (int c = 1; c < 6; c++) // Empty cells B..F
                {
                    titleRow.Cells.Add(XlsxCell.Text(""));
                }

                // 3 Gap columns (G, H, I)
                for (int c = 6; c < 9; c++)
                {
                    titleRow.Cells.Add(XlsxCell.Text(""));
                }

                // Right plot title at Col 9 (J)
                if (rightItem != null)
                {
                    titleRow.Cells.Add(XlsxCell.Text($"{bandName} - {rightItem.Header} Plot", 4));
                    for (int c = 10; c < 15; c++) // Empty cells K..O
                    {
                        titleRow.Cells.Add(XlsxCell.Text(""));
                    }
                }
                
                sheet.Rows.Add(titleRow);

                // 2. Prepare Images and Calculate Row Height
                var leftBytes = imageBytesByUrl[leftItem.Url]!;
                var leftSize = ScaleToEmu(ReadPngSizePx(leftBytes), maxWidthEmu);
                double maxRowHeightPts = leftSize.HeightEmu / 12700.0; // Convert EMU to Points

                byte[]? rightBytes = null;
                (int WidthEmu, int HeightEmu) rightSize = (0, 0);

                if (rightItem != null)
                {
                    rightBytes = imageBytesByUrl[rightItem.Url]!;
                    rightSize = ScaleToEmu(ReadPngSizePx(rightBytes), maxWidthEmu);
                    double rightPts = rightSize.HeightEmu / 12700.0;
                    
                    if (rightPts > maxRowHeightPts)
                    {
                        maxRowHeightPts = rightPts; // Fit tallest image
                    }
                }

                if (maxRowHeightPts > 390.0)
                {
                    maxRowHeightPts = 390.0; // Cap to max row height limit
                }

                // 3. Add the Image Row
                var imageRowIndex0 = sheet.Rows.Count;
                sheet.Rows.Add(new XlsxRow(maxRowHeightPts)); // Tall row for charts

                // Embed Left Plot Image (spanning A-F: 6 cells) - NO OVERLAP
                sheet.Images.Add(new XlsxImage(imageRowIndex0, colIndexLeft, leftBytes, leftSize.WidthEmu, leftSize.HeightEmu, cellSpanCols: imageCellSpan));

                // Embed Right Plot Image (spanning J-O: 6 cells) - NO OVERLAP
                if (rightBytes != null)
                {
                    sheet.Images.Add(new XlsxImage(imageRowIndex0, colIndexRight, rightBytes, rightSize.WidthEmu, rightSize.HeightEmu, cellSpanCols: imageCellSpan));
                }

                // 4. Add "Legend" Section Header Row directly under each plot image
                var legendTitleRow = new XlsxRow(18);
                legendTitleRow.Cells.Add(XlsxCell.Text("Legend", 4));
                for (int c = 1; c < 6; c++) legendTitleRow.Cells.Add(XlsxCell.Text(""));
                for (int c = 6; c < 9; c++) legendTitleRow.Cells.Add(XlsxCell.Text(""));

                if (rightItem != null)
                {
                    legendTitleRow.Cells.Add(XlsxCell.Text("Legend", 4));
                    for (int c = 10; c < 15; c++) legendTitleRow.Cells.Add(XlsxCell.Text(""));
                }
                sheet.Rows.Add(legendTitleRow);

                // 5. Add Compact Legend Table directly under each plot image
                var leftStats = CalculateLegendStatistics(allRows, leftItem.Header, thresholds);
                var rightStats = rightItem != null ? CalculateLegendStatistics(allRows, rightItem.Header, thresholds) : null;
                bool isLeftEarfcn = string.Equals(leftItem.Header, "EARFCN", StringComparison.OrdinalIgnoreCase);
                bool isRightEarfcn = rightItem != null && string.Equals(rightItem.Header, "EARFCN", StringComparison.OrdinalIgnoreCase);

                int maxRows = Math.Max(leftStats.Count, rightStats?.Count ?? 0);
                for (int s = 0; s < maxRows; s++)
                {
                    var statRowIndex0 = sheet.Rows.Count;
                    var statRow = new XlsxRow(16);

                    // Left plot legend row: Color Swatch + Compact text "Range (Count : Percentage%)"
                    if (s < leftStats.Count)
                    {
                        var ls = leftStats[s];
                        var leftTextDisplay = isLeftEarfcn ? ls.Range.Display : ls.Range.RangeOnlyDisplay;
                        statRow.Cells.Add(XlsxCell.Text("", 3)); // Color swatch PNG image
                        statRow.Cells.Add(XlsxCell.Text($"{leftTextDisplay} ({ls.Count} : {ls.Percentage:0.00}%)", 3));
                        for (int c = 2; c < 6; c++) statRow.Cells.Add(XlsxCell.Text(""));

                        var swatch = GenerateColorSwatchPng(ls.Range.ColorHex);
                        sheet.Images.Add(new XlsxImage(statRowIndex0, colIndexLeft, swatch, widthEmu: 180000, heightEmu: 120000, cellSpanCols: 1));
                    }
                    else
                    {
                        for (int c = 0; c < 6; c++) statRow.Cells.Add(XlsxCell.Text(""));
                    }

                    // 3 Gap columns (G, H, I)
                    for (int c = 6; c < 9; c++) statRow.Cells.Add(XlsxCell.Text(""));

                    // Right plot legend row: Color Swatch + Compact text "Range (Count : Percentage%)"
                    if (rightStats != null && s < rightStats.Count)
                    {
                        var rs = rightStats[s];
                        var rightTextDisplay = isRightEarfcn ? rs.Range.Display : rs.Range.RangeOnlyDisplay;
                        statRow.Cells.Add(XlsxCell.Text("", 3)); // Color swatch PNG image
                        statRow.Cells.Add(XlsxCell.Text($"{rightTextDisplay} ({rs.Count} : {rs.Percentage:0.00}%)", 3));
                        for (int c = 10; c < 15; c++) statRow.Cells.Add(XlsxCell.Text(""));

                        var swatch = GenerateColorSwatchPng(rs.Range.ColorHex);
                        sheet.Images.Add(new XlsxImage(statRowIndex0, colIndexRight, swatch, widthEmu: 180000, heightEmu: 120000, cellSpanCols: 1));
                    }

                    sheet.Rows.Add(statRow);
                }

                // 6. Blank spacer rows before next plot pair
                sheet.Rows.Add(XlsxRow.Blank());
                sheet.Rows.Add(XlsxRow.Blank());
            }

            return sheet;
        }

        private sealed class LegendStatRow
        {
            public ThresholdRange Range { get; set; } = new();
            public int Count { get; set; }
            public double Percentage { get; set; }
        }

        private static List<LegendStatRow> CalculateLegendStatistics(
            List<WalkTestLogRow> rows,
            string header,
            ReportThresholdConfig thresholdConfig)
        {
            var ranges = thresholdConfig.GetRangesForHeader(header);
            var result = ranges.Select(r => new LegendStatRow { Range = r, Count = 0 }).ToList();

            if (result.Count == 0)
                return result;

            var headerUpper = (header ?? "").ToUpperInvariant().Trim();

            if (headerUpper == "RSRP" || headerUpper == "RSRQ" || headerUpper == "SINR" ||
                headerUpper == "DL_THPT" || headerUpper == "UL_THPT" || headerUpper == "LTE_BLER" ||
                headerUpper == "MOS" || headerUpper == "PUSCH_TX" || headerUpper == "EARFCN")
            {
                var values = rows.Select(x =>
                    headerUpper == "RSRP" ? (double?)x.Rsrp :
                    headerUpper == "RSRQ" ? (double?)x.Rsrq :
                    headerUpper == "SINR" ? (double?)x.Sinr :
                    headerUpper == "MOS" ? (double?)x.Mos :
                    headerUpper == "DL_THPT" ? ParseDouble(x.DlTpt) :
                    headerUpper == "UL_THPT" ? ParseDouble(x.UlTpt) :
                    headerUpper == "LTE_BLER" ? ParseDouble(x.Bler) :
                    headerUpper == "PUSCH_TX" ? ParseDouble(x.Ta) :
                    headerUpper == "EARFCN" ? ParseDouble(x.Earfcn) : null)
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
                    .ToList();

                int total = values.Count > 0 ? values.Count : 1;

                foreach (var val in values)
                {
                    var match = result.FirstOrDefault(r => r.Range.Contains(val)) ??
                                result.FirstOrDefault(r => r.Range.ContainsInclusive(val));

                    if (match != null)
                        match.Count++;
                }

                foreach (var item in result)
                    item.Percentage = (item.Count * 100.0) / total;
            }
            else
            {
                int total = rows.Count > 0 ? rows.Count : 1;
                foreach (var item in result)
                {
                    if (!string.IsNullOrWhiteSpace(item.Range.ValueMatch))
                    {
                        item.Count = rows.Count(x =>
                            (x.Band ?? "").Contains(item.Range.ValueMatch, StringComparison.OrdinalIgnoreCase) ||
                            (x.Earfcn ?? "").Contains(item.Range.ValueMatch, StringComparison.OrdinalIgnoreCase) ||
                            (x.VolteCall ?? "").Contains(item.Range.ValueMatch, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        item.Count = rows.Count;
                    }

                    item.Percentage = (item.Count * 100.0) / total;
                }
            }

            return result;
        }

        private static double? ParseDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"-?\d+(\.\d+)?");
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
        }

        private static readonly ConcurrentDictionary<string, byte[]> SwatchCache = new(StringComparer.OrdinalIgnoreCase);

        private static byte[] GenerateColorSwatchPng(string hex)
        {
            var key = string.IsNullOrWhiteSpace(hex) ? "#808080" : hex.Trim();
            if (SwatchCache.TryGetValue(key, out var cached))
                return cached;

            var (r, g, b) = ParseHexColor(key);
            const int w = 32;
            const int h = 20;
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
            var color = new SixLabors.ImageSharp.PixelFormats.Rgba32(r, g, b);
            var borderColor = new SixLabors.ImageSharp.PixelFormats.Rgba32(
                (byte)Math.Max(0, r - 30),
                (byte)Math.Max(0, g - 30),
                (byte)Math.Max(0, b - 30));

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                            row[x] = borderColor;
                        else
                            row[x] = color;
                    }
                }
            });

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var bytes = ms.ToArray();
            SwatchCache[key] = bytes;
            return bytes;
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

        public sealed class ThresholdRange
        {
            public string Display { get; set; } = "";
            public double Min { get; set; }
            public double Max { get; set; }
            public string ColorHex { get; set; } = "#808080";
            public string? ValueMatch { get; set; }

            public ThresholdRange() { }

            public ThresholdRange(string display, double min, double max, string colorHex)
            {
                Display = string.IsNullOrWhiteSpace(display) ? FormatRange(min, max) : display;
                Min = min;
                Max = max;
                ColorHex = colorHex;
            }

            private static string FormatRange(double min, double max)
            {
                if (Math.Abs(min - max) < 0.0001)
                    return $"{min:0.##}";
                return $"{min:0.##} to {max:0.##}";
            }

            public string RangeOnlyDisplay
            {
                get
                {
                    if (!string.IsNullOrWhiteSpace(ValueMatch))
                        return ValueMatch;

                    return FormatRange(Min, Max);
                }
            }

            public bool Contains(double val)
            {
                if (Math.Abs(Min - Max) < 0.0001)
                    return Math.Abs(val - Min) < 0.0001;

                if (Min < Max)
                    return val >= Min && val < Max;

                return val >= Max && val < Min;
            }

            public bool ContainsInclusive(double val)
            {
                var lower = Math.Min(Min, Max);
                var upper = Math.Max(Min, Max);
                return val >= lower && val <= upper;
            }
        }

        public sealed class ReportThresholdConfig
        {
            public string Source { get; set; } = "Hardcoded";
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

            public List<ThresholdRange> GetRangesForHeader(string header)
            {
                var h = (header ?? "").ToUpperInvariant().Trim();
                return h switch
                {
                    "RSRP" => Rsrp,
                    "RSRQ" => Rsrq,
                    "SINR" => Sinr,
                    "MOS" => Mos,
                    "DL_THPT" => DlTpt,
                    "UL_THPT" => UlTpt,
                    "EARFCN" => Earfcn,
                    "LTE_BLER" or "BLER" => Bler,
                    "VOLTE_CALL" or "VOLTE" => VolteCall,
                    "PUSCH_TX" => PuschTx,
                    _ => Rsrp
                };
            }

            public static ReportThresholdConfig? FromSessionNotes(string? notes, string source)
            {
                var json = ExtractColorSettingsJson(notes);
                return FromColorSettingsJson(json, source);
            }

            public static string? ExtractColorSettingsJson(string? notes)
            {
                if (string.IsNullOrWhiteSpace(notes)) return null;

                var text = notes.Trim();
                if (text.StartsWith("{") && text.EndsWith("}"))
                    return text;

                var match = Regex.Match(text, @"(\{.*""color_settings"".*\}|\{.*""range"".*\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Value;

                var jsonMatch = Regex.Match(text, @"\{.*\}", RegexOptions.Singleline);
                if (jsonMatch.Success)
                    return jsonMatch.Value;

                return null;
            }

            public static ReportThresholdConfig? FromColorSettingsJson(string? json, string source)
            {
                if (string.IsNullOrWhiteSpace(json)) return null;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                        return null;

                    var fallback = Hardcoded();
                    var config = new ReportThresholdConfig
                    {
                        Source = source,
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
                        rangeRoot.ValueKind == System.Text.Json.JsonValueKind.Object)
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
                        valueRoot.ValueKind == System.Text.Json.JsonValueKind.Object)
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

            public static ReportThresholdConfig Hardcoded()
            {
                return new ReportThresholdConfig
                {
                    Source = "Hardcoded",
                    Rsrp = new List<ThresholdRange>
                    {
                        new("",-75,0,"#006400"),
                        new("",-85,-75,"#92D050"),
                        new("",-95,-85,"#95D5F5"),
                        new("",-105,-95,"#0000FF"),
                        new("",-115,-105,"#FFFF00"),
                        new("",-140,-115,"#FF0000")
                    },
                    Rsrq = new List<ThresholdRange>
                    {
                        new("",-5,0,"#006400"),
                        new("",-10,-5,"#92D050"),
                        new("",-15,-10,"#95D5F5"),
                        new("",-20,-15,"#0000FF"),
                        new("",-25,-20,"#FFFF00"),
                        new("",-30,-25,"#FF0000")
                    },
                    Sinr = new List<ThresholdRange>
                    {
                        new("",25,40,"#006400"),
                        new("",15,25,"#92D050"),
                        new("",10,15,"#95D5F5"),
                        new("",5,10,"#0000FF"),
                        new("",0,5,"#FFFF00"),
                        new("",-20,0,"#FF0000")
                    },
                    DlTpt = new List<ThresholdRange>
                    {
                        new("",100,1000,"#006400"),
                        new("",50,100,"#92D050"),
                        new("",20,50,"#95D5F5"),
                        new("",10,20,"#0000FF"),
                        new("",5,10,"#FFFF00"),
                        new("",0,5,"#FF0000")
                    },
                    UlTpt = new List<ThresholdRange>
                    {
                        new("",30,1000,"#006400"),
                        new("",15,30,"#92D050"),
                        new("",10,15,"#95D5F5"),
                        new("",5,10,"#0000FF"),
                        new("",1,5,"#FFFF00"),
                        new("",0,1,"#FF0000")
                    },
                    Earfcn = new List<ThresholdRange>
                    {
                        new("B3 1800MHz",0,0,"#4AA3FF") { ValueMatch="B3" },
                        new("B5 850MHz",0,0,"#00AA00") { ValueMatch="B5" },
                        new("B40 2300MHz",0,0,"#FFA500") { ValueMatch="B40" },
                        new("B41 2500MHz",0,0,"#FF1493") { ValueMatch="B41" },
                        new("B8 900MHz",0,0,"#4A148C") { ValueMatch="B8" }
                    },
                    Bler = new List<ThresholdRange>
                    {
                        new("",0,1,"#006400") { ValueMatch="< 1%" },
                        new("",1,3,"#92D050") { ValueMatch="1% - 3%" },
                        new("",3,5,"#95D5F5") { ValueMatch="3% - 5%" },
                        new("",5,10,"#0000FF") { ValueMatch="5% - 10%" },
                        new("",10,15,"#FFFF00") { ValueMatch="10% - 15%" },
                        new("",15,100,"#FF0000") { ValueMatch="> 15%" }
                    },
                    VolteCall = new List<ThresholdRange>
                    {
                        new("VoLTE Active",1,1,"#006400") { ValueMatch="1" },
                        new("No VoLTE",0,0,"#FF0000") { ValueMatch="0" }
                    },
                    PuschTx = new List<ThresholdRange>
                    {
                        new("",21,31,"#FF0000"),
                        new("",16,21,"#FFFF00"),
                        new("",9,16,"#0000FF"),
                        new("",1,9,"#95D5F5"),
                        new("",-50,1,"#006400")
                    }
                };
            }

            private static bool ApplyRangeMetric(System.Text.Json.JsonElement root, string metric, Action<List<ThresholdRange>> apply)
            {
                if (!TryGetPropertyIgnoreCase(root, metric, out var element) || element.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return false;

                var ranges = ParseThresholdArray(element, valueMode: false);
                if (ranges.Count == 0) return false;

                apply(ranges);
                return true;
            }

            private static bool ApplyValueMetric(System.Text.Json.JsonElement root, string metric, Action<List<ThresholdRange>> apply)
            {
                if (!TryGetPropertyIgnoreCase(root, metric, out var element) || element.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return false;

                var ranges = ParseThresholdArray(element, valueMode: true);
                if (ranges.Count == 0) return false;

                apply(ranges);
                return true;
            }

            private static List<ThresholdRange> ParseThresholdArray(System.Text.Json.JsonElement element, bool valueMode)
            {
                var ranges = new List<ThresholdRange>();
                if (element.ValueKind != System.Text.Json.JsonValueKind.Array) return ranges;

                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                    var min = GetDouble(item, "min");
                    var max = GetDouble(item, "max");
                    var val = GetStringOrNumberFallback(item, "value");

                    if (valueMode)
                    {
                        if (string.IsNullOrWhiteSpace(val)) continue;

                        ranges.Add(new ThresholdRange(
                            GetStringOrNumberFallback(item, "label", "range", "name") ?? val,
                            0, 0, GetColor(item))
                        {
                            ValueMatch = val
                        });
                        continue;
                    }

                    ranges.Add(new ThresholdRange(
                        GetStringOrNumberFallback(item, "label", "range", "name") ?? "",
                        min, max, GetColor(item)));
                }

                return ranges;
            }

            private static bool TryGetPropertyIgnoreCase(System.Text.Json.JsonElement root, string propertyName, out System.Text.Json.JsonElement value)
            {
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        {
                            value = prop.Value;
                            return true;
                        }
                    }
                }

                value = default;
                return false;
            }

            private static double GetDouble(System.Text.Json.JsonElement item, string propertyName)
            {
                if (TryGetPropertyIgnoreCase(item, propertyName, out var prop))
                {
                    if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetDouble(out var val))
                        return val;

                    if (prop.ValueKind == System.Text.Json.JsonValueKind.String &&
                        double.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                }

                return 0;
            }

            private static string? GetStringOrNumberFallback(System.Text.Json.JsonElement item, params string[] propertyNames)
            {
                foreach (var name in propertyNames)
                {
                    if (!TryGetPropertyIgnoreCase(item, name, out var prop)) continue;

                    if (prop.ValueKind == System.Text.Json.JsonValueKind.String)
                        return prop.GetString();

                    if (prop.ValueKind == System.Text.Json.JsonValueKind.Number)
                        return prop.GetRawText();
                }

                return null;
            }

            private static string GetColor(System.Text.Json.JsonElement item)
            {
                return GetStringOrNumberFallback(item, "color", "colorHex", "color_hex", "hex") ?? "#808080";
            }
        }

        /// <summary>Reads width/height in pixels from a PNG's IHDR chunk (bytes 16-23).</summary>
        private static (int WidthPx, int HeightPx) ReadPngSizePx(byte[] png)
        {
            const int fallbackPx = 800;
            if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
                return (fallbackPx, fallbackPx * 3 / 4);

            int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

            if (width <= 0 || height <= 0)
                return (fallbackPx, fallbackPx * 3 / 4);

            return (width, height);
        }

        /// <summary>
        /// Generates a clean color-coded legend PNG graphic in memory if no pre-generated legend image exists on the server.
        /// </summary>
        private static byte[] GenerateLegendPng(string header)
        {
            const int width = 240;
            const int height = 150;
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);

            var bgColor = new SixLabors.ImageSharp.PixelFormats.Rgba32(245, 247, 250);
            var borderColor = new SixLabors.ImageSharp.PixelFormats.Rgba32(210, 215, 225);
            var legendColors = GetLegendColorsForHeader(header);

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                        {
                            row[x] = borderColor;
                            continue;
                        }

                        row[x] = bgColor;

                        for (int i = 0; i < legendColors.Length; i++)
                        {
                            int boxTop = 15 + (i * 32);
                            int boxBottom = boxTop + 22;
                            int boxLeft = 15;
                            int boxRight = 60;

                            if (x >= boxLeft && x < boxRight && y >= boxTop && y < boxBottom)
                            {
                                row[x] = legendColors[i];
                            }
                        }
                    }
                }
            });

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static SixLabors.ImageSharp.PixelFormats.Rgba32[] GetLegendColorsForHeader(string header)
        {
            return new SixLabors.ImageSharp.PixelFormats.Rgba32[]
            {
                new(112, 173, 71),  // Excellent - Green
                new(146, 208, 80),  // Good - Light Green
                new(255, 192, 0),   // Fair - Yellow
                new(192, 0, 0)      // Poor - Red
            };
        }

        /// <summary>Converts a pixel size to EMU at 96 DPI, capping width and scaling height to match.</summary>
        private static (int WidthEmu, int HeightEmu) ScaleToEmu((int WidthPx, int HeightPx) sizePx, int maxWidthEmu)
        {
            const int emuPerPixel = 9525; // 96 DPI
            var widthEmu = sizePx.WidthPx * emuPerPixel;
            var heightEmu = sizePx.HeightPx * emuPerPixel;

            if (widthEmu <= maxWidthEmu)
                return (widthEmu, heightEmu);

            var scale = (double)maxWidthEmu / widthEmu;
            return (maxWidthEmu, (int)(heightEmu * scale));
        }

        private static List<int> ResolveSessionIds(IEnumerable<long>? requestSessionIds, string? projectSessionIds)
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

            return ids
                .Where(x => x <= int.MaxValue)
                .Select(x => (int)x)
                .Distinct()
                .ToList();
        }

        private static List<long>? ParseSessionIds(string? sessionIds)
        {
            if (string.IsNullOrWhiteSpace(sessionIds))
                return null;

            return Regex.Split(sessionIds, @"[,\s;|]+")
                .Select(x => long.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(x => x > 0)
                .Distinct()
                .ToList();
        }

        private static string BuildImageUrl(int sessionId, string header)
        {
            return $"{ImageBaseUrl}/{sessionId}_{header}.png";
        }

        private static string BuildLegendImageUrl(int sessionId, string header)
        {
            return $"{ImageBaseUrl}/{sessionId}_{header}_legend.png";
        }

        private static string BuildGlobalLegendImageUrl(string header)
        {
            return $"{ImageBaseUrl}/legend_{header.ToLower(CultureInfo.InvariantCulture)}.png";
        }

        private static string ToBandSheetName(string? band, string? network)
        {
            var value = (band ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("NA", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(network) && network.Contains("5G", StringComparison.OrdinalIgnoreCase))
                    return "n78";

                return "Unknown Band";
            }

            value = value.Replace("LTE", "", StringComparison.OrdinalIgnoreCase).Trim();
            value = Regex.Replace(value, @"\s+", "");

            if (Regex.IsMatch(value, @"^[BbNn]\d+[A-Za-z]?$"))
                return value.StartsWith("n", StringComparison.OrdinalIgnoreCase)
                    ? "n" + value[1..]
                    : "B" + value[1..];

            var match = Regex.Match(value, @"\d+");
            if (match.Success)
                return $"B{match.Value}";

            return value.Length <= 31 ? value : value[..31];
        }

        private static string FormatBandValue(int? band)
        {
            return band.HasValue ? $"B{band.Value}" : "";
        }

        private static string FormatValue(int? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture) ?? "";
        }

        private static string FormatValue(float? value)
        {
            return value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        }

        private static string FormatValue(double? value)
        {
            return value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        }

        private static string BandSortKey(string value)
        {
            var match = Regex.Match(value ?? "", @"\d+");
            return match.Success && int.TryParse(match.Value, out var number)
                ? number.ToString("000000", CultureInfo.InvariantCulture)
                : value ?? "";
        }

        public sealed class WalkTestExcelReportRequest
        {
            public int ProjectId { get; set; }
            public List<long>? SessionIds { get; set; }
            public string? Provider { get; set; }
            public string? NetworkType { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public int? Limit { get; set; }
        }

        private sealed class WalkTestLogRow
        {
            public int Id { get; set; }
            public int SessionId { get; set; }
            public DateTime? Timestamp { get; set; }
            public float? Lat { get; set; }
            public float? Lon { get; set; }
            public string? IndoorOutdoor { get; set; }
            public string? Network { get; set; }
            public string? Provider { get; set; }
            public string? Band { get; set; }
            public string BandSheetName { get; set; } = "Unknown Band";
            public string? Pci { get; set; }
            public float? Rsrp { get; set; }
            public float? Rsrq { get; set; }
            public float? Sinr { get; set; }
            public string? DlTpt { get; set; }
            public string? UlTpt { get; set; }
            public float? Mos { get; set; }
            public string? Apps { get; set; }
            public string? NodeBId { get; set; }
            public string? CellId { get; set; }
            public string? Earfcn { get; set; }
            public string? Bler { get; set; }
            public string? VolteCall { get; set; }
            public float? Cqi { get; set; }
            public string? Ta { get; set; }
            public int? Level { get; set; }
            public string? Primary { get; set; }
        }

        private sealed class WalkTestSiteSummaryRow
        {
            public string Source { get; set; } = "";
            public int SourceId { get; set; }
            public int? Version { get; set; }
            public string? Status { get; set; }
            public int? Site { get; set; }
            public int? SiteName { get; set; }
            public string? Sector { get; set; }
            public int? CellId { get; set; }
            public int? SecId { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public int? Tac { get; set; }
            public int? Pci { get; set; }
            public int? Azimuth { get; set; }
            public int? Height { get; set; }
            public int? Band { get; set; }
            public int? Earfcn { get; set; }
            public int? Bw { get; set; }
            public int? MTilt { get; set; }
            public int? ETilt { get; set; }
            public double? TxPower { get; set; }
            public double? ReferenceSignalPower { get; set; }
            public string? Frequency { get; set; }
            public string? Cluster { get; set; }
            public string? Technology { get; set; }
        }

        private sealed class XlsxWorkbook
        {
            public List<XlsxSheet> Sheets { get; } = new();
        }

        private sealed class XlsxSheet
        {
            public XlsxSheet(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public double[]? ColumnWidths { get; set; }
            public List<XlsxRow> Rows { get; } = new();
            public List<XlsxImage> Images { get; } = new();
        }

        /// <summary>
        /// A picture to embed in the sheet, anchored to the top-left of a specific cell.
        /// RowIndex0/ColIndex0 are 0-based (matching OOXML drawing anchors).
        /// </summary>
        private sealed class XlsxImage
        {
            public XlsxImage(int rowIndex0, int colIndex0, byte[] data, int widthEmu = 1724025, int heightEmu = 889000, int cellSpanCols = 6)
            {
                RowIndex0 = rowIndex0;
                ColIndex0 = colIndex0;
                Data = data;
                WidthEmu = widthEmu;
                HeightEmu = heightEmu;
                CellSpanCols = cellSpanCols;
            }

            public int RowIndex0 { get; }
            public int ColIndex0 { get; }
            public byte[] Data { get; }
            public int WidthEmu { get; }
            public int HeightEmu { get; }
            public int CellSpanCols { get; }
        }

        private sealed class XlsxRow
        {
            public XlsxRow(double? height = null)
            {
                Height = height;
            }

            public double? Height { get; }
            public List<XlsxCell> Cells { get; } = new();

            public static XlsxRow Title(string text, int span)
            {
                var row = new XlsxRow(24);
                row.Cells.Add(XlsxCell.Text(text, 1));
                for (var i = 1; i < span; i++)
                    row.Cells.Add(XlsxCell.Text(""));
                return row;
            }

            public static XlsxRow Header(params string[] values)
            {
                var row = new XlsxRow();
                row.Cells.AddRange(values.Select(value => XlsxCell.Text(value, 2)));
                return row;
            }

            public static XlsxRow Data(params string?[] values)
            {
                var row = new XlsxRow();
                row.Cells.AddRange(values.Select(value => XlsxCell.Text(value ?? "", 3)));
                return row;
            }

            public static XlsxRow FromText(string label, string value)
            {
                var row = new XlsxRow();
                row.Cells.Add(XlsxCell.Text(label, 4));
                row.Cells.Add(XlsxCell.Text(value, 3));
                return row;
            }

            public static XlsxRow Blank()
            {
                return new XlsxRow();
            }
        }

        private sealed class XlsxCell
        {
            public string? TextValue { get; init; }
            public string? FormulaValue { get; init; }
            public int StyleId { get; init; }

            public static XlsxCell Text(string? value, int styleId = 0)
            {
                return new XlsxCell { TextValue = value ?? "", StyleId = styleId };
            }

            public static XlsxCell Formula(string formula, int styleId = 0)
            {
                return new XlsxCell { FormulaValue = formula, StyleId = styleId };
            }
        }

        private static class SimpleXlsxWriter
        {
            private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            private const string OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            private const string XmlNs = "http://www.w3.org/XML/1998/namespace"; // ADDED correctly declared XML namespace

            public static byte[] Write(XlsxWorkbook workbook)
            {
                var sheets = BuildUniqueSheets(workbook.Sheets);

                // Sheets that actually have pictures get a drawing part; number them in sheet order
                // so xl/drawings/drawing{N}.xml lines up 1:1 with the sheets that need one.
                var drawingNumberBySheetIndex = new Dictionary<int, int>();
                var nextDrawingNumber = 1;
                for (var i = 0; i < sheets.Count; i++)
                {
                    if (sheets[i].Images.Count == 0)
                        continue;

                    drawingNumberBySheetIndex[i] = nextDrawingNumber++;
                }

                // Every embedded image becomes its own xl/media/image{N}.png part, numbered
                // sequentially across the whole workbook.
                var mediaNumberByImage = new Dictionary<XlsxImage, int>();
                var nextMediaNumber = 1;
                foreach (var sheet in sheets)
                {
                    foreach (var image in sheet.Images)
                        mediaNumberByImage[image] = nextMediaNumber++;
                }

                using var ms = new MemoryStream();
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(sheets.Count, drawingNumberBySheetIndex.Count));
                    WriteEntry(archive, "_rels/.rels", BuildRootRels());
                    WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheets));
                    WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(sheets.Count));
                    WriteEntry(archive, "xl/styles.xml", BuildStylesXml());

                    for (var i = 0; i < sheets.Count; i++)
                    {
                        var hasDrawing = drawingNumberBySheetIndex.TryGetValue(i, out var drawingNumber);
                        WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", BuildWorksheetXml(sheets[i], hasDrawing ? drawingNumber : null));

                        if (hasDrawing)
                        {
                            WriteEntry(
                                archive,
                                $"xl/worksheets/_rels/sheet{i + 1}.xml.rels",
                                BuildWorksheetRels(drawingNumber));
                        }
                    }

                    foreach (var (sheetIndex, drawingNumber) in drawingNumberBySheetIndex)
                    {
                        var sheet = sheets[sheetIndex];
                        WriteEntry(archive, $"xl/drawings/drawing{drawingNumber}.xml", BuildDrawingXml(sheet.Images, mediaNumberByImage));
                        WriteEntry(archive, $"xl/drawings/_rels/drawing{drawingNumber}.xml.rels", BuildDrawingRels(sheet.Images, mediaNumberByImage));
                    }

                    foreach (var (image, mediaNumber) in mediaNumberByImage)
                        WriteBinaryEntry(archive, $"xl/media/image{mediaNumber}.png", image.Data);
                }

                return ms.ToArray();
            }

            private static List<XlsxSheet> BuildUniqueSheets(IEnumerable<XlsxSheet> source)
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new List<XlsxSheet>();

                foreach (var sheet in source)
                {
                    var name = MakeUniqueSheetName(SanitizeSheetName(sheet.Name), used);
                    var copy = new XlsxSheet(name)
                    {
                        ColumnWidths = sheet.ColumnWidths
                    };
                    copy.Rows.AddRange(sheet.Rows);
                    copy.Images.AddRange(sheet.Images);
                    result.Add(copy);
                }

                return result;
            }

            private static string MakeUniqueSheetName(string baseName, HashSet<string> used)
            {
                var name = string.IsNullOrWhiteSpace(baseName) ? "Sheet" : baseName;
                if (used.Add(name))
                    return name;

                for (var index = 2; index < 1000; index++)
                {
                    var suffix = $" ({index})";
                    var candidate = name.Length + suffix.Length > 31
                        ? name[..(31 - suffix.Length)] + suffix
                        : name + suffix;

                    if (used.Add(candidate))
                        return candidate;
                }

                return Guid.NewGuid().ToString("N")[..31];
            }

            private static string SanitizeSheetName(string value)
            {
                var clean = Regex.Replace(value ?? "", @"[\x00-\x1F\[\]\:\*\?\/\\]", " ").Trim();
                clean = Regex.Replace(clean, @"\s+", " ");
                clean = clean.Trim('\'');
                if (clean.Length == 0)
                    clean = "Sheet";
                return clean.Length <= 31 ? clean : clean[..31];
            }

            private static string BuildContentTypes(int sheetCount, int drawingCount)
            {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
                sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
                sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
                if (drawingCount > 0)
                    sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
                sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                for (var i = 1; i <= sheetCount; i++)
                    sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                for (var i = 1; i <= drawingCount; i++)
                    sb.Append($"<Override PartName=\"/xl/drawings/drawing{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
                sb.Append("</Types>");
                return sb.ToString();
            }

            private static string BuildRootRels()
            {
                return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{RelNs}\"><Relationship Id=\"rId1\" Type=\"{OfficeRelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
            }

            private static string BuildWorkbookRels(int sheetCount)
            {
                var sb = new StringBuilder();
                sb.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{RelNs}\">");
                for (var i = 1; i <= sheetCount; i++)
                    sb.Append($"<Relationship Id=\"rId{i}\" Type=\"{OfficeRelNs}/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
                sb.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"{OfficeRelNs}/styles\" Target=\"styles.xml\"/>");
                sb.Append("</Relationships>");
                return sb.ToString();
            }

            private static string BuildWorksheetRels(int drawingNumber)
            {
                return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{RelNs}\"><Relationship Id=\"rId1\" Type=\"{OfficeRelNs}/drawing\" Target=\"../drawings/drawing{drawingNumber}.xml\"/></Relationships>";
            }

            private static string BuildDrawingXml(List<XlsxImage> images, Dictionary<XlsxImage, int> mediaNumberByImage)
            {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sb.Append("<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">");

                for (var i = 0; i < images.Count; i++)
                {
                    var image = images[i];
                    var rId = $"rId{i + 1}";
                    var picId = i + 2; // 1 is reserved implicitly by convention; keep ids unique/nonzero

                    sb.Append("<xdr:twoCellAnchor editAs=\"oneCell\">");
                    sb.Append($"<xdr:from><xdr:col>{image.ColIndex0}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{image.RowIndex0}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>");
                    sb.Append($"<xdr:to><xdr:col>{image.ColIndex0 + image.CellSpanCols}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{image.RowIndex0 + 1}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>");
                    sb.Append("<xdr:pic>");
                    sb.Append("<xdr:nvPicPr>");
                    sb.Append($"<xdr:cNvPr id=\"{picId}\" name=\"Image {picId}\" descr=\"Picture\"/>");
                    sb.Append("<xdr:cNvPicPr/>");
                    sb.Append("</xdr:nvPicPr>");
                    sb.Append("<xdr:blipFill>");
                    sb.Append($"<a:blip xmlns:r=\"{OfficeRelNs}\" r:embed=\"{rId}\" cstate=\"print\"/>");
                    sb.Append("<a:stretch><a:fillRect/></a:stretch>");
                    sb.Append("</xdr:blipFill>");
                    sb.Append("<xdr:spPr>");
                    sb.Append($"<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{image.WidthEmu}\" cy=\"{image.HeightEmu}\"/></a:xfrm>");
                    sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
                    sb.Append("</xdr:spPr>");
                    sb.Append("</xdr:pic>");
                    sb.Append("<xdr:clientData/>");
                    sb.Append("</xdr:twoCellAnchor>");
                }

                sb.Append("</xdr:wsDr>");
                return sb.ToString();
            }

            private static string BuildDrawingRels(List<XlsxImage> images, Dictionary<XlsxImage, int> mediaNumberByImage)
            {
                var sb = new StringBuilder();
                sb.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{RelNs}\">");
                for (var i = 0; i < images.Count; i++)
                {
                    var mediaNumber = mediaNumberByImage[images[i]];
                    sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"{OfficeRelNs}/image\" Target=\"../media/image{mediaNumber}.png\"/>");
                }
                sb.Append("</Relationships>");
                return sb.ToString();
            }

            private static string BuildWorkbookXml(List<XlsxSheet> sheets)
            {
                using var sw = new Utf8StringWriter(CultureInfo.InvariantCulture);
                using (var writer = XmlWriter.Create(sw, XmlSettings())) // FIXED: Explicit writer scoping and flushing
                {
                    writer.WriteStartDocument(true);
                    writer.WriteStartElement("workbook", MainNs);
                    writer.WriteAttributeString("xmlns", "r", null, OfficeRelNs);
                    writer.WriteStartElement("sheets", MainNs);

                    for (var i = 0; i < sheets.Count; i++)
                    {
                        writer.WriteStartElement("sheet", MainNs);
                        writer.WriteAttributeString("name", sheets[i].Name);
                        writer.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("r", "id", OfficeRelNs, $"rId{i + 1}");
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.WriteStartElement("calcPr", MainNs);
                    writer.WriteAttributeString("calcMode", "auto");
                    writer.WriteAttributeString("fullCalcOnLoad", "1");
                    writer.WriteAttributeString("forceFullCalc", "1");
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                    
                    writer.Flush(); // FIXED: Flush before reading string
                }
                return sw.ToString();
            }

            private static string BuildWorksheetXml(XlsxSheet sheet, int? drawingNumber)
            {
                using var sw = new Utf8StringWriter(CultureInfo.InvariantCulture);
                using (var writer = XmlWriter.Create(sw, XmlSettings())) // FIXED: Explicit writer scoping and flushing
                {
                    var rowCount = Math.Max(sheet.Rows.Count, 1);
                    var colCount = Math.Max(sheet.Rows.Select(x => x.Cells.Count).DefaultIfEmpty(1).Max(), 1);

                    writer.WriteStartDocument(true);
                    writer.WriteStartElement("worksheet", MainNs);
                    writer.WriteAttributeString("xmlns", "r", null, OfficeRelNs);

                    writer.WriteStartElement("dimension", MainNs);
                    writer.WriteAttributeString("ref", $"A1:{ColumnName(colCount)}{rowCount}");
                    writer.WriteEndElement();

                    writer.WriteStartElement("sheetViews", MainNs);
                    writer.WriteStartElement("sheetView", MainNs);
                    writer.WriteAttributeString("workbookViewId", "0");
                    writer.WriteStartElement("pane", MainNs);
                    writer.WriteAttributeString("ySplit", "1");
                    writer.WriteAttributeString("topLeftCell", "A2");
                    writer.WriteAttributeString("activePane", "bottomLeft");
                    writer.WriteAttributeString("state", "frozen");
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    writer.WriteEndElement();

                    if (sheet.ColumnWidths?.Length > 0)
                    {
                        writer.WriteStartElement("cols", MainNs);
                        for (var i = 0; i < sheet.ColumnWidths.Length; i++)
                        {
                            writer.WriteStartElement("col", MainNs);
                            writer.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("width", sheet.ColumnWidths[i].ToString("0.##", CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("customWidth", "1");
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }

                    writer.WriteStartElement("sheetData", MainNs);
                    for (var r = 0; r < sheet.Rows.Count; r++)
                    {
                        var row = sheet.Rows[r];
                        writer.WriteStartElement("row", MainNs);
                        writer.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
                        if (row.Height.HasValue)
                        {
                            writer.WriteAttributeString("ht", row.Height.Value.ToString("0.##", CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("customHeight", "1");
                        }

                        for (var c = 0; c < row.Cells.Count; c++)
                            WriteCell(writer, row.Cells[c], r + 1, c + 1);

                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();

                    if (drawingNumber.HasValue)
                    {
                        writer.WriteStartElement("drawing", MainNs);
                        writer.WriteAttributeString("r", "id", OfficeRelNs, "rId1");
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                    
                    writer.Flush(); // FIXED: Flush before reading string
                }
                return sw.ToString();
            }

            private static string BuildStylesXml()
            {
                // Font/fill/border palette below mirrors the reference "Walk Test Sample Report"
                // workbook (Arial throughout, navy header band, peach form-label fill, thin black
                // grid lines) while keeping this sheet's existing table layout unchanged.
                return """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="5">
    <font><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="14"/><color rgb="FF000000"/><name val="Arial"/><family val="2"/></font>
    <font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Arial"/><family val="2"/></font>
    <font><sz val="10"/><color rgb="FF000000"/><name val="Arial"/><family val="2"/></font>
    <font><b/><sz val="10"/><color rgb="FF000000"/><name val="Arial"/><family val="2"/></font>
  </fonts>
  <fills count="6">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFFBE4D5"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FF8EAADB"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill>
  </fills>
  <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style="thin"><color rgb="FF000000"/></left><right style="thin"><color rgb="FF000000"/></right><top style="thin"><color rgb="FF000000"/></top><bottom style="thin"><color rgb="FF000000"/></bottom><diagonal/></border>
  </borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="7">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
    <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="3" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment vertical="center"/></xf>
    <xf numFmtId="0" fontId="4" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center"/></xf>
    <xf numFmtId="0" fontId="2" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="4" fillId="5" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center"/></xf>
  </cellXfs>
  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>
""";
            }

            private static void WriteCell(XmlWriter writer, XlsxCell cell, int row, int col)
            {
                var hasText = !string.IsNullOrEmpty(cell.TextValue);
                var hasFormula = !string.IsNullOrWhiteSpace(cell.FormulaValue);

                // FIXED: Do not write malformed <is> elements for completely empty, unstyled cells
                if (!hasText && !hasFormula && cell.StyleId == 0)
                    return;

                writer.WriteStartElement("c", MainNs);
                writer.WriteAttributeString("r", $"{ColumnName(col)}{row}");
                if (cell.StyleId > 0)
                    writer.WriteAttributeString("s", cell.StyleId.ToString(CultureInfo.InvariantCulture));

                if (hasFormula)
                {
                    writer.WriteElementString("f", MainNs, cell.FormulaValue);
                }
                else if (hasText)
                {
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is", MainNs);
                    writer.WriteStartElement("t", MainNs);
                    
                    // FIXED: valid xml:space namespace resolution and attribute declaration
                    if (cell.TextValue!.StartsWith(" ") || cell.TextValue.EndsWith(" "))
                    {
                        writer.WriteAttributeString("xml", "space", XmlNs, "preserve");
                    }
                    
                    writer.WriteString(cell.TextValue);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            private static string ColumnName(int index)
            {
                var dividend = index;
                var columnName = "";
                while (dividend > 0)
                {
                    var modulo = (dividend - 1) % 26;
                    columnName = Convert.ToChar(65 + modulo) + columnName;
                    dividend = (dividend - modulo) / 26;
                }
                return columnName;
            }

            private static void WriteEntry(ZipArchive archive, string path, string content)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }

            private static void WriteBinaryEntry(ZipArchive archive, string path, byte[] data)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }

            private static XmlWriterSettings XmlSettings()
            {
                return new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    OmitXmlDeclaration = false,
                    Indent = false
                };
            }

            private sealed class Utf8StringWriter : StringWriter
            {
                public Utf8StringWriter(IFormatProvider formatProvider)
                    : base(formatProvider)
                {
                }

                public override Encoding Encoding => Encoding.UTF8;
            }
        }
    }
}