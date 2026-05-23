using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Services;

using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

using Newtonsoft.Json;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ProcessCSVController : Controller
    {
        private const long MaxZipEntryBytes = 50_000_000;
        private static readonly HashSet<string> AllowedZipExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv",
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".webp",
            ".json",
            ".geojson"
        };

        // =====================================================================
        // CSV row models (for reading files) — not EF entities
        // =====================================================================
        private sealed class NetworkLogModel
        {
            [Name("Timestamp")]
            public string? Timestamp { get; set; }

            [Name("Latitude")]
            public string? Latitude { get; set; }

            [Name("Longitude")]
            public string? Longitude { get; set; }

            [Name("Altitude")]
            public string? Altitude { get; set; }

            [Name("Indoor/Outdoor", "Indoor / Outdoor", "indoor_outdoor")]
            public string? IndoorOutdoor { get; set; }

            [Name("Phone Heading (degrees)", "Phone Heading", "phone_heading")]
            public string? PhoneHeading { get; set; }

            [Name("Battery", "Battery Level")]
            public string? Battery { get; set; }

            [Name("Network", "Network Type")]
            public string? Network { get; set; }

            [Name("dls", "Download Speed (KB/s)")]
            public string? dls { get; set; }

            [Name("uls", "Upload Speed (KB/s)")]
            public string? uls { get; set; }

            [Name("total_rx_kb", "Total Rx (KB)")]
            public string? total_rx_kb { get; set; }

            [Name("total_tx_kb", "Total Tx (KB)")]
            public string? total_tx_kb { get; set; }

            [Name("HotSpot", "HotSpot ", "Hot Spot", "hotspot")]
            public string? HotSpot { get; set; }

            [Name("Apps", "Running Apps")]
            public string? Apps { get; set; }

            [Name("App Name", "app_name")]
            public string? AppName { get; set; }

            [Name("MOS")]
            public string? MOS { get; set; }

            [Name("Jitter")]
            public string? Jitter { get; set; }

            [Name("Latency")]
            public string? Latency { get; set; }

            [Name("packet_loss", "Packet Loss")]
            public string? packet_loss { get; set; }

            [Name("call_state", "Call State")]
            public string? call_state { get; set; }

            [Name("Image Name", "image_name")]
            public string? ImageName { get; set; }

            [Name("unsent_data")]
            public string? UnsentData { get; set; }

            [Name("CI", "CI (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)", "CI  (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)")]
            public string? CI { get; set; }

            [Name("PCI", "NR-PCI / PCI / PSC")]
            public string? PCI { get; set; }

            [Name("TAC", "TAC (2G/3G - lac 4G/5G - tac)", "TAC  (2G/3G - lac 4G/5G - tac)")]
            public string? TAC { get; set; }

            [Name("RSRP", "ssRSRP / RSRP / RSCP")]
            public string? RSRP { get; set; }

            [Name("RSRQ", "ssRSRQ / RSRQ / EcNo")]
            public string? RSRQ { get; set; }

            [Name("SINR", "NR-SINR / SINR / RxQual")]
            public string? SINR { get; set; }

            [Name("DL THPT", "dl_tpt")]
            public string? dl_tpt { get; set; }

            [Name("UL THPT", "ul_tpt")]
            public string? ul_tpt { get; set; }

            [Name("EARFCN", "EARFCN (5G - NARFCN 4G - ERAFCN 3G - UARFCN 2G - ARFCN)", "EARFCN (5G - NARFCN 4G - ERAFCN 3G - UARFCN 2G - BCCH)")]
            public string? EARFCN { get; set; }

            [Name("VOLTE CALL", "volte_call")]
            public string? volte_call { get; set; }

            [Name("BAND", "Band", "band")]
            public string? BAND { get; set; }

            [Name("CQI")]
            public string? CQI { get; set; }

            [Name("BLER", "BLER (2G - bitErrorRate 3G - ber Others - BLER)", "BLER (2G - bitErrorRate 3G - ber  Others - BLER)")]
            public string? BLER { get; set; }

            [Name("Alpha Long")]
            public string? m_alpha_long { get; set; }

            [Name("Alpha Short")]
            public string? m_alpha_short { get; set; }

            [Name("No of Cells", "num_cells")]
            public string? num_cells { get; set; }

            [Name("CellInfo_1", "primary_cell_info_1")]
            public string? primary_cell_info_1 { get; set; }

            [Name("CellInfo_2", "primary_cell_info_2")]
            public string? primary_cell_info_2 { get; set; }

            [Name("CellInfo_3", "primary_cell_info_3")]
            public string? primary_cell_info_3 { get; set; }

            [Name("CellInfo_4")]
            public string? CellInfo_4 { get; set; }

            [Name("CellInfo_5")]
            public string? CellInfo_5 { get; set; }

            [Name("CellInfo_6")]
            public string? CellInfo_6 { get; set; }

            [Name("CellInfo_7")]
            public string? CellInfo_7 { get; set; }

            [Name("CellInfo_8")]
            public string? CellInfo_8 { get; set; }

            [Name("CellInfo_9")]
            public string? CellInfo_9 { get; set; }

            [Name("Throughput Details")]
            public string? ThroughputDetails { get; set; }

            [Name("NodeB Id", "NodeB Id/ Site Id", "nodeb_id")]
            public string? NodeBId { get; set; }

            [Name("Cell Id", "cell_id")]
            public string? CellId { get; set; }

            [Name("Primary", "primary")]
            public string? Primary { get; set; }

            [Name("Network Id", "NetworkId", "network_id")]
            public string? NetworkId { get; set; }

            [Name("MCC", "m_mcc")]
            public string? MCC { get; set; }

            [Name("MNC", "m_mnc")]
            public string? MNC { get; set; }

            [Name("RSSI", "RSSI (2G-RxLEV)", "rssi")]
            public string? RSSI { get; set; }

            [Name("TA", "ta")]
            public string? TA { get; set; }

            [Name("GPS Fix Type")]
            public string? GPSFixType { get; set; }

            [Name("GPS HDOP")]
            public string? GPSHDOP { get; set; }

            [Name("GPS VDOP")]
            public string? GPSVDOP { get; set; }

            [Name("Phone Antenna Gain")]
            public string? PhoneAntennaGain { get; set; }

            [Name("csiRsrp", "csi_rsrp")]
            public string? csiRsrp { get; set; }

            [Name("csiRsrq", "csi_rsrq")]
            public string? csiRsrq { get; set; }

            [Name("csiSinr", "csi_sinr")]
            public string? csiSinr { get; set; }

            [Name("Level")]
            public string? Level { get; set; }

            [Name("Speed", "Speed (km/h)", "speed")]
            public string? Speed { get; set; }

            [Name("bw", "BW")]
            public string? BW { get; set; }

            [Name("tbl_sub_session_ps_id")]
            public string? TblSubSessionPsId { get; set; }

            [Name("tbl_sub_session_cs_id")]
            public string? TblSubSessionCsId { get; set; }
        }

        private sealed class PredictionDtatModel
        {
            public string? latitude { get; set; }
            public string? longitude { get; set; }
            public string? RSRP { get; set; }
            public string? RSRQ { get; set; }
            public string? SINR { get; set; }
            public string? ServingCell { get; set; }
            public string? azimuth { get; set; }
            public string? tx_power { get; set; }
            public string? height { get; set; }
            public string? band { get; set; }
            public string? earfcn { get; set; }
            public string? reference_signal_power { get; set; }
            public string? PCI { get; set; }
            public string? Mtilt { get; set; }
            public string? Etilt { get; set; }
        }

        public class SitePredictionCsvModel
{
    // --------- Site prediction fields ----------
    public string? site { get; set; }
    public string? site_name { get; set; }
    public string? sector { get; set; }
    public string? cell_id { get; set; }
    public string? sec_id { get; set; }
    public string? longitude { get; set; }
    public string? latitude { get; set; }
    public string? tac { get; set; }
    public string? pci { get; set; }
    public string? azimuth { get; set; }
    public string? height { get; set; }
    public string? bw { get; set; }
    public string? m_tilt { get; set; }
    public string? e_tilt { get; set; }
    public string? maximum_transmission_power_of_resource { get; set; }
    public string? real_transmit_power_of_resource { get; set; }
    public string? reference_signal_power { get; set; }
    public string? cellsize { get; set; }
    public string? frequency { get; set; }
    public string? band { get; set; }
    public string? uplink_center_frequency { get; set; }
    public string? downlink_frequency { get; set; }
    public string? earfcn { get; set; }

