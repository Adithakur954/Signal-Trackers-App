using SignalTracker.Helper;
using SignalTracker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using SignalTracker.Services; // Required for UserScopeService
using SignalTracker.Security;

namespace SignalTracker.Controllers
{
    [Authorize]
    [Route("ExcelUpload")]
    public class ExcelUploadController : BaseController
    {
        private readonly ApplicationDbContext db;
        private readonly CommonFunction cf;
        private readonly IWebHostEnvironment _env;
        private readonly UserScopeService _userScope; // 1. Service for Security Logic
        private readonly RedisService _redis;
        private readonly IServiceScopeFactory _scopeFactory;

        // Network-log ingestion (UploadFileType == 1) does a bulk row insert plus a
        // heavy self-join UPDATE against tbl_network_log inside one DB transaction.
        // If two of these run at the same time they lock each other out of the same
        // rows and eventually fail with MySQL "Lock wait timeout exceeded". This has
        // been observed happening across SEPARATE deployments/processes that all talk
        // to the same MySQL server (production + other instances hitting the shared
        // DB), so an in-process lock (SemaphoreSlim) isn't enough — it only protects
        // one process. Instead we take a MySQL named lock (GET_LOCK), which is
        // enforced by the DB server itself and serializes every connection from every
        // process/host that acquires the same lock name, regardless of where it runs.
        private const string NetworkLogIngestLockName = "stracer_networklog_ingest";
        private const int NetworkLogIngestLockTimeoutSeconds = 7200;
        private const int NetworkLogIngestLockPollSeconds = 15;
        private static readonly SemaphoreSlim NetworkLogIngestLocalGate = new(1, 1);

        // Robust timezone: Windows ("India Standard Time") and Linux ("Asia/Kolkata")
        private static readonly TimeZoneInfo INDIAN_ZONE = GetIndianZone();
        private static TimeZoneInfo GetIndianZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        }

        // Keep history table writes bounded even if the database column is still short.
        private static string? NormalizeUploadError(string? message, int maxLength = 255)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var trimmed = message.Trim();
            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed[..maxLength];
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

        private static string BuildCompactUploadFileName(string prefix, DateTime timestamp, string originalFileName)
        {
            var extension = Path.GetExtension(originalFileName);
            var stamp = timestamp.ToString("yyMMddHHmmssfff");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"{prefix}{stamp}_{suffix}{extension}";
        }

        private string GetChunkUploadRoot()
        {
            var path = Path.Combine(_env.ContentRootPath, "App_Data", "ChunkUploads");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string NormalizeChunkUploadId(string chunkUploadId)
        {
            if (!Guid.TryParse(chunkUploadId, out var parsed))
                throw new InvalidDataException("Invalid chunkUploadId.");

            return parsed.ToString("N");
        }

        private string GetChunkUploadDirectory(string chunkUploadId)
        {
            var safeId = NormalizeChunkUploadId(chunkUploadId);
            var path = Path.Combine(GetChunkUploadRoot(), safeId);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string GetChunkFileName(int chunkIndex)
            => chunkIndex.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".part";

        private sealed class ChunkUploadManifest
        {
            public string ChunkUploadId { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public int TotalChunks { get; set; }
            public int UploadFileType { get; set; }
            public string? Remarks { get; set; }
            public string? ProjectName { get; set; }
            public string? SessionIds { get; set; }
            public int UploadedChunks { get; set; }
            public int? UploadHistoryId { get; set; }
            public short Status { get; set; } = 2;
            public string? ErrorMessage { get; set; }
            public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;
        }

        private string GetChunkManifestPath(string chunkUploadId)
            => Path.Combine(GetChunkUploadDirectory(chunkUploadId), "manifest.json");

        private async Task<ChunkUploadManifest> ReadChunkManifestAsync(string chunkUploadId, CancellationToken ct = default)
        {
            var path = GetChunkManifestPath(chunkUploadId);
            if (!System.IO.File.Exists(path))
                throw new FileNotFoundException("Chunk upload was not found.");

            await using var stream = System.IO.File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ChunkUploadManifest>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("Chunk upload manifest is invalid.");
        }

        private async Task WriteChunkManifestAsync(ChunkUploadManifest manifest, CancellationToken ct = default)
        {
            manifest.UpdatedOn = DateTime.UtcNow;
            var path = GetChunkManifestPath(manifest.ChunkUploadId);
            await using var stream = System.IO.File.Create(path);
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct);
        }

        private async Task<bool> UploadHistoryOriginalFileNameColumnExistsAsync(CancellationToken ct = default)
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = conn.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(ct);

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = DATABASE()
                      AND table_name = 'tbl_upload_history'
                      AND column_name = 'original_file_name';";

                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                return count > 0;
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        private bool UploadHistoryOriginalFileNameColumnExists()
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = conn.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                conn.Open();

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = DATABASE()
                      AND table_name = 'tbl_upload_history'
                      AND column_name = 'original_file_name';";

                var count = Convert.ToInt32(cmd.ExecuteScalar());
                return count > 0;
            }
            finally
            {
                if (shouldClose)
                    conn.Close();
            }
        }

        private async Task SaveOriginalUploadFileNameAsync(int uploadHistoryId, string originalFileName, CancellationToken ct = default)
        {
            if (!await UploadHistoryOriginalFileNameColumnExistsAsync(ct))
                return;

            var conn = db.Database.GetDbConnection();
            var shouldClose = conn.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(ct);

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE tbl_upload_history
                    SET original_file_name = @originalFileName
                    WHERE id = @id;";

                var originalFileNameParam = cmd.CreateParameter();
                originalFileNameParam.ParameterName = "@originalFileName";
                originalFileNameParam.Value = originalFileName;
                cmd.Parameters.Add(originalFileNameParam);

                var idParam = cmd.CreateParameter();
                idParam.ParameterName = "@id";
                idParam.Value = uploadHistoryId;
                cmd.Parameters.Add(idParam);

                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }

        private (string StoredFileName, string? OriginalFileName)? FindUploadHistoryFileNames(string fileName)
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = conn.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                conn.Open();

