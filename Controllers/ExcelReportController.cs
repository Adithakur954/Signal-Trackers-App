using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Data.Common;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using SignalTracker.Models;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ExcelReportController : ControllerBase
    {
        private const string ImageBaseUrl = "https://apistracer.vinfocom.co.in/uploaded_images";
        private static readonly HashSet<string> UniqueValueHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "PCI", "NODEB_ID", "CELL_ID", "CI", "CELLID", "CID"
        };

        private static bool IsUniqueValueHeader(string? header) =>
            !string.IsNullOrWhiteSpace(header) && UniqueValueHeaders.Contains(header.Trim());

        private static bool RowHasImageName(WalkTestLogRow r, string? headerUpper = null)
        {
            if (!string.IsNullOrWhiteSpace(r.RawImageName))
                return true;

            if (r.MetricColors != null && r.MetricColors.Count > 0)
                return true;

            return false;
        }

        private static List<string> GetUniqueValuesForHeader(List<WalkTestLogRow> rows, string header, bool filterByImageName = true)
        {
            var headerUpper = (header ?? "").ToUpperInvariant().Trim();

            List<WalkTestLogRow> targetRows;
            if (filterByImageName)
            {
                var validImageRows = rows.Where(r => RowHasImageName(r, headerUpper)).ToList();
                targetRows = validImageRows.Count > 0 ? validImageRows : rows;
            }
            else
            {
                targetRows = rows;
            }

            IEnumerable<string?> rawValues = headerUpper switch
            {
                "PCI"      => targetRows.Select(r => r.Pci),
                "NODEB_ID" => targetRows.Select(r => r.NodeBId),
                "CELL_ID" or "CI" or "CELLID" or "CID" => targetRows.Select(r => r.CellId),
                _          => Enumerable.Empty<string?>()
            };

            return rawValues
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static readonly string[] ImageHeaders =
        {
            "BAND", "RSRP", "RSRQ", "SINR", "DL_THPT", "UL_THPT", "CI",
            "EARFCN", "LTE_BLER", "PCI", "NODEB_ID", "PUSCH_TX"
        };

        private readonly ApplicationDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;

        public ExcelReportController(ApplicationDbContext db, IHttpClientFactory httpClientFactory)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
        }

        private static async Task<string> SaveUploadToTempFileAsync(IFormFile upload, CancellationToken ct)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"signaltracker-report-{Guid.NewGuid():N}.zip");
            await using var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);

            await upload.CopyToAsync(output, ct);
            return tempPath;
        }

        [HttpGet("Generate")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Report")]
        public Task<IActionResult> GenerateFromQuery(
            [FromQuery] int projectId,
            [FromQuery] string? sessionIds = null,
            [FromQuery] string? provider = null,
            [FromQuery] string? networkType = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] int? limit = null,
            [FromQuery] string? reportMode = null,
            [FromQuery] string? sheetMode = null,
            [FromQuery] string? mode = null)
        {
            var request = new WalkTestExcelReportRequest
            {
                ProjectId = projectId,
                SessionIds = ParseSessionIds(sessionIds),
                Provider = provider,
                NetworkType = networkType,
                StartDate = startDate,
                EndDate = endDate,
                Limit = limit,
                ReportMode = reportMode ?? sheetMode ?? mode
            };

            return Generate(request);
        }

        [HttpPost("Generate")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Report")]
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

            var reportMode = request.ReportMode ?? request.SheetMode ?? request.Mode ?? "separate";
            var filterByImageName = ResolveFilterByImageName(request, Request.HasFormContentType ? Request.Form : null, Request.Query);

            var workbook = BuildWorkbook(
                project.project_name ?? $"Project {project.id}",
                sessionIds,
                rows,
                siteRows,
                imageBytesByUrl,
                thresholds,
                reportMode,
                filterByImageName);

            var bytes = SimpleXlsxWriter.Write(workbook);
            var filename = $"Walk_Test_Report_{request.ProjectId}_{DateTime.Now:yyyy-MM-dd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }

        // POST api/ExcelReport/GenerateFromZip
        // multipart/form-data:
        //   LogZip / LogZips -> one or more .zip files (at least one required)
        //   ProjectName      -> optional (defaults to first zip file name)
        //   SessionIdOverride -> optional, forces the session id used for image lookup
        //   BandFilter/Bands -> optional, one or more selected bands; ALL/empty = no filter
        //   ShowSampleCount  -> optional (true/false); when true legend shows "(count | %)"
        [HttpPost("GenerateFromZip")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Report")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(1_000_000_000)]
        public async Task<IActionResult> GenerateFromZip([FromForm] ZipReportUploadRequest request)
        {
            var uploads = ResolveUploadedFiles(request, Request);
            if (uploads.Count == 0)
                return BadRequest(new { Message = "At least one log zip file is required." });

            var allTempPaths  = new List<string>();
            var allRawRows    = new List<WalkTestLogRow>();
            var mergedImages  = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var allSiteRows   = new List<WalkTestSiteSummaryRow>();
            var sessionIds    = new List<int>();
            ReportThresholdConfig? thresholds = null;
            var fileNames     = new List<string>();

            try
            {
                var fileGroups = ResolveLogFileGroups(uploads.Count, Request.HasFormContentType ? Request.Form : null, Request.Query);

                for (int zipIdx = 0; zipIdx < uploads.Count; zipIdx++)
                {
                    var upload = uploads[zipIdx];
                    var tempPath = await SaveUploadToTempFileAsync(upload, HttpContext.RequestAborted);
                    allTempPaths.Add(tempPath);

                    await using var zipStream = new FileStream(
                        tempPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 128 * 1024, useAsync: true);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

                    // Extract map images (last zip wins for same key)
                    var mapImages = ExtractMapImagesFromZip(archive, out var detectedSessionId);
                    foreach (var kvp in mapImages)
                        mergedImages[kvp.Key] = kvp.Value;

                    var sid = (int)(request.SessionIdOverride ?? detectedSessionId ?? 0);
                    if (sid > 0 && !sessionIds.Contains(sid)) sessionIds.Add(sid);

                    var zipRows = ExtractNetworkRowsFromZip(archive, sid);
                    int assignedGroup = fileGroups[zipIdx];
                    string fileName = Path.GetFileNameWithoutExtension(upload.FileName);
                    foreach (var r in zipRows)
                    {
                        r.FileIndex = zipIdx;
                        r.SourceFileName = fileName;
                        r.FileGroup = assignedGroup;
                    }
                    allRawRows.AddRange(zipRows);

                    // First zip with a ColorSettings file wins for thresholds
                    if (thresholds == null)
                    {
                        var candidate = ExtractThresholdConfigFromZip(archive);
                        if (!candidate.Source.Equals("Hardcoded", StringComparison.OrdinalIgnoreCase))
                            thresholds = candidate;
                    }

                    allSiteRows.AddRange(ExtractSiteSummaryRowsFromZip(archive));
                    fileNames.Add(fileName);
                }

                thresholds ??= ReportThresholdConfig.Hardcoded();

                // Merge & deduplicate all rows across all zips
                var rows = CleanZipRows(allRawRows);
                if (rows.Count == 0)
                    return BadRequest(new { Message = "No usable network log rows were found inside the uploaded zip(s)." });

                var filterByImageName = ResolveFilterByImageName(request, Request.HasFormContentType ? Request.Form : null);
                if (filterByImageName)
                {
                    var imageRows = rows.Where(r => RowHasImageName(r)).ToList();
                    if (imageRows.Count > 0) rows = imageRows;
                }

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

                // Build project name: join all file names when multiple zips
                var projectName = !string.IsNullOrWhiteSpace(request.ProjectName)
                    ? request.ProjectName
                    : fileNames.Count == 1
                        ? fileNames[0]
                        : string.Join(" + ", fileNames);

                if (selectedBands.Count > 0)
                    projectName += $" ({(selectedBands.Count == 1 ? "Band" : "Bands")}: {string.Join(", ", selectedBands)})";

                var imageBytesByUrl = BuildImageBytesByUrlFromZip(mergedImages, sessionIds);
                var reportMode      = ResolveReportMode(request, Request.HasFormContentType ? Request.Form : null);
                var showSampleCount = ResolveShowSampleCount(request, Request.HasFormContentType ? Request.Form : null);
                var customSheetNames = ResolveCustomSheetNames(Request.HasFormContentType ? Request.Form : null, Request.Query);

                var workbook = BuildWorkbook(
                    projectName, sessionIds, rows, allSiteRows,
                    imageBytesByUrl, thresholds, reportMode, filterByImageName, showSampleCount, customSheetNames);

                var bytes = SimpleXlsxWriter.Write(workbook);
                var primarySessionId = sessionIds.Count > 0 ? sessionIds[0] : 0;
                var filename = $"Walk_Test_Report_Zip_{primarySessionId}_{DateTime.Now:yyyy-MM-dd}.xlsx";
                return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
            }
            finally
            {
                foreach (var p in allTempPaths)
                    try { System.IO.File.Delete(p); } catch { }
            }
        }

        // POST api/ExcelReport/DiscoverBands
        // multipart/form-data:
        //   LogZip / LogZips -> one or more .zip files (at least one required)
        //   SessionIdOverride -> optional
        [HttpPost("DiscoverBands")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Report")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(1_000_000_000)]
        public async Task<IActionResult> DiscoverBands([FromForm] ZipBandDiscoveryRequest request)
        {
            var uploads = ResolveUploadedFilesForDiscovery(request, Request);
            if (uploads.Count == 0)
                return BadRequest(new { Message = "At least one log zip file is required." });

            var allTempPaths = new List<string>();
            var allRawRows   = new List<WalkTestLogRow>();
            var sessionIds   = new List<int>();

            try
            {
                for (int zipIdx = 0; zipIdx < uploads.Count; zipIdx++)
                {
                    var upload = uploads[zipIdx];
                    var tempPath = await SaveUploadToTempFileAsync(upload, HttpContext.RequestAborted);
                    allTempPaths.Add(tempPath);

                    await using var zipStream = new FileStream(
                        tempPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 128 * 1024, useAsync: true);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

                    ExtractMapImagesFromZip(archive, out var detectedSessionId);
                    var sid = (int)(request.SessionIdOverride ?? detectedSessionId ?? 0);
                    if (sid > 0 && !sessionIds.Contains(sid)) sessionIds.Add(sid);

                    var zipRows = ExtractNetworkRowsFromZip(archive, sid);
                    string fileName = Path.GetFileNameWithoutExtension(upload.FileName);
                    foreach (var r in zipRows)
                    {
                        r.FileIndex = zipIdx;
                        r.SourceFileName = fileName;
                    }
                    allRawRows.AddRange(zipRows);
                }

                // Merge & deduplicate all rows across all zips
                var rows = CleanZipRows(allRawRows);

                var filterByImageName = ResolveFilterByImageName(request, Request.HasFormContentType ? Request.Form : null, Request.Query);
                var rowsToSummarize = rows;
                if (filterByImageName)
                {
                    var imageRows = rows.Where(r => RowHasImageName(r)).ToList();
                    if (imageRows.Count > 0) rowsToSummarize = imageRows;
                }

                var validRows = rowsToSummarize
                    .Where(r => !string.IsNullOrWhiteSpace(r.BandSheetName) &&
                                !r.BandSheetName.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) &&
                                !r.BandSheetName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (validRows.Count == 0 && rowsToSummarize.Count > 0)
                {
                    validRows = rowsToSummarize;
                }

                var totalValid  = validRows.Count;
                var bandSummary = validRows
                    .GroupBy(r => r.BandSheetName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        Band       = g.Key,
                        Count      = g.Count(),
                        Percentage = totalValid == 0 ? 0 : Math.Round(g.Count() * 100.0 / totalValid, 2)
                    })
                    .OrderByDescending(x => x.Count)
                    .ToList();

                var bandsByFile = rowsToSummarize
                    .GroupBy(r => new { r.FileIndex, FileName = !string.IsNullOrWhiteSpace(r.SourceFileName) ? r.SourceFileName : $"File {r.FileIndex + 1}" })
                    .OrderBy(g => g.Key.FileIndex)
                    .Select(g =>
                    {
                        var fValidRows = g.Where(r => !string.IsNullOrWhiteSpace(r.BandSheetName) &&
                                                     !r.BandSheetName.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) &&
                                                     !r.BandSheetName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                                          .ToList();
                        if (fValidRows.Count == 0 && g.Count() > 0) fValidRows = g.ToList();

                        var fTotalValid = fValidRows.Count;
                        var fBandSummary = fValidRows
                            .GroupBy(r => r.BandSheetName, StringComparer.OrdinalIgnoreCase)
                            .Select(bg => new
                            {
                                Band       = bg.Key,
                                Count      = bg.Count(),
                                Percentage = fTotalValid == 0 ? 0 : Math.Round(bg.Count() * 100.0 / fTotalValid, 2)
                            })
                            .OrderByDescending(x => x.Count)
                            .ToList();

                        var fSessionId = g.Select(r => r.SessionId).FirstOrDefault(sid => sid > 0);

                        return new
                        {
                            FileIndex      = g.Key.FileIndex,
                            FileName       = g.Key.FileName,
                            SessionId      = fSessionId,
                            TotalRows      = g.Count(),
                            AvailableBands = fBandSummary
                        };
                    })
                    .ToList();

                // SessionId (first, for backward compat) + SessionIds array + FileCount
                var primarySessionId = sessionIds.Count > 0 ? sessionIds[0] : 0;

                return Ok(new
                {
                    SessionId      = primarySessionId,
                    SessionIds     = sessionIds,
                    FileCount      = uploads.Count,
                    TotalRows      = rowsToSummarize.Count,
                    AvailableBands = bandSummary,
                    BandsByFile    = bandsByFile
                });
            }
            finally
            {
                foreach (var p in allTempPaths)
                    try { System.IO.File.Delete(p); } catch { }
            }
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
                var match = Regex.Match(entry.FullName, @"(?:^|[\\/])(?:map_)?(\d+)_([A-Za-z0-9_]+)\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase);
                if (match.Success && long.TryParse(match.Groups[1].Value, out var sid))
                {
                    sessionCounts[sid] = sessionCounts.TryGetValue(sid, out var c) ? c + 1 : 1;
                }
            }

            detectedSessionId = sessionCounts.Count > 0
                ? sessionCounts.OrderByDescending(x => x.Value).First().Key
                : null;

            foreach (var entry in archive.Entries)
            {
                var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                var fileName = Path.GetFileNameWithoutExtension(entry.FullName);
                if (fileName.StartsWith("legend_", StringComparison.OrdinalIgnoreCase))
                {
                    var lHeader = fileName.Substring(7).ToUpperInvariant();
                    try
                    {
                        using var entryStream = entry.Open();
                        using var ms = new MemoryStream();
                        entryStream.CopyTo(ms);
                        var bytes = ms.ToArray();
                        images["LEGEND_" + lHeader] = bytes;
                        if (detectedSessionId.HasValue && detectedSessionId.Value > 0)
                        {
                            images[$"LEGEND_{detectedSessionId.Value}_{lHeader}"] = bytes;
                        }
                    }
                    catch { }
                    continue;
                }

                if (fileName.EndsWith("_legend", StringComparison.OrdinalIgnoreCase))
                {
                    var rawHeader = fileName.Substring(0, fileName.Length - 7);
                    var cleanHeader = Regex.Replace(rawHeader, @"^(?:map_)?(?:\d+_)?", "", RegexOptions.IgnoreCase).ToUpperInvariant();
                    try
                    {
                        using var entryStream = entry.Open();
                        using var ms = new MemoryStream();
                        entryStream.CopyTo(ms);
                        var bytes = ms.ToArray();
                        images["LEGEND_" + cleanHeader] = bytes;
                        if (detectedSessionId.HasValue && detectedSessionId.Value > 0)
                        {
                            images[$"LEGEND_{detectedSessionId.Value}_{cleanHeader}"] = bytes;
                        }
                    }
                    catch { }
                    continue;
                }

                var match = Regex.Match(entry.FullName, @"(?:^|[\\/])(?:map_)?(\d+)_([A-Za-z0-9_]+)\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase);
                string header;
                long? itemSid = null;

                if (match.Success)
                {
                    if (long.TryParse(match.Groups[1].Value, out var sid))
                        itemSid = sid;

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
                    var bytes = ms.ToArray();

                    var sidToUse = itemSid ?? detectedSessionId;
                    if (sidToUse.HasValue && sidToUse.Value > 0)
                    {
                        var sKey = $"{sidToUse.Value}_{header}";
                        images[sKey] = bytes;
                        images[$"{ImageBaseUrl}/{sidToUse.Value}_{header}.png"] = bytes;
                        images[BuildImageUrl((int)sidToUse.Value, header)] = bytes;
                    }

                    // Store global fallback header only if not present yet (first zip wins fallback)
                    if (!images.ContainsKey(header))
                        images[header] = bytes;
                    if (!images.ContainsKey($"MAP_{header}"))
                        images[$"MAP_{header}"] = bytes;
                }
                catch { }
            }

            return images;
        }

        private List<WalkTestLogRow> ExtractNetworkRowsFromZip(ZipArchive archive, long sessionId)
        {
            var rows = new List<WalkTestLogRow>();
            var nextId = 1;

            var csvEntries = archive.Entries
                .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .Where(e => {
                    var fn = Path.GetFileName(e.FullName);
                    if (fn.StartsWith("ColorSettings", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("SiteSummary", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("sites", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("Event", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("L3", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    return fn.StartsWith("NetworkLog", StringComparison.OrdinalIgnoreCase) ||
                           fn.StartsWith("log", StringComparison.OrdinalIgnoreCase) ||
                           fn.Contains("network", StringComparison.OrdinalIgnoreCase);
                })
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
                PrimaryCellInfo = -1, ImageName = -1;
        }

        private static ZipColumnMap BuildZipColumnMap(List<string> headers)
        {
            return new ZipColumnMap
            {
                Timestamp = FindZipColumn(headers, "timestamp"),
                Lat = FindZipColumn(headers, "latitude"),
                Lon = FindZipColumn(headers, "longitude"),
                Network = FindZipColumn(headers, "network type", "network_type", "networktype", "network", "technology", "tech", "rat", "system", "mode"),
                IndoorOutdoor = FindZipColumn(headers, "indoor/outdoor", "indoor_outdoor", "location_type"),
                Mos = FindZipColumn(headers, "mos"),
                CellId = FindZipColumn(headers, "cell id", "cell_id", "cellid", "ci", "cid", "cell identity", "cell_index"),
                Pci = FindZipColumn(headers, "pci / psc", "pci", "psc"),
                Rsrp = FindZipColumn(headers, "ssrsrp", "rsrp"),
                Rsrq = FindZipColumn(headers, "ssrsrq", "rsrq"),
                Sinr = FindZipColumn(headers, "rxqual", "sinr"),
                DlTpt = FindZipColumn(headers, "dl thpt", "dl_tpt", "dl_throughput", "dl throughput"),
                UlTpt = FindZipColumn(headers, "ul thpt", "ul_tpt", "ul_throughput", "ul throughput"),
                Earfcn = FindZipColumn(headers, "nr_earfcn", "nrfcn", "nrarfcn", "nr_arfcn", "arfcn", "dl_earfcn", "dl_frequency", "earfcn", "frequency", "freq"),
                VolteCall = FindZipColumn(headers, "volte call", "volte_call", "volte"),
                Band = FindZipColumn(headers, "primary_band", "primary band", "freq_band", "freq band", "frequency_band", "frequency band", "serving_band", "serving band", "lte_band", "nr_band", "5g_band", "operating_band", "working_band", "band"),
                Bler = FindZipColumn(headers, "bler"),
                AlphaLong = FindZipColumn(headers, "alpha long", "m_alpha_long"),
                AlphaShort = FindZipColumn(headers, "alpha short", "m_alpha_short"),
                NodebId = FindZipColumn(headers,
                    "nodeb id", "nodeb_id", "node_b_id", "nodeb", "node_b",
                    "enodeb id", "enodeb_id", "enodebid", "enodeb",
                    "enb id", "enb_id", "enbid", "enb",
                    "gnodeb id", "gnodeb_id", "gnodebid", "gnodeb",
                    "gnb id", "gnb_id", "gnbid", "gnb",
                    "site id", "site_id", "siteid"),
                Apps = FindZipColumn(headers, "running apps", "apps", "app_name"),
                PuschTx = FindZipColumn(headers, "pusch tx", "pusch_tx", "pusch"),
                Ta = FindZipColumn(headers, "ta"),
                Cqi = FindZipColumn(headers, "cqi"),
                Level = FindZipColumn(headers, "level"),
                Primary = FindZipColumnByName(headers, "primary"),
                PrimaryCellInfo = FindZipColumnByName(headers, "cellinfo_1", "primary_cell_info_1", "primary_cell_info", "cell_info", "cellinfo"),
                ImageName = FindZipColumn(headers, "image name", "image_name")
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

        private static Dictionary<string, string>? ParseImageNameColors(string? imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName)) return null;

            var parts = imageName.Split("@@", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                var match = Regex.Match(part, @"^(.*?)\((#[0-9A-Fa-f]{6}|#[0-9A-Fa-f]{8})\)$");
                if (match.Success)
                {
                    var metricName = match.Groups[1].Value.Trim();
                    var colorHex = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(metricName) && !string.IsNullOrEmpty(colorHex))
                    {
                        dict[metricName] = colorHex;
                    }
                }
            }

            return dict.Count > 0 ? dict : null;
        }

        private static string? GetHexColorFromRows(List<WalkTestLogRow> rows, string headerUpper)
        {
            if (rows == null || rows.Count == 0) return null;

            string[] metricKeys = (headerUpper ?? "").ToUpperInvariant().Trim() switch
            {
                "PCI" => new[] { "PCI" },
                "NODEB_ID" or "NODEB" => new[] { "NodeB ID", "NodeB_ID", "NODEB_ID", "NodeB" },
                "CELL_ID" or "CI" or "CELLID" or "CID" => new[] { "CI", "Cell ID", "CELL_ID", "CellID" },
                "EARFCN" => new[] { "EARFCN" },
                "RSRP" => new[] { "RSRP" },
                "RSRQ" => new[] { "RSRQ" },
                "SINR" => new[] { "SINR" },
                "DL_THPT" => new[] { "DL THPT", "DL_THPT" },
                "UL_THPT" => new[] { "UL THPT", "UL_THPT" },
                "PUSCH_TX" => new[] { "PUSCH Tx", "PUSCH_TX" },
                "BLER" or "LTE_BLER" => new[] { "LTE BLER", "BLER" },
                "VOLTE" or "VOLTE_CALL" => new[] { "VoLTE Call", "VOLTE" },
                _ => new[] { headerUpper ?? "" }
            };

            foreach (var r in rows)
            {
                if (r.MetricColors == null) continue;
                foreach (var key in metricKeys)
                {
                    if (r.MetricColors.TryGetValue(key, out var colorHex) && !string.IsNullOrWhiteSpace(colorHex))
                    {
                        return colorHex.Trim();
                    }
                }
            }

            return null;
        }

        private WalkTestLogRow? ParseZipRow(List<string> cols, ZipColumnMap map, long sessionId, ref int nextId)
        {
            var tsRaw = GetZipCol(cols, map.Timestamp);
            if (!string.IsNullOrWhiteSpace(tsRaw) && tsRaw.Contains("@@"))
                return null; // Discard legend/metadata header rows with shifted columns

            DateTime ts;
            if (!DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
            {
                // Handle relative timestamp formats like "48:48.9" (mm:ss.s or hh:mm:ss)
                var relMatch = Regex.Match(tsRaw ?? "", @"^(\d+):(\d+)(?:\.(\d+))?$");
                if (relMatch.Success)
                {
                    int part1 = int.Parse(relMatch.Groups[1].Value);
                    int part2 = int.Parse(relMatch.Groups[2].Value);
                    // If part1 >= 24, treat as mm:ss else hh:mm
                    ts = part1 >= 24
                        ? DateTime.Today.AddMinutes(part1).AddSeconds(part2)
                        : DateTime.Today.AddHours(part1).AddMinutes(part2);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(tsRaw))
                    {
                        ts = DateTime.Today.AddSeconds(nextId);
                    }
                    else
                    {
                        return null; // Discard corrupted/shifted CSV rows with unparseable timestamp text
                    }
                }
            }

            if (!IsZipPrimaryRegisteredRow(cols, map))
                return null;

            var provider = GetZipCol(cols, map.AlphaShort);
            if (string.IsNullOrWhiteSpace(provider)) provider = GetZipCol(cols, map.AlphaLong);

            var band = GetZipCol(cols, map.Band);
            var network = GetZipCol(cols, map.Network);
            var earfcn = GetZipCol(cols, map.Earfcn);
            var primaryCellInfo = GetZipCol(cols, map.PrimaryCellInfo);
            var primary = GetZipCol(cols, map.Primary);

            var imageNameRaw = GetZipCol(cols, map.ImageName);
            var metricColors = ParseImageNameColors(imageNameRaw);

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
                BandSheetName = ToBandSheetName(band, network, earfcn, string.IsNullOrWhiteSpace(primaryCellInfo) ? primary : primaryCellInfo),
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
                Primary = GetZipCol(cols, map.Primary),
                RawImageName = NormalizeZipText(imageNameRaw),
                MetricColors = metricColors
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

                // Re-resolve BandSheetName after Band/Network have been normalised
                // (NormalizeZipText may have turned "Unknown" → null, changing the result)
                r.BandSheetName = ToBandSheetName(r.Band, r.Network, r.Earfcn, r.Primary);

                if (string.IsNullOrWhiteSpace(r.NodeBId) && !string.IsNullOrWhiteSpace(r.CellId))
                {
                    var digits = Regex.Match(r.CellId, @"\d+").Value;
                    if (long.TryParse(digits, out var cidVal) && cidVal > 256)
                    {
                        r.NodeBId = (cidVal >> 8).ToString();
                    }
                }

                if (r.Lat is < -90 or > 90) r.Lat = null;
                if (r.Lon is < -180 or > 180) r.Lon = null;
                if (r.Lat == 0 && r.Lon == 0) { r.Lat = null; r.Lon = null; }

                var dedupeKey = string.Join('|',
                    r.SessionId, r.Timestamp?.Ticks, r.Pci, r.Rsrp, r.Rsrq, r.Band, r.Lat, r.Lon, r.RawImageName, r.Id);
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
                ApplyZipMetricRanges(rangesByMetric, "PCI", ranges => thresholds.Pci = ranges);
                ApplyZipMetricRanges(rangesByMetric, "NODEB_ID", ranges => thresholds.NodeBId = ranges);
                ApplyZipMetricRanges(rangesByMetric, "NODEB", ranges => thresholds.NodeBId = ranges);
                ApplyZipMetricRanges(rangesByMetric, "CELL_ID", ranges => thresholds.CellId = ranges);
                ApplyZipMetricRanges(rangesByMetric, "CI", ranges => thresholds.CellId = ranges);

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
            // Only fetch map plot images over HTTP (legends are created dynamically by the backend)
            var urls = sessionIds
                .SelectMany(sessionId => ImageHeaders.Select(header => BuildImageUrl(sessionId, header)))
                .Distinct()
                .ToList();

            var result = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            var client = _httpClientFactory.CreateClient("WalkTestReportImages");
            client.Timeout = TimeSpan.FromSeconds(3); // Fast 3s timeout for non-existent image checks

            using var throttle = new SemaphoreSlim(32); // High concurrency for fast parallel downloads

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

            var resultRows = await query
                .OrderBy(x => x.id)
                .Take(limit)
                .Select(x => new WalkTestLogRow
                {
                    Id = (int)x.id,
                    SessionId = x.session_id ?? 0,
                    Timestamp = x.timestamp,
                    Lat = x.lat,
                    Lon = x.lon,
                    IndoorOutdoor = x.indoor_outdoor,
                    Network = x.network,
                    Provider = x.m_alpha_short ?? x.m_alpha_long,
                    Band = x.band,
                    BandSheetName = string.IsNullOrWhiteSpace(x.band) ? "Band" : x.band,
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
                    Primary = x.primary,
                    RawImageName = x.image_path,
                    MetricColors = ParseImageNameColors(x.image_path)
                })
                .ToListAsync(HttpContext.RequestAborted);

            foreach (var row in resultRows)
            {
                if (string.IsNullOrWhiteSpace(row.Ta))
                    row.Ta = ExtractPuschTxFromPrimaryCellInfo(row.Primary);

                if (string.IsNullOrWhiteSpace(row.NodeBId) && !string.IsNullOrWhiteSpace(row.CellId))
                {
                    var digits = Regex.Match(row.CellId, @"\d+").Value;
                    if (long.TryParse(digits, out var cidVal) && cidVal > 256)
                    {
                        row.NodeBId = (cidVal >> 8).ToString();
                    }
                }

                row.BandSheetName = ToBandSheetName(row.Band, row.Network, row.Earfcn, row.Primary);
            }

            return resultRows;
        }

        private static string? ExtractPuschTxFromPrimaryCellInfo(string? primaryCellInfo)
        {
            if (string.IsNullOrWhiteSpace(primaryCellInfo)) return null;

            var match = Regex.Match(primaryCellInfo, @"(?:mPuschTx|pusch_tx|mTxPower|txPower)\s*=\s*(-?\d+(\.\d+)?)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private async Task<List<WalkTestSiteSummaryRow>> QuerySiteSummaryRowsAsync(int projectId)
        {
            var optimizedRows = await QuerySiteSummaryRowsRawAsync(
                projectId,
                isOptimized: true,
                excludedSourceIds: null);

            var optimizedSourceIds = optimizedRows
                .Select(x => x.SourceId)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            var baseRows = await QuerySiteSummaryRowsRawAsync(
                projectId,
                isOptimized: false,
                excludedSourceIds: optimizedSourceIds);

            return optimizedRows
                .Concat(baseRows)
                .OrderBy(x => x.Site ?? int.MaxValue)
                .ThenBy(x => x.Sector)
                .ThenBy(x => x.CellId ?? int.MaxValue)
                .ToList();
        }

        private async Task<List<WalkTestSiteSummaryRow>> QuerySiteSummaryRowsRawAsync(
            int projectId,
            bool isOptimized,
            IReadOnlyCollection<int>? excludedSourceIds)
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(HttpContext.RequestAborted);

            await using var command = conn.CreateCommand();
            var table = isOptimized ? "site_prediction_optimized" : "site_prediction";
            var sourceIdColumn = isOptimized ? "site_prediction_id" : "id";
            var versionSelect = isOptimized ? "version" : "NULL AS version";
            var statusSelect = isOptimized ? "status" : "NULL AS status";
            var secIdSelect = isOptimized ? "sec_id" : "NULL AS sec_id";
            var orderBy = isOptimized
                ? "ORDER BY COALESCE(updated_at, created_at) DESC, id"
                : "ORDER BY site, sector, cell_id";

            var excludeClause = "";
            if (!isOptimized && excludedSourceIds?.Count > 0)
            {
                var names = new List<string>();
                var index = 0;
                foreach (var id in excludedSourceIds)
                {
                    var name = $"@excluded{index++}";
                    names.Add(name);
                    AddCommandParameter(command, name, id);
                }
                excludeClause = $" AND id NOT IN ({string.Join(",", names)})";
            }

            command.CommandText = $@"
                SELECT
                    {sourceIdColumn} AS source_id,
                    {versionSelect},
                    {statusSelect},
                    site, site_name, sector, cell_id, {secIdSelect},
                    latitude, longitude, tac, pci, azimuth, height, band, earfcn, bw,
                    m_tilt, e_tilt, tx_power, reference_signal_power, frequency, cluster, technology
                FROM {table}
                WHERE tbl_project_id = @projectId{excludeClause}
                {orderBy};";
            AddCommandParameter(command, "@projectId", projectId);

            var rows = new List<WalkTestSiteSummaryRow>();
            await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                rows.Add(new WalkTestSiteSummaryRow
                {
                    Source = isOptimized ? "Optimized" : "Original",
                    SourceId = ReadReportInt(reader, "source_id") ?? 0,
                    Version = ReadReportInt(reader, "version"),
                    Status = ReadReportString(reader, "status"),
                    Site = ReadReportInt(reader, "site"),
                    SiteName = ReadReportInt(reader, "site_name"),
                    Sector = ReadReportString(reader, "sector"),
                    CellId = ReadReportInt(reader, "cell_id"),
                    SecId = ReadReportInt(reader, "sec_id"),
                    Latitude = ReadReportDouble(reader, "latitude"),
                    Longitude = ReadReportDouble(reader, "longitude"),
                    Tac = ReadReportInt(reader, "tac"),
                    Pci = ReadReportInt(reader, "pci"),
                    Azimuth = ReadReportInt(reader, "azimuth"),
                    Height = ReadReportInt(reader, "height"),
                    Band = ReadReportInt(reader, "band"),
                    Earfcn = ReadReportInt(reader, "earfcn"),
                    Bw = ReadReportInt(reader, "bw"),
                    MTilt = ReadReportInt(reader, "m_tilt"),
                    ETilt = ReadReportInt(reader, "e_tilt"),
                    TxPower = ReadReportDouble(reader, "tx_power"),
                    ReferenceSignalPower = ReadReportDouble(reader, "reference_signal_power"),
                    Frequency = ReadReportString(reader, "frequency"),
                    Cluster = ReadReportString(reader, "cluster"),
                    Technology = ReadReportString(reader, "technology")
                });
            }

            return rows;
        }

        private static void AddCommandParameter(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static string? ReadReportString(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal)
                ? null
                : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static int? ReadReportInt(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal)) return null;

            var value = reader.GetValue(ordinal);
            if (value is int i) return i;
            if (value is long l) return l > int.MaxValue || l < int.MinValue ? null : (int)l;
            if (value is decimal dec) return dec > int.MaxValue || dec < int.MinValue ? null : (int)dec;
            if (value is double dbl) return dbl > int.MaxValue || dbl < int.MinValue ? null : (int)dbl;

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) &&
                   parsedDouble <= int.MaxValue &&
                   parsedDouble >= int.MinValue
                ? (int)parsedDouble
                : null;
        }

        private static double? ReadReportDouble(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal)) return null;

            var value = reader.GetValue(ordinal);
            if (value is double d) return d;
            if (value is float f) return f;
            if (value is decimal dec) return (double)dec;

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static List<(string DisplayTitle, string BandSheetName, List<WalkTestLogRow> BandRows)> CreateBandBlocks(List<WalkTestLogRow> rows)
        {
            var blocks = new List<(string DisplayTitle, string BandSheetName, List<WalkTestLogRow> BandRows)>();
            var validRows = rows
                .Where(x => !string.IsNullOrWhiteSpace(x.BandSheetName) &&
                            !x.BandSheetName.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) &&
                            !x.BandSheetName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                .ToList();

            bool hasMultipleFiles = validRows.Select(x => x.FileIndex).Distinct().Count() > 1;

            if (hasMultipleFiles)
            {
                var groupedByFileAndBand = validRows
                    .GroupBy(x => new { x.FileIndex, SourceFileName = !string.IsNullOrWhiteSpace(x.SourceFileName) ? x.SourceFileName : $"Log {x.FileIndex + 1}", x.BandSheetName })
                    .OrderBy(g => g.Key.FileIndex)
                    .ThenBy(g => BandSortKey(g.Key.BandSheetName), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var g in groupedByFileAndBand)
                {
                    string displayTitle = $"{g.Key.BandSheetName} ({g.Key.SourceFileName})";
                    blocks.Add((displayTitle, g.Key.BandSheetName, g.ToList()));
                }
            }
            else
            {
                var groupedByBand = validRows
                    .GroupBy(x => x.BandSheetName)
                    .OrderBy(g => BandSortKey(g.Key), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var g in groupedByBand)
                {
                    blocks.Add((g.Key, g.Key, g.ToList()));
                }
            }

            return blocks;
        }

        private static XlsxWorkbook BuildWorkbook(
            string projectName,
            List<int> sessionIds,
            List<WalkTestLogRow> rows,
            List<WalkTestSiteSummaryRow> siteRows,
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            ReportThresholdConfig thresholds,
            string reportMode = "separate",
            bool filterByImageName = true,
            bool showSampleCount = false,
            List<string>? customSheetNames = null)
        {
            var workbook = new XlsxWorkbook();
            workbook.Sheets.Add(BuildSiteSummarySheet(projectName, sessionIds, rows, siteRows));

            var bandGroups = rows
                .Where(x => !string.IsNullOrWhiteSpace(x.BandSheetName) &&
                            !x.BandSheetName.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) &&
                            !x.BandSheetName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.BandSheetName)
                .OrderBy(x => BandSortKey(x.Key), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.Equals(reportMode, "combined", StringComparison.OrdinalIgnoreCase))
            {
                var fileGroupIds = rows
                    .Select(x => x.FileGroup)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                if (fileGroupIds.Count > 1)
                {
                    for (int idx = 0; idx < fileGroupIds.Count; idx++)
                    {
                        int fg = fileGroupIds[idx];
                        var fgRows = rows.Where(x => x.FileGroup == fg).ToList();
                        var fgBandBlocks = CreateBandBlocks(fgRows);

                        string sheetTitle;
                        if (customSheetNames != null && idx < customSheetNames.Count && !string.IsNullOrWhiteSpace(customSheetNames[idx]))
                        {
                            sheetTitle = SanitizeExcelSheetName(customSheetNames[idx]);
                        }
                        else
                        {
                            sheetTitle = $"Combined Group {fg}";
                        }

                        workbook.Sheets.Add(BuildCombinedBandSheet(
                            fgBandBlocks, fgRows, imageBytesByUrl, thresholds,
                            filterByImageName, showSampleCount, sheetTitle));
                    }
                }
                else
                {
                    var bandBlocks = CreateBandBlocks(rows);

                    string sheetTitle = (customSheetNames != null && customSheetNames.Count > 0 && !string.IsNullOrWhiteSpace(customSheetNames[0]))
                        ? SanitizeExcelSheetName(customSheetNames[0])
                        : "Combined";

                    workbook.Sheets.Add(BuildCombinedBandSheet(
                        bandBlocks, rows, imageBytesByUrl, thresholds,
                        filterByImageName, showSampleCount, sheetTitle));
                }
            }
            else
            {
                foreach (var group in bandGroups)
                    workbook.Sheets.Add(BuildBandSheet(group.Key, group.ToList(), rows, imageBytesByUrl, thresholds, filterByImageName, showSampleCount));
            }

            return workbook;
        }

        private static string ResolveReportMode(ZipReportUploadRequest request, IFormCollection? form)
        {
            string? raw = null;
            if (form != null)
            {
                if (form.TryGetValue("ReportMode", out var v1) && !string.IsNullOrWhiteSpace(v1)) raw = v1.ToString();
                else if (form.TryGetValue("SheetMode", out var v2) && !string.IsNullOrWhiteSpace(v2)) raw = v2.ToString();
                else if (form.TryGetValue("Mode", out var v3) && !string.IsNullOrWhiteSpace(v3)) raw = v3.ToString();
                else if (form.TryGetValue("ReportType", out var v4) && !string.IsNullOrWhiteSpace(v4)) raw = v4.ToString();
                else if (form.TryGetValue("Combined", out var v5) && !string.IsNullOrWhiteSpace(v5)) raw = "combined";
                else if (form.TryGetValue("Seperate", out var v6) && !string.IsNullOrWhiteSpace(v6)) raw = "separate";
                else if (form.TryGetValue("Separate", out var v7) && !string.IsNullOrWhiteSpace(v7)) raw = "separate";
            }

            if (!string.IsNullOrWhiteSpace(raw) && raw.Trim().StartsWith("comb", StringComparison.OrdinalIgnoreCase))
            {
                return "combined";
            }

            return "separate";
        }

        private static List<IFormFile> ResolveUploadedFiles(ZipReportUploadRequest request, HttpRequest? httpRequest = null)
        {
            var files = new List<IFormFile>();
            if (httpRequest != null && httpRequest.HasFormContentType && httpRequest.Form.Files.Count > 0)
            {
                foreach (var f in httpRequest.Form.Files)
                {
                    if (f != null && f.Length > 0 && !files.Contains(f))
                        files.Add(f);
                }
            }

            if (files.Count == 0)
            {
                if (request.LogZips?.Count > 0)
                    files.AddRange(request.LogZips.Where(f => f != null && f.Length > 0));
                if (request.LogZip != null && request.LogZip.Length > 0 && !files.Contains(request.LogZip))
                    files.Add(request.LogZip);
            }
            return files;
        }

        private static List<IFormFile> ResolveUploadedFilesForDiscovery(ZipBandDiscoveryRequest request, HttpRequest? httpRequest = null)
        {
            var files = new List<IFormFile>();
            if (httpRequest != null && httpRequest.HasFormContentType && httpRequest.Form.Files.Count > 0)
            {
                foreach (var f in httpRequest.Form.Files)
                {
                    if (f != null && f.Length > 0 && !files.Contains(f))
                        files.Add(f);
                }
            }

            if (files.Count == 0)
            {
                if (request.LogZips?.Count > 0)
                    files.AddRange(request.LogZips.Where(f => f != null && f.Length > 0));
                if (request.LogZip != null && request.LogZip.Length > 0 && !files.Contains(request.LogZip))
                    files.Add(request.LogZip);
            }
            return files;
        }

        private static bool ResolveShowSampleCount(ZipReportUploadRequest request, IFormCollection? form)
        {
            if (request.ShowSampleCount.HasValue) return request.ShowSampleCount.Value;
            if (form != null && form.TryGetValue("ShowSampleCount", out var v))
            {
                var s = v.ToString().Trim().ToLowerInvariant();
                return s == "true" || s == "1" || s == "yes";
            }
            return false;
        }

        private static string SanitizeExcelSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Combined";
            var clean = Regex.Replace(name.Trim(), @"[\\/\?\*::\[\]]", "").Trim();
            if (string.IsNullOrWhiteSpace(clean)) return "Combined";
            return clean.Length <= 31 ? clean : clean[..31];
        }

        private static List<string> ResolveCustomSheetNames(IFormCollection? form, IQueryCollection? query = null)
        {
            var sheetNames = new List<string>();
            var keysToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SheetNames", "CustomSheetNames", "SheetName", "SheetTitles", "SheetTitle", "SheetLabels", "SheetLabel"
            };

            if (form != null)
            {
                foreach (var k in form.Keys)
                {
                    var normalized = k.Replace("_", "").Replace("-", "");
                    if (keysToMatch.Contains(normalized) || keysToMatch.Contains(k))
                    {
                        var values = form[k].ToArray();
                        foreach (var v in values)
                        {
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                var parts = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var p in parts)
                                {
                                    var cleaned = p.Trim().Trim('[', ']', '"', '\'');
                                    if (!string.IsNullOrWhiteSpace(cleaned))
                                        sheetNames.Add(cleaned);
                                }
                            }
                        }
                    }
                }
            }

            if (sheetNames.Count == 0 && query != null)
            {
                foreach (var k in query.Keys)
                {
                    var normalized = k.Replace("_", "").Replace("-", "");
                    if (keysToMatch.Contains(normalized) || keysToMatch.Contains(k))
                    {
                        var values = query[k].ToArray();
                        foreach (var v in values)
                        {
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                var parts = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var p in parts)
                                {
                                    var cleaned = p.Trim().Trim('[', ']', '"', '\'');
                                    if (!string.IsNullOrWhiteSpace(cleaned))
                                        sheetNames.Add(cleaned);
                                }
                            }
                        }
                    }
                }
            }

            return sheetNames;
        }

        private static List<int> ResolveLogFileGroups(int fileCount, IFormCollection? form, IQueryCollection? query = null)
        {
            var groups = new List<int>();
            var keysToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LogFileGroups", "FileGroups", "GroupAssignments", "FileGroup", "FileGroupMap", "LogGroups", "Groups"
            };

            var rawTokens = new List<string>();

            if (form != null)
            {
                foreach (var k in form.Keys)
                {
                    var normalized = k.Replace("_", "").Replace("-", "");
                    if (keysToMatch.Contains(normalized) || keysToMatch.Contains(k))
                    {
                        var values = form[k].ToArray();
                        foreach (var v in values)
                        {
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                var parts = v.Split(new[] { ',', ';', '[', ']', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                rawTokens.AddRange(parts);
                            }
                        }
                    }
                }
            }

            if (rawTokens.Count == 0 && query != null)
            {
                foreach (var k in query.Keys)
                {
                    var normalized = k.Replace("_", "").Replace("-", "");
                    if (keysToMatch.Contains(normalized) || keysToMatch.Contains(k))
                    {
                        var values = query[k].ToArray();
                        foreach (var v in values)
                        {
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                var parts = v.Split(new[] { ',', ';', '[', ']', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                rawTokens.AddRange(parts);
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < fileCount; i++)
            {
                if (i < rawTokens.Count && int.TryParse(rawTokens[i].Trim(), out int parsedGroup) && parsedGroup > 0)
                {
                    groups.Add(parsedGroup);
                }
                else
                {
                    groups.Add(1);
                }
            }

            return groups;
        }

        private static bool ResolveFilterByImageName(object? request, IFormCollection? form, IQueryCollection? query = null)
        {
            var keysToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FilterByImageName", "UseImageNameFilter", "OnlyWithImage", "ImageFilter", "WithImageName", "ImageNameFilter", "ByImage", "ImageOnly"
            };

            // 1. Check Query string parameters
            if (query != null)
            {
                foreach (var qKey in query.Keys)
                {
                    var normalized = qKey.Replace("_", "").Replace("-", "");
                    if (keysToMatch.Contains(normalized) || keysToMatch.Contains(qKey))
                    {
                        var v = query[qKey].ToString().Trim().ToLowerInvariant();
                        if (v == "false" || v == "0" || v == "no" || v == "without_image" || v == "withoutimage" || v == "all" || v == "off") return false;
                        if (v == "true" || v == "1" || v == "yes" || v == "with_image" || v == "withimage" || v == "only_image" || v == "on") return true;
                    }
                }
            }

            // 2. Check Form collection parameters
            if (form != null)
            {
                foreach (var fKey in form.Keys)
                {
                    var normalized = fKey.Replace("_", "").Replace("-", "");
                    if (keysToMatch.Contains(normalized) || keysToMatch.Contains(fKey))
                    {
                        var v = form[fKey].ToString().Trim().ToLowerInvariant();
                        if (v == "false" || v == "0" || v == "no" || v == "without_image" || v == "withoutimage" || v == "all" || v == "off") return false;
                        if (v == "true" || v == "1" || v == "yes" || v == "with_image" || v == "withimage" || v == "only_image" || v == "on") return true;
                    }
                }
            }

            // 3. Check request model properties
            if (request is WalkTestExcelReportRequest req)
            {
                if (req.FilterByImageName.HasValue) return req.FilterByImageName.Value;
                if (!string.IsNullOrWhiteSpace(req.ImageFilter))
                {
                    var v = req.ImageFilter.Trim().ToLowerInvariant();
                    if (v == "false" || v == "0" || v == "no" || v == "without_image" || v == "withoutimage" || v == "all" || v == "off") return false;
                    if (v == "true" || v == "1" || v == "yes" || v == "with_image" || v == "withimage" || v == "only_image" || v == "on") return true;
                }
            }

            return true;
        }

        private static XlsxSheet BuildCombinedBandSheet(
            List<(string DisplayTitle, string BandSheetName, List<WalkTestLogRow> BandRows)> bandBlocks,
            List<WalkTestLogRow> allRows,
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            ReportThresholdConfig thresholds,
            bool filterByImageName = true,
            bool showSampleCount = false,
            string sheetName = "Combined")
        {
            int numBands = bandBlocks.Count;
            int totalCols = Math.Max(15, numBands * 9);
            var columnWidths = new double[totalCols];
            for (int c = 0; c < totalCols; c++)
                columnWidths[c] = (c % 9 >= 6) ? 3.5 : 13.5;

            var safeSheetName = string.IsNullOrWhiteSpace(sheetName) ? "Combined" : sheetName.Trim();
            if (safeSheetName.Length > 31) safeSheetName = safeSheetName[..31];

            var sheet = new XlsxSheet(safeSheetName) { ColumnWidths = columnWidths, FreezeTopRow = false };

            const int cellSpan = 6; // Block 1: A-F (cols 0-5), Block 2: J-O (cols 9-14)...
            const int maxWidthEmu = 3_619_500; // ~380 px wide

            var bandDataList = new List<(string DisplayTitle, string BandName, List<WalkTestLogRow> BandRows, int PrimarySessionId, List<(string Header, byte[] FinalBytes, (int WidthEmu, int HeightEmu) Size)> Plots)>();

            foreach (var block in bandBlocks)
            {
                var displayTitle = block.DisplayTitle;
                var bandName = block.BandSheetName;
                var bandRows = block.BandRows;
                var primarySessionId = bandRows.Select(x => x.SessionId).FirstOrDefault(x => x > 0);

                var plotSlots = new (string Header, byte[] FinalBytes, (int WidthEmu, int HeightEmu) Size)?[ImageHeaders.Length];

                Parallel.For(0, ImageHeaders.Length, hIdx =>
                {
                    var header = ImageHeaders[hIdx];
                    var mapRawBytes = TryResolveMapImage(imageBytesByUrl, primarySessionId, header, bandName);
                    if (mapRawBytes != null && mapRawBytes.Length > 0)
                    {
                        var legendBytes = TryResolveLegendPhoto(imageBytesByUrl, primarySessionId, header, bandRows, thresholds, filterByImageName, showSampleCount);
                        var finalBytes  = OverlayLegendOnMap(mapRawBytes, legendBytes);
                        var size        = ScaleToEmu(ReadPngSizePx(finalBytes), maxWidthEmu);
                        plotSlots[hIdx] = (header, finalBytes, size);
                    }
                });

                var plots = plotSlots
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
                    .ToList();

                if (plots.Count > 0)
                {
                    bandDataList.Add((displayTitle, bandName, bandRows, primarySessionId, plots));
                }
            }

            if (bandDataList.Count == 0) return sheet;

            int activeBands = bandDataList.Count;

            // 1. Band Banner Row
            var headerRow = new XlsxRow(28);
            for (int c = 0; c < totalCols; c++) headerRow.Cells.Add(XlsxCell.Text(""));

            for (int b = 0; b < activeBands; b++)
            {
                int colStart = b * 9;
                headerRow.Cells[colStart] = XlsxCell.Text($"Band: {bandDataList[b].DisplayTitle}", 4);
            }
            sheet.Rows.Add(headerRow);
            sheet.Rows.Add(XlsxRow.Blank());

            // 2. Maximum number of plots among all active bands
            int maxPlots = bandDataList.Max(b => b.Plots.Count);

            for (int p = 0; p < maxPlots; p++)
            {
                // Title Row for plot p across all bands
                var titleRow = new XlsxRow(22);
                for (int c = 0; c < totalCols; c++) titleRow.Cells.Add(XlsxCell.Text(""));

                double maxRowHeight = 220.0;

                for (int b = 0; b < activeBands; b++)
                {
                    var plots = bandDataList[b].Plots;
                    if (p < plots.Count)
                    {
                        int colStart = b * 9;
                        var plot = plots[p];
                        titleRow.Cells[colStart] = XlsxCell.Text($"{bandDataList[b].DisplayTitle} - {plot.Header} Plot", 4);

                        double heightPts = plot.Size.HeightEmu / 12700.0;
                        if (heightPts > maxRowHeight) maxRowHeight = heightPts;
                    }
                }
                sheet.Rows.Add(titleRow);

                if (maxRowHeight > 550.0) maxRowHeight = 550.0;
                if (maxRowHeight < 220.0) maxRowHeight = 220.0;

                // Image Row for plot p across all bands
                int imageRowIdx = sheet.Rows.Count;
                var imageRow = new XlsxRow(maxRowHeight);
                for (int c = 0; c < totalCols; c++) imageRow.Cells.Add(XlsxCell.Text(""));
                sheet.Rows.Add(imageRow);

                for (int b = 0; b < activeBands; b++)
                {
                    var plots = bandDataList[b].Plots;
                    if (p < plots.Count)
                    {
                        int colStart = b * 9;
                        var plot = plots[p];
                        sheet.Images.Add(new XlsxImage(imageRowIdx, colStart, plot.FinalBytes,
                            plot.Size.WidthEmu, plot.Size.HeightEmu, cellSpanCols: cellSpan));
                    }
                }

                // Blank spacer rows between KPI plots
                sheet.Rows.Add(XlsxRow.Blank());
                sheet.Rows.Add(XlsxRow.Blank());
            }

            return sheet;
        }

        private static string ExtractWalkTestDate(List<WalkTestLogRow> rows)
        {
            foreach (var r in rows)
            {
                if (r.Timestamp.HasValue)
                {
                    return r.Timestamp.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                }
            }

            return DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        private static string ExtractEnodeBandsFormatted(List<WalkTestLogRow> rows)
        {
            var bands = rows
                .Select(x => x.BandSheetName)
                .Where(b => !string.IsNullOrWhiteSpace(b) &&
                            !b.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) &&
                            !b.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(b => BandSortKey(b))
                .ToList();

            if (bands.Count == 0) return "XXX";

            return string.Join(" & ", bands);
        }

        private static XlsxSheet BuildSiteSummarySheet(
            string projectName,
            List<int> sessionIds,
            List<WalkTestLogRow> rows,
            List<WalkTestSiteSummaryRow> siteRows)
        {
            var sheet = new XlsxSheet("Site Summary")
            {
                ColumnWidths = new double[] { 50, 30 },
                FreezeTopRow = false
            };

            sheet.Rows.Add(XlsxRow.Title($"Walk Test Excel Report - {projectName}", 22));
            sheet.Rows.Add(XlsxRow.FromText("Generated On", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sheet.Rows.Add(XlsxRow.FromText("Total Log Samples", rows.Count.ToString("N0", CultureInfo.InvariantCulture)));
            sheet.Rows.Add(XlsxRow.FromText("Band Sheets", string.Join(", ", rows.Select(x => x.BandSheetName).Distinct().OrderBy(x => x))));
            sheet.Rows.Add(XlsxRow.Blank());

            var walkTestDate = ExtractWalkTestDate(rows);
            var enodeBands = ExtractEnodeBandsFormatted(rows);

            var metaFields = new (string FieldName, string Value)[]
            {
                ("Circle", "XXX"),
                ("Name of Infra Provider/TOCO", "XXX"),
                ("Site Name", "XXX"),
                ("Site Address", "XXX"),
                ("City/Town", "XXX"),
                ("Site Lat", "XXX"),
                ("Site Long", "XXX"),
                ("Operator Site ID", "XXX"),
                ("Operator Site ID", "XXX"),
                ("TOCO Site ID", "XXX"),
                ("No. of Sectors", "XXX"),
                ("Total No of Floors in the Building", "XXX"),
                ("No. of Floors covered by IBS", "XXX"),
                ("IBS Type (Full, Partial, Skeletal)", "XXX"),
                ("Date of Installation", "XXX"),
                ("Date of I&C", "XXX"),
                ("eNode Band for Node B(900/1800/2100/2300/3500)", enodeBands),
                ("Max. Output Power", "XXX"),
                ("Site Type", "XXX"),
                ("DAS MIMO Configuration", "XXX"),
                ("MEDIA TYPE", "XXX"),
                ("Anchor Operator", "XXX"),
                ("Sharing Opcos", "XXX"),
                ("Sites Layout Attached", "XXX"),
                ("Acceptance KPI Attached", "XXX"),
                ("Area - sqft", "XXX"),
                ("Walk Test Date", walkTestDate)
            };

            foreach (var (field, val) in metaFields)
            {
                var row = new XlsxRow(20);
                row.Cells.Add(XlsxCell.Text(field, 4)); // Bold field header
                row.Cells.Add(XlsxCell.Text(val));
                sheet.Rows.Add(row);
            }

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
            ReportThresholdConfig thresholds,
            bool filterByImageName = true,
            bool showSampleCount = false)
        {
            var columnWidths = new double[15];
            for (int c = 0; c < 15; c++)
                columnWidths[c] = 13.5; // ~100 px per cell

            var sheet = new XlsxSheet(bandName) { ColumnWidths = columnWidths, FreezeTopRow = false };

            const int colLeft    = 0;  // A – left map image anchor
            const int colRight   = 9;  // J – right map image anchor
            const int cellSpan   = 6;  // each image spans 6 columns

            // Decrease max image width to make images narrower as requested
            const int maxWidthEmu = 3_619_500; // ~380 px wide (~4 Excel columns)

            var primarySessionId = bandRows.Select(x => x.SessionId).FirstOrDefault(x => x > 0);

            var validImages = ImageHeaders
                .Select(header => new
                {
                    Header = header,
                    Bytes = TryResolveMapImage(imageBytesByUrl, primarySessionId, header, bandName)
                })
                .Where(x => x.Bytes != null && x.Bytes.Length > 0)
                .ToList();

            for (int i = 0; i < validImages.Count; i += 2)
            {
                var leftItem  = validImages[i];
                var rightItem = (i + 1 < validImages.Count) ? validImages[i + 1] : null;

                // ── 1. Title row ──────────────────────────────────────────────────────
                var titleRow = new XlsxRow(22);
                titleRow.Cells.Add(XlsxCell.Text($"{bandName} - {leftItem.Header} Plot", 4)); // col 0
                for (int c = 1; c < 9; c++) titleRow.Cells.Add(XlsxCell.Text(""));            // cols 1-8
                if (rightItem != null)
                {
                    titleRow.Cells.Add(XlsxCell.Text($"{bandName} - {rightItem.Header} Plot", 4)); // col 9
                    for (int c = 10; c < 15; c++) titleRow.Cells.Add(XlsxCell.Text(""));           // cols 10-14
                }
                sheet.Rows.Add(titleRow);

                // ── 2. Resolve legend photo & overlay onto map image ──────────────────
                var leftRawBytes    = leftItem.Bytes!;
                var leftLegendBytes = TryResolveLegendPhoto(imageBytesByUrl, primarySessionId, leftItem.Header, bandRows, thresholds, filterByImageName, showSampleCount);
                var leftBytes       = OverlayLegendOnMap(leftRawBytes, leftLegendBytes);
                var leftSize        = ScaleToEmu(ReadPngSizePx(leftBytes), maxWidthEmu);

                byte[]? rightBytes = null;
                (int WidthEmu, int HeightEmu) rightSize = (0, 0);
                if (rightItem != null)
                {
                    var rightRawBytes    = rightItem.Bytes!;
                    var rightLegendBytes = TryResolveLegendPhoto(imageBytesByUrl, primarySessionId, rightItem.Header, bandRows, thresholds, filterByImageName, showSampleCount);
                    rightBytes           = OverlayLegendOnMap(rightRawBytes, rightLegendBytes);
                    rightSize            = ScaleToEmu(ReadPngSizePx(rightBytes), maxWidthEmu);
                }

                // ── 3. Image row (legend panel sits at bottom-left outside map boundary) ─
                double rowHeightPts = leftSize.HeightEmu / 12700.0;
                if (rightSize.HeightEmu > 0)
                    rowHeightPts = Math.Max(rowHeightPts, rightSize.HeightEmu / 12700.0);
                if (rowHeightPts > 550.0) rowHeightPts = 550.0;
                if (rowHeightPts < 220.0) rowHeightPts = 220.0;

                var imageRowIdx = sheet.Rows.Count;
                var imageRow    = new XlsxRow(rowHeightPts);
                for (int c = 0; c < 15; c++) imageRow.Cells.Add(XlsxCell.Text(""));
                sheet.Rows.Add(imageRow);

                // Left map image (cols A-F, 6 cells)
                sheet.Images.Add(new XlsxImage(imageRowIdx, colLeft, leftBytes,
                    leftSize.WidthEmu, leftSize.HeightEmu, cellSpanCols: cellSpan));

                // Right map image (cols J-O, 6 cells)
                if (rightBytes != null)
                    sheet.Images.Add(new XlsxImage(imageRowIdx, colRight, rightBytes,
                        rightSize.WidthEmu, rightSize.HeightEmu, cellSpanCols: cellSpan));

                // ── 4. Blank spacer rows ──────────────────────────────────────────────
                sheet.Rows.Add(XlsxRow.Blank());
                sheet.Rows.Add(XlsxRow.Blank());
            }

            return sheet;
        }

        private static byte[]? TryResolveMapImage(
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            int primarySessionId,
            string header,
            string? bandName = null)
        {
            var headerUpper = header.ToUpperInvariant();
            var headerLower = header.ToLowerInvariant();
            var bandUpper   = (bandName ?? "").ToUpperInvariant().Trim();

            var candidateKeys = new List<string>();

            if (primarySessionId > 0)
            {
                candidateKeys.Add($"{primarySessionId}_{headerUpper}");
                candidateKeys.Add($"{primarySessionId}_{headerLower}");
                candidateKeys.Add(BuildImageUrl(primarySessionId, header));
                candidateKeys.Add($"{ImageBaseUrl}/{primarySessionId}_{headerUpper}.png");
                candidateKeys.Add($"{ImageBaseUrl}/{primarySessionId}_{headerLower}.png");
            }

            if (!string.IsNullOrWhiteSpace(bandUpper))
            {
                candidateKeys.Add($"{bandUpper}_{headerUpper}");
                candidateKeys.Add($"{bandUpper}_{headerLower}");
            }

            candidateKeys.Add(headerUpper);
            candidateKeys.Add(headerLower);
            candidateKeys.Add($"MAP_{headerUpper}");
            candidateKeys.Add($"MAP_{headerLower}");
            candidateKeys.Add(BuildImageUrl(0, header));

            foreach (var key in candidateKeys)
            {
                if (imageBytesByUrl.TryGetValue(key, out var b) && b != null && b.Length > 0)
                    return b;
            }

            var altHeader = headerUpper switch
            {
                "CI" => "CELL_ID",
                "CELL_ID" => "CI",
                _ => null
            };

            if (altHeader != null)
            {
                var altKeys = new[]
                {
                    BuildImageUrl(primarySessionId, altHeader),
                    $"{ImageBaseUrl}/{primarySessionId}_{altHeader}.png",
                    $"{ImageBaseUrl}/0_{altHeader}.png",
                    BuildImageUrl(0, altHeader),
                    altHeader,
                    $"MAP_{altHeader}"
                };
                foreach (var key in altKeys)
                {
                    if (imageBytesByUrl.TryGetValue(key, out var b) && b != null && b.Length > 0)
                        return b;
                }
            }

            foreach (var kvp in imageBytesByUrl)
            {
                if (kvp.Value == null || kvp.Value.Length == 0) continue;
                if (kvp.Key.Contains("legend", StringComparison.OrdinalIgnoreCase)) continue;

                if (kvp.Key.Equals(headerUpper, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.EndsWith($"_{headerUpper}.png", StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.EndsWith($"_{headerUpper}", StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.EndsWith($"/{headerUpper}.png", StringComparison.OrdinalIgnoreCase) ||
                    (altHeader != null && (
                        kvp.Key.Equals(altHeader, StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.EndsWith($"_{altHeader}.png", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.EndsWith($"_{altHeader}", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.EndsWith($"/{altHeader}.png", StringComparison.OrdinalIgnoreCase))))
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        private static byte[] TryResolveLegendPhoto(
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            int primarySessionId,
            string header,
            List<WalkTestLogRow> bandRows,
            ReportThresholdConfig thresholds,
            bool filterByImageName = true,
            bool showSampleCount = false)
        {
            if (bandRows != null && bandRows.Count > 0)
            {
                return GenerateLegendPng(header, bandRows, thresholds, filterByImageName, showSampleCount);
            }

            var headerUpper = header.ToUpperInvariant();
            var headerLower = header.ToLowerInvariant();

            var candidateKeys = new List<string>();
            if (primarySessionId > 0)
            {
                candidateKeys.Add($"LEGEND_{primarySessionId}_{headerUpper}");
                candidateKeys.Add($"{primarySessionId}_{headerUpper}_LEGEND");
            }
            candidateKeys.Add($"LEGEND_{headerUpper}");
            candidateKeys.Add($"{headerUpper}_LEGEND");
            candidateKeys.Add($"LEGEND_{headerLower}");
            candidateKeys.Add($"{headerLower}_LEGEND");
            candidateKeys.Add("GLOBAL_LEGEND");
            candidateKeys.Add("LEGEND");

            foreach (var key in candidateKeys)
            {
                if (imageBytesByUrl.TryGetValue(key, out var b) && b != null && b.Length > 0)
                    return b;
            }

            foreach (var kvp in imageBytesByUrl)
            {
                if (kvp.Value == null || kvp.Value.Length == 0) continue;
                if (!kvp.Key.Contains("legend", StringComparison.OrdinalIgnoreCase)) continue;

                if (kvp.Key.Contains(headerUpper, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }

            return GenerateLegendPng(header, bandRows, thresholds);
        }

        private static byte[] OverlayLegendOnMap(byte[] mapBytes, byte[]? legendBytes)
        {
            if (legendBytes == null || legendBytes.Length == 0)
                return mapBytes;

            try
            {
                using var mapImage = Image.Load<Rgba32>(mapBytes);
                using var legendImage = Image.Load<Rgba32>(legendBytes);

                int margin = 16;

                int targetWidth = Math.Max(420, (int)(mapImage.Width * 0.45));
                if (targetWidth > mapImage.Width - margin * 2)
                    targetWidth = mapImage.Width - margin * 2;

                if (legendImage.Width != targetWidth)
                {
                    double scale = (double)targetWidth / legendImage.Width;
                    int newW = targetWidth;
                    int newH = Math.Max(1, (int)(legendImage.Height * scale));
                    legendImage.Mutate(ctx => ctx.Resize(newW, newH));
                }

                int legendPanelHeight = legendImage.Height + margin * 2;
                int combinedWidth = mapImage.Width;
                int combinedHeight = mapImage.Height + legendPanelHeight;

                using var canvas = new Image<Rgba32>(combinedWidth, combinedHeight);
                var white = new Rgba32(255, 255, 255, 255);

                canvas.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        row.Fill(white);
                    }
                });

                canvas.Mutate(ctx => ctx.DrawImage(mapImage, new Point(0, 0), 1.0f));

                int legendX = margin;
                int legendY = mapImage.Height + margin;
                canvas.Mutate(ctx => ctx.DrawImage(legendImage, new Point(legendX, legendY), 1.0f));

                using var ms = new MemoryStream();
                canvas.SaveAsPng(ms);
                return ms.ToArray();
            }
            catch
            {
                return mapBytes;
            }
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
            ReportThresholdConfig thresholds,
            bool filterByImageName = true)
        {
            var headerUpper = (header ?? "").ToUpperInvariant().Trim();
            var ranges = thresholds.GetRangesForHeader(headerUpper);

            var result = ranges.Select(r => new LegendStatRow { Range = r, Count = 0 }).ToList();

            if (result.Count == 0)
                return result;

            List<WalkTestLogRow> targetRows;
            if (filterByImageName)
            {
                var validImageRows = rows.Where(r => RowHasImageName(r, headerUpper)).ToList();
                targetRows = validImageRows.Count > 0 ? validImageRows : rows;
            }
            else
            {
                targetRows = rows;
            }

            if (headerUpper == "RSRP" || headerUpper == "RSRQ" || headerUpper == "SINR" ||
                headerUpper == "DL_THPT" || headerUpper == "UL_THPT" || headerUpper == "LTE_BLER" ||
                headerUpper == "MOS" || headerUpper == "PUSCH_TX" || headerUpper == "EARFCN")
            {
                var values = targetRows.Select(x =>
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
                int total = targetRows.Count > 0 ? targetRows.Count : 1;
                foreach (var item in result)
                {
                    if (!string.IsNullOrWhiteSpace(item.Range.ValueMatch))
                    {
                        item.Count = targetRows.Count(x =>
                            (x.Band ?? "").Contains(item.Range.ValueMatch, StringComparison.OrdinalIgnoreCase) ||
                            (x.Earfcn ?? "").Contains(item.Range.ValueMatch, StringComparison.OrdinalIgnoreCase) ||
                            (x.VolteCall ?? "").Contains(item.Range.ValueMatch, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        item.Count = targetRows.Count;
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

        private const int MaxSwatchCacheEntries = 256;
        private static readonly ConcurrentDictionary<string, byte[]> SwatchCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> SwatchCacheOrder = new();

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
            AddSwatchToCache(key, bytes);
            return bytes;
        }

        private static void AddSwatchToCache(string key, byte[] bytes)
        {
            if (!SwatchCache.TryAdd(key, bytes))
                return;

            SwatchCacheOrder.Enqueue(key);
            while (SwatchCache.Count > MaxSwatchCacheEntries && SwatchCacheOrder.TryDequeue(out var oldKey))
                SwatchCache.TryRemove(oldKey, out _);
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
            public List<ThresholdRange> Pci { get; set; } = new();
            public List<ThresholdRange> NodeBId { get; set; } = new();
            public List<ThresholdRange> CellId { get; set; } = new();

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
                    "PCI" => Pci,
                    "NODEB_ID" or "NODEB" => NodeBId,
                    "CELL_ID" or "CI" or "CELLID" or "CID" => CellId,
                    _ => Rsrp
                };
            }

            public string? GetColorForValue(string header, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;

                var ranges = GetRangesForHeader(header);
                if (ranges == null || ranges.Count == 0) return null;

                var trimmedValue = value.Trim();

                foreach (var r in ranges)
                {
                    if (!string.IsNullOrWhiteSpace(r.ValueMatch) &&
                        r.ValueMatch.Equals(trimmedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return r.ColorHex;
                    }
                }

                if (double.TryParse(trimmedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var dVal))
                {
                    foreach (var r in ranges)
                    {
                        if (string.IsNullOrWhiteSpace(r.ValueMatch) && dVal >= r.Min && dVal <= r.Max)
                        {
                            return r.ColorHex;
                        }
                    }
                }

                return null;
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
                        new("-75 to 0",-75,0,"#006400"),
                        new("-85 to -75",-85,-75,"#92D050"),
                        new("-95 to -85",-95,-85,"#95D5F5"),
                        new("-105 to -95",-105,-95,"#0000FF"),
                        new("-115 to -105",-115,-105,"#FFFF00"),
                        new("-140 to -115",-140,-115,"#FF0000")
                    },
                    Rsrq = new List<ThresholdRange>
                    {
                        new("-5 to 0",-5,0,"#006400"),
                        new("-10 to -5",-10,-5,"#92D050"),
                        new("-15 to -10",-15,-10,"#95D5F5"),
                        new("-20 to -15",-20,-15,"#0000FF"),
                        new("-25 to -20",-25,-20,"#FFFF00"),
                        new("-30 to -25",-30,-25,"#FF0000")
                    },
                    Sinr = new List<ThresholdRange>
                    {
                        new("25 to 40",25,40,"#006400"),
                        new("15 to 25",15,25,"#92D050"),
                        new("10 to 15",10,15,"#95D5F5"),
                        new("5 to 10",5,10,"#0000FF"),
                        new("0 to 5",0,5,"#FFFF00"),
                        new("-20 to 0",-20,0,"#FF0000")
                    },
                    DlTpt = new List<ThresholdRange>
                    {
                        new("100 to 1000",100,1000,"#006400"),
                        new("50 to 100",50,100,"#92D050"),
                        new("20 to 50",20,50,"#95D5F5"),
                        new("10 to 20",10,20,"#0000FF"),
                        new("5 to 10",5,10,"#FFFF00"),
                        new("0 to 5",0,5,"#FF0000")
                    },
                    UlTpt = new List<ThresholdRange>
                    {
                        new("30 to 1000",30,1000,"#006400"),
                        new("15 to 30",15,30,"#92D050"),
                        new("10 to 15",10,15,"#95D5F5"),
                        new("5 to 10",5,10,"#0000FF"),
                        new("1 to 5",1,5,"#FFFF00"),
                        new("0 to 1",0,1,"#FF0000")
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
                        new("0% - 1%",0,1,"#006400") { ValueMatch="< 1%" },
                        new("1% - 3%",1,3,"#92D050") { ValueMatch="1% - 3%" },
                        new("3% - 5%",3,5,"#95D5F5") { ValueMatch="3% - 5%" },
                        new("5% - 10%",5,10,"#0000FF") { ValueMatch="5% - 10%" },
                        new("10% - 15%",10,15,"#FFFF00") { ValueMatch="10% - 15%" },
                        new("> 15%",15,100,"#FF0000") { ValueMatch="> 15%" }
                    },
                    VolteCall = new List<ThresholdRange>
                    {
                        new("VoLTE Active",1,1,"#006400") { ValueMatch="1" },
                        new("No VoLTE",0,0,"#FF0000") { ValueMatch="0" }
                    },
                    PuschTx = new List<ThresholdRange>
                    {
                        new("<= 1 dBm",-50,1,"#006400"),
                        new("1 to 9 dBm",1,9,"#95D5F5"),
                        new("9 to 16 dBm",9,16,"#0000FF"),
                        new("16 to 21 dBm",16,21,"#FFFF00"),
                        new("> 21 dBm",21,35,"#FF0000")
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

        private static byte[] ResolveLegendImageBytes(
            string header,
            List<WalkTestLogRow> bandRows,
            ReportThresholdConfig thresholds,
            IReadOnlyDictionary<string, byte[]?> imageBytesByUrl,
            long primarySessionId)
        {
            string keyZip = "LEGEND_" + header.ToUpperInvariant();
            if (imageBytesByUrl.TryGetValue(keyZip, out var b1) && b1 != null && b1.Length > 0)
                return b1;

            string url1 = BuildLegendImageUrl((int)primarySessionId, header);
            if (imageBytesByUrl.TryGetValue(url1, out var b2) && b2 != null && b2.Length > 0)
                return b2;

            string url2 = BuildGlobalLegendImageUrl(header);
            if (imageBytesByUrl.TryGetValue(url2, out var b3) && b3 != null && b3.Length > 0)
                return b3;

            return GenerateLegendPng(header, bandRows, thresholds);
        }

        /// <summary>
        /// Generates a clean high-definition color-coded vector legend PNG graphic in memory (with Ranges, Count, & Percentage).
        /// </summary>
        private static byte[] GenerateLegendPng(
            string header,
            List<WalkTestLogRow>? allRows = null,
            ReportThresholdConfig? thresholds = null,
            bool filterByImageName = true,
            bool showSampleCount = false)
        {
            thresholds ??= ReportThresholdConfig.Hardcoded();
            allRows ??= new List<WalkTestLogRow>();

            bool isUniqueValues = IsUniqueValueHeader(header);
            bool isEarfcn = string.Equals(header, "EARFCN", StringComparison.OrdinalIgnoreCase);

            var uniqueVals = isUniqueValues ? GetUniqueValuesForHeader(allRows, header, filterByImageName) : null;
            var stats = !isUniqueValues ? CalculateLegendStatistics(allRows, header, thresholds, filterByImageName) : null;

            var lines = new List<(string Label, Rgba32 Color)>();
            if (isUniqueValues && uniqueVals != null)
            {
                var headerUpper = header.ToUpperInvariant().Trim();
                List<WalkTestLogRow> targetRows;
                if (filterByImageName)
                {
                    var validImageRows = allRows.Where(r => RowHasImageName(r, headerUpper)).ToList();
                    targetRows = validImageRows.Count > 0 ? validImageRows : allRows;
                }
                else
                {
                    targetRows = allRows;
                }
                var totalCount = targetRows.Count;

                for (int i = 0; i < uniqueVals.Count; i++)
                {
                    var val = uniqueVals[i];
                    var matchingRows = targetRows.Where(r =>
                        string.Equals(headerUpper switch
                        {
                            "PCI" => r.Pci,
                            "NODEB_ID" => r.NodeBId,
                            _ => r.CellId
                        }, val, StringComparison.OrdinalIgnoreCase)).ToList();

                    int valCount = matchingRows.Count;

                    double pct = totalCount > 0 ? (valCount * 100.0 / totalCount) : 0;
                    string lineLabel = showSampleCount
                        ? $"{headerUpper} {val}  ({valCount} | {pct:0.00}%)"
                        : $"{headerUpper} {val}  ({pct:0.00}%)";

                    // Strict 2-step color lookup:
                    // 1. From Image Name (MetricColors) of matching rows
                    string? hexColor = GetHexColorFromRows(matchingRows, headerUpper);

                    // 2. From ColorSettings / thresholds
                    if (string.IsNullOrWhiteSpace(hexColor))
                    {
                        hexColor = thresholds.GetColorForValue(headerUpper, val);
                    }

                    // 3. Fallback to distinct palette color per unique value
                    if (string.IsNullOrWhiteSpace(hexColor))
                    {
                        var palette = new[]
                        {
                            "#4A148C", "#2196F3", "#4CAF50", "#FF9800", "#E91E63", "#9C27B0",
                            "#00BCD4", "#FF5722", "#795548", "#607D8B", "#3F51B5", "#009688",
                            "#D81B60", "#8E24AA", "#1E88E5", "#00ACC1", "#43A047", "#FB8C00"
                        };
                        hexColor = palette[i % palette.Length];
                    }

                    var (r, g, b) = ParseHexColor(hexColor);
                    lines.Add((lineLabel, new Rgba32(r, g, b)));
                }

                if (lines.Count == 0)
                {
                    lines.Add(($"{headerUpper} Unique Values", new Rgba32(80, 85, 95)));
                }
            }
            else if (stats != null && stats.Count > 0)
            {
                foreach (var stat in stats)
                {
                    var hexColor = stat.Range.ColorHex;
                    if (string.IsNullOrWhiteSpace(hexColor) || hexColor.Equals("#808080", StringComparison.OrdinalIgnoreCase))
                    {
                        var imgColor = GetHexColorFromRows(allRows, header);
                        if (!string.IsNullOrWhiteSpace(imgColor))
                            hexColor = imgColor;
                    }

                    var (r, g, b) = ParseHexColor(hexColor);
                    var rangeDisplay = isEarfcn ? stat.Range.Display : stat.Range.RangeOnlyDisplay;
                    if (string.IsNullOrWhiteSpace(rangeDisplay)) rangeDisplay = stat.Range.Display;

                    string label = showSampleCount
                        ? $"{rangeDisplay}  ({stat.Count} | {stat.Percentage:0.00}%)"
                        : $"{rangeDisplay}  ({stat.Percentage:0.00}%)";
                    lines.Add((label, new Rgba32(r, g, b)));
                }
            }
            else
            {
                var ranges = thresholds.GetRangesForHeader(header);
                foreach (var r in ranges)
                {
                    var (red, green, blue) = ParseHexColor(r.ColorHex);
                    var label = !string.IsNullOrWhiteSpace(r.Display) ? r.Display : r.RangeOnlyDisplay;
                    lines.Add((label, new Rgba32(red, green, blue)));
                }
            }

            int count = Math.Max(1, lines.Count);
            const int boxW = 22;          // Large 22x14px color swatch box
            const int boxH = 14;
            const int spacing = 10;       // Spacing between rows
            const int padding = 16;       // Card padding
            const int textPadding = 12;   // Text padding after swatch
            const int fontSize = 16;      // Large 16pt bold font
            const int titleFontSize = 18; // Large 18pt bold title font
            const int titleGap = 8;       // Gap under title

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            using var font = new SKFont(typeface, fontSize) { Embolden = true };
            using var titleFont = new SKFont(typeface, titleFontSize) { Embolden = true };
            using var textPaint = new SKPaint { Color = new SKColor(15, 20, 30), IsAntialias = true, TextAlign = SKTextAlign.Left };
            using var titlePaint = new SKPaint { Color = new SKColor(10, 40, 120), IsAntialias = true, TextAlign = SKTextAlign.Left };

            var titleText = FormatLegendTitle(header);

            float maxTextWidth = 0;
            foreach (var item in lines)
            {
                var w = textPaint.MeasureText(item.Label ?? "");
                if (w > maxTextWidth) maxTextWidth = w;
            }
            var titleWidth = titlePaint.MeasureText(titleText);

            int width = Math.Max(420, padding * 2 + boxW + textPadding + (int)Math.Ceiling(maxTextWidth) + 16);
            width = Math.Max(width, padding * 2 + (int)Math.Ceiling(titleWidth) + 16);

            int titleBlockHeight = titleFontSize + titleGap + 6;
            int height = padding + titleBlockHeight + count * boxH + (count - 1) * spacing + padding;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(255, 255, 255, 255));

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(170, 175, 185),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };
            canvas.DrawRect(1.0f, 1.0f, width - 2, height - 2, borderPaint);

            // Title Header
            var titleBaselineY = padding + titleFontSize;
            canvas.DrawText(titleText, padding, titleBaselineY, titleFont, titlePaint);

            using var separatorPaint = new SKPaint
            {
                Color = new SKColor(190, 195, 205),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };
            var separatorY = titleBaselineY + titleGap;
            canvas.DrawLine(padding, separatorY, width - padding, separatorY, separatorPaint);

            int listTop = padding + titleBlockHeight;

            for (int i = 0; i < lines.Count; i++)
            {
                var (label, color) = lines[i];
                int boxTop = listTop + i * (boxH + spacing);

                using var boxFill = new SKPaint { Color = new SKColor(color.R, color.G, color.B), IsAntialias = true };
                var rect = new SKRect(padding, boxTop, padding + boxW, boxTop + boxH);
                canvas.DrawRect(rect, boxFill);

                var textX = padding + boxW + textPadding;
                var textY = boxTop + boxH - 1;
                canvas.DrawText(label ?? "", textX, textY, font, textPaint);
            }

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream();
            encoded.SaveTo(ms);
            return ms.ToArray();
        }

        /// <summary>Human-friendly title shown at the top of a generated legend (e.g. "RSRP" -> "RSRP (dBm)").</summary>
        private static string FormatLegendTitle(string header)
        {
            var h = (header ?? "").ToUpperInvariant().Trim();
            return h switch
            {
                "RSRP" => "RSRP (dBm)",
                "RSRQ" => "RSRQ (dB)",
                "SINR" => "SINR (dB)",
                "DL_THPT" => "DL Throughput (Mbps)",
                "UL_THPT" => "UL Throughput (Mbps)",
                "LTE_BLER" or "BLER" => "BLER (%)",
                "VOLTE_CALL" or "VOLTE" => "VoLTE Call",
                "PUSCH_TX" => "PUSCH Tx Power (dBm)",
                "EARFCN" => "EARFCN / Band",
                "CI" => "CI (Cell ID)",
                "CELL_ID" => "Cell ID",
                _ => string.IsNullOrWhiteSpace(header) ? "Legend" : header
            };
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

        private static string? ResolveBandFromEarfcn(string? earfcnRaw)
        {
            if (string.IsNullOrWhiteSpace(earfcnRaw)) return null;

            var match = Regex.Match(earfcnRaw, @"\b\d+\b");
            if (!match.Success || !int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
                return null;

            return e switch
            {
                >= 0 and <= 599 => "B1",
                >= 600 and <= 1199 => "B2",
                >= 1200 and <= 1949 => "B3",
                >= 1950 and <= 2399 => "B4",
                >= 2400 and <= 2649 => "B5",
                >= 2750 and <= 3449 => "B7",
                >= 3450 and <= 3799 => "B8",
                >= 3800 and <= 4149 => "B9",
                >= 4150 and <= 4749 => "B10",
                >= 4750 and <= 4949 => "B11",
                >= 5000 and <= 5179 => "B12",
                >= 5180 and <= 5279 => "B13",
                >= 5280 and <= 5379 => "B14",
                >= 5730 and <= 5849 => "B17",
                >= 5850 and <= 5999 => "B18",
                >= 6000 and <= 6149 => "B19",
                >= 6150 and <= 6449 => "B20",
                >= 6450 and <= 6599 => "B21",
                >= 6600 and <= 7399 => "B28",
                >= 7500 and <= 7699 => "B26",
                >= 8650 and <= 9649 => "B41",
                >= 9750 and <= 9869 => "B42",
                >= 9870 and <= 9989 => "B43",
                >= 37750 and <= 38249 => "B38",
                >= 38250 and <= 38649 => "B39",
                >= 38650 and <= 39649 => "B40",
                >= 39650 and <= 41589 => "B41",
                >= 41590 and <= 43589 => "B42",
                >= 43590 and <= 45589 => "B43",
                >= 45590 and <= 46589 => "B48",
                >= 151600 and <= 160600 => "n28",
                >= 422000 and <= 434000 => "n40",
                >= 500000 and <= 537000 => "n41",
                >= 620000 and <= 653333 => "n78",
                _ => null
            };
        }

        private static string ToBandSheetName(string? band, string? network, string? earfcn = null, string? primaryCellInfo = null)
        {
            var value = (band ?? string.Empty).Trim();

            bool isInvalidBand = string.IsNullOrWhiteSpace(value) ||
                                 value.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("null", StringComparison.OrdinalIgnoreCase);

            if (isInvalidBand)
            {
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
            {
                var prefix = (network != null && (network.Contains("5G", StringComparison.OrdinalIgnoreCase) || network.Contains("NR", StringComparison.OrdinalIgnoreCase))) ? "n" : "B";
                return $"{prefix}{match.Value}";
            }

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
            public string? ReportMode { get; set; }
            public string? SheetMode { get; set; }
            public string? Mode { get; set; }
            public bool? FilterByImageName { get; set; }
            public string? ImageFilter { get; set; }
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
            public string? RawImageName { get; set; }
            public Dictionary<string, string>? MetricColors { get; set; }
            public int FileIndex { get; set; }
            public string? SourceFileName { get; set; }
            public int FileGroup { get; set; } = 1;
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
            public bool FreezeTopRow { get; set; } = false;
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
                        ColumnWidths = sheet.ColumnWidths,
                        FreezeTopRow = sheet.FreezeTopRow
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
                    if (sheet.FreezeTopRow)
                    {
                        writer.WriteStartElement("pane", MainNs);
                        writer.WriteAttributeString("ySplit", "1");
                        writer.WriteAttributeString("topLeftCell", "A2");
                        writer.WriteAttributeString("activePane", "bottomLeft");
                        writer.WriteAttributeString("state", "frozen");
                        writer.WriteEndElement();
                    }
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
