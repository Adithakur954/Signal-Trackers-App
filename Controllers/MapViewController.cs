using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;     // for Regex
using System.Collections.Concurrent;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Newtonsoft.Json;

using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Security;

using System.Data;          // ConnectionState, DbType, etc.
using System.Data.Common;   // DbCommand, DbConnection

using TempPoint = SignalTracker.Models.TempPlainDto;
using System.Diagnostics;
using MySqlConnector;
using System.Linq;
using SignalTracker.Services;
using Microsoft.EntityFrameworkCore.Storage;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MapViewController : BaseController
    {
        private readonly IWebHostEnvironment _env;
        private readonly ApplicationDbContext db;
        private readonly CommonFunction cf;
        private readonly RedisService _redis;
        private readonly UserScopeService _userScope;
        private const int MapViewCacheTtlSeconds = 300;
        private static volatile bool NetworkLogUpdatedAtColumnEnsured;
        private static volatile bool ObsoleteNetworkLogCachesInvalidated;
        private static readonly string[] MapViewCacheInvalidationPatterns =
        {
            "mapview:*",
            "projectpolygons:*",
            "availablepolygons:*",
            "networklog:v2:*",
            "networklog:v3:*",
            "networklog:v8:*",
            "networklog:v9:*",
            "latlon:dist:*",
            "n78_simple_kpi:*",
            "n78_neighbours:*",
            "daterangelog:*"
        };

        public MapViewController(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor, IWebHostEnvironment env, RedisService redis,UserScopeService userScope)
        {
            db = context;
            _env = env;
            cf = new CommonFunction(context, httpContextAccessor);
            _redis = redis;
            _userScope = userScope;
        }

        private string BuildMapViewCacheKey(string endpoint, params object?[] parts)
        {
            var tokens = parts.Select(NormalizeCacheKeyPart);
            return $"mapview:{NormalizeCacheKeyPart(endpoint)}:{string.Join(":", tokens)}";
        }

        private static string NormalizeCacheKeyPart(object? value)
        {
            if (value == null)
                return "null";

            if (value is DateTime dt)
                return dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

            if (value is DateTimeOffset dto)
                return dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

            if (value is bool b)
                return b ? "1" : "0";

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    items.Add(NormalizeCacheKeyPart(item));
                }

                return items.Count == 0
                    ? "empty"
                    : string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal));
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return "empty";

            return Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9_\-\.]+", "_");
        }

        private async Task<T?> TryGetMapViewCacheAsync<T>(string cacheKey) where T : class
        {
            if (_redis?.IsConnected != true)
                return null;

            try
            {
                return await _redis.GetObjectAsync<T>(cacheKey);
            }
            catch
            {
                return null;
            }
        }

        private async Task SetMapViewCacheAsync<T>(string cacheKey, T value, int ttlSeconds = MapViewCacheTtlSeconds) where T : class
        {
            if (_redis?.IsConnected != true)
                return;

            try
            {
                await _redis.SetObjectAsync(cacheKey, value, ttlSeconds);
            }
            catch
            {
                // Best effort only.
            }
        }

        private async Task InvalidateMapViewCachesAsync()
        {
            if (_redis?.IsConnected != true)
                return;

            foreach (var pattern in MapViewCacheInvalidationPatterns)
            {
                try
                {
                    await _redis.DeleteByPatternAsync(pattern);
                }
                catch
                {
                    // Best effort only.
                }
            }
        }

        private async Task InvalidateProjectListCachesAsync()
        {
            if (_redis?.IsConnected != true)
                return;

            try
            {
                await _redis.DeleteByPatternAsync("mapview:projects:*");
            }
            catch
            {
                // Best effort only.
            }
        }

        private async Task InvalidateObsoleteNetworkLogCachesAsync()
        {
            if (ObsoleteNetworkLogCachesInvalidated || _redis?.IsConnected != true)
                return;

            try
            {
                await _redis.DeleteByPatternAsync("networklog:v8:*");
                ObsoleteNetworkLogCachesInvalidated = true;
            }
            catch
            {
                // Best effort only. The v9 key still prevents stale reads.
            }
        }

        private static List<T> SliceCachedPage<T>(IReadOnlyList<T> rows, int limit, int offset)
        {
            if (rows.Count == 0)
                return new List<T>();

            var safeOffset = Math.Max(offset, 0);
            if (safeOffset >= rows.Count)
                return new List<T>();

            var safeLimit = Math.Max(limit, 1);
            return rows.Skip(safeOffset).Take(safeLimit).ToList();
        }

        private sealed class CompareSitePredictionCacheRow
        {
            public Dictionary<string, object?> baseline { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, object?> optimized { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

    
        private static class TechClassifier
        {
            private static readonly HashSet<int> LteTddBands = new HashSet<int>
            { 33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48 };
            private static readonly HashSet<int> NrCommonBands = new HashSet<int>
            { 1,3,5,7,8,20,28,38,40,41,77,78,79 };
            private static readonly HashSet<int> NrExclusiveBands = new HashSet<int>
            { 77,78,79,257,258,260,261 };

            private static readonly Regex RxNumber = new Regex(@"(?<![A-Za-z])(\d{1,3})(?![A-Za-z])", RegexOptions.Compiled);
            private static readonly Regex RxNR = new Regex(@"\bn\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex RxLTE = new Regex(@"\b(LTE|4G|B\d{1,3}|Band\s*\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex RxENDC = new Regex(@"\b(EN-?DC|ENDC)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex RxSA = new Regex(@"\bSA\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex RxNSA = new Regex(@"\bNSA\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex Rx5GHint = new Regex(@"\b(5G|NR|NRARFCN|NSA|EN-?DC|ENDC|N\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            public sealed class Result
            {
                public string Generation { get; set; } = "Unknown"; // 2G/3G/4G/5G/Unknown
                public string Radio { get; set; } = "Unknown";      // GSM/WCDMA/LTE/NR/Unknown
                public string Mode { get; set; } = "Unknown";       // FDD/TDD/SA/NSA/Unknown
                public bool Is5G => string.Equals(Generation, "5G", StringComparison.OrdinalIgnoreCase);
                public bool Is4G => string.Equals(Generation, "4G", StringComparison.OrdinalIgnoreCase);
                public bool IsNsa => string.Equals(Mode, "NSA", StringComparison.OrdinalIgnoreCase);
            }

            public static (bool isNr, int? bandNum) ParseBand(string? bandRaw)
            {
                if (string.IsNullOrWhiteSpace(bandRaw)) return (false, null);
                var s = bandRaw.Trim();

                var mNR = RxNR.Match(s);
                if (mNR.Success)
                {
                    if (int.TryParse(mNR.Value.Trim().TrimStart('n','N'), out var n))
                        return (true, n);
                }

                if (RxLTE.IsMatch(s))
                {
                    var m = RxNumber.Match(s);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var b))
                        return (false, b);
                }

                if (int.TryParse(s, out var justNum))
                    return (false, justNum);

                return (false, null);
            }

            public static Result Classify(string? bandRaw, string? network, string? primaryCellInfo, string? neighborsInfo)
            {
                var (isNr, bandNum) = ParseBand(bandRaw);
                network ??= string.Empty;
                primaryCellInfo ??= string.Empty;
                neighborsInfo ??= string.Empty;
                bool hasLteHint = network.Contains("LTE", StringComparison.OrdinalIgnoreCase)
                    || network.Contains("4G", StringComparison.OrdinalIgnoreCase);
                bool looksLikeNrNumericBand = !isNr
                    && !hasLteHint
                    && !string.IsNullOrWhiteSpace(bandRaw)
                    && int.TryParse(bandRaw.Trim(), out var bandAsNum)
                    && NrCommonBands.Contains(bandAsNum);
                bool isNrExclusiveBand = bandNum.HasValue && NrExclusiveBands.Contains(bandNum.Value);

                bool has5GHint =
                    isNr ||
                    isNrExclusiveBand ||
                    looksLikeNrNumericBand ||
                    network.Contains("5G", StringComparison.OrdinalIgnoreCase) ||
                    network.Contains("NR", StringComparison.OrdinalIgnoreCase) ||
                    network.Contains("NSA", StringComparison.OrdinalIgnoreCase) ||
                    network.Contains("SA", StringComparison.OrdinalIgnoreCase) ||
                    network.Contains("ENDC", StringComparison.OrdinalIgnoreCase) ||
                    Rx5GHint.IsMatch(primaryCellInfo) ||
                    Rx5GHint.IsMatch(neighborsInfo);

                // 5G?
                if (has5GHint)
                {
                    bool hintNSA =
                        RxNSA.IsMatch(network) ||
                        RxNSA.IsMatch(primaryCellInfo) || RxNSA.IsMatch(neighborsInfo) ||
                        RxENDC.IsMatch(primaryCellInfo) || RxENDC.IsMatch(neighborsInfo) ||
                        primaryCellInfo.Contains("LTE", StringComparison.OrdinalIgnoreCase) ||
                        neighborsInfo.Contains("LTE", StringComparison.OrdinalIgnoreCase);

                    return new Result
                    {
                        Generation = "5G",
                        Radio = "NR",
                        Mode = hintNSA ? "NSA" : (RxSA.IsMatch(network) ? "SA" : "SA") // default SA
                    };
                }

                // 4G/LTE?
                if (RxLTE.IsMatch(bandRaw ?? string.Empty)
                    || network.Contains("LTE", StringComparison.OrdinalIgnoreCase)
                    || network.Contains("4G", StringComparison.OrdinalIgnoreCase))
                {
                    var mode = (bandNum.HasValue && LteTddBands.Contains(bandNum.Value)) ? "TDD" : "FDD";
                    return new Result { Generation = "4G", Radio = "LTE", Mode = mode };
                }

                // 3G?
                if (network.Contains("WCDMA", StringComparison.OrdinalIgnoreCase)
                 || network.Contains("HSPA", StringComparison.OrdinalIgnoreCase)
                 || network.Contains("UMTS", StringComparison.OrdinalIgnoreCase)
                 || network.Contains("3G", StringComparison.OrdinalIgnoreCase))
                    return new Result { Generation = "3G", Radio = "WCDMA", Mode = "Unknown" };

                // 2G?
                if (network.Contains("GSM", StringComparison.OrdinalIgnoreCase)
                 || network.Contains("EDGE", StringComparison.OrdinalIgnoreCase)
                 || network.Contains("2G", StringComparison.OrdinalIgnoreCase))
                    return new Result { Generation = "2G", Radio = "GSM", Mode = "Unknown" };

                return new Result();
            }

            internal static object Classify(string? band, string? network, string? primary_cell_info_1)
            {
                throw new NotImplementedException();
            }
        }

        

        public class UserModel
        {
            public string name { get; set; } = string.Empty;
                public string mobile { get; set; } = string.Empty;
                public string make { get; set; } = string.Empty;
                public string model { get; set; } = string.Empty;
                public string os { get; set; } = string.Empty;
            public string operator_name { get; set; } = string.Empty;
            public string? device_id { get; set; }
            public string? gcm_id { get; set; }
            public int? company_id { get; set; }
        }

        [HttpPost, AllowAnonymous, PublicApiKey, Route("user_signup")]
        public async Task<JsonResult> user_signup([FromBody] UserModel model)
        {
            var message = new ReturnAPIResponse();
            try
            {
                if (model == null)
                {
                    message.Status = 0; message.Message = "Invalid request.";
                    return Json(message);
                }

                if (!string.IsNullOrEmpty(model.device_id))
                {
                    var existingByDevice = await db.tbl_user.AsNoTracking()
                        .FirstOrDefaultAsync(u => u.device_id == model.device_id);
                    if (existingByDevice != null)
                    {
                        message.Status = 1;
                        message.Message = "This device is already registered as - " + existingByDevice.name;
                        message.Data = new { userid = existingByDevice.id };
                        return Json(message);
                    }
                }

                var existingUser = await db.tbl_user.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.mobile == model.mobile && u.make == model.make);
                if (existingUser != null)
                {
                    message.Status = 1;
                    message.Message = "User already exists.";
                    message.Data = new { userid = existingUser.id };
                    return Json(message);
                }

                var newUser = new tbl_user
                {
                    name = model.name,
                    mobile = model.mobile,
                    make = model.make,
                    model = model.model,
                    os = model.os,
                    operator_name = model.operator_name,
                    device_id = model.device_id,
                    gcm_id = model.gcm_id,
                    company_id = model.company_id
                };

                db.tbl_user.Add(newUser);
                await db.SaveChangesAsync();

                message.Status = 1;
                message.Message = "User saved successfully.";
                message.Data = new { userid = newUser.id };
            }
            catch (Exception ex)
            {
                message.Status = 0; message.Message = "Error: " + SafeException.Get(ex);
            }
            return Json(message);
        }

        public class SessionStartModel
        {
            public int userid { get; set; }
            public string start_time { get; set; } = string.Empty;
            public string type { get; set; } = "Drive";
            public string? notes { get; set; }
        }

        [HttpPost, AllowAnonymous, PublicApiKey, Route("start_session")]
        public async Task<JsonResult> start_session([FromBody] SessionStartModel model)
        {
            var message = new ReturnAPIResponse();
            try
            {
                var ci = CultureInfo.InvariantCulture;
                var newSess = new tbl_session
                {
                    user_id = model.userid,
                    start_time = DateTime.TryParse(model.start_time, ci, DateTimeStyles.RoundtripKind, out var ts) ? ts : (DateTime?)null,
                    type = model.type,
                    notes = model.notes
                };
                db.tbl_session.Add(newSess);
                await db.SaveChangesAsync();
                await InvalidateMapViewCachesAsync();

                message.Status = 1; message.Message = "Session Started.";
                message.Data = new { sessionid = newSess.id };
            }
            catch (Exception ex) { message.Status = 0; message.Message = "Error: " + SafeException.Get(ex); }
            return Json(message);
        }

        public class SessionEndModel
        {
            public int sessionid { get; set; }
            public string end_time { get; set; } = string.Empty;
            public string start_lat { get; set; } = "0";
            public string start_lon { get; set; } = "0";
            public string end_lat { get; set; } = "0";
            public string end_lon { get; set; } = "0";
            public float distance { get; set; }
            public int capture_frequency { get; set; }
            public string? start_address { get; set; }
            public string? end_address { get; set; }
        }

        [HttpPost, AllowAnonymous, PublicApiKey, Route("end_session")]
        public async Task<JsonResult> end_session([FromBody] SessionEndModel model)
        {
            var message = new ReturnAPIResponse();
            try
            {
                var existingSession = await db.tbl_session.FirstOrDefaultAsync(u => u.id == model.sessionid);
                if (existingSession == null)
                {
                    message.Status = 0; message.Message = "Session not found."; return Json(message);
                }

                var ci = CultureInfo.InvariantCulture;
                existingSession.start_lat = float.TryParse(model.start_lat, NumberStyles.Float, ci, out var latVal) ? latVal : (float?)null;
                existingSession.start_lon = float.TryParse(model.start_lon, NumberStyles.Float, ci, out var lonVal) ? lonVal : (float?)null;
                existingSession.end_lat = float.TryParse(model.end_lat, NumberStyles.Float, ci, out var latVal1) ? latVal1 : (float?)null;
                existingSession.end_lon = float.TryParse(model.end_lon, NumberStyles.Float, ci, out var lonVal1) ? lonVal1 : (float?)null;
                existingSession.end_time = DateTime.TryParse(model.end_time, ci, DateTimeStyles.RoundtripKind, out var ts) ? ts : (DateTime?)null;
                existingSession.start_address = model.start_address;
                existingSession.end_address = model.end_address;
                existingSession.capture_frequency = model.capture_frequency;
                existingSession.distance = model.distance;

                await db.SaveChangesAsync();
                await InvalidateMapViewCachesAsync();
                message.Status = 1; message.Message = "Session Ended.";
            }
            catch (Exception ex) { message.Status = 0; message.Message = "Error: " + SafeException.Get(ex); }
            return Json(message);
        }

        // =========================================================
        // ================== Polygons: list/save ==================
        // =========================================================

        [HttpGet, Route("GetProjectPolygons")]
public async Task<IActionResult> GetProjectPolygons(
    [FromQuery] int projectId,
    [FromQuery] int? company_id = null) // <--- Added Parameter
{
    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
    
    // =========================================================
    // 1. SMART SECURITY: RESOLVE COMPANY ID
    // =========================================================
    bool isSuperAdmin = _userScope.IsSuperAdmin(User);
    int targetCompanyId = GetTargetCompanyId(company_id);

    if (!isSuperAdmin && targetCompanyId == 0)
        return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve Company Context." });

    try
    {
        // =========================================================
        // 2. VALIDATE PROJECT OWNERSHIP (SECURITY CHECK)
        // =========================================================
        // If regular admin, we MUST verify this project belongs to their company.
        if (targetCompanyId > 0)
        {
            // Assuming a table 'tbl_project' exists with 'company_id'
            // We use raw SQL for speed and to avoid Entity Framework setup issues if the model isn't ready.
            bool hasAccess = await CheckProjectAccessAsync(projectId, targetCompanyId);
            
            if (!hasAccess)
            {
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Project does not belong to your company." });
            }
        }

        // ========================================
        //  BUILD CACHE KEY
        // ========================================
        string cacheKey = $"projectpolygons:{projectId}";

        // ========================================
        //  TRY GET FROM CACHE
        // ========================================
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var cacheStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var cached = await _redis.GetObjectAsync<ProjectPolygonsResponse>(cacheKey);
                cacheStopwatch.Stop();

                if (cached != null)
                {
                    totalStopwatch.Stop();
                    Response.Headers["X-Cache"] = "HIT";
                    Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
                    return Ok(cached.data);
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Redis error: operation failed (see server logs)");
            }
        }

        // ========================================
        //  FETCH FROM DATABASE
        // ========================================
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<ProjectPolygonItem>();
        
        var conn = db.Database.GetDbConnection();
        bool shouldClose = false;
        
        try
        {
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
                shouldClose = true;
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, name, ST_AsText(region) AS wkt, area
                FROM map_regions
                WHERE status = 1
                  AND tbl_project_id = @pid;";
            
            AddParam(cmd, "@pid", projectId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                rows.Add(new ProjectPolygonItem
                {
                    id = r.GetFieldValue<int>(0),
                    name = r.IsDBNull(1) ? null : r.GetString(1),
                    wkt = r.IsDBNull(2) ? null : r.GetString(2),
                    area = r.IsDBNull(3) ? (double?)null : Convert.ToDouble(r.GetValue(3))
                });
            }
        }
        finally
        {
            if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                await conn.CloseAsync();
        }

        dbStopwatch.Stop();

        // ========================================
        //  BUILD RESPONSE & CACHE IT
        // ========================================
        var response = new ProjectPolygonsResponse
        {
            data = rows
        };

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                // Cache for 10 minutes
                await _redis.SetObjectAsync(cacheKey, response, ttlSeconds: 600);
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to cache: operation failed (see server logs)");
            }
        }

        totalStopwatch.Stop();
        
        Response.Headers["X-Cache"] = "MISS";
        Response.Headers["X-Row-Count"] = rows.Count.ToString();
        Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();

        return Ok(rows);
    }
    catch (Exception ex)
    {
        totalStopwatch.Stop();
        Console.WriteLine("Stack trace available in server logs.");
        return StatusCode(500, new { status = 0, message = "An internal server error occurred." });
    }
}

// =========================================================
// HELPERS & DTOs (Place these at the bottom of the class)
// =========================================================

private async Task<bool> CheckProjectAccessAsync(int projectId, int companyId)
{
    var conn = db.Database.GetDbConnection();
    bool shouldClose = false;
    try
    {
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); shouldClose = true; }
        await using var cmd = conn.CreateCommand();
        
        // CHECK: Does this project exist AND belong to the company?
        cmd.CommandText = "SELECT COUNT(1) FROM tbl_project WHERE id = @pid AND company_id = @cid";
        AddParam(cmd, "@pid", projectId);
        AddParam(cmd, "@cid", companyId);

        var result = await cmd.ExecuteScalarAsync();
        return (result != null && Convert.ToInt32(result) > 0);
    }
    catch
    {
        // If tbl_project doesn't exist or error, assume strict fail
        return false;
    }
    finally
    {
        if (shouldClose && conn.State == System.Data.ConnectionState.Open) await conn.CloseAsync();
    }
}



public class ProjectPolygonsResponse
{
    public List<ProjectPolygonItem> data { get; set; } = new();
}

public class ProjectPolygonItem
{
    public int id { get; set; }
    public string? name { get; set; }
    public string? wkt { get; set; }
    public double? area { get; set; }
}
// ========================================
//  RESPONSE DTO FOR CACHING
// ========================================


        // --------- DTOs for saving polygons ---------
        public class SavePolygonRequest
        {
            public int? ProjectId { get; set; }
            public string Name { get; set; } = default!;
            public string? Wkt { get; set; }
            public string? GeoJson { get; set; }
            public List<List<double>>? Coordinates { get; set; } // [[lon,lat], ...]
            public List<int> LogIds { get; set; } = new List<int>();
            public double? Area { get; set; }
        }
        public class GeoJson
        {
            [JsonProperty("type")] public string Type { get; set; } = string.Empty;
            [JsonProperty("features")] public List<Feature> Features { get; set; } = new();
        }
        public class Feature
        {
            [JsonProperty("type")] public string Type { get; set; } = string.Empty;
            [JsonProperty("geometry")] public Geometry Geometry { get; set; } = new();
            [JsonProperty("properties")] public Dictionary<string, object> Properties { get; set; } = new();
        }
        public class Geometry
        {
            [JsonProperty("type")] public string Type { get; set; } = string.Empty;
            [JsonProperty("coordinates")] public List<List<List<double>>> Coordinates { get; set; } = new();
        }

        [HttpPost, Route("SavePolygonWithLogs"), Consumes("application/json")]
        public async Task<IActionResult> SavePolygonWithLogs([FromBody] SavePolygonRequest dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest(new { Status = 0, Message = "Invalid payload: Name is required." });

            if (!dto.ProjectId.HasValue || dto.ProjectId.Value <= 0)
                return BadRequest(new { Status = 0, Message = "ProjectId is required and must be a valid tbl_project.id." });

            if (dto.LogIds == null || dto.LogIds.Count == 0)
                return BadRequest(new { Status = 0, Message = "LogIds is required and must contain at least one id." });

            try
            {
                string? wkt = dto.Wkt;

                // 1) Coordinates -> WKT
                if (string.IsNullOrWhiteSpace(wkt) && dto.Coordinates != null && dto.Coordinates.Count >= 3)
                {
                    var ring = new List<string>();
                    foreach (var c in dto.Coordinates)
                    {
                        if (c == null || c.Count < 2) continue;
                        ring.Add($"{c[0]} {c[1]}"); // WKT "lon lat"
                    }
                    if (ring.Count < 3)
                        return BadRequest(new { Status = 0, Message = "At least three coordinates required." });

                    if (ring[0] != ring[^1]) ring.Add(ring[0]); // close
                    wkt = $"POLYGON(({string.Join(", ", ring)}))";
                }

                // 2) GeoJSON -> WKT
                if (string.IsNullOrWhiteSpace(wkt) && !string.IsNullOrWhiteSpace(dto.GeoJson))
                {
                    var gj = JsonConvert.DeserializeObject<GeoJson>(dto.GeoJson);
                    var poly = gj?.Features?.FirstOrDefault(f =>
                        f?.Geometry?.Type?.Equals("Polygon", StringComparison.OrdinalIgnoreCase) == true);

                    if (poly == null)
                        return BadRequest(new { Status = 0, Message = "No polygon found in GeoJSON." });

                    var ring = poly.Geometry.Coordinates?.FirstOrDefault();
                    if (ring == null || ring.Count < 3)
                        return BadRequest(new { Status = 0, Message = "Invalid polygon coordinates in GeoJSON." });

                    var first = ring[0];
                    var last = ring[^1];
                    if (first[0] != last[0] || first[1] != last[1])
                        ring.Add(first);

                    var coordsText = string.Join(", ", ring.Select(c => $"{c[0]} {c[1]}"));
                    wkt = $"POLYGON(({coordsText}))";
                }

                if (string.IsNullOrWhiteSpace(wkt))
                    return BadRequest(new { Status = 0, Message = "Provide polygon via Coordinates / Wkt / GeoJson." });

                var ids = dto.LogIds.Distinct().Where(i => i > 0).ToList();
                if (ids.Count == 0)
                    return BadRequest(new { Status = 0, Message = "LogIds must contain valid positive ids." });

                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync();

                await using var tx = await db.Database.BeginTransactionAsync();

                // INSERT polygon into tbl_savepolygon
                long polygonId;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT INTO tbl_savepolygon
                            (project_id, name, region, area, logids_json)
                        VALUES
                            (@pid, @name, ST_GeomFromText(@wkt, 4326), @area, @logjson);";

                    Add(cmd, "@pid", dto.ProjectId.Value);
                    Add(cmd, "@name", dto.Name);
                    Add(cmd, "@wkt", wkt);
                    Add(cmd, "@area", dto.Area);
                    Add(cmd, "@logjson", JsonConvert.SerializeObject(ids));

                    await cmd.ExecuteNonQueryAsync();
                }

                // get inserted ID
                await using (var cmdId = conn.CreateCommand())
                {
                    cmdId.CommandText = "SELECT LAST_INSERT_ID();";
                    var obj = await cmdId.ExecuteScalarAsync();
                    polygonId = (obj is long l) ? l : Convert.ToInt64(obj);
                }

                await tx.CommitAsync();
                await InvalidateMapViewCachesAsync();

                return Ok(new
                {
                    Status = 1,
                    Message = "Polygon saved; geometry stored in `region`.",
                    PolygonId = polygonId,
                    Name = dto.Name,
                    ProjectId = dto.ProjectId.Value,
                    Area = dto.Area,
                    SRID = 4326
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + SafeException.Get(ex) });
            }
        }

        // --- Model for simple save into map_regions ---
        public class SavePolygonModel
        {
            public int? ProjectId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string WKT { get; set; } = string.Empty;
            public List<int>? SessionIds { get; set; } = new();
            public double? Area { get; set; }
            public int? CompanyId { get; set; }
            [JsonPropertyName("company_id")]
            public int? company_id { get; set; }
        }

        public class UpdateProjectPolygonModel : SavePolygonModel
        {
            public int PolygonId { get; set; }
            public int? Id { get; set; }
        }

        public class ReturnAPIResponse
        {
            public int Status { get; set; }
            public string Message { get; set; } = "";
            public object? Data { get; set; }
        }

        private sealed class SubSessionAnalyticsAccumulator
        {
            private sealed class SubSessionStatusMetrics
            {
                public int RecordCount { get; private set; }
                public double TotalDuration { get; private set; }
                public int DurationCount { get; private set; }
                public double TotalSpeed { get; private set; }
                public int SpeedCount { get; private set; }
                public double TotalFileSize { get; private set; }
                public int FileSizeCount { get; private set; }
                public double? MinSpeed { get; private set; }
                public double? MaxSpeed { get; private set; }

                public void AddObservation(double? durationMs, double? speedKbps, double? fileSizeBytes)
                {
                    RecordCount++;

                    if (durationMs.HasValue)
                    {
                        TotalDuration += durationMs.Value;
                        DurationCount++;
                    }

                    if (speedKbps.HasValue)
                    {
                        TotalSpeed += speedKbps.Value;
                        SpeedCount++;
                        MinSpeed = !MinSpeed.HasValue ? speedKbps.Value : Math.Min(MinSpeed.Value, speedKbps.Value);
                        MaxSpeed = !MaxSpeed.HasValue ? speedKbps.Value : Math.Max(MaxSpeed.Value, speedKbps.Value);
                    }

                    if (fileSizeBytes.HasValue)
                    {
                        TotalFileSize += fileSizeBytes.Value;
                        FileSizeCount++;
                    }
                }
            }

            private sealed class SubSessionLocation
            {
                public long RowId { get; set; }
                public int SubSessionId { get; set; }
                public string? SubSessionType { get; set; }
                public string? Number { get; set; }
                public string? Direction { get; set; }
                public string? ResultStatusRaw { get; set; }
                public float? StartLat { get; set; }
                public float? StartLon { get; set; }
                public float? EndLat { get; set; }
                public float? EndLon { get; set; }
                public double? DurationMs { get; set; }
                public string? ResultStatus { get; set; }
            }

            public int SessionId { get; set; }
            public float? StartLat { get; set; }
            public float? StartLon { get; set; }
            public float? EndLat { get; set; }
            public float? EndLon { get; set; }
            public HashSet<int> SubSessionIds { get; } = new();
            private Dictionary<long, SubSessionLocation> SubSessionLocationMap { get; } = new();
            public double TotalDuration { get; private set; }
            public int DurationCount { get; private set; }
            public double TotalSpeed { get; private set; }
            public int SpeedCount { get; private set; }
            public double TotalFileSize { get; private set; }
            public int FileSizeCount { get; private set; }
            public double? MinSpeed { get; private set; }
            public double? MaxSpeed { get; private set; }
            private Dictionary<string, SubSessionStatusMetrics> StatusMetricsMap { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public void AddSubSession(long? subSessionId)
            {
                if (subSessionId.HasValue && subSessionId.Value >= int.MinValue && subSessionId.Value <= int.MaxValue)
                {
                    SubSessionIds.Add((int)subSessionId.Value);
                }
            }

            private static string NormalizeStatus(string? statusRaw)
            {
                if (string.IsNullOrWhiteSpace(statusRaw))
                {
                    return "2";
                }

                var normalized = statusRaw.Trim().ToUpperInvariant();
                var compact = Regex.Replace(normalized, @"[\s_\-]+", "");

                if (compact is "NOTCONNECTED" or "DISCONNECTED" or "FAILED" or "FAIL" or "ERROR" or "FALSE" or "0" or "2" or "NO")
                {
                    return "2";
                }

                if (compact is "SUCCESS" or "SUCCEEDED" or "SUCCESSFUL" or "PASS" or "PASSED" or "OK" or "TRUE" or "1" or "COMPLETE" or "COMPLETED" or "DONE" or "CONNECTED")
                {
                    return "1";
                }

                if (compact.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                    compact.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    compact.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
                    compact.Contains("CANCEL", StringComparison.OrdinalIgnoreCase))
                {
                    return "2";
                }

                return compact switch
                {
                    _ => "2"
                };
            }

            private static string MergeStatus(string? currentStatus, string? nextStatusRaw)
            {
                var nextStatus = NormalizeStatus(nextStatusRaw);
                return string.Equals(currentStatus, "1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nextStatus, "1", StringComparison.OrdinalIgnoreCase)
                    ? "1"
                    : "2";
            }

            private SubSessionStatusMetrics GetOrCreateStatusMetrics(string? statusRaw)
            {
                var statusKey = NormalizeStatus(statusRaw);
                if (!StatusMetricsMap.TryGetValue(statusKey, out var statusMetrics))
                {
                    statusMetrics = new SubSessionStatusMetrics();
                    StatusMetricsMap[statusKey] = statusMetrics;
                }

                return statusMetrics;
            }

            private SubSessionStatusMetrics GetStatusMetrics(string statusKey)
            {
                return StatusMetricsMap.TryGetValue(statusKey, out var statusMetrics)
                    ? statusMetrics
                    : new SubSessionStatusMetrics();
            }

            private static object ToStatusComparisonResponse(SubSessionStatusMetrics statusMetrics)
            {
                return new
                {
                    count = statusMetrics.RecordCount,
                    total_duration = RoundMetric(statusMetrics.TotalDuration),
                    avg_duration = statusMetrics.DurationCount > 0
                        ? RoundMetric(statusMetrics.TotalDuration / statusMetrics.DurationCount)
                        : 0d,
                    avg_speed = statusMetrics.SpeedCount > 0
                        ? RoundMetric(statusMetrics.TotalSpeed / statusMetrics.SpeedCount)
                        : 0d,
                    min_speed = statusMetrics.MinSpeed.HasValue
                        ? RoundMetric(statusMetrics.MinSpeed.Value)
                        : (double?)null,
                    max_speed = statusMetrics.MaxSpeed.HasValue
                        ? RoundMetric(statusMetrics.MaxSpeed.Value)
                        : (double?)null,
                    total_file_size = RoundMetric(statusMetrics.TotalFileSize),
                    avg_file_size = statusMetrics.FileSizeCount > 0
                        ? RoundMetric(statusMetrics.TotalFileSize / statusMetrics.FileSizeCount)
                        : 0d
                };
            }

            public object ToMetricsResponse()
            {
                var successMetrics = GetStatusMetrics("1");
                var failedMetrics = GetStatusMetrics("2");

                var successCount = successMetrics.RecordCount;
                var failedCount = failedMetrics.RecordCount;

                return new
                {
                    total_duration = RoundMetric(TotalDuration),
                    avg_duration = DurationCount > 0 ? RoundMetric(TotalDuration / DurationCount) : 0d,
                    total_speed = RoundMetric(TotalSpeed),
                    avg_speed = SpeedCount > 0 ? RoundMetric(TotalSpeed / SpeedCount) : 0d,
                    min_speed = MinSpeed.HasValue ? RoundMetric(MinSpeed.Value) : (double?)null,
                    max_speed = MaxSpeed.HasValue ? RoundMetric(MaxSpeed.Value) : (double?)null,
                    total_file_size = RoundMetric(TotalFileSize),
                    avg_file_size = FileSizeCount > 0 ? RoundMetric(TotalFileSize / FileSizeCount) : 0d,
                    status_counts = new
                    {
                        success = successCount,
                        failed = failedCount,
                        total = successCount + failedCount
                    },
                    comparison = new
                    {
                        success = ToStatusComparisonResponse(successMetrics),
                        failed = ToStatusComparisonResponse(failedMetrics)
                    }
                };
            }

            public void AddMetrics(double? durationMs, double? speedKbps, double? fileSizeBytes, string? resultStatusRaw)
            {
                if (durationMs.HasValue)
                {
                    TotalDuration += durationMs.Value;
                    DurationCount++;
                }

                if (speedKbps.HasValue)
                {
                    TotalSpeed += speedKbps.Value;
                    SpeedCount++;
                    MinSpeed = !MinSpeed.HasValue ? speedKbps.Value : Math.Min(MinSpeed.Value, speedKbps.Value);
                    MaxSpeed = !MaxSpeed.HasValue ? speedKbps.Value : Math.Max(MaxSpeed.Value, speedKbps.Value);
                }

                if (fileSizeBytes.HasValue)
                {
                    TotalFileSize += fileSizeBytes.Value;
                    FileSizeCount++;
                }

                var statusMetrics = GetOrCreateStatusMetrics(resultStatusRaw);
                statusMetrics.AddObservation(durationMs, speedKbps, fileSizeBytes);
            }

            public void AddSubSessionLocation(long rowId, long? subSessionId, string? subSessionType, string? number, string? direction, float? startLat, float? startLon, float? endLat, float? endLon, double? durationMs, string? resultStatusRaw)
            {
                if (!subSessionId.HasValue || subSessionId.Value < int.MinValue || subSessionId.Value > int.MaxValue)
                {
                    return;
                }

                var key = rowId;
                var normalizedSubSessionType = NormalizeSubSessionType(subSessionType);
                var normalizedNumber = NormalizeSubSessionText(number);
                var normalizedDirection = NormalizeSubSessionText(direction);
                var normalizedResultStatusRaw = NormalizeSubSessionText(resultStatusRaw);
                if (SubSessionLocationMap.TryGetValue(key, out var existing))
                {
                    existing.SubSessionType ??= normalizedSubSessionType;
                    existing.Number ??= normalizedNumber;
                    existing.Direction ??= normalizedDirection;
                    existing.ResultStatusRaw ??= normalizedResultStatusRaw;
                    existing.StartLat ??= startLat;
                    existing.StartLon ??= startLon;
                    existing.EndLat ??= endLat;
                    existing.EndLon ??= endLon;
                    existing.DurationMs ??= durationMs;
                    existing.ResultStatus = MergeStatus(existing.ResultStatus, resultStatusRaw);
                    return;
                }

                SubSessionLocationMap[key] = new SubSessionLocation
                {
                    RowId = rowId,
                    SubSessionId = (int)subSessionId.Value,
                    SubSessionType = normalizedSubSessionType,
                    Number = normalizedNumber,
                    Direction = normalizedDirection,
                    ResultStatusRaw = normalizedResultStatusRaw,
                    StartLat = startLat,
                    StartLon = startLon,
                    EndLat = endLat,
                    EndLon = endLon,
                    DurationMs = durationMs,
                    ResultStatus = NormalizeStatus(resultStatusRaw)
                };
            }

            private static string? NormalizeSubSessionType(string? subSessionType)
            {
                return string.IsNullOrWhiteSpace(subSessionType)
                    ? null
                    : subSessionType.Trim();
            }

            private static string? NormalizeSubSessionText(string? value)
            {
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : value.Trim();
            }

            public object ToResponse()
            {
                var subSessions = SubSessionLocationMap.Values
                    .OrderBy(x => x.RowId)
                    .Select(x => new
                    {
                        id = x.RowId,
                        sub_session_id = x.SubSessionId,
                        sub_session_type = x.SubSessionType,
                        number = x.Number,
                        direction = x.Direction,
                        coordinates = new
                        {
                            start_lat = x.StartLat,
                            start_lon = x.StartLon,
                            end_lat = x.EndLat,
                            end_lon = x.EndLon
                        },
                        duration_ms = x.DurationMs.HasValue ? RoundMetric(x.DurationMs.Value) : (double?)null,
                        result_status_raw = x.ResultStatusRaw,
                        result_status = string.Equals(x.ResultStatus, "1", StringComparison.OrdinalIgnoreCase) ? "connected" : "not_connected"
                    })
                    .ToList();

                return new
                {
                    session_id = SessionId,
                    coordinates = new
                    {
                        start_lat = StartLat,
                        start_lon = StartLon,
                        end_lat = EndLat,
                        end_lon = EndLon
                    },
                    sub_session_count = SubSessionLocationMap.Count,
                    sub_sessions = subSessions,
                    metrics = ToMetricsResponse()
                };
            }
        }

        [HttpPost("ImportPolygon")]
        public Task<JsonResult> ImportPolygon([FromBody] SavePolygonModel model)
        {
            return SavePolygon(model);
        }

        private static async Task EnsureMapRegionsSessionIdCapacityAsync(DbConnection conn)
        {
            await using var inspect = conn.CreateCommand();
            inspect.CommandText = @"
                SELECT DATA_TYPE, COALESCE(CHARACTER_MAXIMUM_LENGTH, 0)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'map_regions'
                  AND COLUMN_NAME = 'session_id'
                LIMIT 1;";

            string dataType = "";
            long maxLength = 0;
            await using (var reader = await inspect.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    dataType = Convert.ToString(reader.GetValue(0))?.ToLowerInvariant() ?? "";
                    maxLength = Convert.ToInt64(reader.GetValue(1));
                }
            }

            if (dataType is "text" or "mediumtext" or "longtext" || maxLength >= 1024)
            {
                return;
            }

            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE map_regions MODIFY COLUMN session_id VARCHAR(1024) NULL;";
            await alter.ExecuteNonQueryAsync();
        }

        [HttpPost("SavePolygon")]
        public async Task<JsonResult> SavePolygon([FromBody] SavePolygonModel model)
        {
            var message = new ReturnAPIResponse();

            try
            {
                if (model == null || string.IsNullOrWhiteSpace(model.WKT) || string.IsNullOrWhiteSpace(model.Name))
                {
                    message.Status = 0;
                    message.Message = "Invalid polygon data (Name and WKT are required).";
                    return Json(message);
                }

                bool isSuperAdmin = _userScope.IsSuperAdmin(User);
                int currentUserId = GetCurrentUserId();
                int targetCompanyId = GetTargetCompanyId(model.CompanyId ?? model.company_id);
                bool useUserScope = !isSuperAdmin && targetCompanyId == 0 && currentUserId > 0;

                if (!isSuperAdmin && targetCompanyId == 0 && !useUserScope)
                {
                    message.Status = 0;
                    message.Message = "Unauthorized. Unable to resolve Company Context.";
                    return Json(message);
                }

                string sessionStr = string.Join(",", model.SessionIds ?? new List<int>());

                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                await EnsureMapRegionsSessionIdCapacityAsync(conn);

                var mapRegionColumns = await GetTableColumnSetAsync(conn, "map_regions");
                var hasCreatedByUserId = mapRegionColumns.Contains("created_by_user_id");
                var sql = hasCreatedByUserId
                    ? @"
                    INSERT INTO map_regions (tbl_project_id, name, region, status, session_id, area, created_by_user_id)
                    VALUES (@pid, @name, ST_GeomFromText(@wkt, 4326), 1, @sids, @area, @createdByUserId);"
                    : @"
                    INSERT INTO map_regions (tbl_project_id, name, region, status, session_id, area)
                    VALUES (@pid, @name, ST_GeomFromText(@wkt, 4326), 1, @sids, @area);";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                AddParam(cmd, "@pid", (object?)model.ProjectId ?? DBNull.Value);
                AddParam(cmd, "@name", model.Name ?? string.Empty);
                AddParam(cmd, "@wkt", model.WKT ?? string.Empty);
                AddParam(cmd, "@sids", string.IsNullOrWhiteSpace(sessionStr) ? DBNull.Value : sessionStr);
                AddParam(cmd, "@area", (object?)model.Area ?? DBNull.Value);
                if (hasCreatedByUserId)
                {
                    AddParam(cmd, "@createdByUserId", currentUserId > 0 ? currentUserId : DBNull.Value);
                }

                await cmd.ExecuteNonQueryAsync();

                await using var cmdId = conn.CreateCommand();
                cmdId.CommandText = "SELECT LAST_INSERT_ID();";
                var insertedIdRaw = await cmdId.ExecuteScalarAsync();
                var insertedId = insertedIdRaw != null && insertedIdRaw != DBNull.Value
                    ? Convert.ToInt32(insertedIdRaw)
                    : 0;
                await InvalidateMapViewCachesAsync();

                message.Status = 1;
                message.Message = "Polygon saved successfully.";
                message.Data = new
                {
                    polygonId = insertedId,
                    id = insertedId,
                    saved = true,
                    area = model.Area,
                    sessionCsv = string.IsNullOrWhiteSpace(sessionStr) ? null : sessionStr,
                    currentSessionId = HttpContext?.Session?.Id
                };
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = "Error saving polygon: " + SafeException.Get(ex);
            }

            return Json(message);
        }

        [HttpPost("UpdateProjectPolygon")]
        public async Task<JsonResult> UpdateProjectPolygon([FromBody] UpdateProjectPolygonModel model)
        {
            var message = new ReturnAPIResponse();

            try
            {
                var polygonId = model?.PolygonId > 0 ? model.PolygonId : model?.Id ?? 0;
                if (model == null || polygonId <= 0 || model.ProjectId <= 0 || string.IsNullOrWhiteSpace(model.WKT))
                {
                    message.Status = 0;
                    message.Message = "Invalid polygon data (PolygonId, ProjectId and WKT are required).";
                    return Json(message);
                }

                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                var mapRegionColumns = await GetTableColumnSetAsync(conn, "map_regions");
                var hasUpdatedAt = mapRegionColumns.Contains("updated_at");

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = hasUpdatedAt
                    ? @"
                    UPDATE map_regions
                    SET region = ST_GeomFromText(@wkt, 4326),
                        area = @area,
                        updated_at = NOW()
                    WHERE id = @polygonId
                      AND tbl_project_id = @projectId
                      AND status = 1;"
                    : @"
                    UPDATE map_regions
                    SET region = ST_GeomFromText(@wkt, 4326),
                        area = @area
                    WHERE id = @polygonId
                      AND tbl_project_id = @projectId
                      AND status = 1;";

                AddParam(cmd, "@polygonId", polygonId);
                AddParam(cmd, "@projectId", model.ProjectId.Value);
                AddParam(cmd, "@wkt", model.WKT);
                AddParam(cmd, "@area", (object?)model.Area ?? DBNull.Value);

                var updatedRows = await cmd.ExecuteNonQueryAsync();
                if (updatedRows <= 0)
                {
                    message.Status = 0;
                    message.Message = "Polygon was not found for this project.";
                    return Json(message);
                }

                await InvalidateMapViewCachesAsync();

                message.Status = 1;
                message.Message = "Polygon updated successfully.";
                message.Data = new
                {
                    polygonId,
                    projectId = model.ProjectId.Value,
                    updated = true,
                    area = model.Area
                };
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = "Error updating polygon: " + SafeException.Get(ex);
            }

            return Json(message);
        }









[HttpGet("GetAvailablePolygons")]
public async Task<IActionResult> GetAvailablePolygons(
    [FromQuery] int? sessionId = null,
    [FromQuery] int? company_id = null)
{
    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
    
    // =========================================================
    // 1. SMART SECURITY: RESOLVE COMPANY ID
    // =========================================================
    bool isSuperAdmin = _userScope.IsSuperAdmin(User);
    int targetCompanyId = GetTargetCompanyId(company_id);

    if (isSuperAdmin && !sessionId.HasValue)
    {
        targetCompanyId = 0;
    }

    if (!isSuperAdmin && targetCompanyId == 0)
        return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve Company Context." });

    // =========================================================
    // 2. VALIDATE SPECIFIC SESSION OWNERSHIP (FIXED)
    // =========================================================
    if (targetCompanyId > 0 && sessionId.HasValue)
    {
        // FIX: Use a manual JOIN instead of 's.User' navigation property
        bool isOwned = await (
            from s in db.tbl_session.AsNoTracking()
            join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
            where s.id == sessionId.Value && u.company_id == targetCompanyId
            select s.id
        ).AnyAsync();

        if (!isOwned)
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Session does not belong to your company." });
        }
    }

    try
    {
        // ========================================
        //  BUILD CACHE KEY
        // ========================================
        string cacheKey = $"availablepolygons:C{targetCompanyId}:{sessionId?.ToString() ?? "all"}";

        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var cached = await _redis.GetObjectAsync<AvailablePolygonsResponse>(cacheKey);
                if (cached != null)
                {
                    totalStopwatch.Stop();
                    Response.Headers["X-Cache"] = "HIT";
                    return Ok(cached);
                }
            }
            catch { }
        }

        // ========================================
        //  FETCH FROM DATABASE
        // ========================================
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<object>();
        
        var conn = db.Database.GetDbConnection();
        bool shouldClose = false;
        
        try
        {
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
                shouldClose = true;
            }

            await using var cmd = conn.CreateCommand();
            var mapRegionColumns = await GetTableColumnSetAsync(conn, "map_regions");
            var hasCreatedByUserId = mapRegionColumns.Contains("created_by_user_id");

            // SQL STRATEGY
            string whereClause;
            
            if (targetCompanyId == 0 || sessionId.HasValue)
            {
                // FAST PATH: Super Admin OR Validated Single Session
                var sessionClause = @"
                    (
                        @sid IS NULL
                        OR session_id IS NULL
                        OR session_id = ''
                        OR FIND_IN_SET(@sidStr, session_id) > 0
                    )";
                whereClause = sessionClause;
            }
            else
            {
                // SECURE PATH: Company Admin requesting ALL
                whereClause = hasCreatedByUserId
                    ? @"
                    (
                        map_regions.created_by_user_id IN (
                            SELECT u2.id FROM tbl_user u2 WHERE u2.company_id = @compId
                        )
                        OR
                        EXISTS (
                            SELECT 1 FROM tbl_session s
                            JOIN tbl_user u ON s.user_id = u.id
                            WHERE u.company_id = @compId
                            AND FIND_IN_SET(s.id, map_regions.session_id) > 0
                        )
                    )"
                    : @"
                    EXISTS (
                        SELECT 1 FROM tbl_session s
                        JOIN tbl_user u ON s.user_id = u.id
                        WHERE u.company_id = @compId
                        AND FIND_IN_SET(s.id, map_regions.session_id) > 0
                    )";
            }

            cmd.CommandText = $@"
                SELECT id, name, ST_AsText(region) AS wkt, session_id, area
                FROM map_regions
                WHERE status = 1
                  AND tbl_project_id IS NULL
                  AND {whereClause}";

            AddParam(cmd, "@sid", (object?)sessionId ?? DBNull.Value);
            AddParam(cmd, "@sidStr", sessionId?.ToString() ?? (object)DBNull.Value);
            
            if (targetCompanyId > 0)
            {
                AddParam(cmd, "@compId", targetCompanyId);
            }

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var sessionCsv = r.IsDBNull(3) ? null : r.GetString(3);
                var parsed = ParseCsvToIntList(sessionCsv);
                var area = r.IsDBNull(4) ? (double?)null : Convert.ToDouble(r.GetValue(4));

                rows.Add(new
                {
                    id = r.GetFieldValue<int>(0),
                    name = r.IsDBNull(1) ? null : r.GetString(1),
                    wkt = r.IsDBNull(2) ? null : r.GetString(2),
                    area,
                    sessionCsv,
                    sessionIds = parsed
                });
            }
        }

        finally
        {
            if (shouldClose && conn.State == ConnectionState.Open)
                await conn.CloseAsync();
        }

        dbStopwatch.Stop();

        // ========================================
        //  BUILD RESPONSE
        // ========================================
        var response = new AvailablePolygonsResponse
        {
            currentSessionId = HttpContext?.Session?.Id,
            data = rows
        };

        if (_redis != null && _redis.IsConnected)
            await _redis.SetObjectAsync(cacheKey, response, ttlSeconds: 600);

        totalStopwatch.Stop();
        Response.Headers["X-Cache"] = "MISS";
        Response.Headers["X-Row-Count"] = rows.Count.ToString();

        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { error = "Failed to fetch polygons", details = SafeException.Get(ex) });
    }
}

[HttpDelete("DeleteAvailablePolygon")]
public async Task<IActionResult> DeleteAvailablePolygon(
    [FromQuery] int polygonId,
    [FromQuery] int? company_id = null)
{
    if (polygonId <= 0)
        return BadRequest(new { Status = 0, Message = "Valid polygonId is required." });

    try
    {
        bool isSuperAdmin = _userScope.IsSuperAdmin(User);
        int targetCompanyId = GetTargetCompanyId(company_id);

        if (!isSuperAdmin && targetCompanyId == 0)
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve Company Context." });

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        var mapRegionColumns = await GetTableColumnSetAsync(conn, "map_regions");
        var hasCreatedByUserId = mapRegionColumns.Contains("created_by_user_id");

        string ownershipClause = "1 = 1";
        if (!isSuperAdmin)
        {
            ownershipClause = hasCreatedByUserId
                ? @"
                (
                    map_regions.created_by_user_id IN (
                        SELECT u2.id FROM tbl_user u2 WHERE u2.company_id = @compId
                    )
                    OR
                    EXISTS (
                        SELECT 1 FROM tbl_session s
                        JOIN tbl_user u ON s.user_id = u.id
                        WHERE u.company_id = @compId
                        AND FIND_IN_SET(s.id, map_regions.session_id) > 0
                    )
                )"
                : @"
                EXISTS (
                    SELECT 1 FROM tbl_session s
                    JOIN tbl_user u ON s.user_id = u.id
                    WHERE u.company_id = @compId
                    AND FIND_IN_SET(s.id, map_regions.session_id) > 0
                )";
        }

        await using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.CommandText = $@"
                SELECT COUNT(1)
                FROM map_regions
                WHERE id = @polygonId
                  AND tbl_project_id IS NULL
                  AND {ownershipClause};";
            AddParam(existsCmd, "@polygonId", polygonId);
            if (!isSuperAdmin)
                AddParam(existsCmd, "@compId", targetCompanyId);

            var existsRaw = await existsCmd.ExecuteScalarAsync();
            var exists = existsRaw != null && existsRaw != DBNull.Value && Convert.ToInt32(existsRaw) > 0;
            if (!exists)
            {
                return NotFound(new
                {
                    Status = 0,
                    Message = "Polygon not found, already assigned to a project, or not available for your company."
                });
            }
        }

        await using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM map_regions WHERE id = @polygonId AND tbl_project_id IS NULL;";
            AddParam(deleteCmd, "@polygonId", polygonId);
            await deleteCmd.ExecuteNonQueryAsync();
        }

        await InvalidateMapViewCachesAsync();

        return Ok(new { Status = 1, Message = "Polygon deleted successfully.", polygonId });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error deleting polygon: " + SafeException.Get(ex) });
    }
}

        [HttpGet("GetSubSessionAnalytics")]
        [HttpGet("GetSubSessionAnalyticsWithStatus")]
        public async Task<IActionResult> GetSubSessionAnalytics(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null)
        {
            try
            {
                var requestedSessionIds = new List<int>();

                if (sessionId.HasValue && sessionId.Value > 0)
                {
                    requestedSessionIds.Add(sessionId.Value);
                }

                requestedSessionIds.AddRange(ParseCsvToIntList(sessionIds));
                requestedSessionIds.AddRange(ParseCsvToIntList(sessionIdsAlt));
                requestedSessionIds = requestedSessionIds
                    .Where(x => x > 0)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                if ((sessionId.HasValue || !string.IsNullOrWhiteSpace(sessionIds) || !string.IsNullOrWhiteSpace(sessionIdsAlt))
                    && requestedSessionIds.Count == 0)
                {
                    return BadRequest(new { message = "Provide a valid sessionId or comma-separated sessionIds/session_ids." });
                }

                var isWithStatusRoute = Request?.Path.Value?.EndsWith(
                    "/GetSubSessionAnalyticsWithStatus",
                    StringComparison.OrdinalIgnoreCase) == true
                    || string.Equals(Request?.Query["includeStatus"], "1", StringComparison.OrdinalIgnoreCase);

                var cacheKey = BuildMapViewCacheKey(
                    "subsession-v11",
                    requestedSessionIds.Count > 0 ? string.Join("-", requestedSessionIds) : "all",
                    isWithStatusRoute ? "with-status" : "base");

                var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
                if (cached != null)
                    return Json(cached);

                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                var subSessionTypeColumn = await GetFirstExistingColumnAsync(
                    conn,
                    "tbl_sub_session",
                    "sub_session_type",
                    "session_type",
                    "type",
                    "test_type",
                    "service_type");

                var subSessionTypeSql = subSessionTypeColumn != null
                    ? $"`{subSessionTypeColumn.Replace("`", "``")}`"
                    : @"
                        CASE
                            WHEN JSON_VALID(json_data) THEN COALESCE(
                                JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.sub_session_type')),
                                JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.session_type')),
                                JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.type')),
                                JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.test_type')),
                                JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.service_type'))
                            )
                            ELSE NULL
                        END";

                var resultStatusColumn = await GetFirstExistingColumnAsync(
                    conn,
                    "tbl_sub_session",
                    "status");

                var resultStatusSql = resultStatusColumn != null
                    ? @"
                        CASE
                            WHEN JSON_VALID(json_data) THEN COALESCE(
                                NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.result_status'))), ''),
                                NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.connection_status'))), ''),
                                NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.call_status'))), ''),
                                NULLIF(TRIM(CAST(`" + resultStatusColumn.Replace("`", "``") + @"` AS CHAR)), '')
                            )
                            ELSE NULLIF(TRIM(CAST(`" + resultStatusColumn.Replace("`", "``") + @"` AS CHAR)), '')
                        END"
                    : @"
                        CASE
                            WHEN JSON_VALID(json_data) THEN NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.result_status'))), '')
                            ELSE NULL
                        END";

                var numberSql = @"
                    CASE
                        WHEN JSON_VALID(json_data) THEN COALESCE(
                            NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.number'))), ''),
                            NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.phone_number'))), ''),
                            NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.msisdn'))), '')
                        )
                        ELSE NULL
                    END";

                var directionSql = @"
                    CASE
                        WHEN JSON_VALID(json_data) THEN COALESCE(
                            NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.direction'))), ''),
                            NULLIF(TRIM(JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.call_direction'))), '')
                        )
                        ELSE NULL
                    END";

                var durationColumn = await GetFirstExistingColumnAsync(
                    conn,
                    "tbl_sub_session",
                    "duration_ms",
                    "duration",
                    "call_duration_ms");

                var speedColumn = await GetFirstExistingColumnAsync(
                    conn,
                    "tbl_sub_session",
                    "speed_kbps",
                    "speed");

                var fileSizeColumn = await GetFirstExistingColumnAsync(
                    conn,
                    "tbl_sub_session",
                    "file_size_bytes",
                    "file_size");

                var durationSql = durationColumn != null
                    ? $"NULLIF(TRIM(CAST(`{durationColumn.Replace("`", "``")}` AS CHAR)), '')"
                    : @"
                        CASE
                            WHEN JSON_VALID(json_data) THEN JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.duration_ms'))
                            ELSE NULL
                        END";

                var speedSql = speedColumn != null
                    ? $"NULLIF(TRIM(CAST(`{speedColumn.Replace("`", "``")}` AS CHAR)), '')"
                    : @"
                        CASE
                            WHEN JSON_VALID(json_data) THEN JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.speed_kbps'))
                            ELSE NULL
                        END";

                var fileSizeSql = fileSizeColumn != null
                    ? $"NULLIF(TRIM(CAST(`{fileSizeColumn.Replace("`", "``")}` AS CHAR)), '')"
                    : @"
                        CASE
                            WHEN JSON_VALID(json_data) THEN JSON_UNQUOTE(JSON_EXTRACT(json_data, '$.file_size_bytes'))
                            ELSE NULL
                        END";

                var sql = @"
                    SELECT
                        id,
                        session_id,
                        sub_session_id,
                        " + subSessionTypeSql + @" AS sub_session_type,
                        start_lat,
                        start_lon,
                        end_lat,
                        end_lon,
                        " + durationSql + @" AS duration_ms,
                        " + speedSql + @" AS speed_kbps,
                        " + fileSizeSql + @" AS file_size_bytes,
                        " + resultStatusSql + @" AS result_status,
                        " + numberSql + @" AS number,
                        " + directionSql + @" AS direction
                    FROM tbl_sub_session
                    WHERE session_id IS NOT NULL";

                if (requestedSessionIds.Count > 0)
                {
                    sql += $" AND session_id IN ({string.Join(", ", requestedSessionIds.Select((_, i) => $"@sid{i}"))})";
                }

                sql += " ORDER BY id ASC;";

                var sessionMap = new Dictionary<int, SubSessionAnalyticsAccumulator>();
                var overall = new SubSessionAnalyticsAccumulator();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                for (var i = 0; i < requestedSessionIds.Count; i++)
                {
                    AddParam(cmd, $"@sid{i}", requestedSessionIds[i]);
                }

                {
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (reader.IsDBNull(0))
                        {
                            continue;
                        }

                        var rowId = Convert.ToInt64(reader.GetValue(0));
                        var currentSessionId = Convert.ToInt32(reader.GetValue(1));
                        long? subSessionId = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2));
                        var subSessionType = reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString();
                        float? subSessionStartLat = TryParseNullableFloat(reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString());
                        float? subSessionStartLon = TryParseNullableFloat(reader.IsDBNull(5) ? null : reader.GetValue(5)?.ToString());
                        float? subSessionEndLat = TryParseNullableFloat(reader.IsDBNull(6) ? null : reader.GetValue(6)?.ToString());
                        float? subSessionEndLon = TryParseNullableFloat(reader.IsDBNull(7) ? null : reader.GetValue(7)?.ToString());
                        var durationMs = TryParseNullableDouble(reader.IsDBNull(8) ? null : reader.GetValue(8)?.ToString());
                        var speedKbps = TryParseNullableDouble(reader.IsDBNull(9) ? null : reader.GetValue(9)?.ToString());
                        var fileSizeBytes = TryParseNullableDouble(reader.IsDBNull(10) ? null : reader.GetValue(10)?.ToString());
                        var resultStatus = reader.IsDBNull(11) ? null : reader.GetValue(11)?.ToString();
                        var number = reader.IsDBNull(12) ? null : reader.GetValue(12)?.ToString();
                        var direction = reader.IsDBNull(13) ? null : reader.GetValue(13)?.ToString();

                        if (!sessionMap.TryGetValue(currentSessionId, out var sessionAgg))
                        {
                            sessionAgg = new SubSessionAnalyticsAccumulator { SessionId = currentSessionId };
                            sessionMap[currentSessionId] = sessionAgg;
                        }

                        sessionAgg.AddSubSession(subSessionId);
                        sessionAgg.AddSubSessionLocation(rowId, subSessionId, subSessionType, number, direction, subSessionStartLat, subSessionStartLon, subSessionEndLat, subSessionEndLon, durationMs, resultStatus);
                        sessionAgg.AddMetrics(durationMs, speedKbps, fileSizeBytes, resultStatus);

                        overall.AddMetrics(durationMs, speedKbps, fileSizeBytes, resultStatus);
                    }
                }

                var sessionIdsToLoad = sessionMap.Keys.ToList();
                if (sessionIdsToLoad.Count > 0)
                {
                    var sessionCoordinates = await db.tbl_session
                        .AsNoTracking()
                        .Where(s => s.id.HasValue && sessionIdsToLoad.Contains(s.id.Value))
                        .Select(s => new
                        {
                            s.id,
                            s.start_lat,
                            s.start_lon,
                            s.end_lat,
                            s.end_lon
                        })
                        .ToListAsync();

                    foreach (var sessionCoordinate in sessionCoordinates)
                    {
                        if (sessionCoordinate.id.HasValue && sessionMap.TryGetValue(sessionCoordinate.id.Value, out var sessionAgg))
                        {
                            sessionAgg.StartLat = sessionCoordinate.start_lat;
                            sessionAgg.StartLon = sessionCoordinate.start_lon;
                            sessionAgg.EndLat = sessionCoordinate.end_lat;
                            sessionAgg.EndLon = sessionCoordinate.end_lon;
                        }
                    }
                }

                var sessions = sessionMap.Values
                    .OrderBy(x => x.SessionId)
                    .Select(x => x.ToResponse())
                    .ToList();

                object response;
                if (isWithStatusRoute)
                {
                    response = new
                    {
                        status_mode = "column_status_numeric_v1",
                        requested_session_ids = requestedSessionIds,
                        data = sessions,
                        summary = overall.ToMetricsResponse(),
                        status = sessions.Count > 0 ? 1 : 2
                    };
                }
                else
                {
                    response = new
                    {
                        status_mode = "column_status_numeric_v1",
                        requested_session_ids = requestedSessionIds,
                        data = sessions,
                        summary = overall.ToMetricsResponse()
                    };
                }

                await SetMapViewCacheAsync(cacheKey, response, 15);
                return Json(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "An error occurred while fetching sub-session analytics.",
                    details = SafeException.Get(ex)
                });
            }
        }

// ========================================
//  RESPONSE DTO FOR CACHING
// ========================================
public class AvailablePolygonsResponse
{
    public string? currentSessionId { get; set; }
    public List<object> data { get; set; } = new();
}   // ----- helpers -----
        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static async Task<string?> GetFirstExistingColumnAsync(DbConnection conn, string tableName, params string[] candidateColumns)
        {
            if (candidateColumns.Length == 0)
            {
                return null;
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName
                  AND COLUMN_NAME IN ({string.Join(", ", candidateColumns.Select((_, i) => $"@column{i}"))});";

            AddParam(cmd, "@tableName", tableName);
            for (var i = 0; i < candidateColumns.Length; i++)
            {
                AddParam(cmd, $"@column{i}", candidateColumns[i]);
            }

            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    existingColumns.Add(reader.GetString(0));
                }
            }

            return candidateColumns.FirstOrDefault(existingColumns.Contains);
        }

        private sealed class ExistingColumnInfo
        {
            public string Name { get; init; } = string.Empty;
            public string DataType { get; init; } = string.Empty;
            public string ColumnType { get; init; } = string.Empty;
        }

        private static async Task<ExistingColumnInfo?> GetFirstExistingColumnInfoAsync(DbConnection conn, string tableName, params string[] candidateColumns)
        {
            if (candidateColumns.Length == 0)
            {
                return null;
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName
                  AND COLUMN_NAME IN ({string.Join(", ", candidateColumns.Select((_, i) => $"@column{i}"))});";

            AddParam(cmd, "@tableName", tableName);
            for (var i = 0; i < candidateColumns.Length; i++)
            {
                AddParam(cmd, $"@column{i}", candidateColumns[i]);
            }

            var existingColumns = new Dictionary<string, ExistingColumnInfo>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var columnName = reader.GetString(0);
                existingColumns[columnName] = new ExistingColumnInfo
                {
                    Name = columnName,
                    DataType = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ColumnType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                };
            }

            foreach (var candidate in candidateColumns)
            {
                if (existingColumns.TryGetValue(candidate, out var match))
                {
                    return match;
                }
            }

            return null;
        }

        private static bool IsTextColumnType(string? dataType)
        {
            var normalized = string.IsNullOrWhiteSpace(dataType) ? string.Empty : dataType.Trim().ToLowerInvariant();
            return normalized is "char" or "varchar" or "text" or "tinytext" or "mediumtext" or "longtext";
        }

        private static bool IsNumericColumnType(string? dataType)
        {
            var normalized = string.IsNullOrWhiteSpace(dataType) ? string.Empty : dataType.Trim().ToLowerInvariant();
            return normalized is
                "tinyint" or
                "smallint" or
                "mediumint" or
                "int" or
                "integer" or
                "bigint" or
                "decimal" or
                "numeric" or
                "float" or
                "double" or
                "real";
        }

        private async Task EnsureSitePredictionTextColumnAsync(
            DbConnection conn,
            string tableName,
            string columnName,
            string varcharDefinition = "VARCHAR(255) NULL")
        {
            await using var inspectCmd = conn.CreateCommand();
            inspectCmd.CommandText = @"
                SELECT DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName
                  AND COLUMN_NAME = @columnName
                LIMIT 1;";
            AddParam(inspectCmd, "@tableName", tableName);
            AddParam(inspectCmd, "@columnName", columnName);

            var dataTypeObj = await inspectCmd.ExecuteScalarAsync();
            var dataType = dataTypeObj == null || dataTypeObj == DBNull.Value
                ? null
                : Convert.ToString(dataTypeObj)?.Trim();

            if (string.IsNullOrWhiteSpace(dataType) || IsTextColumnType(dataType))
            {
                return;
            }

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = $@"ALTER TABLE `{tableName}` MODIFY COLUMN `{columnName}` {varcharDefinition};";
            await alterCmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureSitePredictionNameColumnsAsync(DbConnection conn)
        {
            await EnsureSitePredictionTextColumnAsync(conn, "site_prediction", "site_name");
            await EnsureSitePredictionTextColumnAsync(conn, "site_prediction_optimized", "site_name");
        }

        private static bool TryParseNodeIdForNumericColumn(string? raw, out double parsedValue)
        {
            parsedValue = 0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var trimmed = raw.Trim();
            if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedValue))
            {
                return true;
            }

            var match = Regex.Match(trimmed, @"^[A-Za-z_\-\s]*?(\d+(?:\.\d+)?)$");
            if (!match.Success)
            {
                return false;
            }

            return double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedValue);
        }

        private static double? TryParseNullableDouble(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static float? TryParseNullableFloat(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (float.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }


        private static double RoundMetric(double value)
        {
            return Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        private static List<int> ParseCsvToIntList(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new List<int>();
            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<int>(parts.Length);
            foreach (var s in parts)
                if (int.TryParse(s, out var v)) list.Add(v);
            return list;
        }

        // =========================================================
        // ================== Polygon analytics ====================
        // =========================================================

        [HttpGet, Route("GetPolygonLogCount")]
        public async Task<JsonResult> GetPolygonLogCount(int polygonId, DateTime? from = null, DateTime? to = null)
        {
            try
            {
                var cacheKey = BuildMapViewCacheKey("polygon-log-count", polygonId, from, to);
                var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
                if (cached != null)
                    return Json(cached);

                IQueryable<tbl_network_log> q = db.tbl_network_log.Where(l => l.polygon_id == polygonId);

                if (from.HasValue)
                    q = q.Where(l => l.timestamp >= from.Value);

                if (to.HasValue)
                    q = q.Where(l => l.timestamp < to.Value.AddDays(1));

                var total = await q.CountAsync();
                DateTime? first = await q.MinAsync(l => l.timestamp);
                DateTime? last = await q.MaxAsync(l => l.timestamp);

                var response = new { polygonId, total, from, to, first, last };
                await SetMapViewCacheAsync(cacheKey, response);
                return Json(response);
            }
            catch (Exception ex)
            {
                return new JsonResult(new { message = "Server error: " + SafeException.Get(ex) }) { StatusCode = 500 };
            }
        }

        // =========================================================
        // =========== Project creation / polygon linking ==========
        // =========================================================

        public class CreateProjectModel
        {
            public string ProjectName { get; set; } = string.Empty;
            public string? Provider { get; set; }
            public string? Tech { get; set; }
            public string? Band { get; set; }
            public string? EarFcn { get; set; }
            public string? Apps { get; set; }
            public DateTime? FromDate { get; set; }
            public DateTime? ToDate { get; set; }
            public List<int>? PolygonIds { get; set; }
            public List<int>? SessionIds { get; set; }
            public string? GridSize { get; set; }
            public string? LogGrid { get; set; }
            public string? log_grid { get; set; }
            public int? company_id { get; set; }
            public int? CompanyId { get; set; }
        }

        public class UpdateProjectSessionsModel
        {
            public int ProjectId { get; set; }
            public List<int>? SessionIds { get; set; }
        }

        public class UpdateProjectSiteSizeModel
        {
            public int ProjectId { get; set; }
            public decimal SiteSize { get; set; }
        }

        private async Task<(int? CompanyId, string? Error)> ResolveProjectCompanyIdAsync(CreateProjectModel? model)
        {
            var isSuperAdmin = _userScope.IsSuperAdmin(User);
            int? requestedCompanyId = model?.company_id ?? model?.CompanyId;
            int targetCompanyId = _userScope.GetTargetCompanyId(User, requestedCompanyId);
            int? projectCompanyId = targetCompanyId > 0 ? targetCompanyId : null;

            if (!projectCompanyId.HasValue && model?.SessionIds != null && model.SessionIds.Any())
            {
                var inferredCompanyIds = await (
                    from s in db.tbl_session
                    join u in db.tbl_user on s.user_id equals u.id
                    where s.id.HasValue
                          && model.SessionIds.Contains(s.id.Value)
                          && u.company_id.HasValue
                          && u.company_id.Value > 0
                    select u.company_id.Value
                )
                .Distinct()
                .Take(2)
                .ToListAsync();

                if (inferredCompanyIds.Count == 1)
                {
                    projectCompanyId = inferredCompanyIds[0];
                }
                else if (inferredCompanyIds.Count > 1)
                {
                    if (isSuperAdmin && !requestedCompanyId.HasValue)
                    {
                        projectCompanyId = 0;
                    }
                    else
                    {
                        return (null, "Selected sessions belong to multiple companies. Please choose sessions from a single company.");
                    }
                }
            }

            if (!projectCompanyId.HasValue)
            {
                if (isSuperAdmin && !requestedCompanyId.HasValue)
                {
                    return (0, null);
                }

                return (null, "Company is required to create a project. Please select a company or choose sessions from one company.");
            }

            return (projectCompanyId, null);
        }

        [HttpPost, Route("CreateProjectWithPolygons")]

public async Task<JsonResult> CreateProjectWithPolygons([FromBody] CreateProjectModel model)
{
    var message = new ReturnAPIResponse();
    int newProjectId = 0; 

    if (model == null || string.IsNullOrWhiteSpace(model.ProjectName))
    {
        return Json(new { Status = 0, Message = "Project Name is required." });
    }

    var companyResolution = await ResolveProjectCompanyIdAsync(model);
    if (!companyResolution.CompanyId.HasValue)
    {
        return Json(new { Status = 0, Message = companyResolution.Error });
    }

    // 2. Create the Execution Strategy
    var strategy = db.Database.CreateExecutionStrategy();

    try
    {
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                var newProj = new tbl_project
                {
                    project_name   = model.ProjectName,
                    company_id     = companyResolution.CompanyId.Value,
                    provider       = model.Provider,
                    tech           = model.Tech,
                    band           = model.Band,
                    earfcn         = model.EarFcn,
                    apps           = model.Apps,
                    from_date      = model.FromDate?.ToString("yyyy-MM-dd"),
                    to_date        = model.ToDate?.ToString("yyyy-MM-dd"),
                    created_on     = DateTime.UtcNow,
                    status         = 1,
                    ref_session_id = (model.SessionIds != null && model.SessionIds.Any())
                                        ? string.Join(",", model.SessionIds)
                                        : null,
                    grid_size      = model.GridSize
                };

                db.tbl_project.Add(newProj);
                await db.SaveChangesAsync();

                newProjectId = newProj.id; 
                await TryUpdateProjectLogGridAsync(newProjectId, model.LogGrid ?? model.log_grid);
                await TryUpdateProjectSiteSizeAsync(newProjectId, 1m);

                if (model.PolygonIds != null && model.PolygonIds.Any())
                {
                    var polygons = await db.map_regions
                                           .Where(p => model.PolygonIds.Contains(p.id))
                                           .ToListAsync();

                    foreach (var p in polygons)
                    {
                        p.tbl_project_id = newProj.id;
                    }

                    await db.SaveChangesAsync();
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw; 
            }
        });

        // Fetch the created project data 
        var createdProjectData = await db.tbl_project
            .AsNoTracking()
            .Where(p => p.id == newProjectId)
            .Select(p => new
            {
                p.id,
                p.project_name,
                p.ref_session_id,
                p.from_date,
                p.to_date,
                p.provider,
                p.tech,
                p.band,
                p.earfcn,
                p.apps,
                p.grid_size,
                p.created_on,
                p.status
            })
            .FirstOrDefaultAsync();

        message.Status  = 1;
        message.Message = "Project created successfully.";
        message.Data    = new
        {
            projectId = newProjectId,
            project   = createdProjectData
        };

        await InvalidateProjectListCachesAsync();
    }
    catch (Exception ex)
    {
        // SURFACING THE REAL ERROR:
        // EF Core hides DB constraints in the InnerException. This will print the actual SQL error.
        string actualError = SafeException.GetInnermost(ex);
        
        message.Status  = 0;
        message.Message = "Error creating project: " + actualError;
        message.Data    = null;
    }

    return Json(message);
}

        [HttpPost, Route("AssignPolygonToProject")]
        public async Task<JsonResult> AssignPolygonToProject(int polygonId, int projectId)
        {
            var message = new ReturnAPIResponse();
            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                await using (var existsCmd = conn.CreateCommand())
                {
                    existsCmd.CommandText = "SELECT COUNT(1) FROM map_regions WHERE id = @polygonId;";
                    AddParam(existsCmd, "@polygonId", polygonId);
                    var existsRaw = await existsCmd.ExecuteScalarAsync();
                    var exists = existsRaw != null && existsRaw != DBNull.Value && Convert.ToInt32(existsRaw) > 0;
                    if (!exists)
                    {
                        message.Status = 0;
                        message.Message = "Polygon not found.";
                        return Json(message);
                    }
                }

                await using (var updateCmd = conn.CreateCommand())
                {
                    updateCmd.CommandText = "UPDATE map_regions SET tbl_project_id = @projectId WHERE id = @polygonId;";
                    AddParam(updateCmd, "@projectId", projectId);
                    AddParam(updateCmd, "@polygonId", polygonId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await InvalidateMapViewCachesAsync();

                message.Status = 1;
                message.Message = "Polygon linked to project.";
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = "Error: " + SafeException.Get(ex);
            }
            return Json(message);
        }

        // =========================================================
        // =============== Logs: list/filter endpoints =============
        // =========================================================

        public class MapFilter
{
    public int session_id { get; set; }
    public string? NetworkType { get; set; } // This now refers to Provider
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int page { get; set; } = 1;
    public int limit { get; set; } = 50000;
}

public class LogFilterModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public TimeSpan? StartTime { get; set; }   // Time picker (HH:mm or HH:mm:ss)
    public TimeSpan? EndTime { get; set; } 
    public string? Provider { get; set; }
    public int? PolygonId { get; set; }
    public DateTime? CursorTs { get; set; }
}

// App list


// App list
private static readonly string[] BaseApps =
{
    "Whatsapp","Instagram","YT","Google Chrome","Google Search","FB",
    "Gmail","Outlook","Spotify","Blinkit","Jio Hotstar","Netflix","Amazon Prime"
};

private sealed class AppAgg
{
    public string AppName { get; }
    public int SampleCount { get; set; }

    public double RsrpSum { get; set; }
    public int RsrpCnt { get; set; }

    public double RsrqSum { get; set; }
    public int RsrqCnt { get; set; }

    public double SinrSum { get; set; }
    public int SinrCnt { get; set; }

    public double MosSum { get; set; }
    public int MosCnt { get; set; }

    // Throughput
    public double DlSum { get; set; }
    public int DlCnt { get; set; }

    public double UlSum { get; set; }
    public int UlCnt { get; set; }

    // Duration tracking
    public DateTime? PreviousTimestamp { get; set; }
    public int ActiveDurationSeconds { get; set; }
    public DateTime? FirstTimestamp { get; set; }
    public DateTime? LastTimestamp { get; set; }

    public AppAgg(string name) { AppName = name; }

    // This was the missing method causing your error
    public void AddSample(DateTime ts, double? rsrp, double? rsrq, double? sinr, double? mos, string dl_tpt, string ul_tpt)
    {
        // 1. Accumulate Signal KPIs
        if (rsrp.HasValue) { RsrpSum += rsrp.Value; RsrpCnt++; }
        if (rsrq.HasValue) { RsrqSum += rsrq.Value; RsrqCnt++; }
        if (sinr.HasValue) { SinrSum += sinr.Value; SinrCnt++; }
        if (mos.HasValue)  { MosSum += mos.Value;  MosCnt++; }

        // 2. Accumulate Throughput (Convert string to double safely)
        if (double.TryParse(dl_tpt, NumberStyles.Any, CultureInfo.InvariantCulture, out var dlVal))
        {
            DlSum += dlVal; DlCnt++;
        }
        if (double.TryParse(ul_tpt, NumberStyles.Any, CultureInfo.InvariantCulture, out var ulVal))
        {
            UlSum += ulVal; UlCnt++;
        }

        // 3. Track Duration
        if (!FirstTimestamp.HasValue) FirstTimestamp = ts;
        LastTimestamp = ts;

        if (PreviousTimestamp.HasValue)
        {
            var gap = (ts - PreviousTimestamp.Value).TotalSeconds;
            // Only count duration if samples are close enough (e.g., < 300 seconds)
            if (gap > 0 && gap <= 300) // 300 matches the 'MaxGapSeconds' passed in calls
            {
                ActiveDurationSeconds += (int)gap;
            }
        }
        PreviousTimestamp = ts;
    }

    // This method is used to finalize the result for the JSON response
    public object ToResult(int maxGapSeconds)
    {
        return new AppSummaryResult
        {
            appName = AppName,
            SampleCount = SampleCount,
            avgRsrp = RsrpCnt > 0 ? Math.Round(RsrpSum / RsrpCnt, 2) : 0,
            avgRsrq = RsrqCnt > 0 ? Math.Round(RsrqSum / RsrqCnt, 2) : 0,
            avgSinr = SinrCnt > 0 ? Math.Round(SinrSum / SinrCnt, 2) : 0,
            avgMos = MosCnt > 0 ? Math.Round(MosSum / MosCnt, 2) : 0,
            avgDl = DlCnt > 0 ? Math.Round(DlSum / DlCnt, 2) : 0,
            avgUl = UlCnt > 0 ? Math.Round(UlSum / UlCnt, 2) : 0,
            durationSeconds = ActiveDurationSeconds,
            durationHHMMSS = TimeSpan.FromSeconds(ActiveDurationSeconds).ToString(@"hh\:mm\:ss")
        };
    }
}
// apps string se base app ka naam
private static string? DetectAppName(string? apps)
{
    if (string.IsNullOrWhiteSpace(apps))
        return null;

    foreach (var app in BaseApps)
    {
        if (apps.IndexOf(app, StringComparison.OrdinalIgnoreCase) >= 0)
            return app;
    }

    return null;
}

// Generic: koi bhi type ho (string / float / double / decimal / int ...)
// use double? me convert karo
private static double? AsDouble(object? value)
{
    if (value == null)
        return null;

    switch (value)
    {
        case double d:
            return d;
        case float f:
            return f;
        case decimal m:
            return (double)m;
        case int i:
            return i;
        case long l:
            return l;
        case string s:
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
                return dv;
            return null;
        default:
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
    }
}


public class MapFilter1
{
    public string session_ids { get; set; }
    public int page { get; set; } = 1;
    public int limit { get; set; } = 20000;
    public string NetworkType { get; set; } = "ALL";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public long? project_id { get; set; }

    public List<long> GetSessionIds()
    {
        if (string.IsNullOrWhiteSpace(session_ids))
            return new List<long>();

        return session_ids
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s.Trim(), out long id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }
}
[HttpGet, Route("GetNetworkLog")]
public async Task<JsonResult> GetNetworkLog([FromQuery] MapFilter1 filters)
{
    var totalStopwatch = Stopwatch.StartNew();
    var sessionIds = filters?.GetSessionIds() ?? new List<long>();

    if (sessionIds.Count == 0)
        return Json(new { message = "No valid session IDs provided", data = new List<object>() });

    try
    {
        var limit = Math.Min(Math.Max(filters.limit, 1), 50000);
        var page = filters.page <= 0 ? 1 : filters.page;
        var pageOffset = (page - 1) * limit;
        string connString = db.Database.GetConnectionString();
        string providerNormalized = null;
        await InvalidateObsoleteNetworkLogCachesAsync();
        await EnsureNetworkLogUpdatedAtColumnAsync(connString);
        string dataVersion = await GetNetworkLogDataVersionAsync(
            connString,
            sessionIds,
            providerNormalized,
            filters
        );

        // 1. Prepare Cache Key
        string cacheKey = BuildNetworkLogCacheKey(
            sessionIds,
            providerNormalized,
            filters.StartDate,
            filters.EndDate,
            filters.project_id,
            dataVersion
        );
        string pageCacheKey = $"{cacheKey}:page:{page}:limit:{limit}";

        // 2. Check Cache
        if (_redis != null && _redis.IsConnected)
        {
            try 
            {
                var cachedPage = await _redis.GetObjectAsync<NetworkLogFullResponse>(pageCacheKey);
                if (cachedPage != null)
                {
                    Response.Headers["X-Cache"] = "HIT";
                    return Json(new { 
                        data = cachedPage.data, 
                        app_summary = cachedPage.app_summary, 
                        io_summary = cachedPage.io_summary, 
                        tpt_volume = cachedPage.tpt_volume,
                        total_count = cachedPage.TotalCount, 
                        session_count = sessionIds.Count,
                        sessions = cachedPage.Sessions, 
                        cachedAt = cachedPage.CachedAt 
                    });
                }
            }
            catch {}

            try
            {
                var cached = await _redis.GetObjectAsync<NetworkLogFullResponse>(cacheKey);
                if (cached != null)
                {
                    var cachedPage = SliceCachedPage(cached.data ?? new List<NetworkLogCacheRow>(), limit, pageOffset);
                    var cachedPageResponse = new NetworkLogFullResponse
                    {
                        data = cachedPage,
                        app_summary = cached.app_summary,
                        io_summary = cached.io_summary,
                        tpt_volume = cached.tpt_volume,
                        TotalCount = cached.TotalCount,
                        Sessions = cached.Sessions,
                        CachedAt = cached.CachedAt
                    };
                    await SetMapViewCacheAsync(pageCacheKey, cachedPageResponse);
                    Response.Headers["X-Cache"] = "HIT";
                    return Json(new { 
                        data = cachedPage, 
                        app_summary = cached.app_summary, 
                        io_summary = cached.io_summary, 
                        tpt_volume = cached.tpt_volume,
                        total_count = cached.TotalCount, 
                        session_count = sessionIds.Count,
                        sessions = cached.Sessions, 
                        cachedAt = cached.CachedAt 
                    });
                }
            }
            catch {}
        }

        async Task<(List<NetworkLogCacheRow> fullData, Dictionary<string, object> appSummary, CombinedStatsDto stats)> FetchBundleAsync(MapFilter1 activeFilters)
        {
            var taskData = GetMainDataOnlyRaw(connString, sessionIds, providerNormalized, activeFilters);
            var taskApps = GetAppSummaryRaw(connString, sessionIds, providerNormalized, activeFilters);
            var taskStats = GetCombinedStatsRaw(connString, sessionIds, providerNormalized, activeFilters);
            await Task.WhenAll(taskData, taskApps, taskStats);
            return (taskData.Result, taskApps.Result, taskStats.Result);
        }

        // Primary fetch: includes project polygon filter when available.
        var bundle = await FetchBundleAsync(filters);

        // Fallback: if project polygon filtering returned no rows, retry without project polygon constraint.
        if (bundle.fullData.Count == 0 && filters.project_id.HasValue && filters.project_id.Value > 0)
        {
            var fallbackFilters = new MapFilter1
            {
                session_ids = filters.session_ids,
                page = filters.page,
                limit = filters.limit,
                NetworkType = filters.NetworkType,
                StartDate = filters.StartDate,
                EndDate = filters.EndDate,
                project_id = null
            };
            bundle = await FetchBundleAsync(fallbackFilters);
        }

        long calculatedTotalCount = bundle.stats.Sessions.Sum(x => x.Count);
        var fullData = bundle.fullData;
        var pageData = SliceCachedPage(fullData, limit, pageOffset);

        // =========================================================================
        // ðŸ“¦ PACKAGE RESPONSE
        // =========================================================================
        var responseObj = new
        {
            data = pageData,
            app_summary = bundle.appSummary,
            io_summary = bundle.stats.IoSummary,
            tpt_volume = bundle.stats.Volume,
            total_count = calculatedTotalCount,
            session_count = sessionIds.Count,
            sessions = bundle.stats.Sessions
        };

        // Cache Logic
        if (_redis != null && _redis.IsConnected)
        {
             var cacheModel = new NetworkLogFullResponse {
                 data = fullData,
                 app_summary = bundle.appSummary,
                 io_summary = bundle.stats.IoSummary,
                 tpt_volume = bundle.stats.Volume,
                 TotalCount = (int)calculatedTotalCount,
                 Sessions = bundle.stats.Sessions,
                 CachedAt = DateTime.UtcNow
             };
             await _redis.SetObjectAsync(cacheKey, cacheModel, ttlSeconds: 300);
             await _redis.SetObjectAsync(pageCacheKey, new NetworkLogFullResponse
             {
                 data = pageData,
                 app_summary = bundle.appSummary,
                 io_summary = bundle.stats.IoSummary,
                 tpt_volume = bundle.stats.Volume,
                 TotalCount = (int)calculatedTotalCount,
                 Sessions = bundle.stats.Sessions,
                 CachedAt = cacheModel.CachedAt
             }, ttlSeconds: 300);
        }

        totalStopwatch.Stop();
        Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();

        return Json(responseObj);
    }
    catch (Exception ex)
    {
        return Json(new { message = "Server Error", details = SafeException.Get(ex) });
    }
}

// ---------------------------------------------------------
// 1ï¸âƒ£ MAIN DATA (Full filtered fetch via EF Core)
// ---------------------------------------------------------
private async Task<List<NetworkLogCacheRow>> GetMainDataOnlyEF(
    List<long> sessionIds, string provider, MapFilter1 filters)
{
    IQueryable<tbl_network_log> query = db.tbl_network_log.AsNoTracking()
        .Where(log => sessionIds.Contains((long)log.session_id));

    query = query.Where(log =>
        (log.m_alpha_short != null && log.m_alpha_short.Trim() != "") ||
        (log.m_alpha_long != null && log.m_alpha_long.Trim() != ""));

    if (!string.IsNullOrEmpty(provider))
        query = query.Where(log =>
            ((log.m_alpha_short != null && log.m_alpha_short != "" ? log.m_alpha_short : log.m_alpha_long) ?? "")
                .ToLower()
                .Contains(provider));

    var wifiPredicate = (System.Linq.Expressions.Expression<Func<tbl_network_log, bool>>)(log =>
        log.primary_cell_info_1 != null &&
        (log.primary_cell_info_1.StartsWith("SSID:") || log.primary_cell_info_1.Contains("BSSID:")));
    var registeredCellPredicate = (System.Linq.Expressions.Expression<Func<tbl_network_log, bool>>)(log =>
        log.primary_cell_info_1 != null &&
        log.primary_cell_info_1.Contains("mRegistered=YES") &&
        log.rsrp >= -140 && log.rsrp <= -44 &&
        log.rsrq >= -34 && log.rsrq <= -3 &&
        log.sinr >= -20 && log.sinr <= 40 &&
        log.mos >= 1 && log.mos <= 5);
    var fiveGCellPredicate = (System.Linq.Expressions.Expression<Func<tbl_network_log, bool>>)(log =>
        (
            (log.network ?? "") + " " +
            (log.band ?? "") + " " +
            (log.primary_cell_info_1 ?? "") + " " +
            (log.all_neigbor_cell_info ?? "")
        ).ToUpper().Contains("5G") ||
        (
            (log.network ?? "") + " " +
            (log.band ?? "") + " " +
            (log.primary_cell_info_1 ?? "") + " " +
            (log.all_neigbor_cell_info ?? "")
        ).ToUpper().Contains("NRARFCN") ||
        (
            (log.network ?? "") + " " +
            (log.band ?? "") + " " +
            (log.primary_cell_info_1 ?? "") + " " +
            (log.all_neigbor_cell_info ?? "")
        ).ToUpper().Contains("MNR") ||
        (
            (log.network ?? "") + " " +
            (log.band ?? "") + " " +
            (log.primary_cell_info_1 ?? "") + " " +
            (log.all_neigbor_cell_info ?? "")
        ).ToUpper().Contains("NCI"));

    // network type (4G/5G/etc) filtering
    if (!string.IsNullOrWhiteSpace(filters.NetworkType) &&
        !filters.NetworkType.Equals("All", StringComparison.OrdinalIgnoreCase))
    {
        var nt = filters.NetworkType.Trim();
        if (nt.Equals("wifi", StringComparison.OrdinalIgnoreCase) ||
            nt.Equals("wi-fi", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(wifiPredicate);
        }
        else
        {
            if (nt.Equals("5g", StringComparison.OrdinalIgnoreCase) ||
                nt.Equals("5g nsa", StringComparison.OrdinalIgnoreCase) ||
                nt.Equals("nr", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(fiveGCellPredicate);
            }
            else
            {
                query = query
                    .Where(registeredCellPredicate)
                    .Where(log => log.network != null && EF.Functions.Like(log.network, $"%{nt}%"));
            }
        }
    }
    else
    {
        query = query.Where(log =>
            (
                log.primary_cell_info_1 != null &&
                log.primary_cell_info_1.Contains("mRegistered=YES") &&
                log.rsrp >= -140 && log.rsrp <= -44 &&
                log.rsrq >= -34 && log.rsrq <= -3 &&
                log.sinr >= -20 && log.sinr <= 40 &&
                log.mos >= 1 && log.mos <= 5
            ) ||
            (
                log.primary_cell_info_1 != null &&
                (log.primary_cell_info_1.StartsWith("SSID:") || log.primary_cell_info_1.Contains("BSSID:"))
            ) ||
            (
                (((log.network ?? "") + " " +
                  (log.band ?? "") + " " +
                  (log.primary_cell_info_1 ?? "") + " " +
                  (log.all_neigbor_cell_info ?? "")).ToUpper().Contains("5G")) ||
                (((log.network ?? "") + " " +
                  (log.band ?? "") + " " +
                  (log.primary_cell_info_1 ?? "") + " " +
                  (log.all_neigbor_cell_info ?? "")).ToUpper().Contains("NRARFCN")) ||
                (((log.network ?? "") + " " +
                  (log.band ?? "") + " " +
                  (log.primary_cell_info_1 ?? "") + " " +
                  (log.all_neigbor_cell_info ?? "")).ToUpper().Contains("MNR")) ||
                (((log.network ?? "") + " " +
                  (log.band ?? "") + " " +
                  (log.primary_cell_info_1 ?? "") + " " +
                  (log.all_neigbor_cell_info ?? "")).ToUpper().Contains("NCI"))
            ));
    }

    DateTime? from = filters.StartDate;
    DateTime? to = filters.EndDate?.AddDays(1);
    if (from.HasValue) query = query.Where(log => log.timestamp >= from.Value);
    if (to.HasValue) query = query.Where(log => log.timestamp < to.Value);

    var rows = await query.OrderBy(log => log.timestamp)
        .Select(log => new NetworkLogCacheRow
        {
            id = log.id,
            session_id = log.session_id ?? 0,
            timestamp = log.timestamp,
            lat = log.lat,
            lon = log.lon,
            battery = log.battery,
            Speed = log.Speed,
            level = log.level,
            apps = log.apps ?? "",
            num_cells = log.num_cells,
            network = log.network ?? "",
            m_alpha_long = log.m_alpha_short != null && log.m_alpha_short != ""
                ? log.m_alpha_short
                : (log.m_alpha_long ?? ""),
            pci = log.pci ?? "",
            rssi = log.rssi,
            rsrp = log.rsrp,
            rsrq = log.rsrq,
            sinr = log.sinr,
            mos = log.mos,
            jitter = log.jitter,
            latency = log.latency,
            tac = log.tac,
            packet_loss = log.packet_loss,
            dl_tpt = log.dl_tpt ?? "0",
            ul_tpt = log.ul_tpt ?? "0",
            band = log.band ?? "",
            image_path = log.image_path ?? "",
            indoor_outdoor = log.indoor_outdoor ?? "",
            nodeb_id = log.nodeb_id ?? "",
            cell_id = log.cell_id ?? "",
            connection_type = log.primary_cell_info_1 != null &&
                (log.primary_cell_info_1.StartsWith("SSID:") || log.primary_cell_info_1.Contains("BSSID:"))
                    ? "wifi"
                    : "network"
        })
        .ToListAsync();

    NormalizeNetworkLogRows(rows);
    return rows;
}

private async Task<List<NetworkLogCacheRow>> GetMainDataOnlyRaw(
    string connString, List<long> sessionIds, string provider, MapFilter1 filters)
{
    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();

    using var cmdConfig = new MySqlCommand("SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", conn);
    await cmdConfig.ExecuteNonQueryAsync();

    var (whereClause, parameters) = await BuildSqlWhereAsync(sessionIds, provider, filters);
    string sql = $@"
        SELECT
            id, session_id, timestamp, lat, lon, battery, Speed, level, apps, num_cells,
            network,
            COALESCE(
                NULLIF(TRIM(BOTH CHAR(39) FROM TRIM(BOTH '""' FROM TRIM(m_alpha_short))), ''),
                TRIM(BOTH CHAR(39) FROM TRIM(BOTH '""' FROM TRIM(m_alpha_long)))
            ) AS provider_name,
            pci, rssi, rsrp, rsrq, sinr, mos, jitter, latency, tac,
            packet_loss, dl_tpt, ul_tpt, band, image_path, indoor_outdoor, nodeb_id, cell_id,
            CASE
                WHEN primary_cell_info_1 LIKE 'SSID:%'
                  OR primary_cell_info_1 LIKE '%BSSID:%'
                  OR EXISTS (
                      SELECT 1
                      FROM tbl_session s
                      WHERE s.id = tbl_network_log.session_id
                        AND LOWER(COALESCE(s.type, '')) = 'wifi'
                  )
                THEN 'wifi'
                ELSE 'network'
            END AS connection_type
        FROM tbl_network_log
        WHERE {whereClause}
        ORDER BY timestamp;";

    var rows = new List<NetworkLogCacheRow>();
    using var cmd = new MySqlCommand(sql, conn);
    foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);

    using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        rows.Add(new NetworkLogCacheRow
        {
            id = rd.IsDBNull(0) ? 0 : rd.GetInt32(0),
            session_id = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetInt64(1)),
            timestamp = rd.IsDBNull(2) ? (DateTime?)null : rd.GetDateTime(2),
            lat = rd.IsDBNull(3) ? (float?)null : rd.GetFloat(3),
            lon = rd.IsDBNull(4) ? (float?)null : rd.GetFloat(4),
            battery = rd.IsDBNull(5) ? (int?)null : rd.GetInt32(5),
            Speed = rd.IsDBNull(6) ? (float?)null : rd.GetFloat(6),
            level = rd.IsDBNull(7) ? (int?)null : rd.GetInt32(7),
            apps = rd.IsDBNull(8) ? "" : rd.GetString(8),
            num_cells = rd.IsDBNull(9) ? (int?)null : rd.GetInt32(9),
            network = rd.IsDBNull(10) ? "" : rd.GetString(10),
            m_alpha_long = rd.IsDBNull(11) ? "" : CleanProviderDisplayName(rd.GetString(11)),
            pci = rd.IsDBNull(12) ? "" : rd.GetString(12),
            rssi = rd.IsDBNull(13) ? (float?)null : rd.GetFloat(13),
            rsrp = rd.IsDBNull(14) ? (float?)null : rd.GetFloat(14),
            rsrq = rd.IsDBNull(15) ? (float?)null : rd.GetFloat(15),
            sinr = rd.IsDBNull(16) ? (float?)null : rd.GetFloat(16),
            mos = rd.IsDBNull(17) ? (float?)null : rd.GetFloat(17),
            jitter = rd.IsDBNull(18) ? (float?)null : rd.GetFloat(18),
            latency = rd.IsDBNull(19) ? (float?)null : rd.GetFloat(19),
            tac = rd.IsDBNull(20) ? "" : Convert.ToString(rd.GetValue(20), CultureInfo.InvariantCulture) ?? "",
            packet_loss = rd.IsDBNull(21) ? (float?)null : rd.GetFloat(21),
            dl_tpt = rd.IsDBNull(22) ? "0" : Convert.ToString(rd.GetValue(22), CultureInfo.InvariantCulture) ?? "0",
            ul_tpt = rd.IsDBNull(23) ? "0" : Convert.ToString(rd.GetValue(23), CultureInfo.InvariantCulture) ?? "0",
            band = rd.IsDBNull(24) ? "" : rd.GetString(24),
            image_path = rd.IsDBNull(25) ? "" : rd.GetString(25),
            indoor_outdoor = rd.IsDBNull(26) ? "" : rd.GetString(26),
            nodeb_id = rd.IsDBNull(27) ? "" : rd.GetString(27),
            cell_id = rd.IsDBNull(28) ? "" : rd.GetString(28),
            connection_type = rd.IsDBNull(29) ? "network" : rd.GetString(29)
        });
    }

    NormalizeNetworkLogRows(rows);
    return rows;
}
// ---------------------------------------------------------
//  APP SUMMARY (Updated with Operator Name)
// ---------------------------------------------------------
// ---------------------------------------------------------
//  APP SUMMARY (Fixed: Shows Multiple Operators)
// ---------------------------------------------------------
private async Task<Dictionary<string, object>> GetAppSummaryRaw(
    string connString, List<long> sessionIds, string provider, MapFilter1 filters)
{
    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();
    
    // Performance: Dirty reads are faster for analytics
    using var cmdConfig = new MySqlCommand("SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", conn);
    await cmdConfig.ExecuteNonQueryAsync();

    var (whereClause, parameters) = await BuildSqlWhereAsync(sessionIds, provider, filters);

    // List of apps to track
    var baseApps = new[] { "Whatsapp", "Instagram", "YT", "Google Chrome", "Google Search", "FB", "Gmail", "Outlook", "Spotify", "Blinkit", "Jio Hotstar","Netflix", "Amazon Prime ", };
    
    var caseBuilder = new StringBuilder();
    foreach (var app in baseApps)
    {
        string pName = $"@app_{Math.Abs(app.GetHashCode())}";
        // Case-insensitive matching
        caseBuilder.Append($" WHEN LOWER(apps) LIKE {pName} THEN '{app}' ");
        if (!parameters.ContainsKey(pName)) parameters.Add(pName, $"%{app.ToLower()}%");
    }

    // âš¡ UPDATED QUERY: Uses GROUP_CONCAT to show ALL operators
    string sql = $@"
        SELECT 
            -- 0. App Name
            CASE {caseBuilder} ELSE 'Other' END as DetectedApp,
            
            -- 1. Count
            COUNT(*) as sample_count,
            
            -- 2-8. Averages
            AVG(rsrp), AVG(rsrq), AVG(sinr), AVG(mos), AVG(jitter), AVG(latency), AVG(packet_loss),
            
            -- 9-10. Throughput (Ignoring 0s)
            AVG(NULLIF(dl_tpt + 0, 0)), 
            AVG(NULLIF(ul_tpt + 0, 0)),
            
            -- 11-12. Time
            MAX(timestamp), MIN(timestamp),
            
            -- 13. OPERATOR LIST (The Fix)
            -- This combines all distinct operators into one string like 'Jio, Airtel'
            GROUP_CONCAT(
                DISTINCT NULLIF(COALESCE(NULLIF(TRIM(m_alpha_short), ''), m_alpha_long), '')
                ORDER BY COALESCE(NULLIF(TRIM(m_alpha_short), ''), m_alpha_long)
                SEPARATOR ', '
            )

        FROM tbl_network_log
        WHERE {whereClause} 
          AND apps IS NOT NULL 
          AND apps != '' 
          AND apps != '0'
        GROUP BY DetectedApp
        HAVING DetectedApp != 'Other'";

    var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    
    using var cmd = new MySqlCommand(sql, conn);
    foreach(var p in parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);

    using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        string appName = rd.GetString(0); // Index 0: DetectedApp
        
        // Calculate Duration
        DateTime maxTs = rd.GetDateTime(11);
        DateTime minTs = rd.GetDateTime(12);
        TimeSpan span = maxTs - minTs;
        int duration = (int)span.TotalSeconds;
        if (duration == 0 && rd.GetInt32(1) > 0) duration = 1; 

        // Read the Multi-Operator String (Index 13)
        string opNames = rd.IsDBNull(13) ? "Unknown" : rd.GetString(13);

        result[appName] = new 
        { 
            appName,
            operatorName = opNames, // Now returns "Jio, Airtel" etc.
            sampleCount = rd.GetInt32(1),
            avgRsrp = Math.Round(rd.IsDBNull(2) ? 0 : rd.GetDouble(2), 2),
            avgRsrq = Math.Round(rd.IsDBNull(3) ? 0 : rd.GetDouble(3), 2),
            avgSinr = Math.Round(rd.IsDBNull(4) ? 0 : rd.GetDouble(4), 2),
            avgMos = Math.Round(rd.IsDBNull(5) ? 0 : rd.GetDouble(5), 2),
            avgJitter = Math.Round(rd.IsDBNull(6) ? 0 : rd.GetDouble(6), 2),
            avgLatency = Math.Round(rd.IsDBNull(7) ? 0 : rd.GetDouble(7), 2),
            avgPacketLoss = Math.Round(rd.IsDBNull(8) ? 0 : rd.GetDouble(8), 2),
            avgDlTptMbps = Math.Round(rd.IsDBNull(9) ? 0 : rd.GetDouble(9), 2),
            avgUlTptMbps = Math.Round(rd.IsDBNull(10) ? 0 : rd.GetDouble(10), 2),
            firstUsedAt = minTs,
            lastUsedAt = maxTs,
            durationSeconds = duration,
            durationHHMMSS = span.ToString(@"hh\:mm\:ss")
        };
    }
    return result;
}
//---------------------------------------------------
// 3ï¸âƒ£ COMBINED STATS (Volume + IO + Sessions in ONE Query)
// ---------------------------------------------------------
private async Task<CombinedStatsDto> GetCombinedStatsRaw(
    string connString, List<long> sessionIds, string provider, MapFilter1 filters)
{
    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();
    
    // Performance: Dirty reads are faster
    using var cmdConfig = new MySqlCommand("SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", conn);
    await cmdConfig.ExecuteNonQueryAsync();

    var (whereClause, parameters) = await BuildSqlWhereAsync(sessionIds, provider, filters);

    // Multi-Statement Query
    string sql = $@"
        -- 1. Volume
        SELECT MAX(total_rx_kb + 0) - MIN(total_rx_kb + 0), MAX(total_tx_kb + 0) - MIN(total_tx_kb + 0) 
        FROM tbl_network_log WHERE {whereClause};

        -- 2. IO Summary
        SELECT indoor_outdoor, COUNT(CASE WHEN dl_tpt IS NOT NULL AND dl_tpt != '' THEN 1 END), COUNT(CASE WHEN ul_tpt IS NOT NULL AND ul_tpt != '' THEN 1 END) 
        FROM tbl_network_log WHERE {whereClause} GROUP BY indoor_outdoor;

        -- 3. Session Counts
        SELECT session_id, COUNT(*) 
        FROM tbl_network_log WHERE {whereClause} GROUP BY session_id;";

    var stats = new CombinedStatsDto();
    
    using var cmd = new MySqlCommand(sql, conn);
    foreach(var p in parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);

    using var rd = await cmd.ExecuteReaderAsync();

    // Read 1: Volume
    if (await rd.ReadAsync())
    {
        stats.Volume = new { 
            dl_kb = rd.IsDBNull(0) ? 0 : rd.GetDouble(0), 
            ul_kb = rd.IsDBNull(1) ? 0 : rd.GetDouble(1) 
        };
    }

    // Read 2: IO Summary
    await rd.NextResultAsync();
    while (await rd.ReadAsync())
    {
        string env = rd.IsDBNull(0) ? "Unknown" : rd.GetString(0);
        stats.IoSummary[env] = new { inputCount = rd.GetInt32(1), outputCount = rd.GetInt32(2) };
    }

    // Read 3: Session Counts
    await rd.NextResultAsync();
    while (await rd.ReadAsync())
    {
        stats.Sessions.Add(new SessionCountDto { SessionId = rd.GetInt64(0), Count = rd.GetInt32(1) });
    }

    return stats;
}

// ---------------------------------------------------------
// HELPERS
// ---------------------------------------------------------
private async Task<string?> ResolveProjectFilterWktAsync(long? projectId)
{
    if (!projectId.HasValue || projectId.Value <= 0)
        return null;

    string connString = db.Database.GetConnectionString();
    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
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
    cmd.Parameters.AddWithValue("@pid", projectId.Value);

    var result = await cmd.ExecuteScalarAsync();
    var wkt = result == null || result == DBNull.Value
        ? null
        : Convert.ToString(result, CultureInfo.InvariantCulture);

    return string.IsNullOrWhiteSpace(wkt) ? null : wkt.Trim();
}

private async Task<(string Clause, Dictionary<string, object> Params)> BuildSqlWhereAsync(
    List<long> ids, string provider, MapFilter1 filters)
{
    var (clause, p) = BuildSqlWhere(ids, provider, filters);

    var projectPolygonWkt = await ResolveProjectFilterWktAsync(filters?.project_id);
    if (!string.IsNullOrWhiteSpace(projectPolygonWkt))
    {
        clause += " AND lat IS NOT NULL AND lon IS NOT NULL";
        clause += " AND ST_Contains(ST_GeomFromText(@projectPolygonWkt, 4326), ST_SRID(POINT(lon, lat), 4326))";
        p["@projectPolygonWkt"] = projectPolygonWkt;
    }

    return (clause, p);
}

private (string Clause, Dictionary<string, object> Params) BuildSqlWhere(
    List<long> ids, string provider, MapFilter1 filters)
{
    var p = new Dictionary<string, object>();
    
    // ORDER MATTERS FOR INDEX USE: Filter by ID first!
    var idParams = new List<string>();
    for(int i=0; i<ids.Count; i++) { 
        string pname = $"@sid{i}"; 
        idParams.Add(pname); 
        p.Add(pname, ids[i]); 
    }
    
    var clauses = new List<string>();
    if (idParams.Any()) clauses.Add($"session_id IN ({string.Join(",", idParams)})");
    else clauses.Add("1 = 0"); 
    clauses.Add("COALESCE(NULLIF(TRIM(m_alpha_short), ''), NULLIF(TRIM(m_alpha_long), '')) IS NOT NULL");

    // Move Wildcard search to the end so it runs on smaller dataset
    if(!string.IsNullOrEmpty(provider)) {
        clauses.Add("COALESCE(NULLIF(TRIM(m_alpha_short), ''), m_alpha_long) LIKE @prov");
        p.Add("@prov", $"%{provider}%");
    }

    const string wifiPredicate = @"(
        primary_cell_info_1 LIKE 'SSID:%'
        OR primary_cell_info_1 LIKE '%BSSID:%'
        OR EXISTS (
            SELECT 1
            FROM tbl_session s
            WHERE s.id = session_id
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

    if (!string.IsNullOrWhiteSpace(filters?.NetworkType) &&
        !filters.NetworkType.Equals("All", StringComparison.OrdinalIgnoreCase))
    {
        var networkType = filters.NetworkType.Trim();
        if (networkType.Equals("wifi", StringComparison.OrdinalIgnoreCase) ||
            networkType.Equals("wi-fi", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add(wifiPredicate);
        }
        else
        {
            if (networkType.Equals("5g", StringComparison.OrdinalIgnoreCase) ||
                networkType.Equals("5g nsa", StringComparison.OrdinalIgnoreCase) ||
                networkType.Equals("nr", StringComparison.OrdinalIgnoreCase))
            {
                clauses.Add(fiveGCellPredicate);
            }
            else
            {
                clauses.Add("network IS NOT NULL AND network LIKE @networkType");
                p.Add("@networkType", $"%{networkType}%");
                clauses.Add(registeredCellPredicate);
            }
        }
    }
    else
    {
        clauses.Add($"({registeredCellPredicate} OR {wifiPredicate} OR {fiveGCellPredicate})");
    }

    if(filters.StartDate.HasValue) {
        clauses.Add("timestamp >= @from");
        p.Add("@from", filters.StartDate);
    }
    if(filters.EndDate.HasValue) {
        clauses.Add("timestamp < @to");
        p.Add("@to", filters.EndDate.Value.AddDays(1));
    }
    return (string.Join(" AND ", clauses), p);
}

// Data Transfer Objects
public class CombinedStatsDto
{
    public object Volume { get; set; } = new { dl_kb = 0.0, ul_kb = 0.0 };
    public Dictionary<string, object> IoSummary { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public List<SessionCountDto> Sessions { get; set; } = new List<SessionCountDto>();
}

public class SessionCountDto
{
    public long SessionId { get; set; }
    public int Count { get; set; }
}

private async Task EnsureNetworkLogUpdatedAtColumnAsync(string connString)
{
    if (NetworkLogUpdatedAtColumnEnsured || string.IsNullOrWhiteSpace(connString))
        return;

    try
    {
        using var conn = new MySqlConnection(connString);
        await conn.OpenAsync();

        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = @"
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = DATABASE()
                  AND table_name = 'tbl_network_log'
                  AND column_name = 'updated_at';";

            var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
            if (!exists)
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = @"
                    ALTER TABLE tbl_network_log
                    ADD COLUMN updated_at DATETIME NOT NULL
                    DEFAULT CURRENT_TIMESTAMP
                    ON UPDATE CURRENT_TIMESTAMP;";
                await alterCmd.ExecuteNonQueryAsync();
            }
        }

        NetworkLogUpdatedAtColumnEnsured = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Network log updated_at ensure skipped: {SafeException.Get(ex)}");
    }
}

private async Task<string> GetNetworkLogDataVersionAsync(
    string connString,
    List<long> sessionIds,
    string provider,
    MapFilter1 filters)
{
    if (sessionIds == null || sessionIds.Count == 0 || string.IsNullOrWhiteSpace(connString))
        return "empty";

    var (whereClause, parameters) = await BuildSqlWhereAsync(sessionIds, provider, filters);

    async Task<string> ReadVersionAsync(bool includeUpdatedAt)
    {
        using var conn = new MySqlConnection(connString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeUpdatedAt
            ? $@"
                SELECT
                    COUNT(*) AS row_count,
                    COALESCE(MAX(id), 0) AS max_id,
                    COALESCE(UNIX_TIMESTAMP(MAX(updated_at)), 0) AS max_updated_at,
                    COALESCE(UNIX_TIMESTAMP(MAX(timestamp)), 0) AS max_timestamp
                FROM tbl_network_log
                WHERE {whereClause};"
            : $@"
                SELECT
                    COUNT(*) AS row_count,
                    COALESCE(MAX(id), 0) AS max_id,
                    0 AS max_updated_at,
                    COALESCE(UNIX_TIMESTAMP(MAX(timestamp)), 0) AS max_timestamp
                FROM tbl_network_log
                WHERE {whereClause};";

        foreach (var parameter in parameters)
            AddParameter(cmd, parameter.Key, parameter.Value);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return "0-0-0-0";

        var count = Convert.ToInt64(reader["row_count"], CultureInfo.InvariantCulture);
        var maxId = Convert.ToInt64(reader["max_id"], CultureInfo.InvariantCulture);
        var maxUpdatedAt = Convert.ToInt64(reader["max_updated_at"], CultureInfo.InvariantCulture);
        var maxTimestamp = Convert.ToInt64(reader["max_timestamp"], CultureInfo.InvariantCulture);
        return $"{count}-{maxId}-{maxUpdatedAt}-{maxTimestamp}";
    }

    try
    {
        return await ReadVersionAsync(includeUpdatedAt: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Network log version fallback: {SafeException.Get(ex)}");
        return await ReadVersionAsync(includeUpdatedAt: false);
    }
}

private string BuildNetworkLogCacheKey(
    List<long> sessionIds, 
    string provider, 
    DateTime? from, 
    DateTime? to,
    long? projectId = null,
    string dataVersion = "noversion")
{
    var sortedSessionIds = string.Join("-", sessionIds.OrderBy(x => x));
    string providerKey = provider ?? "all";
    string fromKey = from?.ToString("yyyyMMdd") ?? "null";
    string toKey = to?.ToString("yyyyMMdd") ?? "null";
    string projectKey = projectId.HasValue && projectId.Value > 0
        ? projectId.Value.ToString(CultureInfo.InvariantCulture)
        : "no_project";
    string versionKey = NormalizeCacheKeyPart(dataVersion);

    return $"networklog:v9:{sortedSessionIds}:{providerKey}:{fromKey}:{toKey}:{projectKey}:{versionKey}";
}

private static string CleanProviderDisplayName(string value)
{
    return string.IsNullOrWhiteSpace(value)
        ? ""
        : value.Trim().Trim('"', '\'');
}

private static void NormalizeNetworkLogRows(List<NetworkLogCacheRow> rows)
{
    foreach (var row in rows)
    {
        row.m_alpha_long = CleanProviderDisplayName(row.m_alpha_long);
        row.provider = NormalizeNetworkLogProvider(row);
        row.band = NormalizeNetworkLogBand(row.band);
        row.technology = NormalizeNetworkLogTechnology(row.network, row.band);
        row.networkType = row.technology;
        row.lng = row.lon;
        row.longitude = row.lon;
        row.latitude = row.lat;
        row.is_wifi = string.Equals(row.connection_type, "wifi", StringComparison.OrdinalIgnoreCase);
        row.log_type = row.is_wifi ? "wifi" : "network";
    }
}

private static string NormalizeNetworkLogProvider(NetworkLogCacheRow row)
{
    var provider = CleanProviderDisplayName(row.m_alpha_long);
    if (row.connection_type.Equals("wifi", StringComparison.OrdinalIgnoreCase))
        return string.IsNullOrWhiteSpace(provider) ? "Unknown" : provider;

    var lower = provider.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(lower) || lower is "unknown" or "null" or "n/a" or "na")
        return "Unknown";
    if (lower.Contains("jio")) return "Jio";
    if (lower.Contains("airtel") || lower.Contains("bharti")) return "Airtel";
    if (lower.Contains("vodafone") || lower.Contains("idea") || lower == "vi" || lower.Contains("vi india")) return "Vi India";
    if (lower.Contains("bsnl")) return "BSNL";
    return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lower);
}

private static string NormalizeNetworkLogBand(string? band)
{
    var value = CleanProviderDisplayName(band ?? "");
    if (string.IsNullOrWhiteSpace(value) || value.Equals("-1", StringComparison.OrdinalIgnoreCase))
        return "";
    return value;
}

private static string NormalizeNetworkLogTechnology(string? network, string? band)
{
    var networkText = (network ?? "").Trim();
    var combined = $"{networkText} {band ?? ""}".ToUpperInvariant();
    if (combined.Contains("WIFI") || combined.Contains("WI-FI")) return "WiFi";
    if (
        combined.Contains("5G")
        || combined.Contains("NR NSA")
        || combined.Contains("NR SA")
        || combined.Contains("NR-CA")
        || combined.Contains("NR-DC")
        || combined.Contains("VONR")
        || combined.Contains("LTE ANCHOR")
        || combined.Contains("LTE-ANCHOR")
        || combined.Contains("LTE_ANCHOR")
        || combined.Contains("NSA")
        || combined.Contains("ENDC")
        || combined.Contains("EN-DC")
        || Regex.IsMatch(combined, @"(^|[^A-Z0-9])NR([^A-Z0-9]|$)")
        || Regex.IsMatch(combined, @"(^|[^A-Z0-9])N[0-9]{1,3}([^A-Z0-9]|$)")
    )
    {
        return "5G";
    }
    if (
        combined.Contains("4G")
        || combined.Contains("LTE")
        || combined.Contains("LTE-A")
        || combined.Contains("LTE A")
        || combined.Contains("LTE-A PRO")
        || combined.Contains("LTE A PRO")
        || combined.Contains("LTE CA")
        || combined.Contains("VOLTE")
        || combined.Contains("NB-IOT")
        || combined.Contains("LTE-M")
    ) return "4G";
    if (
        combined.Contains("3G")
        || combined.Contains("WCDMA")
        || combined.Contains("UMTS")
        || combined.Contains("HSPA")
        || combined.Contains("HSPA+")
        || combined.Contains("DC-HSPA+")
    ) return "3G";
    if (combined.Contains("2G") || combined.Contains("GSM") || combined.Contains("EDGE") || combined.Contains("GPRS")) return "2G";
    return string.IsNullOrWhiteSpace(networkText) ? "Unknown" : networkText;
}

private void AddParameter(DbCommand cmd, string name, object value)
{
    var param = cmd.CreateParameter();
    param.ParameterName = name;
    param.Value = value ?? DBNull.Value;
    cmd.Parameters.Add(param);
}

public class NetworkLogFullResponse
{
    public List<NetworkLogCacheRow> data { get; set; } = new();
    public Dictionary<string, object> app_summary { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> io_summary { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public object tpt_volume { get; set; }
    public DateTime CachedAt { get; set; }
    public int TotalCount { get; set; }
    public List<SessionCountDto> Sessions { get; set; } = new();
}
public class ProviderNetworkTime
{
    public required string Provider { get; set; }
    public required string Network { get; set; }
    public double TimeSeconds { get; set; }
}
public class SessionIdsRequest
{
    public List<int> SessionIds { get; set; }
}

public class NetworkLogCacheRow
{
    public int id { get; set; }
    public int session_id { get; set; }
    public DateTime? timestamp { get; set; }
    public float? lat { get; set; }
    public float? lon { get; set; }
    public float? lng { get; set; }
    public float? latitude { get; set; }
    public float? longitude { get; set; }
    public int? battery { get; set; }
    public float? Speed { get; set; }
    public int? level { get; set; }
    public string apps { get; set; } = "";
    public int? num_cells { get; set; }
    public string network { get; set; } = "";
    public string technology { get; set; } = "";
    public string networkType { get; set; } = "";
    public string m_alpha_long { get; set; } = "";
    public string provider { get; set; } = "";
    public string pci { get; set; } = "";
    public float? rssi { get; set; }
    public float? rsrp { get; set; }
    public float? rsrq { get; set; }
    public float? sinr { get; set; }
    public float? mos { get; set; }
    public float? jitter { get; set; }
    public float? latency { get; set; }
    public string tac { get; set; } = "";
    public float? packet_loss { get; set; }
    public string dl_tpt { get; set; } = "";
    public string ul_tpt { get; set; } = "";
    public string band { get; set; } = "";
    public string image_path { get; set; } = "";
    public string indoor_outdoor { get; set; } = "";
    public string nodeb_id { get; set; } = "";
    public string cell_id { get; set; } = "";
    public string connection_type { get; set; } = "network";
    public string log_type { get; set; } = "network";
    public bool is_wifi { get; set; }
}

// Delete project and unlink the polygon 
[HttpDelete, Route("DeleteProject")]
public async Task<IActionResult> DeleteProject([FromQuery] int projectId)
{
    var message = new ReturnAPIResponse();

    if (projectId <= 0)
    {
        return BadRequest(new { Status = 0, Message = "A valid ProjectId is required." });
    }

    try
    {
        // 1. Initial Checks (Performed outside the retry strategy to avoid unnecessary queries)
        var project = await db.tbl_project.AsNoTracking().FirstOrDefaultAsync(p => p.id == projectId);
        if (project == null)
        {
            return NotFound(new { Status = 0, Message = "Project not found." });
        }

        // 2. Security Check
        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);
        if (!_userScope.IsSuperAdmin(User) && project.company_id != targetCompanyId)
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized to delete this project." });
        }

        // 3. Create the Execution Strategy for safe transactions
        var strategy = db.Database.CreateExecutionStrategy();

        // 4. Wrap the transaction inside the strategy
        await strategy.ExecuteAsync(async () =>
        {
            // Start the transaction INSIDE the execution strategy delegate
            await using var transaction = await db.Database.BeginTransactionAsync();

            try
            {
                // A. Cleanup: map_regions
                var linkedMapRegions = await db.map_regions.Where(p => p.tbl_project_id == projectId).ToListAsync();
                foreach (var poly in linkedMapRegions)
                {
                    poly.tbl_project_id = null;
                }

                // B. Cleanup: tbl_savepolygon via raw SQL
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE tbl_savepolygon SET project_id = NULL WHERE project_id = @pid";
                    cmd.Transaction = transaction.GetDbTransaction(); 
                    
                    var pIdParam = cmd.CreateParameter();
                    pIdParam.ParameterName = "@pid";
                    pIdParam.Value = projectId;
                    cmd.Parameters.Add(pIdParam);
                    
                    await cmd.ExecuteNonQueryAsync();
                }

                // C. Remove the project (Re-fetch inside the tracked context so EF knows to delete it)
                var projToDelete = await db.tbl_project.FindAsync(projectId);
                if (projToDelete != null)
                {
                    db.tbl_project.Remove(projToDelete);
                }

                // Commit all changes atomically
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                await InvalidateMapViewCachesAsync();
            }
            catch
            {
                // Roll back if anything fails, then re-throw so the Execution Strategy can decide whether to retry
                await transaction.RollbackAsync();
                throw; 
            }
        });

        // 5. Cache Invalidation
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                // Uncomment and adjust based on your RedisService's actual delete method (e.g., RemoveAsync, DeleteKeyAsync)
                // await _redis.RemoveAsync($"projectpolygons:{projectId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis cache clear failed on project delete: {SafeException.Get(ex)}");
            }
        }

        message.Status = 1;
        message.Message = $"Project '{project.project_name}' deleted successfully. Polygons are now unused and available.";
        return Ok(message);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error deleting project: " + SafeException.Get(ex) });
    }
}

[HttpPut, Route("UpdateProjectSessions")]
public async Task<IActionResult> UpdateProjectSessions([FromBody] UpdateProjectSessionsModel model)
{
    if (model == null || model.ProjectId <= 0)
    {
        return BadRequest(new { Status = 0, Message = "ProjectId is required." });
    }

    try
    {
        var project = await db.tbl_project.FirstOrDefaultAsync(p => p.id == model.ProjectId);
        if (project == null)
        {
            return NotFound(new { Status = 0, Message = "Project not found." });
        }

        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);
        if (!_userScope.IsSuperAdmin(User) && project.company_id != targetCompanyId)
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized to update this project." });
        }

        var sessionIds = (model.SessionIds ?? new List<int>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        project.ref_session_id = sessionIds.Any() ? string.Join(",", sessionIds) : null;
        await db.SaveChangesAsync();
        await InvalidateProjectListCachesAsync();
        await InvalidateMapViewCachesAsync();

        return Ok(new
        {
            Status = 1,
            Message = "Project sessions updated successfully.",
            Data = new
            {
                project.id,
                project.project_name,
                project.ref_session_id,
                project.from_date,
                project.to_date,
                project.provider,
                project.tech,
                project.band,
                project.earfcn,
                project.apps,
                project.grid_size,
                project.log_grid,
                project.created_on,
                project.status
            }
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error updating project sessions.",
            Details = SafeException.Get(ex)
        });
    }
}

[HttpPut, Route("UpdateProjectSiteSize")]
public async Task<IActionResult> UpdateProjectSiteSize([FromBody] UpdateProjectSiteSizeModel model)
{
    if (model == null || model.ProjectId <= 0)
    {
        return BadRequest(new { Status = 0, Message = "ProjectId is required." });
    }

    if (model.SiteSize <= 0)
    {
        return BadRequest(new { Status = 0, Message = "SiteSize must be greater than 0." });
    }

    try
    {
        int targetCompanyId = _userScope.GetTargetCompanyId(User, null);
        var conn = db.Database.GetDbConnection();
        var shouldClose = false;
        try
        {
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
                shouldClose = true;
            }

            var projectColumns = await GetTableColumnSetAsync(conn, "tbl_project");
            if (!projectColumns.Contains("sitesize"))
            {
                return BadRequest(new
                {
                    Status = 0,
                    Message = "tbl_project.sitesize column is missing. Please run the sitesize SQL migration first."
                });
            }

            await using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT id, project_name, company_id FROM tbl_project WHERE id = @projectId LIMIT 1;";
            AddParam(selectCmd, "@projectId", model.ProjectId);

            string? projectName = null;
            int? projectCompanyId = null;
            await using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { Status = 0, Message = "Project not found." });
                }

                projectName = reader["project_name"] == DBNull.Value ? null : Convert.ToString(reader["project_name"]);
                projectCompanyId = reader["company_id"] == DBNull.Value ? null : Convert.ToInt32(reader["company_id"]);
            }

            if (!_userScope.IsSuperAdmin(User) && projectCompanyId != targetCompanyId)
            {
                return Unauthorized(new { Status = 0, Message = "Unauthorized to update this project." });
            }

            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE tbl_project SET sitesize = @siteSize WHERE id = @projectId;";
            AddParam(updateCmd, "@siteSize", model.SiteSize);
            AddParam(updateCmd, "@projectId", model.ProjectId);
            await updateCmd.ExecuteNonQueryAsync();

            await InvalidateProjectListCachesAsync();
            await InvalidateMapViewCachesAsync();

            return Ok(new
            {
                Status = 1,
                Message = "Project site size updated successfully.",
                Data = new
                {
                    id = model.ProjectId,
                    project_name = projectName,
                    sitesize = model.SiteSize,
                }
            });
        }
        finally
        {
            if (shouldClose && conn.State == ConnectionState.Open)
                await conn.CloseAsync();
        }
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error updating project site size.",
            Details = SafeException.Get(ex)
        });
    }
}

[HttpGet("session/provider-network-time/combined")]
public async Task<IActionResult> GetCombinedProviderNetworkTime(
    [FromQuery] string sessionIds)
{
    if (string.IsNullOrWhiteSpace(sessionIds))
    {
        return BadRequest(new { message = "sessionIds are required" });
    }

    // "2696,2678,2701" -> List<int>
    var sessionIdList = sessionIds
        .Split(',')
        .Select(x => int.TryParse(x.Trim(), out var id) ? id : 0)
        .Where(x => x > 0)
        .Distinct()
        .ToList();

    if (!sessionIdList.Any())
    {
        return BadRequest(new { message = "Invalid sessionIds" });
    }

    try
    {
        var cacheKey = BuildMapViewCacheKey("combined-provider-network-time-v2", sessionIdList);
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        var logs = await db.tbl_network_log
            .AsNoTracking()
            .Where(x =>
                sessionIdList.Contains((int)x.session_id) &&
                x.timestamp != null)
            .OrderBy(x => x.session_id)
            .ThenBy(x => x.timestamp)
            .Select(x => new
            {
                x.session_id,
                x.timestamp,
                x.network,
                x.band,
                x.primary_cell_info_1,
                x.all_neigbor_cell_info,
                x.m_alpha_long
            })
            .ToListAsync();

        if (logs.Count < 2)
        {
            var empty = new { message = "Not enough data" };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        // provider|network => totalSeconds
        var result = new Dictionary<string, double>();

        for (int i = 0; i < logs.Count - 1; i++)
        {
            var current = logs[i];
            var next = logs[i + 1];

            // Never mix different sessions
            if (current.session_id != next.session_id)
                continue;

            var diffSeconds =
                (next.timestamp.Value - current.timestamp.Value).TotalSeconds;

            // Ignore invalid or abnormal gaps
            if (diffSeconds <= 0 || diffSeconds > 3600)
                continue;

            // ----------------------------
            // Interval attribution
            // Keep provider + network classification from the SAME sample.
            // For [current, next), prefer current sample; fallback to next only if current is unusable.
            // ----------------------------
            string NormalizeProvider(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return "UNKNOWN";
                var p = raw.Trim().ToUpperInvariant();

                if (p.Contains("AIRTEL")) return "AIRTEL";
                if (p.Contains("JIO")) return "JIO";
                if (p.Contains("VOD") || p.Contains("IDEA") || p.Contains(" VI ") || p.StartsWith("VI") || p.Contains("VIL"))
                    return "VI";

                return p;
            }

            string ResolveGeneration(string? band, string? network)
            {
                var t = TechClassifier.Classify(band, network, null, null);
                return string.IsNullOrWhiteSpace(t.Generation) || t.Generation.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "OTHER"
                    : t.Generation.ToUpperInvariant();
            }

            var providerCurrent = NormalizeProvider(current.m_alpha_long);
            var networkCurrent = ResolveGeneration(current.band, current.network);

            bool currentUsable = providerCurrent != "UNKNOWN" && networkCurrent != "OTHER";

            string provider = currentUsable ? providerCurrent : NormalizeProvider(next.m_alpha_long);
            string networkType = currentUsable
                ? networkCurrent
                : ResolveGeneration(next.band, next.network);

            string key = $"{provider}|{networkType}";

            if (!result.ContainsKey(key))
                result[key] = 0;

            result[key] += diffSeconds;
        }

        var response = result.Select(x =>
        {
            var parts = x.Key.Split('|');
            var seconds = x.Value;

            return new
            {
                provider = parts[0],
                network = parts[1],
                timeSeconds = Math.Round(seconds, 2),
                timeMinutes = Math.Round(seconds / 60, 2),
                timeHours = Math.Round(seconds / 3600, 2),
                timeReadable = TimeSpan
                    .FromSeconds(seconds)
                    .ToString(@"hh\:mm\:ss")
            };
        })
        .OrderByDescending(x => x.timeSeconds)
        .ToList();

        var payload = new { data = response };
        await SetMapViewCacheAsync(cacheKey, payload);
        return Ok(payload);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            message = "Error calculating combined provider network time",
            error = SafeException.Get(ex)
        });
    }
}


// cdf value calcuation s 
[AllowAnonymous]
[HttpGet("kpi-distribution")]
public async Task<IActionResult> GetKpiDistribution(
    [FromQuery] string sessionIds,
    [FromQuery] string? kpi)
{
    if (string.IsNullOrWhiteSpace(sessionIds))
        return BadRequest("sessionIds are required");

    string ids = sessionIds;
    var cacheKey = BuildMapViewCacheKey("kpi-distribution", ids, kpi ?? "all");
    var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
    if (cached != null)
        return Ok(cached);

    // ðŸ”¹ KPI â†’ SQL Expression Map (RSRP FIXED)
    var kpiMap = new Dictionary<string, (string expr, string column)>
    {
        { "rsrp", ("ROUND(rsrp)", "rsrp") },      // âœ… FIXED
        { "rsrq", ("ROUND(rsrq)", "rsrq") },
        { "sinr", ("ROUND(sinr,1)", "sinr") },
        { "mos",  ("ROUND(mos,1)", "mos") },
        { "pci",  ("pci", "pci") },
        { "dl_tpt", ("ROUND(dl_tpt,2)", "dl_tpt") },
        { "ul_tpt", ("ROUND(ul_tpt,2)", "ul_tpt") }
    };

    async Task<List<KpiDistributionRow>> RunQuery(string expr, string rawColumn)
    {
        string sql = $@"
        SELECT
            value,
            count,
            cumulative_count,
            ROUND((cumulative_count / total_samples) * 100, 4) AS percentage
        FROM (
            SELECT
                CAST({expr} AS SIGNED) AS value,
                COUNT(*) AS count,
                SUM(COUNT(*)) OVER (ORDER BY CAST({expr} AS SIGNED)) AS cumulative_count,
                SUM(COUNT(*)) OVER () AS total_samples
            FROM tbl_network_log
            WHERE session_id IN ({ids})
              AND {rawColumn} IS NOT NULL
            GROUP BY CAST({expr} AS SIGNED)
        ) t
        ORDER BY CAST(value AS SIGNED);
        ";

        return await db.KpiDistributionRows
            .FromSqlRaw(sql)
            .AsNoTracking()
            .ToListAsync();
    }

    // ðŸ”¹ SINGLE KPI
    if (!string.IsNullOrWhiteSpace(kpi))
    {
        kpi = kpi.ToLower();

        if (!kpiMap.ContainsKey(kpi))
            return BadRequest("Invalid KPI");

        var (expr, column) = kpiMap[kpi];
        var data = await RunQuery(expr, column);

        var response = new
        {
            Status = 1,
            KPI = kpi,
            Data = data
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Ok(response);
    }

    // ðŸ”¹ ALL KPIs
    var allData = new Dictionary<string, object>();

    foreach (var item in kpiMap)
    {
        allData[item.Key] = await RunQuery(item.Value.expr, item.Value.column);
    }

    var allResponse = new
    {
        Status = 1,
        Data = allData
    };
    await SetMapViewCacheAsync(cacheKey, allResponse);
    return Ok(allResponse);
}

[HttpGet("lat-lon-distribution")]
public async Task<IActionResult> GetLatLonDistribution(
    [FromQuery] string sessionIds)
{
    if (string.IsNullOrWhiteSpace(sessionIds))
        return BadRequest("sessionIds are required");

    var sessionIdList = sessionIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(id => int.TryParse(id.Trim(), out var v) ? v : 0)
        .Where(v => v > 0)
        .Distinct()
        .OrderBy(x => x)
        .ToList();

    if (!sessionIdList.Any())
        return BadRequest("Invalid sessionIds");

    // ========================================
    // ðŸ”‘ BUILD REDIS CACHE KEY
    // ========================================
    string cacheKey = $"latlon:dist:{string.Join("-", sessionIdList)}";

    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

    // ========================================
    // ðŸ” TRY REDIS CACHE
    // ========================================
    if (_redis != null && _redis.IsConnected)
    {
        try
        {
            var cacheWatch = System.Diagnostics.Stopwatch.StartNew();
            var cached = await _redis.GetObjectAsync<object>(cacheKey);
            cacheWatch.Stop();

            if (cached != null)
            {
                totalStopwatch.Stop();

                Response.Headers["X-Cache"] = "HIT";
                Response.Headers["X-Cache-Lookup-Ms"] = cacheWatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();

                return Ok(cached);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"âš ï¸ Redis read error: {SafeException.Get(ex)}");
        }
    }

    // ========================================
    // ðŸ—„ï¸ FETCH FROM DATABASE
    // ========================================
    var conn = db.Database.GetDbConnection();
    bool shouldClose = false;

    var rows = new List<object>();

    try
    {
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
            shouldClose = true;
        }

        var sidParams = sessionIdList
            .Select((_, i) => $"@sid{i}")
            .ToList();

        string inClause = string.Join(",", sidParams);

        var sql = $@"
WITH rounded_data AS (
    SELECT
        ROUND(lat, 4) AS lat_4,
        ROUND(lon, 4) AS lon_4
    FROM tbl_network_log
    WHERE lat IS NOT NULL
      AND lon IS NOT NULL
      AND session_id IN ({inClause})
),
grouped_data AS (
    SELECT
        lat_4,
        lon_4,
        COUNT(*) AS log_count
    FROM rounded_data
    GROUP BY lat_4, lon_4
),
total_logs AS (
    SELECT SUM(log_count) AS total_count
    FROM grouped_data
)
SELECT
    lat_4,
    lon_4,
    log_count,
    ROUND(log_count * 100.0 / total_count, 4) AS percentage,
    SUM(log_count) OVER (ORDER BY log_count DESC) AS cumulative_count,
    ROUND(
        SUM(log_count) OVER (ORDER BY log_count DESC) * 100.0 / total_count,
        4
    ) AS cumulative_percentage
FROM grouped_data, total_logs
ORDER BY log_count DESC;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        for (int i = 0; i < sessionIdList.Count; i++)
            AddParameter(cmd, $"@sid{i}", sessionIdList[i]);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new
            {
                latitude = reader.GetDecimal(0),
                longitude = reader.GetDecimal(1),
                count = reader.GetInt32(2),
                percentage = reader.GetDecimal(3),
                cumulativeCount = reader.GetInt32(4),
                cumulativePercentage = reader.GetDecimal(5)
            });
        }
    }
    finally
    {
        if (shouldClose && conn.State == ConnectionState.Open)
            await conn.CloseAsync();
    }

    var response = new
    {
        Status = 1,
        SessionCount = sessionIdList.Count,
        Data = rows
    };

    // ========================================
    // ðŸ’¾ SAVE TO REDIS
    // ========================================
    if (_redis != null && _redis.IsConnected)
    {
        try
        {
            await _redis.SetObjectAsync(cacheKey, response, ttlSeconds: 300);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"âš ï¸ Redis write error: {SafeException.Get(ex)}");
        }
    }

    totalStopwatch.Stop();

    Response.Headers["X-Cache"] = "MISS";
    Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
    Response.Headers["X-Row-Count"] = rows.Count.ToString();

    return Ok(response);
}






// see in thiss indoor andd oudoor degreadation parrt  through the sessionss and  we can use the mutliple sessionss ( call as the sessionIds=)

// [HttpGet("i/o-degradation")]
// public async Task<IActionResult> GetIndoorOutdoorDegradationBySession(
//     [FromQuery] string sessionIds
// )
// {
//     try
//     {
//         db.Database.SetCommandTimeout(180);

//         // ===============================
//         // 1ï¸ Parse Session IDs
//         // ===============================
//         var sessionIdList = sessionIds
//             .Split(',', StringSplitOptions.RemoveEmptyEntries)
//             .Select(id => int.Parse(id.Trim()))
//             .ToList();

//         if (!sessionIdList.Any())
//         {
//             return BadRequest(new
//             {
//                 Status = 0,
//                 Message = "Session IDs are required"
//             });
//         }

//         // ===============================
//         // 2ï¸ Indoor vs Outdoor Averages (DB LEVEL)
//         // ===============================
//         var avgData = await db.tbl_network_log
//             .AsNoTracking()
//             .Where(x =>
//                 sessionIdList.Contains(x.session_id) &&
//                 (x.indoor_outdoor == "Indoor" || x.indoor_outdoor == "Outdoor") &&
//                 x.sinr != null &&
//                 x.mos != null
//             )
//             .GroupBy(x => x.indoor_outdoor)
//             .Select(g => new
//             {
//                 Type = g.Key,
//                 AvgSINR = g.Average(x => x.sinr.Value),
//                 AvgMOS = g.Average(x => x.mos.Value)
//             })
//             .ToListAsync();

//         var indoor = avgData.FirstOrDefault(x => x.Type == "Indoor");
//         var outdoor = avgData.FirstOrDefault(x => x.Type == "Outdoor");

//         if (indoor == null || outdoor == null)
//         {
//             return Ok(new
//             {
//                 Status = 1,
//                 Message = "Not enough Indoor / Outdoor data for given sessions"
//             });
//         }

//         double sinrDropPercent =
//             ((outdoor.AvgSINR - indoor.AvgSINR) / outdoor.AvgSINR) * 100;

//         double mosDropPercent =
//             ((outdoor.AvgMOS - indoor.AvgMOS) / outdoor.AvgMOS) * 100;

//         // ===============================
//         // 3ï¸ Degraded Indoor Locations
//         // ===============================
//         var degradedIndoorLocations = await db.tbl_network_log
//             .AsNoTracking()
//             .Where(x =>
//                 sessionIdList.Contains(x.session_id) &&
//                 x.indoor_outdoor == "Indoor" &&
//                 x.lat != null &&
//                 x.lon != null &&
//                 (x.sinr < 5 || x.mos < 3)
//             )
//             .Select(x => new
//             {
//                 lat = x.lat,
//                 lon = x.lon,
//                 sinr = x.sinr,
//                 mos = x.mos,
//                 network = x.network,
//                 operatorName = x.m_alpha_long
//             })
//             .OrderBy(x => x.sinr)
            
//             .ToListAsync();

//         // ===============================
//         // 4ï¸ 5G Indoor Weak Stability
//         // ===============================
//         var indoor5GWeakStability = await db.tbl_network_log
//             .AsNoTracking()
//             .Where(x =>
//                 sessionIdList.Contains(x.session_id) &&
//                 x.indoor_outdoor == "Indoor" &&
//                 x.network != null &&
//                 (x.network.Contains("5G") || x.network.Contains("NR")) &&
//                 x.lat != null &&
//                 x.lon != null &&
//                 x.sinr < 5 &&
//                 x.dl_tpt != null
//             )
//             .Select(x => new
//             {
//                 lat = x.lat,
//                 lon = x.lon,
//                 sinr = x.sinr,
//                 dl_tpt_mbps = Convert.ToDouble(x.dl_tpt),
//                 operatorName = x.m_alpha_long
//             })
//             .OrderBy(x => x.sinr)
            
//             .ToListAsync();

//         // ===============================
//         // 5ï¸ RESPONSE
//         // ===============================
//         return Ok(new
//         {
//             Status = 1,
//             Message = "Indoorâ€“Outdoor Degradation (Session-wise)",
//             SessionIds = sessionIdList,
//             Summary = new
//             {
//                 IndoorAvgSINR = Math.Round(indoor.AvgSINR, 2),
//                 OutdoorAvgSINR = Math.Round(outdoor.AvgSINR, 2),
//                 SINR_Degradation_Percent = Math.Round(sinrDropPercent, 2),
//                 IndoorAvgMOS = Math.Round(indoor.AvgMOS, 2),
//                 OutdoorAvgMOS = Math.Round(outdoor.AvgMOS, 2),
//                 MOS_Degradation_Percent = Math.Round(mosDropPercent, 2)
//             },
//             Insights = new[]
//             {
//                 $"Indoor SINR is {Math.Round(sinrDropPercent,2)}% lower than Outdoor for selected sessions, causing {Math.Round(mosDropPercent,2)}% MOS degradation.",
//                 "5G Indoor samples show higher throughput but poor signal stability at specific indoor locations."
//             },
//             DegradedIndoorLocations = degradedIndoorLocations,
//             Indoor5GWeakStabilityLocations = indoor5GWeakStability
//         });
//     }
//     catch (Exception ex)
//     {
//         return StatusCode(500, new
//         {
//             Status = 0,
//             Message = "Error calculating degradation map for sessions",
//             Error = SafeException.Get(ex)
//         });
//     }
// }

[HttpGet("GetN78NeighboursSimple")]
public async Task<IActionResult> GetN78NeighboursSimple([FromQuery] string sessionIds)
{
    // ================= VALIDATION =================
    if (string.IsNullOrWhiteSpace(sessionIds))
        return BadRequest("sessionIds are required");

    var parsedIds = sessionIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x, out var v) ? v : 0)
        .Where(v => v > 0)
        .Distinct()
        .ToList();

    if (parsedIds.Count == 0)
        return BadRequest("No valid sessionIds");

    string sessionCsv = string.Join(",", parsedIds);
    string cacheKey = $"n78_simple_kpi:{sessionCsv}";

    // ================= REDIS READ =================
    if (_redis != null && _redis.IsConnected)
    {
        try
        {
            var cached = await _redis.GetObjectAsync<List< N78NeighbourSimpleDto>>(cacheKey);
            if (cached != null)
            {
                Response.Headers["X-Cache"] = "HIT";
                return Ok(new
                {
                    Status = 1,
                    Cached = true,
                    SessionCount = parsedIds.Count,
                    RecordCount = cached.Count,
                    Data = cached
                });
            }
        }
        catch { }
    }

    // ================= SQL =================
   var sql = $@"
SELECT
    p.id AS id,
    p.session_id,
    p.timestamp,
    p.lat,
    p.lon,
    p.indoor_outdoor,

    -- Primary info
    p.network AS primary_network,
    p.band    AS primary_band,
    '5GNSA'   AS network_type,
    p.m_alpha_long AS provider,

    -- KPIs (numeric in DB)
    p.rsrp AS rsrp,
    p.rsrq AS rsrq,
    p.sinr AS sinr,
    p.mos  AS mos,

    -- ðŸ”¥ Throughput (VARCHAR â†’ DOUBLE FIX)
    CAST(NULLIF(p.dl_tpt, '') AS DECIMAL(12,4)) AS dl_tpt,
    CAST(NULLIF(p.ul_tpt, '') AS DECIMAL(12,4)) AS ul_tpt,

    -- Neighbour info
    n.network AS neighbour_network,
    n.lat     AS neighbour_lat,
    n.lon     AS neighbour_lon,
    n.band    AS neighbour_band,

    -- Distance (numeric)
    ST_Distance_Sphere(
        POINT(p.lon, p.lat),
        POINT(n.lon, n.lat)
    ) AS distance_meters

FROM tbl_network_log p
INNER JOIN tbl_network_log_neighbour n
    ON  p.session_id = n.session_id
    AND p.timestamp  = n.timestamp
    AND p.lat        = n.lat
    AND p.lon        = n.lon
WHERE p.session_id IN ({sessionCsv})
  AND (p.network LIKE '%NR%' OR p.network LIKE '%5G%')
  AND (n.network LIKE '%LTE%' OR n.network LIKE '%4G%')
ORDER BY p.timestamp;
";


    // ================= DB EXECUTION =================
    var data = await db.N78NeighbourSimpleDto
        .FromSqlRaw(sql)
        .AsNoTracking()
        .ToListAsync();

    // ================= REDIS WRITE =================
    if (_redis != null && _redis.IsConnected)
    {
        try
        {
            await _redis.SetObjectAsync(cacheKey, data, ttlSeconds: 300);
        }
        catch { }
    }

    Response.Headers["X-Cache"] = "MISS";

    return Ok(new
    {
        Status = 1,
        Cached = false,
        SessionCount = parsedIds.Count,
        RecordCount = data.Count,
        Data = data
    });
}


 
// [HttpGet("GetN78Neighbours")]
// public async Task<IActionResult> GetN78Neighbours([FromQuery] string sessionIds)
// {
//     // ================= VALIDATION =================
//     if (string.IsNullOrWhiteSpace(sessionIds))
//         return BadRequest("sessionIds are required");

//     var parsedIds = sessionIds
//         .Split(',', StringSplitOptions.RemoveEmptyEntries)
//         .Select(x => int.TryParse(x, out var v) ? v : 0)
//         .Where(v => v > 0)
//         .Distinct()
//         .ToList();

//     if (parsedIds.Count == 0)
//         return BadRequest("No valid sessionIds");

//     string sessionCsv = string.Join(",", parsedIds);
//     string cacheKey = $"n78_neighbour:{sessionCsv}";

//     // ================= REDIS READ =================
//     if (_redis != null && _redis.IsConnected)
//     {
//         try
//         {
//             var cached = await _redis.GetObjectAsync<List<N78NeighbourDto>>(cacheKey);
//             if (cached != null)
//             {
//                 Response.Headers["X-Cache"] = "HIT";
//                 return Ok(new
//                 {
//                     Status = 1,
//                     Cached = true,
//                     SessionCount = parsedIds.Count,
//                     RecordCount = cached.Count,
//                     Data = cached
//                 });
//             }
//         }
//         catch { }
//     }

//     // ================= FAST SQL (NO TIMEOUT) =================
//     var sql = $@"
//         SELECT
//             l.id,
//             l.session_id,
//             l.timestamp,
//             l.lat,
//             l.lon,
//             l.indoor_outdoor,
//             l.network,
//             '5GNSA' AS network_type,
//             l.m_alpha_long,
//             l.rsrp,
//             l.rsrq,
//             l.sinr,
//             l.mos,
//             l.dl_tpt,
//             l.ul_tpt,
//             l.rssi,
//             n.lat AS neighbour_lat,
//             n.lon AS neighbour_lon,
//             n.band AS neighbour_band,
//             ST_Distance_Sphere(
//                 POINT(l.lon, l.lat),
//                 POINT(n.lon, n.lat)
//             ) AS distance_meters
//         FROM
//         (
//             SELECT id, session_id, timestamp, lat, lon,
//                    indoor_outdoor, network, m_alpha_long,
//                    rsrp, rsrq, sinr, mos, dl_tpt, ul_tpt, rssi
//             FROM tbl_network_log
//             WHERE session_id IN ({sessionCsv})
//               AND lat IS NOT NULL
//               AND lon IS NOT NULL
//         ) l
//         JOIN
//         (
//             SELECT lat, lon, band
//             FROM tbl_network_log_neighbour
//             WHERE band = 'n78'
//               AND lat IS NOT NULL
//               AND lon IS NOT NULL
//         ) n
//           ON n.lat BETWEEN l.lat - 0.00005 AND l.lat + 0.00005
//          AND n.lon BETWEEN l.lon - 0.00005 AND l.lon + 0.00005
//         WHERE ST_Distance_Sphere(
//                 POINT(l.lon, l.lat),
//                 POINT(n.lon, n.lat)
//               ) <= 0;
//     ";

//     // ================= DB EXECUTION =================
//     var data = await db.N78NeighbourDto
//         .FromSqlRaw(sql)
//         .AsNoTracking()
//         .ToListAsync();

//     // ================= REDIS WRITE =================
//     if (_redis != null && _redis.IsConnected)
//     {
//         try
//         {
//             await _redis.SetObjectAsync(cacheKey, data, ttlSeconds: 300);
//         }
//         catch { }
//     }

//     Response.Headers["X-Cache"] = "MISS";

//     return Ok(new
//     {
//         Status = 1,
//         Cached = false,
//         SessionCount = parsedIds.Count,
//         RecordCount = data.Count,
//         Data = data
//     });
// }




[HttpGet("GetN78Neighbours")]
public async Task<IActionResult> GetN78Neighbours([FromQuery] string session_ids, [FromQuery] long? project_id = null)
{
    // ================= 1. VALIDATION =================
    if (string.IsNullOrWhiteSpace(session_ids))
        return BadRequest(new { Status = 0, Message = "session_ids are required" });

    var parsedIds = session_ids
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x.Trim(), out var v) ? v : 0)
        .Where(v => v > 0)
        .Distinct()
        .ToList();

    if (parsedIds.Count == 0)
        return BadRequest(new { Status = 0, Message = "No valid session_ids" });

    string sessionCsv = string.Join(",", parsedIds);
    string projectKey = project_id.HasValue && project_id.Value > 0
        ? project_id.Value.ToString(CultureInfo.InvariantCulture)
        : "no_project";
    string cacheKey = $"n78_neighbours:v2:{sessionCsv}:project:{projectKey}";
    var projectPolygonWkt = await ResolveProjectFilterWktAsync(project_id);

    // ================= 2. REDIS READ =================
    if (_redis != null && _redis.IsConnected)
    {
        try
        {
            var cached = await _redis.GetObjectAsync<List<LTE5GNeighbourDto>>(cacheKey);
            if (cached != null)
            {
                Response.Headers["X-Cache"] = "HIT";
                return Ok(new
                {
                    Status = 1,
                    Cached = true,
                    SessionCount = parsedIds.Count,
                    RecordCount = cached.Count,
                    Data = cached
                });
            }
        }
        catch { /* Redis unavailable â€” fall through to DB */ }
    }

    // ================= 3. RAW SQL (all processing in DB via CTE) =================
    string polygonFilterClause = !string.IsNullOrWhiteSpace(projectPolygonWkt)
        ? "AND p.lat IS NOT NULL AND p.lon IS NOT NULL AND ST_Contains(ST_GeomFromText(@projectPolygonWkt, 4326), ST_SRID(POINT(p.lon, p.lat), 4326))"
        : string.Empty;

    string sql = $@"
WITH PrimaryLogs AS (
    SELECT 
        id, session_id, timestamp, lat, lon, indoor_outdoor, 
        m_alpha_long AS provider,
        network AS primary_network, 
        band AS primary_band, 
        pci AS primary_pci, 
        rsrp AS primary_rsrp, 
        rsrq AS primary_rsrq, 
        sinr AS primary_sinr, 
        mos,
        CAST(NULLIF(dl_tpt, '') AS DECIMAL(12,4)) AS dl_tpt,
        CAST(NULLIF(ul_tpt, '') AS DECIMAL(12,4)) AS ul_tpt
    FROM tbl_network_log p
    WHERE p.session_id IN ({sessionCsv}) AND p.`primary` = 'yes'
      {polygonFilterClause}
),
JoinedData AS (
    SELECT 
        p.*,
        n.network AS neighbour_network, 
        n.band AS neighbour_band, 
        n.pci AS neighbour_pci,
        n.rsrp AS neighbour_rsrp, 
        n.rsrq AS neighbour_rsrq, 
        n.sinr AS neighbour_sinr,
        n.m_alpha_long AS neighbour_provider,
        CAST(NULLIF(n.dl_tpt, '') AS DECIMAL(12,4)) AS neighbour_dl_tpt,
        CAST(NULLIF(n.ul_tpt, '') AS DECIMAL(12,4)) AS neighbour_ul_tpt,
        ROW_NUMBER() OVER (
            PARTITION BY p.id 
            ORDER BY CAST(n.rsrp AS SIGNED) DESC
        ) AS rn
    FROM PrimaryLogs p
    INNER JOIN tbl_network_log_neighbour n 
        ON p.session_id = n.session_id 
        AND p.lat = n.lat 
        AND p.lon = n.lon
    WHERE (
          ((p.primary_network LIKE '%4G%' OR p.primary_network LIKE '%LTE%') AND (n.network LIKE '%5G%' OR n.network LIKE '%NR%'))
          OR
          ((p.primary_network LIKE '%5G%' OR p.primary_network LIKE '%NR%') AND (n.network LIKE '%4G%' OR n.network LIKE '%LTE%'))
          OR
          ((p.primary_network LIKE '%2G%' OR p.primary_network LIKE '%GSM%' OR p.primary_network LIKE '%EDGE%' OR p.primary_network LIKE '%GPRS%') AND (n.network LIKE '%4G%' OR n.network LIKE '%LTE%'))
          OR
          ((p.primary_network LIKE '%4G%' OR p.primary_network LIKE '%LTE%') AND (n.network LIKE '%2G%' OR n.network LIKE '%GSM%' OR n.network LIKE '%EDGE%' OR n.network LIKE '%GPRS%'))
          OR
          ((p.primary_network LIKE '%2G%' OR p.primary_network LIKE '%GSM%' OR p.primary_network LIKE '%EDGE%' OR p.primary_network LIKE '%GPRS%') AND (n.network LIKE '%5G%' OR n.network LIKE '%NR%'))
          OR
          ((p.primary_network LIKE '%5G%' OR p.primary_network LIKE '%NR%') AND (n.network LIKE '%2G%' OR n.network LIKE '%GSM%' OR n.network LIKE '%EDGE%' OR n.network LIKE '%GPRS%'))
    )
)
SELECT 
    id, session_id, timestamp, lat, lon, indoor_outdoor, provider,
    primary_network, primary_band, primary_pci, primary_rsrp, primary_rsrq, primary_sinr,
    mos, dl_tpt, ul_tpt,
    neighbour_network, neighbour_band, neighbour_pci, neighbour_rsrp, neighbour_rsrq, 
    neighbour_sinr, neighbour_provider, neighbour_dl_tpt, neighbour_ul_tpt
FROM JoinedData 
WHERE rn = 1 
ORDER BY timestamp;";

    // ================= 4. RAW ADO.NET READ â€” fetch raw rows from DB =================
    var data = new List<LTE5GNeighbourDto>();
    const int N78NeighbourCommandTimeoutSeconds = 300;

    var conn = db.Database.GetDbConnection();
    bool shouldClose = false;
    try
    {
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
            shouldClose = true;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = N78NeighbourCommandTimeoutSeconds;
        if (!string.IsNullOrWhiteSpace(projectPolygonWkt))
        {
            AddParameter(cmd, "@projectPolygonWkt", projectPolygonWkt);
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // ========= Backend processing: all type conversions done here =========
            data.Add(new LTE5GNeighbourDto
            {
                // --- Identity ---
                id           = reader.IsDBNull(0)  ? 0 : reader.GetInt32(0),
                session_id   = reader.IsDBNull(1)  ? 0 : reader.GetInt32(1),
                timestamp    = reader.IsDBNull(2)  ? default : reader.GetDateTime(2),
                lat          = reader.IsDBNull(3)  ? 0d : Convert.ToDouble(reader.GetValue(3)),
                lon          = reader.IsDBNull(4)  ? 0d : Convert.ToDouble(reader.GetValue(4)),
                indoor_outdoor = reader.IsDBNull(5) ? null : reader.GetString(5),
                provider       = reader.IsDBNull(6) ? null : reader.GetString(6),

                // --- Primary KPIs (float? in DB â†’ double? in DTO) ---
                primary_network = reader.IsDBNull(7)  ? null : reader.GetString(7),
                primary_band    = reader.IsDBNull(8)  ? null : reader.GetString(8),
                primary_pci     = reader.IsDBNull(9)  ? null : reader.GetString(9),
                primary_rsrp    = reader.IsDBNull(10) ? null : (double?)Convert.ToDouble(reader.GetValue(10)),
                primary_rsrq    = reader.IsDBNull(11) ? null : (double?)Convert.ToDouble(reader.GetValue(11)),
                primary_sinr    = reader.IsDBNull(12) ? null : (double?)Convert.ToDouble(reader.GetValue(12)),
                mos             = reader.IsDBNull(13) ? null : (double?)Convert.ToDouble(reader.GetValue(13)),

                // --- Throughput (DECIMAL(12,4) from CAST in SQL â†’ decimal? in DTO) ---
                dl_tpt = reader.IsDBNull(14) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(14)),
                ul_tpt = reader.IsDBNull(15) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(15)),

                // --- Neighbour KPIs (float? in DB â†’ double? in DTO) ---
                neighbour_network  = reader.IsDBNull(16) ? null : reader.GetString(16),
                neighbour_band     = reader.IsDBNull(17) ? null : reader.GetString(17),
                neighbour_pci      = reader.IsDBNull(18) ? null : reader.GetString(18),
                neighbour_rsrp     = reader.IsDBNull(19) ? null : (double?)Convert.ToDouble(reader.GetValue(19)),
                neighbour_rsrq     = reader.IsDBNull(20) ? null : (double?)Convert.ToDouble(reader.GetValue(20)),
                neighbour_sinr     = reader.IsDBNull(21) ? null : (double?)Convert.ToDouble(reader.GetValue(21)),
                neighbour_provider = reader.IsDBNull(22) ? null : reader.GetString(22),
                neighbour_dl_tpt   = reader.IsDBNull(23) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(23)),
                neighbour_ul_tpt   = reader.IsDBNull(24) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(24)),
            });
        }
    }
    finally
    {
        if (shouldClose && conn.State == System.Data.ConnectionState.Open)
            await conn.CloseAsync();
    }

    // Fallback: if polygon filtering produced no rows, retry once without polygon filter.
    if (data.Count == 0 && !string.IsNullOrWhiteSpace(projectPolygonWkt) && project_id.HasValue && project_id.Value > 0)
    {
        var fallbackSql = sql.Replace(
            "AND p.lat IS NOT NULL AND p.lon IS NOT NULL AND ST_Contains(ST_GeomFromText(@projectPolygonWkt, 4326), ST_SRID(POINT(p.lon, p.lat), 4326))",
            string.Empty
        );

        conn = db.Database.GetDbConnection();
        shouldClose = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
                shouldClose = true;
            }

            await using var fallbackCmd = conn.CreateCommand();
            fallbackCmd.CommandText = fallbackSql;
            fallbackCmd.CommandTimeout = N78NeighbourCommandTimeoutSeconds;

            await using var reader = await fallbackCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                data.Add(new LTE5GNeighbourDto
                {
                    id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    session_id = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    timestamp = reader.IsDBNull(2) ? default : reader.GetDateTime(2),
                    lat = reader.IsDBNull(3) ? 0d : Convert.ToDouble(reader.GetValue(3)),
                    lon = reader.IsDBNull(4) ? 0d : Convert.ToDouble(reader.GetValue(4)),
                    indoor_outdoor = reader.IsDBNull(5) ? null : reader.GetString(5),
                    provider = reader.IsDBNull(6) ? null : reader.GetString(6),
                    primary_network = reader.IsDBNull(7) ? null : reader.GetString(7),
                    primary_band = reader.IsDBNull(8) ? null : reader.GetString(8),
                    primary_pci = reader.IsDBNull(9) ? null : reader.GetString(9),
                    primary_rsrp = reader.IsDBNull(10) ? null : (double?)Convert.ToDouble(reader.GetValue(10)),
                    primary_rsrq = reader.IsDBNull(11) ? null : (double?)Convert.ToDouble(reader.GetValue(11)),
                    primary_sinr = reader.IsDBNull(12) ? null : (double?)Convert.ToDouble(reader.GetValue(12)),
                    mos = reader.IsDBNull(13) ? null : (double?)Convert.ToDouble(reader.GetValue(13)),
                    dl_tpt = reader.IsDBNull(14) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(14)),
                    ul_tpt = reader.IsDBNull(15) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(15)),
                    neighbour_network = reader.IsDBNull(16) ? null : reader.GetString(16),
                    neighbour_band = reader.IsDBNull(17) ? null : reader.GetString(17),
                    neighbour_pci = reader.IsDBNull(18) ? null : reader.GetString(18),
                    neighbour_rsrp = reader.IsDBNull(19) ? null : (double?)Convert.ToDouble(reader.GetValue(19)),
                    neighbour_rsrq = reader.IsDBNull(20) ? null : (double?)Convert.ToDouble(reader.GetValue(20)),
                    neighbour_sinr = reader.IsDBNull(21) ? null : (double?)Convert.ToDouble(reader.GetValue(21)),
                    neighbour_provider = reader.IsDBNull(22) ? null : reader.GetString(22),
                    neighbour_dl_tpt = reader.IsDBNull(23) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(23)),
                    neighbour_ul_tpt = reader.IsDBNull(24) ? null : (decimal?)Convert.ToDecimal(reader.GetValue(24)),
                });
            }
        }
        finally
        {
            if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                await conn.CloseAsync();
        }
    }

    // ================= 5. REDIS WRITE =================
    if (_redis != null && _redis.IsConnected)
    {
        try { await _redis.SetObjectAsync(cacheKey, data, ttlSeconds: 300); }
        catch { /* best-effort cache â€” don't fail the request */ }
    }

    Response.Headers["X-Cache"] = "MISS";

    return Ok(new
    {
        Status = 1,
        Cached = false,
        SessionCount = parsedIds.Count,
        RecordCount = data.Count,
        Data = data
    });
}


// [HttpGet("Get4GWith5GNeighbours")]
// public async Task<IActionResult> Get4GWith5GNeighbours([FromQuery] string session_ids)
// {
//     // ================= VALIDATION =================
//     if (string.IsNullOrWhiteSpace(session_ids))
//         return BadRequest(new { Status = 0, Message = "session_ids are required" });

//     var parsedIds = session_ids
//         .Split(',', StringSplitOptions.RemoveEmptyEntries)
//         .Select(x => int.TryParse(x.Trim(), out var v) ? v : 0)
//         .Where(v => v > 0)
//         .Distinct()
//         .ToList();

//     if (parsedIds.Count == 0)
//         return BadRequest(new { Status = 0, Message = "No valid session_ids" });

//     string sessionCsv = string.Join(",", parsedIds);
//     string cacheKey = $"4g_5g_neighbour_v2:{sessionCsv}";

//     // ================= REDIS READ =================
//     if (_redis != null && _redis.IsConnected)
//     {
//         try
//         {
//             var cached = await _redis.GetObjectAsync<List<LTE5GNeighbourDto>>(cacheKey);
//             if (cached != null)
//             {
//                 Response.Headers["X-Cache"] = "HIT";
//                 return Ok(new
//                 {
//                     Status = 1,
//                     Cached = true,
//                     SessionCount = parsedIds.Count,
//                     RecordCount = cached.Count,
//                     Data = cached
//                 });
//             }
//         }
//         catch { }
//     }

//     // ================= YOUR FAST WORKING SQL =================
//     var sql = $@"
//         SELECT 
//             p.id,
//             p.session_id,
//             p.timestamp,
//             p.lat,
//             p.lon,
//             p.indoor_outdoor,
//             p.network AS primary_network,
//             p.band AS primary_band,
//             p.rsrp AS primary_rsrp,
//             p.rsrq AS primary_rsrq,
//             p.sinr AS primary_sinr,
//             p.pci AS primary_pci,
//             p.m_alpha_long AS provider,
//             p.mos,
//             CAST(NULLIF(p.dl_tpt, '') AS DECIMAL(12,4)) AS dl_tpt,
//             CAST(NULLIF(p.ul_tpt, '') AS DECIMAL(12,4)) AS ul_tpt,
//             n.band AS neighbour_band,
//             n.rsrp AS neighbour_rsrp,
//             n.rsrq AS neighbour_rsrq,
//             n.pci AS neighbour_pci
//         FROM tbl_network_log p
//         INNER JOIN tbl_network_log_neighbour n 
//             ON p.lat = n.lat
//             AND p.lon = n.lon
//             AND p.session_id = n.session_id
//         WHERE 
//             p.network IN ('LTE', '4G', 'LTE-A', 'LTE+', 'LTE_CA')
//             AND n.band IN (
//                 'n1', 'n3', 'n5', 'n7', 'n8', 'n20', 'n28',
//                 'n38', 'n40', 'n41', 'n66', 'n71', 
//                 'n77', 'n78', 'n79',
//                 'N1', 'N3', 'N5', 'N7', 'N8', 'N20', 'N28',
//                 'N38', 'N40', 'N41', 'N66', 'N71',
//                 'N77', 'N78', 'N79'
//             )
//             AND p.session_id IN ({sessionCsv})
//             AND p.lat IS NOT NULL 
//             AND p.lon IS NOT NULL
//         ORDER BY p.session_id, p.timestamp;
//     ";

//     // ================= DB EXECUTION =================
//     var data = await db.LTE5GNeighbourDto
//         .FromSqlRaw(sql)
//         .AsNoTracking()
//         .ToListAsync();

//     // ================= REDIS WRITE =================
//     if (_redis != null && _redis.IsConnected)
//     {
//         try
//         {
//             await _redis.SetObjectAsync(cacheKey, data, ttlSeconds: 300);
//         }
//         catch { }
//     }

//     Response.Headers["X-Cache"] = "MISS";

//     return Ok(new
//     {
//         Status = 1,
//         Cached = false,
//         SessionCount = parsedIds.Count,
//         RecordCount = data.Count,
//         Data = data
//     });
// }









[HttpGet, Route("GetProviderWiseVolume")]
public async Task<JsonResult> GetProviderWiseVolume([FromQuery] MapFilter filters)
{
    string sessionIdsParam = HttpContext.Request.Query["session_ids"].ToString();
    if (string.IsNullOrWhiteSpace(sessionIdsParam))
        return Json(new { status = 0, message = "Invalid session_id" });

    var sessionIds = sessionIdsParam
        .Split(',')
        .Select(s => s.Trim())
        .Where(s => int.TryParse(s, out _))
        .Select(int.Parse)
        .ToList();

    if (!sessionIds.Any())
        return Json(new { status = 0, message = "Invalid session_id" });

    try
    {
        using var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        var cacheKey = BuildMapViewCacheKey(
            "provider-wise-volume-v15",
            sessionIdsParam,
            filters?.StartDate,
            filters?.EndDate,
            filters?.NetworkType ?? "all");
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Json(cached);

        // -----------------------------
        // Date Filters
        // -----------------------------
        object fromParam = filters.StartDate.HasValue
            ? filters.StartDate.Value
            : DBNull.Value;

        object toParam = filters.EndDate.HasValue
            ? filters.EndDate.Value.AddDays(1)
            : DBNull.Value;

        // -----------------------------
        // Provider Filter
        // -----------------------------
        string providerNormalized = null;
        if (!string.IsNullOrEmpty(filters.NetworkType) &&
            !filters.NetworkType.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var p = filters.NetworkType.ToLower();
            if (p.StartsWith("j")) providerNormalized = "jio";
            else if (p.StartsWith("a")) providerNormalized = "airtel";
            else if (p.StartsWith("v")) providerNormalized = "vodafone";
        }

        object providerParam = providerNormalized == null
            ? DBNull.Value
            : $"%{providerNormalized}%";

        // -----------------------------
        // Session IDs placeholders
        // -----------------------------
        string sessionIdsPlaceholder =
            string.Join(",", sessionIds.Select((_, i) => $"@sid{i}"));

        // -----------------------------
        // FINAL SQL
        // -----------------------------
string sql = $@"
WITH session_provider_counts AS (
    SELECT
        session_id,
        LOWER(TRIM(m_alpha_long)) AS provider,
        COUNT(*) AS provider_count
    FROM tbl_network_log
    WHERE session_id IN ({sessionIdsPlaceholder})
      AND m_alpha_long IS NOT NULL
      AND TRIM(m_alpha_long) <> ''
      AND LOWER(TRIM(m_alpha_long)) <> 'unknown'
    GROUP BY session_id, LOWER(TRIM(m_alpha_long))
),
session_providers AS (
    SELECT session_id, provider
    FROM (
        SELECT
            session_id,
            provider,
            ROW_NUMBER() OVER (
                PARTITION BY session_id
                ORDER BY provider_count DESC, provider
            ) AS rn
        FROM session_provider_counts
    ) ranked_providers
    WHERE rn = 1
),
base_logs AS (
    SELECT
        l.session_id,
        COALESCE(NULLIF(LOWER(TRIM(l.m_alpha_long)), ''), sp.provider) AS provider,
        CASE
            WHEN (
                    UPPER(CONCAT_WS(' ', COALESCE(l.network, ''), COALESCE(l.band, ''))) LIKE '%5G%'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%NR NSA%'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%NR SA%'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%NR-CA%'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%NR-DC%'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%VONR%'
                 OR UPPER(COALESCE(l.network, '')) LIKE '%LTE ANCHOR%'
                 OR UPPER(COALESCE(l.network, '')) LIKE '%LTE-ANCHOR%'
                 OR UPPER(COALESCE(l.network, '')) LIKE '%LTE_ANCHOR%'
                 OR UPPER(COALESCE(l.network, '')) LIKE '%ENDC%'
                 OR UPPER(COALESCE(l.network, '')) LIKE '%EN-DC%'
                 OR UPPER(COALESCE(l.network, '')) LIKE '%NSA%'
                 OR UPPER(CONCAT_WS(' ', COALESCE(l.network, ''), COALESCE(l.band, ''))) LIKE '%NRARFCN%'
                 OR UPPER(CONCAT_WS(' ', COALESCE(l.network, ''), COALESCE(l.band, ''))) LIKE '%MNR%'
                 OR UPPER(CONCAT_WS(' ', COALESCE(l.network, ''), COALESCE(l.band, ''))) LIKE '%NCI%'
                 OR UPPER(CONCAT_WS(' ', COALESCE(l.network, ''), COALESCE(l.band, ''))) REGEXP '(^|[^A-Z0-9])NR([^A-Z0-9]|$)'
                 OR UPPER(CONCAT_WS(' ', COALESCE(l.network, ''), COALESCE(l.band, ''))) REGEXP '(^|[^A-Z0-9])N[0-9]{{1,3}}([^A-Z0-9]|$)'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) = 'SA'
                 OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '% SA%'
                ) THEN '5G'
            WHEN UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%4G%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%LTE%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%LTE-A%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%LTE A%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%LTE CA%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%VOLTE%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%NB-IOT%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%LTE-M%' THEN '4G'
            WHEN UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%3G%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%WCDMA%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%UMTS%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%HSPA%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%DC-HSPA+%' THEN '3G'
            WHEN UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%2G%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%GSM%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%EDGE%'
              OR UPPER(TRIM(COALESCE(l.network, ''))) LIKE '%GPRS%' THEN '2G'
            ELSE 'Unknown'
        END AS tech,
        l.timestamp,
        CAST(l.total_rx_kb AS DECIMAL(18,4)) AS total_rx_kb,
        CAST(l.total_tx_kb AS DECIMAL(18,4)) AS total_tx_kb,
        CASE
            WHEN l.dl_tpt IS NOT NULL
              AND TRIM(CAST(l.dl_tpt AS CHAR)) REGEXP '^-?[0-9]+(\\.[0-9]+)?$'
            THEN CAST(l.dl_tpt AS DECIMAL(18,4))
            ELSE NULL
        END AS dl_tpt_mbps,
        CASE
            WHEN l.ul_tpt IS NOT NULL
              AND TRIM(CAST(l.ul_tpt AS CHAR)) REGEXP '^-?[0-9]+(\\.[0-9]+)?$'
            THEN CAST(l.ul_tpt AS DECIMAL(18,4))
            ELSE NULL
        END AS ul_tpt_mbps
    FROM tbl_network_log l
    LEFT JOIN session_providers sp ON sp.session_id = l.session_id
    WHERE l.session_id IN ({sessionIdsPlaceholder})
      AND COALESCE(NULLIF(LOWER(TRIM(l.m_alpha_long)), ''), sp.provider) IS NOT NULL
      AND COALESCE(NULLIF(LOWER(TRIM(l.m_alpha_long)), ''), sp.provider) <> 'unknown'
      AND (@from IS NULL OR l.timestamp >= @from)
      AND (@to IS NULL OR l.timestamp < @to)
      AND (@provider IS NULL OR COALESCE(NULLIF(LOWER(TRIM(l.m_alpha_long)), ''), sp.provider) LIKE @provider)
),
ordered_logs AS (
    SELECT
        session_id,
        provider,
        tech,
        timestamp,
        total_rx_kb,
        total_tx_kb,
        dl_tpt_mbps,
        ul_tpt_mbps,
        LAG(provider) OVER (
            PARTITION BY session_id
            ORDER BY timestamp
        ) AS prev_provider,
        LAG(tech) OVER (
            PARTITION BY session_id
            ORDER BY timestamp
        ) AS prev_tech,
        UNIX_TIMESTAMP(timestamp)
        - UNIX_TIMESTAMP(
            LAG(timestamp) OVER (
                PARTITION BY session_id
                ORDER BY timestamp
            )
        ) AS gap_sec
    FROM base_logs
),
filtered_logs AS (
    SELECT *
    FROM ordered_logs
    WHERE tech <> 'Unknown'
),
grouped_logs AS (
    SELECT
        filtered_logs.*,
        SUM(
            CASE
                WHEN prev_tech IS NULL THEN 1
                WHEN prev_provider <> provider THEN 1
                WHEN prev_tech <> tech THEN 1
                WHEN gap_sec IS NOT NULL AND gap_sec > 120 THEN 1
                ELSE 0
            END
        ) OVER (
            PARTITION BY session_id
            ORDER BY timestamp
        ) AS grp
    FROM filtered_logs
),
duration_intervals AS (
    SELECT
        session_id,
        provider,
        tech,
        grp,
        MIN(timestamp) AS start_time,
        MAX(timestamp) AS end_time,
        TIMESTAMPDIFF(SECOND, MIN(timestamp), MAX(timestamp)) AS duration_sec
    FROM grouped_logs
    GROUP BY session_id, provider, tech, grp
),
duration_summary AS (
    SELECT
        session_id,
        provider,
        tech,
        SUM(duration_sec) AS duration_sec
    FROM duration_intervals
    GROUP BY session_id, provider, tech
),
ranked_logs AS (
    SELECT
        filtered_logs.*,
        ROW_NUMBER() OVER (
            PARTITION BY session_id, provider, tech
            ORDER BY CASE WHEN NULLIF(dl_tpt_mbps, 0) IS NULL THEN 1 ELSE 0 END, dl_tpt_mbps
        ) AS dl_rn,
        COUNT(NULLIF(dl_tpt_mbps, 0)) OVER (
            PARTITION BY session_id, provider, tech
        ) AS dl_cnt,
        ROW_NUMBER() OVER (
            PARTITION BY session_id, provider, tech
            ORDER BY CASE WHEN NULLIF(ul_tpt_mbps, 0) IS NULL THEN 1 ELSE 0 END, ul_tpt_mbps
        ) AS ul_rn,
        COUNT(NULLIF(ul_tpt_mbps, 0)) OVER (
            PARTITION BY session_id, provider, tech
        ) AS ul_cnt
    FROM filtered_logs
)

SELECT
    rl.session_id,
    rl.provider,
    rl.tech,
    COUNT(*) AS sample_count,

	    -- Volume in GB
	    ROUND(GREATEST(MAX(rl.total_rx_kb) - MIN(rl.total_rx_kb), 0) / 1024 / 1024, 2) AS dl_gb,
	    ROUND(GREATEST(MAX(rl.total_tx_kb) - MIN(rl.total_tx_kb), 0) / 1024 / 1024, 2) AS ul_gb,

    -- Duration (INT64)
    COALESCE(MAX(ds.duration_sec), 0) AS duration_sec,

    -- Avg throughput comes from logged dl_tpt / ul_tpt samples, grouped by technology.
    ROUND(MIN(NULLIF(rl.dl_tpt_mbps, 0)), 3) AS min_dl_mbps,
    ROUND(MAX(NULLIF(rl.dl_tpt_mbps, 0)), 3) AS max_dl_mbps,
    ROUND(AVG(NULLIF(rl.dl_tpt_mbps, 0)), 3) AS avg_dl_mbps,
    ROUND(STDDEV_SAMP(NULLIF(rl.dl_tpt_mbps, 0)), 3) AS stddev_dl_mbps,
    ROUND(AVG(
        CASE
            WHEN rl.dl_cnt > 0
             AND rl.dl_rn IN (FLOOR((rl.dl_cnt + 1) / 2), FLOOR((rl.dl_cnt + 2) / 2))
            THEN NULLIF(rl.dl_tpt_mbps, 0)
            ELSE NULL
        END
    ), 3) AS median_dl_mbps,
    COUNT(DISTINCT NULLIF(rl.dl_tpt_mbps, 0)) AS distinct_dl_count,

    ROUND(MIN(NULLIF(rl.ul_tpt_mbps, 0)), 3) AS min_ul_mbps,
    ROUND(MAX(NULLIF(rl.ul_tpt_mbps, 0)), 3) AS max_ul_mbps,
    ROUND(AVG(NULLIF(rl.ul_tpt_mbps, 0)), 3) AS avg_ul_mbps,
    ROUND(STDDEV_SAMP(NULLIF(rl.ul_tpt_mbps, 0)), 3) AS stddev_ul_mbps,
    ROUND(AVG(
        CASE
            WHEN rl.ul_cnt > 0
             AND rl.ul_rn IN (FLOOR((rl.ul_cnt + 1) / 2), FLOOR((rl.ul_cnt + 2) / 2))
            THEN NULLIF(rl.ul_tpt_mbps, 0)
            ELSE NULL
        END
    ), 3) AS median_ul_mbps,
    COUNT(DISTINCT NULLIF(rl.ul_tpt_mbps, 0)) AS distinct_ul_count

FROM ranked_logs rl
LEFT JOIN duration_summary ds
    ON ds.session_id = rl.session_id
   AND ds.provider = rl.provider
   AND ds.tech = rl.tech
GROUP BY rl.session_id, rl.provider, rl.tech;
";

        var summary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        for (int i = 0; i < sessionIds.Count; i++)
            Add(cmd, $"@sid{i}", sessionIds[i]);

        Add(cmd, "@from", fromParam);
        Add(cmd, "@to", toParam);
        Add(cmd, "@provider", providerParam);

        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
{
    int sessionId = rd.GetInt32(0);
    
    // Fix: Handle potential DBNull for strings
    string provider = rd.IsDBNull(1) ? "unknown" : rd.GetString(1);
    string tech = rd.IsDBNull(2) ? "unknown" : rd.GetString(2);

    if (string.IsNullOrWhiteSpace(provider) ||
        provider.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(tech) ||
        tech.Equals("unknown", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    int sampleCount = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3));

    double dlGb = rd.IsDBNull(4) ? 0 : Convert.ToDouble(rd.GetValue(4));
    double ulGb = rd.IsDBNull(5) ? 0 : Convert.ToDouble(rd.GetValue(5));

    // Also verify your duration cast; if it's MySQL, it might be a Decimal/Double
    long duration = rd.IsDBNull(6) ? 0L : Convert.ToInt64(rd.GetValue(6));

    double minDlMbps = rd.IsDBNull(7) ? 0 : Convert.ToDouble(rd.GetValue(7));
    double maxDlMbps = rd.IsDBNull(8) ? 0 : Convert.ToDouble(rd.GetValue(8));
    double avgDlMbps = rd.IsDBNull(9) ? 0 : Convert.ToDouble(rd.GetValue(9));
    double stddevDlMbps = rd.IsDBNull(10) ? 0 : Convert.ToDouble(rd.GetValue(10));
    double medianDlMbps = rd.IsDBNull(11) ? 0 : Convert.ToDouble(rd.GetValue(11));
    int distinctDlCount = rd.IsDBNull(12) ? 0 : Convert.ToInt32(rd.GetValue(12));

    double minUlMbps = rd.IsDBNull(13) ? 0 : Convert.ToDouble(rd.GetValue(13));
    double maxUlMbps = rd.IsDBNull(14) ? 0 : Convert.ToDouble(rd.GetValue(14));
    double avgUlMbps = rd.IsDBNull(15) ? 0 : Convert.ToDouble(rd.GetValue(15));
    double stddevUlMbps = rd.IsDBNull(16) ? 0 : Convert.ToDouble(rd.GetValue(16));
    double medianUlMbps = rd.IsDBNull(17) ? 0 : Convert.ToDouble(rd.GetValue(17));
    int distinctUlCount = rd.IsDBNull(18) ? 0 : Convert.ToInt32(rd.GetValue(18));

            string sessionKey = sessionId.ToString();

            if (!summary.ContainsKey(sessionKey))
                summary[sessionKey] = new Dictionary<string, object>();

            var sessionDict = (Dictionary<string, object>)summary[sessionKey];

            if (!sessionDict.ContainsKey(provider))
                sessionDict[provider] = new Dictionary<string, object>();

            var providerDict = (Dictionary<string, object>)sessionDict[provider];

            providerDict[tech] = new
            {
                sample_count = sampleCount,
                dl_gb = dlGb,
                ul_gb = ulGb,
                duration_sec = duration,
                min_dl_mbps = minDlMbps,
                max_dl_mbps = maxDlMbps,
                avg_dl_mbps = avgDlMbps,
                stddev_dl_mbps = stddevDlMbps,
                median_dl_mbps = medianDlMbps,
                distinct_dl_count = distinctDlCount,
                is_constant_dl_tpt = sampleCount > 1 && distinctDlCount == 1,
                min_ul_mbps = minUlMbps,
                max_ul_mbps = maxUlMbps,
                avg_ul_mbps = avgUlMbps,
                stddev_ul_mbps = stddevUlMbps,
                median_ul_mbps = medianUlMbps,
                distinct_ul_count = distinctUlCount,
                is_constant_ul_tpt = sampleCount > 1 && distinctUlCount == 1,
                is_constant_tpt = sampleCount > 1 && distinctDlCount == 1 && distinctUlCount == 1
            };
        }

        var response = new
        {
            status = 1,
            tpt_provider_summary = summary
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Json(response);
    }
    catch (Exception ex)
    {
        return Json(new { status = 0, message = SafeException.Get(ex) });
    }
}


// add the sessions dataa in thiss 


[HttpGet("sessionsDistance")]
public async Task<IActionResult> GetTotalSessionDistance(
    [FromQuery] string sessionIds
)
{
    if (string.IsNullOrEmpty(sessionIds))
        return BadRequest("SessionIds required");

    var ids = sessionIds
        .Split(',')
        .Select(int.Parse)
        .ToList();

    var cacheKey = BuildMapViewCacheKey("sessions-distance", sessionIds);
    var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
    if (cached != null)
        return Ok(cached);

    var totalDistance = await db.tbl_session
        .AsNoTracking()  
        .Where(x => ids.Contains((int)x.id) && x.distance != null)
        .SumAsync(x => (double?)x.distance) ?? 0;

    var response = new
    {
        Status = 1,
        SessionCount = ids.Count,
        TotalDistanceKm = Math.Round(totalDistance, 2)
    };
    await SetMapViewCacheAsync(cacheKey, response);
    return Ok(response);
}

private (double? lat, double? lon) ApplyMeterOffset(double? lat, double? lon, double meters)
{
    if (!lat.HasValue || !lon.HasValue)
        return (lat, lon);

    const double METERS_PER_DEG_LAT = 111320.0;

    double latOffset = meters / METERS_PER_DEG_LAT;
    double lonOffset = meters / (METERS_PER_DEG_LAT * Math.Cos(lat.Value * Math.PI / 180));

    return (
        lat.Value + latOffset,
        lon.Value + lonOffset
    );
}

[HttpGet, Route("GetNeighbourLogsByDateRange")]
public async Task<IActionResult> GetNeighbourLogsByDateRange(
    [FromQuery] LogFilterModel filters,
    [FromQuery] int? company_id = null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        const int PAGE_SIZE = 150000;
        const int MaxGapSeconds = 300;
        const double OFFSET_METERS = 0;

        filters ??= new LogFilterModel();

        // =========================================================
        // 1. STRICT SECURITY: RESOLVE COMPANY ID
        // =========================================================
        bool isSuperAdmin = _userScope.IsSuperAdmin(User);
        int targetCompanyId = GetTargetCompanyId(company_id);
        int currentUserId = GetCurrentUserId();
        bool useUserScope = !isSuperAdmin && targetCompanyId == 0 && currentUserId > 0;

        if (!isSuperAdmin && targetCompanyId == 0 && !useUserScope)
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve Company Context." });

        var cacheKey = BuildMapViewCacheKey(
            "neighbour-logs-date-range",
            targetCompanyId,
            useUserScope ? currentUserId : 0,
            filters.StartDate,
            filters.StartTime,
            filters.EndDate,
            filters.EndTime,
            filters.Provider,
            filters.PolygonId,
            filters.CursorTs);

        var cached = await TryGetMapViewCacheAsync<DateRangeLogResponse>(cacheKey);
        if (cached != null)
        {
            sw.Stop();
            Response.Headers["X-Cache"] = "HIT";
            Response.Headers["X-Total-Ms"] = sw.ElapsedMilliseconds.ToString();
            Response.Headers["X-Row-Count"] = cached.data.Count.ToString();
            return Json(cached);
        }

        // ==============================
        // 2. MERGE DATE + TIME
        // ==============================
        DateTime? startDateTime = null;
        DateTime? endDateTime = null;

        if (filters.StartDate.HasValue)
            startDateTime = filters.StartDate.Value.Date.Add(filters.StartTime ?? TimeSpan.Zero);

        if (filters.EndDate.HasValue)
            endDateTime = filters.EndDate.Value.Date.Add(filters.EndTime ?? new TimeSpan(23, 59, 59));

        // ==============================
        // 3. OPTIMIZED QUERY CONSTRUCTION
        // ==============================
        IQueryable<tbl_network_log_neighbour> baseQuery;

        if (targetCompanyId == 0 && !useUserScope)
        {
            // PATH A: Super Admin (Global) - Direct Table Access
            baseQuery = db.tbl_network_log_neighbour
                .AsNoTracking()
                .Where(x => x.timestamp != null);
        }
        else
        {
            // PATH B: Company/User scoped - Secure Join
            baseQuery = from n in db.tbl_network_log_neighbour.AsNoTracking()
                        join s in db.tbl_session.AsNoTracking() on n.session_id equals s.id
                        join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                        where (useUserScope ? s.user_id == currentUserId : u.company_id == targetCompanyId)
                              && n.timestamp != null
                        select n;
        }

        // ==============================
        // 4. APPLY FILTERS
        // ==============================
        if (startDateTime.HasValue)
            baseQuery = baseQuery.Where(x => x.timestamp >= startDateTime.Value);

        if (endDateTime.HasValue)
            baseQuery = baseQuery.Where(x => x.timestamp <= endDateTime.Value);

        // ðŸ”‘ KEYSET PAGINATION
        if (filters.CursorTs.HasValue)
            baseQuery = baseQuery.Where(x => x.timestamp > filters.CursorTs.Value);

        // ðŸ”’ 5G NR BAND FILTER (n78 / n*)
        baseQuery = baseQuery.Where(x => x.band != null && EF.Functions.Like(x.band.ToLower(), "n%"));

        if (!string.IsNullOrWhiteSpace(filters.Provider))
        {
            string provider = filters.Provider.Trim();
            // Using Like often performs better with indexes than Contains in some SQL configs
            baseQuery = baseQuery.Where(x => x.m_alpha_long != null && EF.Functions.Like(x.m_alpha_long, $"%{provider}%"));
        }

        if (filters.PolygonId.HasValue)
            baseQuery = baseQuery.Where(x => x.polygon_id == filters.PolygonId.Value);

        // ==============================
        // 5. EXECUTE & PROJECT
        // ==============================
        var rows = await baseQuery
            .OrderBy(x => x.timestamp)
            .Take(PAGE_SIZE)
            .Select(l => new
            {
                l.id,
                session_id = l.session_id != null ? (int)l.session_id : 0,
                l.lat,
                l.lon,
                l.rsrp,
                l.rsrq,
                l.sinr,
                l.network,
                l.band,
                l.pci,
                l.timestamp,
                provider = l.m_alpha_long,
                l.dl_tpt,
                l.ul_tpt,
                l.mos,
                l.polygon_id,
                l.image_path,
                l.nodeb_id,
                l.apps,
                l.Speed,
                neighbour_count = 1
            })
            .ToListAsync();

        if (rows.Count == 0)
        {
            var emptyResponse = new DateRangeLogResponse
            {
                data = new List<DateRangeLogItem>(),
                app_summary = new Dictionary<string, object>(),
                next_cursor = null
            };

            await SetMapViewCacheAsync(cacheKey, emptyResponse, 60);

            Response.Headers["X-Cache"] = "MISS";
            Response.Headers["X-Total-Ms"] = sw.ElapsedMilliseconds.ToString();
            Response.Headers["X-Row-Count"] = "0";
            return Json(emptyResponse);
        }

        // ==============================
        // 6. APP SUMMARY (IN-MEMORY)
        // ==============================
        var aggByApp = new Dictionary<string, AppAgg>(StringComparer.OrdinalIgnoreCase);

        foreach (var l in rows)
        {
            var appName = DetectAppName(l.apps);
            if (string.IsNullOrEmpty(appName) || !l.timestamp.HasValue)
                continue;

            if (!aggByApp.TryGetValue(appName, out var agg))
                aggByApp[appName] = agg = new AppAgg(appName);

            agg.SampleCount++;

            var v1 = AsDouble(l.rsrp); if (v1.HasValue) { agg.RsrpSum += v1.Value; agg.RsrpCnt++; }
            var v2 = AsDouble(l.rsrq); if (v2.HasValue) { agg.RsrqSum += v2.Value; agg.RsrqCnt++; }
            var v3 = AsDouble(l.sinr); if (v3.HasValue) { agg.SinrSum += v3.Value; agg.SinrCnt++; }
            var v4 = AsDouble(l.mos);  if (v4.HasValue) { agg.MosSum += v4.Value; agg.MosCnt++; }
            var v5 = AsDouble(l.dl_tpt); if (v5.HasValue) { agg.DlSum += v5.Value; agg.DlCnt++; }
            var v6 = AsDouble(l.ul_tpt); if (v6.HasValue) { agg.UlSum += v6.Value; agg.UlCnt++; }

            if (agg.PreviousTimestamp.HasValue)
            {
                var delta = (l.timestamp.Value - agg.PreviousTimestamp.Value).TotalSeconds;
                if (delta > 0 && delta <= MaxGapSeconds)
                    agg.ActiveDurationSeconds += (int)delta;
            }

            agg.PreviousTimestamp = l.timestamp;
        }

        var appSummary = aggByApp.ToDictionary(k => k.Key, v =>
        {
            var a = v.Value;
            return (object)new AppSummaryResult
            {
                appName = a.AppName,
                SampleCount = a.SampleCount,
                avgRsrp = a.RsrpCnt > 0 ? Math.Round(a.RsrpSum / a.RsrpCnt, 2) : 0,
                avgRsrq = a.RsrqCnt > 0 ? Math.Round(a.RsrqSum / a.RsrqCnt, 2) : 0,
                avgSinr = a.SinrCnt > 0 ? Math.Round(a.SinrSum / a.SinrCnt, 2) : 0,
                avgMos = a.MosCnt > 0 ? Math.Round(a.MosSum / a.MosCnt, 2) : 0,
                avgDl = a.DlCnt > 0 ? Math.Round(a.DlSum / a.DlCnt, 2) : 0,
                avgUl = a.UlCnt > 0 ? Math.Round(a.UlSum / a.UlCnt, 2) : 0,
                durationSeconds = a.ActiveDurationSeconds,
                durationHHMMSS = TimeSpan.FromSeconds(a.ActiveDurationSeconds).ToString(@"hh\:mm\:ss")
            };
        });

        // ==============================
        // 7. BUILD RESPONSE DATA
        // ==============================
        var dataItems = rows.Select(l =>
        {
            var tech = TechClassifier.Classify(l.band, l.network, null, null);
            var offset = ApplyMeterOffset(l.lat, l.lon, OFFSET_METERS);

            return new DateRangeLogItem
            {
                id = l.id,
                session_id = (int)l.session_id,
                lat = offset.lat,
                lon = offset.lon,
                rsrp = l.rsrp,
                rsrq = l.rsrq,
                sinr = l.sinr,
                network = l.network,
                band = l.band,
                pci = l.pci,
                timestamp = l.timestamp,
                provider = l.provider,
                dl_tpt = l.dl_tpt,
                ul_tpt = l.ul_tpt,
                mos = l.mos,
                polygon_id = l.polygon_id,
                image_path = l.image_path,
                nodeb_id = l.nodeb_id,
                neighbour_count = l.neighbour_count,
                apps = l.apps,
                app_name = DetectAppName(l.apps),
                radio = tech.Radio,
                mode = tech.Mode,
                is4g = tech.Is4G,
                is5g = tech.Is5G,
                isNsa = tech.IsNsa,
                Speed = l.Speed
            };
        }).ToList();

        // ==============================
        // 8. FINAL RESPONSE
        // ==============================
        var response = new DateRangeLogResponse
        {
            data = dataItems,
            app_summary = appSummary,
            next_cursor = rows.Last().timestamp
        };

        await SetMapViewCacheAsync(cacheKey, response);

        Response.Headers["X-Total-Ms"] = sw.ElapsedMilliseconds.ToString();
        Response.Headers["X-Row-Count"] = rows.Count.ToString();
        Response.Headers["X-Cache"] = "MISS";

        return Json(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            error = SafeException.Get(ex),
            inner = SafeException.Get(ex?.InnerException)
        });
    }
}
[HttpGet, Route("GetLogsByDateRange")]
public async Task<IActionResult> GetLogsByDateRange(
    [FromQuery] LogFilterModel filters,
    [FromQuery] int? company_id = null
)
{
    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        const int MaxRows = 50000;
        const int MaxGapSeconds = 300;
        filters ??= new LogFilterModel();

        // =========================================================
        // 1. ROBUST SECURITY: RESOLVE COMPANY ID
        // =========================================================
        bool isSuperAdmin = _userScope.IsSuperAdmin(User);
        int targetCompanyId = GetTargetCompanyId(company_id);
        int currentUserId = GetCurrentUserId();
        bool useUserScope = !isSuperAdmin && targetCompanyId == 0 && currentUserId > 0;

        if (!isSuperAdmin && targetCompanyId == 0 && !useUserScope)
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve Company Context." });

        // =========================================================
        // 2. BUILD DATES & CACHE KEY
        // =========================================================
        DateTime? startDateTime = null;
        DateTime? endDateTime = null;

        if (filters.StartDate.HasValue)
            startDateTime = filters.StartDate.Value.Date.Add(filters.StartTime ?? TimeSpan.Zero);

        if (filters.EndDate.HasValue)
            endDateTime = filters.EndDate.Value.Date.Add(filters.EndTime ?? new TimeSpan(23, 59, 59));

        string cacheKey = BuildDateRangeCacheKey(filters, targetCompanyId, useUserScope ? currentUserId : 0);

        // =========================================================
        // 3. TRY REDIS CACHE
        // =========================================================
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var cached = await _redis.GetObjectAsync<DateRangeLogResponse>(cacheKey);
                if (cached != null)
                {
                    totalStopwatch.Stop();
                    Response.Headers["X-Cache"] = "HIT";
                    return Json(cached);
                }
            }
            catch { }
        }

        // =========================================================
        // 4. QUERY CONSTRUCTION (OPTIMIZED SPLIT PATH)
        // =========================================================
        IQueryable<tbl_network_log> query;

        if (targetCompanyId == 0 && !useUserScope)
        {
            // PATH A: SUPER ADMIN (Global View) - Fast Path (No Joins)
            // Fastest possible query directly on the log table
            query = db.tbl_network_log.AsNoTracking();
        }
        else
        {
            // PATH B: Company/User scoped - Secure Path (Joins)
            query = from l in db.tbl_network_log.AsNoTracking()
                    join s in db.tbl_session.AsNoTracking() on l.session_id equals s.id
                    join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                    where useUserScope ? s.user_id == currentUserId : u.company_id == targetCompanyId
                    select l;
        }

        // Apply Filters
        if (startDateTime.HasValue) 
            query = query.Where(x => x.timestamp >= startDateTime.Value);
        
        if (endDateTime.HasValue) 
            query = query.Where(x => x.timestamp <= endDateTime.Value);

        if (!string.IsNullOrWhiteSpace(filters.Provider))
        {
            var provider = filters.Provider.Trim();
            // Use LIKE for better index usage
            query = query.Where(x => x.m_alpha_long != null && EF.Functions.Like(x.m_alpha_long, $"%{provider}%"));
        }

        if (filters.PolygonId.HasValue)
            query = query.Where(x => x.polygon_id == filters.PolygonId.Value);

        // Filter for Registered points only (Optimize Index Usage)
        query = query.Where(x => x.primary_cell_info_1.Contains("mRegistered=YES"));

        // =========================================================
        // 5. FAST PROJECTION (NO N+1 SUBQUERIES)
        // =========================================================
        var rawData = await query
            .OrderBy(x => x.timestamp)
            .Take(MaxRows)
            .Select(log => new
            {
                log.id,
                log.session_id,
                log.lat,
                log.lon,
                log.rsrp,
                log.rsrq,
                log.sinr,
                log.network,
                log.band,
                log.pci,
                log.timestamp,
                provider = log.m_alpha_long,
                log.dl_tpt,
                log.ul_tpt,
                log.mos,
                log.polygon_id,
                log.image_path,
                log.dls,
                log.uls,
                log.primary_cell_info_1,
                log.all_neigbor_cell_info,
                log.nodeb_id,
                log.apps,
                log.Speed
            })
            .ToListAsync();

        if (rawData.Count == 0)
        {
            var empty = new DateRangeLogResponse { data = new List<DateRangeLogItem>(), app_summary = new Dictionary<string, object>() };
            if (_redis != null && _redis.IsConnected) await _redis.SetObjectAsync(cacheKey, empty, 60);
            return Json(empty);
        }

        // =========================================================
        // 6. IN-MEMORY PROCESSING (Fast CPU work)
        // =========================================================
        var aggByApp = new Dictionary<string, AppAgg>(StringComparer.OrdinalIgnoreCase);
        var finalResultList = new List<DateRangeLogItem>(rawData.Count);

        foreach (var l in rawData)
        {
            // A. Detect App Name
            var appName = DetectAppName(l.apps);
            if (!string.IsNullOrEmpty(appName) && l.timestamp.HasValue)
            {
                if (!aggByApp.TryGetValue(appName, out var agg))
                {
                    agg = new AppAgg(appName);
                    aggByApp[appName] = agg;
                }
                agg.SampleCount++;
                agg.AddSample(l.timestamp.Value, l.rsrp, l.rsrq, l.sinr, l.mos, l.dl_tpt, l.ul_tpt);
            }

            // B. Classify Technology
            var tech = TechClassifier.Classify(l.band, l.network, l.primary_cell_info_1, l.all_neigbor_cell_info);

            // C. Build Item
            finalResultList.Add(new DateRangeLogItem
            {
                id = l.id,
                session_id = (int)l.session_id,
                lat = l.lat,
                lon = l.lon,
                rsrp = l.rsrp,
                rsrq = l.rsrq,
                sinr = l.sinr,
                network = l.network,
                band = l.band,
                pci = l.pci,
                timestamp = l.timestamp,
                provider = l.provider,
                dl_tpt = l.dl_tpt,
                ul_tpt = l.ul_tpt,
                mos = l.mos,
                polygon_id = l.polygon_id,
                image_path = l.image_path,
                nodeb_id = l.nodeb_id,
                neighbour_count = 0, // Disabled for performance (Saves 50k DB calls)
                apps = l.apps,
                app_name = appName,
                radio = tech.Radio,
                mode = tech.Mode,
                is4g = tech.Is4G,
                is5g = tech.Is5G,
                isNsa = tech.IsNsa,
                Speed = l.Speed
            });
        }

        var appSummary = aggByApp.ToDictionary(x => x.Key, x => (object)x.Value.ToResult(MaxGapSeconds));

        // =========================================================
        // 7. BUILD RESPONSE & CACHE
        // =========================================================
        var response = new DateRangeLogResponse
        {
            data = finalResultList,
            app_summary = appSummary
        };

        if (_redis != null && _redis.IsConnected)
            await _redis.SetObjectAsync(cacheKey, response, 300);

        totalStopwatch.Stop();
        Response.Headers["X-Cache"] = "MISS";
        Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
        Response.Headers["X-Row-Count"] = finalResultList.Count.ToString();

        return Json(response);
    }
    catch (Exception)
    {
        Console.WriteLine("Stack trace available in server logs.");
        return StatusCode(500, new { error = "An internal server error occurred." });
    }
}

       private int GetTargetCompanyId(int? company_id)
{
    
    return _userScope.GetTargetCompanyId(User, company_id);
}

        private int GetCurrentUserId()
        {
            return TryParseInt(User?.FindFirst("UserId")?.Value)
                ?? TryParseInt(User?.FindFirst("user_id")?.Value)
                ?? HttpContext?.Session?.GetInt32("UserID")
                ?? TryParseInt(HttpContext?.Session?.GetString("UserID"))
                ?? 0;
        }

        private static int? TryParseInt(string? value)
        {
            return int.TryParse(value, out var parsed) ? parsed : null;
        }

        private async Task TryUpdateProjectLogGridAsync(int projectId, string? logGrid)
        {
            if (projectId <= 0 || string.IsNullOrWhiteSpace(logGrid))
                return;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;
            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var projectColumns = await GetTableColumnSetAsync(conn, "tbl_project");
                if (!projectColumns.Contains("log_grid"))
                    return;

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE tbl_project SET log_grid = @logGrid WHERE id = @projectId;";
                if (db.Database.CurrentTransaction != null)
                    cmd.Transaction = db.Database.CurrentTransaction.GetDbTransaction();

                AddParam(cmd, "@logGrid", logGrid);
                AddParam(cmd, "@projectId", projectId);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Older deployments may not have log_grid; project creation should still succeed.
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        private async Task TryUpdateProjectSiteSizeAsync(int projectId, decimal siteSize)
        {
            if (projectId <= 0 || siteSize <= 0)
                return;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;
            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var projectColumns = await GetTableColumnSetAsync(conn, "tbl_project");
                if (!projectColumns.Contains("sitesize"))
                    return;

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE tbl_project SET sitesize = @siteSize WHERE id = @projectId;";
                if (db.Database.CurrentTransaction != null)
                    cmd.Transaction = db.Database.CurrentTransaction.GetDbTransaction();

                AddParam(cmd, "@siteSize", siteSize);
                AddParam(cmd, "@projectId", projectId);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Older deployments may not have sitesize yet; project creation should still succeed.
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        private string BuildDateRangeCacheKey(LogFilterModel filters, int companyId, int userId = 0)
{
    string provider = string.IsNullOrWhiteSpace(filters.Provider)
        ? "all"
        : filters.Provider.Trim().ToLower();

    string fromDate = filters.StartDate?.ToString("yyyyMMdd") ?? "null";
    string toDate = filters.EndDate?.ToString("yyyyMMdd") ?? "null";
    string polygonId = filters.PolygonId?.ToString() ?? "null";

    return $"daterangelog:company:{companyId}:user:{userId}:{provider}:{fromDate}:{toDate}:{polygonId}";
}
/// <summary>
/// 
/// </summary>
// ========================================
//  RESPONSE DTOs FOR CACHING
// ========================================
public class DateRangeLogResponse
{
            internal DateTime? next_cursor;

            public List<DateRangeLogItem> data { get; set; } = new();
    public Dictionary<string, object> app_summary { get; set; } = new();
}

public class DateRangeLogItem
{
    public int id { get; set; }
    public int session_id { get; set; }
    public double? lat { get; set; }
    public double? lon { get; set; }
    public double? rsrp { get; set; }
    public double? rsrq { get; set; }
    public double? sinr { get; set; }
    public string? network { get; set; }
    public string? band { get; set; }
    public string? pci { get; set; }
    public DateTime? timestamp { get; set; }
    public string? provider { get; set; }
    public string? dl_tpt { get; set; }
    public string? ul_tpt { get; set; }
    public double? mos { get; set; }
    public int? polygon_id { get; set; }
    public string? image_path { get; set; }
    public string? nodeb_id { get; set; }
    public int neighbour_count { get; set; }
    public string? apps { get; set; }
    public string? app_name { get; set; }
    public string? radio { get; set; }
    public string? mode { get; set; }
    public bool is4g { get; set; }
    public bool is5g { get; set; }
    public bool isNsa { get; set; }
            public float? Speed { get; internal set; }
        }

public class AppSummaryResult
{
    public string appName { get; set; } = "";
    public int SampleCount { get; set; }
    public double avgRsrp { get; set; }
    public double avgRsrq { get; set; }
    public double avgSinr { get; set; }
    public double avgMos { get; set; }
    public double avgDl { get; set; }
    public double avgUl { get; set; }
    public int durationSeconds { get; set; }
    public string durationHHMMSS { get; set; } = "00:00:00";
}

public class OperatorTechTimeFilter
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }

    public string? Operator { get; set; }      // Airtel / Jio / etc
    public string? Technology { get; set; }    // 4G / 5G / NSA / SA
}



[HttpGet]
[Route("GetTotalUsageTime")]
public async Task<IActionResult> GetTotalUsageTime(
    [FromQuery] OperatorTechTimeFilter filter)
{
    try
    {
        const int MAX_GAP_SECONDS = 300;

        // =====================================
        // 1ï¸ Date + Time combine
        // =====================================
        var startDateTime = filter.StartDate.Date
            .Add(filter.StartTime ?? TimeSpan.Zero);

        var endDateTime = filter.EndDate.Date
            .Add(filter.EndTime ?? new TimeSpan(23, 59, 59));

        var cacheKey = BuildMapViewCacheKey(
            "total-usage-time",
            filter.StartDate,
            filter.EndDate,
            filter.StartTime,
            filter.EndTime,
            filter.Operator,
            filter.Technology);
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        // =====================================
        // 2ï¸ Base query (PRIMARY only)
        // =====================================
        var baseQuery = db.tbl_network_log
            .AsNoTracking()
            .Where(x =>
                x.timestamp >= startDateTime &&
                x.timestamp <= endDateTime &&
                x.primary_cell_info_1.Contains("mRegistered=YES"));

        // OPTIONAL operator filter
        if (!string.IsNullOrWhiteSpace(filter.Operator))
        {
            baseQuery = baseQuery.Where(x =>
                x.m_alpha_long != null &&
                x.m_alpha_long.Contains(filter.Operator));
        }

        var logs = await baseQuery
            .OrderBy(x => x.session_id)
            .ThenBy(x => x.timestamp)
            .Select(x => new
            {
                x.session_id,
                x.timestamp,
                Operator = x.m_alpha_long,
                x.band,
                x.network,
                x.primary_cell_info_1,
                x.all_neigbor_cell_info
            })
            .ToListAsync();

        if (logs.Count < 2)
        {
            var empty = new
            {
                StartDateTime = startDateTime,
                EndDateTime = endDateTime,
                TotalSeconds = 0,
                TotalTime = "00:00:00",
                Breakdown = new List<object>()
            };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        // =====================================
        // 3ï¸ TIME CALCULATION
        // =====================================
        double totalSeconds = 0;
        var breakdown = new Dictionary<string, double>();

        foreach (var session in logs.GroupBy(x => x.session_id))
        {
            DateTime? prevTs = null;
            string? prevKey = null;

            foreach (var row in session)
            {
                var tech = TechClassifier.Classify(
                    row.band,
                    row.network,
                    row.primary_cell_info_1,
                    row.all_neigbor_cell_info
                );

                string technology =
                    tech.Is5G ? "5G" :
                    tech.Is4G ? "4G" :
                    "UNKNOWN";

                // OPTIONAL technology filter
                if (!string.IsNullOrWhiteSpace(filter.Technology))
                {
                    if (!technology.Equals(filter.Technology,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        prevTs = null;
                        prevKey = null;
                        continue;
                    }
                }

                string operatorName =
                    string.IsNullOrWhiteSpace(row.Operator)
                        ? "UNKNOWN"
                        : row.Operator;

                string key = $"{operatorName}||{technology}";

                if (prevTs.HasValue && prevKey == key)
                {
                    var diff =
                        (row.timestamp.Value - prevTs.Value).TotalSeconds;

                    if (diff > 0 && diff <= MAX_GAP_SECONDS)
                    {
                        totalSeconds += diff;

                        if (!breakdown.ContainsKey(key))
                            breakdown[key] = 0;

                        breakdown[key] += diff;
                    }
                }

                prevTs = row.timestamp;
                prevKey = key;
            }
        }

        // =====================================
        // 4ï¸ FORMAT BREAKDOWN RESPONSE
        // =====================================
        var breakdownList = breakdown.Select(kv =>
        {
            var parts = kv.Key.Split("||");
            int sec = (int)kv.Value;

            return new
            {
                Operator = parts[0],
                Technology = parts[1],
                Seconds = sec,
                Time = $"{sec / 3600:D2}:{(sec % 3600) / 60:D2}:{sec % 60:D2}"
            };
        }).OrderByDescending(x => x.Seconds)
          .ToList();

        int totalSec = (int)totalSeconds;

        var response = new
        {
            StartDateTime = startDateTime,
            EndDateTime = endDateTime,
            TotalSeconds = totalSec,
            TotalTime = $"{totalSec / 3600:D2}:{(totalSec % 3600) / 60:D2}:{totalSec % 60:D2}",
            Breakdown = breakdownList
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Ok(response);
    }
    catch (Exception)
    {
        Console.WriteLine("Stack trace available in server logs.");
        return StatusCode(500, new { error = "An internal server error occurred." });
    }
}




private object BuildResponse(
    OperatorTechTimeFilter filter,
    DateTime start,
    DateTime end,
    double totalSeconds)
{
    int sec = (int)totalSeconds;

    return new
    {
        StartDateTime = start,
        EndDateTime = end,

        Operator = string.IsNullOrWhiteSpace(filter.Operator)
            ? "ALL"
            : filter.Operator,

        Technology = string.IsNullOrWhiteSpace(filter.Technology)
            ? "ALL"
            : filter.Technology,

        TotalSeconds = sec,
        TotalTime = $"{sec / 3600:D2}:{(sec % 3600) / 60:D2}:{sec % 60:D2}"
    };
}






// indoor outdoor main work




  
private static string DetectTechnology(string? network)
{
    if (string.IsNullOrWhiteSpace(network))
        return "UNKNOWN";

    var n = network.Trim().ToUpper();

    // 5G
    if (n.Contains("NR") || n.Contains("5G"))
    {
        if (n.Contains("NSA"))
            return "5G NSA";
        if (n.Contains("SA"))
            return "5G SA";

        return "5G";
    }

    // 4G
    if (n.Contains("LTE"))
    {
        if (n.Contains("CA") || n.Contains("ADV"))
            return "4G LTE-A";

        return "4G";
    }

    // 3G
    if (n.Contains("WCDMA") || n.Contains("UMTS") || n.Contains("HSPA"))
        return "3G";

    // 2G
    if (n.Contains("GSM") || n.Contains("EDGE") || n.Contains("GPRS"))
        return "2G";

    // Future-safe fallback
    return n;
}

[HttpGet]
[Route("GetIndoorOutdoorSessionAnalytics")]
public async Task<IActionResult> GetIndoorOutdoorSessionAnalytics(
    [FromQuery] IndoorOutdoorSessionFilter filter)
{
    try
    {
        const int MAX_GAP_SECONDS = 300;

        // =============================
        // 1ï¸ VALIDATION
        // =============================
        if (string.IsNullOrWhiteSpace(filter.SessionIds))
            return BadRequest("SessionIds are required");

        var sessionIds = filter.SessionIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToList();

        string? indoorFilter = filter.IndoorOutdoor?.Trim().ToUpper();
        string? operatorFilter = filter.Operator?.Trim().ToLower();
        string? techFilter = filter.Technology?.Trim().ToUpper();

        var cacheKey = BuildMapViewCacheKey(
            "indoor-outdoor-session-analytics",
            filter.SessionIds,
            indoorFilter ?? "all",
            operatorFilter ?? "all",
            techFilter ?? "all");
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        // =============================
        // 2ï¸ BASE QUERY (PRIMARY ONLY)
        // =============================
        var query = db.tbl_network_log
            .AsNoTracking()
            .Where(x =>
                sessionIds.Contains((int)x.session_id) &&
                x.primary_cell_info_1 != null &&
                x.primary_cell_info_1.Contains("mRegistered=YES"));

        if (!string.IsNullOrWhiteSpace(indoorFilter))
        {
            query = query.Where(x =>
                x.indoor_outdoor != null &&
                x.indoor_outdoor.ToUpper() == indoorFilter);
        }

        if (!string.IsNullOrWhiteSpace(operatorFilter))
        {
            query = query.Where(x =>
                x.m_alpha_long != null &&
                x.m_alpha_long.ToLower().Contains(operatorFilter));
        }

        // =============================
        // 3ï¸ FETCH PRIMARY DATA
        // =============================
        var logs = await query
            .OrderBy(x => x.session_id)
            .ThenBy(x => x.timestamp)
            .Select(x => new IndoorOutdoorLogDto
            {
                session_id = (int)x.session_id,
                timestamp = x.timestamp,

                indoor_outdoor = x.indoor_outdoor,
                operator_name = x.m_alpha_long,

                rsrp = x.rsrp,
                rsrq = x.rsrq,
                sinr = x.sinr,
                mos = x.mos,

                dl_tpt = x.dl_tpt,
                ul_tpt = x.ul_tpt,

                apps = x.apps,
                network = x.network,

                primary_cell_info_1 = x.primary_cell_info_1
            })
            .ToListAsync();

        if (!logs.Any())
        {
            var empty = new { Message = "No data found" };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        // =============================
        // 4ï¸ HELPERS
        // =============================
        float? ParseFloat(string? v)
            => float.TryParse(v, out var f) ? f : null;

        double Avg(IEnumerable<float?> v)
        {
            var list = v.Where(x => x.HasValue).Select(x => x.Value).ToList();
            return list.Any() ? Math.Round(list.Average(), 2) : 0;
        }

        // =============================
        // 5ï¸ RESULT STRUCTURE
        // =============================
        var result = new
        {
            SessionIds = sessionIds,
            Indoor = new List<object>(),
            Outdoor = new List<object>()
        };

        // =============================
        // 6ï¸ GROUP BY INDOOR / OUTDOOR
        // =============================
        var ioGroups = logs
            .Where(x => !string.IsNullOrWhiteSpace(x.indoor_outdoor))
            .GroupBy(x => x.indoor_outdoor!.ToUpper());

        foreach (var ioGroup in ioGroups)
        {
            var ioList = new List<object>();

            // =============================
            // 7ï¸ GROUP BY OPERATOR + TECHNOLOGY
            // =============================
            var opTechGroups = ioGroup.GroupBy(x =>
            {
                string tech = DetectTechnology(x.network);

                if (!string.IsNullOrWhiteSpace(techFilter) &&
                    !tech.StartsWith(techFilter))
                {
                    return null;
                }

                string op =
                    string.IsNullOrWhiteSpace(x.operator_name) ||
                    x.operator_name == "000 000"
                        ? "UNKNOWN"
                        : x.operator_name.Trim();

                return new
                {
                    Operator = op,
                    Technology = tech
                };
            })
            .Where(g => g.Key != null);

            foreach (var group in opTechGroups)
            {
                // =============================
                // KPI CALCULATION
                // =============================
                var kpis = new
                {
                    avg_rsrp = Avg(group.Select(x => x.rsrp)),
                    avg_rsrq = Avg(group.Select(x => x.rsrq)),
                    avg_sinr = Avg(group.Select(x => x.sinr)),
                    avg_mos = Avg(group.Select(x => x.mos)),
                    avg_dl_tpt = Avg(group.Select(x => ParseFloat(x.dl_tpt))),
                    avg_ul_tpt = Avg(group.Select(x => ParseFloat(x.ul_tpt)))
                };

                // =============================
                // APP USAGE TIME
                // =============================
                var appTime = new Dictionary<string, double>();

                foreach (var session in group.GroupBy(x => x.session_id))
                {
                    DateTime? prevTs = null;
                    string? prevApp = null;

                    foreach (var row in session)
                    {
                        if (!row.timestamp.HasValue) continue;

                        var app = DetectAppName(row.apps);
                        if (string.IsNullOrEmpty(app))
                        {
                            prevTs = null;
                            prevApp = null;
                            continue;
                        }

                        if (prevTs.HasValue && prevApp == app)
                        {
                            var diff =
                                (row.timestamp.Value - prevTs.Value).TotalSeconds;

                            if (diff > 0 && diff <= MAX_GAP_SECONDS)
                            {
                                appTime.TryAdd(app, 0);
                                appTime[app] += diff;
                            }
                        }

                        prevTs = row.timestamp;
                        prevApp = app;
                    }
                }

                var appUsage = appTime.Select(x =>
                {
                    int sec = (int)x.Value;
                    return new
                    {
                        appName = x.Key,
                        seconds = sec,
                        time = $"{sec / 3600:D2}:{(sec % 3600) / 60:D2}:{sec % 60:D2}"
                    };
                }).OrderByDescending(x => x.seconds).ToList();

                ioList.Add(new
                {
                    group.Key.Operator,
                    group.Key.Technology,
                    KPIs = kpis,
                    AppUsage = appUsage
                });
            }

            if (ioGroup.Key == "INDOOR")
                result.Indoor.AddRange(ioList);
            else if (ioGroup.Key == "OUTDOOR")
                result.Outdoor.AddRange(ioList);
        }

        // =============================
        // 8ï¸ FINAL RESPONSE
        // =============================
        await SetMapViewCacheAsync(cacheKey, result);
        return Ok(result);
    }
    catch (Exception)
    {
        Console.WriteLine("Stack trace available in server logs.");
        return StatusCode(500, new { error = "An internal server error occurred." });
    }
}

        [HttpGet, Route("GetProviders")]
        public JsonResult GetProviders()
        {
            var cacheKey = BuildMapViewCacheKey("providers");
            var cached = TryGetMapViewCacheAsync<object>(cacheKey).GetAwaiter().GetResult();
            if (cached != null)
                return Json(cached);

            var providerNames = db.tbl_network_log
                .AsNoTracking()
                .Where(p => !string.IsNullOrEmpty(p.m_alpha_long))
                .Select(p => p.m_alpha_long)
                .Distinct()
                .ToList();

            var providers = providerNames
                .Select((name, index) => new { id = index + 1, name })
                .ToList();

            SetMapViewCacheAsync(cacheKey, providers, 600).GetAwaiter().GetResult();
            return Json(providers);
        }

        [HttpGet, Route("GetTechnologies")]
        public JsonResult GetTechnologies()
        {
            var cacheKey = BuildMapViewCacheKey("technologies");
            var cached = TryGetMapViewCacheAsync<object>(cacheKey).GetAwaiter().GetResult();
            if (cached != null)
                return Json(cached);

            var technologyNames = db.tbl_network_log
                .AsNoTracking()
                .Where(t => !string.IsNullOrEmpty(t.network))
                .Select(t => t.network)
                .Distinct()
                .ToList();

            var technologies = technologyNames
                .Select((name, index) => new { id = name, name })
                .ToList();

            SetMapViewCacheAsync(cacheKey, technologies, 600).GetAwaiter().GetResult();
            return Json(technologies);
        }

        [HttpGet, Route("GetBands")]
        public JsonResult GetBands()
        {
            try
            {
                var cacheKey = BuildMapViewCacheKey("bands");
                var cached = TryGetMapViewCacheAsync<object>(cacheKey).GetAwaiter().GetResult();
                if (cached != null)
                    return Json(cached);

                var bandNames = db.tbl_network_log
                    .AsNoTracking()
                    .Where(b => !string.IsNullOrEmpty(b.band))
                    .Select(b => b.band)
                    .Distinct()
                    .ToList();

                var bands = bandNames
                    .Select((name, index) => new { id = index + 1, name })
                    .ToList();

                SetMapViewCacheAsync(cacheKey, bands, 600).GetAwaiter().GetResult();
                return Json(bands);
            }
            catch (Exception ex)
            {
                return new JsonResult(new
                {
                    status = 0,
                    message = "Error fetching bands data",
                    error = SafeException.Get(ex)
                })
                { StatusCode = 500 };
            }
        }

        // =========================================================
        // ==================== Prediction Log =====================
        // =========================================================

        public class PredictionLogQueryDto
        {
            public int? projectId { get; set; }
            public string? Band { get; set; }
            public string? EARFCN { get; set; }
            public DateTime? fromDate { get; set; }
            public DateTime? toDate { get; set; }
            public string? metric { get; set; } = "RSRP";
            public int pointsInsideBuilding { get; set; } = 0;
            public System.Text.Json.JsonElement? coverageHoleJson { get; set; }
            public double? coverageHole { get; set; }
        }

        private static List<SettingReangeColor>? ParseRangeList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                var asList = JsonConvert.DeserializeObject<List<SettingReangeColor>>(raw);
                if (asList != null) return asList;
            }
            catch { }

            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, SettingReangeColor>>(raw);
                if (dict != null) return dict.Values.ToList();
            }
            catch { }

            try
            {
                var single = JsonConvert.DeserializeObject<SettingReangeColor>(raw);
                if (single != null) return new List<SettingReangeColor> { single };
            }
            catch { }

            return null;
        }

        [HttpPost, Route("GetPredictionLog"), Consumes("application/json")]
public JsonResult GetPredictionLog([FromBody] PredictionLogQueryDto q)
{
    var message = new ReturnAPIResponse();
    try
    {
        cf.SessionCheck();

        string? coverageHoleRaw = null;
        double? coverageHoleValue = null;

        var th = db.thresholds.FirstOrDefault(x => x.user_id == cf.UserId);
        if (th == null)
        {
            th = new thresholds { user_id = cf.UserId, is_default = 0 };
            db.thresholds.Add(th);
            db.SaveChanges();
        }

        if (q.coverageHoleJson.HasValue)
        {
            coverageHoleRaw = q.coverageHoleJson.Value.GetRawText();
            th.coveragehole_json = coverageHoleRaw;
        }
        if (q.coverageHole.HasValue)
        {
            coverageHoleValue = q.coverageHole.Value;
            th.coveragehole_value = coverageHoleValue;
        }
        if (q.coverageHoleJson.HasValue || q.coverageHole.HasValue)
            db.SaveChanges();

        if (!q.coverageHoleJson.HasValue)
            coverageHoleRaw = th.coveragehole_json;
        if (!q.coverageHole.HasValue)
            coverageHoleValue = th.coveragehole_value;

        List<SettingReangeColor>? coverageHoleSetting = ParseRangeList(coverageHoleRaw);

        IQueryable<tbl_prediction_data> query = db.tbl_prediction_data.AsNoTracking();
        message.Status = 1;

        if (q.projectId.HasValue && q.projectId.Value != 0)
            query = query.Where(e => e.tbl_project_id == q.projectId.Value);

        if (!string.IsNullOrEmpty(q.Band))
            query = query.Where(e => e.band == q.Band);

        if (!string.IsNullOrEmpty(q.EARFCN))
            query = query.Where(e => e.earfcn == q.EARFCN);

        if (q.fromDate.HasValue)
            query = query.Where(e => e.timestamp >= q.fromDate);

        if (q.toDate.HasValue)
            query = query.Where(e => e.timestamp < q.toDate.Value.AddDays(1));

        var metricKey = (q.metric ?? "RSRP").Trim().ToUpperInvariant();

        // Include band & network
        var data = query.Select(a => new
        {
            a.lat,
            a.lon,
            a.band,
            a.network,
            rsrp = a.rsrp,

            rsrq = a.rsrq,
            sinr = a.sinr,
            
            prm = metricKey == "RSRP" ? a.rsrp
                : (metricKey == "RSRQ" ? a.rsrq : a.sinr)
        }).ToList();

        double? averageRsrp = query.Where(x => x.rsrp != null).Average(x => (double?)x.rsrp);
        double? averageRsrq = query.Where(x => x.rsrq != null).Average(x => (double?)x.rsrq);
        double? averageSinr = query.Where(x => x.sinr != null).Average(x => (double?)x.sinr);

        GraphStruct CoveragePerfGraph = new GraphStruct();
        var setting = db.thresholds
            .AsNoTracking()
            .FirstOrDefault(x => x.user_id == cf.UserId)
            ?? db.thresholds
                .AsNoTracking()
                .FirstOrDefault(x => x.is_default == 1);

        List<SettingReangeColor>? colorSetting = null;
        if (setting != null && data.Count > 0)
        {
            if (metricKey == "RSRP")
                colorSetting = ParseRangeList(setting.rsrp_json);
            else if (metricKey == "RSRQ")
                colorSetting = ParseRangeList(setting.rsrq_json);
            else if (metricKey == "SINR" || metricKey == "SNR")
                colorSetting = ParseRangeList(setting.sinr_json);

            if (colorSetting != null && colorSetting.Count > 0)
            {
                int total = data.Count;
                var series = new GrapSeries();
                foreach (var s in colorSetting)
                {
                    CoveragePerfGraph.Category.Add(s.range);
                    int matched = data.Count(a => a.prm >= s.min && a.prm <= s.max);
                    float per = total > 0 ? (matched * 100f / total) : 0f;
                    series.data.Add(new { y = Math.Round(per, 2), color = s.color });
                }
                CoveragePerfGraph.series.Add(series);
            }
        }

        message.Data = new
        {
            dataList = data,
            avgRsrp = averageRsrp,
            avgRsrq = averageRsrq,
            avgSinr = averageSinr,
            coverageHole = coverageHoleValue,
            coverageHoleSetting = coverageHoleSetting,
            coverageHoleRaw = coverageHoleRaw,
            colorSetting = colorSetting,
            coveragePerfGraph = CoveragePerfGraph
        };
    }
    catch (Exception ex)
    {
        message.Status = 0;
        message.Message = DisplayMessage.ErrorMessage + " " + SafeException.Get(ex);
    }
    return Json(message);
}

[HttpGet, Route("GetPredictionLog")]
public JsonResult GetPredictionLog(
    int? projectId = null,
    string? token = null,
    DateTime? fromDate = null,
    DateTime? toDate = null,
    string? providers = null,
    string? technology = null,
    string? metric = "RSRP",
    bool isBestTechnology = false,
    string? Band = null,
    string? EARFCN = null,
    string? State = null,
    int pointsInsideBuilding = 0,
    bool loadFilters = false,
    string? coverageHoleJson = null,
    double? coverageHole = null)
{
    var message = new ReturnAPIResponse();
    try
    {
        thresholds th = db.thresholds.FirstOrDefault(x => x.user_id == cf.UserId);
        if (th == null)
        {
            th = new thresholds { user_id = cf.UserId, is_default = 0 };
            db.thresholds.Add(th);
            db.SaveChanges();
        }

        if (!string.IsNullOrWhiteSpace(coverageHoleJson))
            th.coveragehole_json = coverageHoleJson;
        if (coverageHole.HasValue)
            th.coveragehole_value = coverageHole;
        if (!string.IsNullOrWhiteSpace(coverageHoleJson) || coverageHole.HasValue)
            db.SaveChanges();

        string? coverageHoleRaw = !string.IsNullOrWhiteSpace(coverageHoleJson) ? coverageHoleJson : th.coveragehole_json;
        double? coverageHoleValue = coverageHole.HasValue ? coverageHole.Value : th.coveragehole_value;

        IQueryable<tbl_prediction_data> query = db.tbl_prediction_data.AsNoTracking();

        if (projectId.HasValue && projectId != 0)
            query = query.Where(e => e.tbl_project_id == projectId);

        if (!string.IsNullOrEmpty(Band))
            query = query.Where(e => e.band == Band);

        if (!string.IsNullOrEmpty(EARFCN))
            query = query.Where(e => e.earfcn == EARFCN);

        if (fromDate.HasValue)
            query = query.Where(e => e.timestamp >= fromDate);

        if (toDate.HasValue)
            query = query.Where(e => e.timestamp < toDate.Value.AddDays(1));

        var metricKey = (metric ?? "RSRP").ToUpperInvariant();

        // include band & network in result
        var baseRows = query
            .Select(a => new
            {
                a.lat,
                a.lon,
                a.band,
                a.network,
                a.rsrp,
                a.rsrq,
                a.sinr
            })
            .ToList();

        var dataList = baseRows.Select(a => new
        {
            a.lat,
            a.lon,
            a.band,
            a.network,
            a.rsrp,
            a.rsrq,
            a.sinr,
            prm = metricKey == "RSRP"
                ? a.rsrp
                : (metricKey == "RSRQ" ? a.rsrq : a.sinr)
        }).ToList();

        double? averageRsrp = baseRows.Where(x => x.rsrp.HasValue).Average(x => (double?)x.rsrp.Value);
        double? averageRsrq = baseRows.Where(x => x.rsrq.HasValue).Average(x => (double?)x.rsrq.Value);
        double? averageSinr = baseRows.Where(x => x.sinr.HasValue).Average(x => (double?)x.sinr.Value);

        GraphStruct coveragePerfGraph = new GraphStruct();
        var setting = db.thresholds
            .AsNoTracking()
            .FirstOrDefault(x => x.user_id == cf.UserId)
            ?? db.thresholds
                .AsNoTracking()
                .FirstOrDefault(x => x.is_default == 1);

        List<SettingReangeColor>? settingObj = null;
        if (setting != null && dataList.Count > 0)
        {
            if (metricKey == "RSRP")
                settingObj = ParseRangeList(setting.rsrp_json);
            else if (metricKey == "RSRQ")
                settingObj = ParseRangeList(setting.rsrq_json);
            else if (metricKey == "SINR")
                settingObj = ParseRangeList(setting.sinr_json);

            if (settingObj != null && settingObj.Count > 0)
            {
                int totalCount = dataList.Count;
                GrapSeries seriesObj = new GrapSeries();
                foreach (var s in settingObj)
                {
                    coveragePerfGraph.Category.Add(s.range);
                    int matchedCount = dataList.Count(a => a.prm >= s.min && a.prm <= s.max);
                    float per = totalCount > 0 ? (matchedCount * 100f / totalCount) : 0f;
                    seriesObj.data.Add(new { y = Math.Round(per, 2), color = s.color });
                }
                coveragePerfGraph.series.Add(seriesObj);
            }
        }

        message.Status = 1;
        message.Data = new
        {
            dataList,
            avgRsrp = averageRsrp,
            avgRsrq = averageRsrq,
            avgSinr = averageSinr,
            colorSetting = settingObj,
            coveragePerfGraph = coveragePerfGraph,
            coverageHole = coverageHoleValue,
            coverageHoleRaw = coverageHoleRaw,
            coverageHoleSetting = ParseRangeList(coverageHoleRaw)
        };
    }
    catch (Exception ex)
    {
        message.Status = 0;
        message.Message = DisplayMessage.ErrorMessage + " " + SafeException.Get(ex);
    }
    return Json(message);
}

        [HttpGet, Route("GetPredictionDataForSelectedBuildingPolygonsRaw")]
        public async Task<JsonResult> GetPredictionDataForSelectedBuildingPolygonsRaw(int projectId, string metric)
        {
            try
            {
                var cacheKey = BuildMapViewCacheKey("prediction-building-polygons-raw", projectId, metric ?? "RSRP");
                var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
                if (cached != null)
                    return Json(cached);

                string sqlQuery = @"
                    SELECT
                        tpd.tbl_project_id,
                        tpd.lat, tpd.lon,
                        tpd.rsrp, tpd.rsrq, tpd.sinr,
                        tpd.band, tpd.earfcn
                    FROM tbl_prediction_data AS tpd
                    JOIN map_regions AS mr
                      ON tpd.tbl_project_id = mr.tbl_project_id
                    WHERE tpd.tbl_project_id = {0}
                      AND ST_Contains(
                            mr.region,
                            ST_PointFromText(CONCAT('POINT(', tpd.lon, ' ', tpd.lat, ')'), 4326)
                          );";

                var rows = await db.Set<TempPoint>()
                    .FromSqlRaw(sqlQuery, projectId)
                    .AsNoTracking()
                    .ToListAsync();

                var metricKey = (metric ?? "RSRP").ToUpperInvariant();

                var data = rows.Select(a => new
                {
                    a.lat,
                    a.lon,
                    Prm = metricKey == "RSRP"
                        ? a.rsrp
                        : (metricKey == "RSRQ" ? a.rsrq : a.sinr)
                }).ToList();

                await SetMapViewCacheAsync(cacheKey, data);
                return Json(data);
            }
            catch (Exception ex)
            {
                return Json(new { error = "An error occurred while fetching data.", details = SafeException.Get(ex) });
            }
        }

        // =========================================================
        // ===================== Image Upload ======================
        // =========================================================

        [HttpPost, AllowAnonymous, PublicApiKey, Route("UploadImage")]
        public async Task<IActionResult> UploadImage([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".gif" };
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !allowedExts.Contains(ext.ToLowerInvariant()))
                return BadRequest("Only image files are allowed.");

            var webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var uploadFolder = Path.Combine(webRootPath, "uploaded_images");
            if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

            var fileName = $"{Guid.NewGuid():N}{ext}";
            var filePath = Path.Combine(uploadFolder, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            var publicUrl = $"/uploaded_images/{fileName}";
            return Ok(new
            {
                message = "Image uploaded successfully.",
                filename = fileName,
                url = publicUrl
            });
        }

        [HttpPost, AllowAnonymous, PublicApiKey, Route("UploadImageLegacy")]
        public async Task<IActionResult> UploadImageLegacy([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
            if (!allowedTypes.Contains(file.ContentType))
                return BadRequest("Only image files are allowed.");

            var webRootPath = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var uploadFolder = Path.Combine(webRootPath, "uploaded_images");
            if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

            var filePath = Path.Combine(uploadFolder, Path.GetFileName(file.FileName));
            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            return Ok(new
            {
                message = "Image uploaded successfully.",
                filename = file.FileName
            });
        }

        // =========================================================
        // ==================== Log ingestion ======================
        // =========================================================

        public class NetworkLogPostModel
        {
            [JsonPropertyName("sessionid")]
            public int sessionid { get; set; }

            [JsonPropertyName("data")]
            public List<log_network> data { get; set; } = new();
        }

        [HttpPost, AllowAnonymous, PublicApiKey, Route("log_networkAsync")]
        public async Task<JsonResult> log_networkAsync([FromBody] NetworkLogPostModel model)
        {
            var message = new ReturnMessage();
            var ci = CultureInfo.InvariantCulture;

            try
            {
                if (model?.data == null || !model.data.Any())
                {
                    message.Status = 0; message.Message = "No data received.";
                    return Json(message);
                }

                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync();

                // Pre-prepare polygon lookup command
                await using var polyCmd = conn.CreateCommand();
                polyCmd.CommandText = @"
                    SELECT id
                    FROM map_regions
                    WHERE ST_Contains(
                            region,
                            ST_GeomFromText(CONCAT('POINT(', @lon, ' ', @lat, ')'), 4326)
                          )
                    LIMIT 1;";

                var pLon = polyCmd.CreateParameter(); pLon.ParameterName = "@lon"; polyCmd.Parameters.Add(pLon);
                var pLat = polyCmd.CreateParameter(); pLat.ParameterName = "@lat"; polyCmd.Parameters.Add(pLat);

                var logsToInsert = new List<tbl_network_log>(model.data.Count);

                foreach (var item in model.data)
                {
                    var log = new tbl_network_log
                    {
                        session_id = model.sessionid,
                        timestamp = DateTime.TryParse(item.timestamp, ci, DateTimeStyles.RoundtripKind, out var ts)
                            ? ts : (DateTime?)null,

                        lat = float.TryParse(item.lat, NumberStyles.Float, ci, out var latVal)
                            ? latVal : (float?)null,

                        lon = float.TryParse(item.lon, NumberStyles.Float, ci, out var lonVal)
                            ? lonVal : (float?)null,

                        battery = int.TryParse(item.battery, out var batVal)
                            ? batVal : (int?)null,

                        dls = item.dls,
                        uls = item.uls,
                        call_state = item.call_state,
                        hotspot = item.hotspot,
                        apps = item.apps,

                        num_cells = int.TryParse(item.num_cells, out var ncVal)
                            ? ncVal : (int?)null,

                        network = item.network,
                        m_mcc = int.TryParse(item.m_mcc, out var mccVal)
                            ? mccVal : (int?)null,

                        m_mnc = int.TryParse(item.m_mnc, out var mncVal)
                            ? mncVal : (int?)null,

                        m_alpha_long = item.m_alpha_long,
                        m_alpha_short = item.m_alpha_short,
                        mci = item.mci,
                        pci = item.pci,
                        tac = item.tac,
                        earfcn = item.earfcn,

                        rssi = float.TryParse(item.rssi, NumberStyles.Float, ci, out var rssiVal)
                            ? rssiVal : (float?)null,

                        rsrp = float.TryParse(item.rsrp, NumberStyles.Float, ci, out var rsrpVal)
                            ? rsrpVal : (float?)null,

                        rsrq = float.TryParse(item.rsrq, NumberStyles.Float, ci, out var rsrqVal)
                            ? rsrqVal : (float?)null,

                        sinr = float.TryParse(item.sinr, NumberStyles.Float, ci, out var sinrVal)
                            ? sinrVal : (float?)null,

                        total_rx_kb = item.total_rx_kb,
                        total_tx_kb = item.total_tx_kb,
                        mos = float.TryParse(item.mos, NumberStyles.Float, ci, out var mosVal)
                            ? mosVal : (float?)null,

                        jitter = float.TryParse(item.jitter, NumberStyles.Float, ci, out var jitterVal)
                            ? jitterVal : (float?)null,

                        latency = float.TryParse(item.latency, NumberStyles.Float, ci, out var latnVal)
                            ? latnVal : (float?)null,

                        packet_loss = float.TryParse(item.packet_loss, NumberStyles.Float, ci, out var lossVal)
                            ? lossVal : (float?)null,

                        dl_tpt = item.dl_tpt,
                        ul_tpt = item.ul_tpt,
                        volte_call = item.volte_call,
                        band = item.band,

                        cqi = float.TryParse(item.cqi, NumberStyles.Float, ci, out var cqiVal)
                            ? cqiVal : (float?)null,

                        bler = item.bler,
                        primary_cell_info_1 = item.primary_cell_info_1,
                        primary_cell_info_2 = item.primary_cell_info_2,
                        all_neigbor_cell_info = item.all_neigbor_cell_info,
                        image_path = item.image_path
                    };

                    if (log.lat.HasValue && log.lon.HasValue)
                    {
                        pLon.Value = log.lon.Value;
                        pLat.Value = log.lat.Value;

                        var obj = await polyCmd.ExecuteScalarAsync();
                        log.polygon_id = (obj == null || obj == DBNull.Value)
                            ? null
                            : (int?)Convert.ToInt32(obj);
                    }

                    logsToInsert.Add(log);
                }

                await db.tbl_network_log.AddRangeAsync(logsToInsert);
                await db.SaveChangesAsync();
                await InvalidateMapViewCachesAsync();

                message.Status = 1;
                message.Message = "Data saved successfully.";
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = "Error: " + SafeException.Get(ex);
            }
            return Json(message);
        }

        // =========================================================
        // =================== site_prediction CSV =================
        // =========================================================

        public class UploadSitePredictionRequest
        {
            public long ProjectId { get; set; }
            public IFormFile File { get; set; } = default!;
        }

       [HttpPost, Route("UploadSitePredictionCsv")]
[RequestSizeLimit(200_000_000)]
public async Task<IActionResult> UploadSitePredictionCsv([FromForm] UploadSitePredictionRequest req)
{
    if (req == null || req.ProjectId <= 0 || req.File == null || req.File.Length == 0)
        return BadRequest(new { Status = 0, Message = "ProjectId and CSV file are required." });

    var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "site","sector","cell_id","longitude","latitude","pci","azimuth",
        "band","earfcn","cluster","technology",
        "m_tilt","e_tilt","height"   // âœ… ADDED
    };

    var inserted = 0;
    using var reader = new StreamReader(req.File.OpenReadStream());
    var headerLine = await reader.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(headerLine))
        return BadRequest(new { Status = 0, Message = "Empty CSV." });

    var headers = headerLine.Split(',', StringSplitOptions.TrimEntries);
    var headerSet = new HashSet<string>(
        headers.Select(h => h.Trim().Trim('"')),
        StringComparer.OrdinalIgnoreCase
    );

    var missing = required.Where(h => !headerSet.Contains(h)).ToList();
    if (missing.Count > 0)
        return BadRequest(new { Status = 0, Message = "Missing required CSV columns: " + string.Join(", ", missing) });

    int Col(string name) =>
        Array.FindIndex(headers, h =>
            string.Equals(h.Trim().Trim('"'), name, StringComparison.OrdinalIgnoreCase));

    var idxSite     = Col("site");
    var idxSector   = Col("sector");
    var idxCellId   = Col("cell_id");
    var idxLon      = Col("longitude");
    var idxLat      = Col("latitude");
    var idxPci      = Col("pci");
    var idxAz       = Col("azimuth");
    var idxBand     = Col("band");
    var idxEarfcn   = Col("earfcn");
    var idxCluster  = Col("cluster");
    var idxTech     = Col("technology");

    var idxMTilt    = Col("m_tilt");     // âœ… ADDED
    var idxETilt    = Col("e_tilt");     // âœ… ADDED
    var idxHeight   = Col("height");     // âœ… ADDED

    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync();

    await using var tx = await conn.BeginTransactionAsync();

    string sql = @"
        INSERT INTO site_prediction
            (site, sector, cell_id, longitude, latitude,
             pci, azimuth, band, earfcn, cluster, Technology,
             m_tilt, e_tilt, height, tbl_project_id)
        VALUES
            (@site, @sector, @cell_id, @lon, @lat,
             @pci, @az, @band, @earfcn, @cluster, @tech,
             @m_tilt, @e_tilt, @height, @pid);";

    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        var cols = SplitCsv(line, headers.Length);
        if (cols.Length != headers.Length) continue;

        if (string.IsNullOrWhiteSpace(cols[idxSite])     ||
            string.IsNullOrWhiteSpace(cols[idxSector])   ||
            string.IsNullOrWhiteSpace(cols[idxCellId])   ||
            string.IsNullOrWhiteSpace(cols[idxLon])      ||
            string.IsNullOrWhiteSpace(cols[idxLat])      ||
            string.IsNullOrWhiteSpace(cols[idxPci])      ||
            string.IsNullOrWhiteSpace(cols[idxAz])       ||
            string.IsNullOrWhiteSpace(cols[idxBand])     ||
            string.IsNullOrWhiteSpace(cols[idxEarfcn])   ||
            string.IsNullOrWhiteSpace(cols[idxCluster])  ||
            string.IsNullOrWhiteSpace(cols[idxTech])     ||
            string.IsNullOrWhiteSpace(cols[idxMTilt])    ||   // âœ… ADDED
            string.IsNullOrWhiteSpace(cols[idxETilt])    ||   // âœ… ADDED
            string.IsNullOrWhiteSpace(cols[idxHeight]))       // âœ… ADDED
        {
            continue;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;

        Add(cmd, "@site",    ToInt(cols[idxSite]));
        Add(cmd, "@sector",  ToInt(cols[idxSector]));
        Add(cmd, "@cell_id", cols[idxCellId]);
        Add(cmd, "@lon",     ToDouble(cols[idxLon]));
        Add(cmd, "@lat",     ToDouble(cols[idxLat]));
        Add(cmd, "@pci",     ToInt(cols[idxPci]));
        Add(cmd, "@az",      ToInt(cols[idxAz]));
        Add(cmd, "@band",    ToInt(cols[idxBand]));
        Add(cmd, "@earfcn",  ToInt(cols[idxEarfcn]));
        Add(cmd, "@cluster", cols[idxCluster]);
        Add(cmd, "@tech",    cols[idxTech]);

        Add(cmd, "@m_tilt",  ToInt(cols[idxMTilt]));      // âœ… ADDED
        Add(cmd, "@e_tilt",  ToInt(cols[idxETilt]));      // âœ… ADDED
        Add(cmd, "@height",  ToDouble(cols[idxHeight]));  // âœ… ADDED

        Add(cmd, "@pid",     req.ProjectId);

        inserted += await cmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
    await InvalidateMapViewCachesAsync();

    return Ok(new
    {
        Status = 1,
        Message = "Uploaded.",
        Inserted = inserted
    });
}

        private static readonly Dictionary<string, string> SitePredictionColumnMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["site"] = "site",
            ["site_id"] = "site",
            ["siteId"] = "site",
            ["site_name"] = "site_name",
            ["node_id"] = "node_id",
            ["nodeId"] = "node_id",
            ["node_b_id"] = "node_id",
            ["nodeb_id"] = "node_id",
            ["nodebId"] = "node_id",
            ["sector"] = "sector",
            ["sector_id"] = "sector",
            ["sectorId"] = "sector",
            ["cell_id"] = "cell_id",
            ["cellId"] = "cell_id",
            ["sec_id"] = "sec_id",
            ["secId"] = "sec_id",
            ["longitude"] = "longitude",
            ["lng"] = "longitude",
            ["lon"] = "longitude",
            ["latitude"] = "latitude",
            ["lat"] = "latitude",
            ["tac"] = "tac",
            ["pci"] = "pci",
            ["azimuth"] = "azimuth",
            ["height"] = "height",
            ["bw"] = "bw",
            ["beamwidth"] = "bw",
            ["bandwidth"] = "bw",
            ["m_tilt"] = "m_tilt",
            ["e_tilt"] = "e_tilt",
            ["maximum_transmission_power_of_resource"] = "tx_power",
            ["maximumTransmissionPowerOfResource"] = "tx_power",
            ["tx_power"] = "tx_power",
            ["txPower"] = "tx_power",
            ["real_transmit_power_of_resource"] = "real_transmit_power_of_resource",
            ["realTransmitPowerOfResource"] = "real_transmit_power_of_resource",
            ["reference_signal_power"] = "reference_signal_power",
            ["referenceSignalPower"] = "reference_signal_power",
            ["rs_power"] = "reference_signal_power",
            ["rsPower"] = "reference_signal_power",
            ["cellsize"] = "cellsize",
            ["cell_size"] = "cellsize",
            ["cellSize"] = "cellsize",
            ["frequency"] = "frequency",
            ["band"] = "band",
            ["uplink_center_frequency"] = "uplink_center_frequency",
            ["uplinkCenterFrequency"] = "uplink_center_frequency",
            ["uplink_frequency"] = "uplink_center_frequency",
            ["uplinkFrequency"] = "uplink_center_frequency",
            ["downlink_frequency"] = "downlink_frequency",
            ["downlinkFrequency"] = "downlink_frequency",
            ["download_frequency"] = "downlink_frequency",
            ["downloadFrequency"] = "downlink_frequency",
            ["earfcn"] = "earfcn",
            ["cluster"] = "cluster",
            ["provider"] = "cluster",
            ["operator_name"] = "cluster",
            ["operatorName"] = "cluster",
            ["network"] = "cluster",
            ["operator"] = "cluster",
            ["Technology"] = "Technology",
            ["technology"] = "Technology"
        };

        private static string? ResolveSitePredictionProviderInput(
            string? provider,
            string? operatorName,
            string? operatorNameSnake,
            string? operatorAlias,
            string? cluster)
        {
            foreach (var candidate in new[] { provider, operatorName, operatorNameSnake, operatorAlias, cluster })
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate.Trim();
            }
            return null;
        }

        private static string BuildSitePredictionFilterClause(
            int? site,
            string? cellId,
            string? cluster,
            string? technology,
            int? band,
            int? pci,
            string siteExpr,
            string cellExpr,
            string clusterExpr,
            string projectProviderExpr,
            string technologyExpr,
            string bandExpr,
            string pciExpr)
        {
            var filters = new List<string>();

            if (site.HasValue) filters.Add($"{siteExpr} = @site");
            if (!string.IsNullOrWhiteSpace(cellId)) filters.Add($"CONVERT({cellExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @cell");
            if (!string.IsNullOrWhiteSpace(cluster)) filters.Add($"((CONVERT({clusterExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @clus) OR (CONVERT({projectProviderExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @clus))");
            if (!string.IsNullOrWhiteSpace(technology)) filters.Add($"CONVERT({technologyExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @tech");
            if (band.HasValue) filters.Add($"{bandExpr} = @band");
            if (pci.HasValue) filters.Add($"{pciExpr} = @pci");

            return filters.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", filters);
        }

        private static string BuildSitePredictionDualFilterClause(
            int? site,
            string? cellId,
            string? cluster,
            string? technology,
            int? band,
            int? pci,
            string originalSiteExpr,
            string updatedSiteExpr,
            string originalCellExpr,
            string updatedCellExpr,
            string originalClusterExpr,
            string updatedClusterExpr,
            string originalProjectProviderExpr,
            string updatedProjectProviderExpr,
            string originalTechnologyExpr,
            string updatedTechnologyExpr,
            string originalBandExpr,
            string updatedBandExpr,
            string originalPciExpr,
            string updatedPciExpr)
        {
            var filters = new List<string>();

            if (site.HasValue)
            {
                filters.Add($"(({updatedSiteExpr} = @site) OR ({originalSiteExpr} = @site))");
            }

            if (!string.IsNullOrWhiteSpace(cellId))
            {
                filters.Add($"((CONVERT({updatedCellExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @cell) OR (CONVERT({originalCellExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @cell))");
            }

            if (!string.IsNullOrWhiteSpace(cluster))
            {
                filters.Add($@"(
                    (CONVERT({updatedClusterExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @clus)
                    OR (CONVERT({originalClusterExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @clus)
                    OR (CONVERT({updatedProjectProviderExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @clus)
                    OR (CONVERT({originalProjectProviderExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @clus)
                )");
            }

            if (!string.IsNullOrWhiteSpace(technology))
            {
                filters.Add($"((CONVERT({updatedTechnologyExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @tech) OR (CONVERT({originalTechnologyExpr} USING utf8mb4) COLLATE utf8mb4_unicode_ci = @tech))");
            }

            if (band.HasValue)
            {
                filters.Add($"(({updatedBandExpr} = @band) OR ({originalBandExpr} = @band))");
            }

            if (pci.HasValue)
            {
                filters.Add($"(({updatedPciExpr} = @pci) OR ({originalPciExpr} = @pci))");
            }

            return filters.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", filters);
        }

        private static void AddSitePredictionFilterParameters(
            DbCommand cmd,
            int? site,
            string? cellId,
            string? cluster,
            string? technology,
            int? band,
            int? pci)
        {
            if (site.HasValue) Add(cmd, "@site", site.Value);
            if (!string.IsNullOrWhiteSpace(cellId)) Add(cmd, "@cell", cellId.Trim());
            if (!string.IsNullOrWhiteSpace(cluster)) Add(cmd, "@clus", cluster.Trim());
            if (!string.IsNullOrWhiteSpace(technology)) Add(cmd, "@tech", technology.Trim());
            if (band.HasValue) Add(cmd, "@band", band.Value);
            if (pci.HasValue) Add(cmd, "@pci", pci.Value);
        }

        private static List<int> ParsePolygonIds(string? polygonIdsCsv)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(polygonIdsCsv)) return result;

            foreach (var token in polygonIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    result.Add(id);
                }
            }

            return result.Distinct().ToList();
        }

        private static void AddPolygonIdsParameters(DbCommand cmd, IReadOnlyList<int> polygonIds)
        {
            for (var i = 0; i < polygonIds.Count; i++)
            {
                Add(cmd, $"@poly_{i}", polygonIds[i]);
            }
        }

        private static string BuildPolygonFilterClause(
            IReadOnlyList<int> polygonIds,
            string latExpr,
            string lonExpr)
        {
            if (polygonIds == null || polygonIds.Count == 0) return string.Empty;

            var polyParams = string.Join(", ", polygonIds.Select((_, i) => $"@poly_{i}"));

            // Global-safe handling for MySQL geographic SRID 4326:
            // - Polygons are stored from frontend WKT in lat/lng text order.
            // - Matching points should be built with POINT(lon, lat) via the POINT constructor,
            //   not via ST_GeomFromText('POINT(...)'), which can misinterpret axis order.
            // - Use CASE so MySQL never evaluates an invalid fallback branch.
            return $@"
                AND EXISTS (
                    SELECT 1
                    FROM map_regions mr_filter
                    WHERE mr_filter.tbl_project_id = @pid
                      AND mr_filter.id IN ({polyParams})
                      AND CASE
                        WHEN ({latExpr}) BETWEEN -90 AND 90
                          AND ({lonExpr}) BETWEEN -180 AND 180
                        THEN ST_Contains(
                          mr_filter.region,
                          ST_SRID(POINT({lonExpr}, {latExpr}), 4326)
                        )
                        WHEN ({lonExpr}) BETWEEN -90 AND 90
                          AND ({latExpr}) BETWEEN -180 AND 180
                        THEN ST_Contains(
                          mr_filter.region,
                          ST_SRID(POINT({latExpr}, {lonExpr}), 4326)
                        )
                        ELSE 0
                      END = 1
                )";
        }

        private static string? NormalizeSitePredictionOperator(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var s = value.Trim().ToLowerInvariant();
            if (s.Contains("airtel") || s.Contains("bharti")) return "airtel";
            if (s.Contains("jio") || s.Contains("reliance")) return "jio";
            if (s == "vi" || s.StartsWith("vi ") || s.StartsWith("vi-") || s.StartsWith("vi_") ||
                s.Contains("vodafone") || s.Contains("vodaphone") || s.Contains("vodafone idea") || s.Contains("idea"))
                return "vi";
            if (s.Contains("bsnl")) return "bsnl";
            return null;
        }

        private static string? ResolveSitePredictionOperator(string? cluster, string? bandRaw, string? earfcnRaw, string? frequency)
        {
            var normalizedCluster = NormalizeSitePredictionOperator(cluster);
            if (!string.IsNullOrWhiteSpace(normalizedCluster))
                return normalizedCluster;

            if (int.TryParse(earfcnRaw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var earfcn))
            {
                switch (earfcn)
                {
                    case 315:
                        return "airtel";
                    case 3676:
                        return "vi";
                    case 39150:
                        return "jio";
                }
            }

            if (int.TryParse(bandRaw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var band))
            {
                switch (band)
                {
                    case 1:
                    case 3:
                    case 41:
                        return "airtel";
                    case 8:
                        return "vi";
                    case 5:
                    case 28:
                    case 40:
                    case 78:
                        return "jio";
                }
            }

            var f = frequency?.Trim().ToLowerInvariant() ?? string.Empty;
            if (f.Contains("1800")) return "airtel";
            if (f.Contains("900")) return "vi";
            if (f.Contains("700")) return "jio";
            if (f.Contains("2300")) return "jio";
            if (f.Contains("3300") || f.Contains("3500")) return "jio";

            return null;
        }

        private static string? ReadSitePredictionText(Dictionary<string, object?> row, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!row.TryGetValue(key, out var value) || value == null || value is DBNull)
                    continue;

                var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return null;
        }

        private static void EnrichSitePredictionRow(Dictionary<string, object?> row)
        {
            var originalCluster = ReadSitePredictionText(row, "original_cluster");
            var clusterValue = ReadSitePredictionText(row, "cluster");
            var optimizedCluster = ReadSitePredictionText(row, "optimized_cluster");
            var projectProvider = ReadSitePredictionText(row, "project_provider");
            var bandRaw = ReadSitePredictionText(row, "band");
            var earfcnRaw = ReadSitePredictionText(row, "earfcn");
            var frequency = ReadSitePredictionText(row, "frequency");

            var rawCluster = originalCluster ?? clusterValue ?? optimizedCluster ?? projectProvider;
            row["raw_cluster"] = rawCluster;

            var normalizedProvider =
                NormalizeSitePredictionOperator(originalCluster)
                ?? NormalizeSitePredictionOperator(clusterValue)
                ?? NormalizeSitePredictionOperator(optimizedCluster)
                ?? NormalizeSitePredictionOperator(projectProvider);

            var provider =
                normalizedProvider
                ?? rawCluster
                ?? ResolveSitePredictionOperator(rawCluster, bandRaw, earfcnRaw, frequency)
                ?? projectProvider;
            if (!string.IsNullOrWhiteSpace(provider))
            {
                row["provider"] = provider;
                row["operator_name"] = provider;
            }
            else if (!string.IsNullOrWhiteSpace(rawCluster))
            {
                row["provider"] = rawCluster;
                row["operator_name"] = rawCluster;
            }
        }

        private async Task EnsureSitePredictionOptimizedTableAsync(DbConnection conn)
        {
            await using (var createCmd = conn.CreateCommand())
            {
                createCmd.CommandText = "CREATE TABLE IF NOT EXISTS site_prediction_optimized LIKE site_prediction;";
                await createCmd.ExecuteNonQueryAsync();
            }

            await EnsureSitePredictionOptimizedScenarioColumnAsync(conn);
            await EnsureSitePredictionOptimizedIdentityAsync(conn);

            var requiredColumns = new (string Name, string Definition)[]
            {
                ("site_prediction_id", "INT NULL"),
                ("is_updated", "TINYINT(1) NOT NULL DEFAULT 1"),
                ("version", "INT NOT NULL DEFAULT 1"),
                ("status", "VARCHAR(20) NULL DEFAULT 'updated'"),
                ("created_at", "DATETIME NULL"),
                ("updated_at", "DATETIME NULL"),
                ("updated_by", "VARCHAR(255) NULL")
            };

            foreach (var column in requiredColumns)
            {
                await using var existsCmd = conn.CreateCommand();
                existsCmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = 'site_prediction_optimized'
                      AND COLUMN_NAME = @columnName;";
                Add(existsCmd, "@columnName", column.Name);

                var existsObj = await existsCmd.ExecuteScalarAsync();
                var exists = existsObj != null && existsObj != DBNull.Value && Convert.ToInt32(existsObj) > 0;
                if (exists)
                {
                    continue;
                }

                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE `site_prediction_optimized` ADD COLUMN `{column.Name}` {column.Definition};";
                await alterCmd.ExecuteNonQueryAsync();
            }

            // Keep one optimized row per source row (sector-level from site_prediction row id).
            await using (var dedupeCmd = conn.CreateCommand())
            {
                dedupeCmd.CommandText = @"
                    DELETE dup
                    FROM site_prediction_optimized dup
                    INNER JOIN site_prediction_optimized keep
                        ON keep.site_prediction_id = dup.site_prediction_id
                       AND keep.scenario = dup.scenario
                       AND keep.id > dup.id
                    WHERE dup.site_prediction_id IS NOT NULL;";
                await dedupeCmd.ExecuteNonQueryAsync();
            }

            await using (var oldUniqueExistsCmd = conn.CreateCommand())
            {
                oldUniqueExistsCmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = 'site_prediction_optimized'
                      AND INDEX_NAME = 'uq_spo_site_prediction_id';";
                var oldUniqueExistsObj = await oldUniqueExistsCmd.ExecuteScalarAsync();
                var oldUniqueExists = oldUniqueExistsObj != null &&
                                      oldUniqueExistsObj != DBNull.Value &&
                                      Convert.ToInt32(oldUniqueExistsObj) > 0;
                if (oldUniqueExists)
                {
                    await using var dropOldUniqueCmd = conn.CreateCommand();
                    dropOldUniqueCmd.CommandText = "ALTER TABLE site_prediction_optimized DROP INDEX uq_spo_site_prediction_id;";
                    await dropOldUniqueCmd.ExecuteNonQueryAsync();
                }
            }

            await using (var uniqueExistsCmd = conn.CreateCommand())
            {
                uniqueExistsCmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = 'site_prediction_optimized'
                      AND INDEX_NAME = 'uq_spo_site_prediction_scenario';";
                var uniqueExistsObj = await uniqueExistsCmd.ExecuteScalarAsync();
                var uniqueExists = uniqueExistsObj != null &&
                                   uniqueExistsObj != DBNull.Value &&
                                   Convert.ToInt32(uniqueExistsObj) > 0;
                if (!uniqueExists)
                {
                    await using var addUniqueCmd = conn.CreateCommand();
                    addUniqueCmd.CommandText =
                        "ALTER TABLE site_prediction_optimized ADD UNIQUE INDEX uq_spo_site_prediction_scenario (site_prediction_id, scenario);";
                    await addUniqueCmd.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task EnsureSitePredictionOptimizedIdentityAsync(DbConnection conn)
        {
            await using (var createCmd = conn.CreateCommand())
            {
                createCmd.CommandText = "CREATE TABLE IF NOT EXISTS `site_prediction_optimized` LIKE `site_prediction`;";
                await createCmd.ExecuteNonQueryAsync();
            }

            var hasIdColumn = false;
            var idIsAutoIncrement = false;
            var idHasKey = false;
            var idHasUniqueKey = false;
            await using (var idColumnCmd = conn.CreateCommand())
            {
                idColumnCmd.CommandText = @"
                    SELECT EXTRA, COLUMN_KEY
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = 'site_prediction_optimized'
                      AND COLUMN_NAME = 'id'
                    LIMIT 1;";

                await using var reader = await idColumnCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    hasIdColumn = true;
                    var extra = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var columnKey = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    idIsAutoIncrement = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
                    idHasKey = !string.IsNullOrWhiteSpace(columnKey);
                    idHasUniqueKey = columnKey.Equals("PRI", StringComparison.OrdinalIgnoreCase) ||
                                     columnKey.Equals("UNI", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!hasIdColumn)
            {
                await using var addIdCmd = conn.CreateCommand();
                addIdCmd.CommandText = @"
                    ALTER TABLE `site_prediction_optimized`
                    ADD COLUMN `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY FIRST;";
                await addIdCmd.ExecuteNonQueryAsync();
                return;
            }

            await using (var normalizeRequiredCmd = conn.CreateCommand())
            {
                normalizeRequiredCmd.CommandText = @"
                    SELECT COUNT(1)
                    FROM (
                        SELECT id
                        FROM `site_prediction_optimized`
                        GROUP BY id
                        HAVING id IS NULL OR id <= 0 OR COUNT(1) > 1
                    ) bad_ids;";
                var normalizeObj = await normalizeRequiredCmd.ExecuteScalarAsync();
                var normalizeRequired = normalizeObj != null &&
                                        normalizeObj != DBNull.Value &&
                                        Convert.ToInt32(normalizeObj) > 0;
                if (normalizeRequired)
                {
                    await using (var seedSequenceCmd = conn.CreateCommand())
                    {
                        seedSequenceCmd.CommandText = "SET @spo_next_id := 0;";
                        await seedSequenceCmd.ExecuteNonQueryAsync();
                    }

                    await using (var normalizeCmd = conn.CreateCommand())
                    {
                        normalizeCmd.CommandText = @"
                        UPDATE `site_prediction_optimized`
                        SET `id` = (@spo_next_id := @spo_next_id + 1)
                        ORDER BY COALESCE(`id`, 0), COALESCE(`site_prediction_id`, 0), COALESCE(`scenario`, 0);";
                        await normalizeCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            if (!idHasUniqueKey)
            {
                await using var addIdUniqueCmd = conn.CreateCommand();
                addIdUniqueCmd.CommandText =
                    "ALTER TABLE `site_prediction_optimized` ADD UNIQUE INDEX `uq_spo_id` (`id`);";
                await addIdUniqueCmd.ExecuteNonQueryAsync();
                idHasKey = true;
                idHasUniqueKey = true;
            }

            if (!idIsAutoIncrement)
            {
                await using var modifyIdCmd = conn.CreateCommand();
                modifyIdCmd.CommandText =
                    "ALTER TABLE `site_prediction_optimized` MODIFY COLUMN `id` INT NOT NULL AUTO_INCREMENT;";
                await modifyIdCmd.ExecuteNonQueryAsync();
            }
        }

        private async Task<bool> EnsureSitePredictionOptimizedScenarioColumnAsync(DbConnection conn)
        {
            await using (var createCmd = conn.CreateCommand())
            {
                createCmd.CommandText = "CREATE TABLE IF NOT EXISTS `site_prediction_optimized` LIKE `site_prediction`;";
                await createCmd.ExecuteNonQueryAsync();
            }

            await using var existsCmd = conn.CreateCommand();
            existsCmd.CommandText = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'site_prediction_optimized'
                  AND COLUMN_NAME = 'scenario';";

            var existsObj = await existsCmd.ExecuteScalarAsync();
            var exists = existsObj != null && existsObj != DBNull.Value && Convert.ToInt32(existsObj) > 0;
            if (exists)
                return false;

            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = @"
                ALTER TABLE `site_prediction_optimized`
                ADD COLUMN `scenario` INT NOT NULL DEFAULT 1 AFTER `site_prediction_id`;";
            await alterCmd.ExecuteNonQueryAsync();
            return true;
        }

        [HttpPost, Route("EnsureSitePredictionOptimizedScenarioColumn")]
        public async Task<IActionResult> EnsureSitePredictionOptimizedScenarioColumn()
        {
            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                    await conn.OpenAsync();

                var created = await EnsureSitePredictionOptimizedScenarioColumnAsync(conn);
                return Ok(new
                {
                    Status = 1,
                    Message = created
                        ? "scenario column created in site_prediction_optimized."
                        : "scenario column already exists in site_prediction_optimized.",
                    Table = "site_prediction_optimized",
                    Column = "scenario",
                    Created = created
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "Error creating scenario column in site_prediction_optimized.",
                    Details = SafeException.Get(ex)
                });
            }
        }

        private static async Task<HashSet<string>> GetTableColumnSetAsync(DbConnection conn, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName;";
            Add(cmd, "@tableName", tableName);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    columns.Add(name);
                }
            }
            return columns;
        }

        private async Task<long?> ResolveSitePredictionProjectIdAsync(
            DbConnection conn,
            DbTransaction? tx,
            IReadOnlyList<Newtonsoft.Json.Linq.JObject> items)
        {
            var explicitProjectIds = new HashSet<long>();
            foreach (var item in items)
            {
                if (TryGetLongToken(item, "project_id", out var projectId) ||
                    TryGetLongToken(item, "projectId", out projectId))
                {
                    if (projectId > 0)
                        explicitProjectIds.Add(projectId);
                }
            }

            if (explicitProjectIds.Count == 1)
                return explicitProjectIds.First();
            if (explicitProjectIds.Count > 1)
                throw new InvalidOperationException("All rows in one scenario must belong to the same project.");

            foreach (var item in items)
            {
                long sourceId = 0;
                if (TryGetLongToken(item, "source_id", out var explicitSourceId))
                {
                    sourceId = explicitSourceId;
                }
                else if (TryGetLongToken(item, "id", out var idValue))
                {
                    sourceId = idValue;
                }

                if (sourceId <= 0)
                    continue;

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT tbl_project_id FROM site_prediction WHERE id = @id LIMIT 1;";
                Add(cmd, "@id", sourceId);
                var obj = await cmd.ExecuteScalarAsync();
                if (obj != null &&
                    obj != DBNull.Value &&
                    long.TryParse(Convert.ToString(obj, CultureInfo.InvariantCulture), out var projectId) &&
                    projectId > 0)
                {
                    return projectId;
                }
            }

            return null;
        }

        private async Task<int> ResolveSitePredictionScenarioAsync(
            DbConnection conn,
            DbTransaction tx,
            long projectId,
            IReadOnlyList<Newtonsoft.Json.Linq.JObject> items)
        {
            int requestedScenario = 0;
            foreach (var item in items)
            {
                if (TryGetIntToken(item, "scenario", out var scenario) ||
                    TryGetIntToken(item, "scenario_id", out scenario))
                {
                    if (scenario <= 0 || scenario > 6)
                        throw new InvalidOperationException("Scenario must be between 1 and 6.");

                    if (requestedScenario > 0 && requestedScenario != scenario)
                        throw new InvalidOperationException("All rows in one request must use the same scenario.");

                    requestedScenario = scenario;
                }
            }

            if (requestedScenario > 0)
                return requestedScenario;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                SELECT DISTINCT scenario
                FROM site_prediction_optimized
                WHERE tbl_project_id = @pid
                  AND scenario BETWEEN 1 AND 6
                ORDER BY scenario;";
            Add(cmd, "@pid", projectId);

            var used = new HashSet<int>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                    used.Add(Convert.ToInt32(reader.GetValue(0)));
            }

            for (var scenario = 1; scenario <= 6; scenario++)
            {
                if (!used.Contains(scenario))
                    return scenario;
            }

            throw new InvalidOperationException("please delete one scenario because u can make only 6 scenarios");
        }

        private static bool TryGetLongToken(Newtonsoft.Json.Linq.JObject item, string key, out long value)
        {
            value = 0;
            return item.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                   long.TryParse(token?.ToString(), out value);
        }

        private static bool TryGetIntToken(Newtonsoft.Json.Linq.JObject item, string key, out int value)
        {
            value = 0;
            return item.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                   int.TryParse(token?.ToString(), out value);
        }

        private string ResolveSitePredictionUpdatedBy()
        {
            return User?.FindFirst("UserId")?.Value
                ?? User?.Identity?.Name
                ?? HttpContext?.Session?.GetInt32("UserID")?.ToString()
                ?? "system";
        }

        private static object NormalizeSitePredictionValue(Newtonsoft.Json.Linq.JToken val)
        {
            if (val == null || val.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                return DBNull.Value;

            if (val.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                return (long)val;

            if (val.Type == Newtonsoft.Json.Linq.JTokenType.Float)
                return (double)val;

            if (val.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                return (bool)val;

            var raw = val.ToString();
            return string.IsNullOrWhiteSpace(raw) ? DBNull.Value : raw.Trim();
        }

        private static object NormalizeSitePredictionBandValue(Newtonsoft.Json.Linq.JToken val) =>
            NormalizeSitePredictionValue(val);

        [HttpGet, Route("GetUpdatedSitePrediction")]
        public Task<IActionResult> GetUpdatedSitePrediction(
            [FromQuery] long projectId,
            [FromQuery] int? site = null,
            [FromQuery] string? cell_id = null,
            [FromQuery] string? cluster = null,
            [FromQuery] string? provider = null,
            [FromQuery] string? technology = null,
            [FromQuery] int? band = null,
            [FromQuery] int? pci = null,
            [FromQuery] int limit = 50000,
            [FromQuery] int offset = 0,
            [FromQuery] int? scenario = null)
        {
            return GetSitePrediction(projectId, site, cell_id, cluster, provider, technology, band, pci, limit, offset, "combined", 0, scenario);
        }

        [HttpGet, Route("GetSitePrediction")]
        public async Task<IActionResult> GetSitePrediction(
            [FromQuery] long projectId,
            [FromQuery] int? site = null,
            [FromQuery] string? cell_id = null,
            [FromQuery] string? cluster = null,
            [FromQuery] string? provider = null,
            [FromQuery] string? technology = null,
            [FromQuery] int? band = null,
            [FromQuery] int? pci = null,
            [FromQuery] int limit = 50000,
            [FromQuery] int offset = 0,
            [FromQuery] string version = "combined",
            [FromQuery] int simple = 0,
            [FromQuery] int? scenario = null,
            [FromQuery(Name = "polygon_ids")] string? polygonIdsCsv = null)
        {
            if (projectId <= 0)
                return BadRequest(new { Status = 0, Message = "projectId is required" });

            var effectiveProviderFilter = string.IsNullOrWhiteSpace(provider) ? cluster : provider;
            var requestedVersion = (version ?? "combined").Trim().ToLowerInvariant();
            if (requestedVersion == "updated")
            {
                requestedVersion = "combined";
            }

            if (requestedVersion != "original" && requestedVersion != "combined")
            {
                return BadRequest(new { Status = 0, Message = "version must be 'original' or 'combined'" });
            }

            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            if (requestedVersion == "combined")
            {
                await EnsureSitePredictionOptimizedTableAsync(conn);
            }

            var sourceColumns = await GetTableColumnSetAsync(conn, "site_prediction");
            var optimizedColumns = requestedVersion == "combined"
                ? await GetTableColumnSetAsync(conn, "site_prediction_optimized")
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceNodeColumn = new[] { "node_id", "nodeb_id", "node_b_id" }
                .FirstOrDefault(candidate => sourceColumns.Contains(candidate));
            var optimizedNodeColumn = new[] { "node_id", "nodeb_id", "node_b_id" }
                .FirstOrDefault(candidate => optimizedColumns.Contains(candidate));
            var combinedNodeExpr = !string.IsNullOrWhiteSpace(sourceNodeColumn) || !string.IsNullOrWhiteSpace(optimizedNodeColumn)
                ? $"CONVERT(COALESCE({(string.IsNullOrWhiteSpace(optimizedNodeColumn) ? "NULL" : $"spo.{optimizedNodeColumn}")}, {(string.IsNullOrWhiteSpace(sourceNodeColumn) ? "NULL" : $"sp.{sourceNodeColumn}")}) USING utf8mb4) COLLATE utf8mb4_unicode_ci"
                : "NULL";
            var sourceNodeExpr = !string.IsNullOrWhiteSpace(sourceNodeColumn)
                ? $"CONVERT(sp.{sourceNodeColumn} USING utf8mb4) COLLATE utf8mb4_unicode_ci"
                : "NULL";

            if (scenario.HasValue && scenario.Value > 6)
            {
                return BadRequest(new
                {
                    Status = 0,
                    Message = "you have to delete one scenario because there is a limit you only 6 scenarios"
                });
            }

            if (scenario.HasValue && scenario.Value <= 0)
            {
                return BadRequest(new { Status = 0, Message = "scenario must be between 1 and 6" });
            }

            await using var cmd = conn.CreateCommand();
            var polygonIds = ParsePolygonIds(polygonIdsCsv);
            var polygonFilterCombined = BuildPolygonFilterClause(
                polygonIds,
                "COALESCE(spo.latitude, sp.latitude)",
                "COALESCE(spo.longitude, sp.longitude)");
            var polygonFilterOriginal = BuildPolygonFilterClause(
                polygonIds,
                "sp.latitude",
                "sp.longitude");
            var filterClause = requestedVersion == "combined"
                ? BuildSitePredictionDualFilterClause(
                    site,
                    cell_id,
                    effectiveProviderFilter,
                    technology,
                    band,
                    pci,
                    "sp.site",
                    "spo.site",
                    "sp.cell_id",
                    "spo.cell_id",
                    "sp.cluster",
                    "spo.cluster",
                    "p.provider",
                    "p.provider",
                    "sp.Technology",
                    "spo.Technology",
                    "sp.band",
                    "spo.band",
                    "sp.pci",
                    "spo.pci")
                : BuildSitePredictionFilterClause(
                    site,
                    cell_id,
                    effectiveProviderFilter,
                    technology,
                    band,
                    pci,
                    "sp.site",
                    "sp.cell_id",
                    "sp.cluster",
                    "p.provider",
                    "sp.Technology",
                    "sp.band",
                    "sp.pci");

            int? activeScenario = null;
            var hasScenarioData = false;
            var hasAnyScenarioData = false;
            if (requestedVersion == "combined" && optimizedColumns.Contains("scenario"))
            {
                if (scenario.HasValue)
                {
                    activeScenario = scenario.Value;
                    await using (var anyScenarioCmd = conn.CreateCommand())
                    {
                        anyScenarioCmd.CommandText = @"
                            SELECT COUNT(1)
                            FROM site_prediction_optimized
                            WHERE tbl_project_id = @pid
                              AND scenario BETWEEN 1 AND 6;";
                        Add(anyScenarioCmd, "@pid", projectId);
                        hasAnyScenarioData = Convert.ToInt32(await anyScenarioCmd.ExecuteScalarAsync() ?? 0) > 0;
                    }

                    await using (var existsScenarioCmd = conn.CreateCommand())
                    {
                        existsScenarioCmd.CommandText = @"
                            SELECT COUNT(1)
                            FROM site_prediction_optimized
                            WHERE tbl_project_id = @pid
                              AND scenario = @scenario;";
                        Add(existsScenarioCmd, "@pid", projectId);
                        Add(existsScenarioCmd, "@scenario", activeScenario.Value);
                        hasScenarioData = Convert.ToInt32(await existsScenarioCmd.ExecuteScalarAsync() ?? 0) > 0;
                    }
                }
                else
                {
                    await using var scenarioCmd = conn.CreateCommand();
                    scenarioCmd.CommandText = @"
                        SELECT scenario
                        FROM site_prediction_optimized
                        WHERE tbl_project_id = @pid
                          AND scenario BETWEEN 1 AND 6
                        GROUP BY scenario
                        ORDER BY scenario
                        LIMIT 1;";
                    Add(scenarioCmd, "@pid", projectId);
                    var scenarioObj = await scenarioCmd.ExecuteScalarAsync();
                    if (scenarioObj != null && scenarioObj != DBNull.Value)
                    {
                        activeScenario = Convert.ToInt32(scenarioObj);
                        hasScenarioData = true;
                        hasAnyScenarioData = true;
                    }
                }
            }

            if (scenario.HasValue && hasAnyScenarioData && !hasScenarioData)
            {
                return Json(new
                {
                    Status = 1,
                    Version = requestedVersion,
                    Scenario = activeScenario,
                    ScenarioName = $"Scenario {activeScenario}",
                    Message = $"Scenario {activeScenario} has no data.",
                    Count = 0,
                    TotalCount = 0,
                    Data = Array.Empty<object>()
                });
            }

            var scenarioFilterClause = hasScenarioData && activeScenario.HasValue
                ? "AND spo.scenario = @scenario"
                : string.Empty;
            var optimizedHasScenarioSelect = optimizedColumns.Contains("scenario") ? "spo.scenario" : "NULL";
            var scenarioSelectExpr = requestedVersion == "combined"
                ? $"{optimizedHasScenarioSelect} AS scenario,"
                : "NULL AS scenario,";

            static string ResolveTxPowerExpr(string alias, HashSet<string> columns)
            {
                if (columns.Contains("tx_power"))
                    return $"{alias}.`tx_power`";
                if (columns.Contains("maximum_transmission_power_of_resource"))
                    return $"{alias}.`maximum_transmission_power_of_resource`";
                return "NULL";
            }

            var baselineTxPowerExpr = ResolveTxPowerExpr("sp", sourceColumns);
            var optimizedTxPowerExpr = ResolveTxPowerExpr("spo", optimizedColumns);
            var mergedTxPowerExpr = requestedVersion == "combined"
                ? $"COALESCE({optimizedTxPowerExpr}, {baselineTxPowerExpr})"
                : baselineTxPowerExpr;

            cmd.CommandText = requestedVersion == "combined"
                ? $@"
                WITH optimized_rows AS (
                    SELECT
                        original_id,
                        optimized_id,
                        is_updated,
                        version,
                        status,
                        created_at,
                        updated_at,
                        updated_by,
                        scenario,
                        site,
                        site_name,
                        node_id,
                        sector,
                        cell_id,
                        sec_id,
                        longitude,
                        latitude,
                        tac,
                        pci,
                        azimuth,
                        height,
                        bw,
                        m_tilt,
                        e_tilt,
                        maximum_transmission_power_of_resource,
                        real_transmit_power_of_resource,
                        reference_signal_power,
                        cellsize,
                        frequency,
                        band,
                        uplink_center_frequency,
                        downlink_frequency,
                        earfcn,
                        project_provider,
                        original_cluster,
                        optimized_cluster,
                        cluster,
                        tbl_project_id,
                        tbl_upload_id,
                        Technology
                    FROM (
                    SELECT
                        COALESCE(spo.site_prediction_id, sp.id) AS original_id,
                        spo.id AS optimized_id,
                        1 AS is_updated,
                        COALESCE(spo.version, 1) AS version,
                        CONVERT(COALESCE(spo.status, 'updated') USING utf8mb4) COLLATE utf8mb4_unicode_ci AS status,
                        spo.created_at,
                        spo.updated_at,
                        CONVERT(spo.updated_by USING utf8mb4) COLLATE utf8mb4_unicode_ci AS updated_by,
                        {scenarioSelectExpr}
                        CONVERT(COALESCE(spo.site, sp.site) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS site,
                        CONVERT(COALESCE(spo.site_name, sp.site_name) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS site_name,
                        {combinedNodeExpr} AS node_id,
                        CONVERT(COALESCE(spo.sector, sp.sector) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS sector,
                        CONVERT(COALESCE(spo.cell_id, sp.cell_id) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS cell_id,
                        CONVERT(COALESCE(spo.sec_id, sp.sec_id) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS sec_id,
                        COALESCE(spo.longitude, sp.longitude) AS longitude,
                        COALESCE(spo.latitude, sp.latitude) AS latitude,
                        COALESCE(spo.tac, sp.tac) AS tac,
                        COALESCE(spo.pci, sp.pci) AS pci,
                        COALESCE(spo.azimuth, sp.azimuth) AS azimuth,
                        COALESCE(spo.height, sp.height) AS height,
                        COALESCE(spo.bw, sp.bw) AS bw,
                        COALESCE(spo.m_tilt, sp.m_tilt) AS m_tilt,
                        COALESCE(spo.e_tilt, sp.e_tilt) AS e_tilt,
                        {mergedTxPowerExpr} AS maximum_transmission_power_of_resource,
                        COALESCE(spo.real_transmit_power_of_resource, sp.real_transmit_power_of_resource) AS real_transmit_power_of_resource,
                        COALESCE(spo.reference_signal_power, sp.reference_signal_power) AS reference_signal_power,
                        COALESCE(spo.cellsize, sp.cellsize) AS cellsize,
                        COALESCE(spo.frequency, sp.frequency) AS frequency,
                        COALESCE(spo.band, sp.band) AS band,
                        COALESCE(spo.uplink_center_frequency, sp.uplink_center_frequency) AS uplink_center_frequency,
                        COALESCE(spo.downlink_frequency, sp.downlink_frequency) AS downlink_frequency,
                        COALESCE(spo.earfcn, sp.earfcn) AS earfcn,
                        CONVERT(p.provider USING utf8mb4) COLLATE utf8mb4_unicode_ci AS project_provider,
                        CONVERT(sp.cluster USING utf8mb4) COLLATE utf8mb4_unicode_ci AS original_cluster,
                        CONVERT(spo.cluster USING utf8mb4) COLLATE utf8mb4_unicode_ci AS optimized_cluster,
                        CONVERT(COALESCE(spo.cluster, sp.cluster) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS cluster,
                        COALESCE(spo.tbl_project_id, sp.tbl_project_id) AS tbl_project_id,
                        COALESCE(spo.tbl_upload_id, sp.tbl_upload_id) AS tbl_upload_id,
                        CONVERT(COALESCE(spo.Technology, sp.Technology) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS Technology,
                        ROW_NUMBER() OVER (
                            PARTITION BY
                                COALESCE(spo.site_prediction_id, 0),
                                CONVERT(COALESCE(spo.site, sp.site, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                CONVERT(COALESCE(spo.sector, sp.sector, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                CONVERT(COALESCE(spo.cell_id, sp.cell_id, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci
                            ORDER BY spo.id DESC
                        ) AS rn
                    FROM site_prediction_optimized spo
                    LEFT JOIN site_prediction sp ON sp.id = spo.site_prediction_id
                    LEFT JOIN tbl_project p ON p.id = COALESCE(spo.tbl_project_id, sp.tbl_project_id)
                    WHERE COALESCE(spo.tbl_project_id, sp.tbl_project_id) = @pid
                    {scenarioFilterClause}
                    {filterClause}
                    {polygonFilterCombined}
                      AND NOT (
                          (
                              spo.sector IS NULL
                              OR TRIM(CONVERT(spo.sector USING utf8mb4)) = ''
                          )
                          AND COALESCE(spo.cell_id, sp.cell_id) IS NOT NULL
                          AND EXISTS (
                              SELECT 1
                              FROM site_prediction_optimized spo2
                              LEFT JOIN site_prediction sp2 ON sp2.id = spo2.site_prediction_id
                              WHERE COALESCE(spo2.tbl_project_id, sp2.tbl_project_id) = COALESCE(spo.tbl_project_id, sp.tbl_project_id)
                                AND spo2.scenario = spo.scenario
                                AND CONVERT(COALESCE(spo2.site, sp2.site, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                    CONVERT(COALESCE(spo.site, sp.site, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci
                                AND CONVERT(COALESCE(spo2.cell_id, sp2.cell_id, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                    CONVERT(COALESCE(spo.cell_id, sp.cell_id, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci
                                AND spo2.id <> spo.id
                                AND spo2.sector IS NOT NULL
                                AND TRIM(CONVERT(spo2.sector USING utf8mb4)) <> ''
                          )
                      )
                    ) ranked_optimized
                    WHERE rn = 1
                ),
                baseline_rows AS (
                    SELECT
                        sp.id AS original_id,
                        NULL AS optimized_id,
                        0 AS is_updated,
                        0 AS version,
                        'original' AS status,
                        NULL AS created_at,
                        NULL AS updated_at,
                        NULL AS updated_by,
                        NULL AS scenario,
                        CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci AS site,
                        CONVERT(sp.site_name USING utf8mb4) COLLATE utf8mb4_unicode_ci AS site_name,
                        {sourceNodeExpr} AS node_id,
                        CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci AS sector,
                        CONVERT(sp.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci AS cell_id,
                        CONVERT(sp.sec_id USING utf8mb4) COLLATE utf8mb4_unicode_ci AS sec_id,
                        sp.longitude,
                        sp.latitude,
                        sp.tac,
                        sp.pci,
                        sp.azimuth,
                        sp.height,
                        sp.bw,
                        sp.m_tilt,
                        sp.e_tilt,
                        {baselineTxPowerExpr} AS maximum_transmission_power_of_resource,
                        sp.real_transmit_power_of_resource,
                        sp.reference_signal_power,
                        sp.cellsize,
                        sp.frequency,
                        sp.band,
                        sp.uplink_center_frequency,
                        sp.downlink_frequency,
                        sp.earfcn,
                        CONVERT(p.provider USING utf8mb4) COLLATE utf8mb4_unicode_ci AS project_provider,
                        CONVERT(sp.cluster USING utf8mb4) COLLATE utf8mb4_unicode_ci AS original_cluster,
                        NULL AS optimized_cluster,
                        CONVERT(sp.cluster USING utf8mb4) COLLATE utf8mb4_unicode_ci AS cluster,
                        sp.tbl_project_id,
                        sp.tbl_upload_id,
                        CONVERT(sp.Technology USING utf8mb4) COLLATE utf8mb4_unicode_ci AS Technology
                    FROM site_prediction sp
                    LEFT JOIN tbl_project p ON p.id = sp.tbl_project_id
                    LEFT JOIN site_prediction_optimized spo ON 1 = 0
                    WHERE sp.tbl_project_id = @pid
                    {filterClause}
                    {polygonFilterOriginal}
                      AND NOT EXISTS (
                          SELECT 1
                          FROM optimized_rows `orw`
                          WHERE
                              CONVERT(COALESCE(`orw`.site, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                  CONVERT(COALESCE(sp.site, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci
                              AND CONVERT(COALESCE(`orw`.sector, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                  CONVERT(COALESCE(sp.sector, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci
                      )
                )
                SELECT
                    *
                FROM optimized_rows
                UNION ALL
                SELECT
                    *
                FROM baseline_rows
                ORDER BY is_updated DESC, original_id DESC;"
                : $@"
                SELECT
                    sp.id AS original_id,
                    NULL AS optimized_id,
                    0 AS is_updated,
                    0 AS version,
                    'original' AS status,
                    NULL AS created_at,
                    NULL AS updated_at,
                    NULL AS updated_by,
                    NULL AS scenario,
                    sp.site,
                    sp.site_name,
                    {sourceNodeExpr} AS node_id,
                    sp.sector,
                    sp.cell_id,
                    sp.sec_id,
                    sp.longitude,
                    sp.latitude,
                    sp.tac,
                    sp.pci,
                    sp.azimuth,
                    sp.height,
                    sp.bw,
                    sp.m_tilt,
                    sp.e_tilt,
                    {mergedTxPowerExpr} AS maximum_transmission_power_of_resource,
                    sp.real_transmit_power_of_resource,
                    sp.reference_signal_power,
                    sp.cellsize,
                    sp.frequency,
                    sp.band,
                    sp.uplink_center_frequency,
                    sp.downlink_frequency,
                    sp.earfcn,
                    CONVERT(p.provider USING utf8mb4) COLLATE utf8mb4_unicode_ci AS project_provider,
                    sp.cluster AS original_cluster,
                    NULL AS optimized_cluster,
                    sp.cluster AS cluster,
                    sp.tbl_project_id,
                    sp.tbl_upload_id,
                    sp.Technology
                FROM site_prediction sp
                LEFT JOIN tbl_project p ON p.id = sp.tbl_project_id
                WHERE sp.tbl_project_id = @pid
                {filterClause}
                {polygonFilterOriginal}
                ORDER BY sp.id DESC;";

            Add(cmd, "@pid", projectId);
            if (hasScenarioData && activeScenario.HasValue)
            {
                Add(cmd, "@scenario", activeScenario.Value);
            }
            AddSitePredictionFilterParameters(cmd, site, cell_id, effectiveProviderFilter, technology, band, pci);
            AddPolygonIdsParameters(cmd, polygonIds);

            var list = new List<Dictionary<string, object?>>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < r.FieldCount; i++)
                {
                    row[r.GetName(i)] = await r.IsDBNullAsync(i) ? null : r.GetValue(i);
                }
                EnrichSitePredictionRow(row);
                list.Add(row);
            }

            var pageSize = Math.Clamp(limit, 1, 50000);
            var safeOffset = Math.Max(offset, 0);
            var pagedRows = SliceCachedPage(list, pageSize, safeOffset);
            var simplified = simple == 1;
            List<object> resultRows = simplified
                ? pagedRows.Select(ToSimpleSitePredictionRow).ToList<object>()
                : pagedRows.Cast<object>().ToList();
            var response = new
            {
                Status = 1,
                Version = requestedVersion,
                Scenario = activeScenario,
                ScenarioName = activeScenario.HasValue ? $"Scenario {activeScenario.Value}" : null,
                ScenarioMode = hasScenarioData ? "scenario" : "full_project",
                Count = resultRows.Count,
                TotalCount = list.Count,
                Data = resultRows
            };

            return Json(response);
        }

        private static object ToSimpleSitePredictionRow(Dictionary<string, object?> row)
        {
            static string? ReadString(Dictionary<string, object?> source, params string[] keys)
            {
                foreach (var key in keys)
                {
                    if (!source.TryGetValue(key, out var value) || value == null) continue;
                    var text = Convert.ToString(value)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                return null;
            }

            static double? ReadDouble(Dictionary<string, object?> source, params string[] keys)
            {
                foreach (var key in keys)
                {
                    if (!source.TryGetValue(key, out var value) || value == null) continue;
                    try
                    {
                        if (value is IConvertible) return Convert.ToDouble(value);
                    }
                    catch { }
                }
                return null;
            }

            static int? ReadInt(Dictionary<string, object?> source, params string[] keys)
            {
                foreach (var key in keys)
                {
                    if (!source.TryGetValue(key, out var value) || value == null) continue;
                    try
                    {
                        if (value is IConvertible) return Convert.ToInt32(value);
                    }
                    catch { }
                }
                return null;
            }

            return new
            {
                id = ReadInt(row, "original_id", "id"),
                optimizedId = ReadInt(row, "optimized_id"),
                scenario = ReadInt(row, "scenario"),
                isUpdated = ReadInt(row, "is_updated") ?? 0,
                status = ReadString(row, "status") ?? "original",
                version = ReadInt(row, "version") ?? 0,
                projectId = ReadInt(row, "tbl_project_id"),
                site = ReadString(row, "site"),
                siteName = ReadString(row, "site_name"),
                nodeId = ReadString(row, "node_id", "nodeb_id", "node_b_id"),
                sector = ReadString(row, "sector"),
                secId = ReadString(row, "sec_id"),
                cellId = ReadString(row, "cell_id"),
                lat = ReadDouble(row, "latitude", "lat"),
                lng = ReadDouble(row, "longitude", "lon", "lng"),
                azimuth = ReadDouble(row, "azimuth"),
                height = ReadDouble(row, "height"),
                bw = ReadDouble(row, "bw"),
                mTilt = ReadDouble(row, "m_tilt"),
                eTilt = ReadDouble(row, "e_tilt"),
                band = ReadString(row, "band"),
                earfcn = ReadString(row, "earfcn"),
                pci = ReadInt(row, "pci"),
                tac = ReadString(row, "tac"),
                technology = ReadString(row, "Technology", "technology"),
                provider = ReadString(row, "provider", "operator_name", "project_provider", "cluster"),
                operatorName = ReadString(row, "operator_name", "provider", "project_provider", "cluster"),
                cellsize = ReadString(row, "cellsize", "cell_size", "cellSize"),
                frequency = ReadString(row, "frequency"),
                uplink_center_frequency = ReadString(row, "uplink_center_frequency", "uplink_frequency", "uplinkCenterFrequency", "uplinkFrequency"),
                downlink_frequency = ReadString(row, "downlink_frequency", "download_frequency", "downlinkFrequency", "downloadFrequency"),
                real_transmit_power_of_resource = ReadDouble(row, "real_transmit_power_of_resource", "realTransmitPowerOfResource"),
                txPower = ReadDouble(row, "maximum_transmission_power_of_resource", "tx_power"),
                rsPower = ReadDouble(row, "reference_signal_power")
            };
        }

        [HttpGet, Route("CompareSitePrediction")]
        public async Task<IActionResult> CompareSitePrediction(
            [FromQuery] long projectId,
            [FromQuery] int? site = null,
            [FromQuery] string? cell_id = null,
            [FromQuery] string? cluster = null,
            [FromQuery] string? technology = null,
            [FromQuery] int? band = null,
            [FromQuery] int? pci = null,
            [FromQuery] int limit = 50000,
            [FromQuery] int offset = 0)
        {
            if (projectId <= 0)
                return BadRequest(new { Status = 0, Message = "projectId is required" });

            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await EnsureSitePredictionOptimizedTableAsync(conn);
            var sourceColumns = await GetTableColumnSetAsync(conn, "site_prediction");
            var optimizedColumns = await GetTableColumnSetAsync(conn, "site_prediction_optimized");
            var optimizedOnlyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "site_prediction_id",
                "is_updated",
                "version",
                "status",
                "created_at",
                "updated_at",
                "updated_by"
            };
            var commonColumns = sourceColumns
                .Where(col => optimizedColumns.Contains(col) && !optimizedOnlyColumns.Contains(col))
                .OrderBy(col => col.Equals("id", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(col => col, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (commonColumns.Count == 0)
            {
                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "No common columns found between site_prediction and site_prediction_optimized."
                });
            }

            await using var cmd = conn.CreateCommand();
            var filterClause = BuildSitePredictionDualFilterClause(
                site,
                cell_id,
                cluster,
                technology,
                band,
                pci,
                "sp.site",
                "spo.site",
                "sp.cell_id",
                "spo.cell_id",
                "sp.cluster",
                "spo.cluster",
                "p.provider",
                "p.provider",
                "sp.Technology",
                "spo.Technology",
                "sp.band",
                "spo.band",
                "sp.pci",
                "spo.pci");
            var compareSelectColumns = string.Join(
                ",\n                    ",
                commonColumns.SelectMany(col => new[]
                {
                    $"sp.`{col}` AS `original_{col}`",
                    $"spo.`{col}` AS `updated_{col}`"
                }));

            cmd.CommandText = $@"
                WITH latest_optimized AS (
                    SELECT *
                    FROM (
                        SELECT
                            spo.*,
                            ROW_NUMBER() OVER (
                                PARTITION BY
                                    COALESCE(spo.site_prediction_id, 0),
                                    CONVERT(COALESCE(spo.site, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                    CONVERT(COALESCE(spo.sector, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                    CONVERT(COALESCE(spo.cell_id, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci
                                ORDER BY spo.id DESC
                            ) AS rn
                        FROM site_prediction_optimized spo
                        WHERE spo.tbl_project_id = @pid
                    ) ranked
                    WHERE rn = 1
                )
                SELECT
                    {compareSelectColumns}
                FROM latest_optimized spo
                LEFT JOIN site_prediction sp
                    ON sp.id = spo.site_prediction_id
                    OR (
                        sp.tbl_project_id = spo.tbl_project_id
                        AND CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                            CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci
                        AND (
                            spo.sector IS NULL
                            OR sp.sector IS NULL
                            OR CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                CONVERT(spo.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci
                        )
                        AND (
                            spo.cell_id IS NULL
                            OR sp.cell_id IS NULL
                            OR CONVERT(sp.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                CONVERT(spo.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci
                        )
                    )
                LEFT JOIN tbl_project p ON p.id = COALESCE(sp.tbl_project_id, spo.tbl_project_id)
                WHERE COALESCE(sp.tbl_project_id, spo.tbl_project_id) = @pid
                {filterClause}
                ORDER BY COALESCE(sp.id, spo.site_prediction_id, spo.id) DESC;";

            Add(cmd, "@pid", projectId);
            AddSitePredictionFilterParameters(cmd, site, cell_id, cluster, technology, band, pci);

            var allRows = new List<CompareSitePredictionCacheRow>();

            await using var r = await cmd.ExecuteReaderAsync();
            var baselineOrdinals = commonColumns.ToDictionary(
                col => col,
                col => r.GetOrdinal($"original_{col}"),
                StringComparer.OrdinalIgnoreCase);
            var optimizedOrdinals = commonColumns.ToDictionary(
                col => col,
                col => r.GetOrdinal($"updated_{col}"),
                StringComparer.OrdinalIgnoreCase);

            while (await r.ReadAsync())
            {
                var baselineRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var optimizedRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                foreach (var col in commonColumns)
                {
                    var baselineOrdinal = baselineOrdinals[col];
                    var optimizedOrdinal = optimizedOrdinals[col];
                    baselineRow[col] = await r.IsDBNullAsync(baselineOrdinal) ? null : r.GetValue(baselineOrdinal);
                    optimizedRow[col] = await r.IsDBNullAsync(optimizedOrdinal) ? null : r.GetValue(optimizedOrdinal);
                }

                allRows.Add(new CompareSitePredictionCacheRow
                {
                    baseline = baselineRow,
                    optimized = optimizedRow
                });
            }

            var pagedRows = SliceCachedPage(allRows, Math.Clamp(limit, 1, 50000), Math.Max(offset, 0));
            var response = new
            {
                Status = 1,
                baseline = pagedRows.Select(x => new { baseline = x.baseline }).ToList(),
                optimized = pagedRows.Select(x => new { optimized = x.optimized }).ToList()
            };

            return Json(response);
        }

        [HttpPost, Route("UpdateSitePrediction")]
        public async Task<IActionResult> UpdateSitePrediction()
        {
            long currentItemId = 0;
            try
            {
                using var reader = new System.IO.StreamReader(Request.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return BadRequest(new { Status = 0, Message = "Empty payload" });

                var payloadTokens = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JToken>(body);
                if (payloadTokens == null)
                    return BadRequest(new { Status = 0, Message = "Invalid JSON" });

                var items = new List<Newtonsoft.Json.Linq.JObject>();
                if (payloadTokens is Newtonsoft.Json.Linq.JArray arr)
                {
                    foreach (var token in arr)
                    {
                        if (token is Newtonsoft.Json.Linq.JObject obj) items.Add(obj);
                    }
                }
                else if (payloadTokens is Newtonsoft.Json.Linq.JObject obj)
                { 
                    items.Add(obj);
                }
                else
                {
                    return BadRequest(new { Status = 0, Message = "Expected JSON object or array" });
                }

                if (items.Count == 0)
                    return Ok(new { Status = 1, Message = "No items to update" });

                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync();

                await EnsureSitePredictionNameColumnsAsync(conn);
                await EnsureSitePredictionOptimizedTableAsync(conn);
                var sourceColumns = await GetTableColumnSetAsync(conn, "site_prediction");
                var optimizedColumns = await GetTableColumnSetAsync(conn, "site_prediction_optimized");
                var reservedOptimizedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "id",
                    "site_prediction_id",
                    "scenario",
                    "scenario_id",
                    "is_updated",
                    "version",
                    "status",
                    "created_at",
                    "updated_at",
                    "updated_by"
                };
                var copyColumns = sourceColumns
                    .Where(col => optimizedColumns.Contains(col) && !reservedOptimizedColumns.Contains(col))
                    .ToList();

                if (copyColumns.Count == 0)
                {
                    throw new InvalidOperationException("No common columns found between site_prediction and site_prediction_optimized.");
                }

                var insertColumnList = string.Join(
                    ",\n                                    ",
                    copyColumns.Select(col => $"`{col}`"));
                var selectColumnList = string.Join(
                    ",\n                                    ",
                    copyColumns.Select(col => $"sp.`{col}`"));

                int totalUpdated = 0;
                var requestedIds = new List<long>();
                var updatedIds = new List<long>();
                var skippedIds = new List<long>();
                var updatedBy = ResolveSitePredictionUpdatedBy();

                await using var tx = await conn.BeginTransactionAsync();
                var scenarioProjectId = await ResolveSitePredictionProjectIdAsync(conn, tx, items);
                if (!scenarioProjectId.HasValue || scenarioProjectId.Value <= 0)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new
                    {
                        Status = 0,
                        Message = "project_id is required to save an optimized scenario."
                    });
                }

                var scenario = await ResolveSitePredictionScenarioAsync(conn, tx, scenarioProjectId.Value, items);

                var itemIndex = 0;
                foreach (var item in items)
                {
                    itemIndex++;
                    long parsedLookupId = 0;
                    var hasLookupId =
                        item.TryGetValue("id", StringComparison.OrdinalIgnoreCase, out var idToken) &&
                        long.TryParse(idToken.ToString(), out parsedLookupId) &&
                        parsedLookupId > 0;
                    var lookupId = hasLookupId ? parsedLookupId : itemIndex;

                    if (hasLookupId)
                    {
                        currentItemId = lookupId;
                        requestedIds.Add(lookupId);
                    }

                    long? explicitSourceId = null;
                    if (item.TryGetValue("source_id", StringComparison.OrdinalIgnoreCase, out var sourceIdToken) &&
                        long.TryParse(sourceIdToken.ToString(), out var parsedSourceId))
                    {
                        explicitSourceId = parsedSourceId;
                    }

                    long? explicitSiteId = null;
                    string? explicitSiteSelectorText = null;
                    if (item.TryGetValue("site_id_selector", StringComparison.OrdinalIgnoreCase, out var siteIdToken) &&
                        long.TryParse(siteIdToken.ToString(), out var parsedSiteId))
                    {
                        explicitSiteId = parsedSiteId;
                    }
                    if (item.TryGetValue("site_id_selector", StringComparison.OrdinalIgnoreCase, out var siteSelectorToken))
                    {
                        var rawSiteSelector = siteSelectorToken?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawSiteSelector))
                        {
                            explicitSiteSelectorText = rawSiteSelector;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(explicitSiteSelectorText) &&
                        item.TryGetValue("site_selector", StringComparison.OrdinalIgnoreCase, out var altSiteSelectorToken))
                    {
                        var rawAltSiteSelector = altSiteSelectorToken?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawAltSiteSelector))
                        {
                            explicitSiteSelectorText = rawAltSiteSelector;
                        }
                    }

                    string? explicitSectorSelectorText = null;
                    if (item.TryGetValue("sector_selector", StringComparison.OrdinalIgnoreCase, out var sectorSelectorToken))
                    {
                        var rawSectorSelector = sectorSelectorToken?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawSectorSelector))
                        {
                            explicitSectorSelectorText = rawSectorSelector;
                        }
                    }
                    var hasSiteSectorSelector =
                        !string.IsNullOrWhiteSpace(explicitSiteSelectorText) &&
                        !string.IsNullOrWhiteSpace(explicitSectorSelectorText);
                    var moveEntireSite =
                        item.TryGetValue("move_entire_site", StringComparison.OrdinalIgnoreCase, out var moveEntireSiteToken) &&
                        bool.TryParse(moveEntireSiteToken.ToString(), out var parsedMoveEntireSite) &&
                        parsedMoveEntireSite;
                    var hasWholeSiteSelector =
                        moveEntireSite &&
                        !string.IsNullOrWhiteSpace(explicitSiteSelectorText);

                    long? sourceId = explicitSourceId;
                    long? siteId = explicitSiteId;

                    if (!sourceId.HasValue && !siteId.HasValue && hasLookupId)
                    {
                        // Backward-compatible resolution:
                        // 1) if id exists as site_prediction.id -> treat as source row id
                        // 2) else treat id as site id
                        await using var existsCmd = conn.CreateCommand();
                        existsCmd.Transaction = tx;
                        existsCmd.CommandText = "SELECT COUNT(*) FROM site_prediction WHERE id = @id;";
                        Add(existsCmd, "@id", lookupId);
                        var existsObj = await existsCmd.ExecuteScalarAsync();
                        var rowExists = existsObj != null && existsObj != DBNull.Value && Convert.ToInt32(existsObj) > 0;

                        if (rowExists) sourceId = lookupId;
                        else siteId = lookupId;
                    }

                    if (!sourceId.HasValue && !siteId.HasValue && !hasSiteSectorSelector && !hasWholeSiteSelector)
                    {
                        skippedIds.Add(lookupId);
                        continue;
                    }

                    var updates = new List<string>();
                    var parameters = new Dictionary<string, object?>();
                    var seenDbColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var prop in item.Properties())
                    {
                        var key = prop.Name;
                        if (key.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("source_id", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("project_id", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("projectId", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("scenario", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("scenario_id", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("move_entire_site", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("site_id_selector", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("site_selector", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.Equals("sector_selector", StringComparison.OrdinalIgnoreCase)) continue;

                        string? dbColumn = null;
                        if (key.Equals("node_id", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("nodeId", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("node_b_id", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("nodeb_id", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("nodebId", StringComparison.OrdinalIgnoreCase))
                        {
                            dbColumn = new[] { "node_id", "nodeb_id", "node_b_id" }
                                .FirstOrDefault(candidate => optimizedColumns.Contains(candidate));
                        }
                        else if (SitePredictionColumnMap.TryGetValue(key, out var mappedColumn))
                        {
                            dbColumn = mappedColumn;
                        }
                        else if (!string.IsNullOrWhiteSpace(key) &&
                                 optimizedColumns.Contains(key) &&
                                 !reservedOptimizedColumns.Contains(key))
                        {
                            // Allow direct column updates for fields that exist in DB but are
                            // not explicitly listed in SitePredictionColumnMap.
                            dbColumn = key;
                        }

                        if (!string.IsNullOrWhiteSpace(dbColumn))
                        {
                            if (!seenDbColumns.Add(dbColumn)) continue;
                            string paramName = "@" + key + "_" + lookupId;
                            updates.Add($"spo.`{dbColumn}` = {paramName}");
                            parameters[paramName] = dbColumn.Equals("band", StringComparison.OrdinalIgnoreCase)
                                ? NormalizeSitePredictionBandValue(prop.Value)
                                : NormalizeSitePredictionValue(prop.Value);
                        }
                    }

                    if (updates.Count == 0)
                    {
                        skippedIds.Add(lookupId);
                        continue;
                    }

                    await using (var seedCmd = conn.CreateCommand())
                    {
                        seedCmd.Transaction = tx;
                        seedCmd.CommandText = sourceId.HasValue
                            ? $@"
                                INSERT INTO site_prediction_optimized (
                                    site_prediction_id,
                                    scenario,
                                    {insertColumnList},
                                    is_updated,
                                    version,
                                    status,
                                    created_at,
                                    updated_at,
                                    updated_by
                                )
                                SELECT
                                    sp.id,
                                    @scenario,
                                    {selectColumnList},
                                    1,
                                    0,
                                    'updated',
                                    UTC_TIMESTAMP(),
                                    UTC_TIMESTAMP(),
                                    @updatedBy
                                FROM site_prediction sp
                                WHERE sp.id = @sourceId
                                  AND sp.tbl_project_id = @scenarioProjectId
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM site_prediction_optimized spo
                                      WHERE spo.site_prediction_id = sp.id
                                        AND spo.scenario = @scenario
                                  );"
                            : hasWholeSiteSelector
                                ? $@"
                                INSERT INTO site_prediction_optimized (
                                    site_prediction_id,
                                    scenario,
                                    {insertColumnList},
                                    is_updated,
                                    version,
                                    status,
                                    created_at,
                                    updated_at,
                                    updated_by
                                )
                                SELECT
                                    sp.id,
                                    @scenario,
                                    {selectColumnList},
                                    1,
                                    0,
                                    'updated',
                                    UTC_TIMESTAMP(),
                                    UTC_TIMESTAMP(),
                                    @updatedBy
                                FROM site_prediction sp
                                WHERE CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSiteText
                                  AND sp.tbl_project_id = @scenarioProjectId
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM site_prediction_optimized spo
                                      WHERE spo.site_prediction_id = sp.id
                                        AND spo.scenario = @scenario
                                  );"
                            : hasSiteSectorSelector
                                ? $@"
                                INSERT INTO site_prediction_optimized (
                                    site_prediction_id,
                                    scenario,
                                    {insertColumnList},
                                    is_updated,
                                    version,
                                    status,
                                    created_at,
                                    updated_at,
                                    updated_by
                                )
                                SELECT
                                    sp.id,
                                    @scenario,
                                    {selectColumnList},
                                    1,
                                    0,
                                    'updated',
                                    UTC_TIMESTAMP(),
                                    UTC_TIMESTAMP(),
                                    @updatedBy
                                FROM site_prediction sp
                                WHERE CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSiteText
                                  AND CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSectorText
                                  AND sp.tbl_project_id = @scenarioProjectId
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM site_prediction_optimized spo
                                      WHERE spo.site_prediction_id = sp.id
                                        AND spo.scenario = @scenario
                                  );"
                            : $@"
                                INSERT INTO site_prediction_optimized (
                                    site_prediction_id,
                                    scenario,
                                    {insertColumnList},
                                    is_updated,
                                    version,
                                    status,
                                    created_at,
                                    updated_at,
                                    updated_by
                                )
                                SELECT
                                    sp.id,
                                    @scenario,
                                    {selectColumnList},
                                    1,
                                    0,
                                    'updated',
                                    UTC_TIMESTAMP(),
                                    UTC_TIMESTAMP(),
                                    @updatedBy
                                FROM site_prediction sp
                                WHERE sp.site = @targetSite
                                  AND sp.tbl_project_id = @scenarioProjectId
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM site_prediction_optimized spo
                                      WHERE spo.site_prediction_id = sp.id
                                        AND spo.scenario = @scenario
                                  );";

                        Add(seedCmd, "@updatedBy", updatedBy);
                        Add(seedCmd, "@scenario", scenario);
                        Add(seedCmd, "@scenarioProjectId", scenarioProjectId.Value);
                        if (sourceId.HasValue) Add(seedCmd, "@sourceId", sourceId.Value);
                        else if (hasWholeSiteSelector)
                        {
                            Add(seedCmd, "@targetSiteText", explicitSiteSelectorText!);
                        }
                        else if (hasSiteSectorSelector)
                        {
                            Add(seedCmd, "@targetSiteText", explicitSiteSelectorText!);
                            Add(seedCmd, "@targetSectorText", explicitSectorSelectorText!);
                        }
                        else Add(seedCmd, "@targetSite", siteId!.Value);
                        await seedCmd.ExecuteNonQueryAsync();
                    }

                    var sql = sourceId.HasValue
                        ? $@"
                            UPDATE site_prediction_optimized spo
                            SET {string.Join(", ", updates)},
                                spo.is_updated = 1,
                                spo.status = 'updated',
                                spo.version = COALESCE(spo.version, 0) + 1,
                                spo.updated_at = UTC_TIMESTAMP(),
                                spo.updated_by = @updatedBy_{lookupId}
                            WHERE spo.site_prediction_id = @sourceId_{lookupId}
                              AND spo.scenario = @scenario_{lookupId}
                              AND COALESCE(spo.tbl_project_id, 0) = @scenarioProjectId_{lookupId};"
                        : hasWholeSiteSelector
                            ? $@"
                            UPDATE site_prediction_optimized spo
                            LEFT JOIN site_prediction sp ON sp.id = spo.site_prediction_id
                            SET {string.Join(", ", updates)},
                                spo.is_updated = 1,
                                spo.status = 'updated',
                                spo.version = COALESCE(spo.version, 0) + 1,
                                spo.updated_at = UTC_TIMESTAMP(),
                                spo.updated_by = @updatedBy_{lookupId}
                            WHERE (
                                    CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSiteText_{lookupId}
                                    OR CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSiteText_{lookupId}
                                  )
                              AND spo.scenario = @scenario_{lookupId}
                              AND COALESCE(spo.tbl_project_id, sp.tbl_project_id) = @scenarioProjectId_{lookupId};"
                        : hasSiteSectorSelector
                            ? $@"
                            UPDATE site_prediction_optimized spo
                            LEFT JOIN site_prediction sp ON sp.id = spo.site_prediction_id
                            SET {string.Join(", ", updates)},
                                spo.is_updated = 1,
                                spo.status = 'updated',
                                spo.version = COALESCE(spo.version, 0) + 1,
                                spo.updated_at = UTC_TIMESTAMP(),
                                spo.updated_by = @updatedBy_{lookupId}
                            WHERE (
                                    (
                                        CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSiteText_{lookupId}
                                        AND CONVERT(spo.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSectorText_{lookupId}
                                    )
                                    OR (
                                        CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSiteText_{lookupId}
                                        AND CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @targetSectorText_{lookupId}
                                    )
                                  )
                              AND spo.scenario = @scenario_{lookupId}
                              AND COALESCE(spo.tbl_project_id, sp.tbl_project_id) = @scenarioProjectId_{lookupId};"
                        : $@"
                            UPDATE site_prediction_optimized spo
                            LEFT JOIN site_prediction sp ON sp.id = spo.site_prediction_id
                            SET {string.Join(", ", updates)},
                                spo.is_updated = 1,
                                spo.status = 'updated',
                                spo.version = COALESCE(spo.version, 0) + 1,
                                spo.updated_at = UTC_TIMESTAMP(),
                                spo.updated_by = @updatedBy_{lookupId}
                            WHERE (
                                    spo.site = @targetSite_{lookupId}
                                    OR sp.site = @targetSite_{lookupId}
                                  )
                              AND spo.scenario = @scenario_{lookupId}
                              AND COALESCE(spo.tbl_project_id, sp.tbl_project_id) = @scenarioProjectId_{lookupId};";

                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    Add(cmd, $"@updatedBy_{lookupId}", updatedBy);
                    Add(cmd, $"@scenario_{lookupId}", scenario);
                    Add(cmd, $"@scenarioProjectId_{lookupId}", scenarioProjectId.Value);
                    if (sourceId.HasValue) Add(cmd, $"@sourceId_{lookupId}", sourceId.Value);
                    else if (hasWholeSiteSelector)
                    {
                        Add(cmd, $"@targetSiteText_{lookupId}", explicitSiteSelectorText!);
                    }
                    else if (hasSiteSectorSelector)
                    {
                        Add(cmd, $"@targetSiteText_{lookupId}", explicitSiteSelectorText!);
                        Add(cmd, $"@targetSectorText_{lookupId}", explicitSectorSelectorText!);
                    }
                    else Add(cmd, $"@targetSite_{lookupId}", siteId!.Value);

                    foreach (var kvp in parameters)
                    {
                        Add(cmd, kvp.Key, kvp.Value ?? DBNull.Value);
                    }

                    int rows = await cmd.ExecuteNonQueryAsync();
                    totalUpdated += rows;
                    if (rows > 0) updatedIds.Add(lookupId);
                    else skippedIds.Add(lookupId);
                }

                await tx.CommitAsync();
                await InvalidateMapViewCachesAsync();

                return Ok(new
                {
                    Status = 1,
                    Message = $"Successfully updated {totalUpdated} prediction(s)",
                    SavedTable = "site_prediction_optimized",
                    BaseTable = "site_prediction",
                    BaseTableUpdated = false,
                    Scenario = scenario,
                    ScenarioName = $"Scenario {scenario}",
                    RowsAffected = totalUpdated,
                    Requested = requestedIds.Count,
                    UpdatedIds = updatedIds.Distinct().ToArray(),
                    SkippedIds = skippedIds.Distinct().ToArray()
                });
            }
            catch (Exception ex)
            {
                if (ex is InvalidOperationException &&
                    (SafeException.Get(ex).Contains("scenario", StringComparison.OrdinalIgnoreCase) ||
                     SafeException.Get(ex).Contains("project", StringComparison.OrdinalIgnoreCase)))
                {
                    return BadRequest(new
                    {
                        Status = 0,
                        Message = SafeException.Get(ex)
                    });
                }

                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "Error updating site prediction.",
                    FailedId = currentItemId > 0 ? (long?)currentItemId : null,
                    Details = SafeException.Get(ex)
                });
            }
        }

        public class DeleteSitePredictionRequest
        {
            public long ProjectId { get; set; }
            public long? SourceId { get; set; }
            public string? Site { get; set; }
            public string? Sector { get; set; }
            public bool DeleteEntireSite { get; set; }
            public bool OptimizedOnly { get; set; }
        }

        public class DeleteSitePredictionScenarioRequest
        {
            public long ProjectId { get; set; }
            public int Scenario { get; set; }
        }

public class DeleteLtePredictionOptimisedScenarioRequest
{
    public long ProjectId { get; set; }
    public long ScenarioId { get; set; }
}

[HttpGet, Route("GetSitePredictionScenarios")]
public async Task<IActionResult> GetSitePredictionScenarios([FromQuery] long projectId)
{
    if (projectId <= 0)
        return BadRequest(new { Status = 0, Message = "projectId is required." });

    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await EnsureSitePredictionOptimizedTableAsync(conn);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                scenario,
                MAX(COALESCE(updated_at, created_at)) AS latest_at,
                COUNT(*) AS row_count
            FROM site_prediction_optimized
            WHERE tbl_project_id = @pid
              AND scenario BETWEEN 1 AND 6
            GROUP BY scenario
            ORDER BY scenario;";
        Add(cmd, "@pid", projectId);

        var rows = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var scenario = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
            if (scenario <= 0) continue;

            var latestAt = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1));
            var rowCount = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));

            rows.Add(new
            {
                scenario_id = scenario,
                scenario_name = $"Scenario {scenario}",
                status = "updated",
                row_count = rowCount,
                updated_at = latestAt
            });
        }

        return Ok(new
        {
            Status = 1,
            Message = "Site prediction scenarios fetched.",
            Data = rows
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error fetching site prediction scenarios.",
            Details = SafeException.Get(ex)
        });
    }
}

        [HttpPost, Route("DeleteSitePredictionScenario")]
        public async Task<IActionResult> DeleteSitePredictionScenario([FromBody] DeleteSitePredictionScenarioRequest? model)
        {
            if (model == null)
                return BadRequest(new { Status = 0, Message = "Invalid payload." });

            if (model.ProjectId <= 0)
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });

            if (model.Scenario <= 0 || model.Scenario > 6)
            {
                return BadRequest(new
                {
                    Status = 0,
                    Message = "scenario must be between 1 and 6"
                });
            }

            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                    await conn.OpenAsync();

                await EnsureSitePredictionOptimizedTableAsync(conn);

                await using var tx = await conn.BeginTransactionAsync();
                await using var deleteCmd = conn.CreateCommand();
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = @"
                    DELETE FROM site_prediction_optimized
                    WHERE tbl_project_id = @pid
                      AND scenario = @scenario;";
                Add(deleteCmd, "@pid", model.ProjectId);
                Add(deleteCmd, "@scenario", model.Scenario);

                var deletedRows = await deleteCmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
                await InvalidateMapViewCachesAsync();

                return Ok(new
                {
                    Status = 1,
                    Message = deletedRows > 0
                        ? $"Scenario {model.Scenario} deleted successfully."
                        : $"No rows found for Scenario {model.Scenario}.",
                    ProjectId = model.ProjectId,
                    Scenario = model.Scenario,
                    ScenarioName = $"Scenario {model.Scenario}",
                    RowsAffected = deletedRows,
                    DeletedOptimizedRows = deletedRows
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "Error deleting site prediction scenario.",
                    Details = SafeException.Get(ex)
                });
            }
        }

        [HttpPost, Route("DeleteLtePredictionOptimisedScenario")]
        public async Task<IActionResult> DeleteLtePredictionOptimisedScenario([FromBody] DeleteLtePredictionOptimisedScenarioRequest? model)
        {
            if (model == null)
                return BadRequest(new { Status = 0, Message = "Invalid payload." });

            if (model.ProjectId <= 0)
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });

            if (model.ScenarioId <= 0)
            {
                return BadRequest(new
                {
                    Status = 0,
                    Message = "scenario_id must be greater than 0"
                });
            }

            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                    await conn.OpenAsync();

                var optimizedColumns = await GetTableColumnSetAsync(conn, "lte_prediction_optimised_results");
                var scenarioDeleteClause = optimizedColumns.Contains("public_scenario_id")
                    ? "(public_scenario_id = @scenarioId OR (public_scenario_id IS NULL AND scenario_id = @scenarioId))"
                    : "scenario_id = @scenarioId";

                await using var tx = await conn.BeginTransactionAsync();
                await using var deleteCmd = conn.CreateCommand();
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $@"
                    DELETE FROM lte_prediction_optimised_results
                    WHERE project_id = @pid
                      AND {scenarioDeleteClause};";
                Add(deleteCmd, "@pid", model.ProjectId);
                Add(deleteCmd, "@scenarioId", model.ScenarioId);

                var deletedRows = await deleteCmd.ExecuteNonQueryAsync();
                await using var deleteScenarioCmd = conn.CreateCommand();
                deleteScenarioCmd.Transaction = tx;
                deleteScenarioCmd.CommandText = @"
                    DELETE FROM lte_optimization_scenarios
                    WHERE project_id = @pid
                      AND scenario_id = @scenarioId;";
                Add(deleteScenarioCmd, "@pid", model.ProjectId);
                Add(deleteScenarioCmd, "@scenarioId", model.ScenarioId);
                var deletedScenarioRows = await deleteScenarioCmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
                await InvalidateMapViewCachesAsync();

                return Ok(new
                {
                    Status = 1,
                    Message = deletedRows > 0 || deletedScenarioRows > 0
                        ? $"LTE optimized Scenario {model.ScenarioId} deleted successfully."
                        : $"No LTE optimized rows found for Scenario {model.ScenarioId}.",
                    ProjectId = model.ProjectId,
                    ScenarioId = model.ScenarioId,
                    ScenarioName = $"Scenario {model.ScenarioId}",
                    RowsAffected = deletedRows,
                    DeletedOptimisedRows = deletedRows,
                    DeletedScenarioRows = deletedScenarioRows,
                    Table = "lte_prediction_optimised_results"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "Error deleting LTE optimized scenario.",
                    Details = SafeException.Get(ex)
                });
            }
        }

        [HttpPost, Route("DeleteSitePrediction")]
        public async Task<IActionResult> DeleteSitePrediction([FromBody] DeleteSitePredictionRequest? model)
        {
            if (model == null)
                return BadRequest(new { Status = 0, Message = "Invalid payload." });

            if (model.ProjectId <= 0)
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });

            var siteValue = (model.Site ?? string.Empty).Trim();
            var sectorValue = (model.Sector ?? string.Empty).Trim();
            var deleteBySourceId = model.SourceId.HasValue && model.SourceId.Value > 0;
            var deleteEntireSite = model.DeleteEntireSite;
            var optimizedOnly = model.OptimizedOnly;

            if (!deleteBySourceId && string.IsNullOrWhiteSpace(siteValue))
                return BadRequest(new { Status = 0, Message = "Either SourceId or Site is required." });

            if (!deleteBySourceId && !deleteEntireSite && string.IsNullOrWhiteSpace(sectorValue))
                return BadRequest(new { Status = 0, Message = "Sector is required when deleting a single sector." });

            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                    await conn.OpenAsync();

                await EnsureSitePredictionOptimizedTableAsync(conn);

                if (optimizedOnly)
                {
                    await using var txOptimizedOnly = await conn.BeginTransactionAsync();
                    await using var deleteOptimizedOnlyCmd = conn.CreateCommand();
                    deleteOptimizedOnlyCmd.Transaction = txOptimizedOnly;

                    var optimizedWhereParts = new List<string> { "spo.tbl_project_id = @pid" };
                    if (deleteBySourceId)
                    {
                        optimizedWhereParts.Add("spo.site_prediction_id = @sourceId");
                    }
                    else
                    {
                        optimizedWhereParts.Add(
                            "CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @site");
                        if (!deleteEntireSite)
                        {
                            optimizedWhereParts.Add(
                                "CONVERT(spo.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @sector");
                        }
                    }

                    deleteOptimizedOnlyCmd.CommandText = $@"
                        DELETE FROM site_prediction_optimized spo
                        WHERE {string.Join(" AND ", optimizedWhereParts)};";

                    Add(deleteOptimizedOnlyCmd, "@pid", model.ProjectId);
                    if (deleteBySourceId) Add(deleteOptimizedOnlyCmd, "@sourceId", model.SourceId!.Value);
                    if (!deleteBySourceId) Add(deleteOptimizedOnlyCmd, "@site", siteValue);
                    if (!deleteBySourceId && !deleteEntireSite) Add(deleteOptimizedOnlyCmd, "@sector", sectorValue);

                    var deletedOptimizedOnlyRows = await deleteOptimizedOnlyCmd.ExecuteNonQueryAsync();
                    await txOptimizedOnly.CommitAsync();
                    await InvalidateMapViewCachesAsync();

                    return Ok(new
                    {
                        Status = 1,
                        Message = deletedOptimizedOnlyRows > 0
                            ? "Optimized rows deleted successfully."
                            : "No optimized rows matched the request.",
                        RowsAffected = deletedOptimizedOnlyRows,
                        DeletedSourceRows = 0,
                        DeletedOptimizedRows = deletedOptimizedOnlyRows,
                        OptimizedOnly = true
                    });
                }

                await using var tx = await conn.BeginTransactionAsync();

                var sourceLookupParts = new List<string>();
                if (deleteBySourceId)
                {
                    sourceLookupParts.Add("sp.id = @sourceId");
                }

                if (!string.IsNullOrWhiteSpace(siteValue))
                {
                    var siteLookupParts = new List<string>
                    {
                        "CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @site"
                    };
                    if (!deleteEntireSite)
                    {
                        siteLookupParts.Add("CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @sector");
                    }
                    sourceLookupParts.Add($"({string.Join(" AND ", siteLookupParts)})");
                }

                var selectSql = $@"
                    SELECT sp.id
                    FROM site_prediction sp
                    WHERE sp.tbl_project_id = @pid
                      AND ({string.Join(" OR ", sourceLookupParts)});";

                var sourceIds = new List<long>();
                await using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = selectSql;
                    Add(selectCmd, "@pid", model.ProjectId);
                    if (deleteBySourceId) Add(selectCmd, "@sourceId", model.SourceId!.Value);
                    if (!string.IsNullOrWhiteSpace(siteValue)) Add(selectCmd, "@site", siteValue);
                    if (!string.IsNullOrWhiteSpace(siteValue) && !deleteEntireSite) Add(selectCmd, "@sector", sectorValue);

                    await using var reader = await selectCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (!await reader.IsDBNullAsync(0))
                        {
                            sourceIds.Add(Convert.ToInt64(reader.GetValue(0)));
                        }
                    }
                }

                var sourceIdParamNames = sourceIds.Select((_, idx) => $"@id{idx}").ToList();
                var sourceIdInClause = string.Join(", ", sourceIdParamNames);

                int deletedSourceRows = 0;
                if (sourceIds.Count > 0)
                {
                    await using var deleteSourceCmd = conn.CreateCommand();
                    {
                        deleteSourceCmd.Transaction = tx;
                        deleteSourceCmd.CommandText = $@"
                            DELETE FROM site_prediction
                            WHERE id IN ({sourceIdInClause});";

                        for (int i = 0; i < sourceIds.Count; i += 1)
                        {
                            Add(deleteSourceCmd, sourceIdParamNames[i], sourceIds[i]);
                        }

                        deletedSourceRows = await deleteSourceCmd.ExecuteNonQueryAsync();
                    }
                }

                int deletedOptimizedRows = 0;
                var optimizedWhere = new List<string>();
                if (sourceIds.Count > 0)
                {
                    optimizedWhere.Add($"spo.site_prediction_id IN ({sourceIdInClause})");
                }
                if (!string.IsNullOrWhiteSpace(siteValue))
                {
                    var optimizedSiteParts = new List<string>
                    {
                        "spo.tbl_project_id = @pid",
                        "CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @site"
                    };
                    if (!deleteEntireSite)
                    {
                        optimizedSiteParts.Add("CONVERT(spo.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @sector");
                    }
                    optimizedWhere.Add($"({string.Join(" AND ", optimizedSiteParts)})");
                }

                if (optimizedWhere.Count > 0)
                {
                    await using var deleteOptimizedCmd = conn.CreateCommand();
                    deleteOptimizedCmd.Transaction = tx;
                    deleteOptimizedCmd.CommandText = $@"
                        DELETE FROM site_prediction_optimized spo
                        WHERE {string.Join(" OR ", optimizedWhere)};";

                    for (int i = 0; i < sourceIds.Count; i += 1)
                    {
                        Add(deleteOptimizedCmd, sourceIdParamNames[i], sourceIds[i]);
                    }
                    if (!string.IsNullOrWhiteSpace(siteValue))
                    {
                        Add(deleteOptimizedCmd, "@pid", model.ProjectId);
                        Add(deleteOptimizedCmd, "@site", siteValue);
                        if (!deleteEntireSite)
                        {
                            Add(deleteOptimizedCmd, "@sector", sectorValue);
                        }
                    }

                    deletedOptimizedRows = await deleteOptimizedCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                await InvalidateMapViewCachesAsync();

                return Ok(new
                {
                    Status = 1,
                    Message = deletedSourceRows > 0 || deletedOptimizedRows > 0
                        ? "Deleted successfully."
                        : "No matching rows found.",
                    RowsAffected = deletedSourceRows + deletedOptimizedRows,
                    DeletedSourceRows = deletedSourceRows,
                    DeletedOptimizedRows = deletedOptimizedRows,
                    DeletedSourceIds = sourceIds
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "Error deleting site prediction rows.",
                    Details = SafeException.Get(ex)
                });
            }
        }


[HttpPost, Route("createProject")]
public async Task<IActionResult> CreateSimpleProject([FromBody] CreateProjectModel model)
{
    var response = new ReturnAPIResponse();

    // 1. Validation: Project Name is the absolute minimum requirement
    if (model == null || string.IsNullOrWhiteSpace(model.ProjectName))
    {
        return BadRequest(new { Status = 0, Message = "Project Name is required." });
    }

    try
    {
        // 2. Security: Resolve the Company ID from the authenticated user context
        var companyResolution = await ResolveProjectCompanyIdAsync(model);
        if (!companyResolution.CompanyId.HasValue)
        {
            return BadRequest(new { Status = 0, Message = companyResolution.Error });
        }

        // 3. Initialize the Project Entity
        var newProject = new tbl_project
        {
            project_name = model.ProjectName,
            created_on = DateTime.UtcNow,
            status = 1,
            company_id = companyResolution.CompanyId.Value,
            
            // Store the Session IDs as a comma-separated string
            ref_session_id = (model.SessionIds != null && model.SessionIds.Any())
                ? string.Join(",", model.SessionIds)
                : null,

            // Optional: Map other fields if they happen to be provided, else they remain null
            provider = model.Provider,
            tech = model.Tech,
            band = model.Band,
            earfcn = model.EarFcn,
            apps = model.Apps,
            from_date = model.FromDate?.ToString("yyyy-MM-dd"),
            to_date = model.ToDate?.ToString("yyyy-MM-dd"),
            grid_size = model.GridSize
        };

        // 4. Save to Database
        db.tbl_project.Add(newProject);
        await db.SaveChangesAsync();
        await TryUpdateProjectLogGridAsync(newProject.id, model.LogGrid ?? model.log_grid);
        await TryUpdateProjectSiteSizeAsync(newProject.id, 1m);
        await InvalidateProjectListCachesAsync();

        response.Status = 1;
        response.Message = "Project created successfully with associated sessions.";
        response.Data = new { projectId = newProject.id };

        return Ok(response);
    }
    catch (Exception ex)
    {
        string details = SafeException.Get(ex?.InnerException) ?? SafeException.Get(ex);
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Internal Server Error while creating project.",
            Details = details
        });
    }





    
}













// get the site  from the ml part 

        [HttpGet, Route("GetSiteNoMl")]
public async Task<IActionResult> GetSiteNoMl(
    [FromQuery] long projectId,
    [FromQuery] string? network = null,
    [FromQuery] int? site_key_inferred = null,
    [FromQuery] double? pci_or_psi = null,
    [FromQuery] double? earfcn_or_narfcn = null,
    [FromQuery] int limit = 200,
    [FromQuery] int offset = 0)
{
    if (projectId <= 0)
        return BadRequest(new { Status = 0, Message = "projectId is required" });

    var cacheKey = BuildMapViewCacheKey(
        "site-no-ml",
        projectId,
        network ?? "all",
        site_key_inferred,
        pci_or_psi,
        earfcn_or_narfcn);
    var cachedRows = await TryGetMapViewCacheAsync<List<Dictionary<string, object?>>>(cacheKey);
    if (cachedRows != null)
    {
        var cachedPageRows = SliceCachedPage(cachedRows, Math.Clamp(limit, 1, 20000), Math.Max(offset, 0));
        return Json(new
        {
            Status = 1,
            Count = cachedPageRows.Count,
            Data = cachedPageRows
        });
    }

	    var sql = @"
	    SELECT
	        s.*
	    FROM site_noMl s
	    WHERE s.project_id = @pid
	    /**n**/ /**s**/ /**p**/ /**e**/
	    ORDER BY s.id DESC;
	";


    sql = sql.Replace("/**n**/", string.IsNullOrWhiteSpace(network) ? "" : "AND s.network = @n");
    sql = sql.Replace("/**s**/", site_key_inferred.HasValue ? "AND s.site_key_inferred = @s" : "");
    sql = sql.Replace("/**p**/", pci_or_psi.HasValue ? "AND s.pci_or_psi = @p" : "");
    sql = sql.Replace("/**e**/", earfcn_or_narfcn.HasValue ? "AND s.earfcn_or_narfcn = @e" : "");

    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    Add(cmd, "@pid", projectId);
    if (!string.IsNullOrWhiteSpace(network)) Add(cmd, "@n", network);
    if (site_key_inferred.HasValue) Add(cmd, "@s", site_key_inferred.Value);
    if (pci_or_psi.HasValue) Add(cmd, "@p", pci_or_psi.Value);
    if (earfcn_or_narfcn.HasValue) Add(cmd, "@e", earfcn_or_narfcn.Value);

    var list = new List<Dictionary<string, object?>>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.FieldCount; i++)
            row[r.GetName(i)] = await r.IsDBNullAsync(i) ? null : r.GetValue(i);
        list.Add(row);
    }

    var pagedRows = SliceCachedPage(list, Math.Clamp(limit, 1, 20000), Math.Max(offset, 0));
    var response = new
    {
        Status = 1,
        Count = pagedRows.Count,
        Data = pagedRows
    };
    await SetMapViewCacheAsync(cacheKey, list);
    return Json(response);
}
[HttpGet, Route("GetSiteMl")]
public async Task<IActionResult> GetSiteMl(
    [FromQuery] long projectId,
    [FromQuery] string? network = null,
    [FromQuery] int? site_key_inferred = null,
    [FromQuery] double? pci_or_psi = null,
    [FromQuery] double? earfcn_or_narfcn = null,
    [FromQuery] int limit = 200,
    [FromQuery] int offset = 0)
{
    if (projectId <= 0)
        return BadRequest(new { Status = 0, Message = "projectId is required" });

    var cacheKey = BuildMapViewCacheKey(
        "site-ml",
        projectId,
        network ?? "all",
        site_key_inferred,
        pci_or_psi,
        earfcn_or_narfcn);
    var cachedRows = await TryGetMapViewCacheAsync<List<Dictionary<string, object?>>>(cacheKey);
    if (cachedRows != null)
    {
        var cachedPageRows = SliceCachedPage(cachedRows, Math.Clamp(limit, 1, 20000), Math.Max(offset, 0));
        return Json(new
        {
            Status = 1,
            Count = cachedPageRows.Count,
            Data = cachedPageRows
        });
    }

    var sql = @"
    SELECT
        s.id, s.network, s.earfcn_or_narfcn,
        s.site_key_inferred, s.pci_or_psi, s.samples,
        s.lat_pred, s.lon_pred,
        s.azimuth_deg_5, s.azimuth_deg_5_soft, s.azimuth_deg_label_soft,
        s.azimuth_adjustment_deg, s.template_spacing_deg,
        s.beamwidth_deg_est, 
        -- s.median_sample_distance_m,  <--- REMOVED THIS COLUMN TO FIX ERROR
        s.cell_id_representative, s.sector_count,
        s.azimuth_reliability, s.spacing_used
    FROM site_ml s  -- Ensure this matches your actual table name (lowercase usually)
    JOIN map_regions mr
      ON mr.tbl_project_id = @pid
     AND ST_Contains(
           mr.region,
           CASE
               WHEN s.lat_pred BETWEEN -90 AND 90
                AND s.lon_pred BETWEEN -180 AND 180
               THEN ST_PointFromText(
                       CONCAT('POINT(', s.lat_pred, ' ', s.lon_pred, ')'), -- Lat first
                       4326
                   )
               ELSE NULL
           END
         )
    WHERE 1=1
      /**n**/ /**s**/ /**p**/ /**e**/
    ORDER BY s.id DESC;
";

    // ... (rest of the function remains the same)
    sql = sql.Replace("/**n**/", string.IsNullOrWhiteSpace(network) ? "" : "AND s.network = @n");
    sql = sql.Replace("/**s**/", site_key_inferred.HasValue ? "AND s.site_key_inferred = @s" : "");
    sql = sql.Replace("/**p**/", pci_or_psi.HasValue ? "AND s.pci_or_psi = @p" : "");
    sql = sql.Replace("/**e**/", earfcn_or_narfcn.HasValue ? "AND s.earfcn_or_narfcn = @e" : "");

    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    Add(cmd, "@pid", projectId);
    if (!string.IsNullOrWhiteSpace(network)) Add(cmd, "@n", network);
    if (site_key_inferred.HasValue) Add(cmd, "@s", site_key_inferred.Value);
    if (pci_or_psi.HasValue) Add(cmd, "@p", pci_or_psi.Value);
    if (earfcn_or_narfcn.HasValue) Add(cmd, "@e", earfcn_or_narfcn.Value);

    var list = new List<Dictionary<string, object?>>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.FieldCount; i++)
            row[r.GetName(i)] = await r.IsDBNullAsync(i) ? null : r.GetValue(i);
        list.Add(row);
    }

    var pagedRows = SliceCachedPage(list, Math.Clamp(limit, 1, 20000), Math.Max(offset, 0));
    var response = new
    {
        Status = 1,
        Count = pagedRows.Count,
        Data = pagedRows
    };
    await SetMapViewCacheAsync(cacheKey, list);
    return Json(response);
}

[HttpPost, Route("AddSitePrediction")]
public async Task<IActionResult> AddSitePrediction([FromBody] AddSitePredictionModel model)
{
    if (model == null) return BadRequest("Invalid payload.");
    if (model.ProjectId <= 0) return BadRequest("ProjectId required.");
    if (string.IsNullOrWhiteSpace(model.Site)) return BadRequest("Site is required.");
    if (double.IsNaN(model.Latitude) || double.IsInfinity(model.Latitude) || model.Latitude < -90 || model.Latitude > 90)
        return BadRequest("Latitude must be between -90 and 90.");
    if (double.IsNaN(model.Longitude) || double.IsInfinity(model.Longitude) || model.Longitude < -180 || model.Longitude > 180)
        return BadRequest("Longitude must be between -180 and 180.");
    if (model.Bands == null || !model.Bands.Any(b => !string.IsNullOrWhiteSpace(b)))
        return BadRequest("At least one band is required.");

    // Validate that all arrays have the same length based on Sectors
    int sectorCount = model.Sectors?.Count ?? 0;
    
    if (sectorCount == 0)
        return BadRequest("At least one sector is required.");

    if (model.Azimuths?.Count != sectorCount)
        return BadRequest($"Azimuths count ({model.Azimuths?.Count}) must match sectors count ({sectorCount}).");

    var cellIds = model.CellIds != null && model.CellIds.Count > 0
        ? model.CellIds
        : model.CellIdsSnake;
    if (cellIds != null && cellIds.Count > 0 && cellIds.Count != sectorCount)
        return BadRequest($"cellIds count ({cellIds.Count}) must match sectors count ({sectorCount}).");

    if (model.Technologies == null || !model.Technologies.Any())
        return BadRequest("At least one Technology required.");

    var cleanedBands = (model.Bands ?? new List<string>())
        .Where(raw => !string.IsNullOrWhiteSpace(raw))
        .Select(raw => raw.Trim())
        .Select(raw =>
        {
            if (raw.StartsWith("B", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("N", StringComparison.OrdinalIgnoreCase))
            {
                return raw.Substring(1).Trim();
            }
            return raw;
        })
        .Where(raw => !string.IsNullOrWhiteSpace(raw))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (cleanedBands.Count == 0)
        return BadRequest("At least one valid band is required.");

    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var nodeId = string.IsNullOrWhiteSpace(model.NodeId)
            ? string.IsNullOrWhiteSpace(model.NodeIdSnake)
                ? model.NodeBId
                : model.NodeIdSnake
            : model.NodeId;
        nodeId = string.IsNullOrWhiteSpace(nodeId) ? null : nodeId.Trim();
        var providerValue = ResolveSitePredictionProviderInput(
            model.Provider,
            model.OperatorName,
            model.OperatorNameSnake,
            model.Operator,
            model.Cluster);
        var nodeColumnInfo = nodeId == null
            ? null
            : await GetFirstExistingColumnInfoAsync(conn, "site_prediction", "node_id", "nodeb_id", "node_b_id");

        object? nodeInsertParamValue = null;
        string? nodeColumn = null;
        if (nodeColumnInfo != null)
        {
            if (IsTextColumnType(nodeColumnInfo.DataType))
            {
                nodeColumn = nodeColumnInfo.Name;
                nodeInsertParamValue = nodeId;
            }
            else if (IsNumericColumnType(nodeColumnInfo.DataType))
            {
                if (!TryParseNodeIdForNumericColumn(nodeId, out var parsedNodeId))
                {
                    return BadRequest(
                        $"Node B ID '{nodeId}' must be numeric because site_prediction.{nodeColumnInfo.Name} is stored as {nodeColumnInfo.ColumnType}.");
                }

                nodeColumn = nodeColumnInfo.Name;
                nodeInsertParamValue = parsedNodeId;
            }
        }

        var nodeInsertColumn = string.IsNullOrWhiteSpace(nodeColumn) ? string.Empty : $", `{nodeColumn}`";
        var nodeInsertValue = string.IsNullOrWhiteSpace(nodeColumn) ? string.Empty : ", @nodeId";

        // 1. ADDED missing columns: cluster, earfcn
        string sql = $@"
        INSERT INTO site_prediction (
            tbl_project_id, site, cluster, sector, cell_id,
            latitude, longitude, pci, azimuth, band, earfcn,
            Technology, height, m_tilt, e_tilt{nodeInsertColumn}
        ) VALUES (
            @pid, @site, @cluster, @sec, @cid, @lat, @lon,
            @pci, @azi, @band, @earfcn, @tech, @h, @mt, @et{nodeInsertValue}
        );";

        foreach (var tech in model.Technologies)
        {
            if (string.IsNullOrWhiteSpace(tech.Technology))
                return BadRequest("Technology type is required.");

            // Validate that the nested idValues array matches the sectors length
            if (tech.IdValues == null || tech.IdValues.Count != sectorCount)
                return BadRequest($"idValues count ({tech.IdValues?.Count}) must match sector count ({sectorCount}) for {tech.Technology}");

            foreach (var cleanBand in cleanedBands)
            {
                for (int i = 0; i < sectorCount; i++)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;

                    // 3. Correctly extract the current index [i] from arrays
                    string sector = model.Sectors[i].ToString(CultureInfo.InvariantCulture);
                    int azimuth = model.Azimuths[i];
                    int pciValue = tech.IdValues[i];
                    int cellIdValue = cellIds != null && cellIds.Count > i ? cellIds[i] : pciValue;
                    
                    double? height = model.Heights != null && model.Heights.Count > i ? model.Heights[i] : (double?)null;
                    double? mTilt = model.MechanicalTilts != null && model.MechanicalTilts.Count > i ? model.MechanicalTilts[i] : (double?)null;
                    double? eTilt = model.ElectricalTilts != null && model.ElectricalTilts.Count > i ? model.ElectricalTilts[i] : (double?)null;

                    AddParam(cmd, "@pid", model.ProjectId);
                    AddParam(cmd, "@site", model.Site);
                    AddParam(cmd, "@cluster", string.IsNullOrWhiteSpace(providerValue) ? DBNull.Value : providerValue);
                    AddParam(cmd, "@sec", sector);
                    AddParam(cmd, "@cid", cellIdValue);
                    AddParam(cmd, "@lat", model.Latitude);
                    AddParam(cmd, "@lon", model.Longitude);
                    AddParam(cmd, "@pci", tech.Technology == "4G" ? pciValue : DBNull.Value);
                    AddParam(cmd, "@azi", azimuth);
                    AddParam(cmd, "@band", cleanBand);
                    AddParam(cmd, "@earfcn", string.IsNullOrWhiteSpace(tech.Earfcn) ? DBNull.Value : tech.Earfcn);
                    AddParam(cmd, "@tech", tech.Technology);
                    
                    // Assign extracted array variables
                    AddParam(cmd, "@h", height ?? (object)DBNull.Value);
                    AddParam(cmd, "@mt", mTilt ?? (object)DBNull.Value);
                    AddParam(cmd, "@et", eTilt ?? (object)DBNull.Value);
                    if (!string.IsNullOrWhiteSpace(nodeColumn) && nodeInsertParamValue != null)
                    {
                        AddParam(cmd, "@nodeId", nodeInsertParamValue);
                    }

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        await InvalidateMapViewCachesAsync();
        return Ok(new { Status = 1, Message = "Inserted successfully." });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = SafeException.Get(ex) });
    }
}
    // =========================================================
        // =========== Saved polygons list (tbl_savepolygon) =======
        // =========================================================

        [HttpGet, Route("ListSavedPolygons")]
        public async Task<IActionResult> ListSavedPolygons(
            [FromQuery] long projectId,
            [FromQuery] int limit = 200,
            [FromQuery] int offset = 0)
        {
            if (projectId <= 0)
                return BadRequest(new { Status = 0, Message = "projectId is required" });

            var cacheKey = BuildMapViewCacheKey("saved-polygons", projectId);
            var cachedRows = await TryGetMapViewCacheAsync<List<Dictionary<string, object?>>>(cacheKey);
            if (cachedRows != null)
            {
                var cachedPageRows = SliceCachedPage(cachedRows, Math.Clamp(limit, 1, 50000), Math.Max(offset, 0));
                return Json(new
                {
                    Status = 1,
                    Data = cachedPageRows
                });
            }

            var rows = new List<Dictionary<string, object?>>();
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    id,
                    name,
                    ST_AsText(region) AS wkt,
                    project_id,
                    area
                FROM tbl_savepolygon
                WHERE project_id = @pid
                ORDER BY id DESC;";

            Add(cmd, "@pid", projectId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = r.GetFieldValue<long>(0),
                    ["name"] = r.IsDBNull(1) ? null : r.GetString(1),
                    ["wkt"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["project_id"] = r.IsDBNull(3) ? (long?)null : r.GetFieldValue<long>(3),
                    ["area"] = r.IsDBNull(4) ? (double?)null : Convert.ToDouble(r.GetDecimal(4))
                });
            }

            var pagedRows = SliceCachedPage(rows, Math.Clamp(limit, 1, 50000), Math.Max(offset, 0));
            var response = new
            {
                Status = 1,
                Data = pagedRows
            };
            await SetMapViewCacheAsync(cacheKey, rows);
            return Json(response);
        }

        [HttpPost, Route("AssignExistingSitePredictionToProject")]
        public async Task<IActionResult> AssignExistingSitePredictionToProject(
            [FromQuery] long projectId,
            [FromQuery] int[] siteIds)
        {
            if (projectId <= 0 || siteIds == null || siteIds.Length == 0)
                return BadRequest(new { Status = 0, Message = "projectId and siteIds[] required" });

            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var tx = await db.Database.BeginTransactionAsync();

            int affected = 0;
            foreach (var id in siteIds)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE site_prediction
                    SET tbl_project_id = @pid
                    WHERE id = @id;";
                Add(cmd, "@pid", projectId);
                Add(cmd, "@id", id);

                affected += await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            await InvalidateMapViewCachesAsync();

            return Ok(new
            {
                Status = 1,
                Updated = affected
            });
        }

        [HttpGet, Route("GetProjectPolygonsV2")]
        public async Task<IActionResult> GetProjectPolygonsV2([FromQuery] long projectId, [FromQuery] string? source)
        {
            if (projectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "projectId is required" });
            }

            var src = (source ?? "map").Trim().ToLowerInvariant();
            if (src != "map" && src != "save")
            {
                return BadRequest(new { Status = 0, Message = "source must be 'map' or 'save'" });
            }

            try
            {
                var cacheKey = BuildMapViewCacheKey("project-polygons-v2", projectId, src);
                var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
                if (cached != null)
                    return Ok(cached);

                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync();

                var savedPolyList = new List<object>();
                var mapRegionList = new List<object>();

                if (src == "save")
                {
                    await using (var cmd1 = conn.CreateCommand())
                    {
                        cmd1.CommandText = @"
                            SELECT 
                                id,
                                name,
                                ST_AsText(region) AS wkt,
                                project_id,
                                area
                            FROM tbl_savepolygon
                            WHERE project_id = @pid
                            ORDER BY id DESC;";

                        Add(cmd1, "@pid", projectId);

                        await using var r1 = await cmd1.ExecuteReaderAsync();
                        while (await r1.ReadAsync())
                        {
                            var idVal = r1.GetFieldValue<long>(0);
                            var nameVal = r1.IsDBNull(1) ? null : r1.GetString(1);
                            var wktVal = r1.IsDBNull(2) ? null : r1.GetString(2);

                            long projIdVal = r1.IsDBNull(3) ? projectId : r1.GetFieldValue<long>(3);
                            var areaVal = r1.IsDBNull(4) ? (double?)null : Convert.ToDouble(r1.GetValue(4));

                            savedPolyList.Add(new
                            {
                                Id = idVal,
                                Name = nameVal,
                                Source = "tbl_savepolygon",
                                ProjectId = projIdVal,
                                Wkt = wktVal,
                                Area = areaVal
                            });
                        }
                    }
                }

                if (src == "map")
                {
                    await using (var cmd2 = conn.CreateCommand())
                    {
                        cmd2.CommandText = @"
                            SELECT 
                                id,
                                name,
                                ST_AsText(region) AS wkt,
                                tbl_project_id,
                                area
                            FROM map_regions
                            WHERE status = 1
                              AND tbl_project_id = @pid
                            ORDER BY id DESC;";

                        Add(cmd2, "@pid", projectId);

                        await using var r2 = await cmd2.ExecuteReaderAsync();
                        while (await r2.ReadAsync())
                        {
                            long idVal = Convert.ToInt64(r2.GetInt32(0));
                            var nameVal = r2.IsDBNull(1) ? null : r2.GetString(1);
                            var wktVal = r2.IsDBNull(2) ? null : r2.GetString(2);
                            long projIdVal = r2.IsDBNull(3) ? projectId : Convert.ToInt64(r2.GetInt32(3));
                            var areaVal = r2.IsDBNull(4) ? (double?)null : Convert.ToDouble(r2.GetValue(4));

                            mapRegionList.Add(new
                            {
                                Id = idVal,
                                Name = nameVal,
                                Source = "map_regions",
                                ProjectId = projIdVal,
                                Wkt = wktVal,
                                Area = areaVal
                            });
                        }
                    }
                }

                List<object> finalList = (src == "save") ? savedPolyList : mapRegionList;

                var response = new
                {
                    Status = 1,
                    ProjectId = projectId,
                    SourceRequested = src,
                    CountFromSavePolygon = savedPolyList.Count,
                    CountFromMapRegions = mapRegionList.Count,
                    Data = finalList
                };
                await SetMapViewCacheAsync(cacheKey, response);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Status = 0,
                    Message = "Error fetching polygons.",
                    Details = SafeException.Get(ex)
                });
            }
        }
       
[HttpGet, Route("GetNeighboursForPrimary")]
public async Task<IActionResult> GetNeighboursForPrimary(
    [FromQuery] long sessionId,
    [FromQuery] string? dls = null,
    [FromQuery] string? uls = null)
{
    if (sessionId <= 0)
        return BadRequest(new { Status = 0, Message = "sessionId is required" });

    try
    {
        var cacheKey = BuildMapViewCacheKey("neighbours-for-primary", sessionId, dls ?? "all", uls ?? "all");
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT *
            FROM tbl_network_log
            WHERE session_id = @sid
              AND (@dls IS NULL OR TRIM(@dls) = '' OR dls = @dls)
              AND (@uls IS NULL OR TRIM(@uls) = '' OR uls = @uls)
            ORDER BY `timestamp` ASC;";

        AddParam(cmd, "@sid", sessionId);
        AddParam(cmd, "@dls", string.IsNullOrWhiteSpace(dls) ? DBNull.Value : dls);
        AddParam(cmd, "@uls", string.IsNullOrWhiteSpace(uls) ? DBNull.Value : uls);

        var rows = new List<Dictionary<string, object?>>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = await r.IsDBNullAsync(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            var empty = new { Status = 1, sessionId, primaries = Array.Empty<object>() };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        // --------------------------------------------------------------------
        // Helper functions
        // --------------------------------------------------------------------

        static string GS(Dictionary<string, object?> d, string k)
            => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v) ?? "" : "";

        static object? G(Dictionary<string, object?> d, string k)
            => d.TryGetValue(k, out var v) ? v : null;

        static string ExtractValue(string input, string key)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var match = Regex.Match(input, key + @"\s*=\s*([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        static bool IsPrimary(Dictionary<string, object?> d)
        {
            var p = GS(d, "primary_cell_info_1");
            return !string.IsNullOrWhiteSpace(p)
                   && p.Contains("mRegistered=YES", StringComparison.OrdinalIgnoreCase);
        }

        // Group rows by timestamp
        var groups = rows
            .GroupBy(row => GS(row, "timestamp"))
            .Where(g => g.Any(IsPrimary))
            .OrderBy(g => g.Key)
            .ToList();

        var primaries = new List<object>();
        var allPrimaryList = new List<(string primaryCellId, string pci, string id, string lat, string lon)>();

        foreach (var g in groups)
        {
            var primary = g.First(IsPrimary);
            var pInfo = GS(primary, "primary_cell_info_1");

            var nrId = ExtractValue(pInfo, "mNci");
            var lteId = ExtractValue(pInfo, "mCi");

            string primaryCellId =
                !string.IsNullOrWhiteSpace(nrId) ? nrId :
                !string.IsNullOrWhiteSpace(lteId) ? lteId :
                GS(primary, "cell_id");

            string primaryPci = ExtractValue(pInfo, "mPci");
            if (string.IsNullOrWhiteSpace(primaryPci))
                primaryPci = GS(primary, "pci");

            var lat = GS(primary, "lat");
            var lon = GS(primary, "lon");

            allPrimaryList.Add((primaryCellId, primaryPci, GS(primary, "id"), lat, lon));

            var neighbours = g.Where(x => !IsPrimary(x)).Select(n =>
            {
                var txt = GS(n, "neighbour_cell_info_1");
                var nr = ExtractValue(txt, "mNci");
                var lte = ExtractValue(txt, "mCi");

                string cellId =
                    !string.IsNullOrWhiteSpace(nr) ? nr :
                    !string.IsNullOrWhiteSpace(lte) ? lte :
                    primaryCellId;

                return new
                {
                    id = GS(n, "id"),
                    cell_id = cellId,
                    pci = GS(n, "pci"),
                    rsrq = G(n, "rsrq"),
                    rsrp = G(n, "rsrp"),
                    sinr = G(n, "sinr"),
                    mos = G(n, "mos"),
                    jitter = G(n, "jitter"),
                    dl_tpt = G(n, "dl_tpt"),
                    ul_tpt = G(n, "ul_tpt"),
                    lat = G(n, "lat"),
                    lon = G(n, "lon"),
                    band = G(n, "band"),
                    latency = G(n, "latency")
                };
            }).ToList();

            primaries.Add(new
            {
                primary_id = GS(primary, "id"),
                primary_cell_id = primaryCellId,
                primary_pci = primaryPci,
                neighbours_data = neighbours
            });
        }

        // --------------------------------------------------------------------
        // PCI COLLISION BASED ON PRIMARY + SAME LAT/LON CONDITION
        // --------------------------------------------------------------------

        var primaryPciCollision =
            allPrimaryList
            .GroupBy(x => x.pci)
            .Where(g => g.Select(z => z.primaryCellId).Distinct().Count() > 1) // PCI repeated on different cell IDs
            .Select(g =>
            {
                // Group by lat+lon (same location)
                var latLonGroups = g
                    .GroupBy(z => new { z.lat, z.lon })
                    .Where(gl => gl.Count() > 1) // same lat+lon
                    .ToList();

                if (!latLonGroups.Any())
                    return null; // no lat/lon match â†’ no collision

                return new
                {
                    pci = g.Key,
                    locations = latLonGroups.Select(gl => new
                    {
                        lat = gl.Key.lat,
                        lon = gl.Key.lon,
                        cells = gl.Select(c => new
                        {
                            cell_id = c.primaryCellId,
                            id = c.id
                        }).ToList()
                    }).ToList()
                };
            })
            .Where(x => x != null)
            .ToList();

        var response = new
        {
            Status = 1,
            sessionId,
            pci_collision_primary = primaryPciCollision,
            primaries
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = SafeException.Get(ex)
        });
    }
}



[HttpGet, Route("GetProjects")]
public async Task<IActionResult> GetProjects([FromQuery] int? company_id = null)
{
    var message = new ReturnAPIResponse();
    int targetCompanyId = 0;
    bool isSuperAdmin = false;
    int currentUserId = 0;
    bool useUserScope = false;

    try
    {
        cf.SessionCheck();

        // 1. Resolve company securely (same pattern)
        targetCompanyId = GetTargetCompanyId(company_id);
        isSuperAdmin = _userScope.IsSuperAdmin(User);
        currentUserId = GetCurrentUserId();
        useUserScope = !isSuperAdmin && targetCompanyId == 0 && currentUserId > 0;

        // Super admin check
        if (targetCompanyId == 0 && !isSuperAdmin && !useUserScope)
        {
            return Unauthorized(new
            {
                Status = 0,
                Message = "Unauthorized. Invalid Company."
            });
        }

        var cacheKey = BuildMapViewCacheKey("projects", targetCompanyId, useUserScope ? currentUserId : 0);
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Json(cached);

        // 2. Query with company filter + joined region geometry.
        List<object> projects;
        try
        {
            projects = await GetProjectsFromRawSqlAsync(
                targetCompanyId,
                isSuperAdmin,
                useUserScope,
                currentUserId);
        }
        catch (Exception ex)
        {
            // Defensive fallback for schema/driver/cache inconsistencies across DBs (main/TW).
            Console.WriteLine($"[GetProjects] Raw SQL path failed, using fallback. Error: {ex}");
            projects = await GetProjectsFallbackAsync(targetCompanyId, isSuperAdmin, useUserScope, currentUserId);
        }

        message.Status = 1;
        message.Data = projects;
        await SetMapViewCacheAsync(cacheKey, message);
    }
    catch (Exception ex)
    {
        if (ex is KeyNotFoundException ||
            SafeException.Get(ex).Contains("not present in the dictionary", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var safeProjects = await GetProjectsFallbackAsync(targetCompanyId, isSuperAdmin, useUserScope, currentUserId);
                message.Status = 1;
                message.Data = safeProjects;
                message.Message = "Project list returned with fallback.";
                return Json(message);
            }
            catch (Exception fallbackEx)
            {
                Console.WriteLine($"[GetProjects] Outer fallback failed: {fallbackEx}");
            }
        }

        message.Status = 0;
        message.Message = DisplayMessage.ErrorMessage + " " + SafeException.Get(ex);
    }

    return Json(message);
}

private async Task<List<object>> GetProjectsFromRawSqlAsync(
    int targetCompanyId,
    bool isSuperAdmin,
    bool useUserScope,
    int currentUserId)
{
    var projects = new List<object>();
    string connString = db.Database.GetConnectionString();

    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();
    var projectColumns = await GetTableColumnSetAsync(conn, "tbl_project");
    var logGridSelect = projectColumns.Contains("log_grid")
        ? "p.log_grid"
        : "NULL AS log_grid";
    var siteSizeSelect = projectColumns.Contains("sitesize")
        ? "p.sitesize"
        : "1.00 AS sitesize";
    var createdByUserFilter = projectColumns.Contains("created_by_user_id")
        ? "OR (@uid > 0 AND p.created_by_user_id = @uid)"
        : string.Empty;

    using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        SELECT
            p.id,
            p.project_name,
            p.ref_session_id,
            p.from_date,
            p.to_date,
            p.provider,
            p.tech,
            p.band,
            p.earfcn,
            p.apps,
            p.grid_size,
            {logGridSelect},
            {siteSizeSelect},
            p.created_on,
            p.Download_path,
            ST_AsText(p.polygon) AS project_polygon_wkt,
            mr.region_wkt,
            mr.region_blob_b64,
            COALESCE(ST_AsText(p.polygon), mr.region_wkt) AS geometry_wkt
        FROM tbl_project p
        LEFT JOIN (
            SELECT
                mr1.tbl_project_id,
                ST_AsText(mr1.region) AS region_wkt,
                TO_BASE64(mr1.region) AS region_blob_b64
            FROM map_regions mr1
            INNER JOIN (
                SELECT tbl_project_id, MAX(id) AS max_id
                FROM map_regions
                WHERE tbl_project_id IS NOT NULL
                GROUP BY tbl_project_id
            ) picked
              ON picked.tbl_project_id = mr1.tbl_project_id
             AND picked.max_id = mr1.id
        ) mr
          ON mr.tbl_project_id = p.id
        WHERE (p.status IS NULL OR p.status <> 0)
          AND (
                @global = 1
                OR p.company_id = @cid
                {createdByUserFilter}
              )
        ORDER BY p.id DESC;";

    cmd.Parameters.AddWithValue("@global", isSuperAdmin && targetCompanyId == 0 ? 1 : 0);
    cmd.Parameters.AddWithValue("@cid", targetCompanyId);
    cmd.Parameters.AddWithValue("@uid", useUserScope ? currentUserId : 0);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        object? ReadDb(string name)
        {
            var value = reader[name];
            return value == DBNull.Value ? null : value;
        }

        projects.Add(new
        {
            id = ReadDb("id"),
            project_name = ReadDb("project_name"),
            ref_session_id = ReadDb("ref_session_id"),
            from_date = ReadDb("from_date"),
            to_date = ReadDb("to_date"),
            provider = ReadDb("provider"),
            tech = ReadDb("tech"),
            band = ReadDb("band"),
            earfcn = ReadDb("earfcn"),
            apps = ReadDb("apps"),
            grid_size = ReadDb("grid_size"),
            log_grid = ReadDb("log_grid"),
            sitesize = ReadDb("sitesize"),
            created_on = ReadDb("created_on"),
            Download_path = ReadDb("Download_path"),
            project_polygon_wkt = ReadDb("project_polygon_wkt"),
            region_wkt = ReadDb("region_wkt"),
            region_blob_b64 = ReadDb("region_blob_b64"),
            geometry_wkt = ReadDb("geometry_wkt"),
        });
    }

    return projects;
}

private async Task<List<object>> GetProjectsFallbackAsync(
    int targetCompanyId,
    bool isSuperAdmin,
    bool useUserScope,
    int currentUserId)
{
    var projects = new List<object>();
    string connString = db.Database.GetConnectionString();

    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();

    var projectColumns = await GetTableColumnSetAsync(conn, "tbl_project");
    var logGridSelect = projectColumns.Contains("log_grid")
        ? "p.log_grid"
        : "NULL AS log_grid";
    var siteSizeSelect = projectColumns.Contains("sitesize")
        ? "p.sitesize"
        : "1.00 AS sitesize";
    var createdByUserFilter = projectColumns.Contains("created_by_user_id")
        ? "OR (@uid > 0 AND p.created_by_user_id = @uid)"
        : string.Empty;

    using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"
        SELECT
            p.id,
            p.project_name,
            p.ref_session_id,
            p.from_date,
            p.to_date,
            p.provider,
            p.tech,
            p.band,
            p.earfcn,
            p.apps,
            p.grid_size,
            {logGridSelect},
            {siteSizeSelect},
            p.created_on,
            p.Download_path
        FROM tbl_project p
        WHERE (p.status IS NULL OR p.status <> 0)
          AND (
                @global = 1
                OR p.company_id = @cid
                {createdByUserFilter}
              )
        ORDER BY p.id DESC;";

    cmd.Parameters.AddWithValue("@global", isSuperAdmin && targetCompanyId == 0 ? 1 : 0);
    cmd.Parameters.AddWithValue("@cid", targetCompanyId);
    cmd.Parameters.AddWithValue("@uid", useUserScope ? currentUserId : 0);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        object? ReadDb(string name)
        {
            var value = reader[name];
            return value == DBNull.Value ? null : value;
        }

        projects.Add(new
        {
            id = ReadDb("id"),
            project_name = ReadDb("project_name"),
            ref_session_id = ReadDb("ref_session_id"),
            from_date = ReadDb("from_date"),
            to_date = ReadDb("to_date"),
            provider = ReadDb("provider"),
            tech = ReadDb("tech"),
            band = ReadDb("band"),
            earfcn = ReadDb("earfcn"),
            apps = ReadDb("apps"),
            grid_size = ReadDb("grid_size"),
            log_grid = ReadDb("log_grid"),
            sitesize = ReadDb("sitesize"),
            created_on = ReadDb("created_on"),
            Download_path = ReadDb("Download_path"),
            project_polygon_wkt = (object?)null,
            region_wkt = (object?)null,
            region_blob_b64 = (object?)null,
            geometry_wkt = (object?)null
        });
    }

    return projects;
}


[HttpGet, Route("GetDominanceDetails")]
public async Task<JsonResult> GetDominanceDetails([FromQuery] MapFilter1 filters)
{
    var sessionIds = filters?.GetSessionIds() ?? new List<long>();
    if (sessionIds.Count == 0)
        return Json(new { success = false, message = "No valid session IDs provided" });

    try
    {
        var cacheKey = BuildMapViewCacheKey("dominance-details", sessionIds, filters?.NetworkType ?? "all");
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Json(cached);

        string connString = db.Database.GetConnectionString();
        using var conn = new MySqlConnection(connString);
        await conn.OpenAsync();

        using var cmdConfig = new MySqlCommand("SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", conn);
        await cmdConfig.ExecuteNonQueryAsync();

        // 1. Build Parameters
        var idParams = new List<string>();
        var p = new Dictionary<string, object>();
        for (int i = 0; i < sessionIds.Count; i++)
        {
            string pname = $"@sid{i}";
            idParams.Add(pname);
            p.Add(pname, sessionIds[i]);
        }

        string sessionWhereClause = idParams.Any() 
            ? $"t1.session_id IN ({string.Join(",", idParams)})" 
            : "1=0";

        // network type filter for dominance query
        string networkClause = "";
        if (!string.IsNullOrWhiteSpace(filters?.NetworkType) &&
            !filters.NetworkType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            var nt = filters.NetworkType.Trim();
            if (nt.Equals("5G", StringComparison.OrdinalIgnoreCase) ||
                nt.Equals("4G", StringComparison.OrdinalIgnoreCase) ||
                nt.Equals("3G", StringComparison.OrdinalIgnoreCase) ||
                nt.Equals("2G", StringComparison.OrdinalIgnoreCase))
            {
                // use parameter to avoid SQL injection
                networkClause = " AND t1.network LIKE @netPattern";
                p.Add("@netPattern", $"%{nt}%");
            }
        }

        // 2. The SQL Query
        string sql = $@"
            SELECT 
                t1.session_id,
                t1.id AS LogId,
                t1.lat,
                t1.lon,
                (t1.rsrp - t2.rsrp) AS DominanceValue
            FROM tbl_network_log t1
            JOIN tbl_network_log_neighbour t2 
                ON t1.session_id = t2.session_id 
                AND t1.timestamp = t2.timestamp
            WHERE {sessionWhereClause}
                AND t1.primary_cell_info_1 LIKE '%mRegistered=YES%'
                AND t1.rsrp IS NOT NULL 
                AND t2.rsrp IS NOT NULL
                {networkClause}
            ORDER BY t1.id, DominanceValue ASC
            ";

        // 3. Grouping Logic
        var groupedData = new Dictionary<string, Dictionary<string, object>>();

        using var cmd = new MySqlCommand(sql, conn);
        foreach (var param in p) cmd.Parameters.AddWithValue(param.Key, param.Value);

        using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            string logId = rd.GetInt64(1).ToString();
            
            // Format as number or string depending on your preference. 
            // Using double here so it appears as number in JSON [ -12.5, 5.0 ]
            double domVal = Math.Round(rd.GetDouble(4), 2); 

            // If new LogId, create the row structure
            if (!groupedData.ContainsKey(logId))
            {
                var row = new Dictionary<string, object>();
                row["session_id"] = rd.GetInt64(0).ToString();
                row["LogId"] = logId;
                row["lat"] = rd.IsDBNull(2) ? 0.0 : rd.GetDouble(2);
                row["lon"] = rd.IsDBNull(3) ? 0.0 : rd.GetDouble(3);
                
                // Initialize the simple list
                row["dominance"] = new List<double>(); 

                groupedData[logId] = row;
            }

            // Add value to the array
            var domList = groupedData[logId]["dominance"] as List<double>;
            domList.Add(domVal);
        }

        var response = new 
        { 
            success = true, 
            count = groupedData.Count, 
            data = groupedData.Values 
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Json(response);
    }
    catch (Exception ex)
    {
        return Json(new { success = false, message = "Server Error", details = SafeException.Get(ex) });
    }
}
[HttpGet, Route("GetPciDistribution")]
public async Task<JsonResult> GetPciDistribution([FromQuery] MapFilter1 filters)
{
    var sessionIds = filters?.GetSessionIds() ?? new List<long>();
    
    if (sessionIds.Count == 0)
        return Json(new { message = "No valid session IDs provided" });

    try
    {
        var cacheKey = BuildMapViewCacheKey("pci-distribution", sessionIds);
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Json(cached);

        string connString = db.Database.GetConnectionString();

        // Task 1: Primary YES (Normalized by Total Primary Count)
        var taskPrimary = GetPciDistributionGlobal(
            connString, 
            "tbl_network_log", 
            sessionIds, 
            isPrimaryTable: true
        );

        // Task 2: Neighbor NO (Normalized by Total Neighbor Count)
        var taskNeighbor = GetPciDistributionGlobal(
            connString, 
            "tbl_network_log_neighbour", 
            sessionIds, 
            isPrimaryTable: false
        );

        await Task.WhenAll(taskPrimary, taskNeighbor);

        var response = new
        {
            success = true,
            primary_yes = taskPrimary.Result,
            primary_no = taskNeighbor.Result
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Json(response);
    }
    catch (Exception ex)
    {
        return Json(new { success = false, message = "Server Error", details = SafeException.Get(ex) });
    }
}

// ---------------------------------------------------------
// HELPER: GLOBAL PERCENTAGE CALCULATION (Matches Excel Pivot)
// ---------------------------------------------------------
private async Task<Dictionary<int, Dictionary<int, double>>> GetPciDistributionGlobal(
    string connString, 
    string tableName, 
    List<long> sessionIds, 
    bool isPrimaryTable)
{
    using var conn = new MySqlConnection(connString);
    await conn.OpenAsync();

    // 1. Build Parameters & WHERE Clause
    var p = new Dictionary<string, object>();
    var clauses = new List<string>();
    var idParams = new List<string>();

    for (int i = 0; i < sessionIds.Count; i++)
    {
        string pname = $"@sid{i}";
        idParams.Add(pname);
        p.Add(pname, sessionIds[i]);
    }
    
    if (idParams.Any()) 
        clauses.Add($"session_id IN ({string.Join(",", idParams)})");
    else 
        return new Dictionary<int, Dictionary<int, double>>();

    if (isPrimaryTable)
    {
        clauses.Add("primary_cell_info_1 LIKE '%mRegistered=YES%'");
    }

    string whereClause = string.Join(" AND ", clauses);

    // 2. Aggregate Query (Raw Counts)
    string sql = $@"
        SELECT pci, num_cells, COUNT(*) as cell_count
        FROM {tableName}
        WHERE {whereClause}
        GROUP BY pci, num_cells";

    var rawData = new List<(int pci, int num_cells, int count)>();

    using var cmd = new MySqlCommand(sql, conn);
    foreach (var param in p) cmd.Parameters.AddWithValue(param.Key, param.Value);

    using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        if (!rd.IsDBNull(0) && !rd.IsDBNull(1))
        {
            int pci = rd.GetInt32(0);
            int numCells = Convert.ToInt32(rd.GetValue(1)); 
            int count = rd.GetInt32(2);
            rawData.Add((pci, numCells, count));
        }
    }

    // 3. ðŸ§® CALCULATE GLOBAL PERCENTAGE (The "Excel" Logic)
    var result = new Dictionary<int, Dictionary<int, double>>();

    // Step A: Calculate Grand Total (Total rows in this session filter)
    double grandTotal = rawData.Sum(x => x.count);

    if (grandTotal == 0) return result;

    var groupedByPci = rawData.GroupBy(x => x.pci);

    foreach (var group in groupedByPci)
    {
        int pci = group.Key;
        var pciDistribution = new Dictionary<int, double>();

        // Pre-fill keys 1-16 with 0.0
        for(int i=1; i<=16; i++) pciDistribution[i] = 0.0;

        foreach (var row in group)
        {
            // Step B: Divide by GRAND TOTAL (Not PCI Total)
            // This ensures the percentages represent the density across the entire drive test.
            if (row.num_cells <= 0)
            {
                continue;
            }

            // Defensive guard: source data can occasionally contain outlier values
            // beyond the usual 1..16 range; avoid KeyNotFound exceptions.
            if (!pciDistribution.ContainsKey(row.num_cells))
            {
                pciDistribution[row.num_cells] = 0.0;
            }

            pciDistribution[row.num_cells] = Math.Round(row.count / grandTotal, 4);
        }
        
        result[pci] = pciDistribution;
    }

    return result;
}




[HttpGet, Route("GetLtePredictionStats")]
public async Task<IActionResult> GetLtePredictionStats([FromQuery] long projectId, [FromQuery] string metric)
{
    if (projectId <= 0)
        return BadRequest(new { Status = 0, Message = "A valid projectId is required." });

    if (string.IsNullOrWhiteSpace(metric))
        return BadRequest(new { Status = 0, Message = "A metric (RSRP, RSRQ, SINR) is required." });

    metric = metric.Trim().ToUpperInvariant();

    try
    {
        var cacheKey = BuildMapViewCacheKey("lte-prediction-stats", projectId, metric);
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        List<double> valuesList;

        // FIX: Using db.Set<T>() explicitly resolves the CS0411 and CS1061 compiler errors
        var baseQuery = db.Set<tbl_lte_prediction_results>()
                          .AsNoTracking()
                          .Where(x => x.ProjectId == projectId);

        if (metric == "RSRP")
        {
            valuesList = await baseQuery
                .Where(x => x.PredRsrp.HasValue)
                .Select(x => x.PredRsrp!.Value)
                .ToListAsync();
        }
        else if (metric == "RSRQ")
        {
            valuesList = await baseQuery
                .Where(x => x.PredRsrq.HasValue)
                .Select(x => x.PredRsrq!.Value)
                .ToListAsync();
        }
        else if (metric == "SINR" || metric == "SNR")
        {
            valuesList = await baseQuery
                .Where(x => x.PredSinr.HasValue)
                .Select(x => x.PredSinr!.Value)
                .ToListAsync();
        }
        else
        {
            return BadRequest(new { Status = 0, Message = "Invalid metric. Please pass RSRP, RSRQ, or SINR." });
        }

        if (valuesList.Count == 0)
        {
            var empty = new { Status = 0, Message = $"No {metric} prediction data found for this project." };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        var response = new
        {
            Status = 1,
            ProjectId = projectId,
            Metric = metric,
            Data = CalculateMetrics(valuesList)
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error calculating stats: " + SafeException.Get(ex) });
    }
}

// Helper method to compute the statistics
private object? CalculateMetrics(List<double> values)
{
    if (values == null || values.Count == 0) return null;

    // Sort the array (required for Median)
    values.Sort();
    int count = values.Count;

    double mean = values.Average();
    
    // For RSRP, RSRQ, and SINR: Higher values indicate a better signal.
    double best = values.Max();   
    double worst = values.Min();

    // Median: Middle value of the sorted array
    double median = (count % 2 == 0)
        ? (values[(count / 2) - 1] + values[count / 2]) / 2.0
        : values[count / 2];

    // Mode: Most frequent value. 
    // We round to 1 decimal place before grouping so continuous RF data actually forms sensible clusters.
    double? mode = values
        .GroupBy(v => Math.Round(v, 1))
        .OrderByDescending(g => g.Count())
        .FirstOrDefault()?.Key;

    return new
    {
        SampleCount = count,
        Average = Math.Round(mean, 2),
        Mean = Math.Round(mean, 2),        // Included as requested, though mathematically identical to Average
        Median = Math.Round(median, 2),
        Mode = mode,
        Best = Math.Round(best, 2),
        Worst = Math.Round(worst, 2)
    };
}

[HttpGet, Route("GetLtePredictionLocationStats")]
public async Task<IActionResult> GetLtePredictionLocationStats(
    [FromQuery] long projectId, 
    [FromQuery] string metric, 
    [FromQuery] string statType = "avg",
    [FromQuery] string? siteId = null) // 1. ADDED SITE ID PARAMETER HERE
{
    if (projectId <= 0)
        return BadRequest(new { Status = 0, Message = "A valid projectId is required." });

    if (string.IsNullOrWhiteSpace(metric))
        return BadRequest(new { Status = 0, Message = "A metric (RSRP, RSRQ, SINR) is required." });

    metric = metric.Trim().ToUpperInvariant();
    statType = statType.Trim().ToLowerInvariant(); 

    try
    {
        var cacheKey = BuildMapViewCacheKey("lte-prediction-location-stats", projectId, metric, statType, siteId ?? "all");
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        var baseQuery = db.Set<tbl_lte_prediction_results>()
                          .AsNoTracking()
                          .Where(x => x.ProjectId == projectId);

        // 2. FILTER BY SITE ID IF PROVIDED
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            baseQuery = baseQuery.Where(x => x.SiteId == siteId);
        }

        var rawData = new List<dynamic>();

        // 3. FETCH SITE ID FROM THE DATABASE
        if (metric == "RSRP")
        {
            rawData = await baseQuery.Where(x => x.PredRsrp.HasValue)
                .Select(x => new { Lat = Math.Round(x.Lat, 6), Lon = Math.Round(x.Lon, 6), SiteId = x.SiteId, Value = x.PredRsrp!.Value })
                .ToListAsync<dynamic>();
        }
        else if (metric == "RSRQ")
        {
            rawData = await baseQuery.Where(x => x.PredRsrq.HasValue)
                .Select(x => new { Lat = Math.Round(x.Lat, 6), Lon = Math.Round(x.Lon, 6), SiteId = x.SiteId, Value = x.PredRsrq!.Value })
                .ToListAsync<dynamic>();
        }
        else if (metric == "SINR" || metric == "SNR")
        {
            rawData = await baseQuery.Where(x => x.PredSinr.HasValue)
                .Select(x => new { Lat = Math.Round(x.Lat, 6), Lon = Math.Round(x.Lon, 6), SiteId = x.SiteId, Value = x.PredSinr!.Value })
                .ToListAsync<dynamic>();
        }
        else
        {
            return BadRequest(new { Status = 0, Message = "Invalid metric. Please pass RSRP, RSRQ, or SINR." });
        }

        if (rawData.Count == 0)
        {
            string msg = $"No {metric} prediction data found for this project";
            msg += string.IsNullOrWhiteSpace(siteId) ? "." : $" and site ID '{siteId}'.";
            var empty = new { Status = 0, Message = msg };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        // 4. GROUP BY LAT, LON, AND SITE ID
        var groupedData = rawData
            .GroupBy(x => new { x.Lat, x.Lon, x.SiteId })
            .Select(g => 
            {
                var values = g.Select(v => (double)v.Value).ToList();
                double requestedValue = CalculateSingleStat(values, statType);

                return new 
                {
                    lat = g.Key.Lat,
                    lon = g.Key.Lon,
                    siteId = g.Key.SiteId, // 5. RETURN SITE ID IN THE JSON DATA
                    sampleCount = values.Count,
                    value = requestedValue 
                };
            })
            .ToList();

        var response = new
        {
            Status = 1,
            ProjectId = projectId,
            SiteIdFiltered = siteId ?? "All", 
            Metric = metric,
            StatRequested = statType,
            TotalLocations = groupedData.Count,
            Data = groupedData
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error calculating location stats: " + SafeException.Get(ex) });
    }
}

[HttpGet, Route("GetLtePredictionLocationStatsRefined")]
public async Task<IActionResult> GetLtePredictionLocationStatsRefined(
    [FromQuery] long projectId, 
    [FromQuery] string metric, 
    [FromQuery] string statType = "avg",
    [FromQuery] string? siteId = null)
{
    if (projectId <= 0)
        return BadRequest(new { Status = 0, Message = "A valid projectId is required." });

    if (string.IsNullOrWhiteSpace(metric))
        return BadRequest(new { Status = 0, Message = "A metric (TOP2, TOP3, MEASURED) is required." });

    metric = metric.Trim().ToUpperInvariant();
    statType = statType.Trim().ToLowerInvariant(); 

    try
    {
        var cacheKey = BuildMapViewCacheKey("lte-prediction-location-stats-refined", projectId, metric, statType, siteId ?? "all");
        var cached = await TryGetMapViewCacheAsync<object>(cacheKey);
        if (cached != null)
            return Ok(cached);

        var baseQuery = db.Set<tbl_lte_prediction_results_refined>()
                          .AsNoTracking()
                          .Where(x => x.project_id == projectId);

        if (!string.IsNullOrWhiteSpace(siteId))
        {
            baseQuery = baseQuery.Where(x => x.site_id == siteId);
        }

        var rawData = new List<dynamic>();

        if (metric == "TOP2" || metric == "PRED_RSRP_TOP2_AVG")
        {
            rawData = await baseQuery.Where(x => x.pred_rsrp_top2_avg.HasValue)
                .Select(x => new { Lat = Math.Round(x.lat, 6), Lon = Math.Round(x.lon, 6), SiteId = x.site_id, Value = x.pred_rsrp_top2_avg!.Value })
                .ToListAsync<dynamic>();
        }
        else if (metric == "TOP3" || metric == "PRED_RSRP_TOP3_AVG")
        {
            rawData = await baseQuery.Where(x => x.pred_rsrp_top3_avg.HasValue)
                .Select(x => new { Lat = Math.Round(x.lat, 6), Lon = Math.Round(x.lon, 6), SiteId = x.site_id, Value = x.pred_rsrp_top3_avg!.Value })
                .ToListAsync<dynamic>();
        }
        else if (metric == "MEASURED" || metric == "MEASURED_DT_RSRP")
        {
            rawData = await baseQuery.Where(x => x.measured_dt_rsrp.HasValue)
                .Select(x => new { Lat = Math.Round(x.lat, 6), Lon = Math.Round(x.lon, 6), SiteId = x.site_id, Value = x.measured_dt_rsrp!.Value })
                .ToListAsync<dynamic>();
        }
        else
        {
            return BadRequest(new { Status = 0, Message = "Invalid metric. Please pass TOP2, TOP3, or MEASURED." });
        }

        if (rawData.Count == 0)
        {
            string msg = $"No {metric} prediction data found for this project";
            msg += string.IsNullOrWhiteSpace(siteId) ? "." : $" and site ID '{siteId}'.";
            var empty = new { Status = 0, Message = msg };
            await SetMapViewCacheAsync(cacheKey, empty);
            return Ok(empty);
        }

        var groupedData = rawData
            .GroupBy(x => new { x.Lat, x.Lon, x.SiteId })
            .Select(g => 
            {
                var values = g.Select(v => (double)v.Value).ToList();
                double requestedValue = CalculateSingleStat(values, statType);

                return new 
                {
                    lat = g.Key.Lat,
                    lon = g.Key.Lon,
                    siteId = g.Key.SiteId,
                    sampleCount = values.Count,
                    value = requestedValue 
                };
            })
            .ToList();

        var response = new
        {
            Status = 1,
            ProjectId = projectId,
            SiteIdFiltered = siteId ?? "All", 
            Metric = metric,
            StatRequested = statType,
            TotalLocations = groupedData.Count,
            Data = groupedData
        };
        await SetMapViewCacheAsync(cacheKey, response);
        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error calculating location stats: " + SafeException.Get(ex) });
    }
}

[HttpGet, Route("GetSitePredictionBase")]
public async Task<IActionResult> GetSitePredictionBase(
    [FromQuery(Name = "project_id")] int? projectId = null,
    [FromQuery(Name = "node_b_id")] string? nodeBId = null,
    [FromQuery(Name = "cell_id")] string? cellId = null,
    [FromQuery(Name = "sector")] string? sector = null,
    [FromQuery(Name = "sector_id")] string? sectorId = null,
    [FromQuery(Name = "polygon_ids")] string? polygonIdsCsv = null)
{
    var trimmedNodeBId = string.IsNullOrWhiteSpace(nodeBId) ? null : nodeBId.Trim();
    var trimmedCellId = string.IsNullOrWhiteSpace(cellId) ? null : cellId.Trim();
    var trimmedSector = string.IsNullOrWhiteSpace(sector) ? null : sector.Trim();
    var trimmedSectorId = string.IsNullOrWhiteSpace(sectorId) ? null : sectorId.Trim();
    var lookupSector = trimmedSectorId ?? trimmedSector;
    var polygonIds = projectId.HasValue ? ParsePolygonIds(polygonIdsCsv) : new List<int>();
    var polygonFilter = BuildPolygonFilterClause(polygonIds, "b.lat", "b.lon");
    var lookupNodeBIds = new List<string>();
    var lookupCellIds = new List<string>();
    var lookupNodeBCellIds = new List<string>();
    void AddLookupValue(List<string> values, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!values.Any(existing => string.Equals(existing, text, StringComparison.OrdinalIgnoreCase)))
            values.Add(text);
    }
    string? GetPipeExpandedNodeBId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Contains('|', StringComparison.Ordinal)) return null;
        return $"{text}|{text}";
    }
    void AddNodeBLookupValue(string? value)
    {
        AddLookupValue(lookupNodeBIds, value);
        AddLookupValue(lookupNodeBIds, GetPipeExpandedNodeBId(value));
    }
    void AddNodeBCellLookupValues(string? nodeValue, params string?[] cellValues)
    {
        var normalizedNode = (nodeValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedNode)) return;

        var expandedNode = GetPipeExpandedNodeBId(normalizedNode);
        foreach (var rawCellValue in cellValues)
        {
            var normalizedCell = (rawCellValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedCell)) continue;

            AddLookupValue(lookupNodeBCellIds, $"{normalizedNode}_{normalizedCell}");
            if (!string.IsNullOrWhiteSpace(expandedNode))
                AddLookupValue(lookupNodeBCellIds, $"{expandedNode}_{normalizedCell}");
        }
    }
    var combinedNodeBCellId =
        !string.IsNullOrWhiteSpace(trimmedNodeBId) && !string.IsNullOrWhiteSpace(trimmedCellId)
            ? $"{trimmedNodeBId}_{trimmedCellId}"
            : (string.IsNullOrWhiteSpace(trimmedNodeBId) &&
               !string.IsNullOrWhiteSpace(trimmedCellId) &&
               trimmedCellId.Contains("_", StringComparison.Ordinal)
                ? trimmedCellId
                : null);

    AddNodeBLookupValue(trimmedNodeBId);
    AddLookupValue(lookupCellIds, trimmedCellId);
    AddLookupValue(lookupNodeBCellIds, combinedNodeBCellId);
    AddNodeBCellLookupValues(trimmedNodeBId, trimmedCellId);

    if (!string.IsNullOrWhiteSpace(trimmedCellId) && trimmedCellId.Contains("_", StringComparison.Ordinal))
    {
        var parts = trimmedCellId.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var inferredNodeBId = parts[0];
            var localCellId = parts[^1];
            var cellWithoutNode = string.Join("_", parts.Skip(1));

            AddNodeBLookupValue(inferredNodeBId);
            AddLookupValue(lookupCellIds, localCellId);
            AddLookupValue(lookupCellIds, cellWithoutNode);
            AddNodeBCellLookupValues(inferredNodeBId, localCellId, cellWithoutNode, trimmedCellId);
        }
    }

    if (!string.IsNullOrWhiteSpace(trimmedNodeBId) && !string.IsNullOrWhiteSpace(trimmedCellId))
    {
        var localCellId = trimmedCellId.Contains("_", StringComparison.Ordinal)
            ? trimmedCellId.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
            : trimmedCellId;
        AddLookupValue(lookupCellIds, localCellId);
        AddNodeBCellLookupValues(trimmedNodeBId, localCellId, trimmedCellId);
    }

    if (string.IsNullOrWhiteSpace(trimmedNodeBId) &&
        string.IsNullOrWhiteSpace(trimmedCellId) &&
        string.IsNullOrWhiteSpace(lookupSector))
    {
        return BadRequest(new
        {
            Status = 0,
            Message = "At least one lookup key is required (node_b_id, cell_id, sector, or sector_id)."
        });
    }

    try
    {
        const string tableName = "lte_prediction_baseline_results";
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var baselineColumns = await GetTableColumnSetAsync(conn, tableName);
        string? baselineCombinedColumn = null;
        foreach (var candidate in new[] { "node_b_cell_id", "nodeb_id_cell_id" })
        {
            if (baselineColumns.Contains(candidate))
            {
                baselineCombinedColumn = candidate;
                break;
            }
        }
        string? sectorColumn = null;
        foreach (var candidate in new[] { "sector_id", "sector", "sec_id" })
        {
            if (baselineColumns.Contains(candidate))
            {
                sectorColumn = candidate;
                break;
            }
        }

        string Eq(string alias, string column, string paramName) =>
            $"CONVERT(COALESCE({alias}.`{column}`, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci = CONVERT(@{paramName} USING utf8mb4) COLLATE utf8mb4_unicode_ci";
        string InLookup(string alias, string column, string paramPrefix, IReadOnlyList<string> values)
        {
            var names = values
                .Select((_, i) => $"@{paramPrefix}_{i}")
                .ToList();
            return $"CONVERT(COALESCE({alias}.`{column}`, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci IN ({string.Join(", ", names.Select(name => $"CONVERT({name} USING utf8mb4) COLLATE utf8mb4_unicode_ci"))})";
        }

        var andClauses = new List<string>();
        if (projectId.HasValue && baselineColumns.Contains("project_id"))
            andClauses.Add("b.`project_id` = @project_id");

        var lookupClauses = new List<string>();
        var hasNodeLookup = !string.IsNullOrWhiteSpace(trimmedNodeBId);
        var hasCellLookup = !string.IsNullOrWhiteSpace(trimmedCellId);

        if (hasNodeLookup && hasCellLookup)
        {
            if (!string.IsNullOrWhiteSpace(baselineCombinedColumn) && lookupNodeBCellIds.Count > 0)
                lookupClauses.Add(InLookup("b", baselineCombinedColumn, "node_b_cell_id", lookupNodeBCellIds));

            if (baselineColumns.Contains("node_b_id") && baselineColumns.Contains("cell_id") && lookupNodeBIds.Count > 0 && lookupCellIds.Count > 0)
                lookupClauses.Add($"({InLookup("b", "node_b_id", "node_b_id", lookupNodeBIds)} AND {InLookup("b", "cell_id", "cell_id", lookupCellIds)})");
            else if (baselineColumns.Contains("site_id") && baselineColumns.Contains("cell_id") && lookupNodeBIds.Count > 0 && lookupCellIds.Count > 0)
                lookupClauses.Add($"({InLookup("b", "site_id", "node_b_id", lookupNodeBIds)} AND {InLookup("b", "cell_id", "cell_id", lookupCellIds)})");
        }
        else
        {
            if (hasNodeLookup)
            {
                if (baselineColumns.Contains("node_b_id") && lookupNodeBIds.Count > 0) lookupClauses.Add(InLookup("b", "node_b_id", "node_b_id", lookupNodeBIds));
                if (baselineColumns.Contains("site_id") && lookupNodeBIds.Count > 0) lookupClauses.Add(InLookup("b", "site_id", "node_b_id", lookupNodeBIds));
                if (!string.IsNullOrWhiteSpace(baselineCombinedColumn) && lookupNodeBIds.Count > 0) lookupClauses.Add(InLookup("b", baselineCombinedColumn, "node_b_id", lookupNodeBIds));
            }
            if (hasCellLookup)
            {
                if (baselineColumns.Contains("cell_id") && lookupCellIds.Count > 0) lookupClauses.Add(InLookup("b", "cell_id", "cell_id", lookupCellIds));
                if (!string.IsNullOrWhiteSpace(baselineCombinedColumn))
                {
                    var combinedLookupValues = lookupNodeBCellIds.Count > 0 ? lookupNodeBCellIds : lookupCellIds;
                    if (combinedLookupValues.Count > 0) lookupClauses.Add(InLookup("b", baselineCombinedColumn, "node_b_cell_id", combinedLookupValues));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(lookupSector))
        {
            if (!string.IsNullOrWhiteSpace(sectorColumn))
            {
                if (hasNodeLookup || hasCellLookup)
                    andClauses.Add(Eq("b", sectorColumn, "sector_lookup"));
                else
                    lookupClauses.Add(Eq("b", sectorColumn, "sector_lookup"));
            }
            else if (!hasNodeLookup && !hasCellLookup)
            {
                if (baselineColumns.Contains("cell_id")) lookupClauses.Add(Eq("b", "cell_id", "sector_lookup"));
            }
        }

        if (lookupClauses.Count == 0)
        {
            var empty = new { Status = 1, Table = tableName, Count = 0, Data = Array.Empty<object>() };
            return Ok(empty);
        }

        var whereParts = new List<string>();
        if (andClauses.Count > 0) whereParts.AddRange(andClauses);
        whereParts.Add($"({string.Join(" OR ", lookupClauses)})");

        var selectSector = !string.IsNullOrWhiteSpace(sectorColumn)
            ? $"b.`{sectorColumn}` AS sector_lookup"
            : "NULL AS sector_lookup";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT
    b.id,
    b.project_id,
    b.job_id,
    b.site_id,
    b.lat,
    b.lon,
    b.pred_rsrp,
    b.pred_rsrq,
    b.pred_sinr,
    b.node_b_id,
    b.cell_id,
    b.operator AS operator_name,
    b.created_at,
    {(string.IsNullOrWhiteSpace(baselineCombinedColumn) ? "NULL" : $"b.`{baselineCombinedColumn}`")} AS node_b_cell_id,
    {selectSector}
FROM {tableName} b
WHERE {string.Join(" AND ", whereParts)}
{polygonFilter}
ORDER BY b.id DESC;";

        if (projectId.HasValue) Add(cmd, "@project_id", projectId.Value);
        if (polygonIds.Count > 0) Add(cmd, "@pid", projectId!.Value);
        AddPolygonIdsParameters(cmd, polygonIds);
        for (var i = 0; i < lookupNodeBIds.Count; i++) Add(cmd, $"@node_b_id_{i}", lookupNodeBIds[i]);
        for (var i = 0; i < lookupCellIds.Count; i++) Add(cmd, $"@cell_id_{i}", lookupCellIds[i]);
        for (var i = 0; i < lookupNodeBCellIds.Count; i++) Add(cmd, $"@node_b_cell_id_{i}", lookupNodeBCellIds[i]);
        if (!string.IsNullOrWhiteSpace(lookupSector)) Add(cmd, "@sector_lookup", lookupSector);

        var items = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            items.Add(row);
        }

        var response = new
        {
            Status = 1,
            Table = tableName,
            ProjectIdFiltered = projectId,
            CellIdFiltered = trimmedCellId,
            NodeBIdFiltered = trimmedNodeBId,
            NodeBCellIdFiltered = combinedNodeBCellId,
            NodeBIdLookupValues = lookupNodeBIds,
            CellIdLookupValues = lookupCellIds,
            NodeBCellIdLookupValues = lookupNodeBCellIds,
            SectorFiltered = lookupSector,
            Total = items.Count,
            Count = items.Count,
            Data = items
        };
        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error fetching site prediction base data: " + SafeException.Get(ex) });
    }
}

[HttpGet, Route("GetSitePredictionOptimised")]
public async Task<IActionResult> GetSitePredictionOptimised(
    [FromQuery(Name = "project_id")] int? projectId = null,
    [FromQuery(Name = "node_b_id")] string? nodeBId = null,
    [FromQuery(Name = "cell_id")] string? cellId = null,
    [FromQuery(Name = "sector")] string? sector = null,
    [FromQuery(Name = "sector_id")] string? sectorId = null,
    [FromQuery(Name = "scenario")] int? scenario = null,
    [FromQuery(Name = "scenario_id")] int? scenarioId = null,
    [FromQuery(Name = "public_scenario_id")] int? publicScenarioId = null,
    [FromQuery(Name = "site_prediction_scenario_id")] int? sitePredictionScenarioId = null,
    [FromQuery(Name = "polygon_ids")] string? polygonIdsCsv = null)
{
    var trimmedNodeBId = string.IsNullOrWhiteSpace(nodeBId) ? null : nodeBId.Trim();
    var trimmedCellId = string.IsNullOrWhiteSpace(cellId) ? null : cellId.Trim();
    var trimmedSector = string.IsNullOrWhiteSpace(sector) ? null : sector.Trim();
    var trimmedSectorId = string.IsNullOrWhiteSpace(sectorId) ? null : sectorId.Trim();
    var lookupSector = trimmedSectorId ?? trimmedSector;
    var requestedScenario = scenario ?? scenarioId ?? publicScenarioId ?? sitePredictionScenarioId;
    var selectedScenario = requestedScenario.HasValue && requestedScenario.Value > 0
        ? requestedScenario.Value
        : (int?)null;
    var polygonIds = projectId.HasValue ? ParsePolygonIds(polygonIdsCsv) : new List<int>();
    var optimizedPolygonFilter = BuildPolygonFilterClause(polygonIds, "o.lat", "o.lon");
    var baselinePolygonFilter = BuildPolygonFilterClause(polygonIds, "b.lat", "b.lon");
    var lookupNodeBIds = new List<string>();
    var lookupCellIds = new List<string>();
    var lookupNodeBCellIds = new List<string>();
    void AddLookupValue(List<string> values, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!values.Any(existing => string.Equals(existing, text, StringComparison.OrdinalIgnoreCase)))
            values.Add(text);
    }
    string? GetPipeExpandedNodeBId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Contains('|', StringComparison.Ordinal)) return null;
        return $"{text}|{text}";
    }
    void AddNodeBLookupValue(string? value)
    {
        AddLookupValue(lookupNodeBIds, value);
        AddLookupValue(lookupNodeBIds, GetPipeExpandedNodeBId(value));
    }
    void AddNodeBCellLookupValues(string? nodeValue, params string?[] cellValues)
    {
        var normalizedNode = (nodeValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedNode)) return;

        var expandedNode = GetPipeExpandedNodeBId(normalizedNode);
        foreach (var rawCellValue in cellValues)
        {
            var normalizedCell = (rawCellValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedCell)) continue;

            AddLookupValue(lookupNodeBCellIds, $"{normalizedNode}_{normalizedCell}");
            if (!string.IsNullOrWhiteSpace(expandedNode))
                AddLookupValue(lookupNodeBCellIds, $"{expandedNode}_{normalizedCell}");
        }
    }
    var combinedNodeBCellId =
        !string.IsNullOrWhiteSpace(trimmedNodeBId) && !string.IsNullOrWhiteSpace(trimmedCellId)
            ? $"{trimmedNodeBId}_{trimmedCellId}"
            : (string.IsNullOrWhiteSpace(trimmedNodeBId) &&
               !string.IsNullOrWhiteSpace(trimmedCellId) &&
               trimmedCellId.Contains("_", StringComparison.Ordinal)
                ? trimmedCellId
                : null);

    AddNodeBLookupValue(trimmedNodeBId);
    AddLookupValue(lookupCellIds, trimmedCellId);
    AddLookupValue(lookupNodeBCellIds, combinedNodeBCellId);
    AddNodeBCellLookupValues(trimmedNodeBId, trimmedCellId);

    if (!string.IsNullOrWhiteSpace(trimmedCellId) && trimmedCellId.Contains("_", StringComparison.Ordinal))
    {
        var parts = trimmedCellId.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var inferredNodeBId = parts[0];
            var localCellId = parts[^1];
            var cellWithoutNode = string.Join("_", parts.Skip(1));

            AddNodeBLookupValue(inferredNodeBId);
            AddLookupValue(lookupCellIds, localCellId);
            AddLookupValue(lookupCellIds, cellWithoutNode);
            AddNodeBCellLookupValues(inferredNodeBId, localCellId, cellWithoutNode, trimmedCellId);
        }
    }

    if (!string.IsNullOrWhiteSpace(trimmedNodeBId) && !string.IsNullOrWhiteSpace(trimmedCellId))
    {
        var localCellId = trimmedCellId.Contains("_", StringComparison.Ordinal)
            ? trimmedCellId.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
            : trimmedCellId;
        AddLookupValue(lookupCellIds, localCellId);
        AddNodeBCellLookupValues(trimmedNodeBId, localCellId, trimmedCellId);
    }

    if (string.IsNullOrWhiteSpace(trimmedNodeBId) &&
        string.IsNullOrWhiteSpace(trimmedCellId) &&
        string.IsNullOrWhiteSpace(lookupSector))
    {
        return BadRequest(new
        {
            Status = 0,
            Message = "At least one lookup key is required (node_b_id, cell_id, sector, or sector_id)."
        });
    }

    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var optimizedColumns = await GetTableColumnSetAsync(conn, "lte_prediction_optimised_results");
        var baselineColumns = await GetTableColumnSetAsync(conn, "lte_prediction_baseline_results");
        string? optimizedCombinedColumn = null;
        foreach (var candidate in new[] { "node_b_cell_id", "nodeb_id_cell_id" })
        {
            if (optimizedColumns.Contains(candidate))
            {
                optimizedCombinedColumn = candidate;
                break;
            }
        }
        string? baselineCombinedColumn = null;
        foreach (var candidate in new[] { "node_b_cell_id", "nodeb_id_cell_id" })
        {
            if (baselineColumns.Contains(candidate))
            {
                baselineCombinedColumn = candidate;
                break;
            }
        }

        string? optimizedSectorColumn = null;
        foreach (var candidate in new[] { "sector_id", "sector", "sec_id" })
        {
            if (optimizedColumns.Contains(candidate))
            {
                optimizedSectorColumn = candidate;
                break;
            }
        }

        string? baselineSectorColumn = null;
        foreach (var candidate in new[] { "sector_id", "sector", "sec_id" })
        {
            if (baselineColumns.Contains(candidate))
            {
                baselineSectorColumn = candidate;
                break;
            }
        }

        string Eq(string alias, string column, string paramName) =>
            $"CONVERT(COALESCE({alias}.`{column}`, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci = CONVERT(@{paramName} USING utf8mb4) COLLATE utf8mb4_unicode_ci";

        string InLookup(string alias, string column, string paramPrefix, IReadOnlyList<string> values)
        {
            var names = values
                .Select((_, i) => $"@{paramPrefix}_{i}")
                .ToList();
            return $"CONVERT(COALESCE({alias}.`{column}`, '') USING utf8mb4) COLLATE utf8mb4_unicode_ci IN ({string.Join(", ", names.Select(name => $"CONVERT({name} USING utf8mb4) COLLATE utf8mb4_unicode_ci"))})";
        }

        string BuildWhereClause(string alias, HashSet<string> columns, string? sectorColumn)
        {
            var andClauses = new List<string>();
            if (projectId.HasValue && columns.Contains("project_id"))
                andClauses.Add($"{alias}.`project_id` = @project_id");

            var lookupClauses = new List<string>();
            var hasNodeLookup = !string.IsNullOrWhiteSpace(trimmedNodeBId);
            var hasCellLookup = !string.IsNullOrWhiteSpace(trimmedCellId);

            if (hasNodeLookup && hasCellLookup)
            {
                if (lookupNodeBCellIds.Count > 0)
                {
                    if (columns.Contains("node_b_cell_id")) lookupClauses.Add(InLookup(alias, "node_b_cell_id", "node_b_cell_id", lookupNodeBCellIds));
                    if (columns.Contains("nodeb_id_cell_id")) lookupClauses.Add(InLookup(alias, "nodeb_id_cell_id", "node_b_cell_id", lookupNodeBCellIds));
                }

                if (columns.Contains("node_b_id") && columns.Contains("cell_id"))
                    lookupClauses.Add($"({InLookup(alias, "node_b_id", "node_b_id", lookupNodeBIds)} AND {InLookup(alias, "cell_id", "cell_id", lookupCellIds)})");
                else if (columns.Contains("site_id") && columns.Contains("cell_id"))
                    lookupClauses.Add($"({InLookup(alias, "site_id", "node_b_id", lookupNodeBIds)} AND {InLookup(alias, "cell_id", "cell_id", lookupCellIds)})");
            }
            else
            {
                if (hasNodeLookup)
                {
                    if (columns.Contains("node_b_id")) lookupClauses.Add(InLookup(alias, "node_b_id", "node_b_id", lookupNodeBIds));
                    if (columns.Contains("site_id")) lookupClauses.Add(InLookup(alias, "site_id", "node_b_id", lookupNodeBIds));
                    if (columns.Contains("node_b_cell_id")) lookupClauses.Add(InLookup(alias, "node_b_cell_id", "node_b_id", lookupNodeBIds));
                    if (columns.Contains("nodeb_id_cell_id")) lookupClauses.Add(InLookup(alias, "nodeb_id_cell_id", "node_b_id", lookupNodeBIds));
                }
                if (hasCellLookup)
                {
                    if (columns.Contains("cell_id")) lookupClauses.Add(InLookup(alias, "cell_id", "cell_id", lookupCellIds));
                    if (columns.Contains("node_b_cell_id")) lookupClauses.Add(InLookup(alias, "node_b_cell_id", "node_b_cell_id", lookupNodeBCellIds.Count > 0 ? lookupNodeBCellIds : lookupCellIds));
                    if (columns.Contains("nodeb_id_cell_id")) lookupClauses.Add(InLookup(alias, "nodeb_id_cell_id", "node_b_cell_id", lookupNodeBCellIds.Count > 0 ? lookupNodeBCellIds : lookupCellIds));
                }
            }

            if (!string.IsNullOrWhiteSpace(lookupSector))
            {
                if (!string.IsNullOrWhiteSpace(sectorColumn))
                {
                    if (hasNodeLookup || hasCellLookup)
                        andClauses.Add(Eq(alias, sectorColumn, "sector_lookup"));
                    else
                        lookupClauses.Add(Eq(alias, sectorColumn, "sector_lookup"));
                }
                else if (!hasNodeLookup && !hasCellLookup)
                {
                    if (columns.Contains("cell_id")) lookupClauses.Add(Eq(alias, "cell_id", "sector_lookup"));
                }
            }

            if (lookupClauses.Count == 0)
                return "1=0";

            var whereParts = new List<string>();
            if (andClauses.Count > 0) whereParts.AddRange(andClauses);
            whereParts.Add($"({string.Join(" OR ", lookupClauses)})");
            return string.Join(" AND ", whereParts);
        }

        var optimizedWhere = BuildWhereClause("o", optimizedColumns, optimizedSectorColumn);
        if (selectedScenario.HasValue)
        {
            string? scenarioClause = null;
            if (optimizedColumns.Contains("public_scenario_id") && optimizedColumns.Contains("scenario_id"))
            {
                scenarioClause = "(o.`public_scenario_id` = @scenario_id OR (o.`public_scenario_id` IS NULL AND o.`scenario_id` = @scenario_id))";
            }
            else if (optimizedColumns.Contains("public_scenario_id"))
            {
                scenarioClause = "o.`public_scenario_id` = @scenario_id";
            }
            else if (optimizedColumns.Contains("scenario_id"))
            {
                scenarioClause = "o.`scenario_id` = @scenario_id";
            }

            if (!string.IsNullOrWhiteSpace(scenarioClause))
            {
                optimizedWhere = $"({optimizedWhere}) AND {scenarioClause}";
            }
        }
        var baselineWhere = BuildWhereClause("b", baselineColumns, baselineSectorColumn);

        var optimizedSectorSelect = !string.IsNullOrWhiteSpace(optimizedSectorColumn)
            ? $"o.`{optimizedSectorColumn}` AS sector_lookup"
            : "NULL AS sector_lookup";
        var baselineSectorSelect = !string.IsNullOrWhiteSpace(baselineSectorColumn)
            ? $"b.`{baselineSectorColumn}` AS sector_lookup"
            : "NULL AS sector_lookup";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
WITH optimized_source AS (
    SELECT 
        o.id, o.project_id, o.job_id, o.lat, o.lon,
        o.pred_rsrp, o.pred_rsrq, o.pred_sinr,
        o.node_b_id, o.cell_id, o.operator, o.created_at, o.site_id,
        {(string.IsNullOrWhiteSpace(optimizedCombinedColumn) ? "NULL" : $"o.`{optimizedCombinedColumn}`")} AS node_b_cell_id,
        {optimizedSectorSelect}
    FROM lte_prediction_optimised_results o
    WHERE {optimizedWhere}
    {optimizedPolygonFilter}
),
optimized_rows AS (
    SELECT *
    FROM (
        SELECT
            os.*,
            ROW_NUMBER() OVER (
                PARTITION BY
                    COALESCE(os.node_b_cell_id, ''),
                    COALESCE(os.node_b_id, ''),
                    COALESCE(os.cell_id, ''),
                    COALESCE(os.site_id, ''),
                    os.lat,
                    os.lon
                ORDER BY os.id DESC
            ) AS rn
        FROM optimized_source os
    ) ranked
    WHERE ranked.rn = 1
),
baseline_rows AS (
    SELECT 
        b.id, b.project_id, b.job_id, b.lat, b.lon,
        b.pred_rsrp, b.pred_rsrq, b.pred_sinr,
        b.node_b_id, b.cell_id, b.operator, b.created_at, b.site_id,
        {(string.IsNullOrWhiteSpace(baselineCombinedColumn) ? "NULL" : $"b.`{baselineCombinedColumn}`")} AS node_b_cell_id,
        {baselineSectorSelect}
    FROM lte_prediction_baseline_results b
    WHERE {baselineWhere}
    {baselinePolygonFilter}
)
SELECT 
    o.id, o.project_id, o.job_id, o.lat, o.lon,
    o.pred_rsrp, o.pred_rsrq, o.pred_sinr,
    o.node_b_id, o.cell_id, o.operator, o.created_at,
    o.site_id, o.node_b_cell_id, o.sector_lookup,
    'lte_prediction_optimised_results' AS source_table
FROM optimized_rows o

UNION ALL

SELECT
    b.id, b.project_id, b.job_id, b.lat, b.lon,
    b.pred_rsrp, b.pred_rsrq, b.pred_sinr,
    b.node_b_id, b.cell_id, b.operator, b.created_at,
    b.site_id, b.node_b_cell_id, b.sector_lookup,
    'lte_prediction_baseline_results' AS source_table
FROM baseline_rows b
WHERE NOT EXISTS (SELECT 1 FROM optimized_rows);";

        if (projectId.HasValue) Add(cmd, "@project_id", projectId.Value);
        if (polygonIds.Count > 0) Add(cmd, "@pid", projectId!.Value);
        AddPolygonIdsParameters(cmd, polygonIds);
        for (var i = 0; i < lookupNodeBIds.Count; i++) Add(cmd, $"@node_b_id_{i}", lookupNodeBIds[i]);
        for (var i = 0; i < lookupCellIds.Count; i++) Add(cmd, $"@cell_id_{i}", lookupCellIds[i]);
        for (var i = 0; i < lookupNodeBCellIds.Count; i++) Add(cmd, $"@node_b_cell_id_{i}", lookupNodeBCellIds[i]);
        if (!string.IsNullOrWhiteSpace(lookupSector)) Add(cmd, "@sector_lookup", lookupSector);
        if (selectedScenario.HasValue) Add(cmd, "@scenario_id", selectedScenario.Value);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);

            rows.Add(row);
        }

        var response = new
        {
            Status = 1,
            ProjectIdFiltered = projectId,
            NodeBIdFiltered = trimmedNodeBId,
            CellIdFiltered = trimmedCellId,
            NodeBCellIdFiltered = combinedNodeBCellId,
            NodeBIdLookupValues = lookupNodeBIds,
            CellIdLookupValues = lookupCellIds,
            NodeBCellIdLookupValues = lookupNodeBCellIds,
            SectorFiltered = lookupSector,
            ScenarioFiltered = selectedScenario,
            Count = rows.Count,
            Data = rows
        };
        return Ok(response);
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error fetching site prediction optimised data: " + SafeException.Get(ex) });
    }
}

// Keep this helper method right below the API!
private double CalculateSingleStat(List<double> values, string statType)
{
    values.Sort();
    int count = values.Count;

    switch (statType)
    {
        case "median":
            double median = (count % 2 == 0)
                ? (values[(count / 2) - 1] + values[count / 2]) / 2.0
                : values[count / 2];
            return Math.Round(median, 2);

        case "mode":
            double? mode = values
                .GroupBy(v => Math.Round(v, 1))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;
            return mode ?? Math.Round(values.Average(), 2);

        case "best":
            return Math.Round(values.Max(), 2);

        case "worst":
            return Math.Round(values.Min(), 2);

        case "avg":
        case "mean":
        default:
            return Math.Round(values.Average(), 2);
    }
}
// Yeh helper function ab sirf wahi calculate karega jo user ne maanga hai

// Is class ko apne controller ke last mein add kar lijiye (helper method ka return type define karne ke liye)
public class LocationStats
{
    public int SampleCount { get; set; }
    public double Average { get; set; }
    public double Mean { get; set; }
    public double Median { get; set; }
    public double? Mode { get; set; }
    public double Best { get; set; }
    public double Worst { get; set; }
}






        // =========================================================
        // ==================== local helpers ======================
        // =========================================================

        private static void Add(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static int? ToInt(string s)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : null;
        }

        private static double? ToDouble(string s)
        {
            return double.TryParse(
                s,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var v
            )
                ? v
                : null;
        }

        private static string[] SplitCsv(string input, int expect)
        {
            var list = new List<string>(expect);
            var sb = new StringBuilder();
            bool inQ = false;

            for (int i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (ch == '"')
                {
                    if (inQ && i + 1 < input.Length && input[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQ = !inQ;
                    }
                }
                else if (ch == ',' && !inQ)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }


//nbdbsjcjd

//hello
            list.Add(sb.ToString());
            return list.ToArray();
        }
    }
}