            try
            {
                using var cmd = conn.CreateCommand();
                if (UploadHistoryOriginalFileNameColumnExists())
                {
                    cmd.CommandText = @"
                        SELECT file_name, original_file_name
                        FROM tbl_upload_history
                        WHERE file_name = @fileName OR original_file_name = @fileName
                        ORDER BY id DESC
                        LIMIT 1;";
                }
                else
                {
                    cmd.CommandText = @"
                        SELECT file_name, NULL AS original_file_name
                        FROM tbl_upload_history
                        WHERE file_name = @fileName
                        ORDER BY id DESC
                        LIMIT 1;";
                }

                var fileNameParam = cmd.CreateParameter();
                fileNameParam.ParameterName = "@fileName";
                fileNameParam.Value = fileName;
                cmd.Parameters.Add(fileNameParam);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;

                var storedFileName = reader.GetString(0);
                var originalFileName = reader.IsDBNull(1) ? null : reader.GetString(1);
                return (storedFileName, originalFileName);
            }
            finally
            {
                if (shouldClose)
                    conn.Close();
            }
        }

        private async Task<(int UploadHistoryId, bool Success, string? ErrorMessage, bool ProcessingStarted)> ProcessSingleUploadAsync(
            IFormFile uploadFile,
            string remarks,
            string polygonPath,
            string polygonFile,
            int userId,
            string userName,
            DateTime nowIst,
            int uploadFileType,
            int projectId)
        {
            var root = _env.ContentRootPath;
            var uploadsDir = Path.Combine(root, "UploadedExcels");
            Directory.CreateDirectory(uploadsDir);

            string originalMainName = Path.GetFileName(uploadFile.FileName);
            string savedMainName = string.IsNullOrWhiteSpace(originalMainName)
                ? BuildCompactUploadFileName("U_", nowIst, uploadFile.FileName)
                : originalMainName;
            string mainPath = Path.Combine(uploadsDir, savedMainName);

            using (var stream = System.IO.File.Create(mainPath))
            {
                await uploadFile.CopyToAsync(stream);
            }

            return await ProcessStoredUploadAsync(
                mainPath,
                savedMainName,
                remarks,
                polygonPath,
                polygonFile,
                userId,
                uploadFileType,
                projectId,
                nowIst);
        }

        private async Task<(int UploadHistoryId, bool Success, string? ErrorMessage, bool ProcessingStarted)> ProcessStoredUploadAsync(
            string mainPath,
            string savedMainName,
            string remarks,
            string polygonPath,
            string polygonFile,
            int userId,
            int uploadFileType,
            int projectId,
            DateTime nowIst)
        {
            var excelDetails = new tbl_upload_history
            {
                remarks = remarks,
                file_name = savedMainName,
                original_file_name = savedMainName,
                polygon_file = polygonFile,
                file_type = uploadFileType,
                status = 2,
                uploaded_by = userId,
                uploaded_on = nowIst
            };

            db.tbl_upload_history.Add(excelDetails);
            await db.SaveChangesAsync();
            await SaveOriginalUploadFileNameAsync(excelDetails.id, savedMainName);

            if (uploadFileType == 1)
            {
                _ = Task.Run(() => ProcessNetworkLogUploadInBackgroundAsync(
                    excelDetails.id,
                    mainPath,
                    savedMainName,
                    polygonPath,
                    uploadFileType,
                    projectId,
                    remarks,
                    userId));

                return (excelDetails.id, true, null, true);
            }

            string errorMsg = "";
            var csvProc = new ProcessCSVController(db, cf, _redis);

            bool ok;
            ok = csvProc.Process(
                excelDetails.id,
                mainPath,
                savedMainName,
                polygonPath,
                uploadFileType,
                projectId,
                remarks,
                out errorMsg,
                userId
            );

            excelDetails.status = (short)(ok ? 1 : 0);
            excelDetails.errors = NormalizeUploadError(errorMsg);
            await db.SaveChangesAsync();

            return (excelDetails.id, ok, errorMsg, false);
        }

        private async Task ProcessNetworkLogUploadInBackgroundAsync(
            int uploadHistoryId,
            string mainPath,
            string originalFileName,
            string polygonPath,
            int uploadFileType,
            int projectId,
            string remarks,
            int userId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var httpContextAccessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                var redis = scope.ServiceProvider.GetRequiredService<RedisService>();
                var scopedCommonFunction = new CommonFunction(scopedDb, httpContextAccessor);
                var csvProc = new ProcessCSVController(scopedDb, scopedCommonFunction, redis);

                string errorMsg = "";
                var (ok, processingMessage) = await RunNetworkLogIngestSerializedAsync(scopedDb, () =>
                {
                    var success = csvProc.Process(
                        uploadHistoryId,
                        mainPath,
                        originalFileName,
                        polygonPath,
                        uploadFileType,
                        projectId,
                        remarks,
                        out var innerError,
                        userId
                    );
                    return (success, innerError);
                });

                if (!string.IsNullOrWhiteSpace(processingMessage))
                {
                    errorMsg = processingMessage;
                }

                var history = await scopedDb.tbl_upload_history.FirstOrDefaultAsync(x => x.id == uploadHistoryId);
                if (history != null)
                {
                    history.status = (short)(ok ? 1 : 0);
                    history.errors = NormalizeUploadError(errorMsg);
                    await scopedDb.SaveChangesAsync();
                }

                if (ok && string.Equals(Path.GetExtension(mainPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var scopedUserScope = scope.ServiceProvider.GetRequiredService<UserScopeService>();
                    var scopedConnectionProvider = scope.ServiceProvider.GetRequiredService<IDbConnectionProvider>();
                    var scopedNetworkLogData = scope.ServiceProvider.GetRequiredService<NetworkLogDataService>();
                    var l3EventController = new L3EventController(
                        scopedDb,
                        httpContextAccessor,
                        _env,
                        redis,
                        scopedUserScope,
                        scopedConnectionProvider,
                        scopedNetworkLogData);
                    await l3EventController.RegisterCompletedUploadAsync(
                        uploadHistoryId,
                        originalFileName,
                        userId,
                        projectId > 0 ? projectId : null,
                        remarks);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Network-log background upload {uploadHistoryId} failed: {ex}");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var history = await scopedDb.tbl_upload_history.FirstOrDefaultAsync(x => x.id == uploadHistoryId);
                    if (history != null)
                    {
                        history.status = 0;
                        history.errors = NormalizeUploadError(SafeException.GetInnermost(ex));
                        await scopedDb.SaveChangesAsync();
                    }
                }
                catch
                {
                    // If even status persistence fails, leave the row in Processing;
                    // the server log remains the source of truth for this rare case.
                }
            }
        }

        // Acquires a MySQL named lock scoped to the same physical DB server `db` is
        // connected to, runs `work`, then always releases the lock (including on
        // exceptions and connection drops — MySQL auto-releases a session's named
        // locks if the connection is lost). If another process/host is already
        // holding the lock, wait outside MySQL and retry. Do not block inside
        // GET_LOCK for hours, because every waiting GET_LOCK consumes one pooled
        // database connection and can starve the whole web app.
        private async Task<(bool Success, string? ErrorMessage)> RunNetworkLogIngestSerializedAsync(
            Func<(bool Success, string? ErrorMessage)> work)
        {
            return await RunNetworkLogIngestSerializedAsync(db, work);
        }

        private static async Task<(bool Success, string? ErrorMessage)> RunNetworkLogIngestSerializedAsync(
            ApplicationDbContext targetDb,
            Func<(bool Success, string? ErrorMessage)> work)
        {
            var connectionString = targetDb.Database.GetConnectionString();
            var waitStarted = DateTime.UtcNow;
            var localGateAcquired = false;

            try
            {
                while (!localGateAcquired)
                {
                    localGateAcquired = await NetworkLogIngestLocalGate.WaitAsync(TimeSpan.FromSeconds(NetworkLogIngestLockPollSeconds));
                    if (localGateAcquired)
                    {
                        break;
                    }

                    if ((DateTime.UtcNow - waitStarted).TotalSeconds >= NetworkLogIngestLockTimeoutSeconds)
                    {
                        return (false,
                            "Another network-log upload is still being processed on the server. This upload could not start before the queue timeout.");
                    }
                }

                while (true)
                {
                    await using var lockConnection = new MySqlConnection(connectionString);
                    await lockConnection.OpenAsync();

                    bool lockAcquired;
                    await using (var acquireCmd = lockConnection.CreateCommand())
                    {
                        acquireCmd.CommandText = "SELECT GET_LOCK(@lockName, 0)";
                        acquireCmd.CommandTimeout = 30;
                        acquireCmd.Parameters.AddWithValue("@lockName", NetworkLogIngestLockName);
                        var result = await acquireCmd.ExecuteScalarAsync();
                        lockAcquired = result is long l && l == 1;
                    }

                    if (lockAcquired)
                    {
                        try
                        {
                            return work();
                        }
                        finally
                        {
                            try
                            {
                                await using var releaseCmd = lockConnection.CreateCommand();
                                releaseCmd.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                                releaseCmd.Parameters.AddWithValue("@lockName", NetworkLogIngestLockName);
                                await releaseCmd.ExecuteScalarAsync();
                            }
                            catch
                            {
                                // Best-effort release; closing the connection below also
                                // releases the lock server-side if this fails for any reason.
                            }
                        }
                    }

                    if ((DateTime.UtcNow - waitStarted).TotalSeconds >= NetworkLogIngestLockTimeoutSeconds)
                    {
                        return (false,
                            "Another network-log upload is still being processed on the server. This upload could not start before the queue timeout.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(NetworkLogIngestLockPollSeconds));
                }
            }
            finally
            {
                if (localGateAcquired)
                {
                    NetworkLogIngestLocalGate.Release();
                }
            }
        }

        public ExcelUploadController(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            IWebHostEnvironment env,
            UserScopeService userScope,
            RedisService redis,
            IServiceScopeFactory scopeFactory) // 2. Inject Service
        {
            db = context;
            cf = new CommonFunction(context, httpContextAccessor);
            _env = env;
            _userScope = userScope;
            _redis = redis;
            _scopeFactory = scopeFactory;
        }

        // GET: /ExcelUpload/Index
        [HttpGet("Index")]
        public ActionResult Index()
        {
            if (!IsAngularRequest() || !cf.SessionCheck())
                return RedirectToAction("Index", "Home");
            return View();
        }

        // GET: /ExcelUpload/DownloadExcel
        [HttpGet("DownloadExcel")]
        public IActionResult DownloadExcel(int fileType, string? fileName)
        {
            var root = _env.ContentRootPath;
            string filePath;
            string downloadName;

            if (fileType == 0)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    return Json(new { status = 0, message = "fileName is required for fileType=0" });

                // Security: Prevent Directory Traversal
                fileName = Path.GetFileName(fileName);

                var uploadsDir = Path.Combine(root, "UploadedExcels");
                if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

                filePath = Path.Combine(uploadsDir, fileName);
                downloadName = fileName;

                var uploadHistory = FindUploadHistoryFileNames(fileName);

                if (uploadHistory != null)
                {
                    var storedFileName = Path.GetFileName(uploadHistory.Value.StoredFileName);
                    var storedPath = Path.Combine(uploadsDir, storedFileName);
                    if (System.IO.File.Exists(storedPath))
                    {
                        filePath = storedPath;
                        downloadName = string.IsNullOrWhiteSpace(uploadHistory.Value.OriginalFileName)
                            ? storedFileName
                            : Path.GetFileName(uploadHistory.Value.OriginalFileName);
                    }
                }
            }
            else
            {
                string? templateName = null;
                if (fileType >= 0 && Constant.TempFiles != null && fileType < Constant.TempFiles.Length)
                    templateName = Constant.TempFiles[fileType];

                if (string.IsNullOrWhiteSpace(templateName))
                    return Json(new { status = 0, message = "Unknown template type." });

                var templatesDir = Path.Combine(root, "Template-Files");
                filePath = Path.Combine(templatesDir, templateName);
                downloadName = templateName;
            }

            if (!System.IO.File.Exists(filePath))
                return Json(new { status = 0, message = "Template not found" });

            var bytes = System.IO.File.ReadAllBytes(filePath);
            var contentType = CommonFunction.GetMimeType(filePath);
            return File(bytes, contentType, downloadName);
        }

        // GET: /ExcelUpload/DownloadPythonRuntime
        [HttpGet("DownloadPythonRuntime")]
        [AllowAnonymous]
        [PublicApiKey]
        public IActionResult DownloadPythonRuntime()
        {
            return DownloadExcel(4, null);
        }

        // GET: /ExcelUpload/GetUploadedExcelFiles
        [HttpGet("GetUploadedExcelFiles")]
        public async Task<IActionResult> GetUploadedExcelFiles(
            int fileType, 
            [FromQuery] int? company_id = null, // Added for SuperAdmin filtering
            CancellationToken ct = default)
        {
            try
            {
                // =========================================================
                // 1. SMART SECURITY: RESOLVE COMPANY ID
                // =========================================================
                int targetCompanyId = GetTargetCompanyId(company_id);

                // Security Check: If regular admin (not super) and no company resolved, block.
                if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User))
                {
                    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company Context." });
                }

                Console.WriteLine($"📥 GetUploadedExcelFiles - fileType: {fileType}, CompanyId: {targetCompanyId}");

                // =========================================================
                // 2. QUERY WITH SECURITY FILTER
                // =========================================================
               var query =
    from h in db.tbl_upload_history.AsNoTracking()

    join u in db.tbl_user.AsNoTracking()
        on h.uploaded_by equals u.id into gu
    from u in gu.DefaultIfEmpty()

    // ✅ FIX: Use SqlFunctions or Convert to ensure types match
    // Depending on your EF version, h.id.ToString() is the standard way
    join s in db.tbl_session.AsNoTracking()
        on h.id.ToString() equals s.tbl_upload_id into gs
    from s in gs.DefaultIfEmpty()

    where h.file_type == fileType
    && (targetCompanyId == 0 || (u != null && u.company_id == targetCompanyId))
    && (s == null || s.type != "l3_event")
    && (h.remarks == null || !h.remarks.StartsWith("L3/Event"))

    select new
    {
        id = h.id,
        file_type = h.file_type,
        // ✅ Use s.id since session_id column is missing in your DB schema
        session_id = s != null ? s.id.ToString() : null,
        file_name = h.file_name,
        stored_file_name = h.file_name,
        uploaded_on = h.uploaded_on,
        uploaded_by = u != null ? u.name : "Unknown",
        status = h.status == 1 ? "Success" : 
                 (h.status == 2 ? "Processing" : "Failed"),
        remarks = h.remarks,
        errors = h.errors
    };
                var data = await query
                    .OrderByDescending(x => x.id)
                    .Take(20)
                    .ToListAsync(ct);

                var originalFileNames = new Dictionary<int, string>();
                if (await UploadHistoryOriginalFileNameColumnExistsAsync(ct) && data.Count > 0)
                {
                    var ids = data.Select(x => x.id).ToList();
                    var conn = db.Database.GetDbConnection();
                    var shouldClose = conn.State != System.Data.ConnectionState.Open;
                    if (shouldClose)
                        await conn.OpenAsync(ct);

                    try
                    {
                        await using var cmd = conn.CreateCommand();
                        var idParameterNames = new List<string>();
                        for (var i = 0; i < ids.Count; i++)
                        {
                            var paramName = $"@id{i}";
                            idParameterNames.Add(paramName);
                            var param = cmd.CreateParameter();
                            param.ParameterName = paramName;
                            param.Value = ids[i];
                            cmd.Parameters.Add(param);
                        }

                        cmd.CommandText = $@"
                            SELECT id, original_file_name
                            FROM tbl_upload_history
                            WHERE id IN ({string.Join(", ", idParameterNames)})
                              AND original_file_name IS NOT NULL
                              AND original_file_name <> '';";

                        await using var reader = await cmd.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                        {
                            originalFileNames[reader.GetInt32(0)] = reader.GetString(1);
                        }
                    }
                    finally
                    {
                        if (shouldClose)
                            await conn.CloseAsync();
                    }
                }

                var displayData = data.Select(x => new
                {
                    x.id,
                    x.file_type,
                    x.session_id,
                    file_name = originalFileNames.TryGetValue(x.id, out var originalFileName)
                        ? originalFileName
                        : x.file_name,
                    original_file_name = originalFileNames.TryGetValue(x.id, out var originalName)
                        ? originalName
                        : null,
                    x.stored_file_name,
                    x.uploaded_on,
                    x.uploaded_by,
                    x.status,
                    x.remarks,
                    x.errors
                }).ToList();

                return Ok(new { Status = 1, Data = displayData });
            }
            catch (Exception)
            {
                Console.WriteLine("❌ Error in GetUploadedExcelFiles: operation failed (see server logs)");
                return StatusCode(500, new { Status = 0, Message = "An internal server error occurred." });
            }
        }

        [HttpPost("StartChunkUpload")]
        public async Task<IActionResult> StartChunkUpload(
            [FromForm] string fileName,
            [FromForm] long fileSize,
            [FromForm] int totalChunks,
            [FromForm] int UploadFileType,
            [FromForm] string? remarks = null,
            [FromForm] string? ProjectName = null,
            [FromForm] string? SessionIds = null,
            CancellationToken ct = default)
        {
            try
            {
                cf.SessionCheck();

                if (string.IsNullOrWhiteSpace(fileName))
                    return Json(new { Status = 0, Message = "fileName is required." });
                if (fileSize <= 0)
                    return Json(new { Status = 0, Message = "fileSize is required." });
                if (totalChunks <= 0)
                    return Json(new { Status = 0, Message = "totalChunks is required." });

                var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
                if (UploadFileType == 1 && extension != ".csv" && extension != ".zip")
                    return Json(new { Status = 0, Message = "Session uploads support only .csv or .zip files." });

                var chunkUploadId = Guid.NewGuid().ToString("N");
                var manifest = new ChunkUploadManifest
                {
                    ChunkUploadId = chunkUploadId,
                    FileName = Path.GetFileName(fileName),
                    FileSize = fileSize,
                    TotalChunks = totalChunks,
                    UploadFileType = UploadFileType,
                    Remarks = remarks,
                    ProjectName = ProjectName,
                    SessionIds = SessionIds,
                    UploadedChunks = 0,
                    Status = 2
                };

                await WriteChunkManifestAsync(manifest, ct);

                return Json(new
                {
                    Status = 1,
                    Message = "Chunk upload started.",
                    ChunkUploadId = chunkUploadId,
                    TotalChunks = totalChunks,
                    UploadedChunks = 0
                });
            }
            catch (Exception ex)
            {
                return Json(new { Status = 0, Message = SafeException.GetInnermost(ex) });
            }
        }

        [HttpPost("UploadChunk")]
        [RequestSizeLimit(64_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 64_000_000, ValueLengthLimit = int.MaxValue, MultipartHeadersLengthLimit = int.MaxValue)]
        public async Task<IActionResult> UploadChunk(
            [FromForm] string chunkUploadId,
            [FromForm] int chunkIndex,
            [FromForm] int totalChunks,
            [FromForm] IFormFile chunk,
            CancellationToken ct = default)
        {
            try
            {
                cf.SessionCheck();

                var manifest = await ReadChunkManifestAsync(chunkUploadId, ct);
                if (totalChunks != manifest.TotalChunks)
                    return Json(new { Status = 0, Message = "totalChunks does not match upload manifest." });
                if (chunkIndex < 0 || chunkIndex >= manifest.TotalChunks)
                    return Json(new { Status = 0, Message = "Invalid chunkIndex." });
                if (chunk == null || chunk.Length == 0)
                    return Json(new { Status = 0, Message = "Chunk file is required." });

                var uploadDir = GetChunkUploadDirectory(chunkUploadId);
                var chunkPath = Path.Combine(uploadDir, GetChunkFileName(chunkIndex));
                await using (var output = System.IO.File.Create(chunkPath))
                {
                    await chunk.CopyToAsync(output, ct);
                }

                manifest.UploadedChunks = Directory
                    .EnumerateFiles(uploadDir, "*.part")
                    .Count();
                await WriteChunkManifestAsync(manifest, ct);

                return Json(new
                {
                    Status = 1,
                    Message = "Chunk uploaded.",
                    ChunkUploadId = manifest.ChunkUploadId,
                    ChunkIndex = chunkIndex,
                    UploadedChunks = manifest.UploadedChunks,
                    TotalChunks = manifest.TotalChunks,
                    UploadProgress = manifest.TotalChunks == 0 ? 0 : (int)Math.Round(manifest.UploadedChunks * 100.0 / manifest.TotalChunks)
                });
            }
            catch (Exception ex)
            {
                return Json(new { Status = 0, Message = SafeException.GetInnermost(ex) });
            }
        }

        [HttpPost("CompleteChunkUpload")]
        [RequestSizeLimit(64_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 64_000_000, ValueLengthLimit = int.MaxValue, MultipartHeadersLengthLimit = int.MaxValue)]
        public async Task<IActionResult> CompleteChunkUpload(
            [FromForm] string chunkUploadId,
            [FromForm] string? remarks = null,
            [FromForm] string? ProjectName = null,
            [FromForm] string? SessionIds = null,
            [FromForm] int? UploadFileType = null,
            [FromForm] IFormFile? UploadNoteFile = null,
            CancellationToken ct = default)
        {
            try
            {
                cf.SessionCheck();

                var manifest = await ReadChunkManifestAsync(chunkUploadId, ct);
                var uploadDir = GetChunkUploadDirectory(chunkUploadId);
                var missing = Enumerable.Range(0, manifest.TotalChunks)
                    .Where(i => !System.IO.File.Exists(Path.Combine(uploadDir, GetChunkFileName(i))))
                    .ToList();
                if (missing.Count > 0)
                    return Json(new { Status = 0, Message = $"Missing chunks: {string.Join(",", missing.Take(20))}" });

                var root = _env.ContentRootPath;
                var uploadsDir = Path.Combine(root, "UploadedExcels");
                Directory.CreateDirectory(uploadsDir);
                DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, INDIAN_ZONE);
                var savedMainName = BuildCompactUploadFileName("U_", nowIst, manifest.FileName);
                var mainPath = Path.Combine(uploadsDir, savedMainName);

                await using (var merged = System.IO.File.Create(mainPath))
                {
                    for (var i = 0; i < manifest.TotalChunks; i++)
                    {
                        var chunkPath = Path.Combine(uploadDir, GetChunkFileName(i));
                        await using var input = System.IO.File.OpenRead(chunkPath);
                        await input.CopyToAsync(merged, ct);
                    }
                }

                string polygonPath = "";
                string polygonFile = "";
                if (UploadNoteFile != null && UploadNoteFile.Length > 0)
                {
                    polygonFile = BuildCompactUploadFileName("P_", nowIst, UploadNoteFile.FileName);
                    polygonPath = Path.Combine(uploadsDir, polygonFile);
                    await using var polygonOutput = System.IO.File.Create(polygonPath);
                    await UploadNoteFile.CopyToAsync(polygonOutput, ct);
                }

                int userId = cf.UserId > 0 ? cf.UserId : Convert.ToInt32(HttpContext.Session.GetInt32("UserID"));
                string userName = cf.UserName ?? "Unknown";
                int uploadFileType = UploadFileType.GetValueOrDefault(manifest.UploadFileType);
                int projectId = 0;

                if (uploadFileType == 2)
                {
                    var objProject = new tbl_project
                    {
                        project_name = FirstNonBlank(ProjectName, manifest.ProjectName),
                        ref_session_id = FirstNonBlank(SessionIds, manifest.SessionIds),
                        created_by_user_id = userId,
                        created_by_user_name = userName,
                        status = 1
                    };

                    db.tbl_project.Add(objProject);
                    await db.SaveChangesAsync(ct);
                    projectId = objProject.id;
                }

                var result = await ProcessStoredUploadAsync(
                    mainPath,
                    savedMainName,
                    FirstNonBlank(remarks, manifest.Remarks) ?? string.Empty,
                    polygonPath,
                    polygonFile,
                    userId,
                    uploadFileType,
                    projectId,
                    nowIst);

                manifest.UploadHistoryId = result.UploadHistoryId;
                manifest.Status = result.ProcessingStarted ? (short)2 : (short)(result.Success ? 1 : 0);
                manifest.ErrorMessage = result.ErrorMessage;
                await WriteChunkManifestAsync(manifest, ct);

                return Json(new
                {
                    Status = result.ProcessingStarted ? 2 : (result.Success ? 1 : 0),
                    Message = result.ProcessingStarted
                        ? "File uploaded successfully. Processing started in the background."
                        : result.Success
                            ? "File uploaded and processed successfully."
                            : "Upload failed.",
                    ChunkUploadId = manifest.ChunkUploadId,
                    UploadId = result.UploadHistoryId,
                    ProcessingStarted = result.ProcessingStarted,
                    ErrorMessage = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                try
                {
                    var manifest = await ReadChunkManifestAsync(chunkUploadId, CancellationToken.None);
                    manifest.Status = 0;
                    manifest.ErrorMessage = SafeException.GetInnermost(ex);
                    await WriteChunkManifestAsync(manifest, CancellationToken.None);
                }
                catch { }

                return Json(new { Status = 0, Message = SafeException.GetInnermost(ex) });
            }
        }

        [HttpGet("GetChunkUploadStatus")]
        public async Task<IActionResult> GetChunkUploadStatus([FromQuery] string? chunkUploadId = null, [FromQuery] int? uploadId = null, CancellationToken ct = default)
        {
            try
            {
                cf.SessionCheck();

                ChunkUploadManifest? manifest = null;
                if (!string.IsNullOrWhiteSpace(chunkUploadId))
                {
                    manifest = await ReadChunkManifestAsync(chunkUploadId, ct);
                    uploadId ??= manifest.UploadHistoryId;
                }

                short? status = manifest?.Status;
                string? error = manifest?.ErrorMessage;
                if (uploadId.GetValueOrDefault() > 0)
                {
                    var history = await db.tbl_upload_history
                        .AsNoTracking()
                        .Where(x => x.id == uploadId.Value)
                        .Select(x => new { x.status, x.errors })
                        .FirstOrDefaultAsync(ct);
                    if (history != null)
                    {
                        status = history.status;
                        error = history.errors;
                    }
                }

                var uploadedChunks = manifest?.UploadedChunks ?? 0;
                var totalChunks = manifest?.TotalChunks ?? 0;
                var uploadProgress = totalChunks == 0 ? 0 : (int)Math.Round(uploadedChunks * 100.0 / totalChunks);
                var processingProgress = status switch
                {
                    1 => 100,
                    0 => 100,
                    2 => uploadProgress >= 100 ? 50 : 0,
                    _ => 0
                };

                return Json(new
                {
                    Status = status ?? 2,
                    ChunkUploadId = manifest?.ChunkUploadId,
                    UploadId = uploadId,
                    UploadedChunks = uploadedChunks,
                    TotalChunks = totalChunks,
                    UploadProgress = uploadProgress,
                    ProcessingProgress = processingProgress,
                    State = (status ?? 2) == 1 ? "completed" : (status ?? 2) == 0 ? "failed" : uploadProgress >= 100 ? "processing" : "uploading",
                    ErrorMessage = error
                });
            }
            catch (Exception ex)
            {
                return Json(new { Status = 0, Message = SafeException.GetInnermost(ex) });
            }
        }

        // POST: /ExcelUpload/UploadExcelFile
        [HttpPost("UploadExcelFile")]
        [RequestSizeLimit(524_288_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 524_288_000, ValueLengthLimit = int.MaxValue, MultipartHeadersLengthLimit = int.MaxValue)]
        public async Task<IActionResult> UploadExcelFile(
            [FromForm] string remarks,
            [FromForm] string token,
            [FromForm] string ip,
            [FromForm] string ProjectName,
            [FromForm] string SessionIds,
            [FromForm] int UploadFileType,
            List<IFormFile> UploadFile,
            IFormFile UploadNoteFile
        )
        {
            try
            {
                cf.SessionCheck();

                var uploadFiles = (UploadFile ?? new List<IFormFile>())
                    .Where(file => file != null && file.Length > 0)
                    .ToList();

                if (uploadFiles.Count == 0)
                    return Json(new { Status = 0, Message = "Please select at least one excel file." });

                if (UploadFileType == 1)
                {
                    var invalidSessionFile = uploadFiles.FirstOrDefault(file => !IsCsvOrZip(file));
                    if (invalidSessionFile != null)
                    {
                        return Json(new
                        {
                            Status = 0,
                            Message = $"{invalidSessionFile.FileName} is not supported for session uploads. Please upload a .csv file or the session .zip export."
                        });
                    }
                }

                const long maxUploadBytes = 524_288_000;
                var oversized = uploadFiles.FirstOrDefault(file => file.Length > maxUploadBytes);
                if (oversized != null)
                    return Json(new { Status = 0, Message = "Each upload file must be less than 500 MB." });

                if (UploadNoteFile != null && UploadNoteFile.Length > maxUploadBytes)
                    return Json(new { Status = 0, Message = "Polygon file size must be less than 500 MB." });

                // -------- SAVE FILES --------
                var root = _env.ContentRootPath;
                var uploadsDir = Path.Combine(root, "UploadedExcels");
                Directory.CreateDirectory(uploadsDir);

                DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, INDIAN_ZONE);

                // Polygon file if exists
                string polygonPath = "";
                string polygonFile = "";

                if (UploadNoteFile != null && UploadNoteFile.Length > 0)
                {
                    polygonFile = BuildCompactUploadFileName("P_", nowIst, UploadNoteFile.FileName);
                    polygonPath = Path.Combine(uploadsDir, polygonFile);

                    using (var stream = System.IO.File.Create(polygonPath))
                        await UploadNoteFile.CopyToAsync(stream);
                }

                int userId = cf.UserId > 0 ? cf.UserId : Convert.ToInt32(HttpContext.Session.GetInt32("UserID"));
                string userName = cf.UserName ?? "Unknown";
                int projectId = 0;

                if (UploadFileType == 2)
                {
                    var objProject = new tbl_project
                    {
                        project_name = ProjectName,
                        ref_session_id = SessionIds,
                        created_by_user_id = userId,
                        created_by_user_name = userName,
                        status = 1
                    };

                    db.tbl_project.Add(objProject);
                    db.SaveChanges();
                    projectId = objProject.id;
                }

                var results = new List<(int UploadHistoryId, bool Success, string? ErrorMessage, string FileName, bool ProcessingStarted)>();
                foreach (var file in uploadFiles)
                {
                    var result = await ProcessSingleUploadAsync(
                        file,
                        remarks,
                        polygonPath,
                        polygonFile,
                        userId,
                        userName,
                        nowIst,
                        UploadFileType,
                        projectId
                    );
                    results.Add((result.UploadHistoryId, result.Success, result.ErrorMessage, file.FileName, result.ProcessingStarted));
                }

                bool allOk = results.All(item => item.Success);
                bool anyProcessingStarted = results.Any(item => item.ProcessingStarted);
                var failures = results
                    .Where(item => !item.Success)
                    .Select(item => new
                    {
                        item.FileName,
                        item.UploadHistoryId,
                        ErrorMessage = string.IsNullOrWhiteSpace(item.ErrorMessage)
                            ? "Processing failed without a detailed error message."
                            : item.ErrorMessage
                    })
                    .ToList();
                var failedFiles = failures.Select(item => item.FileName).ToList();
                var createdUploadIds = results.Select(item => item.UploadHistoryId).ToList();
                string responseMessage = anyProcessingStarted
                    ? (results.Count == 1
                        ? "File uploaded successfully. Processing started in the background."
                        : $"{results.Count} files uploaded successfully. Processing started in the background.")
                    : allOk
                    ? (results.Count == 1
                        ? "File uploaded and processed successfully."
                        : $"{results.Count} files uploaded and processed successfully.")
                    : (failedFiles.Count == 1
                        ? $"Upload failed for {failedFiles[0]}."
                        : $"Upload completed with failures for: {string.Join(", ", failedFiles)}");

                int? createdSessionId = null;
                if (UploadFileType == 1 && createdUploadIds.Count == 1)
                {
                    // Convert the int id to string to match the varchar column in tbl_session
                    string uploadIdStr = createdUploadIds[0].ToString();

                    createdSessionId = await db.tbl_session
                        .AsNoTracking()
                        .Where(s => s.tbl_upload_id == uploadIdStr)
                        .OrderByDescending(s => s.id)
                        .Select(s => (int?)s.id)
                    .FirstOrDefaultAsync();
                }

                return Json(new
                {
                    Status = anyProcessingStarted ? 2 : (allOk ? 1 : 0),
                    Message = responseMessage,
                    UploadId = createdUploadIds.Count == 1 ? createdUploadIds[0] : (int?)null,
                    UploadIds = createdUploadIds,
                    SessionId = createdSessionId,
                    Failures = failures
                });
            }
            catch (Exception ex)
            {
                return Json(new ReturnAPIResponse
                {
                    Status = 0,
                    Message = SafeException.GetInnermost(ex)
                });
            }
        }

        // GET: /ExcelUpload/GetSessions
        [HttpGet("GetSessions")]
        public async Task<IActionResult> GetSessions(
            DateTime fromDate, 
            DateTime toDate,
            [FromQuery] int? company_id = null) // Added parameter
        {
            var message = new ReturnAPIResponse();
            try
            {
                cf.SessionCheck();

                // =========================================================
                // 1. SMART SECURITY: RESOLVE COMPANY ID
                // =========================================================
                int targetCompanyId = GetTargetCompanyId(company_id);

                if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User))
                {
                     return Unauthorized(new { Status = 0, Message = "Unauthorized." });
                }

                // =========================================================
                // 2. FILTERED QUERY
                // =========================================================
                var query = 
                    from s in db.tbl_session.AsNoTracking()
                    join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                    where s.start_time >= fromDate && s.end_time <= toDate
                    // SECURITY FILTER
                    && (targetCompanyId == 0 || u.company_id == targetCompanyId)
                    select new
                    {
                        s.id,
                        s.start_time,
                        s.notes,
                        s.start_address,
                        userName = u.name
                    };

                var rawSessions = await query.ToListAsync();

                var formattedSessions = rawSessions.Select(x => new
                {
                    id = x.id,
                    label = $"{x.userName} {(x.start_time == null ? "" : x.start_time.Value.ToString("dd MMM yyyy hh:mm tt"))} {x.notes} {x.start_address}"
                }).ToList();

                message.Status = 1;
                message.Data = formattedSessions;
            }
            catch (Exception ex)
            {
                message.Message = DisplayMessage.ErrorMessage + " " + SafeException.Get(ex);
            }
            return Json(message);
        }

        // GET: /ExcelUpload/Test
        [HttpGet("Test")]
        public IActionResult Test()
        {
            return Ok(new 
            { 
                message = "ExcelUpload controller is working!",
                timestamp = DateTime.Now,
                environment = _env.EnvironmentName
            });
        }

        // ----------------- Helpers -----------------

        private int GetTargetCompanyId(int? company_id)
        {
            return _userScope.GetTargetCompanyId(User, company_id);
        }

        private static bool IsCsvOrZip(IFormFile f)
        {
            if (f == null) return false;
            var ext = Path.GetExtension(f.FileName)?.ToLowerInvariant();
            if (ext == ".csv" || ext == ".zip") return true;
            return false; 
        }

        private static bool IsCsvZipOrGeoJson(IFormFile f)
        {
            if (f == null) return false;
            var ext = Path.GetExtension(f.FileName)?.ToLowerInvariant();
            if (ext == ".csv" || ext == ".zip" || ext == ".geojson" || ext == ".json") return true;
            return false;
        }
    }
}
































































