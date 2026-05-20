using SignalTracker.Helper;
using SignalTracker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using SignalTracker.Services; // Required for UserScopeService

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

        // Robust timezone: Windows ("India Standard Time") and Linux ("Asia/Kolkata")
        private static readonly TimeZoneInfo INDIAN_ZONE = GetIndianZone();
        private static TimeZoneInfo GetIndianZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        }

        public ExcelUploadController(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            IWebHostEnvironment env,
            UserScopeService userScope,
            RedisService redis) // 2. Inject Service
        {
            db = context;
            cf = new CommonFunction(context, httpContextAccessor);
            _env = env;
            _userScope = userScope;
            _redis = redis;
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
        public IActionResult DownloadPythonRuntime()
        {
            return DownloadExcel(4, null);
        }

        // GET: /ExcelUpload/GetUploadedExcelFiles
        [HttpGet("GetUploadedExcelFiles")]
        [AllowAnonymous]
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
        remarks = h.remarks
    };
                var data = await query
                    .OrderByDescending(x => x.id)
                    .Take(20)
                    .ToListAsync(ct);

                return Ok(new { Status = 1, Data = data });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in GetUploadedExcelFiles: {ex.Message}");
                return StatusCode(500, new { Status = 0, Message = "Server error: " + ex.Message });
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
            [FromForm] IFormFile UploadFile,
            [FromForm] IFormFile UploadNoteFile
        )
        {
            tbl_upload_history? excel_details = null;
            try
            {
                cf.SessionCheck();

                if (UploadFile == null || UploadFile.Length == 0)
                    return Json(new { Status = 0, Message = "Please select excel file." });

                const long maxUploadBytes = 524_288_000;
                if (UploadFile.Length > maxUploadBytes)
                    return Json(new { Status = 0, Message = "Upload file size must be less than 500 MB." });

                if (UploadNoteFile != null && UploadNoteFile.Length > maxUploadBytes)
                    return Json(new { Status = 0, Message = "Polygon file size must be less than 500 MB." });

                // -------- SAVE FILES --------
                var root = _env.ContentRootPath;
                var uploadsDir = Path.Combine(root, "UploadedExcels");
                Directory.CreateDirectory(uploadsDir);

                DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, INDIAN_ZONE);

                string mainExt = Path.GetExtension(UploadFile.FileName);
                string savedMainName = "File_" + nowIst.ToString("MMddyyyyHmmss") + mainExt;
                string mainPath = Path.Combine(uploadsDir, savedMainName);

                using (var stream = System.IO.File.Create(mainPath))
                    await UploadFile.CopyToAsync(stream);

                // Polygon file if exists
                string polygonPath = "";
                string polygonFile = "";

                if (UploadNoteFile != null && UploadNoteFile.Length > 0)
                {
                    polygonFile = "Polygon_" + nowIst.ToString("MMddyyyyHmmss") + Path.GetExtension(UploadNoteFile.FileName);
                    polygonPath = Path.Combine(uploadsDir, polygonFile);

                    using (var stream = System.IO.File.Create(polygonPath))
                        await UploadNoteFile.CopyToAsync(stream);
                }

                // Save DB history
                // Note: Upload is always attributed to the current logged-in user
                int userId = cf.UserId > 0 ? cf.UserId : Convert.ToInt32(HttpContext.Session.GetInt32("UserID"));
                string userName = cf.UserName ?? "Unknown";

                excel_details = new tbl_upload_history
                {
                    remarks = remarks,
                    file_name = savedMainName,
                    polygon_file = polygonFile,
                    file_type = UploadFileType,
                    status = 2, // Processing
                    uploaded_by = userId,
                    uploaded_on = nowIst
                };

                db.tbl_upload_history.Add(excel_details);
                db.SaveChanges();

                string errorMsg = "";
                int projectId = 0;

                // Create Project if type = 2
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

                var csvProc = new ProcessCSVController(db, cf, _redis);
                bool ok = csvProc.Process(
                    excel_details.id,
                    mainPath,
                    UploadFile.FileName,
                    polygonPath,
                    UploadFileType,
                    projectId,
                    remarks,
                    out errorMsg,
                    userId
                );

                excel_details.status = (short)(ok ? 1 : 0);
                excel_details.errors = string.IsNullOrWhiteSpace(errorMsg) ? null : errorMsg;
                db.SaveChanges();

                int? createdSessionId = null;
if (UploadFileType == 1)
{
    // Convert the int id to string to match the varchar column in tbl_session
    string uploadIdStr = excel_details.id.ToString(); 

    createdSessionId = await db.tbl_session
        .AsNoTracking()
        .Where(s => s.tbl_upload_id == uploadIdStr) // ✅ Both are now strings
        .OrderByDescending(s => s.id)
        .Select(s => (int?)s.id)
        .FirstOrDefaultAsync();
}

                return Json(new
                {
                    Status = ok ? 1 : 0,
                    Message = ok
                        ? "File uploaded and processed successfully."
                        : (string.IsNullOrWhiteSpace(errorMsg) ? "File processing failed." : errorMsg),
                    UploadId = excel_details.id,
                    SessionId = createdSessionId
                });
            }
            catch (Exception ex)
            {
                if (excel_details != null)
                {
                    try
                    {
                        excel_details.status = 0;
                        excel_details.errors = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                        db.SaveChanges();
                    }
                    catch { }
                }
                return Json(new ReturnAPIResponse
                {
                    Status = 0,
                    Message = ex.InnerException != null ? ex.InnerException.Message : ex.Message
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
                message.Message = DisplayMessage.ErrorMessage + " " + ex.Message;
            }
            return Json(message);
        }

        // GET: /ExcelUpload/Test
        [HttpGet("Test")]
        [AllowAnonymous]
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
//                 Console.WriteLine($"❌ Error in GetUploadedExcelFiles: {ex.Message}");
//                 return StatusCode(500, new { Status = 0, Message = "Server error: " + ex.Message });
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
//                     Message = ex.InnerException != null ? ex.InnerException.Message : ex.Message
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
//                 message.Message = DisplayMessage.ErrorMessage + " " + ex.Message;
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