    // ---------- old network log (IP) columns ----------
    public string? Timestamp { get; set; }
    public string? SourceIP { get; set; }
    public string? DestinationIP { get; set; }
    public string? SourcePort { get; set; }
    public string? DestinationPort { get; set; }
    public string? Protocol { get; set; }
    public string? PacketSize { get; set; }
    public string? Flags { get; set; }
    public string? TimeToLive { get; set; }
    public string? Length { get; set; }
    public string? Info { get; set; }

    // ---------- new phone/network measurement CSV columns ----------
    // note: these are currently only validated, not stored yet
    public string? Latitude { get; set; }
    public string? Longitude { get; set; }
    public string? Battery { get; set; }
    public string? Network { get; set; }
    public string? dls { get; set; }
    public string? uls { get; set; }
    public string? total_rx_kb { get; set; }
    public string? total_tx_kb { get; set; }
    public string? HotSpot { get; set; }
    public string? Apps { get; set; }
    public string? MOS { get; set; }

    [Name("CI  (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)")]
    public string? CICombined { get; set; }

    [Name("EARFCN (5G - NARFCN 4G - ERAFCN 3G - UARFCN 2G - ARFCN)")]
    public string? EARFCNCombined { get; set; }

    [Name("BLER (2G - bitErrorRate 3G - ber  Others - BLER)")]
    public string? BLERCombined { get; set; }

    public string? CQI { get; set; }
    public string? Latency { get; set; }
    public string? Jitter { get; set; }

    [Name("DL THPT")]
    public string? DL_THPT { get; set; }

    public string? Level { get; set; }
    [Name("Alpha Long")]
    public string? AlphaLong { get; set; }
    [Name("Alpha Short")]
    public string? AlphaShort { get; set; }

    public string? MCC { get; set; }
    public string? MNC { get; set; }

    [Name("TAC  (2G/3G - lac 4G/5G - tac)")]
    public string? TACCombined { get; set; }

    [Name("PCI")]
    public string? PCINetwork { get; set; }

    public string? RSRP { get; set; }
    public string? RSRQ { get; set; }
    public string? SINR { get; set; }
    public string? csiRsrp { get; set; }
    public string? csiRsrq { get; set; }
    public string? csiSinr { get; set; }

    [Name("GPS Fix Type")]
    public string? GPSFixType { get; set; }

    [Name("GPS HDOP")]
    public string? GPSHDOP { get; set; }

    [Name("GPS VDOP")]
    public string? GPSVDOP { get; set; }

    [Name("NodeB Id")]
    public string? NodeBId { get; set; }

    [Name("Phone Antenna Gain")]
    public string? PhoneAntennaGain { get; set; }

    [Name("Cell Id")]
    public string? CellIdNetwork { get; set; }

    public string? Primary { get; set; }

    [Name("Throughput Details")]
    public string? ThroughputDetails { get; set; }

    [Name("No of Cells")]
    public string? NoOfCells { get; set; }

    public string? CellInfo_1 { get; set; }
    public string? CellInfo_2 { get; set; }

    // optional extras
    public string? cluster { get; set; }
    public string? Technology { get; set; }
}
        // =====================================================================
        // ctor + services
        // =====================================================================
        private readonly ApplicationDbContext db;
        private readonly CommonFunction cf;
        private readonly RedisService _redis;

        public ProcessCSVController(ApplicationDbContext context, CommonFunction _cf, RedisService redis)
        {
            db = context;
            cf = _cf;
            _redis = redis;
        }

        private void InvalidateNetworkLogCachesBestEffort()
        {
            if (_redis == null || !_redis.IsConnected)
                return;

            var patterns = new[]
            {
                "networklog:v2:*",
                "networklog:v3:*",
                "mapview:*",
                "daterangelog:*",
                "latlon:dist:*",
                "n78_simple_kpi:*",
                "n78_neighbours:*",
                "alllogs:*"
            };

            foreach (var pattern in patterns)
            {
                try
                {
                    _redis.DeleteByPatternAsync(pattern).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" Redis invalidation error [{pattern}]: {SafeException.Get(ex)}");
                }
            }
        }

        // =====================================================================
        // Helpers — header normalization & validators
        // =====================================================================
        private static string NormalizeHeader(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var t = s.Trim().Trim('\uFEFF').ToLowerInvariant();
            t = t.Replace("_", "").Replace(" ", "").Replace("-", "").Replace("/", "");
            t = t.Replace("(", "").Replace(")", "");
            return t;
        }

        private static IEnumerable<string> SynonymsFor(string canonical) =>
            canonical switch
            {
                "sitename" => new[] { "sitename", "site" },
                "secid" => new[] { "secid", "sectorid" },
                "bw" => new[] { "bw", "bandwidth" },
                "mtilt" => new[] { "mtilt", "mtiltdeg" },
                "etilt" => new[] { "etilt", "etiltdeg" },
                "maximumtransmissionpowerofresource" =>
                    new[] { "maximumtransmissionpowerofresource", "maximumtransmitpowerofresource", "maximumtxpowerofresource" },
                "realtransmitpowerofresource" =>
                    new[] { "realtransmitpowerofresource", "realtransmissionpowerofresource", "realtxpowerofresource" },
                "referencesignalpower" => new[] { "referencesignalpower", "refsignalpower" },
                "uplinkcenterfrequency" => new[] { "uplinkcenterfrequency", "ulcenterfrequency" },
                "downlinkfrequency" => new[] { "downlinkfrequency", "downlinkcenterfrequency", "dlcenterfrequency" },
                "ci" => new[] { "ci", "eci", "cid", "cellidentity", "cellidentifier", "cgi", "ecgi" },
                _ => Array.Empty<string>()
            };

        [NonAction]
        private bool ValidateCsvHeadersFlexible(string filePath, string[] expectedHeaders, out string missingHeaders)
        {
            missingHeaders = "";
            var headerLine = System.IO.File.ReadLines(filePath).FirstOrDefault();
            if (headerLine == null)
            {
                missingHeaders = "No header row found";
                return false;
            }

            var actualSet = headerLine
                .Split(',')
                .Select(NormalizeHeader)
                .Where(h => !string.IsNullOrEmpty(h))
                .ToHashSet();

            var missing = new List<string>();

            foreach (var exp in expectedHeaders)
            {
                var canonical = NormalizeHeader(exp);
                bool found = actualSet.Contains(canonical);

                if (!found)
                {
                    foreach (var syn in SynonymsFor(canonical))
                    {
                        if (actualSet.Contains(syn)) { found = true; break; }
                    }
                }

                if (!found) missing.Add(exp);
            }

            if (missing.Count > 0)
            {
                missingHeaders = string.Join(", ", missing);
                return false;
            }

            return true;
        }

        [NonAction]
        public bool ValidateCsvHeadersStrict(string filePath, string[] expectedHeaders, out string missingHeaders)
        {
            bool isValidTemplate = false;
            missingHeaders = "";

            var firstLine = System.IO.File.ReadLines(filePath).FirstOrDefault();
            if (firstLine != null)
            {
                var actualHeaders = firstLine.Split(',').Select(h => h.Trim()).ToArray();
                var missing = expectedHeaders.Where(expected => !actualHeaders.Contains(expected, StringComparer.OrdinalIgnoreCase)).ToList();
                isValidTemplate = missing.Count == 0;
                if (!isValidTemplate)
                    missingHeaders = string.Join(", ", missing);
            }

            return isValidTemplate;
        }

        // =====================================================================
        // Public API
        // =====================================================================
        /// <summary>Upload a Site Prediction CSV (or ZIP of CSVs) and process it (fileType=15).</summary>
        [HttpPost("upload/site-prediction")]