// using SignalTracker.Helper;
// using SignalTracker.Models;
// using Microsoft.AspNetCore.Authorization;
// using Microsoft.AspNetCore.Http;
// using Microsoft.AspNetCore.Mvc;
// using Microsoft.EntityFrameworkCore;
// using Microsoft.AspNetCore.Hosting;
// using System;
// using System.IO;
// using System.Linq;
// using System.Threading;
// using System.Threading.Tasks;

// namespace SignalTracker.Controllers
// {
//     [Authorize]
//     [Route("ExcelUpload")]  // ✅ Added base route
//     public class ExcelUploadController : BaseController
//     {
//         private readonly ApplicationDbContext db;
//         private readonly CommonFunction cf;
//         private readonly IWebHostEnvironment _env;

//         // Robust timezone: Windows ("India Standard Time") and Linux ("Asia/Kolkata")
//         private static readonly TimeZoneInfo INDIAN_ZONE = GetIndianZone();
//         private static TimeZoneInfo GetIndianZone()
//         {
//             try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
//             catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
//         }

//         public ExcelUploadController(
//             ApplicationDbContext context,
//             IHttpContextAccessor httpContextAccessor,
//             IWebHostEnvironment env)
//         {
//             db = context;
//             cf = new CommonFunction(context, httpContextAccessor);
//             _env = env;
//         }

