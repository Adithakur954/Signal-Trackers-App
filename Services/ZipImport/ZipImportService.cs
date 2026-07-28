using System.Diagnostics;
using System.Formats.Asn1;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;
using SignalTracker.Models.ZipImport;

namespace SignalTracker.Services.ZipImport
{
    public sealed class ZipImportService
    {
        private const int BatchSize = 5000;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<ZipImportService> _logger;

        public ZipImportService(ApplicationDbContext db, ILogger<ZipImportService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<ZipImportSummary> ImportAsync(
            IFormFile zipFile,
            int userId,
            int? sessionId,
            string? notes,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var summary = new ZipImportSummary();

            if (zipFile == null || zipFile.Length == 0)
                throw new ArgumentException("ZIP file is required.", nameof(zipFile));

            if (userId <= 0)
                throw new InvalidOperationException("Unable to resolve logged-in user.");

            var companyId = await _db.tbl_user
                .AsNoTracking()
                .Where(x => x.id == userId)
                .Select(x => x.company_id ?? 0)
                .FirstOrDefaultAsync(cancellationToken);

            var targetSessionId = await ResolveSessionAsync(userId, sessionId, notes, cancellationToken);
            summary.SessionId = targetSessionId;
            await EnsureNetworkLogExtraJsonColumnsAsync(cancellationToken);

            var tempZipPath = Path.Combine(Path.GetTempPath(), $"signaltracker_zip_import_{Guid.NewGuid():N}.zip");
            try
            {
                await using (var fs = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
                {
                    await zipFile.CopyToAsync(fs, cancellationToken);
                }

                var networkBatch = new List<tbl_network_log>(BatchSize);
                var neighbourBatch = new List<tbl_network_log_neighbour>(BatchSize);
                var uploadNetworkKeys = new HashSet<string>(StringComparer.Ordinal);
                var uploadNeighbourKeys = new HashSet<string>(StringComparer.Ordinal);
                var pendingSubSessions = new Dictionary<string, PendingSubSession>(StringComparer.Ordinal);
                var existingSubSessionIds = await LoadExistingSubSessionIdsAsync(targetSessionId, cancellationToken);
                var nextGeneratedSubSessionId = await GetNextSubSessionIdAsync(targetSessionId, cancellationToken);
                SessionBounds? bounds = null;

                using var zipStream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var isNetworkLogEntry = IsNetworkLogEntry(entry);
                    var subSessionType = TryGetSubSessionTypeFromName(entry);
                    if (!isNetworkLogEntry && !subSessionType.HasValue)
                    {
                        summary.FilesSkipped++;
                        continue;
                    }

                    try
                    {
                        if (isNetworkLogEntry)
                        {
                            await ProcessNetworkLogEntryAsync(
                                entry,
                                targetSessionId,
                                userId,
                                companyId,
                                networkBatch,
                                neighbourBatch,
                                uploadNetworkKeys,
                                uploadNeighbourKeys,
                                pendingSubSessions,
                                existingSubSessionIds,
                                () => nextGeneratedSubSessionId++,
                                b => bounds = SessionBounds.Merge(bounds, b),
                                summary,
                                cancellationToken);
                        }
                        else
                        {
                            await ProcessSubSessionEntryAsync(
                                entry,
                                targetSessionId,
                                userId,
                                subSessionType.Value,
                                pendingSubSessions,
                                existingSubSessionIds,
                                () => nextGeneratedSubSessionId++,
                                summary,
                                cancellationToken);
                        }

                        summary.FilesProcessed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed importing ZIP entry {EntryName}", entry.FullName);
                        summary.Errors.Add($"{entry.FullName}: {ex.GetBaseException().Message}");
                    }
                }

                await FlushNetworkBatchAsync(networkBatch, summary, cancellationToken);
                await FlushNeighbourBatchAsync(neighbourBatch, summary, cancellationToken);
                await FlushSubSessionsAsync(pendingSubSessions.Values, existingSubSessionIds, summary, cancellationToken);
                await UpdateSessionBoundsAsync(targetSessionId, bounds, cancellationToken);
                AddSubSessionWarnings(summary);

                summary.Success = summary.Errors.Count == 0 || summary.NetworkLogInserted > 0 || summary.NetworkNeighbourInserted > 0 || summary.SubSessionInserted > 0;
                return summary;
            }
            finally
            {
                sw.Stop();
                summary.ProcessingTime = sw.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                TryDelete(tempZipPath);
            }
        }

        private async Task<int> ResolveSessionAsync(int userId, int? sessionId, string? notes, CancellationToken cancellationToken)
        {
            if (sessionId.GetValueOrDefault() > 0)
            {
                var exists = await _db.tbl_session.AnyAsync(x => x.id == sessionId.Value, cancellationToken);
                if (!exists)
                    throw new InvalidOperationException($"Session {sessionId.Value} was not found.");

                return sessionId.Value;
            }

            var session = new tbl_session
            {
                user_id = userId,
                type = "network",
                notes = string.IsNullOrWhiteSpace(notes) ? "zip import" : notes.Trim(),
                uploaded_on = DateTime.Now
            };
            _db.tbl_session.Add(session);
            await _db.SaveChangesAsync(cancellationToken);
            return session.id ?? 0;
        }

        private static bool IsNetworkLogEntry(ZipArchiveEntry entry)
        {
            var name = Path.GetFileName(entry.FullName);
            return entry.Length > 0 &&
                   name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                   (name.StartsWith("NetworkLog_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("NetworkLogUnsent_", StringComparison.OrdinalIgnoreCase));
        }

        private static byte? TryGetSubSessionTypeFromName(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0) return null;

            var name = Path.GetFileNameWithoutExtension(entry.FullName);
            if (!Path.GetFileName(entry.FullName).EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return null;

            var tokens = Regex.Split(name, "[^A-Za-z0-9]+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (tokens.Any(x => x.Equals("CS", StringComparison.OrdinalIgnoreCase) || x.Equals("CallSession", StringComparison.OrdinalIgnoreCase)))
                return 1;
            if (tokens.Any(x => x.Equals("PS", StringComparison.OrdinalIgnoreCase) || x.Equals("PacketSession", StringComparison.OrdinalIgnoreCase)))
                return 2;

            return null;
        }

        private async Task ProcessNetworkLogEntryAsync(
            ZipArchiveEntry entry,
            int sessionId,
            int userId,
            int companyId,
            List<tbl_network_log> networkBatch,
            List<tbl_network_log_neighbour> neighbourBatch,
            HashSet<string> uploadNetworkKeys,
            HashSet<string> uploadNeighbourKeys,
            Dictionary<string, PendingSubSession> pendingSubSessions,
            Dictionary<string, long> existingSubSessionIds,
            Func<int> nextSubSessionId,
            Action<SessionBounds> mergeBounds,
            ZipImportSummary summary,
            CancellationToken cancellationToken)
        {
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
            using var csv = new CsvReader(reader, CreateCsvConfiguration());

            if (!await csv.ReadAsync() || !csv.ReadHeader())
                return;

            var headerMap = BuildHeaderMap(csv.HeaderRecord ?? Array.Empty<string>());
            while (await csv.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var timestampRaw = GetField(csv, headerMap, "Timestamp");
                if (!TryParseTimestamp(timestampRaw, out var timestamp))
                {
                    if (LooksLikeDataRow(timestampRaw))
                        summary.InvalidRows++;
                    continue;
                }

                var lat = ParseFloat(GetField(csv, headerMap, "Latitude", "lat"));
                var lon = ParseFloat(GetField(csv, headerMap, "Longitude", "lon"));
                if (!lat.HasValue || !lon.HasValue)
                {
                    summary.InvalidRows++;
                    continue;
                }

                var log = BuildNetworkLog(csv, headerMap, sessionId, companyId, timestamp, lat.Value, lon.Value);
                mergeBounds(new SessionBounds(timestamp, lat.Value, lon.Value));

                var csPayload = GetField(csv, headerMap, "CS");
                var psPayload = GetField(csv, headerMap, "PS");
                if (!string.IsNullOrWhiteSpace(csPayload))
                    summary.CsPayloadRows++;
                if (!string.IsNullOrWhiteSpace(psPayload))
                    summary.PsPayloadRows++;
                else if (headerMap.ContainsKey(NormalizeHeader("PS")))
                    summary.EmptyPsPayloadRows++;

                var csSubSessionId = CaptureSubSession(
                    csv,
                    headerMap,
                    userId,
                    sessionId,
                    timestamp,
                    lat.Value,
                    lon.Value,
                    type: 1,
                    rawPayload: csPayload,
                    nextSubSessionId,
                    pendingSubSessions,
                    existingSubSessionIds);
                if (csSubSessionId.HasValue)
                    log.tbl_sub_session_cs_id = csSubSessionId.Value;

                var psSubSessionId = CaptureSubSession(
                    csv,
                    headerMap,
                    userId,
                    sessionId,
                    timestamp,
                    lat.Value,
                    lon.Value,
                    type: 2,
                    rawPayload: psPayload,
                    nextSubSessionId,
                    pendingSubSessions,
                    existingSubSessionIds);
                if (psSubSessionId.HasValue)
                    log.tbl_sub_session_ps_id = psSubSessionId.Value;

                if (IsPrimaryNo(log.primary))
                {
                    var neighbour = ToNeighbourLog(log);
                    var key = BuildNeighbourKey(neighbour);
                    if (!uploadNeighbourKeys.Add(key))
                    {
                        summary.DuplicatesSkipped++;
                    }
                    else
                    {
                        neighbourBatch.Add(neighbour);
                        if (neighbourBatch.Count >= BatchSize)
                            await FlushNeighbourBatchAsync(neighbourBatch, summary, cancellationToken);
                    }
                }
                else
                {
                    var key = BuildNetworkKey(log);
                    if (!uploadNetworkKeys.Add(key))
                    {
                        summary.DuplicatesSkipped++;
                    }
                    else
                    {
                        networkBatch.Add(log);
                        if (networkBatch.Count >= BatchSize)
                            await FlushNetworkBatchAsync(networkBatch, summary, cancellationToken);
                    }
                }
            }
        }

        private async Task ProcessSubSessionEntryAsync(
            ZipArchiveEntry entry,
            int sessionId,
            int userId,
            byte type,
            Dictionary<string, PendingSubSession> pendingSubSessions,
            Dictionary<string, long> existingSubSessionIds,
            Func<int> nextSubSessionId,
            ZipImportSummary summary,
            CancellationToken cancellationToken)
        {
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 64);
            using var csv = new CsvReader(reader, CreateCsvConfiguration());

            if (!await csv.ReadAsync() || !csv.ReadHeader())
                return;

            var headerMap = BuildHeaderMap(csv.HeaderRecord ?? Array.Empty<string>());
            while (await csv.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = GetField(csv, headerMap, type == 1 ? "CS" : "PS", "json_data", "payload", "details", "sub_session_details")
                    ?? BuildCurrentRowJson(csv, csv.HeaderRecord ?? Array.Empty<string>());
                if (string.IsNullOrWhiteSpace(payload))
                {
                    summary.InvalidRows++;
                    continue;
                }

                var timestamp = TryParseTimestamp(FirstNonBlank(
                    GetField(csv, headerMap, "start_time", "Start Time", "Timestamp"),
                    GetField(csv, headerMap, "timestamp")), out var parsedTimestamp)
                    ? parsedTimestamp
                    : DateTime.Now;
                var lat = ParseFloat(GetField(csv, headerMap, "start_lat", "Latitude", "lat"));
                var lon = ParseFloat(GetField(csv, headerMap, "start_lon", "Longitude", "lon"));

                var id = CaptureSubSessionRecord(
                    userId,
                    sessionId,
                    timestamp,
                    lat,
                    lon,
                    type,
                    payload,
                    ParsePositiveInt(GetField(csv, headerMap, "Sub Session Id", "sub_session_id")),
                    nextSubSessionId,
                    pendingSubSessions,
                    existingSubSessionIds);

                if (id.HasValue)
                {
                    if (type == 1) summary.CsPayloadRows++;
                    if (type == 2) summary.PsPayloadRows++;
                }
            }
        }

        private static CsvConfiguration CreateCsvConfiguration()
        {
            return new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                BadDataFound = null,
                MissingFieldFound = null,
                HeaderValidated = null,
                DetectColumnCountChanges = false,
                Mode = CsvMode.RFC4180,
                TrimOptions = TrimOptions.Trim
            };
        }

        private static Dictionary<string, int> BuildHeaderMap(string[] headers)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                var key = NormalizeHeader(headers[i]);
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                    map[key] = i;
            }
            return map;
        }