[Consumes("multipart/form-data")]
public IActionResult UploadSitePrediction(
    [FromForm] IFormFile file,
    [FromForm] int projectId,
    [FromForm] int excelId = 0,
    [FromForm] string? remarks = "")
{
    if (file == null || file.Length == 0)
        return BadRequest(new { success = false, message = "File is required." });

    try
    {
        var incomingRoot = Path.Combine(Directory.GetCurrentDirectory(), "UploadedExcels", "Incoming");
        Directory.CreateDirectory(incomingRoot);

        var savedPath = Path.Combine(incomingRoot, $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
        using (var fs = new FileStream(savedPath, FileMode.Create))
            file.CopyTo(fs);

        // 🔹 EXTRA: validate headers for site-prediction + network log CSV before processing
        string[] expectedHeaders = new string[]
        {
            // ---------- site prediction columns ----------
            "site","site_name","sector","cell_id","sec_id","longitude","latitude","tac","pci","azimuth",
            "height","bw","m_tilt","e_tilt","maximum_transmission_power_of_resource",
            "real_transmit_power_of_resource","reference_signal_power","cellsize","frequency","band",
            "uplink_center_frequency","downlink_frequency","earfcn",

            // ---------- old network log (IP) columns ----------
            "Timestamp","SourceIP","DestinationIP","SourcePort","DestinationPort",
            "Protocol","PacketSize","Flags","TimeToLive","Length","Info",

            // ---------- new phone/network measurement CSV columns ----------
            "Latitude","Longitude","Battery","Network","dls","uls",
            "total_rx_kb","total_tx_kb","HotSpot","Apps","MOS",

            "CI  (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)",
            "EARFCN (5G - NARFCN 4G - ERAFCN 3G - UARFCN 2G - ARFCN)",
            "BLER (2G - bitErrorRate 3G - ber  Others - BLER)",
            "CQI",
            "Latency",
            "Jitter",
            "DL THPT",
            "Level",
            "Alpha Long",
            "Alpha Short",

            "MCC",
            "MNC",
            "TAC  (2G/3G - lac 4G/5G - tac)",
            "PCI",
            "RSRP",
            "RSRQ",
            "SINR",
            "csiRsrp",
            "csiRsrq",
            "csiSinr",

            "GPS Fix Type",
            "GPS HDOP",
            "GPS VDOP",

            "NodeB Id",
            "Phone Antenna Gain",
            "Cell Id",
            "Primary",
            "Throughput Details",
            "No of Cells",
            "CellInfo_1",
            "CellInfo_2"
            // cluster / Technology are optional
        };

        string missingHeaders;
        if (!ValidateCsvHeadersFlexible(savedPath, expectedHeaders, out missingHeaders))
        {
            // if header validation fails, we return a proper error
            return BadRequest(new
            {
                success = false,
                message = $"Invalid file: '{missingHeaders}' columns are missing."
            });
        }

        // 🔹 ORIGINAL LOGIC – unchanged
        string errorMsg;
        bool ok = ProcessFile(
            fileType: 15,
            excelID: excelId,
            directorypath: savedPath,
            originalFileName: file.FileName,
            polygonFilePath: null,
            projectId: projectId,
            Remarks: remarks ?? string.Empty,
            errorMsag: out errorMsg
        );

        if (!ok)
            return BadRequest(new { success = false, message = errorMsg });

        return Ok(new
        {
            success = true,
            message = "Site prediction processed successfully.",
            details = errorMsg
        });
    }
    catch (Exception)
    {
        return StatusCode(500, new { success = false, message = "An internal server error occurred." });
    }
}

        /// <summary>
        /// Fetch paged site prediction rows.
        /// Returns: network (cluster/operator), earfcn, site, latitude, longitude, azimuth, height, band, technology (+ ids).
        /// </summary>
        [HttpGet("site-prediction")]
        public IActionResult GetSitePredictions([FromQuery] int projectId = 0, [FromQuery] int take = 50, [FromQuery] int skip = 0)
        {
            if (take <= 0) take = 50;
            if (skip < 0) skip = 0;

            var q = db.site_prediction.AsQueryable();
            if (projectId > 0) q = q.Where(r => r.tbl_project_id == projectId);

            var total = q.Count();

            var items = q.OrderBy(r => r.id)
                         .Skip(skip)
                         .Take(take)
                         .Select(r => new
                         {
                             r.id,
                             site = r.site ?? r.site_name,   // null-safe site value
                             r.sector,
                             r.cell_id,
                             r.sec_id,

                             network   = ResolveOperator(r), // operator name
                             r.earfcn,
                             latitude  = r.latitude,
                             longitude = r.longitude,
                             r.azimuth,
                             r.height,
                             r.band,
                             technology = r.technology
                         })
                         .ToList();

            return Ok(new { total, skip, take, count = items.Count, items });
        }

        // =====================================================================
        // Internal pipeline
        // =====================================================================
        [NonAction]
        public bool Process(
            int ExcelId,
            string directoryPath,
            string originalFileName,
            string? polygonFilePath,
            int fileType,
            int projectId,
            string Remarks,
            out string errorMsag,
            int uploadedByUserId = 0)
        {
            return ProcessFile(
                fileType,
                ExcelId,
                directoryPath,
                originalFileName,
                polygonFilePath,
                projectId,
                Remarks,
                out errorMsag,
                uploadedByUserId);
        }

        [NonAction]
        public bool ProcessFile(
            int fileType,
            int excelID,
            string directorypath,
            string originalFileName,
            string? polygonFilePath,
            int projectId,
            string Remarks,
            out string errorMsag,
            int uploadedByUserId = 0)
        {
            bool IsValidSheet = true;
            List<string> errorList = new();
            List<string> allErrorList = new();
            List<string> uploadedSuccessSheetList = new();
            int rowInserted = 0;
            int rowUpdated = 0;
            errorMsag = "";

            string extractpath = string.Empty;

            try
            {
                if (System.IO.File.Exists(directorypath))
                {
                    extractpath = Path.Combine(Directory.GetCurrentDirectory(), "UploadedExcels", "Extract" + DateTime.Now.ToString("MMddyyyyHmmss"));

                    List<string> files = new();
                    List<string> polygonFiles = new();
                    List<string> imageList = new();

                    bool isZipFile = IsValidZip(directorypath);

                    if (isZipFile)
                    {
                        (files, imageList) = ExtractZipAndSeparateFiles(directorypath, extractpath);
                    }
                    else
                    {
                        files.Add(directorypath);
                    }

	                    int sessionId = 0;
	                    int resolvedCompanyId = 0;
	                    var strategy = db.Database.CreateExecutionStrategy();
	                    strategy.Execute(() =>
	                    {
                        // outer transaction only for fileType=1 to keep session + logs atomic
                        using var outerTx = fileType == 1 ? db.Database.BeginTransaction() : null;

                        if (fileType == 1)
                        {
                            var resolvedUserId = uploadedByUserId > 0 ? uploadedByUserId : cf.UserId;
                            if (resolvedUserId <= 0)
                            {
                                allErrorList.Add("Unable to resolve logged-in user for session creation.");
                                IsValidSheet = false;
                            }
	                            else
	                            {
	                                resolvedCompanyId = db.tbl_user
	                                    .AsNoTracking()
	                                    .Where(u => u.id == resolvedUserId)
	                                    .Select(u => u.company_id ?? 0)
	                                    .FirstOrDefault();

	                                var session = new tbl_session
	                                {
	                                    user_id = resolvedUserId,
                                    type = "network",
                                    notes = string.IsNullOrEmpty(Remarks) ? "file upload" : Remarks,
                                    uploaded_on = DateTime.Now,
                                    // tbl_upload_id column is varchar, convert int to string
                                    tbl_upload_id = excelID > 0 ? excelID.ToString() : null
                                };
                                db.tbl_session.Add(session);
                                db.SaveChanges();
                                // session.id is nullable in the model
                                sessionId = session.id ?? 0;
                            }
                        }
                        else if (fileType == 2)
                        {
                            if (!string.IsNullOrEmpty(polygonFilePath))
                            {
                                bool isPolygonZipFile = IsValidZip(polygonFilePath);
                                if (isPolygonZipFile)
                                {
                                    polygonFiles = ExtractJsonFiles(polygonFilePath, extractpath);
                                }
                                else
                                {
                                    polygonFiles.Add(polygonFilePath);
                                }

                                foreach (string file in polygonFiles)
                                {
                                    if (!string.IsNullOrEmpty(file))
                                    {
                                        bool polygonOk = ProcessPredictionPloygonJson(file, excelID, projectId, ref rowInserted, ref rowUpdated, out errorList);
                                        if (!polygonOk) IsValidSheet = false;
                                        if (errorList.Count > 0) allErrorList.AddRange(errorList);
                                    }
                                }
                            }
                            else
                            {
                                allErrorList.Add("Polygon file path is required for fileType 2.");
                                IsValidSheet = false;
                            }
                        }

                        foreach (string file in files)
                        {
                            if (fileType == 1)
                            {
	                                if (sessionId <= 0)
	                                {
	                                    IsValidSheet = false;
	                                    allErrorList.Add("Session creation failed. Network logs were not processed.");
	                                    break;
	                                }
	                                IsValidSheet = ProcessNetLogWorkSheet(sessionId, resolvedCompanyId, file, imageList, excelID, ref rowInserted, ref rowUpdated, out errorList, outerTx);
	                            }
                            else if (fileType == 2)
                                IsValidSheet = ProcessCtrPredictionSheet(file, excelID, projectId, ref rowInserted, ref rowUpdated, out errorList);
                            else if (fileType == 15)
                                IsValidSheet = ProcessSitePredictionSheet(file, excelID, projectId, ref rowInserted, ref rowUpdated, out errorList, uploadedSuccessSheetList);

                            if (errorList.Count > 0)
                                allErrorList.AddRange(errorList);
                        }

                        if (outerTx != null)
                        {
                            if (fileType == 1)
                            {
                                // For large network-log uploads, keep the created session and
                                // commit valid rows even if some rows had validation errors.
                                if (sessionId > 0 && IsValidSheet)
                                    outerTx.Commit();
                                else
                                    outerTx.Rollback();
                            }
                            else
                            {
                                if (IsValidSheet && allErrorList.Count == 0)
                                    outerTx.Commit();
                                else
                                    outerTx.Rollback();
                            }
                        }
                    });


                    if (IsValidSheet && imageList.Count > 0)
                    {
                        var imgpath = Path.Combine(Directory.GetCurrentDirectory(), "UploadedExcels", "Images_" + sessionId);
                        if (!Directory.Exists(imgpath))
                            Directory.CreateDirectory(imgpath);
                        foreach (var imagePath in imageList)
                        {
                            string fn = Path.GetFileName(imagePath);
                            string destPath = Path.Combine(imgpath, fn);

                            if (System.IO.File.Exists(destPath))
                                System.IO.File.Delete(destPath);

                            System.IO.File.Move(imagePath, destPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                IsValidSheet = false;
                errorMsag = "Exception " + SafeException.Get(ex);
            }

            try
            {
                if (!string.IsNullOrEmpty(extractpath) && Directory.Exists(extractpath))
                {
                    Directory.Delete(extractpath, recursive: true);
                }
            }
            catch { }

            if (allErrorList.Count > 0)
            {
                errorMsag = "Errorneous Sheets:" + Environment.NewLine + string.Join(Environment.NewLine, allErrorList);

                if (uploadedSuccessSheetList.Count > 0)
                {
                    errorMsag += Environment.NewLine + "Uploaded Sheets: " + Environment.NewLine + string.Join(Environment.NewLine, uploadedSuccessSheetList);
                }
            }
            else if (fileType == 15 && uploadedSuccessSheetList.Count > 0)
            {
                errorMsag += Environment.NewLine + "Uploaded Sheets: " + Environment.NewLine + string.Join(Environment.NewLine, uploadedSuccessSheetList);
            }

            return IsValidSheet;
        }

        // =====================================================================
        // Zip / JSON helpers
        // =====================================================================
        [NonAction]
        public bool IsValidJson(string filePath)
        {
            try
            {
                string jsonContent = System.IO.File.ReadAllText(filePath);
                using (JsonDocument.Parse(jsonContent))
                    return true;
            }
            catch { return false; }
        }

        [NonAction]
        public bool IsValidZip(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 4)
                {
                    return false;
                }

                Span<byte> header = stackalloc byte[4];
                if (fs.Read(header) != header.Length)
                {
                    return false;
                }

                if (!header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) &&
                    !header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) &&
                    !header.SequenceEqual(new byte[] { 0x50, 0x4B, 0x07, 0x08 }))
                {
                    return false;
                }

                fs.Position = 0;
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read, true);
                return archive.Entries.Any(entry => !string.IsNullOrEmpty(entry.Name));
            }
            catch
            {
                return false;
            }
        }

        private static string GetNormalizedDestinationPath(string extractPath, string entryName)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(extractPath, entryName));
            if (!destinationPath.StartsWith(extractPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Zip entry escapes the extraction directory.");
            }

            return destinationPath;
        }

        private static void ExtractZipToDirectorySecurely(ZipArchive archive, string extractPath)
        {
            if (!Directory.Exists(extractPath))
            {
                Directory.CreateDirectory(extractPath);
            }

            if (archive.Entries.Count > 1000)
            {
                throw new InvalidOperationException("Zip archive contains too many entries.");
            }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                if (entry.Length > MaxZipEntryBytes || entry.CompressedLength > MaxZipEntryBytes)
                {
                    throw new InvalidOperationException("Zip archive contains an entry that exceeds the allowed file size.");
                }

                var destinationPath = GetNormalizedDestinationPath(extractPath, entry.FullName);
                var extension = Path.GetExtension(destinationPath);
                if (string.IsNullOrEmpty(extension) || !AllowedZipExtensions.Contains(extension))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? extractPath);

                using var entryStream = entry.Open();
                using var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                entryStream.CopyTo(outputStream);
            }
        }

        [NonAction]
        public (List<string> CsvFiles, List<string> ImageFiles) ExtractZipAndSeparateFiles(string zipFilePath, string extractPath)
        {
            List<string> csvFiles = new();
            List<string> imageFiles = new();

            using var fs = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read, true);

            ExtractZipToDirectorySecurely(archive, extractPath);

            var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png",
                ".jpg",
                ".jpeg",
                ".gif",
                ".bmp",
                ".webp"
            };

            var allFiles = Directory.GetFiles(extractPath, "*.*", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                var extension = Path.GetExtension(file);
                if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    csvFiles.Add(file);
                }
                else if (imageExtensions.Contains(extension))
                {
                    imageFiles.Add(file);
                }
            }

            return (csvFiles, imageFiles);
        }

        [NonAction]
        public List<string> ExtractJsonFiles(string zipFilePath, string extractPath)
        {
            using var fs = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read, true);

            ExtractZipToDirectorySecurely(archive, extractPath);

            var jsonFilesOut = new List<string>();
            jsonFilesOut.AddRange(Directory.GetFiles(extractPath, "*.json", SearchOption.AllDirectories));
            jsonFilesOut.AddRange(Directory.GetFiles(extractPath, "*.geojson", SearchOption.AllDirectories));

            return jsonFilesOut;
        }

        // =====================================================================
        // Process: Network Log (fileType=1)
        // =====================================================================
        [NonAction]
	public bool ProcessNetLogWorkSheet(
	    int sessionId,
	    int companyId,
	    string filePath,
	    List<string> imageList,
	    int ExcelID,
	    ref int rowInserted,
    ref int rowUpdated,
    out List<string> errorList,
    Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? outerTx = null)
{
	    bool isColValValid = true;
	    errorList = new();
	    string fileName = Path.GetFileName(filePath);

	    try
	    {
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Header ko trim kar dega (leading / trailing spaces hata dega)
            PrepareHeaderForMatch = args => args.Header.Trim(),
            // Agar koi column missing ho to error NA throw kare
            MissingFieldFound = null,
            // CsvHelper ki strict header validation bilkul off
            HeaderValidated = null
        };

        using var csv = new CsvReader(reader, config);

        //  YAHAN KOI expectedHeaders / ValidateCsvHeadersStrict NAHI HAI
        //  Direct records read karenge; mapping [Name(...)] handle karega

        try
        {
            int rowIndex = 0;
            bool hasAnyRecord = false;

            foreach (var row in csv.GetRecords<NetworkLogModel>())
            {
                hasAnyRecord = true;
                rowIndex++;

                // --------- TIMESTAMP HANDLING ----------
                string rawDate = (row.Timestamp ?? string.Empty)
                    .Trim()
                    .Trim('\uFEFF');

                if (string.IsNullOrWhiteSpace(rawDate))
                {
                    // khali timestamp row skip
                    continue;
                }

                DateTime timestamp;
                string[] formats =
                {
                    "yyyy-MM-dd HH:mm:ss.fff",
                    "yyyy-MM-dd HH:mm:ss",
                    "dd-MM-yyyy HH:mm:ss",
                    "dd-MM-yyyy HH:mm:ss.fff",
                    "MM/dd/yyyy HH:mm:ss",
                    "MM/dd/yyyy HH:mm:ss.fff"
                };

                bool parsed =
                    DateTime.TryParseExact(rawDate, formats,
                                            CultureInfo.InvariantCulture,
                                            DateTimeStyles.AllowWhiteSpaces,
                                            out timestamp)
                    || DateTime.TryParse(rawDate,
                                         CultureInfo.InvariantCulture,
                                         DateTimeStyles.AllowWhiteSpaces,
                                         out timestamp);

                if (!parsed)
                {
                    // Xiaomi MODEL wali metadata row wagaira yahan aa jayegi, usko ignore kar denge
                    if (!char.IsDigit(rawDate.FirstOrDefault()))
                    {
                        // meta row hai, skip without error
                        continue;
                    }

                    // Agar tum bilkul bhi error nahi chahte ho, ye 3 line bhi hata sakte ho
                    errorList.Add($"Row {rowIndex} ({row.Timestamp}): Invalid Timestamp in sheet {fileName}");
                    continue;
                }
                // --------- END TIMESTAMP HANDLING ----------

	                // Upload creates a fresh session, so rows are always inserts.
	                // Avoid per-row DB lookup to keep large uploads fast.
	                var entity = new tbl_network_log();

	                entity.session_id = sessionId;
	                entity.company_id = companyId;
	                entity.timestamp  = timestamp;
	                entity.lat        = ParseFloat(row.Latitude);
                entity.lon        = ParseFloat(row.Longitude);
                entity.battery    = ParseInt(row.Battery);

                entity.dls        = row.dls;
                entity.uls        = row.uls;
                entity.call_state = row.call_state;
                entity.app_name   = row.AppName;
                entity.apps       = string.IsNullOrWhiteSpace(row.Apps) ? row.AppName : row.Apps;

                var parsedMcc = ParseInt(row.MCC);
                var parsedMnc = ParseInt(row.MNC);
                entity.m_mcc = parsedMcc;
                entity.m_mnc = parsedMnc;
                entity.mcc   = parsedMcc;
                entity.mnc   = parsedMnc;

                entity.ta = row.TA;
                entity.level = ParseInt(row.Level);
                entity.Speed = ParseFloat(row.Speed);

                entity.nodeb_id = row.NodeBId;
                entity.cell_id = row.CellId;
                entity.primary = row.Primary;
                entity.network_id = row.NetworkId;
                entity.bw = row.BW;
                entity.gps_fix_type = row.GPSFixType;
                entity.gps_hdop = ParseFloat(row.GPSHDOP);
                entity.gps_vdop = ParseFloat(row.GPSVDOP);
                entity.phone_antenna_gain = row.PhoneAntennaGain;
                entity.csi_rsrp = ParseFloat(row.csiRsrp);
                entity.csi_rsrq = ParseFloat(row.csiRsrq);
                entity.csi_sinr = ParseFloat(row.csiSinr);
                entity.tbl_sub_session_ps_id = ParseLong(row.TblSubSessionPsId);
                entity.tbl_sub_session_cs_id = ParseLong(row.TblSubSessionCsId);

                if (!string.IsNullOrEmpty(row.HotSpot))
                {
                    var f = imageList.FirstOrDefault(a => a == row.HotSpot);
                    if (f != null && System.IO.File.Exists(f))
                        entity.image_path = row.HotSpot;
                    else
                        entity.hotspot = row.HotSpot;
                }

                entity.num_cells = ParseInt(row.num_cells);

                entity.network       = row.Network;
                entity.m_alpha_long  = row.m_alpha_long;
                entity.m_alpha_short = row.m_alpha_short;
                entity.mci           = row.CI;
                entity.pci           = row.PCI;
                entity.tac           = row.TAC;
                entity.earfcn        = row.EARFCN;

                bool valid;
                entity.rsrp = ValidParseFloat(row.RSRP, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.RSRP))
                {
                    errorList.Add($"Row {rowIndex} ({row.RSRP}): Invalid RSRP");
                    continue;
                }

                entity.rsrq = ValidParseFloat(row.RSRQ, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.RSRQ))
                {
                    errorList.Add($"Row {rowIndex} ({row.RSRQ}): Invalid RSRQ");
                    continue;
                }

                entity.sinr = ValidParseFloat(row.SINR, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.SINR))
                {
                    errorList.Add($"Row {rowIndex} ({row.SINR}): Invalid SINR");
                    continue;
                }

                entity.total_rx_kb = row.total_rx_kb;
                entity.total_tx_kb = row.total_tx_kb;

                entity.mos = ValidParseFloat(row.MOS, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.MOS))
                {
                    errorList.Add($"Row {rowIndex} ({row.MOS}): Invalid MOS");
                    continue;
                }

                entity.jitter = ValidParseFloat(row.Jitter, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.Jitter))
                {
                    errorList.Add($"Row {rowIndex} ({row.Jitter}): Invalid Jitter");
                    continue;
                }

                entity.latency = ValidParseFloat(row.Latency, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.Latency))
                {
                    errorList.Add($"Row {rowIndex} ({row.Latency}): Invalid Latency");
                    continue;
                }

                entity.packet_loss = ValidParseFloat(row.packet_loss, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.packet_loss))
                {
                    errorList.Add($"Row {rowIndex} ({row.packet_loss}): Invalid Packet Loss");
                    continue;
                }

                entity.dl_tpt     = row.dl_tpt;
                entity.ul_tpt     = row.ul_tpt;
                entity.volte_call = row.volte_call;
                entity.band       = row.BAND;

                entity.cqi = ValidParseFloat(row.CQI, out valid);
                if (!valid && !string.IsNullOrWhiteSpace(row.CQI))
                {
                    errorList.Add($"Row {rowIndex} ({row.CQI}): Invalid CQI");
                    continue;
                }

                entity.bler = row.BLER;

                entity.primary_cell_info_1 = row.primary_cell_info_1;
                entity.primary_cell_info_2 = row.primary_cell_info_2;
                entity.primary_cell_info_3 = row.primary_cell_info_3;
                if (!string.IsNullOrWhiteSpace(row.ThroughputDetails))
                    entity.all_neigbor_cell_info = row.ThroughputDetails;

                if (entity.id > 0)
                    db.Entry(entity).State = EntityState.Modified;
                else
                    db.tbl_network_log.Add(entity);

                rowInserted++;
            }

	            if (!hasAnyRecord)
	            {
	                return true;
	            }

	            if (isColValValid)
	            {
	                db.SaveChanges();
	                InvalidateNetworkLogCachesBestEffort();
	            }
	            else
	            {
	                // detach changes so caller can rollback or skip invalid rows safely
	                foreach (var e in db.ChangeTracker.Entries()
	                             .Where(e => e.State is EntityState.Added or EntityState.Modified))
	                    e.State = EntityState.Detached;
	            }
	        }
	        catch (Exception ex)
	        {
	            foreach (var e in db.ChangeTracker.Entries()
	                         .Where(e => e.State is EntityState.Added or EntityState.Modified))
	                e.State = EntityState.Detached;

	            errorList.Add($"{fileName} General error: {(SafeException.GetInnermost(ex))}");
	            return false;
        }
    }
    catch (Exception ex)
    {
        errorList.Add($"{fileName} General error: {(SafeException.GetInnermost(ex))}");
        return false;
    }

    return isColValValid;
}
        // =====================================================================
        // Process: CTR Prediction (fileType=2)
        // =====================================================================
        [NonAction]
        public bool ProcessCtrPredictionSheet(string filePath, int ExcelID, int projectId, ref int rowInserted, ref int rowUpdated, out List<string> errorList)
        {
            bool isColValValid = true;
            errorList = new();

            using var reader = new StreamReader(filePath, Encoding.UTF8);
            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { PrepareHeaderForMatch = args => args.Header.Trim(), };
            using var csv = new CsvReader(reader, config);

            string[] expectedHeaders = "latitude,longitude,RSRP,RSRQ,SINR,ServingCell,azimuth,tx_power,height,band,earfcn,reference_signal_power,PCI,Mtilt,Etilt"
                .Split(',').Select(a => a.Trim()).ToArray();

            string missingHeaders;
            if (!ValidateCsvHeadersStrict(filePath, expectedHeaders, out missingHeaders))
            {
                errorList.Add("invalid file:- '" + missingHeaders + "' columns are missing");
                return false;
            }

	            var records = csv.GetRecords<PredictionDtatModel>().ToList();
	            if (records.Count == 0) return true;

	            try
	            {
	                foreach (var row in records)
                {
                    var obj = new tbl_prediction_data
                    {
                        tbl_project_id = projectId,
                        lat = ParseFloat(row.latitude),
                        lon = ParseFloat(row.longitude),
                        rsrp = ParseFloat(row.RSRP),
                        rsrq = ParseFloat(row.RSRQ),
                        sinr = ParseFloat(row.SINR),
                        serving_cell = row.ServingCell,
                        azimuth = row.azimuth,
                        tx_power = row.tx_power,
                        height = row.height,
                        band = row.band,
                        earfcn = row.earfcn,
                        reference_signal_power = row.reference_signal_power,
                        pci = row.PCI,
                        mtilt = row.Mtilt,
                        etilt = row.Etilt
                    };

                    if (obj.id > 0) db.Entry(obj).State = EntityState.Modified;
                    else db.tbl_prediction_data.Add(obj);

                    rowInserted++;
	                }

	                db.SaveChanges();
	            }
	            catch (Exception ex)
	            {
	                foreach (var e in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified)){
	                    e.State = EntityState.Detached;
	                }
                errorList.Add($"General error: An internal server error occurred.");
                return false;
            }

            return isColValValid;
        }

        // =====================================================================
        // Process: Site Prediction (fileType=15)
        // =====================================================================
        [NonAction]