//         // GET: /ExcelUpload/Index
//         [HttpGet("Index")]
//         public ActionResult Index()
//         {
//             if (!IsAngularRequest() || !cf.SessionCheck())
//                 return RedirectToAction("Index", "Home");
//             return View();
//         }

//         // GET: /ExcelUpload/DownloadExcel?fileType=1&fileName=test.csv
//         [HttpGet("DownloadExcel")]
//         [AllowAnonymous]
//         public IActionResult DownloadExcel(int fileType, string? fileName)
//         {
//             var root = _env.ContentRootPath;

//             string filePath;
//             string downloadName;

//             if (fileType == 0)
//             {
//                 if (string.IsNullOrWhiteSpace(fileName))
//                     return Json(new { status = 0, message = "fileName is required for fileType=0" });

//                 var uploadsDir = Path.Combine(root, "UploadedExcels");
//                 if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

//                 filePath = Path.Combine(uploadsDir, fileName);
//                 downloadName = Path.GetFileName(fileName);
//             }
//             else
//             {
//                 string? templateName = null;
//                 if (fileType >= 0 && Constant.TempFiles != null && fileType < Constant.TempFiles.Length)
//                     templateName = Constant.TempFiles[fileType];

//                 if (string.IsNullOrWhiteSpace(templateName))
//                     return Json(new { status = 0, message = "Unknown template type." });