        private static string? GetField(CsvReader csv, Dictionary<string, int> headerMap, params string[] names)
        {
            foreach (var name in names)
            {
                if (headerMap.TryGetValue(NormalizeHeader(name), out var index))
                {
                    try
                    {
                        return csv.GetField(index)?.Trim();
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        private static string? BuildCurrentRowJson(CsvReader csv, string[] headers)
        {
            var data = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Length; index++)
            {
                var header = headers[index]?.Trim();
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                string? value;
                try
                {
                    value = csv.GetField(index)?.Trim();
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(value))
                    data[header] = value;
            }

            return data.Count == 0 ? null : JsonSerializer.Serialize(data);
        }

        private static string NormalizeHeader(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        private static void AddSubSessionWarnings(ZipImportSummary summary)
        {
            if (summary.CsPayloadRows > 0 && summary.PsPayloadRows == 0 && summary.EmptyPsPayloadRows > 0)
            {
                summary.Warnings.Add(
                    $"PS column was present but empty in {summary.EmptyPsPayloadRows} network log rows. Only CS sub-sessions were imported because the file does not contain PS payload data.");
            }
        }

        private static bool TryParseTimestamp(string? value, out DateTime timestamp)
        {
            timestamp = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim().Trim('\uFEFF').Trim('"');
            var formats = new[]
            {
                "yyyy-MM-dd HH:mm:ss.fff",
                "yyyy-MM-dd HH:mm:ss",
                "dd-MM-yyyy HH:mm:ss",
                "dd-MM-yyyy HH:mm:ss.fff",
                "MM/dd/yyyy HH:mm:ss",
                "MM/dd/yyyy HH:mm:ss.fff"
            };
            return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp) ||
                   DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp);
        }

        private static bool LooksLikeDataRow(string? timestampRaw)
        {
            var text = timestampRaw?.Trim();
            return !string.IsNullOrWhiteSpace(text) && char.IsDigit(text[0]);
        }

        private static tbl_network_log BuildNetworkLog(
            CsvReader csv,
            Dictionary<string, int> map,
            int sessionId,
            int companyId,
            DateTime timestamp,
            float lat,
            float lon)
        {
            var apps = GetField(csv, map, "Apps", "Running Apps");
            var appName = GetField(csv, map, "App Name", "app_name");
            var mcc = ParseInt(GetField(csv, map, "MCC", "m_mcc"));
            var mnc = ParseInt(GetField(csv, map, "MNC", "m_mnc"));

            return new tbl_network_log
            {
                session_id = sessionId,
                company_id = companyId,
                timestamp = timestamp,
                lat = lat,
                lon = lon,
                altitude = ParseFloat(GetField(csv, map, "Altitude")),
                indoor_outdoor = GetField(csv, map, "Indoor/Outdoor", "Indoor / Outdoor", "indoor_outdoor"),
                battery = ParseInt(GetField(csv, map, "Battery", "Battery Level")),
                dls = GetField(csv, map, "dls", "Download Speed (KB/s)"),
                uls = GetField(csv, map, "uls", "Upload Speed (KB/s)"),
                total_rx_kb = GetField(csv, map, "total_rx_kb", "Total Rx (KB)"),
                total_tx_kb = GetField(csv, map, "total_tx_kb", "Total Tx (KB)"),
                hotspot = GetField(csv, map, "HotSpot", "Hot Spot"),
                apps = string.IsNullOrWhiteSpace(apps) ? appName : apps,
                app_name = appName,
                mos = ParseFloat(GetField(csv, map, "MOS")),
                jitter = ParseFloat(GetField(csv, map, "Jitter")),
                latency = ParseFloat(GetField(csv, map, "Latency")),
                packet_loss = ParseFloat(GetField(csv, map, "packet_loss", "Packet Loss")),
                call_state = GetField(csv, map, "call_state", "Call State"),
                image_path = GetField(csv, map, "Image Name", "image_name"),
                mci = GetField(csv, map, "CI", "mci", "CI  (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)", "CI (5G - Nci 4G - Ci 3G - BasestationId 2G - Cid)"),
                pci = GetField(csv, map, "PCI", "NR-PCI / PCI / PSC"),
                rssi = ParseFloat(GetField(csv, map, "RSSI", "RSSI  (2G-RxLEV)", "rssi")),
                rsrp = ParseFloat(GetField(csv, map, "RSRP", "ssRSRP / RSRP / RSCP")),
                rsrq = ParseFloat(GetField(csv, map, "RSRQ", "ssRSRQ / RSRQ / EcNo")),
                sinr = ParseFloat(GetField(csv, map, "SINR", "NR-SINR / SINR / RxQual")),
                dl_tpt = GetField(csv, map, "DL THPT", "dl_tpt"),
                ul_tpt = GetField(csv, map, "UL THPT", "ul_tpt"),
                earfcn = GetField(csv, map, "EARFCN", "EARFCN (5G - NARFCN 4G - ERAFCN 3G - UARFCN 2G - BCCH)"),
                volte_call = GetField(csv, map, "VOLTE CALL", "volte_call"),
                band = GetField(csv, map, "BAND", "Band"),
                cqi = ParseFloat(GetField(csv, map, "CQI")),
                bler = GetField(csv, map, "BLER", "BLER (2G - bitErrorRate 3G - ber Others - BLER)", "BLER (2G - bitErrorRate 3G - ber  Others - BLER)"),
                m_alpha_long = GetField(csv, map, "Alpha Long", "m_alpha_long"),
                m_alpha_short = GetField(csv, map, "Alpha Short", "m_alpha_short"),
                Speed = ParseFloat(GetField(csv, map, "Speed", "Speed (km/h)", "speed")),
                ta = GetField(csv, map, "TA", "PUSCH Tx (dBm)", "ta"),
                m_mcc = mcc,
                m_mnc = mnc,
                mcc = mcc,
                mnc = mnc,
                tac = GetField(csv, map, "TAC", "TAC (2G/3G - lac 4G/5G - tac)"),
                gps_fix_type = GetField(csv, map, "GPS Fix Type", "gps_fix_type"),
                gps_hdop = ParseFloat(GetField(csv, map, "GPS HDOP", "gps_hdop")),
                gps_vdop = ParseFloat(GetField(csv, map, "GPS VDOP", "gps_vdop")),
                phone_antenna_gain = GetField(csv, map, "Phone Antenna Gain", "phone_antenna_gain"),
                csi_rsrp = ParseFloat(GetField(csv, map, "csiRsrp", "csi_rsrp")),
                csi_rsrq = ParseFloat(GetField(csv, map, "csiRsrq", "csi_rsrq")),
                csi_sinr = ParseFloat(GetField(csv, map, "csiSinr", "csi_sinr")),
                level = ParseInt(GetField(csv, map, "Level")),
                cell_id = GetField(csv, map, "Cell Id", "cell_id"),
                nodeb_id = GetField(csv, map, "NodeB Id", "NodeB Id/ Site Id", "nodeb_id"),
                primary = GetField(csv, map, "Primary", "primary"),
                all_neigbor_cell_info = GetField(csv, map, "Throughput Details"),
                num_cells = ParseInt(GetField(csv, map, "No of Cells", "num_cells")),
                primary_cell_info_1 = FirstNonBlank(
                    GetField(csv, map, "CellInfo_1", "primary_cell_info_1"),
                    ReadJsonString(GetField(csv, map, "unsent_data") ?? "", "primary_cell_info_1", "CellInfo_1", "cellinfo_1")),
                primary_cell_info_2 = GetField(csv, map, "CellInfo_2", "primary_cell_info_2"),
                primary_cell_info_3 = GetField(csv, map, "CellInfo_3", "primary_cell_info_3"),
                network = GetField(csv, map, "Network", "Network Type"),
                network_id = GetField(csv, map, "Network Id", "NetworkId", "network_id"),
                bw = GetField(csv, map, "bw", "BW"),
                extra_json = BuildNetworkLogExtraJson(csv, map)
            };
        }

        private static tbl_network_log_neighbour ToNeighbourLog(tbl_network_log source)
        {
            return new tbl_network_log_neighbour
            {
                primary = source.primary ?? "No",
                network_id = source.network_id,
                indoor_outdoor = source.indoor_outdoor,
                nodeb_id = source.nodeb_id,
                cell_id = source.cell_id ?? "",
                session_id = source.session_id ?? 0,
                timestamp = source.timestamp,
                lat = source.lat,
                lon = source.lon,
                altitude = source.altitude,
                battery = source.battery,
                dls = source.dls,
                uls = source.uls,
                call_state = source.call_state,
                hotspot = source.hotspot,
                apps = source.apps,
                num_cells = source.num_cells,
                network = source.network,
                m_mcc = source.m_mcc,
                m_mnc = source.m_mnc,
                m_alpha_long = source.m_alpha_long,
                m_alpha_short = source.m_alpha_short,
                mci = source.mci,
                pci = source.pci,
                tac = source.tac,
                earfcn = source.earfcn,
                rssi = source.rssi,
                rsrp = source.rsrp,
                rsrq = source.rsrq,
                sinr = source.sinr,
                total_rx_kb = source.total_rx_kb,
                total_tx_kb = source.total_tx_kb,
                mos = source.mos,
                jitter = source.jitter,
                latency = source.latency,
                packet_loss = source.packet_loss,
                dl_tpt = source.dl_tpt,
                ul_tpt = source.ul_tpt,
                volte_call = source.volte_call,
                band = source.band,
                cqi = source.cqi,
                bler = source.bler,
                primary_cell_info_1 = source.primary_cell_info_1,
                primary_cell_info_2 = source.primary_cell_info_2,
                all_neigbor_cell_info = source.all_neigbor_cell_info,
                image_path = source.image_path,
                polygon_id = source.polygon_id,
                tbl_sub_session_ps_id = source.tbl_sub_session_ps_id,
                tbl_sub_session_cs_id = source.tbl_sub_session_cs_id,
                extra_json = source.extra_json,
                Speed = source.Speed
            };
        }

        private static string? BuildNetworkLogExtraJson(CsvReader csv, Dictionary<string, int> map)
        {
            var data = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            AddExtraValue(data, "altitude", GetField(csv, map, "Altitude"));
            AddExtraValue(data, "phone_heading", GetField(csv, map, "Phone Heading (degrees)", "Phone Heading", "phone_heading"));
            AddExtraValue(data, "image_name", GetField(csv, map, "Image Name", "image_name"));
            AddExtraValue(data, "unsent_data", GetField(csv, map, "unsent_data"));
            AddExtraValue(data, "sub_session_id", GetField(csv, map, "Sub Session Id", "sub_session_id"));
            AddExtraValue(data, "sub_session_details", GetField(csv, map, "Sub Session Details", "sub_session_details"));
            AddExtraValue(data, "cs", GetField(csv, map, "CS"));
            AddExtraValue(data, "ps", GetField(csv, map, "PS"));
            return data.Count == 0 ? null : JsonSerializer.Serialize(data);
        }

        private static void AddExtraValue(IDictionary<string, object?> data, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                data[key] = value.Trim();
        }

        private async Task EnsureNetworkLogExtraJsonColumnsAsync(CancellationToken cancellationToken)
        {
            await EnsureTextColumnAsync("tbl_network_log", "extra_json", cancellationToken);
            await EnsureTextColumnAsync("tbl_network_log_neighbour", "extra_json", cancellationToken);
            await EnsureColumnAsync("tbl_network_log", "altitude", "DOUBLE NULL", cancellationToken);
            await EnsureColumnAsync("tbl_network_log_neighbour", "altitude", "DOUBLE NULL", cancellationToken);
            await EnsureColumnAsync("tbl_network_log", "tbl_sub_session_ps_id", "BIGINT NULL", cancellationToken);
            await EnsureColumnAsync("tbl_network_log", "tbl_sub_session_cs_id", "BIGINT NULL", cancellationToken);
            await EnsureColumnAsync("tbl_network_log_neighbour", "tbl_sub_session_ps_id", "BIGINT NULL", cancellationToken);
            await EnsureColumnAsync("tbl_network_log_neighbour", "tbl_sub_session_cs_id", "BIGINT NULL", cancellationToken);
        }

        private async Task EnsureTextColumnAsync(string tableName, string columnName, CancellationToken cancellationToken)
        {
            await EnsureColumnAsync(tableName, columnName, "LONGTEXT NULL", cancellationToken);
        }

        private async Task EnsureColumnAsync(string tableName, string columnName, string columnDefinition, CancellationToken cancellationToken)
        {
            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(cancellationToken);

            try
            {
                await using var exists = conn.CreateCommand();
                exists.CommandText = @"
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = DATABASE()
                      AND table_name = @tableName
                      AND column_name = @columnName;";
                var tableParam = exists.CreateParameter();
                tableParam.ParameterName = "@tableName";
                tableParam.Value = tableName;
                exists.Parameters.Add(tableParam);
                var columnParam = exists.CreateParameter();
                columnParam.ParameterName = "@columnName";
                columnParam.Value = columnName;
                exists.Parameters.Add(columnParam);

                var count = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (count > 0) return;

                await using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE `{tableName.Replace("`", "``")}` ADD COLUMN `{columnName.Replace("`", "``")}` {columnDefinition};";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        private async Task FlushNetworkBatchAsync(List<tbl_network_log> batch, ZipImportSummary summary, CancellationToken cancellationToken)
        {
            if (batch.Count == 0) return;

            var existing = await LoadExistingNetworkKeysAsync(batch, cancellationToken);
            var insert = batch.Where(x => !existing.Contains(BuildNetworkKey(x))).ToList();
            summary.DuplicatesSkipped += batch.Count - insert.Count;

            if (insert.Count > 0)
            {
                var previous = _db.ChangeTracker.AutoDetectChangesEnabled;
                try
                {
                    _db.ChangeTracker.AutoDetectChangesEnabled = false;
                    await _db.tbl_network_log.AddRangeAsync(insert, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    summary.NetworkLogInserted += insert.Count;
                    _db.ChangeTracker.Clear();
                }
                finally
                {
                    _db.ChangeTracker.AutoDetectChangesEnabled = previous;
                }
            }
            batch.Clear();
        }

        private async Task FlushNeighbourBatchAsync(List<tbl_network_log_neighbour> batch, ZipImportSummary summary, CancellationToken cancellationToken)
        {
            if (batch.Count == 0) return;

            var existing = await LoadExistingNeighbourKeysAsync(batch, cancellationToken);
            var insert = batch.Where(x => !existing.Contains(BuildNeighbourKey(x))).ToList();
            summary.DuplicatesSkipped += batch.Count - insert.Count;

            if (insert.Count > 0)
            {
                var previous = _db.ChangeTracker.AutoDetectChangesEnabled;
                try
                {
                    _db.ChangeTracker.AutoDetectChangesEnabled = false;
                    await _db.tbl_network_log_neighbour.AddRangeAsync(insert, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    summary.NetworkNeighbourInserted += insert.Count;
                    _db.ChangeTracker.Clear();
                }
                finally
                {
                    _db.ChangeTracker.AutoDetectChangesEnabled = previous;
                }
            }
            batch.Clear();
        }

        private async Task<HashSet<string>> LoadExistingNetworkKeysAsync(List<tbl_network_log> batch, CancellationToken cancellationToken)
        {
            var sessionId = batch.Select(x => x.session_id).FirstOrDefault();
            var min = batch.Min(x => x.timestamp);
            var max = batch.Max(x => x.timestamp);
            if (!sessionId.HasValue || !min.HasValue || !max.HasValue) return new HashSet<string>(StringComparer.Ordinal);

            var rows = await _db.tbl_network_log
                .AsNoTracking()
                .Where(x => x.session_id == sessionId && x.timestamp >= min && x.timestamp <= max)
                .Select(x => new tbl_network_log
                {
                    session_id = x.session_id,
                    timestamp = x.timestamp,
                    lat = x.lat,
                    lon = x.lon,
                    primary = x.primary,
                    network = x.network,
                    pci = x.pci,
                    earfcn = x.earfcn,
                    cell_id = x.cell_id,
                    nodeb_id = x.nodeb_id
                })
                .ToListAsync(cancellationToken);

            return rows.Select(BuildNetworkKey).ToHashSet(StringComparer.Ordinal);
        }

        private async Task<HashSet<string>> LoadExistingNeighbourKeysAsync(List<tbl_network_log_neighbour> batch, CancellationToken cancellationToken)
        {
            var sessionId = batch.Select(x => x.session_id).FirstOrDefault();
            var min = batch.Min(x => x.timestamp);
            var max = batch.Max(x => x.timestamp);
            if (sessionId <= 0 || !min.HasValue || !max.HasValue) return new HashSet<string>(StringComparer.Ordinal);

            var rows = await _db.tbl_network_log_neighbour
                .AsNoTracking()
                .Where(x => x.session_id == sessionId && x.timestamp >= min && x.timestamp <= max)
                .Select(x => new tbl_network_log_neighbour
                {
                    session_id = x.session_id,
                    timestamp = x.timestamp,
                    lat = x.lat,
                    lon = x.lon,
                    primary = x.primary,
                    network = x.network,
                    pci = x.pci,
                    earfcn = x.earfcn,
                    cell_id = x.cell_id,
                    nodeb_id = x.nodeb_id
                })
                .ToListAsync(cancellationToken);

            return rows.Select(BuildNeighbourKey).ToHashSet(StringComparer.Ordinal);
        }

        private static string BuildNetworkKey(tbl_network_log row)
        {
            return string.Join("|",
                row.session_id,
                row.timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                FormatFloat(row.lat),
                FormatFloat(row.lon),
                NormalizeKeyPart(row.primary),
                NormalizeKeyPart(row.network),
                NormalizeKeyPart(row.pci),
                NormalizeKeyPart(row.earfcn),
                NormalizeKeyPart(row.cell_id),
                NormalizeKeyPart(row.nodeb_id));
        }

        private static string BuildNeighbourKey(tbl_network_log_neighbour row)
        {
            return string.Join("|",
                row.session_id,
                row.timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                FormatFloat(row.lat),
                FormatFloat(row.lon),
                NormalizeKeyPart(row.primary),
                NormalizeKeyPart(row.network),
                NormalizeKeyPart(row.pci),
                NormalizeKeyPart(row.earfcn),
                NormalizeKeyPart(row.cell_id),
                NormalizeKeyPart(row.nodeb_id));
        }

        private long? CaptureSubSession(
            CsvReader csv,
            Dictionary<string, int> map,
            int userId,
            int sessionId,
            DateTime timestamp,
            float lat,
            float lon,
            byte type,
            string? rawPayload,
            Func<int> nextSubSessionId,
            Dictionary<string, PendingSubSession> pending,
            Dictionary<string, long> existingIds)
        {
            return CaptureSubSessionRecord(
                userId,
                sessionId,
                timestamp,
                lat,
                lon,
                type,
                rawPayload,
                ParsePositiveInt(GetField(csv, map, "Sub Session Id")),
                nextSubSessionId,
                pending,
                existingIds);
        }

        private long? CaptureSubSessionRecord(
            int userId,
            int sessionId,
            DateTime timestamp,
            float? lat,
            float? lon,
            byte type,
            string? rawPayload,
            int? requestedSubSessionId,
            Func<int> nextSubSessionId,
            Dictionary<string, PendingSubSession> pending,
            Dictionary<string, long> existingIds)
        {
            if (string.IsNullOrWhiteSpace(rawPayload)) return null;

            var normalizedJson = NormalizePayloadJson(rawPayload);
            if (string.IsNullOrWhiteSpace(normalizedJson)) return null;

            var key = BuildSubSessionKey(sessionId, type, normalizedJson);
            if (existingIds.TryGetValue(key, out var existingId)) return existingId;

            if (!pending.TryGetValue(key, out var item))
            {
                var subSessionId = requestedSubSessionId ?? nextSubSessionId();
                item = new PendingSubSession
                {
                    Key = key,
                    Entity = new tbl_sub_session
                    {
                        user_id = userId,
                        session_id = sessionId,
                        sub_session_id = subSessionId,
                        type = type,
                        start_time = timestamp,
                        end_time = ResolvePayloadEndTime(timestamp, normalizedJson),
                        json_data = normalizedJson,
                        status = ResolvePayloadStatus(normalizedJson),
                        start_lat = lat,
                        start_lon = lon,
                        end_lat = lat,
                        end_lon = lon
                    }
                };
                pending[key] = item;
            }

            if (!item.Entity.start_time.HasValue || timestamp < item.Entity.start_time.Value)
            {
                item.Entity.start_time = timestamp;
                item.Entity.start_lat = lat;
                item.Entity.start_lon = lon;
            }

            var payloadEndTime = item.Entity.start_time.HasValue
                ? ResolvePayloadEndTime(item.Entity.start_time.Value, normalizedJson)
                : null;
            if (payloadEndTime.HasValue && (!item.Entity.end_time.HasValue || payloadEndTime.Value > item.Entity.end_time.Value))
                item.Entity.end_time = payloadEndTime.Value;
            if (!item.Entity.end_time.HasValue || timestamp > item.Entity.end_time.Value)
                item.Entity.end_time = timestamp;
            item.Entity.end_lat = lat;
            item.Entity.end_lon = lon;

            return item.Entity.sub_session_id;
        }

        private async Task<Dictionary<string, long>> LoadExistingSubSessionIdsAsync(int sessionId, CancellationToken cancellationToken)
        {
            var rows = await _db.tbl_sub_session
                .AsNoTracking()
                .Where(x => x.session_id == sessionId && x.json_data != null)
                .Select(x => new { x.session_id, x.type, x.sub_session_id, x.json_data })
                .ToListAsync(cancellationToken);

            return rows
                .Where(x => x.session_id.HasValue && x.type.HasValue && x.sub_session_id.HasValue && x.sub_session_id.Value > 0 && !string.IsNullOrWhiteSpace(x.json_data))
                .Select(x => new
                {
                    Key = BuildSubSessionKey(x.session_id!.Value, x.type!.Value, NormalizePayloadJson(x.json_data)!),
                    Id = (long)x.sub_session_id!.Value
                })
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);
        }

        private async Task<int> GetNextSubSessionIdAsync(int sessionId, CancellationToken cancellationToken)
        {
            var max = await _db.tbl_sub_session
                .AsNoTracking()
                .Where(x => x.session_id == sessionId)
                .MaxAsync(x => (int?)x.sub_session_id, cancellationToken);

            return Math.Max(1, max.GetValueOrDefault() + 1);
        }

        private async Task FlushSubSessionsAsync(
            IEnumerable<PendingSubSession> pending,
            Dictionary<string, long> existingIds,
            ZipImportSummary summary,
            CancellationToken cancellationToken)
        {
            var entities = pending
                .Where(x => !existingIds.ContainsKey(x.Key))
                .Select(x => x.Entity)
                .ToList();

            if (entities.Count == 0) return;

            await _db.tbl_sub_session.AddRangeAsync(entities, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            summary.SubSessionInserted += entities.Count;
            _db.ChangeTracker.Clear();

            foreach (var entity in entities)
            {
                if (entity.session_id.HasValue && entity.type.HasValue && !string.IsNullOrWhiteSpace(entity.json_data))
                    existingIds[BuildSubSessionKey(entity.session_id.Value, entity.type.Value, entity.json_data)] = entity.sub_session_id.GetValueOrDefault();
            }
        }

        private static string? NormalizePayloadJson(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = raw.Trim().Trim('"');
            text = text.Replace("\"\"", "\"", StringComparison.Ordinal);
            if (!text.StartsWith("{", StringComparison.Ordinal) && text.Contains(':', StringComparison.Ordinal))
                text = "{" + text + "}";

            var candidate = text
                .Replace(';', ',')
                .Replace('\'', '"');

            if (TryCanonicalizeJson(candidate, out var canonical))
                return canonical;

            return JsonSerializer.Serialize(new SortedDictionary<string, object?>
            {
                ["raw"] = raw.Trim()
            });
        }

        private static bool TryCanonicalizeJson(string value, out string canonical)
        {
            canonical = "";
            try
            {
                using var doc = JsonDocument.Parse(value);
                canonical = CanonicalizeElement(doc.RootElement);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string CanonicalizeElement(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return element.GetRawText();

            var dict = new SortedDictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in element.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var pair in dict)
                {
                    writer.WritePropertyName(pair.Key);
                    pair.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string BuildSubSessionKey(int sessionId, byte type, string normalizedJson)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson.Trim()));
            return $"{sessionId}|{type}|{Convert.ToHexString(bytes)}";
        }

        private static byte? ResolvePayloadStatus(string normalizedJson)
        {
            var status = ReadJsonString(normalizedJson, "result_status", "connection_status", "call_status", "status");
            if (string.IsNullOrWhiteSpace(status)) return null;

            var normalized = status.Trim().ToLowerInvariant();
            if (normalized is "connected" or "retained") return 1;
            if (normalized is "failed" or "not connected" or "notconnected" or "not_connected") return 2;
            return null;
        }

        private static DateTime? ResolvePayloadEndTime(DateTime startTime, string normalizedJson)
        {
            var duration = ReadJsonDouble(normalizedJson, "duration_ms", "duration");
            return duration.HasValue && duration.Value > 0
                ? startTime.AddMilliseconds(duration.Value)
                : startTime;
        }

        private static string? ReadJsonString(string normalizedJson, params string[] names)
        {
            if (string.IsNullOrWhiteSpace(normalizedJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(normalizedJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var name in names)
                {
                    if (doc.RootElement.TryGetProperty(name, out var prop))
                        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
                }
            }
            catch
            {
            }
            return null;
        }

        private static string? FirstNonBlank(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return null;
        }

        private static double? ReadJsonDouble(string normalizedJson, params string[] names)
        {
            var text = ReadJsonString(normalizedJson, names);
            return ParseDouble(text);
        }

        private async Task UpdateSessionBoundsAsync(int sessionId, SessionBounds? bounds, CancellationToken cancellationToken)
        {
            if (bounds == null) return;

            var session = await _db.tbl_session.FirstOrDefaultAsync(x => x.id == sessionId, cancellationToken);
            if (session == null) return;

            session.start_time = MinDate(session.start_time, bounds.StartTime);
            session.end_time = MaxDate(session.end_time, bounds.EndTime);
            session.start_lat ??= bounds.StartLat;
            session.start_lon ??= bounds.StartLon;
            session.end_lat = bounds.EndLat;
            session.end_lon = bounds.EndLon;
            await _db.SaveChangesAsync(cancellationToken);
        }

        private static DateTime? MinDate(DateTime? existing, DateTime value) =>
            !existing.HasValue || value < existing.Value ? value : existing;

        private static DateTime? MaxDate(DateTime? existing, DateTime value) =>
            !existing.HasValue || value > existing.Value ? value : existing;

        private static bool IsPrimaryNo(string? primary)
        {
            var normalized = (primary ?? "").Trim();
            return normalized.Equals("No", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("N", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("False", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("0", StringComparison.OrdinalIgnoreCase);
        }

        private static int? ParseInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (int.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl) &&
                   dbl <= int.MaxValue &&
                   dbl >= int.MinValue
                ? (int)dbl
                : null;
        }

        private static int? ParsePositiveInt(string? value)
        {
            var parsed = ParseInt(value);
            return parsed.GetValueOrDefault() > 0 ? parsed : null;
        }

        private static float? ParseFloat(string? value)
        {
            return float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) &&
                   !float.IsNaN(parsed) &&
                   !float.IsInfinity(parsed)
                ? parsed
                : null;
        }

        private static double? ParseDouble(string? value)
        {
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) &&
                   !double.IsNaN(parsed) &&
                   !double.IsInfinity(parsed)
                ? parsed
                : null;
        }

        private static string NormalizeKeyPart(string? value) => (value ?? "").Trim().ToUpperInvariant();

        private static string FormatFloat(float? value) =>
            value.HasValue ? value.Value.ToString("G9", CultureInfo.InvariantCulture) : "";

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class PendingSubSession
        {
            public string Key { get; set; } = "";
            public tbl_sub_session Entity { get; set; } = new();
        }

        private sealed class SessionBounds
        {
            public SessionBounds(DateTime timestamp, float lat, float lon)
            {
                StartTime = timestamp;
                EndTime = timestamp;
                StartLat = lat;
                StartLon = lon;
                EndLat = lat;
                EndLon = lon;
            }

            public DateTime StartTime { get; private set; }
            public DateTime EndTime { get; private set; }
            public float StartLat { get; private set; }
            public float StartLon { get; private set; }
            public float EndLat { get; private set; }
            public float EndLon { get; private set; }

            public static SessionBounds Merge(SessionBounds? current, SessionBounds next)
            {
                if (current == null) return next;
                if (next.StartTime < current.StartTime)
                {
                    current.StartTime = next.StartTime;
                    current.StartLat = next.StartLat;
                    current.StartLon = next.StartLon;
                }
                if (next.EndTime > current.EndTime)
                {
                    current.EndTime = next.EndTime;
                    current.EndLat = next.EndLat;
                    current.EndLon = next.EndLon;
                }
                return current;
            }
        }
    }
}