public bool ProcessSitePredictionSheet(
    string filePath,
    int ExcelID,
    int projectId,
    ref int rowInserted,
    ref int rowUpdated,
    out List<string> errorList,
    List<string> uploadedSuccessSheetList)
{
    bool isColValValid = true;
    errorList = new();
    string fileName = Path.GetFileName(filePath);

    try
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.Trim(),
            MissingFieldFound = null
        };
        using var csv = new CsvReader(reader, config);

        // 🔹 ALL REQUIRED HEADERS (site + all CSV columns)
        string[] expectedHeaders = new string[]
        {
            // ---------- site prediction columns ----------
            "site","site_name","sector","cell_id","sec_id","longitude","latitude","tac","pci","azimuth",
            "height","bw","m_tilt","e_tilt","maximum_transmission_power_of_resource",
            "real_transmit_power_of_resource","reference_signal_power","cellsize","frequency","band",
            "uplink_center_frequency","downlink_frequency","earfcn",
             

            // ---------- old network log (IP) columns ----------
            "Timestamp","SourceIP","DestinationIP","SourcePort","DestinationPort",
            "Protocol","PacketSize","Flags","TimeToLive","Length","Info",

            // ---------- new phone/network measurement CSV columns ----------
            "Latitude","Longitude","Battery","Network","dls","uls",
            "total_rx_kb","total_tx_kb","HotSpot","Apps","MOS",

            "CI  (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)",
            "EARFCN (5G - NARFCN 4G - ERAFCN 3G - UARFCN 2G - ARFCN)",
            "BLER (2G - bitErrorRate 3G - ber  Others - BLER)",
            "CQI",
            "Latency",
            "Jitter",
            "DL THPT",
            "Level",
            "Alpha Long",
            "Alpha Short",

            "MCC",
            "MNC",
            "TAC  (2G/3G - lac 4G/5G - tac)",
            "PCI",
            "RSRP",
            "RSRQ",
            "SINR",
            "csiRsrp",
            "csiRsrq",
            "csiSinr",

            "GPS Fix Type",
            "GPS HDOP",
            "GPS VDOP",

            "NodeB Id",
            "Phone Antenna Gain",
            "Cell Id",
            "Primary",
            "Throughput Details",
            "No of Cells",
            "CellInfo_1",
            "CellInfo_2"
            // cluster / Technology still optional
        };

        string missingHeaders;
        if (!ValidateCsvHeadersFlexible(filePath, expectedHeaders, out missingHeaders))
        {
            errorList.Add(fileName + " invalid file:- '" + missingHeaders + "' columns are missing");
            return false;
        }

	        var records = csv.GetRecords<SitePredictionCsvModel>().ToList();
	        if (records.Count == 0)
	        {
	            return true; // no data rows
	        }

	        try
	        {
	            int rowIndex = 1;

            foreach (var row in records)
            {
                var parsedCellId = ParseCompositeCellId(row.cell_id);
                var parsedSecId = TryInt(row.sec_id) ?? TryInt(row.sector) ?? parsedCellId;

                var temp = new site_prediction
                {
                    earfcn = TryInt(row.earfcn),
                    band = TryInt(row.band),
                    frequency = row.frequency
                };

                var obj = new site_prediction
                {
                    tbl_project_id = projectId,
                    tbl_upload_id = ExcelID,

                    site = TryInt(row.site),
                    site_name = TryInt(row.site_name),
                    sector = row.sector,

                    cell_id = parsedCellId,
                    sec_id = parsedSecId,

                    longitude = TryDouble(row.longitude),
                    latitude = TryDouble(row.latitude),

                    tac = TryInt(row.tac),
                    pci = TryInt(row.pci),
                    azimuth = TryInt(row.azimuth),

                    height = TryInt(row.height),
                    bw = TryInt(row.bw),
                    m_tilt = TryInt(row.m_tilt),
                    e_tilt = TryInt(row.e_tilt),

                    maximum_transmission_power_of_resource =
                        TryDouble(row.maximum_transmission_power_of_resource),
                    real_transmit_power_of_resource =
                        TryDouble(row.real_transmit_power_of_resource),
                    reference_signal_power =
                        TryDouble(row.reference_signal_power),

                    cellsize  = row.cellsize,
                    frequency = row.frequency,
                    band      = TryInt(row.band),

                    uplink_center_frequency = row.uplink_center_frequency,
                    downlink_frequency      = row.downlink_frequency,
                    earfcn                  = TryInt(row.earfcn),

                    // existing network log fields you were already storing
                    Timestamp       = row.Timestamp,
                    SourceIP        = row.SourceIP,
                    DestinationIP   = row.DestinationIP,
                    SourcePort      = row.SourcePort,
                    DestinationPort = row.DestinationPort,
                    Protocol        = row.Protocol,
                    PacketSize      = row.PacketSize,
                    Flags           = row.Flags,
                    TimeToLive      = row.TimeToLive,
                    Length          = row.Length,
                    Info            = row.Info,

                    // extra phone metrics stored (optional; you can drop these if not needed in DB)
                    Battery         = row.Battery,
                    Network         = row.Network,
                    dls             = row.dls,
                    uls             = row.uls,
                    total_rx_kb     = row.total_rx_kb,
                    total_tx_kb     = row.total_tx_kb,
                    HotSpot         = row.HotSpot,
                    Apps            = row.Apps,
                    MOS             = row.MOS,

                    // cluster / technology
                    cluster    = string.IsNullOrWhiteSpace(row.cluster) ? ResolveOperator(temp) : row.cluster,
                    technology = row.Technology
                };

                if (obj.latitude == null || obj.longitude == null)
                {
                    errorList.Add($"Row {rowIndex}: Missing or invalid Latitude/Longitude.");
                    isColValValid = false;
                    rowIndex++;
                    continue;
                }

                db.Set<site_prediction>().Add(obj);
                rowInserted++;
                rowIndex++;
            }

	            if (isColValValid)
	            {
	                db.SaveChanges();
	                uploadedSuccessSheetList.Add(fileName);
	            }
	            else
	            {
	                foreach (var e in db.ChangeTracker.Entries()
	                             .Where(e => e.State is EntityState.Added or EntityState.Modified))
	                {
                    e.State = EntityState.Detached;
                }
            }
	        }
	        catch (Exception ex)
	        {
	            foreach (var e in db.ChangeTracker.Entries()
	                         .Where(e => e.State is EntityState.Added or EntityState.Modified))
	            {
                e.State = EntityState.Detached;
            }

            errorList.Add($"{fileName} General error: An internal server error occurred.");
            return false;
        }
    }
    catch (Exception)
    {
        errorList.Add($"{fileName} General error: An internal server error occurred.");
        return false;
    }

    return isColValValid;
}

        // =====================================================================
        // Polygons (stub)
        // =====================================================================
        [NonAction]
        public bool ProcessPredictionPloygonJson(string filePath, int ExcelID, int projectId, ref int rowInserted, ref int rowUpdated, out List<string> errorList)
        {
            bool isColValValid = true;
            errorList = new();

            if (!IsValidJson(filePath))
            {
                errorList.Add("invalid file:- '" + Path.GetFileName(filePath));
                return false;
            }

	            string json = System.IO.File.ReadAllText(filePath);
	            var geoJson = JsonConvert.DeserializeObject<dynamic>(json);
	            if (geoJson == null) return true;

	            try
	            {
	                // TODO: polygon processing (left intentionally)
	            }
	            catch (Exception ex)
	            {
	                errorList.Add($"General error: {(SafeException.GetInnermost(ex))}");
	                return false;
	            }

            return isColValValid;
        }

        // =====================================================================
        // CSV / parsing helpers
        // =====================================================================
        [NonAction]
        public DataTable ReadCsvManual(string filePath)
        {
            var dt = new DataTable();

            var lines = System.IO.File.ReadAllLines(filePath);
            if (lines.Length == 0) return dt;

            var headers = lines[0].Split(',');
            foreach (var header in headers) dt.Columns.Add(header.Trim());

            for (int i = 1; i < lines.Length; i++)
            {
                var values = lines[i].Split(',');
                var row = dt.NewRow();
                for (int j = 0; j < headers.Length && j < values.Length; j++)
                    row[j] = values[j].Trim();
                dt.Rows.Add(row);
            }

            return dt;
        }

        private float? ValidParseFloat(string? value, out bool isValid)
        {
            isValid = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                isValid = true;
                return null;
            }
            if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out float result) &&
                !float.IsNaN(result) && !float.IsInfinity(result))
            {
                isValid = true;
                if (result == 2147483647) { isValid = false; return null; }
                return result;
            }
            return null;
        }

        private static int? TryInt(string? v) => int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : (int?)null;
        private static double? TryDouble(string? v) => double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : (double?)null;
        private static int? ParseCompositeCellId(string? value)
        {
            var direct = TryInt(value);
            if (direct.HasValue) return direct;

            var raw = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return null;

            var suffix = parts[^1];
            return TryInt(suffix);
        }

        private float? ParseFloat(string? value) => float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (float?)null;
        private double? ParseDouble(string? value) => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (double?)null;
        private int? ParseInt(string? value) => int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (int?)null;
        private long? ParseLong(string? value) => long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : (long?)null;
        private DateTime? ParseDateTime(string? value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : (DateTime?)null;

        [NonAction]
        public string readDateFromExcel(string val, int ExcelID, string Row_no)
        {
            string correcteddate = val;
            if (val != "" && !val.ToLower().Equals("nil") && !val.ToLower().Equals("na") && !val.ToLower().Equals("-") && !val.ToLower().Contains("for") && !val.ToLower().Contains("the") && !val.ToLower().Contains("yet") && !val.ToLower().Contains("not") && !val.ToLower().Contains("vacancy"))
            {
                try
                {
                    double d = double.Parse(val, CultureInfo.InvariantCulture);
                    DateTime conv = DateTime.FromOADate(d);
                    correcteddate = conv.ToString("dd-MM-yyyy");
                }
                catch (Exception)
                {
                    if (DateTime.TryParseExact(val, "dd/MMM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
                        DateTime.TryParseExact(val, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ||
                        DateTime.TryParseExact(val, "dd MMM yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ||
                        DateTime.TryParseExact(val, "d MMM yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ||
                        DateTime.TryParseExact(val, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                    {
                        correcteddate = dt.ToString("dd-MM-yyyy");
                    }
                    else if (val.Contains("-") || val.Contains("/"))
                    {
                        string[] arr = val.Contains("-") ? val.Split('-') : val.Split('/');
                        if (arr.Length == 3)
                        {
                            int dd = Convert.ToInt32(arr[0], CultureInfo.InvariantCulture);
                            int mm;
                            if (!int.TryParse(arr[1], NumberStyles.Any, CultureInfo.InvariantCulture, out mm))
                            {
                                string t = arr[1].Trim();
                                mm = t.Length == 3 ? getMonth(t) : getMonth1(t);
                            }
                            int yy = Convert.ToInt32(arr[2], CultureInfo.InvariantCulture);
                            if (arr[2].Length == 2) yy = Convert.ToInt32("20" + arr[2], CultureInfo.InvariantCulture);
                            if (mm > 12) { int t2 = dd; dd = mm; mm = t2; }
                            correcteddate = $"{dd:00}-{mm:00}-{yy}";
                        }
                    }
                    else if (val.Contains("."))
                    {
                        string[] arr = val.Split('.');
                        if (arr.Length == 3)
                        {
                            int dd = Convert.ToInt32(arr[0], CultureInfo.InvariantCulture);
                            int mm = Convert.ToInt32(arr[1], CultureInfo.InvariantCulture);
                            int yy = Convert.ToInt32(arr[2], CultureInfo.InvariantCulture);
                            correcteddate = $"{dd:00}-{mm:00}-{yy}";
                        }
                    }
                    else if (val.Contains("th") || val.Contains("rd"))
                    {
                        string[] arr = val.Contains("th")
                            ? val.Split(new string[] { "th" }, StringSplitOptions.None)
                            : val.Split(new string[] { "rd" }, StringSplitOptions.None);

                        if (arr.Length == 2)
                        {
                            int dd = Convert.ToInt32(arr[0], CultureInfo.InvariantCulture);
                            string mmyy = arr[1];
                            if (mmyy.Contains(","))
                            {
                                string[] arr1 = mmyy.Split(',');
                                int mm = int.TryParse(arr1[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var tmp) ? tmp : getMonth(arr1[0]);
                                int yy = Convert.ToInt32(arr1[1], CultureInfo.InvariantCulture);
                                correcteddate = $"{dd}-{mm:00}-{yy}";
                            }
                        }
                    }
                }
            }
            return correcteddate;
        }

        private int getMonth(string t)
        {
            t = t.ToLower().Trim();
            return t switch
            {
                "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
                "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
                _ => 0
            };
        }

        private int getMonth1(string t)
        {
            t = t.ToLower().Trim();
            return t switch
            {
                "january" => 1, "february" => 2, "march" => 3, "april" => 4, "may" => 5, "june" => 6,
                "july" => 7, "august" => 8, "september" => 9, "october" => 10, "november" => 11, "december" => 12,
                _ => 0
            };
        }

        // =====================================================================
        // Operator inference (NEW)
        // =====================================================================
        private static readonly Dictionary<int, string> EarToOp = new()
        {
            //  adjust per your circle/data
            { 315,   "airtel" }, // LTE B1 sample from your data
            { 39150, "jio"    }, // LTE B40 (2300) — change if Airtel in your circle
            { 3676,  "vi"     }, // LTE B8 (900) — adjust if needed
            // { <earfcn>, "operator" },
        };

        private static readonly Dictionary<int, string> BandToOp = new()
        {
            // conservative defaults — tune to your circle
            { 1,  "airtel" },
            { 3,  "airtel" },
            { 5,  "jio"    },
            { 8,  "vi"     },
            { 28, "jio"    }, // 700
            { 40, "jio"    }, // 2300
            { 41, "airtel" },
            { 78, "jio"    }, // 3.5 GHz NR
        };

        private static string NormalizeOperator(string s)
        {
            s = s?.Trim().ToLowerInvariant() ?? "";
            if (s.Contains("airtel") || s.Contains("bharti")) return "airtel";
            if (s.Contains("jio")    || s.Contains("reliance")) return "jio";
            if (s == "vi" || s.Contains("vodafone") || s.Contains("idea")) return "vi";
            if (s.Contains("bsnl")) return "bsnl";
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static string? ResolveOperator(site_prediction r)
        {
            // 1) If cluster text already has operator letters, normalize and return.
            if (!string.IsNullOrWhiteSpace(r.cluster) && r.cluster.Any(char.IsLetter))
                return NormalizeOperator(r.cluster);

            // 2) Try by EARFCN/NARFCN mapping
            if (r.earfcn.HasValue && EarToOp.TryGetValue(r.earfcn.Value, out var opByEar))
                return opByEar;

            // 3) Try by Band mapping
            if (r.band.HasValue && BandToOp.TryGetValue(r.band.Value, out var opByBand))
                return opByBand;

            // 4) Fallback from frequency string
            var f = r.frequency?.ToLowerInvariant() ?? "";
            if (f.Contains("2300")) return "jio";     // adjust if Airtel 2300 in your circle
            if (f.Contains("1800")) return "airtel";
            if (f.Contains("900"))  return "vi";
            if (f.Contains("700"))  return "jio";
            if (f.Contains("3300") || f.Contains("3500")) return "jio"; // n78

            // Unknown
            return null;
        }
    }
}

        