//                 var templatesDir = Path.Combine(root, "Template-Files");
//                 filePath = Path.Combine(templatesDir, templateName);
//                 downloadName = templateName;
//             }

//             if (!System.IO.File.Exists(filePath))
//                 return Json(new { status = 0, message = "Template not found" });

//             var bytes = System.IO.File.ReadAllBytes(filePath);
//             var contentType = CommonFunction.GetMimeType(filePath);
//             return File(bytes, contentType, downloadName);
//         }

//         // GET: /ExcelUpload/GetUploadedExcelFiles?fileType=1
//         [HttpGet("GetUploadedExcelFiles")]
//         [AllowAnonymous]
//         public async Task<IActionResult> GetUploadedExcelFiles(int fileType, CancellationToken ct = default)
//         {
//             try
//             {
//                 Console.WriteLine($"📥 GetUploadedExcelFiles called - fileType: {fileType}");

//                 var currentUserId = cf.UserId;
//                 bool filterByUser = currentUserId > 0;

//                 var query =
//                     from h in db.tbl_upload_history.AsNoTracking()
//                     join u in db.tbl_user.AsNoTracking() on h.uploaded_by equals u.id into gu
//                     from u in gu.DefaultIfEmpty()
//                     where h.file_type == fileType
//                     select new
//                     {
//                         id = h.id,
//                         file_type = h.file_type,
//                         file_name = h.file_name,
//                         uploaded_on = h.uploaded_on,
//                         uploaded_by = u != null ? u.name : null,
//                         uploaded_id = h.uploaded_by,
//                         status = h.status == 1 ? "Success" : "Failed",
//                         remarks = h.remarks
//                     };

