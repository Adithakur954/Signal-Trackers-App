using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using CsvHelper;
using CsvHelper.Configuration;
using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Services;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class L3EventController : BaseController
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IWebHostEnvironment _env;
        private readonly RedisService _redis;
        private readonly UserScopeService _userScope;

        public L3EventController(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            IWebHostEnvironment env,
            RedisService redis,
            UserScopeService userScope)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _env = env;
            _redis = redis;
            _userScope = userScope;
        }

        [HttpGet("GetDiagnosticCallSummary")]
        [HttpGet("GetEventL3CallSummary")]
        public Task<IActionResult> GetDiagnosticCallSummary(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 20000)
        {
            return CreateMapViewController().GetDiagnosticCallSummary(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GetDiagnosticTabCounts")]
        public Task<IActionResult> GetDiagnosticTabCounts(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 50000)
        {
            return CreateMapViewController().GetDiagnosticTabCounts(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GetDiagnosticExcelRows")]
        [HttpGet("GetDiagnosticMapRows")]
        public Task<IActionResult> GetDiagnosticExcelRows(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 20000)
        {
            return CreateMapViewController().GetDiagnosticExcelRows(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GetDiagnosticCallSummaryOnly")]
        public Task<IActionResult> GetDiagnosticCallSummaryOnly(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 50000)
        {
            return CreateMapViewController().GetDiagnosticCallSummaryOnly(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GetDiagnosticAnalyzerSummary")]
        public Task<IActionResult> GetDiagnosticAnalyzerSummary(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 50000)
        {
            return CreateMapViewController().GetDiagnosticAnalyzerSummary(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GetDiagnosticFlowModels")]
        public IActionResult GetDiagnosticFlowModels()
        {
            return CreateMapViewController().GetDiagnosticFlowModels();
        }

        [HttpGet("GetDiagnosticL3Messages")]
        public Task<IActionResult> GetDiagnosticL3Messages(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 20000)
        {
            return CreateMapViewController().GetDiagnosticL3Messages(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GetDiagnosticEvents")]
        public Task<IActionResult> GetDiagnosticEvents(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 20000)
        {
            return CreateMapViewController().GetDiagnosticEvents(sessionId, sessionIds, sessionIdsAlt, uploadId, take);
        }

        [HttpGet("GenerateDiagnosticEventAnalyzerPdf")]
        public Task<IActionResult> GenerateDiagnosticEventAnalyzerPdf(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 50000,
            [FromQuery] int reportRows = 600,
            [FromQuery] string? sourceFileName = null)
        {
            return CreateMapViewController().GenerateDiagnosticEventAnalyzerPdf(sessionId, sessionIds, sessionIdsAlt, uploadId, take, reportRows, sourceFileName);
        }

        [HttpGet("GenerateDiagnosticL3SummaryPdf")]
        public Task<IActionResult> GenerateDiagnosticL3SummaryPdf(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 50000,
            [FromQuery] int reportRows = 1000,
            [FromQuery] string? sourceFileName = null)
        {
            return CreateMapViewController().GenerateDiagnosticL3SummaryPdf(sessionId, sessionIds, sessionIdsAlt, uploadId, take, reportRows, sourceFileName);
        }

        [HttpPost("AddSessionUpload")]
        [RequestSizeLimit(512L * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
        public async Task<IActionResult> AddSessionUpload(
            [FromForm] int? projectId = null,
            [FromForm] string? dataType = null,
            [FromForm] IFormFile? zipFile = null,
            [FromForm] IFormFile? l3File = null,
            [FromForm] IFormFile? eventFile = null,
            CancellationToken cancellationToken = default)
        {
            var userId = GetCurrentUserId();
            if (userId <= 0)
                return Unauthorized(new { status = 0, message = "Unable to resolve logged-in user." });

            await EnsureL3EventSchemaAsync(cancellationToken);

            int? linkedProjectId = projectId.GetValueOrDefault() > 0 ? projectId.Value : null;
            if (linkedProjectId.HasValue)
            {
                var projectInfo = await GetAuthorizedProjectInfoAsync(linkedProjectId.Value, userId, cancellationToken);
                if (projectInfo == null)
                    return NotFound(new { status = 0, message = "Project was not found or is not available for this user." });
            }

            var tempFiles = new List<string>();
            try
            {
                List<PreparedDiagnosticFile> preparedL3Files;
                List<PreparedDiagnosticFile> preparedEventFiles;
                string originalUploadName;
                long totalUploadSize;

                if (zipFile is { Length: > 0 })
                {
                    if (!Path.GetExtension(zipFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        return BadRequest(new { status = 0, message = "Only a .zip file is supported for ZIP upload." });

                    (preparedL3Files, preparedEventFiles) = await PrepareL3EventZipAsync(zipFile, tempFiles, cancellationToken);
                    originalUploadName = Path.GetFileName(zipFile.FileName);
                    totalUploadSize = zipFile.Length;
                }
                else
                {
                    var normalizedType = NormalizeUploadDataType(dataType);
                    if (normalizedType == null)
                        return BadRequest(new { status = 0, message = "Upload a ZIP file or provide dataType as L3, Event, or L3Event." });

                    var requestedL3 = normalizedType.Value.HasL3;
                    var requestedEvent = normalizedType.Value.HasEvent;
                    if (requestedL3 && (l3File == null || l3File.Length == 0))
                        return BadRequest(new { status = 0, message = "L3 file is required for this upload type." });
                    if (requestedEvent && (eventFile == null || eventFile.Length == 0))
                        return BadRequest(new { status = 0, message = "Event file is required for this upload type." });

                    preparedL3Files = requestedL3 && l3File != null
                        ? [new PreparedDiagnosticFile(await SaveUploadTempFileAsync(l3File, tempFiles, cancellationToken), Path.GetFileName(l3File.FileName), l3File.Length)]
                        : [];
                    preparedEventFiles = requestedEvent && eventFile != null
                        ? [new PreparedDiagnosticFile(await SaveUploadTempFileAsync(eventFile, tempFiles, cancellationToken), Path.GetFileName(eventFile.FileName), eventFile.Length)]
                        : [];
                    originalUploadName = string.Join(", ", preparedL3Files.Concat(preparedEventFiles).Select(file => file.FileName));
                    totalUploadSize = preparedL3Files.Concat(preparedEventFiles).Sum(file => file.Length);
                }

                var hasL3 = preparedL3Files.Count > 0;
                var hasEvent = preparedEventFiles.Count > 0;
                await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {

                    var history = new tbl_upload_history
                    {
                        uploaded_on = DateTime.Now,
                        file_type = 1,
                        file_name = originalUploadName,
                        uploaded_by = userId,
                        remarks = linkedProjectId.HasValue
                            ? $"L3/Event Add Session upload for project {linkedProjectId.Value}"
                            : "L3/Event Add Session upload",
                        status = 1,
                        polygon_file = string.Empty
                    };
                    _context.Set<tbl_upload_history>().Add(history);
                    await _context.SaveChangesAsync(cancellationToken);
                    await UpdateUploadHistoryOriginalFileNameAsync(history.id, originalUploadName, cancellationToken);

                    var session = new tbl_session
                    {
                        user_id = userId,
                        type = "l3_event",
                        notes = $"Add Sessions: {(hasL3 && hasEvent ? "L3 + Event" : hasL3 ? "L3" : "Event")}",
                        uploaded_on = DateTime.Now,
                        tbl_upload_id = history.id.ToString(CultureInfo.InvariantCulture)
                    };
                    _context.tbl_session.Add(session);
                    await _context.SaveChangesAsync(cancellationToken);

                    var sessionId = session.id ?? 0;
                    if (sessionId <= 0)
                        throw new InvalidOperationException("Session creation failed.");

                    var insertedL3Rows = 0;
                    foreach (var file in preparedL3Files)
                        insertedL3Rows += await ImportL3FileAsync(sessionId, history.id, file.FilePath, file.FileName, cancellationToken);
                    var insertedEventRows = 0;
                    foreach (var file in preparedEventFiles)
                        insertedEventRows += await ImportEventFileAsync(sessionId, history.id, file.FilePath, file.FileName, cancellationToken);
                    if (hasL3 && insertedL3Rows == 0)
                        throw new InvalidDataException("No L3 rows could be parsed from the ZIP.");
                    if (hasEvent && insertedEventRows == 0)
                        throw new InvalidDataException("No Event rows could be parsed from the ZIP.");

                    var l3EventHistoryId = await InsertL3EventHistoryAsync(
                        linkedProjectId,
                        sessionId,
                        history.id,
                        hasL3,
                        hasEvent,
                        originalUploadName,
                        string.Join(", ", preparedL3Files.Select(file => file.FileName)),
                        string.Join(", ", preparedEventFiles.Select(file => file.FileName)),
                        totalUploadSize,
                        insertedL3Rows,
                        insertedEventRows,
                        cancellationToken);

                    await UpdateSessionL3EventFlagsAsync(sessionId, hasL3, hasEvent, cancellationToken);
                    if (linkedProjectId.HasValue)
                        await UpdateProjectForL3EventSessionAsync(linkedProjectId.Value, sessionId, hasL3, hasEvent, cancellationToken);

                    await tx.CommitAsync(cancellationToken);

                    return Ok(new
                    {
                        status = 1,
                        message = "L3/Event session created successfully.",
                        projectId = linkedProjectId,
                        sessionId,
                        uploadId = history.id,
                        fileName = originalUploadName,
                        l3 = hasL3,
                        @event = hasEvent,
                        history = new
                        {
                            uploadHistoryId = history.id,
                            l3EventHistoryId
                        },
                        rows = new
                        {
                            l3 = insertedL3Rows,
                            events = insertedEventRows
                        }
                    });
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }
            catch (InvalidDataException ex)
            {
                return BadRequest(new { status = 0, message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    status = 0,
                    message = "An error occurred while creating the L3/Event session.",
                    details = SafeException.Get(ex)
                });
            }
            finally
            {
                foreach (var tempFile in tempFiles)
                {
                    TryDeleteFile(tempFile);
                }
            }
        }

        [HttpGet("GetL3EventHistory")]
        public async Task<IActionResult> GetL3EventHistory(
            [FromQuery] int? projectId = null,
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery] string? dataType = null,
            [FromQuery] int take = 100,
            CancellationToken cancellationToken = default)
        {
            await EnsureL3EventSchemaAsync(cancellationToken);
            var rows = await GetL3EventUploadHistoryAsync(projectId, sessionId, sessionIds, dataType, Math.Clamp(take, 1, 50000), cancellationToken);
            return Ok(new { status = 1, data = rows });
        }

        private MapViewController CreateMapViewController()
        {
            return new MapViewController(_context, _httpContextAccessor, _env, _redis, _userScope)
            {
                ControllerContext = ControllerContext,
                Url = Url
            };
        }

        private int GetCurrentUserId()
        {
            return TryParseInt(User?.FindFirst("UserId")?.Value)
                ?? TryParseInt(User?.FindFirst("user_id")?.Value)
                ?? HttpContext.Session.GetInt32("UserID")
                ?? TryParseInt(HttpContext.Session.GetString("UserID"))
                ?? 0;
        }

        private static int? TryParseInt(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static (bool HasL3, bool HasEvent)? NormalizeUploadDataType(string? dataType)
        {
            var value = Regex.Replace(dataType ?? string.Empty, @"[\s_+\-/]+", string.Empty).ToLowerInvariant();
            return value switch
            {
                "l3" => (true, false),
                "event" or "events" => (false, true),
                "l3event" or "eventl3" or "both" => (true, true),
                _ => null
            };
        }

        private async Task<ProjectInfo?> GetAuthorizedProjectInfoAsync(int projectId, int userId, CancellationToken cancellationToken)
        {
            var isSuperAdmin = _userScope.IsSuperAdmin(User);
            var userCompanyId = await _context.tbl_user
                .AsNoTracking()
                .Where(x => x.id == userId)
                .Select(x => x.company_id)
                .FirstOrDefaultAsync(cancellationToken);

            var project = await _context.tbl_project
                .AsNoTracking()
                .Where(x => x.id == projectId)
                .Select(x => new ProjectInfo(x.id, x.company_id, x.ref_session_id))
                .FirstOrDefaultAsync(cancellationToken);

            if (project == null)
                return null;

            if (isSuperAdmin || userCompanyId == 0 || project.CompanyId == userCompanyId)
                return project;

            return null;
        }

        private async Task<string> SaveUploadTempFileAsync(IFormFile file, List<string> tempFiles, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedL3EventExtensions.Contains(extension))
                throw new InvalidDataException($"Unsupported file extension '{extension}'. Allowed: .csv, .txt.");

            var root = Path.Combine(Path.GetTempPath(), "signaltracker_l3_event_upload");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"{Guid.NewGuid():N}{extension}");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
            await file.CopyToAsync(stream, cancellationToken);
            tempFiles.Add(path);
            return path;
        }

        private async Task<(List<PreparedDiagnosticFile> L3Files, List<PreparedDiagnosticFile> EventFiles)> PrepareL3EventZipAsync(
            IFormFile zipFile,
            List<string> tempFiles,
            CancellationToken cancellationToken)
        {
            const long maxUncompressedBytes = 512L * 1024 * 1024;
            const int maxArchiveEntries = 2000;
            var root = Path.Combine(Path.GetTempPath(), "signaltracker_l3_event_upload");
            Directory.CreateDirectory(root);

            var zipPath = Path.Combine(root, $"{Guid.NewGuid():N}.zip");
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                await zipFile.CopyToAsync(output, cancellationToken);
            }
            tempFiles.Add(zipPath);

            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > maxArchiveEntries)
                throw new InvalidDataException($"ZIP contains too many entries. Maximum allowed is {maxArchiveEntries}.");

            var supportedEntries = archive.Entries
                .Where(entry => entry.Length > 0 && AllowedL3EventExtensions.Contains(Path.GetExtension(entry.Name)))
                .ToList();
            var l3Entries = supportedEntries
                .Where(entry => Path.GetFileNameWithoutExtension(entry.Name).Contains("L3", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var eventEntries = supportedEntries
                .Where(entry => Path.GetFileNameWithoutExtension(entry.Name).Contains("Event", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (l3Entries.Count == 0 || eventEntries.Count == 0)
                throw new InvalidDataException("ZIP must contain L3 and Event files (.csv or .txt) whose filenames contain 'L3' and 'Event'.");

            var selectedEntries = l3Entries.Concat(eventEntries).Distinct().ToList();
            if (selectedEntries.Any(entry => entry.Length > maxUncompressedBytes) || selectedEntries.Sum(entry => entry.Length) > maxUncompressedBytes)
                throw new InvalidDataException("The uncompressed L3 and Event files exceed the 512 MB upload limit.");

            var prepared = new Dictionary<ZipArchiveEntry, PreparedDiagnosticFile>();
            foreach (var entry in selectedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(entry.Name);
                var filePath = Path.Combine(root, $"{Guid.NewGuid():N}{extension}");
                await using (var input = entry.Open())
                await using (var output = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }
                tempFiles.Add(filePath);
                prepared[entry] = new PreparedDiagnosticFile(filePath, Path.GetFileName(entry.Name), entry.Length);
            }

            return (
                l3Entries.Select(entry => prepared[entry]).ToList(),
                eventEntries.Select(entry => prepared[entry]).ToList());
        }

        private async Task<int> ImportEventFileAsync(int sessionId, int uploadId, string filePath, string originalFileName, CancellationToken cancellationToken)
        {
            var inserted = 0;
            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                BadDataFound = null,
                MissingFieldFound = null,
                HeaderValidated = null,
                DetectColumnCountChanges = false
            });

            var rowNo = 0;
            await foreach (var record in csv.GetRecordsAsync<dynamic>(cancellationToken))
            {
                rowNo++;
                var row = NormalizeDiagnosticRow((IDictionary<string, object?>)record);
                await InsertEventDiagnosticRowAsync(sessionId, uploadId, originalFileName, rowNo, row, cancellationToken);
                inserted++;
            }

            return inserted;
        }

        private async Task<int> ImportL3FileAsync(int sessionId, int uploadId, string filePath, string originalFileName, CancellationToken cancellationToken)
        {
            var inserted = 0;
            if (string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in ReadL3MessageTextRecords(filePath))
                {
                    await InsertL3DiagnosticRowAsync(sessionId, uploadId, originalFileName, row.RowNo, "txt", row.Values, row.RawText, cancellationToken);
                    inserted++;
                }

                return inserted;
            }

            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                BadDataFound = null,
                MissingFieldFound = null,
                HeaderValidated = null,
                DetectColumnCountChanges = false
            });

            var rowNo = 0;
            await foreach (var record in csv.GetRecordsAsync<dynamic>(cancellationToken))
            {
                rowNo++;
                var row = NormalizeDiagnosticRow((IDictionary<string, object?>)record);
                await InsertL3DiagnosticRowAsync(sessionId, uploadId, originalFileName, rowNo, "csv", row, null, cancellationToken);
                inserted++;
            }

            return inserted;
        }

        private async Task InsertEventDiagnosticRowAsync(
            int sessionId,
            int uploadId,
            string fileName,
            int rowNo,
            Dictionary<string, object?> row,
            CancellationToken cancellationToken)
        {
            var conn = _context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = @"
                INSERT INTO tbl_event_log
                    (tbl_upload_id, session_id, source_file_name, row_no, timestamp_text, latitude, longitude,
                     category, event_name, detail, source, severity, raw_json)
                VALUES
                    (@uploadId, @sessionId, @fileName, @rowNo, @timestampText, @latitude, @longitude,
                     @category, @eventName, @detail, @source, @severity, @rawJson);";
            AddParam(cmd, "@uploadId", uploadId);
            AddParam(cmd, "@sessionId", sessionId);
            AddParam(cmd, "@fileName", Path.GetFileName(fileName));
            AddParam(cmd, "@rowNo", rowNo);
            AddParam(cmd, "@timestampText", GetDiagnosticValue(row, "timestamp", "time"));
            AddParam(cmd, "@latitude", ParseDiagnosticDouble(GetDiagnosticValue(row, "latitude", "lat")));
            AddParam(cmd, "@longitude", ParseDiagnosticDouble(GetDiagnosticValue(row, "longitude", "lon", "lng")));
            AddParam(cmd, "@category", GetDiagnosticValue(row, "category"));
            AddParam(cmd, "@eventName", GetDiagnosticValue(row, "event", "event_name", "message"));
            AddParam(cmd, "@detail", GetDiagnosticValue(row, "detail", "description"));
            AddParam(cmd, "@source", GetDiagnosticValue(row, "source"));
            AddParam(cmd, "@severity", GetDiagnosticValue(row, "severity", "level"));
            AddParam(cmd, "@rawJson", JsonSerializer.Serialize(row));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task InsertL3DiagnosticRowAsync(
            int sessionId,
            int uploadId,
            string fileName,
            int rowNo,
            string sourceFileType,
            Dictionary<string, object?> row,
            string? rawText,
            CancellationToken cancellationToken)
        {
            var conn = _context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = @"
                INSERT INTO tbl_l3_log
                    (tbl_upload_id, session_id, source_file_name, source_file_type, row_no, timestamp_text, latitude, longitude,
                     category, message, detail, source, severity, raw_text, raw_json)
                VALUES
                    (@uploadId, @sessionId, @fileName, @sourceFileType, @rowNo, @timestampText, @latitude, @longitude,
                     @category, @message, @detail, @source, @severity, @rawText, @rawJson);";
            AddParam(cmd, "@uploadId", uploadId);
            AddParam(cmd, "@sessionId", sessionId);
            AddParam(cmd, "@fileName", Path.GetFileName(fileName));
            AddParam(cmd, "@sourceFileType", sourceFileType);
            AddParam(cmd, "@rowNo", rowNo);
            AddParam(cmd, "@timestampText", GetDiagnosticValue(row, "timestamp", "time"));
            AddParam(cmd, "@latitude", ParseDiagnosticDouble(GetDiagnosticValue(row, "latitude", "lat")));
            AddParam(cmd, "@longitude", ParseDiagnosticDouble(GetDiagnosticValue(row, "longitude", "lon", "lng")));
            AddParam(cmd, "@category", GetDiagnosticValue(row, "category"));
            AddParam(cmd, "@message", GetDiagnosticValue(row, "message", "event", "message_name"));
            AddParam(cmd, "@detail", GetDiagnosticValue(row, "detail", "description"));
            AddParam(cmd, "@source", GetDiagnosticValue(row, "source"));
            AddParam(cmd, "@severity", GetDiagnosticValue(row, "severity", "level"));
            AddParam(cmd, "@rawText", rawText);
            AddParam(cmd, "@rawJson", JsonSerializer.Serialize(row));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task UpdateSessionL3EventFlagsAsync(int sessionId, bool hasL3, bool hasEvent, CancellationToken cancellationToken)
        {
            await ExecuteNonQueryAsync(
                "UPDATE tbl_session SET `l3` = @l3, `event` = @event WHERE id = @sessionId;",
                cancellationToken,
                ("@l3", hasL3),
                ("@event", hasEvent),
                ("@sessionId", sessionId));
        }

        private async Task UpdateProjectForL3EventSessionAsync(int projectId, int sessionId, bool hasL3, bool hasEvent, CancellationToken cancellationToken)
        {
            var project = await _context.tbl_project.FirstAsync(x => x.id == projectId, cancellationToken);
            var sessionIds = (project.ref_session_id ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(x => int.Parse(x, CultureInfo.InvariantCulture))
                .Append(sessionId)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            project.ref_session_id = string.Join(",", sessionIds);
            await _context.SaveChangesAsync(cancellationToken);

            var setParts = new List<string>();
            if (hasL3) setParts.Add("`l3` = TRUE");
            if (hasEvent) setParts.Add("`event` = TRUE");
            if (setParts.Count > 0)
            {
                await ExecuteNonQueryAsync(
                    $"UPDATE tbl_project SET {string.Join(", ", setParts)} WHERE id = @projectId;",
                    cancellationToken,
                    ("@projectId", projectId));
            }
        }

        private Task UpdateUploadHistoryOriginalFileNameAsync(int uploadId, string fileName, CancellationToken cancellationToken)
        {
            return ExecuteNonQueryAsync(
                "UPDATE tbl_upload_history SET original_file_name = @fileName WHERE id = @uploadId;",
                cancellationToken,
                ("@fileName", fileName),
                ("@uploadId", uploadId));
        }

        private async Task<long> InsertL3EventHistoryAsync(
            int? projectId,
            int sessionId,
            int uploadId,
            bool hasL3,
            bool hasEvent,
            string originalFileName,
            string? l3FileName,
            string? eventFileName,
            long totalFileSize,
            int l3RowsImported,
            int eventRowsImported,
            CancellationToken cancellationToken)
        {
            var dataType = hasL3 && hasEvent ? "L3Event" : hasL3 ? "L3" : "Event";

            var insertedId = await ExecuteScalarAsync(@"
                INSERT INTO tbl_l3_event_history
                    (project_id, session_id, tbl_upload_id, data_type, has_l3, has_event,
                     original_file_name, l3_file_name, event_file_name, total_file_size,
                     l3_rows_imported, event_rows_imported, uploaded_by, uploaded_on, status, remarks)
                VALUES
                    (@projectId, @sessionId, @uploadId, @dataType, @hasL3, @hasEvent,
                     @originalFileName, @l3FileName, @eventFileName, @totalFileSize,
                     @l3RowsImported, @eventRowsImported, @uploadedBy, @uploadedOn, 1, @remarks);
                SELECT LAST_INSERT_ID();",
                cancellationToken,
                ("@projectId", projectId),
                ("@sessionId", sessionId),
                ("@uploadId", uploadId),
                ("@dataType", dataType),
                ("@hasL3", hasL3),
                ("@hasEvent", hasEvent),
                ("@originalFileName", originalFileName),
                ("@l3FileName", l3FileName),
                ("@eventFileName", eventFileName),
                ("@totalFileSize", totalFileSize),
                ("@l3RowsImported", l3RowsImported),
                ("@eventRowsImported", eventRowsImported),
                ("@uploadedBy", GetCurrentUserId()),
                ("@uploadedOn", DateTime.Now),
                ("@remarks", $"{dataType} Add Session upload"));

            return Convert.ToInt64(insertedId, CultureInfo.InvariantCulture);
        }

        private async Task<List<object>> GetL3EventUploadHistoryAsync(
            int? projectId,
            int? sessionId,
            string? sessionIds,
            string? dataType,
            int take,
            CancellationToken cancellationToken)
        {
            var parsedSessionIds = ParseSessionIds(sessionIds);
            if (sessionId.HasValue && sessionId.Value > 0)
                parsedSessionIds.Add(sessionId.Value);

            var currentUserId = GetCurrentUserId();
            var isSuperAdmin = _userScope.IsSuperAdmin(User);
            var userCompanyId = currentUserId > 0
                ? await _context.tbl_user
                    .AsNoTracking()
                    .Where(x => x.id == currentUserId)
                    .Select(x => x.company_id)
                    .FirstOrDefaultAsync(cancellationToken)
                : 0;

            var conn = _context.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            var where = new List<string>();

            if (!isSuperAdmin)
            {
                if (userCompanyId > 0)
                {
                    where.Add("(p.company_id = @companyId OR (h.project_id IS NULL AND h.uploaded_by = @currentUserId))");
                    AddParam(cmd, "@companyId", userCompanyId);
                    AddParam(cmd, "@currentUserId", currentUserId);
                }
                else
                {
                    where.Add("h.uploaded_by = @currentUserId");
                    AddParam(cmd, "@currentUserId", currentUserId);
                }
            }

            if (projectId.HasValue && projectId.Value > 0)
            {
                where.Add("h.project_id = @projectId");
                AddParam(cmd, "@projectId", projectId.Value);
            }

            var normalizedDataType = Regex.Replace(dataType ?? string.Empty, @"[\s_+\-/]+", string.Empty).ToLowerInvariant();
            if (normalizedDataType is "l3" or "event" or "events" or "l3event" or "eventl3" or "both")
            {
                if (normalizedDataType == "l3")
                    where.Add("h.has_l3 = TRUE AND h.has_event = FALSE");
                else if (normalizedDataType is "event" or "events")
                    where.Add("h.has_l3 = FALSE AND h.has_event = TRUE");
                else
                    where.Add("h.has_l3 = TRUE AND h.has_event = TRUE");
            }

            if (parsedSessionIds.Count > 0)
            {
                var names = new List<string>();
                var index = 0;
                foreach (var id in parsedSessionIds.Distinct().Take(500))
                {
                    var name = $"@sessionId{index++}";
                    names.Add(name);
                    AddParam(cmd, name, id);
                }
                where.Add($"h.session_id IN ({string.Join(", ", names)})");
            }

            AddParam(cmd, "@take", take);
            cmd.CommandText = $@"
                SELECT h.id, h.project_id, p.project_name, h.session_id, h.tbl_upload_id, h.data_type, h.has_l3, h.has_event,
                       h.original_file_name, h.l3_file_name, h.event_file_name, h.total_file_size,
                       h.l3_rows_imported, h.event_rows_imported, h.uploaded_by, h.uploaded_on,
                       h.status, h.remarks, u.name AS uploaded_by_name
                FROM tbl_l3_event_history h
                LEFT JOIN tbl_project p ON p.id = h.project_id
                LEFT JOIN tbl_user u ON u.id = h.uploaded_by
                {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
                ORDER BY h.id DESC
                LIMIT @take;";

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new
                {
                    id = ReadDb<long>(reader, "id"),
                    projectId = ReadDb<int?>(reader, "project_id"),
                    projectName = ReadDb<string>(reader, "project_name"),
                    sessionId = ReadDb<int>(reader, "session_id"),
                    uploadHistoryId = ReadDb<int>(reader, "tbl_upload_id"),
                    dataType = ReadDb<string>(reader, "data_type"),
                    l3 = ReadDb<bool>(reader, "has_l3"),
                    @event = ReadDb<bool>(reader, "has_event"),
                    originalFileName = ReadDb<string>(reader, "original_file_name"),
                    l3FileName = ReadDb<string>(reader, "l3_file_name"),
                    eventFileName = ReadDb<string>(reader, "event_file_name"),
                    totalFileSize = ReadDb<long>(reader, "total_file_size"),
                    l3RowsImported = ReadDb<int>(reader, "l3_rows_imported"),
                    eventRowsImported = ReadDb<int>(reader, "event_rows_imported"),
                    uploadedBy = ReadDb<int>(reader, "uploaded_by"),
                    uploadedByName = ReadDb<string>(reader, "uploaded_by_name"),
                    uploadedOn = ReadDb<DateTime>(reader, "uploaded_on"),
                    status = ReadDb<short>(reader, "status"),
                    remarks = ReadDb<string>(reader, "remarks")
                });
            }

            return rows;
        }

        private async Task EnsureL3EventSchemaAsync(CancellationToken cancellationToken)
        {
            await EnsureColumnAsync("tbl_session", "l3", "BOOLEAN NOT NULL DEFAULT FALSE", cancellationToken);
            await EnsureColumnAsync("tbl_session", "event", "BOOLEAN NOT NULL DEFAULT FALSE", cancellationToken);
            await EnsureColumnAsync("tbl_project", "l3", "BOOLEAN NOT NULL DEFAULT FALSE", cancellationToken);
            await EnsureColumnAsync("tbl_project", "event", "BOOLEAN NOT NULL DEFAULT FALSE", cancellationToken);
            await EnsureColumnAsync("tbl_upload_history", "original_file_name", "LONGTEXT NULL", cancellationToken);
            await EnsureDiagnosticTablesAsync(cancellationToken);
            await EnsureDiagnosticHistoryTablesAsync(cancellationToken);
        }

        private async Task EnsureDiagnosticTablesAsync(CancellationToken cancellationToken)
        {
            await ExecuteNonQueryAsync(@"
                CREATE TABLE IF NOT EXISTS tbl_l3_log (
                    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    tbl_upload_id INT NULL,
                    session_id INT NULL,
                    source_file_name VARCHAR(255) NULL,
                    source_file_type VARCHAR(32) NULL,
                    row_no INT NULL,
                    timestamp_text VARCHAR(64) NULL,
                    latitude DOUBLE NULL,
                    longitude DOUBLE NULL,
                    category VARCHAR(128) NULL,
                    message VARCHAR(512) NULL,
                    detail LONGTEXT NULL,
                    source VARCHAR(128) NULL,
                    severity VARCHAR(64) NULL,
                    raw_text LONGTEXT NULL,
                    raw_json LONGTEXT NULL,
                    uploaded_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX ix_tbl_l3_log_session (session_id),
                    INDEX ix_tbl_l3_log_upload (tbl_upload_id)
                );", cancellationToken);

            await ExecuteNonQueryAsync(@"
                CREATE TABLE IF NOT EXISTS tbl_event_log (
                    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    tbl_upload_id INT NULL,
                    session_id INT NULL,
                    source_file_name VARCHAR(255) NULL,
                    row_no INT NULL,
                    timestamp_text VARCHAR(64) NULL,
                    latitude DOUBLE NULL,
                    longitude DOUBLE NULL,
                    category VARCHAR(128) NULL,
                    event_name VARCHAR(512) NULL,
                    detail LONGTEXT NULL,
                    source VARCHAR(128) NULL,
                    severity VARCHAR(64) NULL,
                    raw_json LONGTEXT NULL,
                    uploaded_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX ix_tbl_event_log_session (session_id),
                    INDEX ix_tbl_event_log_upload (tbl_upload_id)
                );", cancellationToken);
        }

        private async Task EnsureDiagnosticHistoryTablesAsync(CancellationToken cancellationToken)
        {
            await ExecuteNonQueryAsync(@"
                CREATE TABLE IF NOT EXISTS tbl_l3_event_history (
                    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    project_id INT NULL,
                    session_id INT NOT NULL,
                    tbl_upload_id INT NOT NULL,
                    data_type VARCHAR(32) NOT NULL,
                    has_l3 BOOLEAN NOT NULL DEFAULT FALSE,
                    has_event BOOLEAN NOT NULL DEFAULT FALSE,
                    original_file_name VARCHAR(500) NOT NULL,
                    l3_file_name VARCHAR(500) NULL,
                    event_file_name VARCHAR(500) NULL,
                    total_file_size BIGINT NOT NULL DEFAULT 0,
                    l3_rows_imported INT NOT NULL DEFAULT 0,
                    event_rows_imported INT NOT NULL DEFAULT 0,
                    uploaded_by INT NOT NULL,
                    uploaded_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    status SMALLINT NOT NULL DEFAULT 1,
                    remarks VARCHAR(500) NULL,
                    INDEX ix_tbl_l3_event_history_project (project_id),
                    INDEX ix_tbl_l3_event_history_session (session_id),
                    INDEX ix_tbl_l3_event_history_upload (tbl_upload_id),
                    INDEX ix_tbl_l3_event_history_type (data_type)
                );", cancellationToken);

            await ExecuteNonQueryAsync("ALTER TABLE tbl_l3_event_history MODIFY COLUMN project_id INT NULL;", cancellationToken);
        }

        private async Task EnsureColumnAsync(string tableName, string columnName, string definition, CancellationToken cancellationToken)
        {
            if (await ColumnExistsAsync(tableName, columnName, cancellationToken))
                return;

            await ExecuteNonQueryAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition};", cancellationToken);
        }

        private async Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken)
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName
                  AND COLUMN_NAME = @columnName;";
            AddParam(cmd, "@tableName", tableName);
            AddParam(cmd, "@columnName", columnName);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
        }

        private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                AddParam(cmd, name, value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                AddParam(cmd, name, value);
            return await cmd.ExecuteScalarAsync(cancellationToken);
        }

        private static HashSet<int> ParseSessionIds(string? sessionIds)
        {
            return (sessionIds ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
                .Where(x => x > 0)
                .ToHashSet();
        }

        private static T? ReadDb<T>(IDataRecord reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal))
                return default;

            var value = reader.GetValue(ordinal);
            if (value is T typed)
                return typed;

            return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object?> NormalizeDiagnosticRow(IDictionary<string, object?> source)
        {
            var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in source)
            {
                var key = Regex.Replace(kvp.Key ?? string.Empty, @"[^\w]+", "_").Trim('_').ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(key))
                    normalized[key] = kvp.Value;
            }
            return normalized;
        }

        private static string? GetDiagnosticValue(Dictionary<string, object?> row, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (row.TryGetValue(key, out var value) && value != null)
                {
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }
            return null;
        }

        private static double? ParseDiagnosticDouble(string? value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static List<(int RowNo, Dictionary<string, object?> Values, string RawText)> ReadL3MessageTextRecords(string filePath)
        {
            var records = new List<(int RowNo, Dictionary<string, object?> Values, string RawText)>();
            var current = new List<string>();

            void Flush()
            {
                if (current.Count == 0)
                    return;

                var first = current[0].Trim();
                if (!Regex.IsMatch(first, @"^\d{6}\s+"))
                {
                    current.Clear();
                    return;
                }

                var raw = string.Join(Environment.NewLine, current);
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["timestamp"] = Regex.Match(first, @"^\d{6}\s+(\S+)").Groups[1].Value,
                    ["category"] = Regex.Match(first, @"^\d{6}\s+\S+\s+\S+\s+(\S+)").Groups[1].Value,
                    ["message"] = first
                };

                var latLon = Regex.Match(raw, @"Latitude:\s*([-+]?\d+(?:\.\d+)?),\s*Longitude:\s*([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (latLon.Success)
                {
                    values["latitude"] = latLon.Groups[1].Value;
                    values["longitude"] = latLon.Groups[2].Value;
                }

                var messageName = Regex.Match(raw, "Message Name:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (messageName.Success)
                    values["message"] = messageName.Groups[1].Value;

                records.Add((records.Count + 1, values, raw));
                current.Clear();
            }

            foreach (var line in System.IO.File.ReadLines(filePath))
            {
                if (Regex.IsMatch(line, @"^\d{6}\s+") && current.Count > 0)
                    Flush();

                if (current.Count > 0 || Regex.IsMatch(line, @"^\d{6}\s+"))
                    current.Add(line);
            }

            Flush();
            return records;
        }

        private static void AddParam(System.Data.Common.DbCommand cmd, string name, object? value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch
            {
                // Temporary upload cleanup is best effort.
            }
        }

        private static readonly HashSet<string> AllowedL3EventExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv",
            ".txt"
        };

        private sealed record ProjectInfo(int Id, int? CompanyId, string? RefSessionId);
        private sealed record PreparedDiagnosticFile(string FilePath, string FileName, long Length);
    }
}
