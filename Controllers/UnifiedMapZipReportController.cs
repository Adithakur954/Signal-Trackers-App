using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace SignalTracker.Controllers
{
    // Generates the exact same PDF report as UnifiedMapReportController,
    // but reads its data from an uploaded log zip instead of the database.
    //
    // Place this file next to UnifiedMapReportController.cs (same project/namespace),
    // it reuses UnifiedMapReportRow / ReportThresholdConfig / ReportLogo /
    // UnifiedMapReportFactory / UnifiedMapRawPdfBuilder / UnifiedMapPdfRequest
    // that are already defined there.
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UnifiedMapZipReportController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;

        public UnifiedMapZipReportController(IWebHostEnvironment env)
        {
            _env = env;
        }

        private static async Task<string> SaveUploadToTempFileAsync(IFormFile upload, CancellationToken ct)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"signaltracker-unified-report-{Guid.NewGuid():N}.zip");
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

        // POST api/UnifiedMapZipReport/GenerateFromZip
        // multipart/form-data:
        //   LogZip          -> the .zip file (required)
        //   Title           -> optional report title
        //   GeneratedBy     -> optional
        //   ProjectName     -> optional (defaults to zip file name)
        //   SessionIdOverride -> optional, forces the session id used for image lookup
        //   BandFilter/Bands -> optional, one or more selected bands; ALL/empty = no filter
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

        private static bool ResolveFilterByImageName(object? request, IFormCollection? form, IQueryCollection? query = null)
        {
            var keysToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FilterByImageName", "UseImageNameFilter", "OnlyWithImage", "ImageFilter", "WithImageName", "ImageNameFilter", "ByImage", "ImageOnly"
            };

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

            return true;
        }

        // POST api/UnifiedMapZipReport/GenerateFromZip
        [HttpPost("GenerateFromZip")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Report")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(1_000_000_000)]
        public async Task<IActionResult> GenerateFromZip([FromForm] ZipReportUploadRequest request)
        {
            var uploads = ResolveUploadedFiles(request, Request);
            if (uploads.Count == 0)
                return BadRequest(new { Message = "At least one log zip file is required." });

            var filterByImageName = ResolveFilterByImageName(request, Request.HasFormContentType ? Request.Form : null, Request.Query);
            var selectedBands = ResolveSelectedBands(request, Request.HasFormContentType ? Request.Form : null);

            var generatedPdfs = new List<(string Filename, byte[] PdfBytes)>();

            foreach (var upload in uploads)
            {
                var tempZipPath = await SaveUploadToTempFileAsync(upload, HttpContext.RequestAborted);
                try
                {
                    await using var zipStream = new FileStream(
                        tempZipPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 128 * 1024,
                        useAsync: true);

                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

                    var mapImages = ExtractMapImages(archive, out var detectedSessionId);
                    var sessionId = request.SessionIdOverride ?? detectedSessionId ?? 0;

                    var rawRows = ExtractNetworkRows(archive, sessionId);
                    var rows = CleanRows(rawRows);
                    if (rows.Count == 0) continue;

                    if (filterByImageName)
                    {
                        var imageRows = rows.Where(RowHasImageName).ToList();
                        if (imageRows.Count > 0) rows = imageRows;
                    }

                    if (selectedBands.Count > 0)
                    {
                        rows = FilterRowsByBands(rows, selectedBands);
                    }

                    if (rows.Count == 0) continue;

                    var thresholds = ExtractThresholdConfig(archive);
                    var companyLogo = LoadCompanyLogo();
                    var productLogo = LoadProductLogo();

                    var sessionIds = sessionId > 0 ? new List<long> { sessionId } : new List<long>();

                    var pdfRequest = new UnifiedMapPdfRequest
                    {
                        ProjectId = 0,
                        Title = string.IsNullOrWhiteSpace(request.Title) ? "Drive Test Analytics Report" : request.Title,
                        GeneratedBy = request.GeneratedBy,
                        SessionIds = sessionIds,
                        NetworkType = "ALL"
                    };

                    var baseName = Path.GetFileNameWithoutExtension(upload.FileName);
                    var projectName = string.IsNullOrWhiteSpace(request.ProjectName)
                        ? baseName
                        : request.ProjectName;

                    if (selectedBands.Count > 0)
                    {
                        projectName += $" ({(selectedBands.Count == 1 ? "Band" : "Bands")}: {string.Join(", ", selectedBands)})";
                    }

                    var report = UnifiedMapReportFactory.Create(
                        pdfRequest,
                        projectName,
                        sessionIds,
                        rows,
                        thresholds,
                        companyLogo,
                        productLogo);

                    report.MapImages = mapImages;

                    var pdfBytes = UnifiedMapRawPdfBuilder.Build(report);
                    var pdfFilename = $"UnifiedMap_ZipReport_{sessionId}_{baseName}_{DateTime.Now:yyyy-MM-dd}.pdf";
                    generatedPdfs.Add((pdfFilename, pdfBytes));
                }
                finally
                {
                    if (System.IO.File.Exists(tempZipPath))
                    {
                        try { System.IO.File.Delete(tempZipPath); } catch { }
                    }
                }
            }

            if (generatedPdfs.Count == 0)
            {
                return BadRequest(new { Message = "No usable network log rows were found in the uploaded zip file(s)." });
            }

            // Single zip file -> return single PDF directly
            if (generatedPdfs.Count == 1)
            {
                return File(generatedPdfs[0].PdfBytes, "application/pdf", generatedPdfs[0].Filename);
            }

            // Multiple zip files -> package a separate PDF for each file into a ZIP archive
            using var zipMs = new MemoryStream();
            using (var zipArchive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (pdfName, pdfBytes) in generatedPdfs)
                {
                    var zipEntry = zipArchive.CreateEntry(pdfName, CompressionLevel.Fastest);
                    using var entryStream = zipEntry.Open();
                    entryStream.Write(pdfBytes, 0, pdfBytes.Length);
                }
            }

            zipMs.Seek(0, SeekOrigin.Begin);
            var zipDownloadName = $"UnifiedMap_PDF_Reports_{DateTime.Now:yyyy-MM-dd}.zip";
            return File(zipMs.ToArray(), "application/zip", zipDownloadName);
        }

        // ---------------------------------------------------------------
        // Data cleaning — runs after CSV parsing, before the rows are used
        // for the report OR (later) inserted into the database.
        // ---------------------------------------------------------------

        private static List<UnifiedMapReportRow> CleanRows(List<UnifiedMapReportRow> rows)
        {
            var cleaned = new List<UnifiedMapReportRow>(rows.Count);
            var seen = new HashSet<string>();

            foreach (var r in rows)
            {
                // 1) must have a valid timestamp (belt-and-braces; parser already checks this)
                if (!r.Timestamp.HasValue) continue;

                // 2) normalize text fields: trims quotes/whitespace and turns
                //    placeholder junk ("N/A", "Unknown", "null", "") into a real null.
                //    (Report-building code already renders null as "Unknown" in
                //    charts/tables, so this does not change the report — it just
                //    keeps the stored value clean instead of storing the literal
                //    string "N/A"/"Unknown".)
                r.Provider = NormalizeText(r.Provider);
                r.Band = NormalizeText(r.Band);
                r.Network = NormalizeText(r.Network);
                r.Pci = NormalizeText(r.Pci);
                r.NodebId = NormalizeText(r.NodebId);
                r.CellId = NormalizeText(r.CellId);
                r.IndoorOutdoor = NormalizeText(r.IndoorOutdoor);
                r.Apps = NormalizeText(r.Apps);
                r.Bler = NormalizeText(r.Bler);

                // 3) match the DB report filter: band must be present.
                if (r.Band == null) continue;

                // 4) drop clearly invalid GPS values instead of plotting garbage points
                if (r.Lat is < -90 or > 90) r.Lat = null;
                if (r.Lon is < -180 or > 180) r.Lon = null;
                if (r.Lat == 0 && r.Lon == 0) { r.Lat = null; r.Lon = null; }

                // 5) de-duplicate: the same physical reading sometimes appears twice
                //    (e.g. repeated across neighbour-cell rows at the exact same
                //    timestamp). Treat session + timestamp + pci + rsrp + rsrq +
                //    band as the identity of one reading.
                var dedupeKey = string.Join('|',
                    r.SessionId, r.Timestamp?.Ticks, r.Pci, r.Rsrp, r.Rsrq, r.Band, r.Lat, r.Lon, r.Id);
                if (!seen.Add(dedupeKey)) continue;

                cleaned.Add(r);
            }

            // reassign sequential ids after rows were dropped/reordered
            for (var i = 0; i < cleaned.Count; i++) cleaned[i].Id = i + 1;

            return cleaned;
        }

        private static string? NormalizeText(string? value)
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

        private static List<UnifiedMapReportRow> FilterRowsByBands(
            IEnumerable<UnifiedMapReportRow> rows,
            IReadOnlyCollection<string> selectedBands)
        {
            var wanted = selectedBands
                .Select(CanonicalBandKey)
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return rows
                .Where(row => wanted.Contains(CanonicalBandKey(row.Band)))
                .ToList();
        }

        private static string CanonicalBandKey(string? value)
        {
            var text = NormalizeText(value);
            if (text == null) return "";

            var key = Regex.Replace(text, @"\s+", "", RegexOptions.CultureInvariant).ToUpperInvariant();
            if (key.StartsWith("BAND", StringComparison.Ordinal))
                key = "B" + key[4..];
            if (Regex.IsMatch(key, @"^\d{1,3}$"))
                key = "B" + key;
            return key;
        }

        // POST api/UnifiedMapZipReport/DiscoverBands
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
            var allRows = new List<UnifiedMapReportRow>();
            var sessionIds = new List<int>();

            try
            {
                foreach (var upload in uploads)
                {
                    var tempZipPath = await SaveUploadToTempFileAsync(upload, HttpContext.RequestAborted);
                    allTempPaths.Add(tempZipPath);

                    await using var zipStream = new FileStream(
                        tempZipPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 128 * 1024, useAsync: true);

                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

                    ExtractMapImages(archive, out var detectedSessionId);
                    var sid = (int)(request.SessionIdOverride ?? detectedSessionId ?? 0);
                    if (sid > 0 && !sessionIds.Contains(sid)) sessionIds.Add(sid);

                    var rawRows = ExtractNetworkRows(archive, sid);
                    allRows.AddRange(CleanRows(rawRows));
                }

                var filterByImageName = ResolveFilterByImageName(request, Request.HasFormContentType ? Request.Form : null, Request.Query);
                var rowsToSummarize = allRows;
                if (filterByImageName)
                {
                    var imageRows = allRows.Where(RowHasImageName).ToList();
                    if (imageRows.Count > 0) rowsToSummarize = imageRows;
                }

                var validRows = rowsToSummarize
                    .Where(r => !string.IsNullOrWhiteSpace(r.Band) &&
                                !r.Band.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                                !r.Band.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (validRows.Count == 0 && rowsToSummarize.Count > 0)
                {
                    validRows = rowsToSummarize;
                }

                var totalValid = validRows.Count;
                var bandSummary = validRows
                    .GroupBy(r => r.Band!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        Band = g.Key,
                        Count = g.Count(),
                        Percentage = totalValid == 0 ? 0 : Math.Round(g.Count() * 100.0 / totalValid, 2)
                    })
                    .OrderByDescending(x => x.Count)
                    .Cast<object>()
                    .ToList();

                return Ok(new
                {
                    SessionId = sessionIds.FirstOrDefault(),
                    SessionIds = sessionIds,
                    FileCount = uploads.Count,
                    TotalRows = rowsToSummarize.Count,
                    AvailableBands = bandSummary
                });
            }
            finally
            {
                foreach (var path in allTempPaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        try { System.IO.File.Delete(path); } catch { }
                    }
                }
            }
        }

        private static ReportThresholdConfig ExtractThresholdConfig(ZipArchive archive)
        {
            var thresholds = ReportThresholdConfig.Hardcoded();
            var entry = archive.Entries
                .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(e => Path.GetFileName(e.FullName).StartsWith("ColorSettings_", StringComparison.OrdinalIgnoreCase));

            if (entry == null) return thresholds;

            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = reader.ReadToEnd()
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count < 2) return thresholds;

                var headers = ParseCsvLine(lines[0]);
                var metricIndex = FindHeader(headers, "Metric");
                var typeIndex = FindHeader(headers, "Type");
                var minIndex = FindHeader(headers, "Min");
                var maxIndex = FindHeader(headers, "Max");
                var valueIndex = FindHeader(headers, "Value");
                var colorIndex = FindHeader(headers, "Color");
                var labelIndex = FindHeader(headers, "Label");

                if (metricIndex < 0 || typeIndex < 0 || colorIndex < 0) return thresholds;

                var rangesByMetric = new Dictionary<string, List<ThresholdRange>>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in lines.Skip(1))
                {
                    var cols = ParseCsvLine(line);
                    var metric = GetCol(cols, metricIndex).Trim().ToUpperInvariant();
                    var type = GetCol(cols, typeIndex).Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(type)) continue;

                    var color = NormalizeColorHex(GetCol(cols, colorIndex));
                    var label = GetCol(cols, labelIndex);
                    ThresholdRange? range = null;

                    if (type == "RANGE")
                    {
                        var min = ParseDoubleSafe(GetCol(cols, minIndex));
                        var max = ParseDoubleSafe(GetCol(cols, maxIndex));
                        if (!min.HasValue || !max.HasValue) continue;
                        range = new ThresholdRange(label, min.Value, max.Value, color);
                    }
                    else if (type == "VALUE")
                    {
                        var value = GetCol(cols, valueIndex);
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

                ApplyMetricRanges(rangesByMetric, "RSRP", ranges => thresholds.Rsrp = ranges);
                ApplyMetricRanges(rangesByMetric, "RSRQ", ranges => thresholds.Rsrq = ranges);
                ApplyMetricRanges(rangesByMetric, "SINR", ranges => thresholds.Sinr = ranges);
                ApplyMetricRanges(rangesByMetric, "DL_THPT", ranges => thresholds.DlTpt = ranges);
                ApplyMetricRanges(rangesByMetric, "UL_THPT", ranges => thresholds.UlTpt = ranges);
                ApplyMetricRanges(rangesByMetric, "EARFCN", ranges => thresholds.Earfcn = ranges);
                ApplyMetricRanges(rangesByMetric, "BLER", ranges => thresholds.Bler = ranges);
                ApplyMetricRanges(rangesByMetric, "LTE_BLER", ranges => thresholds.Bler = ranges);
                ApplyMetricRanges(rangesByMetric, "VOLTE", ranges => thresholds.VolteCall = ranges);
                ApplyMetricRanges(rangesByMetric, "VOLTE_CALL", ranges => thresholds.VolteCall = ranges);
                ApplyMetricRanges(rangesByMetric, "PUSCH_TX", ranges => thresholds.PuschTx = ranges);

                thresholds.Source = $"Log zip color settings ({Path.GetFileName(entry.FullName)})";
            }
            catch
            {
                return thresholds;
            }

            return thresholds;
        }

        private static void ApplyMetricRanges(
            Dictionary<string, List<ThresholdRange>> rangesByMetric,
            string metric,
            Action<List<ThresholdRange>> apply)
        {
            if (rangesByMetric.TryGetValue(metric, out var ranges) && ranges.Count > 0)
                apply(ranges);
        }

        private static int FindHeader(List<string> headers, string name)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static double? ParseDoubleSafe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
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



        // NOTE: header group allows underscores too, so files like
        // "5356_DL_THPT.png", "5356_LTE_BLER.png", "5356_VOLTE_CALL.png",
        // "5356_NODEB_ID.png" etc. are matched correctly (previously only
        // single-word headers like RSRP/RSRQ/SINR/EARFCN matched, which is why
        // some Map View pages were missing their images).
        private static readonly Regex ImageNamePattern =
            new(@"(?:^|[\\/])(\d+)_([A-Za-z0-9_]+)\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase);

        // ---------------------------------------------------------------
        // Image extraction: matches files like "5356_RSRP.png" inside the zip
        // ---------------------------------------------------------------

        private Dictionary<string, ReportLogo> ExtractMapImages(ZipArchive archive, out long? detectedSessionId)
        {
            var images = new Dictionary<string, ReportLogo>(StringComparer.OrdinalIgnoreCase);
            var sessionCounts = new Dictionary<long, int>();

            foreach (var entry in archive.Entries)
            {
                var match = ImageNamePattern.Match(entry.FullName);
                if (!match.Success) continue;

                if (long.TryParse(match.Groups[1].Value, out var sid))
                    sessionCounts[sid] = sessionCounts.TryGetValue(sid, out var c) ? c + 1 : 1;

                var header = match.Groups[2].Value.ToUpperInvariant();

                try
                {
                    using var entryStream = entry.Open();
                    using var ms = new MemoryStream();
                    entryStream.CopyTo(ms);
                    var originalBytes = ms.ToArray();

                    byte[] jpegBytes;
                    try
                    {
                        using var image = Image.Load(originalBytes);
                        using var outMs = new MemoryStream();
                        image.Save(outMs, new JpegEncoder { Quality = 90 });
                        jpegBytes = outMs.ToArray();
                    }
                    catch
                    {
                        jpegBytes = originalBytes;
                    }

                    var (width, height) = UnifiedMapRawPdfBuilder.GetJpegDimensions(jpegBytes);
                    if (width > 0 && height > 0)
                        images[header] = new ReportLogo(jpegBytes, width, height);
                }
                catch
                {
                    // skip unreadable image entries
                }
            }

            detectedSessionId = sessionCounts.Count > 0
                ? sessionCounts.OrderByDescending(x => x.Value).First().Key
                : (long?)null;

            return images;
        }

        // ---------------------------------------------------------------
        // CSV extraction
        // ---------------------------------------------------------------

        // Consume timestamped network logs (e.g. NetworkLog_20260711_163832.csv)
        // as well as NetworkLogUnsent_{session_id}.csv files.
        private static readonly Regex NetworkLogCsvNamePattern =
            new(@"^NetworkLogs?_.*\.csv$", RegexOptions.IgnoreCase);

        private List<UnifiedMapReportRow> ExtractNetworkRows(ZipArchive archive, long sessionId)
        {
            var rows = new List<UnifiedMapReportRow>();
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
                var map = BuildColumnMap(headers);

                for (var i = 1; i < lines.Count; i++)
                {
                    var cols = ParseCsvLine(lines[i]);
                    if (cols.Count < 2) continue;

                    var row = ParseStandardRow(cols, map, sessionId, ref nextId);

                    if (row != null) rows.Add(row);
                }
            }

            return rows;
        }

        private sealed class ColumnMap
        {
            public int Timestamp = -1, Lat = -1, Lon = -1, Network = -1, IndoorOutdoor = -1,
                Mos = -1, Jitter = -1, Latency = -1, PacketLoss = -1, CellId = -1, Pci = -1,
                Rsrp = -1, Rsrq = -1, Sinr = -1, DlTpt = -1, UlTpt = -1, Earfcn = -1,
                VolteCall = -1, Band = -1, Bler = -1, AlphaLong = -1, AlphaShort = -1,
                Rssi = -1, NodebId = -1, Apps = -1, PuschTx = -1, Primary = -1,
                PrimaryCellInfo = -1, ImageName = -1;
        }

        private static ColumnMap BuildColumnMap(List<string> headers)
        {
            return new ColumnMap
            {
                Timestamp = FindColumn(headers, "timestamp"),
                Lat = FindColumn(headers, "latitude"),
                Lon = FindColumn(headers, "longitude"),
                Network = FindColumn(headers, "network type", "network_type", "networktype", "network", "technology", "tech", "rat", "system", "mode"),
                IndoorOutdoor = FindColumn(headers, "indoor/outdoor", "indoor_outdoor", "location_type"),
                Mos = FindColumn(headers, "mos"),
                Jitter = FindColumn(headers, "jitter"),
                Latency = FindColumn(headers, "latency"),
                PacketLoss = FindColumn(headers, "packet loss"),
                CellId = FindColumn(headers, "cell id", "cell_id", "cellid", "ci", "cid", "cell identity", "cell_index"),
                Pci = FindColumn(headers, "pci / psc", "pci", "psc"),
                Rsrp = FindColumn(headers, "ssrsrp", "rsrp"),
                Rsrq = FindColumn(headers, "ssrsrq", "rsrq"),
                Sinr = FindColumn(headers, "rxqual", "sinr"),
                DlTpt = FindColumn(headers, "dl thpt", "dl_tpt", "dl_throughput", "dl throughput"),
                UlTpt = FindColumn(headers, "ul thpt", "ul_tpt", "ul_throughput", "ul throughput"),
                Earfcn = FindColumn(headers, "nr_earfcn", "nrfcn", "nrarfcn", "nr_arfcn", "arfcn", "dl_earfcn", "dl_frequency", "earfcn", "frequency", "freq"),
                VolteCall = FindColumn(headers, "volte call", "volte_call", "volte"),
                Band = FindColumn(headers, "primary_band", "primary band", "freq_band", "freq band", "frequency_band", "frequency band", "serving_band", "serving band", "lte_band", "nr_band", "5g_band", "operating_band", "working_band", "band"),
                Bler = FindColumn(headers, "bler"),
                AlphaLong = FindColumn(headers, "alpha long", "m_alpha_long"),
                AlphaShort = FindColumn(headers, "alpha short", "m_alpha_short"),
                Rssi = FindColumn(headers, "rssi"),
                NodebId = FindColumn(headers,
                    "nodeb id", "nodeb_id", "node_b_id", "nodeb", "node_b",
                    "enodeb id", "enodeb_id", "enodebid", "enodeb",
                    "enb id", "enb_id", "enbid", "enb",
                    "gnodeb id", "gnodeb_id", "gnodebid", "gnodeb",
                    "gnb id", "gnb_id", "gnbid", "gnb",
                    "site id", "site_id", "siteid"),
                Apps = FindColumn(headers, "running apps", "apps", "app_name"),
                PuschTx = FindColumn(headers, "pusch tx", "pusch_tx", "pusch"),
                Primary = FindColumnByName(headers, "primary"),
                PrimaryCellInfo = FindColumnByName(headers, "cellinfo_1", "primary_cell_info_1", "primary_cell_info", "cell_info", "cellinfo"),
                ImageName = FindColumn(headers, "image_name", "imagename", "image name", "image")
            };
        }

        private static int FindColumnByName(List<string> headers, params string[] candidates)
        {
            var candidateNames = candidates
                .Select(NormalizeColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < headers.Count; i++)
            {
                if (candidateNames.Contains(NormalizeColumnName(headers[i])))
                    return i;
            }

            return -1;
        }

        private static int FindColumn(List<string> headers, params string[] candidates)
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

        private static string NormalizeColumnName(string value) =>
            Regex.Replace(value.Trim(), @"[^a-z0-9]+", "", RegexOptions.IgnoreCase);

        private UnifiedMapReportRow? ParseStandardRow(List<string> cols, ColumnMap map, long sessionId, ref int nextId)
        {
            var tsRaw = GetCol(cols, map.Timestamp);
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

            if (!IsPrimaryRegisteredRow(cols, map))
                return null;

            var provider = GetCol(cols, map.AlphaShort);
            if (string.IsNullOrWhiteSpace(provider)) provider = GetCol(cols, map.AlphaLong);

            var band = GetCol(cols, map.Band);
            var network = GetCol(cols, map.Network);
            var earfcn = GetCol(cols, map.Earfcn);
            var primaryCellInfo = GetCol(cols, map.PrimaryCellInfo);

            var resolvedBand = ResolveZipReportBand(band, network, earfcn, primaryCellInfo);

            var nodebId = GetCol(cols, map.NodebId);
            var cellId = GetCol(cols, map.CellId);

            if (string.IsNullOrWhiteSpace(nodebId) ||
                nodebId.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                nodebId.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(cellId))
                {
                    var digits = Regex.Match(cellId, @"\d+").Value;
                    if (long.TryParse(digits, out var cidVal) && cidVal > 256)
                    {
                        nodebId = (cidVal >> 8).ToString();
                    }
                }
            }

            return new UnifiedMapReportRow
            {
                Id = nextId++,
                SessionId = (int)sessionId,
                Timestamp = ts,
                Lat = ParseFloat(GetCol(cols, map.Lat)),
                Lon = ParseFloat(GetCol(cols, map.Lon)),
                Network = network,
                Provider = CleanProvider(provider),
                Band = string.IsNullOrWhiteSpace(band) || band.Equals("NA", StringComparison.OrdinalIgnoreCase) || band.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? resolvedBand : band,
                Pci = GetCol(cols, map.Pci),
                Rssi = ParseFloat(GetCol(cols, map.Rssi)),
                Rsrp = ClampKpi(ParseFloat(GetCol(cols, map.Rsrp)), -140, -44),
                Rsrq = ClampKpi(ParseFloat(GetCol(cols, map.Rsrq)), -34, 3),
                Sinr = ClampKpi(ParseFloat(GetCol(cols, map.Sinr)), -23, 40),
                Mos = ParseFloat(GetCol(cols, map.Mos)),
                Jitter = ParseFloat(GetCol(cols, map.Jitter)),
                Latency = ParseFloat(GetCol(cols, map.Latency)),
                PacketLoss = ParseFloat(GetCol(cols, map.PacketLoss)),
                Earfcn = ParseIntSafe(earfcn),
                Bler = GetCol(cols, map.Bler),
                VolteCall = ParseIntSafe(GetCol(cols, map.VolteCall)),
                DlTpt = GetCol(cols, map.DlTpt),
                UlTpt = GetCol(cols, map.UlTpt),
                NodebId = nodebId,
                Apps = GetCol(cols, map.Apps),
                IndoorOutdoor = GetCol(cols, map.IndoorOutdoor),
                CellId = cellId,
                PuschTx = GetCol(cols, map.PuschTx),
                RawImageName = GetCol(cols, map.ImageName)
            };
        }

        private static string? ResolveZipReportBand(string? band, string? network, string? earfcn, string? primaryCellInfo)
        {
            var value = (band ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value) &&
                !value.Equals("NA", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Unknown Band", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("-1", StringComparison.OrdinalIgnoreCase))
            {
                return FormatZipBandName(value, network);
            }

            if (!string.IsNullOrWhiteSpace(primaryCellInfo))
            {
                var mBandsMatch = Regex.Match(primaryCellInfo, @"mBands=\[\s*(n?\d+)\s*\]", RegexOptions.IgnoreCase);
                if (mBandsMatch.Success)
                {
                    return FormatZipBandName(mBandsMatch.Groups[1].Value, network);
                }
            }

            if (!string.IsNullOrWhiteSpace(earfcn) && int.TryParse(earfcn.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var earfcnVal))
            {
                var derived = EarfcnToZipBandName(earfcnVal, network);
                if (!string.IsNullOrWhiteSpace(derived))
                    return derived;
            }

            if (!string.IsNullOrWhiteSpace(network) && network.Contains("5G", StringComparison.OrdinalIgnoreCase))
            {
                return "n78";
            }

            return null;
        }

        private static string EarfcnToZipBandName(int earfcn, string? network)
        {
            if (earfcn is >= 0 and <= 599) return "B1";
            if (earfcn is >= 600 and <= 1199) return "B2";
            if (earfcn is >= 1200 and <= 1949) return "B3";
            if (earfcn is >= 1950 and <= 2399) return "B4";
            if (earfcn is >= 2400 and <= 2649) return "B5";
            if (earfcn is >= 2750 and <= 3449) return "B7";
            if (earfcn is >= 3450 and <= 3799) return "B8";
            if (earfcn is >= 6150 and <= 6449) return "B20";
            if (earfcn is >= 9210 and <= 9659) return "B28";
            if (earfcn is >= 37750 and <= 38249) return "B38";
            if (earfcn is >= 38650 and <= 39649) return "B40";
            if (earfcn is >= 39650 and <= 41589) return "B41";

            if (earfcn is >= 151600 and <= 160600) return "n28";
            if (earfcn is >= 422000 and <= 434000) return "n40";
            if (earfcn is >= 620000 and <= 653333) return "n78";

            return string.Empty;
        }

        private static string FormatZipBandName(string value, string? network)
        {
            value = value.Replace("LTE", "", StringComparison.OrdinalIgnoreCase).Trim();
            value = Regex.Replace(value, @"\s+", "");

            if (Regex.IsMatch(value, @"^[BbNn]\d+[A-Za-z]?$"))
                return value.StartsWith("n", StringComparison.OrdinalIgnoreCase)
                    ? "n" + value[1..]
                    : "B" + value[1..];

            var match = Regex.Match(value, @"\d+");
            if (match.Success)
            {
                bool is5g = !string.IsNullOrWhiteSpace(network) && network.Contains("5G", StringComparison.OrdinalIgnoreCase);
                return is5g ? $"n{match.Value}" : $"B{match.Value}";
            }

            return value.Length <= 31 ? value : value[..31];
        }

        private static bool RowHasImageName(UnifiedMapReportRow r)
        {
            if (string.IsNullOrWhiteSpace(r.RawImageName)) return false;
            var v = r.RawImageName.Trim();
            return !v.Equals("NA", StringComparison.OrdinalIgnoreCase) &&
                   !v.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
                   !v.Equals("null", StringComparison.OrdinalIgnoreCase) &&
                   !v.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrimaryRegisteredRow(List<string> cols, ColumnMap map)
        {
            var primary = GetCol(cols, map.Primary);
            if (!string.IsNullOrWhiteSpace(primary) && !primary.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                return false;

            var primaryCellInfo = GetCol(cols, map.PrimaryCellInfo);
            if (!string.IsNullOrWhiteSpace(primaryCellInfo))
                return primaryCellInfo.Contains("mRegistered=YES", StringComparison.OrdinalIgnoreCase);

            return true;
        }

        private static string GetCol(List<string> cols, int idx) =>
            idx >= 0 && idx < cols.Count ? cols[idx].Trim() : "";

        private static string CleanProvider(string? value) =>
            (value ?? "").Trim().Trim('"').Trim('\'');

        private static float? ParseFloat(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"-?\d+(\.\d+)?");
            return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : (float?)null;
        }

        private static int? ParseIntSafe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"-?\d+");
            return m.Success && int.TryParse(m.Value, out var v) ? v : (int?)null;
        }

        private static float? ClampKpi(float? value, float min, float max)
        {
            if (!value.HasValue) return null;
            return Math.Min(Math.Max(value.Value, min), max);
        }

        // Minimal RFC4180-style CSV line parser (handles quoted fields with
        // embedded commas / escaped double-quotes). Assumes no embedded newlines
        // inside quoted fields, which holds for these log files.
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

        private ReportLogo? LoadCompanyLogo()
        {
            return ReportImageHelper.LoadCompanyLogo(_env);
        }

        private ReportLogo? LoadProductLogo()
        {
            return ReportImageHelper.LoadProductLogo(_env);
        }
    }

    public sealed class ZipBandDiscoveryRequest
    {
        /// <summary>Single zip file — kept for backward compatibility.</summary>
        public IFormFile? LogZip { get; set; }

        /// <summary>Multiple zip files. Takes precedence over LogZip when provided.</summary>
        public List<IFormFile>? LogZips { get; set; }

        public long? SessionIdOverride { get; set; }
    }

    public sealed class ZipReportUploadRequest
    {
        /// <summary>Single zip file — kept for backward compatibility.</summary>
        public IFormFile? LogZip { get; set; }

        /// <summary>Multiple zip files. Takes precedence over LogZip when provided.</summary>
        public List<IFormFile>? LogZips { get; set; }

        /// <summary>
        /// When true, legend entries show sample count alongside percentage,
        /// e.g. "-75 to 0  (152 | 42.30%)". Default false = percentage only.
        /// </summary>
        public bool? ShowSampleCount { get; set; }

        public string? Title { get; set; }
        public string? GeneratedBy { get; set; }
        public string? ProjectName { get; set; }
        public long? SessionIdOverride { get; set; }

        // Optional: e.g. "B3" or "B3,B8". "ALL" or empty = no filter (default).
        public string? BandFilter { get; set; }

        // Optional multi-select form binding: send Bands=B3&Bands=B8 or Bands=B3,B8.
        public List<string>? Bands { get; set; }
    }
}