//                 if (filterByUser)
//                     query = query.Where(x => x.uploaded_id == currentUserId);

//                 var data = await query
//                     .OrderByDescending(x => x.id)
//                     .Take(20)
//                     .ToListAsync(ct);

//                 Console.WriteLine($"✅ Returning {data.Count} files");

//                 return Ok(new { Status = 1, Data = data });
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"❌ Error in GetUploadedExcelFiles: {SafeException.Get(ex)}");
//                 return StatusCode(500, new { Status = 0, Message = "Server error: " + SafeException.Get(ex) });
//             }
//         }

//         // POST: /ExcelUpload/UploadExcelFile
//         [HttpPost("UploadExcelFile")]
//         [RequestSizeLimit(100_000_000)]
//         public async Task<IActionResult> UploadExcelFile(
//             [FromForm] string remarks,
//             [FromForm] string token,
//             [FromForm] string ip,
//             [FromForm] string ProjectName,
//             [FromForm] string SessionIds,
//             [FromForm] int UploadFileType,
//             [FromForm] IFormFile UploadFile,
//             [FromForm] IFormFile UploadNoteFile
//         )
//         {
//             var message = new ReturnAPIResponse();

//             try
//             {
//                 cf.SessionCheck();

//                 message.Status = 1;

//                 if (UploadFile == null || UploadFile.Length == 0)
//                     return Json(new { Status = 0, Message = "Please select excel file." });

