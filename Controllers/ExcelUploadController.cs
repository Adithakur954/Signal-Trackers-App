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
        private const int NetworkLogIngestLockTimeoutSeconds = 90;

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

        private static string BuildCompactUploadFileName(string prefix, DateTime timestamp, string originalFileName)
        {
            var extension = Path.GetExtension(originalFileName);
            var stamp = timestamp.ToString("yyMMddHHmmssfff");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"{prefix}{stamp}_{suffix}{extension}";
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

            string savedMainName = BuildCompactUploadFileName("U_", nowIst, uploadFile.FileName);
            string mainPath = Path.Combine(uploadsDir, savedMainName);

            using (var stream = System.IO.File.Create(mainPath))
            {
                await uploadFile.CopyToAsync(stream);
            }

            var excelDetails = new tbl_upload_history
            {
                remarks = remarks,
                file_name = savedMainName,
                polygon_file = polygonFile,
                file_type = uploadFileType,
                status = 2,
                uploaded_by = userId,
                uploaded_on = nowIst
            };

            db.tbl_upload_history.Add(excelDetails);
            await db.SaveChangesAsync();

            if (uploadFileType == 1)
            {
                _ = Task.Run(() => ProcessNetworkLogUploadInBackgroundAsync(
                    excelDetails.id,
                    mainPath,
                    uploadFile.FileName,
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
                uploadFile.FileName,
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
            }
            catch (Exception ex)
            {
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
        // holding the lock past the timeout, uploading fails fast with a clear
        // message instead of racing into a long, confusing "Lock wait timeout".
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
            await using var lockConnection = new MySqlConnection(connectionString);
            await lockConnection.OpenAsync();

            bool lockAcquired;
            await using (var acquireCmd = lockConnection.CreateCommand())
            {
                acquireCmd.CommandText = "SELECT GET_LOCK(@lockName, @timeoutSeconds)";
                acquireCmd.Parameters.AddWithValue("@lockName", NetworkLogIngestLockName);
                acquireCmd.Parameters.AddWithValue("@timeoutSeconds", NetworkLogIngestLockTimeoutSeconds);
                var result = await acquireCmd.ExecuteScalarAsync();
                lockAcquired = result is long l && l == 1;
            }

            if (!lockAcquired)
            {
                return (false,
                    "Another network-log upload is currently being processed on the server. Please try again in a minute.");
            }

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
                    // Best-effort release; closing the connection below also releases
                    // the lock server-side if this fails for any reason.
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
        [AllowAnonymous]
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

    select new
    {
        id = h.id,
        file_type = h.file_type,
        // ✅ Use s.id since session_id column is missing in your DB schema
        session_id = s != null ? s.id.ToString() : null,
        file_name = h.file_name,
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

                return Ok(new { Status = 1, Data = data });
            }
            catch (Exception)
            {
                Console.WriteLine("❌ Error in GetUploadedExcelFiles: operation failed (see server logs)");
                return StatusCode(500, new { Status = 0, Message = "An internal server error occurred." });
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
            [FromForm] List<IFormFile> UploadFile,
            [FromForm] IFormFile UploadNoteFile
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