//                 // -------- SAVE FILES --------
//                 var root = _env.ContentRootPath;
//                 var uploadsDir = Path.Combine(root, "UploadedExcels");
//                 Directory.CreateDirectory(uploadsDir);

//                 DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, INDIAN_ZONE);

//                 string mainExt = Path.GetExtension(UploadFile.FileName);
//                 string savedMainName = "File_" + nowIst.ToString("MMddyyyyHmmss") + mainExt;
//                 string mainPath = Path.Combine(uploadsDir, savedMainName);

//                 using (var stream = System.IO.File.Create(mainPath))
//                     await UploadFile.CopyToAsync(stream);

//                 // Polygon file if exists
//                 string polygonPath = "";
//                 string polygonFile = "";

//                 if (UploadNoteFile != null && UploadNoteFile.Length > 0)
//                 {
//                     polygonFile = "Polygon_" + nowIst.ToString("MMddyyyyHmmss") + Path.GetExtension(UploadNoteFile.FileName);
//                     polygonPath = Path.Combine(uploadsDir, polygonFile);

//                     using (var stream = System.IO.File.Create(polygonPath))
//                         await UploadNoteFile.CopyToAsync(stream);
//                 }

//                 // Save DB history
//                 int userId = Convert.ToInt32(HttpContext.Session.GetInt32("UserID"));

//                 var excel_details = new tbl_upload_history
//                 {
//                     remarks = remarks,
//                     file_name = savedMainName,
//                     polygon_file = polygonFile,
//                     file_type = UploadFileType,
//                     status = 1,
//                     uploaded_by = userId,
//                     uploaded_on = nowIst
//                 };

//                 db.tbl_upload_history.Add(excel_details);
//                 db.SaveChanges();

//                 // ------ RETURN SUCCESS IMMEDIATELY ------
//                 var response = Json(new
//                 {
//                     Status = 1,
//                     Message = "File uploaded successfully. Processing started..."
//                 });

//                 // -------- BACKGROUND PROCESSING --------
//                 _ = Task.Run(() =>
//                 {
//                     try
//                     {
//                         string errorMsg = "";
//                         int projectId = 0;

//                         // Project create if type = 2
//                         if (UploadFileType == 2)
//                         {
//                             var objProject = new tbl_project
//                             {
//                                 project_name = ProjectName,
//                                 ref_session_id = SessionIds,
//                                 created_by_user_id = userId,
//                                 created_by_user_name = cf.UserName,
//                                 status = 1
//                             };

//                             db.tbl_project.Add(objProject);
//                             db.SaveChanges();
//                             projectId = objProject.id;
//                         }

//                         var csvProc = new ProcessCSVController(db, cf);

//                         bool ok = csvProc.Process(
//                             excel_details.id,
//                             mainPath,
//                             UploadFile.FileName,
//                             polygonPath,
//                             UploadFileType,
//                             projectId,
//                             remarks,
//                             out errorMsg
//                         );

//                         excel_details.status = (short)(ok ? 1 : 0);
//                         excel_details.errors = errorMsg;

//                         db.SaveChanges();
//                     }
//                     catch (Exception ex)
//                     {
//                         new Writelog(db).write_exception_log(0, "ExcelUpload", "BG Process", DateTime.Now, ex);
//                     }
//                 });

//                 return response;
//             }
//             catch (Exception ex)
//             {
//                 return Json(new ReturnAPIResponse
//                 {
//                     Status = 0,
//                     Message = SafeException.GetInnermost(ex)
//                 });
//             }
//         }

//         // GET: /ExcelUpload/GetSessions?fromDate=2024-01-01&toDate=2024-12-31
//         [HttpGet("GetSessions")]
//         public JsonResult GetSessions(DateTime fromDate, DateTime toDate)
//         {
//             var message = new ReturnAPIResponse();
//             try
//             {
//                 cf.SessionCheck();

//                 var rawSessions = db.tbl_session
//                     .Where(s => s.start_time >= fromDate && s.end_time <= toDate)
//                     .Join(db.tbl_user,
//                           s => s.user_id,
//                           u => u.id,
//                           (s, u) => new
//                           {
//                               s.id,
//                               s.start_time,
//                               s.notes,
//                               s.start_address,
//                               userName = u.name
//                           })
//                     .ToList();

//                 var formattedSessions = rawSessions.Select(x => new
//                 {
//                     id = x.id,
//                     label = $"{x.userName} {(x.start_time == null ? "" : x.start_time.Value.ToString("dd MMM yyyy hh:mm tt"))} {x.notes} {x.start_address}"
//                 }).ToList();

//                 message.Status = 1;
//                 message.Data = formattedSessions;
//             }
//             catch (Exception ex)
//             {
//                 message.Message = DisplayMessage.ErrorMessage + " " + SafeException.Get(ex);
//             }
//             return Json(message);
//         }

//         // GET: /ExcelUpload/Test (for debugging)
//         [HttpGet("Test")]
//         [AllowAnonymous]
//         public IActionResult Test()
//         {
//             return Ok(new 
//             { 
//                 message = "ExcelUpload controller is working!",
//                 timestamp = DateTime.Now,
//                 environment = _env.EnvironmentName,
//                 contentRoot = _env.ContentRootPath
//             });
//         }

//         // ----------------- Helpers -----------------

//         private static bool IsCsvOrZip(IFormFile f)
//         {
//             if (f == null) return false;
//             var ext = Path.GetExtension(f.FileName)?.ToLowerInvariant();
//             if (ext == ".csv" || ext == ".zip") return true;

//             var ct = f.ContentType?.ToLowerInvariant();
//             return ct == "text/csv"
//                 || ct == "application/vnd.ms-excel"
//                 || ct == "application/zip"
//                 || ct == "application/x-zip-compressed"
//                 || ct == "application/octet-stream";
//         }

//         private static bool IsCsvZipOrGeoJson(IFormFile f)
//         {
//             if (f == null) return false;
//             var ext = Path.GetExtension(f.FileName)?.ToLowerInvariant();
//             if (ext == ".csv" || ext == ".zip" || ext == ".geojson" || ext == ".json") return true;

//             var ct = f.ContentType?.ToLowerInvariant();
//             return ct == "application/geo+json"
//                 || ct == "application/json"
//                 || ct == "text/csv"
//                 || ct == "application/vnd.ms-excel"
//                 || ct == "application/zip"
//                 || ct == "application/x-zip-compressed"
//                 || ct == "application/octet-stream";
//         }
//     }
// }
