using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
using Newtonsoft.Json.Linq;
using SignalTracker.Helper;
using SignalTracker.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using System.Web;
using System.Security.Claims;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [Route("Admin")]
    [Authorize]
    public class AdminController : BaseController
    {
        private readonly ApplicationDbContext db;
        private readonly CommonFunction cf;
        private readonly IMemoryCache _cache; // Injected for caching

        private readonly UserScopeService _userScope;
        private readonly RedisService _redis;
        // Cache for index-existence check to avoid hitting INFORMATION_SCHEMA repeatedly
        private static bool? _hasSessionUserIndex;
        private string? m_alpha_long;

private int GetTargetCompanyId(int? explicitCompanyId)
{
        return _userScope.GetTargetCompanyId(User, explicitCompanyId);
}

private int GetCurrentUserId()
{
        return _userScope.GetCurrentUserId(User);
}

private bool UseCurrentUserScope(int targetCompanyId, int currentUserId)
{
        return !_userScope.IsSuperAdmin(User) && targetCompanyId == 0 && currentUserId > 0;
}

        // RSRP Threshold for Good/Bad classification in Indoor/Polygon views
        private const double RsrpThreshold = -90.0;

        // common timeout for heavy LINQ queries (AvgRsrpV2, AvgRsrqV2, etc.)
        private const int HeavyQueryTimeoutSeconds = 180;

        public AdminController(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor, IMemoryCache cache, RedisService redis, UserScopeService userScope)

        {
            db = context;
            cf = new CommonFunction(context, httpContextAccessor);
            _cache = cache; // Initialized
            _redis = redis;
            _userScope = userScope;
        }




        [HttpGet("redis/keys")]
        public async Task<IActionResult> GetRedisKeys(
                    [FromQuery] string pattern = "*",
                    [FromQuery] int limit = 500)
        {
            var keys = await _redis.GetKeysAsync(pattern, limit);

            return Ok(new
            {
                count = keys.Count,
                keys
            });
        }

        // ---------------- REDIS KEY DETAILS ----------------
        [HttpGet("redis/key-info")]
        public async Task<IActionResult> GetKeyInfo([FromQuery] string key)
        {
            var exists = await _redis.GetStringAsync(key) != null;
            var ttl = await _redis.GetTtlAsync(key);

            return Ok(new
            {
                key,
                exists,
                ttlSeconds = ttl?.TotalSeconds   // ? FIXED (not method group)
            });
        }

        // ---------------- EXTEND TTL ----------------
        [HttpPost("redis/extend-ttl")]
        public async Task<IActionResult> ExtendTtl(
            [FromQuery] string key,
            [FromQuery] int seconds = 300)
        {
            var success = await _redis.ExtendTtlAsync(key, seconds);

            return Ok(new
            {
                key,
                extendedBySeconds = seconds,
                success
            });
        }

        // ---------------- DELETE KEY ----------------
        [HttpDelete("redis/delete")]
        public async Task<IActionResult> DeleteKey([FromQuery] string key)
        {
            var deleted = await _redis.DeleteAsync(key);

            return Ok(new
            {
                key,
                deleted
            });
        }

        // ---------------- FLUSH ALL ----------------
        [HttpPost("redis/flush")]
        public async Task<IActionResult> FlushRedis()
        {
            var success = await _redis.FlushAllAsync();

            return Ok(new
            {
                success
            });
        }

        // ========================= BASIC VIEWS =========================
        [HttpGet("Index")]
        public IActionResult Index()
        {
            if (!cf.SessionCheck())
                return RedirectToAction("Index", "Home");
            return View();
        }

        [HttpGet("DbDiagnostics")]
        public async Task<IActionResult> DbDiagnostics()
        {
            var conn = db.Database.GetDbConnection();

            var claimCountry = User.FindFirst("country_code")?.Value;
            var sessionCountry = HttpContext.Session.GetString("country_code");
            var userTypeClaim = User.FindFirst("UserTypeId")?.Value;
            var companyClaim = User.FindFirst("CompanyId")?.Value;

            int users = 0;
            int sessions = 0;
            int logs = 0;

            try
            {
                users = await db.tbl_user.AsNoTracking().CountAsync();
                sessions = await db.tbl_session.AsNoTracking().CountAsync();
                logs = await db.tbl_network_log.AsNoTracking().CountAsync();
            }
            catch
            {
                // Keep diagnostics response resilient even if one table is missing/inaccessible.
            }

            return Ok(new
            {
                Status = 1,
                Db = new
                {
                    conn.DataSource,
                    conn.Database
                },
                Auth = new
                {
                    IsAuthenticated = User.Identity?.IsAuthenticated ?? false,
                    claimCountry,
                    sessionCountry,
                    userTypeClaim,
                    companyClaim
                },
                Counts = new
                {
                    tbl_user = users,
                    tbl_session = sessions,
                    tbl_network_log = logs
                }
            });
        }

        [HttpGet("Dashboard")]
        public IActionResult Dashboard()
        {
            if (!IsAngularRequest() || !cf.SessionCheck())
                return RedirectToAction("Index", "Home");

            ViewBag.UserType = cf.UserType;
            return View();
        }

        // ========================= SHARED HELPERS =========================

        private async Task<bool> MySqlIndexExistsAsync(string table, string indexName)
        {
            // Avoid repeated INFORMATION_SCHEMA hits
            if (table == "tbl_session" && indexName == "user_id" && _hasSessionUserIndex.HasValue)
                return _hasSessionUserIndex.Value;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;
            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = @table
                      AND INDEX_NAME = @index";

                Add(cmd, "@table", table);
                Add(cmd, "@index", indexName);

                var result = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                var exists = result > 0;

                if (table == "tbl_session" && indexName == "user_id")
                    _hasSessionUserIndex = exists;

                return exists;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        private async Task<int> CountDistinctUsersWithIndexHintIfAvailableAsync()
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            if (await MySqlIndexExistsAsync("tbl_session", "user_id"))
            {
                try
                {
                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync();
                        shouldClose = true;
                    }
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(DISTINCT `user_id`) FROM `tbl_session` USE INDEX (`user_id`);";
                    var result = await cmd.ExecuteScalarAsync();
                    return Convert.ToInt32(result);
                }
                catch
                {
                    // Fallback below
                }
                finally
                {
                    if (shouldClose && conn.State == ConnectionState.Open)
                        await conn.CloseAsync();
                }
            }

            // Fallback to LINQ if index check/hint fails
            return await db.tbl_session
                .AsNoTracking()
                .Select(s => s.user_id)
                .Distinct()
                .CountAsync();
        }

        private static IQueryable<tbl_network_log> FilterByNetworkType(IQueryable<tbl_network_log> q, string networkType)
        {
            if (string.IsNullOrWhiteSpace(networkType) || networkType.Equals("All", StringComparison.OrdinalIgnoreCase))
                return q;

            var t = networkType.Trim();
            if (t.Equals("5G", StringComparison.OrdinalIgnoreCase)) return q.Where(n => n.network != null && EF.Functions.Like(n.network, "%5G%"));
            if (t.Equals("4G", StringComparison.OrdinalIgnoreCase)) return q.Where(n => n.network != null && EF.Functions.Like(n.network, "%4G%"));
            if (t.Equals("3G", StringComparison.OrdinalIgnoreCase)) return q.Where(n => n.network != null && EF.Functions.Like(n.network, "%3G%"));
            if (t.Equals("2G", StringComparison.OrdinalIgnoreCase)) return q.Where(n => n.network != null && EF.Functions.Like(n.network, "%2G%"));
            return q;
        }

        private static string NormalizeNetworkType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim().Trim('{', '}');
            if (s.Contains("|", StringComparison.Ordinal)) return null;
            var allowed = new[] { "2G", "3G", "4G", "5G", "All" };
            return allowed.FirstOrDefault(a => a.Equals(s, StringComparison.OrdinalIgnoreCase));
        }

        private static void Add(DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private bool IsRedisReady => _redis != null && _redis.IsConnected;

        private static string NormalizeCacheSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "all";

            return value.Trim()
                .ToLowerInvariant()
                .Replace(":", "_")
                .Replace(" ", "_");
        }

        private async Task<T?> TryGetCachedObjectAsync<T>(string cacheKey) where T : class
        {
            if (!IsRedisReady)
                return null;

            try
            {
                return await _redis.GetObjectAsync<T>(cacheKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Redis read error [{cacheKey}]: {ex.Message}");
                return null;
            }
        }

        private async Task CacheObjectAsync<T>(string cacheKey, T value, int ttlSeconds) where T : class
        {
            if (!IsRedisReady || value == null)
                return;

            try
            {
                await _redis.SetObjectAsync(cacheKey, value, ttlSeconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Redis write error [{cacheKey}]: {ex.Message}");
            }
        }

        private async Task<string?> TryGetCachedStringAsync(string cacheKey)
        {
            if (!IsRedisReady)
                return null;

            try
            {
                return await _redis.GetStringAsync(cacheKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Redis read error [{cacheKey}]: {ex.Message}");
                return null;
            }
        }

        private async Task CacheStringAsync(string cacheKey, string value, int ttlSeconds)
        {
            if (!IsRedisReady)
                return;

            try
            {
                await _redis.SetStringAsync(cacheKey, value, ttlSeconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Redis write error [{cacheKey}]: {ex.Message}");
            }
        }

        private async Task<long?> TryGetCachedLongAsync(string cacheKey)
        {
            var cached = await TryGetCachedStringAsync(cacheKey);
            if (string.IsNullOrWhiteSpace(cached))
                return null;

            return long.TryParse(cached, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private Task CacheLongAsync(string cacheKey, long value, int ttlSeconds)
        {
            return CacheStringAsync(cacheKey, value.ToString(CultureInfo.InvariantCulture), ttlSeconds);
        }

        private async Task InvalidateCalculatedCachesAsync()
        {
            if (!IsRedisReady)
                return;

            var patterns = new[]
            {
                "alllogs:*",
                "networklog:v2:*",
                "networklog:v3:*",
                "mapview:*",
                "daterangelog:*",
                "latlon:dist:*",
                "n78_simple_kpi:*",
                "n78_neighbours:*",
                "opcoverage:*",
                "opquality:*",
                "polygonpoints:*",
                "polygongoodbad:*",
                "DashboardTotalsV2:*",
                "NetDur:*",
                "MonthlySamples:*",
                "SessionsDateRange:*",
                "sessions:list:*",
                "sesstechmin:*",
                "indoorcount:*",
                "outdoorcount:*",
                "indoorkpis:*",
                "outdoorkpis:*",
                "indoorbadcount:*",
                "indoorgoodcount:*",
                "outdoorbadcount:*",
                "outdoorgoodcount:*",
                "indoorbadlogs:*",
                "indoorgoodlogs:*",
                "outdoorbadlogs:*",
                "outdoorgoodlogs:*",
                "indoorallsessionlogs:*",
                "indoorallsessionlogspaged:*",
                "OpSamples:*",
                "OpAvgTpt10:*",
                "netdist:*",
                "AvgRsrp:*",
                "AvgRsrq:*",
                "AvgSinr:*",
                "AvgMos:*",
                "AvgJitter:*",
                "AvgLatency:*",
                "AvgPacketLoss:*",
                "AvgDlTpt:*",
                "AvgUlTpt:*",
                "BandDist:*",
                "HandsetDist:MakeOnly:*",
                "op-indoor-outdoor-avg:*",
                "boxplot:v7:*",
                "AppKPIs:*",
                "OperatorsList:*",
                "NetworksList:*",
                "users:list:*",
                "users:by-id:*",
                "coverageholes:*",
                "netdur:*"
            };

            foreach (var pattern in patterns)
            {
                try
                {
                    await _redis.DeleteByPatternAsync(pattern);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" Redis invalidation error [{pattern}]: {ex.Message}");
                }
            }
        }


        /// <summary>
        /// Execute a LINQ query with an increased command timeout, then restore the old timeout.
        /// Use this helper for queries that can scan / aggregate a large part of tbl_network_log.
        /// </summary>
        private async Task<List<T>> ExecuteHeavyQueryAsync<T>(IQueryable<T> query)
        {
            var previousTimeout = db.Database.GetCommandTimeout(); // int?
            try
            {
                db.Database.SetCommandTimeout(HeavyQueryTimeoutSeconds);
                return await query.ToListAsync();
            }
            finally
            {
                db.Database.SetCommandTimeout(previousTimeout);
            }
        }

        // ========================= REACT DASHBOARD MAIN API =========================
        // This endpoint is now optimized to only fetch the summary totals (cached)
        // The rest of the dashboard graphs should use the dedicated V2 APIs (MonthlySamplesV2, AvgRsrpV2, etc.)

        // ========================= OTHER EXISTING ACTIONS (unchanged logically) =========================

        // Retained for backward compatibility, no changes applied.

        // Retained for backward compatibility, no changes applied.

        // Retained for simplicity, as per the original structure

        #region

        [HttpGet("GetUsers")]
        public async Task<JsonResult> GetUsers(
            int company_id = 0,
            string UserName = null,
            string Email = null,
            string Mobile = null,
            int? Status = null,
            string CompanyName = null)
        {
            var message = new ReturnAPIResponse();
            try
            {
                if (User?.Identity?.IsAuthenticated != true)
                {
                    message.Status = 0;
                    message.Message = "Unauthorized access.";
                    return Json(message);
                }

                bool isSuperAdmin = _userScope.IsSuperAdmin(User);
                int targetCompanyId = GetTargetCompanyId(company_id > 0 ? company_id : null);
                if (targetCompanyId == 0 && !isSuperAdmin)
                {
                    message.Status = 0;
                    message.Message = "Unauthorized. Unable to resolve Company Context.";
                    return Json(message);
                }

                var cacheKey = $"users:list:{targetCompanyId}:{NormalizeCacheSegment(UserName)}:{NormalizeCacheSegment(Email)}:{NormalizeCacheSegment(Mobile)}:{(Status.HasValue ? Status.Value.ToString(CultureInfo.InvariantCulture) : "all")}:{NormalizeCacheSegment(CompanyName)}";
                var cached = await TryGetCachedObjectAsync<ReturnAPIResponse>(cacheKey);
                if (cached != null)
                    return Json(cached);

                var query = from u in db.tbl_user.AsNoTracking()
                            join c in db.tbl_company.AsNoTracking() on u.company_id equals c.id into companyJoin
                            from company in companyJoin.DefaultIfEmpty()
                            select new
                            {
                                User = u,
                                CompanyName = company != null ? company.company_name : null
                            };

                // Non-super admins are always locked to their own company.
                // Super admins can optionally request a specific company via company_id.
                if (targetCompanyId > 0)
                {
                    query = query.Where(x => x.User.company_id == targetCompanyId);
                }

                if (!string.IsNullOrWhiteSpace(UserName))
                    query = query.Where(x => EF.Functions.Like(x.User.name, $"%{UserName}%"));
                if (!string.IsNullOrWhiteSpace(Email))
                    query = query.Where(x => EF.Functions.Like(x.User.email, $"%{Email}%"));
                if (!string.IsNullOrWhiteSpace(Mobile))
                    query = query.Where(x => EF.Functions.Like(x.User.mobile, $"%{Mobile}%"));
                if (Status.HasValue)
                    query = query.Where(x => x.User.isactive == Status.Value);
                if (!string.IsNullOrWhiteSpace(CompanyName))
                    query = query.Where(x => x.CompanyName != null && EF.Functions.Like(x.CompanyName, $"%{CompanyName}%"));

                // Latest license code per company from issued licenses
                var latestLicenseCodes = db.tbl_company_user_license_issued
                    .AsNoTracking()
                    .GroupBy(h => h.tbl_company_id)
                    .Select(g => new
                    {
                        CompanyId = g.Key,
                        LicenseCode = g.OrderByDescending(h => h.created_on)
                                       .Select(h => h.license_code)
                                       .FirstOrDefault()
                    });

                var result = await query
                    .OrderBy(x => x.User.name)
                    .Select(x => new
                    {
                        license_code = latestLicenseCodes
                            .Where(l => l.CompanyId == x.User.company_id)
                            .Select(l => l.LicenseCode)
                            .FirstOrDefault(),
                        ob_user = new tbl_user
                        {
                            id = x.User.id,
                            uid = x.User.uid,
                            name = x.User.name,
                            password = !string.IsNullOrEmpty(x.User.password) ? "***************" : null,
                            email = x.User.email,
                            make = x.User.make,
                            model = x.User.model,
                            os = x.User.os,
                            operator_name = x.User.operator_name,
                            company_id = x.User.company_id,
                            mobile = x.User.mobile,
                            isactive = x.User.isactive,
                            m_user_type_id = x.User.m_user_type_id,
                            last_login = x.User.last_login,
                            date_created = x.User.date_created,
                            device_id = x.User.device_id,
                            gcm_id = x.User.gcm_id
                        },
                        user_id = x.User.id,
                        user_name = x.User.name,
                        user_email = x.User.email,
                        user_mobile = x.User.mobile,
                        user_isactive = x.User.isactive,
                        m_user_type_id = x.User.m_user_type_id,
                        company_id = x.User.company_id,
                        company_name = x.CompanyName,
                        created_on = x.User.date_created
                    })
                    .ToListAsync();

                message.Data = result;
                message.Status = 1;
                message.Message = "Success";
                await CacheObjectAsync(cacheKey, message, 300);
            }
            catch (Exception ex)
            {
                try { new Writelog(db).write_exception_log(0, "AdminController", "GetUsers", DateTime.Now, ex); } catch { }
                message.Status = 0;
                message.Message = "Error: " + ex.Message;
            }
            return Json(message);
        }
        #endregion


        #region 

        [HttpGet("GetUserById")]
        public async Task<JsonResult> GetUserById(string token, int UserID)
        {
            var message = new ReturnAPIResponse();
            try
            {
                cf.SessionCheck();
                message.Status = 1; // cf.MatchToken(token);
                var cacheKey = $"users:by-id:{UserID}";
                var cached = await TryGetCachedObjectAsync<ReturnAPIResponse>(cacheKey);
                if (cached != null)
                    return Json(cached);

                if (message.Status == 1)
                {
                    var user = await db.tbl_user.AsNoTracking()
                        .Where(a => a.isactive == 1 && a.id == UserID)
                        .Select(u => new tbl_user
                        {
                            id = u.id,
                            uid = u.uid,
                            token = u.token,
                            name = u.name,
                            password = !string.IsNullOrEmpty(u.password) ? new string('*', 15) : null,
                            email = u.email,
                            make = u.make,
                            model = u.model,
                            os = u.os,
                            operator_name = u.operator_name,
                            company_id = u.company_id,
                            mobile = u.mobile,
                            isactive = u.isactive,
                            m_user_type_id = u.m_user_type_id,
                            last_login = u.last_login,
                            date_created = u.date_created,
                            device_id = u.device_id,
                            gcm_id = u.gcm_id
                        })
                        .FirstOrDefaultAsync();

                    message.Data = user;
                    await CacheObjectAsync(cacheKey, message, 300);
                }
            }
            catch (Exception ex)
            {
                try { new Writelog(db).write_exception_log(0, "AdminHomeController", "GetUserById", DateTime.Now, ex); } catch { }
            }
            return Json(message);
        }

        public static string DecodeFrom64(string encodedData)
        {
            var encoder = new System.Text.UTF8Encoding();
            var utf8Decode = encoder.GetDecoder();
            byte[] todecode_byte = Convert.FromBase64String(encodedData);
            int charCount = utf8Decode.GetCharCount(todecode_byte, 0, todecode_byte.Length);
            char[] decoded_char = new char[charCount];
            utf8Decode.GetChars(todecode_byte, 0, todecode_byte.Length, decoded_char, 0);
            return new string(decoded_char);
        }

        public static string EncodePasswordToBase64(string password)
        {
            try
            {
                byte[] encData_byte = System.Text.Encoding.UTF8.GetBytes(password);
                return Convert.ToBase64String(encData_byte);
            }
            catch (Exception ex)
            {
                throw new Exception("Error in base64Encode " + ex.Message);
            }
        }

        [HttpPost("SaveUserDetails")]
        public async Task<JsonResult> SaveUserDetails([FromForm] IFormCollection values, tbl_user users, string token1, string ip)
        {
            var message = new ReturnAPIResponse();
            try
            {
                cf.SessionCheck();
                message.Status = 1; // cf.MatchToken(token1);

                if (message.Status == 1)
                {
                    users.name = HttpUtility.HtmlEncode(users.name);
                    users.email = HttpUtility.HtmlEncode(users.email);
                    users.mobile = HttpUtility.HtmlEncode(users.mobile);

                    if (users.id == 0)
                    {
                        var exists = await db.tbl_user.AsNoTracking().AnyAsync(a => a.email == users.email && a.isactive == 1);
                        if (!exists)
                        {
                            users.date_created = DateTime.Now;
                            users.isactive = 1;
                            db.tbl_user.Add(users);
                            await db.SaveChangesAsync();
                            message.Status = 1;
                            // Assuming DisplayMessage.UserDetailsSaved is a constant
                            message.Message = "User Details Saved";
                            await InvalidateCalculatedCachesAsync();
                        }
                        else
                        {
                            // Assuming DisplayMessage.UserExist is a constant
                            message.Message = "User Already Exists";
                        }
                    }
                    else
                    {
                        var getUser = await db.tbl_user.FirstOrDefaultAsync(a => a.id == users.id);
                        if (getUser != null)
                        {
                            getUser.name = users.name;
                            getUser.email = users.email;
                            getUser.mobile = users.mobile;
                            getUser.m_user_type_id = users.m_user_type_id;
                            db.Entry(getUser).State = EntityState.Modified;
                            await db.SaveChangesAsync();
                            message.Status = 2;
                            // Assuming DisplayMessage.UserDetailsUpdated is a constant
                            message.Message = "User Details Updated";
                            await InvalidateCalculatedCachesAsync();
                        }
                    }
                    message.token = ""; // cf.CreateToken(ip);
                }
            }
            catch (Exception ex)
            {
                message.Status = 0;
                // Assuming DisplayMessage.ErrorMessage is a constant
                message.Message = "Error Message" + " " + ex.Message;
            }
            return Json(message);
        }

        [HttpPost("GetUser")]
        public async Task<JsonResult> GetUser(int UserID, string token)
        {
            var message = new ReturnAPIResponse();
            try
            {
                cf.SessionCheck();
                message = cf.MatchToken(token);
                if (message.Status == 1)
                {
                    var user = await db.tbl_user.AsNoTracking()
                        .Where(a => a.id == UserID)
                        .Select(u => new tbl_user
                        {
                            id = u.id,
                            uid = u.uid,
                            token = u.token,
                            name = u.name,
                            password = "",
                            email = u.email,
                            make = u.make,
                            model = u.model,
                            os = u.os,
                            operator_name = u.operator_name,
                            company_id = u.company_id,
                            mobile = u.mobile,
                            isactive = u.isactive,
                            m_user_type_id = u.m_user_type_id,
                            last_login = u.last_login,
                            date_created = u.date_created,
                            device_id = u.device_id,
                            gcm_id = u.gcm_id
                        })
                        .FirstOrDefaultAsync();

                    message.Data = user;
                }
            }
            catch (Exception ex)
            {
                // Assuming DisplayMessage.ErrorMessage is a constant
                message.Message = "Error Message" + " " + ex.Message;
            }
            return Json(message);
        }

        [HttpPost("DeleteUser")]
        public async Task<IActionResult> DeleteUser([FromBody] DeleteUserRequest request)
        {
            var message = new ReturnAPIResponse();

            try
            {
                cf.SessionCheck();

                if (request == null || request.Id <= 0)
                {
                    message.Status = 0;
                    message.Message = "Invalid user id";
                    return Ok(message);
                }

                var user = await db.tbl_user
                                   .Where(x => x.id == request.Id && x.isactive != 2)
                                   .FirstOrDefaultAsync();

                if (user == null)
                {
                    message.Status = 0;
                    message.Message = "User not found or already deleted";
                    return Ok(message);
                }


                user.isactive = 2;

                db.tbl_user.Update(user);
                var rows = await db.SaveChangesAsync();

                if (rows > 0)
                {
                    message.Status = 1;
                    message.Message = "User deleted successfully";
                    message.token = cf.CreateToken(request.Ip);
                    await InvalidateCalculatedCachesAsync();
                }
                else
                {
                    message.Status = 0;
                    message.Message = "Delete failed";
                }
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = ex.Message;
            }

            return Ok(message);
        }

        [HttpPost("ActivateUser")]
        public async Task<IActionResult> ActivateUser([FromBody] DeleteUserRequest request)
        {
            var message = new ReturnAPIResponse();

            try
            {
                cf.SessionCheck();

                if (request == null || request.Id <= 0)
                {
                    message.Status = 0;
                    message.Message = "Invalid user id";
                    return Ok(message);
                }

                var user = await db.tbl_user
                                   .Where(x => x.id == request.Id)
                                   .FirstOrDefaultAsync();

                if (user == null)
                {
                    message.Status = 0;
                    message.Message = "User not found";
                    return Ok(message);
                }

                user.isactive = 1;
                db.tbl_user.Update(user);
                var rows = await db.SaveChangesAsync();

                if (rows > 0)
                {
                    message.Status = 1;
                    message.Message = "User activated successfully";
                    message.token = cf.CreateToken(request.Ip);
                    await InvalidateCalculatedCachesAsync();
                }
                else
                {
                    message.Status = 0;
                    message.Message = "Activation failed";
                }
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = ex.Message;
            }

            return Ok(message);
        }

        [HttpPost("InactivateUser")]
        public async Task<IActionResult> InactivateUser([FromBody] DeleteUserRequest request)
        {
            var message = new ReturnAPIResponse();

            try
            {
                cf.SessionCheck();

                if (request == null || request.Id <= 0)
                {
                    message.Status = 0;
                    message.Message = "Invalid user id";
                    return Ok(message);
                }

                var user = await db.tbl_user
                                   .Where(x => x.id == request.Id && x.isactive != 2)
                                   .FirstOrDefaultAsync();

                if (user == null)
                {
                    message.Status = 0;
                    message.Message = "User not found or already deleted";
                    return Ok(message);
                }

                user.isactive = 0;
                db.tbl_user.Update(user);
                var rows = await db.SaveChangesAsync();

                if (rows > 0)
                {
                    message.Status = 1;
                    message.Message = "User inactivated successfully";
                    message.token = cf.CreateToken(request.Ip);
                    await InvalidateCalculatedCachesAsync();
                }
                else
                {
                    message.Status = 0;
                    message.Message = "Inactivate failed";
                }
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = ex.Message;
            }

            return Ok(message);
        }

        [HttpPost("DeleteUserPermanent")]
        public async Task<IActionResult> DeleteUserPermanent([FromBody] DeleteUserRequest request)
        {
            var message = new ReturnAPIResponse();

            try
            {
                cf.SessionCheck();

                if (request == null || request.Id <= 0)
                {
                    message.Status = 0;
                    message.Message = "Invalid user id";
                    return Ok(message);
                }

                var user = await db.tbl_user
                                   .Where(x => x.id == request.Id)
                                   .FirstOrDefaultAsync();

                if (user == null)
                {
                    message.Status = 0;
                    message.Message = "User not found";
                    return Ok(message);
                }

                db.tbl_user.Remove(user);
                var rows = await db.SaveChangesAsync();

                if (rows > 0)
                {
                    message.Status = 1;
                    message.Message = "User permanently deleted";
                    message.token = cf.CreateToken(request.Ip);
                    await InvalidateCalculatedCachesAsync();
                }
                else
                {
                    message.Status = 0;
                    message.Message = "Permanent delete failed";
                }
            }
            catch (Exception ex)
            {
                message.Status = 0;
                message.Message = ex.Message;
            }

            return Ok(message);
        }
        public class DeleteUserRequest
        {
            public int Id { get; set; }
            public string Ip { get; set; }
        }


        [HttpPost("UserResetPassword")]
        public async Task<JsonResult> UserResetPassword(int userid, string newpwd, string captcha)
        {
            // Assuming ReturnMessage is a simple class for API responses
            ReturnMessage ret = new ReturnMessage();
            try
            {
                var getUser = await db.tbl_user.FirstOrDefaultAsync(a => a.id == userid);
                if (getUser != null)
                {
                    getUser.password = newpwd;
                    db.Entry(getUser).State = EntityState.Modified;
                    await db.SaveChangesAsync();
                    ret.Status = 1;
                    ret.Message = "Password has been reset successfully.";
                    await InvalidateCalculatedCachesAsync();
                }
                else
                {
                    ret.Status = 0;
                    ret.Message = "Invalid Request";
                }
            }
            catch (Exception ex)
            {
                ret.Status = 0;
                // Assuming DisplayMessage.ErrorMessage is a constant
                ret.Message = "Error Message" + " " + ex.Message;
            }
            return Json(ret);
        }

        [HttpPost("ChangePassword")]
        public async Task<JsonResult> ChangePassword(int userid, string oldpwd, string newpwd, string captcha)
        {
            // Assuming ReturnMessage is a simple class for API responses
            ReturnMessage ret = new ReturnMessage();
            try
            {
                // Assuming HttpContext is available via BaseController/Middleware
                if (HttpContext?.Session.GetString("CaptchaImageText") == captcha)
                {
                    var getUser = await db.tbl_user.FirstOrDefaultAsync(a => a.id == userid && a.password == oldpwd);
                    if (getUser != null)
                    {
                        getUser.password = newpwd;
                        db.Entry(getUser).State = EntityState.Modified;
                        await db.SaveChangesAsync();
                        ret.Status = 1;
                        await InvalidateCalculatedCachesAsync();
                    }
                    else
                    {
                        ret.Status = 0;
                        ret.Message = "Old password is wrong";
                    }
                }
                else
                {
                    ret.Status = 0;
                    ret.Message = "Invalid CAPTCHA Code !";
                }
            }
            catch (Exception ex)
            {
                ret.Status = 0;
                // Assuming DisplayMessage.ErrorMessage is a constant
                ret.Message = "Error Message" + " " + ex.Message;
            }
            return Json(ret);
        }
        #endregion

        #region Manage Sessions


        // Retained, simple query (NOT paged)
        // ========================================
        //  DTO FOR CACHING
        // ========================================
        public class NetworkLogItem
        {
            public int session_id { get; set; }
            public double lat { get; set; }
            public double lon { get; set; }
            public double? rsrp { get; set; }
            public double? rsrq { get; set; }
            public double? sinr { get; set; }
            public string network { get; set; }
            public DateTime? timestamp { get; set; }
        }

        public class PagedNetworkLogItem
        {
            public long id { get; set; }
            public long session_id { get; set; }
            public double? lat { get; set; }
            public double? lon { get; set; }
            public double? rsrp { get; set; }
            public double? rsrq { get; set; }
            public double? sinr { get; set; }
            public string network { get; set; }
            public DateTime? timestamp { get; set; }
        }

        public class PagedNetworkLogsResponse
        {
            public int Status { get; set; } = 1;
            public int PageNumber { get; set; }
            public int PageSize { get; set; }
            public long TotalCount { get; set; }
            public int TotalPages { get; set; }
            public bool HasMore { get; set; }
            public List<PagedNetworkLogItem> Items { get; set; } = new();
        }

        // ========================================
        //  MAIN ENDPOINT WITH REDIS CACHING
        // ========================================
        [HttpGet("GetAllNetworkLogs")]
        public async Task<JsonResult> GetAllNetworkLogs(
            [FromQuery] int? sessionId = null,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null,
            [FromQuery] int maxRows = 50000,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                int targetCompanyId = 0;
                bool isAuthorized = false;

                // =========================================================
                // SCENARIO 1: DID YOU PROVIDE A COMPANY ID?
                // =========================================================
                if (company_id.HasValue && company_id.Value > 0)
                {
                    // YES: Use the provided ID directly.
                    targetCompanyId = company_id.Value;
                    isAuthorized = true;
                }
                else
                {
                    // =========================================================
                    // SCENARIO 2: NO COMPANY ID? CHECK FOR TOKEN.
                    // =========================================================
                    string token = string.Empty;

                    // 1. Check Header
                    if (Request.Headers.ContainsKey("Authorization"))
                    {
                        token = Request.Headers["Authorization"].ToString();
                        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            token = token.Substring(7).Trim();
                    }
                    // 2. Check URL
                    else if (Request.Query.ContainsKey("token"))
                    {
                        token = Request.Query["token"].ToString();
                    }

                    // 3. Validate Token in DB
                    if (!string.IsNullOrEmpty(token))
                    {
                        var user = await db.tbl_user.AsNoTracking()
                            .Select(u => new { u.token, u.company_id, u.isactive })
                            .FirstOrDefaultAsync(u => u.token == token && u.isactive == 1);

                        if (user != null)
                        {
                            targetCompanyId = user.company_id ?? 0;
                            isAuthorized = true;
                        }
                    }
                }

                // =========================================================
                // FINAL CHECK: DO WE HAVE A VALID COMPANY?
                // =========================================================
                if (!isAuthorized || targetCompanyId == 0)
                {
                    return Json(new { Status = 0, Message = "Unauthorized. Please provide either a valid 'company_id' or a valid 'token'." });
                }

                // =========================================================
                // 3. BUILD CACHE KEY
                // =========================================================
                string sessionKey = sessionId?.ToString() ?? "all";
                string fromKey = fromDate?.ToString("yyyyMMdd") ?? "null";
                string toKey = toDate?.ToString("yyyyMMdd") ?? "null";

                // Cache key now includes the Resolved Company ID
                string cacheKey = $"alllogs:sql:{targetCompanyId}:{sessionKey}:{fromKey}:{toKey}:{maxRows}";

                // =========================================================
                // 4. CHECK REDIS CACHE
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cachedLogs = await _redis.GetObjectAsync<List<NetworkLogItem>>(cacheKey);
                        if (cachedLogs != null)
                        {
                            totalStopwatch.Stop();
                            Response.Headers["X-Cache"] = "HIT";
                            Response.Headers["X-Row-Count"] = cachedLogs.Count.ToString();
                            return Json(cachedLogs);
                        }
                    }
                    catch (Exception) { /* Ignore Redis errors */ }
                }

                // =========================================================
                // 5. EXECUTE RAW SQL (FILTER BY TARGET COMPANY)
                // =========================================================
                var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var logs = new List<NetworkLogItem>();

                var conn = db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                using (var cmd = conn.CreateCommand())
                {
                    string sql = @"
                SELECT 
                    l.session_id, l.lat, l.lon, l.rsrp, l.rsrq, l.sinr, l.network, l.timestamp
                FROM tbl_network_log l
                JOIN tbl_session s ON l.session_id = s.id
                JOIN tbl_user u ON s.user_id = u.id
                WHERE u.company_id = @companyId
                  AND l.lat IS NOT NULL 
                  AND l.lon IS NOT NULL";

                    // Filters
                    if (sessionId.HasValue) sql += " AND l.session_id = @sessionId";
                    if (fromDate.HasValue) sql += " AND l.timestamp >= @fromDate";
                    if (toDate.HasValue) sql += " AND l.timestamp < @toDate";

                    // Limit
                    sql += " ORDER BY l.timestamp DESC LIMIT @maxRows";

                    cmd.CommandText = sql;

                    // Parameters
                    var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);
                    var pMax = cmd.CreateParameter(); pMax.ParameterName = "@maxRows"; pMax.Value = Math.Min(maxRows, 100000); cmd.Parameters.Add(pMax);

                    if (sessionId.HasValue)
                    {
                        var p = cmd.CreateParameter(); p.ParameterName = "@sessionId"; p.Value = sessionId.Value; cmd.Parameters.Add(p);
                    }
                    if (fromDate.HasValue)
                    {
                        var p = cmd.CreateParameter(); p.ParameterName = "@fromDate"; p.Value = fromDate.Value; cmd.Parameters.Add(p);
                    }
                    if (toDate.HasValue)
                    {
                        var p = cmd.CreateParameter(); p.ParameterName = "@toDate"; p.Value = toDate.Value.AddDays(1); cmd.Parameters.Add(p);
                    }

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            logs.Add(new NetworkLogItem
                            {
                                session_id = reader.GetInt32(0),
                                lat = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                                lon = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                                rsrp = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                                rsrq = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                                sinr = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                                network = reader.IsDBNull(6) ? null : reader.GetString(6),
                                timestamp = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7)
                            });
                        }
                    }
                }

                dbStopwatch.Stop();

                // =========================================================
                // 6. CACHE & RETURN
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    await _redis.SetObjectAsync(cacheKey, logs, ttlSeconds: 300);
                }

                totalStopwatch.Stop();

                Response.Headers["X-Cache"] = "MISS";
                Response.Headers["X-Row-Count"] = logs.Count.ToString();
                Response.Headers["X-Db-Time-Ms"] = dbStopwatch.ElapsedMilliseconds.ToString();

                return Json(logs);
            }
            catch (Exception ex)
            {
                return Json(new { Status = 0, Message = "Error: " + ex.Message });
            }
        }// ========================================
         //  CACHE MANAGEMENT ENDPOINTS
         // ========================================

        // Clear cache endpoint
        [HttpDelete, Route("ClearAllLogsCache")]
        public async Task<JsonResult> ClearAllLogsCache(
            [FromQuery] int? sessionId = null,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null)
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    return Json(new { status = 0, message = "Redis not connected" });
                }

                long deleted = 0;

                if (sessionId.HasValue || fromDate.HasValue || toDate.HasValue)
                {
                    string sessionKey = sessionId?.ToString() ?? "*";
                    string fromKey = fromDate?.ToString("yyyyMMdd") ?? "*";
                    string toKey = toDate?.ToString("yyyyMMdd") ?? "*";

                    string pattern = $"alllogs:sql:*:{sessionKey}:{fromKey}:{toKey}:*";
                    deleted = await _redis.DeleteByPatternAsync(pattern);

                    Console.WriteLine($" Cleared {deleted} cache keys matching: {pattern}");
                }
                else
                {
                    deleted = await _redis.DeleteByPatternAsync("alllogs:*");
                    Console.WriteLine($" Cleared all logs caches");
                }

                return Json(new
                {
                    status = 1,
                    message = "All logs cache cleared successfully",
                    keysDeleted = deleted,
                    sessionId = sessionId,
                    fromDate = fromDate?.ToString("yyyy-MM-dd"),
                    toDate = toDate?.ToString("yyyy-MM-dd")
                });
            }
            catch (Exception ex)
            {
                return Json(new { status = 0, message = "Error: " + ex.Message });
            }
        }

        // Get cache statistics
        [HttpGet, Route("GetAllLogsCacheStats")]
        public async Task<JsonResult> GetAllLogsCacheStats()
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    return Json(new { status = 0, message = "Redis not connected" });
                }

                var keys = await _redis.GetKeysAsync("alllogs:*", count: 100);
                var cacheStats = new List<object>();

                foreach (var key in keys)
                {
                    var ttl = await _redis.GetTtlAsync(key);

                    cacheStats.Add(new
                    {
                        cacheKey = key,
                        ttlSeconds = ttl?.TotalSeconds,
                        expiresAt = ttl.HasValue
                            ? DateTime.UtcNow.Add(ttl.Value).ToString("yyyy-MM-dd HH:mm:ss")
                            : "Never"
                    });
                }

                return Json(new
                {
                    status = 1,
                    totalCacheEntries = keys.Count,
                    cacheEntries = cacheStats
                });
            }
            catch (Exception ex)
            {
                return Json(new { status = 0, message = "Error: " + ex.Message });
            }
        }

        // Warm up cache
        [HttpPost, Route("WarmUpAllLogsCache")]
        public async Task<JsonResult> WarmUpAllLogsCache(
            [FromQuery] int? sessionId = null,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null,
            [FromQuery] int maxRows = 50000)
        {
            try
            {
                await GetAllNetworkLogs(sessionId, fromDate, toDate, maxRows);

                return Json(new
                {
                    status = 1,
                    message = "Cache warmed up successfully",
                    sessionId = sessionId,
                    fromDate = fromDate?.ToString("yyyy-MM-dd"),
                    toDate = toDate?.ToString("yyyy-MM-dd"),
                    maxRows = maxRows
                });
            }
            catch (Exception ex)
            {
                return Json(new { status = 0, message = "Error: " + ex.Message });
            }
        }

        // NEW: paginated endpoint for network logs (pageNumber + pageSize)
        [HttpGet("GetAllNetworkLogsPaged")]
        public async Task<JsonResult> GetAllNetworkLogsPaged(
            int pageNumber = 1,
            int pageSize = 2000)
        {
            try
            {
                if (pageNumber < 1) pageNumber = 1;
                if (pageSize < 1) pageSize = 100;
                if (pageSize > 10000) pageSize = 10000;

                var cacheKey = $"alllogs:paged:{pageNumber}:{pageSize}";
                var cached = await TryGetCachedObjectAsync<PagedNetworkLogsResponse>(cacheKey);
                if (cached != null)
                    return Json(cached);

                var query = db.tbl_network_log
                    .AsNoTracking()
                    .Where(log => log.lat != null && log.lon != null);

                var totalCount = await query.CountAsync();

                var skip = (pageNumber - 1) * pageSize;

                var items = await query
                    .OrderBy(log => log.id)
                    .Skip(skip)
                    .Take(pageSize)
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
                        log.timestamp
                    })
                    .ToListAsync();

                var typedItems = items.Select(item => new PagedNetworkLogItem
                {
                    id = Convert.ToInt64(item.id),
                    session_id = Convert.ToInt64(item.session_id),
                    lat = item.lat,
                    lon = item.lon,
                    rsrp = item.rsrp,
                    rsrq = item.rsrq,
                    sinr = item.sinr,
                    network = item.network,
                    timestamp = item.timestamp
                }).ToList();

                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

                var response = new PagedNetworkLogsResponse
                {
                    Status = 1,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    TotalPages = totalPages,
                    HasMore = pageNumber < totalPages,
                    Items = typedItems
                };

                await CacheObjectAsync(cacheKey, response, 300);

                return Json(response);
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500;
                return Json(new
                {
                    Status = 0,
                    Message = "An error occurred on the server: " + ex.Message
                });
            }
        }

        // Retained, simple query
        [HttpGet("GetOperatorCoverageRanking")]
public async Task<IActionResult> GetOperatorCoverageRanking(
    double min = -95,
    double max = 0,
    [FromQuery] DateTime? from = null,
    [FromQuery] DateTime? to = null,
    [FromQuery] int? company_id = null)
{
    try
    {
        int targetCompanyId = GetTargetCompanyId(company_id);
        int currentUserId = GetCurrentUserId();
        bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
        if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope)
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
        }

        var effectiveTo = to ?? DateTime.UtcNow;
        var effectiveFrom = from ?? effectiveTo.AddDays(-14);
        if (effectiveFrom > effectiveTo)
        {
            var tmp = effectiveFrom;
            effectiveFrom = effectiveTo;
            effectiveTo = tmp;
        }

        string cacheKey = $"opcoverage:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{min}:{max}:{effectiveFrom:yyyyMMdd}:{effectiveTo:yyyyMMdd}";
        if (_redis != null && _redis.IsConnected)
        {
            var cached = await _redis.GetObjectAsync<List<OperatorQualityItem>>(cacheKey);
            if (cached != null)
            {
                return Ok(cached);
            }
        }

        var toExclusive = effectiveTo.AddDays(1);
        var result = new List<OperatorQualityItem>();

        if (targetCompanyId == 0 && !useUserScope)
        {
            result = await db.tbl_network_log
                .AsNoTracking()
                .Where(l => l.rsrp != null
                         && l.rsrp >= min
                         && l.rsrp <= max
                         && l.timestamp.HasValue
                         && l.timestamp.Value >= effectiveFrom
                         && l.timestamp.Value < toExclusive
                         && !string.IsNullOrEmpty(l.m_alpha_long))
                .GroupBy(l => l.m_alpha_long)
                .Select(g => new OperatorQualityItem
                {
                    name = g.Key,
                    count = g.Count()
                })
                .OrderByDescending(x => x.count)
                .ToListAsync();
        }
        else
        {
            db.Database.SetCommandTimeout(90);

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        n.m_alpha_long AS name,
                        COUNT(*) AS count
                    FROM tbl_user u
                    STRAIGHT_JOIN tbl_session s ON s.user_id = u.id
                    STRAIGHT_JOIN tbl_network_log n ON n.session_id = s.id
                    WHERE (__SCOPE_CLAUSE__)
                      AND n.rsrp IS NOT NULL
                      AND n.rsrp >= @min
                      AND n.rsrp <= @max
                      AND n.timestamp >= @fromDate
                      AND n.timestamp < @toDate
                      AND n.m_alpha_long IS NOT NULL
                      AND n.m_alpha_long <> ''
                    GROUP BY n.m_alpha_long
                    ORDER BY count DESC;";
                cmd.CommandText = cmd.CommandText.Replace(
                    "__SCOPE_CLAUSE__",
                    useUserScope ? "s.user_id = @userId" : "u.company_id = @companyId");
                cmd.CommandTimeout = 90;

                if (useUserScope)
                {
                    var pUser = cmd.CreateParameter(); pUser.ParameterName = "@userId"; pUser.Value = currentUserId; cmd.Parameters.Add(pUser);
                }
                else
                {
                    var pCompany = cmd.CreateParameter(); pCompany.ParameterName = "@companyId"; pCompany.Value = targetCompanyId; cmd.Parameters.Add(pCompany);
                }
                var pMin = cmd.CreateParameter(); pMin.ParameterName = "@min"; pMin.Value = min; cmd.Parameters.Add(pMin);
                var pMax = cmd.CreateParameter(); pMax.ParameterName = "@max"; pMax.Value = max; cmd.Parameters.Add(pMax);
                var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@fromDate"; pFrom.Value = effectiveFrom; cmd.Parameters.Add(pFrom);
                var pTo = cmd.CreateParameter(); pTo.ParameterName = "@toDate"; pTo.Value = toExclusive; cmd.Parameters.Add(pTo);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new OperatorQualityItem
                    {
                        name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                        count = reader.IsDBNull(1) ? 0 : reader.GetInt32(1)
                    });
                }
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        if (_redis != null && _redis.IsConnected && result.Count > 0)
        {
            await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
        }

        return Ok(result);
    }
    catch (Exception ex)
    {
        try { new Writelog(db).write_exception_log(0, "AdminController", "GetOperatorCoverageRanking", DateTime.Now, ex); } catch { }
        return StatusCode(500, new { Message = "Error: " + ex.Message });
    }
}

        // Retained, simple query
        // ========================================
        // DTO FOR CACHING
        // ========================================
        public class OperatorQualityItem
        {
            public string name { get; set; }
            public int count { get; set; }
        }

        // ========================================
        //  MAIN ENDPOINT WITH REDIS CACHING
        // ========================================
       [HttpGet("GetOperatorQualityRanking")]
public async Task<IActionResult> GetOperatorQualityRanking(
    double min = -10,
    double max = 0,
    [FromQuery] DateTime? from = null,
    [FromQuery] DateTime? to = null,
    [FromQuery] int? company_id = null)
{
    var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        // 1. RESOLVE COMPANY ID
        int targetCompanyId = GetTargetCompanyId(company_id);
        int currentUserId = GetCurrentUserId();
        bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);

        if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope)
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
        }

        var effectiveTo = to ?? DateTime.UtcNow;
        var effectiveFrom = from ?? effectiveTo.AddDays(-14);
        if (effectiveFrom > effectiveTo)
        {
            var tmp = effectiveFrom;
            effectiveFrom = effectiveTo;
            effectiveTo = tmp;
        }

        // 2. CACHE KEY
        string cacheKey = $"opquality:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{min}:{max}:{effectiveFrom:yyyyMMdd}:{effectiveTo:yyyyMMdd}";

        // 3. TRY REDIS (Standard Cache Logic)
        if (_redis != null && _redis.IsConnected)
        {
            var cached = await _redis.GetObjectAsync<List<OperatorQualityItem>>(cacheKey);
            if (cached != null)
            {
                totalStopwatch.Stop();
                Response.Headers["X-Cache"] = "HIT";
                return Ok(cached);
            }
        }

        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<OperatorQualityItem> result = new List<OperatorQualityItem>();

        // =================================================================
        //  OPTIMIZATION CORE: SPLIT EXECUTION PATHS
        // =================================================================
        
        // 4A. FAST PATH: SUPER ADMIN (ALL DATA)
        // If CompanyID is 0, we DO NOT need to join Session or User tables.
        // We just aggregate the Log table directly. This removes massive overhead.
        if (targetCompanyId == 0 && !useUserScope)
        {
            result = await db.tbl_network_log
                .AsNoTracking()
                .Where(l => l.rsrq != null 
                         && l.rsrq >= min 
                         && l.rsrq <= max 
                         && l.timestamp.HasValue
                         && l.timestamp.Value >= effectiveFrom
                         && l.timestamp.Value < effectiveTo.AddDays(1)
                         && !string.IsNullOrEmpty(l.m_alpha_long))
                .GroupBy(l => l.m_alpha_long)
                .Select(g => new OperatorQualityItem
                {
                    name = g.Key,
                    count = g.Count()
                })
                .OrderByDescending(x => x.count)
                .ToListAsync();
        }
        // 4B. OPTIMIZED PATH: SPECIFIC COMPANY
        // We use Raw SQL to force the most efficient join order.
        // We set a Timeout specifically for this query.
        else
        {
            // Set a strict command timeout for this heavy query
            db.Database.SetCommandTimeout(60); 

            // Raw SQL allows the DB optimizer to see distinct constants instead of variables inside OR clauses
            string sql = @"
                SELECT 
                    n.m_alpha_long AS name,
                    COUNT(*) AS count
                FROM tbl_session s
                JOIN tbl_user u ON s.user_id = u.id
                JOIN tbl_network_log n ON n.session_id = s.id
                WHERE __SCOPE_CLAUSE__
                  AND n.rsrq >= {1} 
                  AND n.rsrq <= {2}
                  AND n.timestamp >= {3}
                  AND n.timestamp < DATE_ADD({4}, INTERVAL 1 DAY)
                  AND n.m_alpha_long IS NOT NULL 
                  AND n.m_alpha_long <> ''
                GROUP BY n.m_alpha_long
                ORDER BY count DESC";

            // Execute Raw SQL
            // Note: We are projecting into a temporary DTO or using an anonymous type via FromSql if Entity allows, 
            // but for GroupBy results in EF Core 3/5/6, standard ADO.NET or a specific class mapping is safer.
            // Since OperatorQualityItem is likely not a DB Entity, we use a classic ADO.NET approach for maximum speed on the read.
            
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                // Replace parameters manually or use AddParameter to be safe against injection (though inputs are typed here)
                // Using numbered parameters for raw format is safer:
                cmd.CommandText = sql
                    .Replace("{1}", "@p1")
                    .Replace("{2}", "@p2")
                    .Replace("{3}", "@p3")
                    .Replace("{4}", "@p4")
                    .Replace("__SCOPE_CLAUSE__", useUserScope ? "s.user_id = @userId" : "u.company_id = @p0");

                if (useUserScope)
                {
                    var pUser = cmd.CreateParameter(); pUser.ParameterName = "@userId"; pUser.Value = currentUserId; cmd.Parameters.Add(pUser);
                }
                else
                {
                    var p0 = cmd.CreateParameter(); p0.ParameterName = "@p0"; p0.Value = targetCompanyId; cmd.Parameters.Add(p0);
                }
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@p1"; p1.Value = min; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@p2"; p2.Value = max; cmd.Parameters.Add(p2);
                var p3 = cmd.CreateParameter(); p3.ParameterName = "@p3"; p3.Value = effectiveFrom; cmd.Parameters.Add(p3);
                var p4 = cmd.CreateParameter(); p4.ParameterName = "@p4"; p4.Value = effectiveTo; cmd.Parameters.Add(p4);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        result.Add(new OperatorQualityItem
                        {
                            name = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                            count = reader.GetInt32(1)
                        });
                    }
                }
            }
        }

        dbStopwatch.Stop();
        
        // 5. CACHE RESULT
        if (_redis != null && _redis.IsConnected && result.Count > 0)
        {
            await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
        }

        totalStopwatch.Stop();
        
        Response.Headers["X-Cache"] = "MISS";
        Response.Headers["X-Database-Ms"] = dbStopwatch.ElapsedMilliseconds.ToString();
        Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();

        return Ok(result);
    }
    catch (Exception ex)
    {
        try { new Writelog(db).write_exception_log(0, "AdminController", "GetOperatorQualityRanking", DateTime.Now, ex); } catch { }
        return StatusCode(500, new { Message = "Error: " + ex.Message });
    }
}
        // ========================================
        //  CACHE MANAGEMENT
        // ========================================

        // Clear cache helper
        private async Task ClearOperatorQualityCacheAsync()
        {
            if (_redis == null || !_redis.IsConnected) return;

            try
            {
                long deleted = await _redis.DeleteByPatternAsync("opquality:*");
                Console.WriteLine($" Cleared {deleted} operator quality cache keys");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Failed to clear operator quality cache: {ex.Message}");
            }
        }

        // Clear cache endpoint
        [HttpDelete, Route("ClearOperatorQualityCache")]
        public async Task<JsonResult> ClearOperatorQualityCache()
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    return Json(new { status = 0, message = "Redis not connected" });
                }

                long deleted = await _redis.DeleteByPatternAsync("opquality:*");
                Console.WriteLine($"Cleared all operator quality caches");

                return Json(new
                {
                    status = 1,
                    message = "Operator quality cache cleared",
                    keysDeleted = deleted
                });
            }
            catch (Exception ex)
            {
                return Json(new { status = 0, message = "Error: " + ex.Message });
            }
        }
        // Indoor/Outdoor counts & KPIs

        [HttpGet("IndoorCount")]
public async Task<IActionResult> IndoorCount(
    [FromQuery] string indoorColumn = "indoor_outdoor", 
    [FromQuery] string networkType = null,
    [FromQuery] int? company_id = null)
{
    try
    {
        // =========================================================
        // 1. SMART SECURITY: RESOLVE COMPANY ID
        // =========================================================
        int targetCompanyId = GetTargetCompanyId(company_id);

        // Security Check: If not Super Admin and no valid company found, deny access.
        if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User))
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
        }

        // =========================================================
        // 2. CALL OPTIMIZED HELPER
        // =========================================================
        // We pass the targetCompanyId to the helper so it chooses the fastest query path.
        var cnt = await CountForKpiSqlAsync(indoorColumn, isIndoor: true, networkType: networkType, companyId: targetCompanyId);
        
        return Ok(new { Status = 1, Count = cnt });
    }
    catch (Exception ex)
    {
        // Log error if needed
        return StatusCode(500, new { Status = 0, Message = "Error fetching indoor count: " + ex.Message });
    }
}

        [HttpGet("OutdoorCount")]
public async Task<IActionResult> OutdoorCount(
    [FromQuery] string indoorColumn = "indoor_outdoor", 
    [FromQuery] string networkType = null,
    [FromQuery] int? company_id = null) // <--- Added Parameter
{
    try
    {
        // =========================================================
        // 1. SMART SECURITY: RESOLVE COMPANY ID
        // =========================================================
        int targetCompanyId = GetTargetCompanyId(company_id);

        // Security Check: If not Super Admin and no valid company found, deny access.
        // This prevents regular users from running global queries.
        if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User))
        {
            return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
        }

        // =========================================================
        // 2. CALL OPTIMIZED HELPER
        // =========================================================
        // We pass targetCompanyId. The helper decides whether to run the 
        // "Fast Path" (Global) or "Secure Path" (Joins) based on this ID.
        var cnt = await CountForKpiSqlAsync(indoorColumn, isIndoor: false, networkType: networkType, companyId: targetCompanyId);
        
        return Ok(new { Status = 1, Count = cnt });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error fetching outdoor count: " + ex.Message });
    }
}
        [HttpGet("IndoorKpis")]
        public async Task<JsonResult> IndoorKpis(string networkType = null, string indoorColumn = "indoor_outdoor")
        {
            try
            {
                var list = await GetOperatorKpisSqlAsync(networkType, indoorColumn, isIndoor: true);
                return Json(BuildCompactKpiResponse(list, networkType));
            }
            catch (Exception ex)
            {
                return Json(new { Status = 0, Message = "Error fetching indoor KPIs: " + ex.Message });
            }
        }

        [HttpGet("OutdoorKpis")]
        public async Task<JsonResult> OutdoorKpis(string networkType = null, string indoorColumn = "indoor_outdoor")
        {
            try
            {
                var list = await GetOperatorKpisSqlAsync(networkType, indoorColumn, isIndoor: false);
                return Json(BuildCompactKpiResponse(list, networkType));
            }
            catch (Exception ex)
            {
                return Json(new { Status = 0, Message = "Error fetching outdoor KPIs: " + ex.Message });
            }
        }

        // ---- helper: count with SAME filters as KPI (indoor/outdoor + networkType) ----
        private async Task<long> CountForKpiSqlAsync(string indoorColumn, bool isIndoor, string networkType, int companyId = 0)
	{
	    var cacheKey = $"{(isIndoor ? "indoorcount" : "outdoorcount")}:{companyId}:{NormalizeCacheSegment(indoorColumn)}:{NormalizeCacheSegment(networkType)}";

	    var cached = await TryGetCachedLongAsync(cacheKey);
	    if (cached.HasValue)
	        return cached.Value;

	    var conn = db.Database.GetDbConnection();
	    var shouldClose = false;
	    try
	    {
	        if (conn.State != ConnectionState.Open) { await conn.OpenAsync(); shouldClose = true; }

        var nt = NormalizeNetworkType(networkType);
        var netFilter = (string.IsNullOrEmpty(nt) || nt.Equals("All", StringComparison.OrdinalIgnoreCase))
            ? ""
            : " AND n.network = @net ";

        // Build Environment Filter (Indoor vs Outdoor)
        string envFilter;
        if (isIndoor)
        {
            envFilter = $"n.`{indoorColumn}` = 'indoor'";
        }
        else
        {
            // Outdoor includes explicit 'outdoor', NULL, or empty string
            envFilter = $"(n.`{indoorColumn}` = 'outdoor' OR n.`{indoorColumn}` IS NULL OR n.`{indoorColumn}` = '')";
        }

        string sql;

        // =========================================================
        // OPTIMIZATION: SPLIT EXECUTION PATHS
        // =========================================================
        if (companyId == 0)
        {
            // FAST PATH: GLOBAL VIEW (Super Admin)
            // No joins needed. Queries the log table directly. Very fast.
            sql = $@"
                SELECT COUNT(*)
                FROM tbl_network_log n
                WHERE {envFilter}
                  {netFilter};";
        }
        else
        {
            // SECURE PATH: SPECIFIC COMPANY
            // Must join Session and User tables to filter by company_id.
            // Using STRAIGHT_JOIN (implied order) usually helps performance by forcing Session lookup first.
            sql = $@"
                SELECT COUNT(*)
                FROM tbl_network_log n
                JOIN tbl_session s ON n.session_id = s.id
                JOIN tbl_user u ON s.user_id = u.id
                WHERE u.company_id = @companyId
                  AND {envFilter}
                  {netFilter};";
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 240; // Extended timeout for heavy aggregation

        // Add Network Parameter if needed
        if (!string.IsNullOrEmpty(netFilter))
        {
            Add(cmd, "@net", nt);
        }

        // Add Company Parameter only for secure path
        if (companyId > 0)
        {
            Add(cmd, "@companyId", companyId);
        }

	        var obj = await cmd.ExecuteScalarAsync();
	        var value = obj == null ? 0L : Convert.ToInt64(obj);
	        await CacheLongAsync(cacheKey, value, 300);
	        return value;
	    }
	    finally
	    {
	        if (shouldClose && conn.State == ConnectionState.Open) await conn.CloseAsync();
	    }
}
        public class OperatorKpiRow
        {
            public string name { get; set; }
            public int samples { get; set; }
            public double avg_rsrp { get; set; }
            public double avg_rsrq { get; set; }
            public double avg_sinr { get; set; }
            public double avg_mos { get; set; }
            public double avg_jitter { get; set; }
            public double avg_latency { get; set; }
            public double avg_packet_loss { get; set; }
            public double avg_dl_tpt { get; set; }
            public double avg_ul_tpt { get; set; }
        }

        // Indoor/outdoor KPIs by operator
        private async Task<List<OperatorKpiRow>> GetOperatorKpisSqlAsync(string networkType, string indoorColumn, bool isIndoor)
        {
            var cacheKey = $"{(isIndoor ? "indoorkpis" : "outdoorkpis")}:{NormalizeCacheSegment(indoorColumn)}:{NormalizeCacheSegment(networkType)}";

            var cached = await TryGetCachedObjectAsync<List<OperatorKpiRow>>(cacheKey);
            if (cached != null)
                return cached;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;
            try
            {
                if (conn.State != ConnectionState.Open) { await conn.OpenAsync(); shouldClose = true; }

                var nt = NormalizeNetworkType(networkType);
                var netFilter = (string.IsNullOrEmpty(nt) || nt.Equals("All", StringComparison.OrdinalIgnoreCase))
                    ? ""
                    : " AND n.network = @net ";

                string envFilter;
                if (isIndoor)
                {
                    envFilter = $"n.`{indoorColumn}` = 'indoor'";
                }
                else
                {
                    envFilter = $"(n.`{indoorColumn}` = 'outdoor' OR n.`{indoorColumn}` IS NULL OR n.`{indoorColumn}` = '')";
                }

                var sql = $@"
                    SELECT
                      n.m_alpha_long AS operator_name,
                      COUNT(*)     AS samples,
                      AVG(n.rsrp)    AS avg_rsrp,
                      AVG(n.rsrq)    AS avg_rsrq,
                      AVG(n.sinr)    AS avg_sinr,
                      AVG(n.mos)     AS avg_mos,
                      AVG(n.jitter)  AS avg_jitter,
                      AVG(n.latency) AS avg_latency,
                      AVG(n.packet_loss) AS avg_packet_loss,
                      AVG(NULLIF(CAST(n.dl_tpt AS DECIMAL(18,4)),0)) AS avg_dl_tpt,
                      AVG(NULLIF(CAST(n.ul_tpt AS DECIMAL(18,4)),0)) AS avg_ul_tpt
                    FROM tbl_network_log n
                    WHERE {envFilter}
                      {netFilter}
                    GROUP BY n.m_alpha_long
                    ORDER BY samples DESC;";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 240;

                if (!string.IsNullOrEmpty(netFilter))
                {
                    Add(cmd, "@net", nt);
                }

                var list = new List<OperatorKpiRow>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    list.Add(new OperatorKpiRow
                    {
                        name = rd.IsDBNull(0) ? "" : rd.GetString(0),
                        samples = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1)),
                        avg_rsrp = Math.Round(rd.IsDBNull(2) ? 0d : Convert.ToDouble(rd.GetValue(2)), 2),
                        avg_rsrq = Math.Round(rd.IsDBNull(3) ? 0d : Convert.ToDouble(rd.GetValue(3)), 2),
                        avg_sinr = Math.Round(rd.IsDBNull(4) ? 0d : Convert.ToDouble(rd.GetValue(4)), 2),
                        avg_mos = Math.Round(rd.IsDBNull(5) ? 0d : Convert.ToDouble(rd.GetValue(5)), 2),
                        avg_jitter = Math.Round(rd.IsDBNull(6) ? 0d : Convert.ToDouble(rd.GetValue(6)), 2),
                        avg_latency = Math.Round(rd.IsDBNull(7) ? 0d : Convert.ToDouble(rd.GetValue(7)), 2),
                        avg_packet_loss = Math.Round(rd.IsDBNull(8) ? 0d : Convert.ToDouble(rd.GetValue(8)), 2),
                        avg_dl_tpt = Math.Round(rd.IsDBNull(9) ? 0d : Convert.ToDouble(rd.GetValue(9)), 2),
                        avg_ul_tpt = Math.Round(rd.IsDBNull(10) ? 0d : Convert.ToDouble(rd.GetValue(10)), 2)
                    });
                }

                await CacheObjectAsync(cacheKey, list, 600);
                return list;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open) await conn.CloseAsync();
            }
        }

        private static object BuildCompactKpiResponse(IEnumerable<OperatorKpiRow> list, string appliedNetworkType)
        {
            var operators = list.Select(x => new
            {
                name = x.name,
                samples = x.samples,
                avg_rsrp = x.avg_rsrp,
                avg_rsrq = x.avg_rsrq,
                avg_sinr = x.avg_sinr,
                avg_mos = x.avg_mos,
                avg_jitter = x.avg_jitter,
                avg_latency = x.avg_latency,
                avg_packet_loss = x.avg_packet_loss,
                avg_dl_tpt = x.avg_dl_tpt,
                avg_ul_tpt = x.avg_ul_tpt
            }).ToList();

            var totals = new { operatorCount = operators.Count, totalSamples = operators.Sum(o => o.samples) };
            var rankings = new
            {
                bySamples = operators.OrderByDescending(o => o.samples).Select(o => o.name).ToList(),
                byAvgRsrp = operators.OrderByDescending(o => o.avg_rsrp).Select(o => o.name).ToList(),
                byAvgRsrq = operators.OrderByDescending(o => o.avg_rsrq).Select(o => o.name).ToList(),
                byAvgSinr = operators.OrderByDescending(o => o.avg_sinr).Select(o => o.name).ToList(),
                byAvgMos = operators.OrderByDescending(o => o.avg_mos).Select(o => o.name).ToList(),
                byAvgJitterAsc = operators.OrderBy(o => o.avg_jitter).Select(o => o.name).ToList(),
                byAvgLatencyAsc = operators.OrderBy(o => o.avg_latency).Select(o => o.name).ToList(),
                byAvgLossAsc = operators.OrderBy(o => o.avg_packet_loss).Select(o => o.name).ToList(),
                byAvgDlTpt = operators.OrderByDescending(o => o.avg_dl_tpt).Select(o => o.name).ToList(),
                byAvgUlTpt = operators.OrderByDescending(o => o.avg_ul_tpt).Select(o => o.name).ToList()
            };

            return new { Status = 1, appliedNetworkType = appliedNetworkType?.ToUpperInvariant() ?? "ALL", totals, operators, rankings };
        }

        private async Task<long> CountEnvGoodBadSqlAsync(
            string indoorColumn,
            bool isIndoor,
            bool isGood,
            string networkType)
        {
            var cacheKey = $"{(isIndoor ? (isGood ? "indoorgoodcount" : "indoorbadcount") : (isGood ? "outdoorgoodcount" : "outdoorbadcount"))}:{NormalizeCacheSegment(indoorColumn)}:{NormalizeCacheSegment(networkType)}";

            var cached = await TryGetCachedLongAsync(cacheKey);
            if (cached.HasValue)
                return cached.Value;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var nt = NormalizeNetworkType(networkType);
                var netFilter = (string.IsNullOrEmpty(nt) || nt.Equals("All", StringComparison.OrdinalIgnoreCase))
                    ? ""
                    : " AND n.network = @net ";

                // Indoor/outdoor filter (case-insensitive, trim)
                string envFilter;
                if (isIndoor)
                {
                    envFilter = $"LOWER(TRIM(n.`{indoorColumn}`)) = 'indoor'";
                }
                else
                {
                    // Outdoor = NOT indoor (ya NULL / empty)
                    envFilter =
                        $"(LOWER(TRIM(n.`{indoorColumn}`)) <> 'indoor' " +
                        $" OR n.`{indoorColumn}` IS NULL " +
                        $" OR n.`{indoorColumn}` = '')";
                }

                // Good / Bad RSRP filter
                string rsrpFilter = isGood
                    ? "n.rsrp >= @thr"
                    : "n.rsrp < @thr";

                var sql = $@"
                    SELECT COUNT(*)
                    FROM tbl_network_log n
                    WHERE {envFilter}
                      AND {rsrpFilter}
                      {netFilter};";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 240;

                Add(cmd, "@thr", RsrpThreshold);

                if (!string.IsNullOrEmpty(netFilter))
                {
                    Add(cmd, "@net", nt);
                }

                var obj = await cmd.ExecuteScalarAsync();
                var value = obj == null ? 0L : Convert.ToInt64(obj);
                await CacheLongAsync(cacheKey, value, 300);
                return value;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        [HttpGet("IndoorBadCount")]
        public async Task<JsonResult> IndoorBadCount(
            string indoorColumn = "indoor_outdoor",
            string networkType = null)
        {
            try
            {
                var cnt = await CountEnvGoodBadSqlAsync(
                    indoorColumn,
                    isIndoor: true,
                    isGood: false,
                    networkType: networkType);

                return Json(new
                {
                    Status = 1,
                    Env = "Indoor",
                    Quality = "Bad",
                    Threshold = RsrpThreshold,
                    Count = cnt
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching indoor bad count: " + ex.Message
                });
            }
        }

        [HttpGet("IndoorGoodCount")]
        public async Task<JsonResult> IndoorGoodCount(
            string indoorColumn = "indoor_outdoor",
            string networkType = null)
        {
            try
            {
                var cnt = await CountEnvGoodBadSqlAsync(
                    indoorColumn,
                    isIndoor: true,
                    isGood: true,
                    networkType: networkType);

                return Json(new
                {
                    Status = 1,
                    Env = "Indoor",
                    Quality = "Good",
                    Threshold = RsrpThreshold,
                    Count = cnt
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching indoor good count: " + ex.Message
                });
            }
        }

        private async Task<List<object>> GetIndoorGoodBadPointsSqlAsync(
            bool isGood,
            string indoorColumn = "indoor_outdoor",
            string networkType = null,
            int maxRows = 1000)
        {
            var cacheKey = $"{(isGood ? "indoorgoodlogs" : "indoorbadlogs")}:{NormalizeCacheSegment(indoorColumn)}:{NormalizeCacheSegment(networkType)}:{maxRows}";

            var cached = await TryGetCachedObjectAsync<List<object>>(cacheKey);
            if (cached != null)
                return cached;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var nt = NormalizeNetworkType(networkType);
                var netFilter = (string.IsNullOrEmpty(nt) || nt.Equals("All", StringComparison.OrdinalIgnoreCase))
                    ? ""
                    : " AND n.network = @net ";

                // sirf indoor
                var envFilter = $"LOWER(TRIM(n.`{indoorColumn}`)) = 'indoor'";

                // good / bad filter
                var rsrpFilter = isGood ? "n.rsrp >= @thr" : "n.rsrp < @thr";

                var sql = $@"
                    SELECT
                        n.id,
                        n.lat,
                        n.lon,
                        n.rsrp
                    FROM tbl_network_log n
                    WHERE {envFilter}
                      AND {rsrpFilter}
                      {netFilter}
                    ORDER BY n.id
                    LIMIT @limit;";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 240;

                // threshold
                Add(cmd, "@thr", RsrpThreshold);

                // limit
                Add(cmd, "@limit", maxRows);

                // networkType param (agar diya ho)
                if (!string.IsNullOrEmpty(netFilter))
                {
                    Add(cmd, "@net", nt);
                }

                var list = new List<object>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    list.Add(new
                    {
                        id = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0)),
                        lat = rd.IsDBNull(1) ? 0d : Convert.ToDouble(rd.GetValue(1)),
                        lon = rd.IsDBNull(2) ? 0d : Convert.ToDouble(rd.GetValue(2)),
                        rsrp = rd.IsDBNull(3) ? 0d : Convert.ToDouble(rd.GetValue(3))
                    });
                }

                await CacheObjectAsync(cacheKey, list, 300);
                return list;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        [HttpGet("IndoorBadLogs")]
        public async Task<JsonResult> IndoorBadLogs(
            string indoorColumn = "indoor_outdoor",
            string networkType = null,
            int maxRows = 1000)
        {
            try
            {
                var logs = await GetIndoorGoodBadPointsSqlAsync(
                    isGood: false,
                    indoorColumn: indoorColumn,
                    networkType: networkType,
                    maxRows: maxRows);

                return Json(new
                {
                    Status = 1,
                    Env = "Indoor",
                    Quality = "Bad",
                    Threshold = RsrpThreshold,
                    Count = logs.Count,
                    Logs = logs
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching indoor bad logs: " + ex.Message
                });
            }
        }

        [HttpGet("IndoorGoodLogs")]
        public async Task<JsonResult> IndoorGoodLogs(
            string indoorColumn = "indoor_outdoor",
            string networkType = null,
            int maxRows = 1000)
        {
            try
            {
                var logs = await GetIndoorGoodBadPointsSqlAsync(
                    isGood: true,
                    indoorColumn: indoorColumn,
                    networkType: networkType,
                    maxRows: maxRows);

                return Json(new
                {
                    Status = 1,
                    Env = "Indoor",
                    Quality = "Good",
                    Threshold = RsrpThreshold,
                    Count = logs.Count,
                    Logs = logs
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching indoor good logs: " + ex.Message
                });
            }
        }

        private async Task<List<object>> GetAllIndoorSessionLogsSqlAsync(
            string networkType,
            string indoorColumn = "indoor_outdoor",
            int maxRows = 200000
        )
        {
            var cacheKey = $"indoorallsessionlogs:{NormalizeCacheSegment(indoorColumn)}:{NormalizeCacheSegment(networkType)}:{maxRows}";

            var cached = await TryGetCachedObjectAsync<List<object>>(cacheKey);
            if (cached != null)
                return cached;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var nt = NormalizeNetworkType(networkType);
                var isAllNetworks =
                    string.IsNullOrEmpty(nt) ||
                    nt.Equals("All", StringComparison.OrdinalIgnoreCase);

                var netFilter = isAllNetworks
                    ? ""
                    : " AND n.network = @net ";

                // Sirf indoor samples (case-insensitive, spaces ignore)
                var envFilter = $"LOWER(TRIM(n.`{indoorColumn}`)) = 'indoor'";

                var sql = $@"
                    SELECT
                        n.session_id,
                        n.lat,
                        n.lon,
                        n.rsrp
                    FROM tbl_network_log n
                    WHERE {envFilter}
                        {netFilter}
                    ORDER BY n.session_id, n.timestamp
                    LIMIT @maxRows;";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 240;

                Add(cmd, "@maxRows", maxRows);

                if (!isAllNetworks)
                {
                    Add(cmd, "@net", nt);
                }

                var list = new List<object>();
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    list.Add(new
                    {
                        sessionId = rd.IsDBNull(0) ? 0L : Convert.ToInt64(rd.GetValue(0)),
                        lat = rd.IsDBNull(1) ? 0d : Convert.ToDouble(rd.GetValue(1)),
                        lon = rd.IsDBNull(2) ? 0d : Convert.ToDouble(rd.GetValue(2)),
                        rsrp = rd.IsDBNull(3) ? 0d : Convert.ToDouble(rd.GetValue(3))
                    });
                }

                await CacheObjectAsync(cacheKey, list, 300);
                return list;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                {
                    await conn.CloseAsync();
                }
            }
        }

        [HttpGet("AllIndoorSessionLogs")]
        public async Task<JsonResult> AllIndoorSessionLogs(
            string networkType = null,
            string indoorColumn = "indoor_outdoor",
            int maxRows = 200000)
        {
            try
            {
                var logs = await GetAllIndoorSessionLogsSqlAsync(
                    networkType: networkType,
                    indoorColumn: indoorColumn,
                    maxRows: maxRows);

                var appliedNetworkType = NormalizeNetworkType(networkType);
                if (string.IsNullOrEmpty(appliedNetworkType) ||
                    appliedNetworkType.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    appliedNetworkType = "ALL";
                }

                return Json(new
                {
                    Status = 1,
                    Env = "Indoor",
                    appliedNetworkType = appliedNetworkType,
                    totalLogs = logs.Count,
                    logs = logs
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching all indoor session logs: " + ex.Message
                });
            }
        }

        // NEW: paginated endpoint for AllIndoorSessionLogs
        [HttpGet("AllIndoorSessionLogsPaged")]
        public async Task<JsonResult> AllIndoorSessionLogsPaged(
            string networkType = null,
            string indoorColumn = "indoor_outdoor",
            int pageNumber = 1,
            int pageSize = 5000)
        {
            try
            {
                if (pageNumber < 1) pageNumber = 1;
                if (pageSize < 1) pageSize = 100;
                if (pageSize > 20000) pageSize = 20000;

                var conn = db.Database.GetDbConnection();
                var shouldClose = false;

                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var nt = NormalizeNetworkType(networkType);
                var isAllNetworks =
                    string.IsNullOrEmpty(nt) ||
                    nt.Equals("All", StringComparison.OrdinalIgnoreCase);

                var netFilter = isAllNetworks
                    ? ""
                    : " AND n.network = @net ";

                var envFilter = $"LOWER(TRIM(n.`{indoorColumn}`)) = 'indoor'";

                // total count
                var countSql = $@"
                    SELECT COUNT(*)
                    FROM tbl_network_log n
                    WHERE {envFilter}
                      {netFilter};";

                long totalCount;
                await using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = countSql;
                    if (!isAllNetworks)
                        Add(countCmd, "@net", nt);

                    var obj = await countCmd.ExecuteScalarAsync();
                    totalCount = obj == null ? 0L : Convert.ToInt64(obj);
                }

                var offset = (pageNumber - 1) * pageSize;

                var sql = $@"
                    SELECT
                        n.id,
                        n.session_id,
                        n.lat,
                        n.lon,
                        n.rsrp
                    FROM tbl_network_log n
                    WHERE {envFilter}
                      {netFilter}
                    ORDER BY n.id
                    LIMIT @pageSize OFFSET @offset;";

                var list = new List<object>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 240;

                    Add(cmd, "@pageSize", pageSize);
                    Add(cmd, "@offset", offset);

                    if (!isAllNetworks)
                        Add(cmd, "@net", nt);

                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync())
                    {
                        list.Add(new
                        {
                            id = rd.IsDBNull(0) ? 0L : Convert.ToInt64(rd.GetValue(0)),
                            sessionId = rd.IsDBNull(1) ? 0L : Convert.ToInt64(rd.GetValue(1)),
                            lat = rd.IsDBNull(2) ? 0d : Convert.ToDouble(rd.GetValue(2)),
                            lon = rd.IsDBNull(3) ? 0d : Convert.ToDouble(rd.GetValue(3)),
                            rsrp = rd.IsDBNull(4) ? 0d : Convert.ToDouble(rd.GetValue(4))
                        });
                    }
                }

                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
                var appliedNetworkType = isAllNetworks ? "ALL" : nt.ToUpperInvariant();

                return Json(new
                {
                    Status = 1,
                    Env = "Indoor",
                    appliedNetworkType,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    TotalPages = totalPages,
                    HasMore = pageNumber < totalPages,
                    Items = list
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching indoor session logs: " + ex.Message
                });
            }
        }

        [HttpGet("GetSessions")]
public async Task<IActionResult> GetSessions([FromQuery] int? company_id = null)
{
    // =========================================================
    // 1. SMART SECURITY: RESOLVE COMPANY ID
    // =========================================================
    int targetCompanyId = GetTargetCompanyId(company_id);
    int currentUserId = _userScope.GetCurrentUserId(User);
    bool useUserScope = !_userScope.IsSuperAdmin(User) && targetCompanyId == 0 && currentUserId > 0;

    if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope)
    {
        return Unauthorized(new
        {
            Status = 0,
            Message = "Unauthorized. Invalid Company."
        });
    }

    var cacheKey = $"sessions:list:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}";
    var cached = await TryGetCachedObjectAsync<List<object>>(cacheKey);
    if (cached != null)
    {
        return Ok(new
        {
            Status = 1,
            Source = "REDIS",
            Data = cached
        });
    }

    // =========================================================
    // 2. EXECUTE SECURE QUERY
    // =========================================================
    try
    {
        var sessions = await (
            from s in db.tbl_session.AsNoTracking()
            join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
            where useUserScope
                ? s.user_id == currentUserId
                : (targetCompanyId == 0 || u.company_id == targetCompanyId)
            orderby s.start_time descending
            select new
            {
                id = s.id,
                session_name = "Session " + s.id,

                // ?? COMPANY INFO
                company_id = u.company_id,

                start_time = s.start_time,
                end_time = s.end_time,
                notes = s.notes,

                start_lat = s.start_lat,
                start_lon = s.start_lon,
                end_lat = (double?)s.end_lat,
                end_lon = (double?)s.end_lon,

                capture_frequency = (double?)s.capture_frequency,
                distance_km = s.distance,

                start_address = s.start_address,
                end_address = s.end_address,

                CreatedBy = u.name,
                mobile = u.mobile,
                make = u.make,
                model = u.model,
                os = u.os,
                operator_name = u.operator_name
            }
        ).ToListAsync();

        await CacheObjectAsync(cacheKey, sessions.Select(x => (object)x).ToList(), 300);

        return Ok(new
        {
            Status = 1,
            Source = "DATABASE",
            Data = sessions
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Status = 0,
            Message = "Error: " + ex.Message
        });
    }
}

        // Optimized: Removed fetching all logs inside this method.

        [HttpGet("GetSessionsByDateRange")]
        public async Task<IActionResult> GetSessionsByDateRange(
            string startDateIso,
            string endDateIso,
            [FromQuery] int? company_id = null)
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = 0;
            bool isAuthorized = false;

            if (company_id.HasValue && company_id.Value > 0)
            {
                targetCompanyId = company_id.Value;
                isAuthorized = true;
            }
            else
            {
                string token = string.Empty;
                if (Request.Headers.ContainsKey("Authorization"))
                {
                    token = Request.Headers["Authorization"].ToString();
                    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = token.Substring(7).Trim();
                }
                else if (Request.Query.ContainsKey("token"))
                {
                    token = Request.Query["token"].ToString();
                }

                if (!string.IsNullOrEmpty(token))
                {
                    var user = await db.tbl_user.AsNoTracking()
                        .Select(u => new { u.token, u.company_id, u.isactive })
                        .FirstOrDefaultAsync(u => u.token == token && u.isactive == 1);

                    if (user != null)
                    {
                        targetCompanyId = user.company_id ?? 0;
                        isAuthorized = true;
                    }
                }
            }

            if (!isAuthorized || targetCompanyId == 0)
            {
                return StatusCode(401, new { Status = 0, Message = "Unauthorized. Please provide either a valid 'company_id' or a valid 'token'." });
            }

            try
            {
                if (!DateTime.TryParse(startDateIso, out DateTime startDate) ||
                    !DateTime.TryParse(endDateIso, out DateTime endDate))
                {
                    return Json(new { success = false, Message = "Invalid date format" });
                }

                endDate = endDate.Date.AddDays(1).AddTicks(-1);

                // =========================================================
                // 2. CACHE KEY
                // =========================================================
                string fromKey = startDate.ToString("yyyyMMdd");
                string toKey = endDate.ToString("yyyyMMdd");
                string cacheKey = $"SessionsDateRange:{targetCompanyId}:{fromKey}:{toKey}";

                // =========================================================
                // 3. TRY REDIS
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                    if (cached != null) return Json(cached);
                }

                // =========================================================
                // 4. EXECUTE SECURE QUERY (FIXED CASTING)
                // =========================================================
                var sessionsData = await (
                    from s in db.tbl_session.AsNoTracking()
                    join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                    where targetCompanyId == 0 || u.company_id == targetCompanyId
                       && s.start_time.HasValue
                       && s.start_time.Value >= startDate
                       && s.start_time.Value <= endDate
                    orderby s.start_time descending
                    select new
                    {
                        id = s.id,
                        session_name = "Session " + s.id,
                        start_time = s.start_time,
                        end_time = s.end_time,
                        notes = s.notes,

                        // --- FIX: Safely Cast Float/Double to Double first ---
                        start_lat = (double?)s.start_lat,
                        start_lon = (double?)s.start_lon,
                        end_lat = (double?)s.end_lat,
                        end_lon = (double?)s.end_lon,

                        // --- FIX: Safe Cast for Int Conversion ---
                        // Pehle Double? mein cast karein, fir Int? mein.
                        capture_frequency = (int?)(double?)s.capture_frequency,

                        distance_km = s.distance,
                        start_address = s.start_address,
                        end_address = s.end_address,
                        CreatedBy = u.name,
                        mobile = u.mobile,
                        make = u.make,
                        model = u.model,
                        os = u.os,
                        operator_name = u.operator_name,
                        Logs = (object)null
                    })
                    .ToListAsync();

                // =========================================================
                // 5. SAVE TO REDIS
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    await _redis.SetObjectAsync(cacheKey, sessionsData, ttlSeconds: 300);
                }

                return Json(sessionsData);
            }
            catch (Exception ex)
            {
                // Add detailed error logging here if possible
                Response.StatusCode = 500;
                return Json(new { Message = "Error fetching sessions: " + ex.Message });
            }
        }
        [HttpDelete("DeleteSession")]
        public async Task<IActionResult> DeleteSession([FromQuery] double id)
        {
            try
            {
                int sessionId = Convert.ToInt32(Math.Floor(id));

                if (sessionId <= 0)
                    return BadRequest(new { success = false, message = "Invalid session id" });

                using var tx = await db.Database.BeginTransactionAsync();

                //  RAW SQL — NO PARAMETER CLASS NEEDED
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM tbl_network_log WHERE session_id = {0}", sessionId);

                var rows = await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM tbl_session WHERE id = {0}", sessionId);

                await tx.CommitAsync();

                if (rows == 0)
                    return NotFound(new { success = false, message = "Session not found" });

                await InvalidateCalculatedCachesAsync();

                return Ok(new
                {
                    success = true,
                    message = "Session deleted successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = ex.InnerException?.Message ?? ex.Message
                });
            }
        }


        #endregion

        #region Polygon Good / Bad Summary (RSRP based)

        public class PolygonGoodBadSummaryRow
        {
            public long id { get; set; }
            public string name { get; set; }
            public long? project_id { get; set; }
            public double? area { get; set; }
            public string region_wkt { get; set; }
            public int good_count { get; set; }
            public int bad_count { get; set; }
            public int total_count { get; set; }
        }

        private async Task<List<PolygonGoodBadSummaryRow>> GetPolygonGoodBadSummarySqlAsync(
            double rsrpThreshold = -95,
            string networkType = null,
            long? projectId = null)
        {
            var cacheKey = $"polygongoodbad:{rsrpThreshold.ToString(CultureInfo.InvariantCulture)}:{NormalizeCacheSegment(networkType)}:{projectId?.ToString(CultureInfo.InvariantCulture) ?? "all"}";

            var cached = await TryGetCachedObjectAsync<List<PolygonGoodBadSummaryRow>>(cacheKey);
            if (cached != null)
                return cached;

            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var nt = NormalizeNetworkType(networkType);

                var netFilterJoin = (string.IsNullOrEmpty(nt) ||
                                     nt.Equals("All", StringComparison.OrdinalIgnoreCase))
                    ? ""
                    : " AND n.network = @net ";

                var projectFilter = projectId.HasValue
                    ? " WHERE p.project_id = @pid "
                    : "";

                // NOTE: Using ST_SRID(p.region) only works if all polygons in the DB have the same SRID.
                // Assuming SRID 4326 is used for lat/lon.
                var sql = $@"
                    SELECT
                      p.id,
                      p.name,
                      p.project_id,
                      p.area,
                      ST_AsText(p.region) AS region_wkt,
                      SUM(CASE WHEN n.rsrp > @thr THEN 1 ELSE 0 END) AS good_cnt,
                      SUM(CASE WHEN n.rsrp <= @thr AND n.rsrp IS NOT NULL THEN 1 ELSE 0 END) AS bad_cnt,
                      COUNT(n.rsrp) AS total_cnt
                    FROM tbl_savepolygon p
                    LEFT JOIN tbl_network_log n
                      ON ST_CONTAINS(
                            p.region,
                            ST_GeomFromText(
                                CONCAT('POINT(', n.lon, ' ', n.lat, ')'),
                                4326
                            )
                        )
                      {netFilterJoin}
                    {projectFilter}
                    GROUP BY p.id, p.name, p.project_id, p.area, region_wkt
                    ORDER BY p.id;";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 600;

                Add(cmd, "@thr", rsrpThreshold);

                if (!string.IsNullOrEmpty(netFilterJoin))
                {
                    Add(cmd, "@net", nt);
                }

                if (projectId.HasValue)
                {
                    Add(cmd, "@pid", projectId.Value);
                }

                var list = new List<PolygonGoodBadSummaryRow>();
                await using var rd = await cmd.ExecuteReaderAsync();

                while (await rd.ReadAsync())
                {
                    list.Add(new PolygonGoodBadSummaryRow
                    {
                        id = rd.GetInt64(0),
                        name = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        project_id = rd.IsDBNull(2) ? (long?)null : rd.GetInt64(2),
                        area = rd.IsDBNull(3) ? (double?)null : rd.GetDouble(3),
                        region_wkt = rd.IsDBNull(4) ? "" : rd.GetString(4),
                        good_count = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5)),
                        bad_count = rd.IsDBNull(6) ? 0 : Convert.ToInt32(rd.GetValue(6)),
                        total_count = rd.IsDBNull(7) ? 0 : Convert.ToInt32(rd.GetValue(7))
                    });
                }

                await CacheObjectAsync(cacheKey, list, 600);
                return list;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        [HttpGet("PolygonGoodBadSummary")]
        public async Task<JsonResult> PolygonGoodBadSummary(
            double rsrpThreshold = -95,
            string networkType = null,
            long? projectId = null)
        {
            try
            {
                var list = await GetPolygonGoodBadSummarySqlAsync(rsrpThreshold, networkType, projectId);

                var response = new
                {
                    Status = 1,
                    rsrpThreshold,
                    appliedNetworkType = string.IsNullOrEmpty(networkType)
                        ? "ALL"
                        : networkType.ToUpperInvariant(),
                    polygons = list.Select(p => new
                    {
                        p.id,
                        p.name,
                        p.project_id,
                        p.area,
                        p.region_wkt,
                        p.good_count,
                        p.bad_count,
                        p.total_count,
                        good_percent = p.total_count == 0
                            ? 0
                            : Math.Round(100.0 * p.good_count / p.total_count, 2),
                        bad_percent = p.total_count == 0
                            ? 0
                            : Math.Round(100.0 * p.bad_count / p.total_count, 2)
                    })
                };

                return Json(response);
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching polygon good/bad summary: " + ex.Message
                });
            }
        }

        #endregion

        #region Polygon Points (detail with lat/long + quality)

        private async Task<List<dynamic>> GetPolygonPointsSqlAsync(
            long polygonId,
            double rsrpThreshold = -95,
            string networkType = null)
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var nt = NormalizeNetworkType(networkType);

                var netFilterJoin = (string.IsNullOrEmpty(nt) ||
                                     nt.Equals("All", StringComparison.OrdinalIgnoreCase))
                    ? ""
                    : " AND n.network = @net ";

                var sql = $@"
                    SELECT
                      n.id,
                      n.lat,     -- latitude column
                      n.lon,     -- longitude column
                      n.rsrp,
                      n.rsrq,
                      n.sinr,
                      n.mos,
                      n.jitter,
                      n.latency,
                      n.packet_loss,
                      n.dl_tpt,
                      n.ul_tpt,
                      CASE WHEN n.rsrp > @thr THEN 'GOOD' ELSE 'BAD' END AS quality
                    FROM tbl_savepolygon p
                    JOIN tbl_network_log n
                      ON p.id = @polygonId
                     AND ST_CONTAINS(
                             p.region,
                             ST_GeomFromText(
                                 CONCAT('POINT(', n.lon, ' ', n.lat, ')'),
                                 4326
                             )
                         )
                     {netFilterJoin}
                    ORDER BY n.id;";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 600;

                Add(cmd, "@polygonId", polygonId);
                Add(cmd, "@thr", rsrpThreshold);

                if (!string.IsNullOrEmpty(netFilterJoin))
                {
                    Add(cmd, "@net", nt);
                }

                var list = new List<dynamic>();
                await using var rd = await cmd.ExecuteReaderAsync();

                while (await rd.ReadAsync())
                {
                    list.Add(new
                    {
                        id = rd.GetInt64(0),
                        latitude = rd.IsDBNull(1) ? (double?)null : rd.GetDouble(1),
                        longitude = rd.IsDBNull(2) ? (double?)null : rd.GetDouble(2),
                        rsrp = rd.IsDBNull(3) ? (double?)null : rd.GetDouble(3),
                        rsrq = rd.IsDBNull(4) ? (double?)null : rd.GetDouble(4),
                        sinr = rd.IsDBNull(5) ? (double?)null : rd.GetDouble(5),
                        mos = rd.IsDBNull(6) ? (double?)null : rd.GetDouble(6),
                        jitter = rd.IsDBNull(7) ? (double?)null : rd.GetDouble(7),
                        latency = rd.IsDBNull(8) ? (double?)null : rd.GetDouble(8),
                        packet_loss = rd.IsDBNull(9) ? (double?)null : rd.GetDouble(9),
                        dl_tpt = rd.IsDBNull(10) ? (double?)null : rd.GetDouble(10),
                        ul_tpt = rd.IsDBNull(11) ? (double?)null : rd.GetDouble(11),
                        quality = rd.IsDBNull(12) ? "" : rd.GetString(12)
                    });
                }

                return list;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }

        [HttpGet("PolygonPoints")]
        public async Task<JsonResult> PolygonPoints(
     long polygonId,
     double rsrpThreshold = -95,
     string networkType = null)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // ========================================
                //  BUILD CACHE KEY
                // ========================================
                string normalizedNetworkType = string.IsNullOrWhiteSpace(networkType)
                    ? "all"
                    : networkType.Trim().ToLowerInvariant();

                string cacheKey = $"polygonpoints:{polygonId}:{rsrpThreshold}:{normalizedNetworkType}";

                // ========================================
                //  TRY GET FROM REDIS CACHE
                // ========================================
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cacheStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var cached = await _redis.GetObjectAsync<PolygonPointsResponse>(cacheKey);
                        cacheStopwatch.Stop();

                        if (cached != null)
                        {
                            totalStopwatch.Stop();
                            Console.WriteLine($" Cache HIT: {cacheKey}");
                            Console.WriteLine($"    Cache lookup: {cacheStopwatch.ElapsedMilliseconds}ms");
                            Console.WriteLine($"    Total time: {totalStopwatch.ElapsedMilliseconds}ms");

                            Response.Headers["X-Cache"] = "HIT";
                            Response.Headers["X-Cache-Lookup-Ms"] = cacheStopwatch.ElapsedMilliseconds.ToString();
                            Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();

                            return Json(new
                            {
                                Status = 1,
                                polygonId,
                                rsrpThreshold,
                                appliedNetworkType = string.IsNullOrEmpty(networkType)
                                    ? "ALL"
                                    : networkType.ToUpperInvariant(),
                                samples = cached.samples,
                                cachedAt = cached.CachedAt
                            });
                        }

                        Console.WriteLine($" Cache MISS: {cacheKey}");
                    }
                    catch (Exception redisEx)
                    {
                        Console.WriteLine($" Redis read error: {redisEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine(" Redis not available, skipping cache lookup");
                }

                // ========================================
                //  FETCH FROM DATABASE
                // ========================================
                var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var samples = await GetPolygonPointsSqlAsync(polygonId, rsrpThreshold, networkType);

                dbStopwatch.Stop();
                Console.WriteLine($"    Database query: {dbStopwatch.ElapsedMilliseconds}ms");
                Console.WriteLine($"    Samples found: {samples?.Count ?? 0}");

                // ========================================
                //  BUILD RESPONSE & CACHE IT
                // ========================================
                var response = new PolygonPointsResponse
                {
                    samples = samples,
                    CachedAt = DateTime.UtcNow
                };

                // Cache for 10 minutes (polygon data changes infrequently)
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cacheWriteStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        await _redis.SetObjectAsync(cacheKey, response, ttlSeconds: 600);
                        cacheWriteStopwatch.Stop();

                        Console.WriteLine($" Cached: {cacheKey} (TTL: 10 min)");
                        Console.WriteLine($"   Cache write: {cacheWriteStopwatch.ElapsedMilliseconds}ms");
                    }
                    catch (Exception redisEx)
                    {
                        Console.WriteLine($" Failed to cache: {redisEx.Message}");
                    }
                }

                totalStopwatch.Stop();
                Console.WriteLine($"    Total time: {totalStopwatch.ElapsedMilliseconds}ms");

                Response.Headers["X-Cache"] = "MISS";
                Response.Headers["X-Database-Ms"] = dbStopwatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Sample-Count"] = (samples?.Count ?? 0).ToString();

                return Json(new
                {
                    Status = 1,
                    polygonId,
                    rsrpThreshold,
                    appliedNetworkType = string.IsNullOrEmpty(networkType)
                        ? "ALL"
                        : networkType.ToUpperInvariant(),
                    samples
                });
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                Console.WriteLine($" ERROR in PolygonPoints: {ex.Message}");
                Console.WriteLine($" Stack Trace: {ex.StackTrace}");

                return Json(new
                {
                    Status = 0,
                    Message = "Error fetching polygon samples: " + ex.Message,
                    StackTrace = ex.StackTrace
                });
            }
        }

        // ========================================
        //  RESPONSE DTO FOR CACHING
        // ========================================
        public class PolygonPointsResponse
        {
            public object samples { get; set; }
            public DateTime CachedAt { get; set; }
        }
        #endregion


        // ====================================================================
        // =========================  V2: SPLIT APIs  ==========================
        // ====================================================================

        public class OpNetValueDto
        {
            public string operatorName { get; set; }
            public string network { get; set; }
            public double value { get; set; }

            public double avg_rsrp { get; set; }
            public double avg_rsrq { get; set; }
            public double avg_sinr { get; set; }
            public double avg_mos { get; set; }
            public double avg_dl_tpt { get; set; }
            public double avg_ul_tpt { get; set; }
            public double avg_jitter { get; set; }
            public double avg_packet_loss { get; set; }
        }

        [Keyless]
        public sealed class BandOpNetDto
        {
            public string? operatorName { get; set; }   //  nullable
            public string? network { get; set; }        //  nullable
            public string? band { get; set; }           // nullable
            public long count { get; set; }
        }

        private enum Metric
        {
            Rsrp,
            Rsrq,
            Sinr,
            Mos,
            Jitter,
            Latency,
            PacketLoss,
            DlTpt,
            UlTpt
        }

        private async Task<JsonResult> SafeOkV2<T>(
    string action,
    Func<Task<T>> fn,
    string cacheKey = null,
    int ttlSeconds = 300) where T : class
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var msg = new ReturnAPIResponse();

            try
            {
                T data = null;

                // ========================================
                //  TRY GET FROM REDIS CACHE
                // ========================================
                if (!string.IsNullOrEmpty(cacheKey) && _redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cacheStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        data = await _redis.GetObjectAsync<T>(cacheKey);
                        cacheStopwatch.Stop();

                        if (data != null)
                        {
                            totalStopwatch.Stop();
                            Console.WriteLine($" Cache HIT [{action}]: {cacheKey}");
                            Console.WriteLine($"    Cache lookup: {cacheStopwatch.ElapsedMilliseconds}ms");
                            Console.WriteLine($"    Total time: {totalStopwatch.ElapsedMilliseconds}ms");

                            // Add performance headers
                            Response.Headers["X-Cache"] = "HIT";
                            Response.Headers["X-Cache-Lookup-Ms"] = cacheStopwatch.ElapsedMilliseconds.ToString();
                            Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
                            Response.Headers["X-Action"] = action;

                            msg.Status = 1;
                            msg.Data = data;
                            return Json(msg);
                        }

                        Console.WriteLine($"Cache MISS [{action}]: {cacheKey}");
                    }
                    catch (Exception redisEx)
                    {
                        Console.WriteLine($" Redis read error [{action}]: {redisEx.Message}");
                        // Continue without cache
                    }
                }

                // ========================================
                //  EXECUTE DATA FUNCTION
                // ========================================
                var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();
                data = await fn();
                dbStopwatch.Stop();

                Console.WriteLine($"    Data fetch [{action}]: {dbStopwatch.ElapsedMilliseconds}ms");

                // ========================================
                //  CACHE THE RESULT IN REDIS
                // ========================================
                if (!string.IsNullOrEmpty(cacheKey) && data != null && _redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cacheWriteStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        await _redis.SetObjectAsync(cacheKey, data, ttlSeconds);
                        cacheWriteStopwatch.Stop();

                        Console.WriteLine($" Cached [{action}]: {cacheKey} (TTL: {ttlSeconds}s)");
                        Console.WriteLine($"    Cache write: {cacheWriteStopwatch.ElapsedMilliseconds}ms");
                    }
                    catch (Exception redisEx)
                    {
                        Console.WriteLine($" Failed to cache [{action}]: {redisEx.Message}");
                    }
                }

                totalStopwatch.Stop();
                Console.WriteLine($"    Total time [{action}]: {totalStopwatch.ElapsedMilliseconds}ms");

                // Add performance headers
                Response.Headers["X-Cache"] = "MISS";
                Response.Headers["X-Database-Ms"] = dbStopwatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Action"] = action;

                msg.Status = 1;
                msg.Data = data;
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                msg.Status = 0;
                msg.Message = "Error: " + ex.Message;

                Console.WriteLine($" ERROR in {action}: {ex.Message}");
                Console.WriteLine($" Stack Trace: {ex.StackTrace}");

                try
                {
                    new Writelog(db).write_exception_log(0, "AdminController", action, DateTime.Now, ex);
                }
                catch { }
            }

            return Json(msg);
        }
        private static HashSet<string> ParseCsv(string csv) =>
                    string.IsNullOrWhiteSpace(csv)
                        ? null
                        : new HashSet<string>(
                            csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                            StringComparer.OrdinalIgnoreCase);

        private static string NormalizeOperatorBucket(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var s = value.Trim();
            var u = s.ToUpperInvariant();

            if (u.Contains("AIRTEL")) return "Airtel";
            if (u.Contains("JIO")) return "Jio";
            if (u == "VI" || u.Contains("VODAFONE") || u.Contains("IDEA") || u.Contains("VI INDIA")) return "VI India";
            if (u.Contains("BSNL")) return "BSNL";

            return s;
        }

        private static string NormalizeNetworkBucket(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var s = value.Trim();
            var u = s.ToUpperInvariant();

            if (u.Contains("5G") || u.Contains("NR") || u.Contains("NSA") || u == "SA" || u.Contains(" SA")) return "5G";
            if (u.Contains("4G") || u.Contains("LTE")) return "4G";
            if (u.Contains("3G") || u.Contains("WCDMA") || u.Contains("UMTS") || u.Contains("HSPA")) return "3G";
            if (u.Contains("2G") || u.Contains("EDGE") || u.Contains("GSM") || u.Contains("GPRS")) return "2G";

            return s;
        }

        private static IQueryable<tbl_network_log> ApplyFilters(
            IQueryable<tbl_network_log> q,
            string operatorCsv,
            string networkCsv,
            DateTime? from,
            DateTime? to)
        {
            var ops = ParseCsv(operatorCsv);
            var nets = ParseCsv(networkCsv);

            if (ops is not null && ops.Count > 0)
                q = q.Where(n => !string.IsNullOrEmpty(n.m_alpha_long) && ops.Contains(n.m_alpha_long));

            if (nets is not null && nets.Count > 0)
                q = q.Where(n => !string.IsNullOrEmpty(n.network) && nets.Contains(n.network));

            if (from.HasValue)
                q = q.Where(n => n.timestamp.HasValue && n.timestamp.Value >= from.Value);

            if (to.HasValue)
                q = q.Where(n => n.timestamp.HasValue && n.timestamp.Value < to.Value);

            return q;
        }

        private static double SafeParse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0d;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return v;
            return 0d;
        }

        /// <summary>
        /// Average numeric metric (RSRP/RSRQ/SINR/etc.) per (operator, network) with filters.
        /// </summary>
        /// 
        public class AvgMetricRow
        {
            public string OperatorName { get; set; }
            public string Network { get; set; }
            public double Value { get; set; }
        }


        private async Task<List<object>> AveragePerOperatorNetworkAsync(
       string operatorName,
       string networkType,
       DateTime? from,
       DateTime? to,
       Metric metric,
       bool ascending = false)
        {
            var baseQuery = db.tbl_network_log.AsNoTracking().AsQueryable();

            // -----------------------------
            // FILTERS
            // -----------------------------
            if (!string.IsNullOrWhiteSpace(operatorName))
                baseQuery = baseQuery.Where(x =>
                    x.m_alpha_long != null &&
                    EF.Functions.Like(x.m_alpha_long, $"%{operatorName}%"));

            if (!string.IsNullOrWhiteSpace(networkType) &&
                !networkType.Equals("All", StringComparison.OrdinalIgnoreCase))
                baseQuery = baseQuery.Where(x =>
                    x.network != null &&
                    EF.Functions.Like(x.network, $"%{networkType}%"));

            if (from.HasValue)
                baseQuery = baseQuery.Where(x => x.timestamp >= from.Value);

            if (to.HasValue)
                baseQuery = baseQuery.Where(x => x.timestamp <= to.Value);

            // -----------------------------
            // NUMERIC METRICS (EF SAFE)
            // -----------------------------
            IQueryable<AvgMetricRow> query = metric switch
            {
                Metric.Rsrp => baseQuery
                    .Where(x => x.rsrp.HasValue)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.rsrp.Value)
                    }),

                Metric.Rsrq => baseQuery
                    .Where(x => x.rsrq.HasValue && x.rsrq != 0)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.rsrq.Value)
                    }),

                Metric.Sinr => baseQuery
                    .Where(x => x.sinr.HasValue)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.sinr.Value)
                    }),

                Metric.Mos => baseQuery
                    .Where(x => x.mos.HasValue)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.mos.Value)
                    }),

                Metric.Jitter => baseQuery
                    .Where(x => x.jitter.HasValue)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.jitter.Value)
                    }),

                Metric.Latency => baseQuery
                    .Where(x => x.latency.HasValue)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.latency.Value)
                    }),

                Metric.PacketLoss => baseQuery
                    .Where(x => x.packet_loss.HasValue)
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(x => (double)x.packet_loss.Value)
                    }),

                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            };

            // -----------------------------
            // SORT
            // -----------------------------
            query = ascending
                ? query.OrderBy(x => x.Value)
                : query.OrderByDescending(x => x.Value);

            var data = await query.ToListAsync();

            // -----------------------------
            // DL / UL THROUGHPUT (POST-PROCESS)
            // -----------------------------
            if (metric == Metric.DlTpt || metric == Metric.UlTpt)
            {
                var raw = await baseQuery
                    .Select(x => new
                    {
                        x.m_alpha_long,
                        x.network,
                        value = metric == Metric.DlTpt ? x.dl_tpt : x.ul_tpt
                    })
                    .ToListAsync();

                data = raw
                    .Where(x => double.TryParse(x.value, out _))
                    .GroupBy(x => new { x.m_alpha_long, x.network })
                    .Select(g => new AvgMetricRow
                    {
                        OperatorName = g.Key.m_alpha_long,
                        Network = g.Key.network,
                        Value = g.Average(v => double.Parse(v.value))
                    })
                    .OrderByDescending(x => x.Value)
                    .ToList();
            }

            // -----------------------------
            // FINAL SHAPE
            // -----------------------------
            return data.Select(x => new
            {
                operatorName = x.OperatorName,
                network = x.Network,
                value = Math.Round(x.Value, 2)
            }).ToList<object>();
        }




        /// <summary>
        /// Average “numeric-but-stored-as-string” metric per (operator, network).
        /// Used for DL/UL throughput. DB part still uses ExecuteHeavyQueryAsync.
        /// </summary>
        private async Task<List<OpNetValueDto>> AverageStringPerOperatorNetworkAsync(
            string operatorName,
            string networkType,
            DateTime? from,
            DateTime? to,
            Func<tbl_network_log, string> selector)
        {
            var q = db.tbl_network_log.AsNoTracking()
                .Where(n => !string.IsNullOrEmpty(n.m_alpha_long) &&
                            !string.IsNullOrEmpty(n.network));

            q = ApplyFilters(q, operatorName, networkType, from, to);

            // Pull only the columns we need from SQL (with extended timeout)
            var projected = q.Select(n => new
            {
                n.m_alpha_long,
                n.network,
                s = selector(n)
            });

            var rows = await ExecuteHeavyQueryAsync(projected);

            // aggregate in memory
            return rows
                .Where(r => !string.IsNullOrWhiteSpace(r.s))
                .GroupBy(r => new { r.m_alpha_long, r.network })
                .Select(g => new OpNetValueDto
                {
                    operatorName = g.Key.m_alpha_long,
                    network = g.Key.network,
                    value = Math.Round(
                        g.Select(x => SafeParse(x.s))
                            .Where(v => v > 0)
                            .DefaultIfEmpty(0)
                            .Average(), 2)
                })
                .Where(x => x.value > 0)
                .OrderByDescending(x => x.value)
                .ToList();
        }

       [HttpGet("TotalsV2")]
public async Task<IActionResult> TotalsV2([FromQuery] int? company_id = null)
{
    // =========================================================
    // 1. SMART SECURITY: RESOLVE COMPANY ID
    // =========================================================
    // Determine the target company based on user role and input.
    int targetCompanyId = GetTargetCompanyId(company_id);
    int currentUserId = _userScope.GetCurrentUserId(User);
    bool useUserScope = !_userScope.IsSuperAdmin(User) && targetCompanyId == 0 && currentUserId > 0;

    // If no valid company is found and user is NOT a Super Admin, deny access.
    if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope)
    {
        return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
    }

    // =========================================================
    // 2. DEFINE CACHE KEY (Include Company ID)
    // =========================================================
    // Cache must be isolated per company so Company A doesn't see Company B's totals.
    string cacheKey = $"DashboardTotalsV2:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}";

    // =========================================================
    // 3. EXECUTE SAFE QUERY HANDLER
    // =========================================================
    // We pass the cache key and logic to our SafeOkV2 helper (assuming it accepts these params).
    // Note: I expanded the lambda to handle the logic explicitly here for clarity.
    
    return await SafeOkV2(nameof(TotalsV2), async () =>
    {
        var today = DateTime.Today;

        // --- 1. Total Sessions (Filtered by Company) ---
        // Join Session -> User to check company_id
        var totalSessions = await (
            from s in db.tbl_session.AsNoTracking()
            join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
            where useUserScope
                ? s.user_id == currentUserId
                : (targetCompanyId == 0 || u.company_id == targetCompanyId)
            select s
        ).CountAsync();

        // --- 2. Total Online Sessions (Filtered by Company) ---
        var totalOnlineSessions = await (
            from s in db.tbl_session.AsNoTracking()
            join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
            where (useUserScope
                   ? s.user_id == currentUserId
                   : (targetCompanyId == 0 || u.company_id == targetCompanyId))
               && s.start_time != null 
               && s.end_time == null 
               && s.start_time.Value.Date == today
            select s
        ).CountAsync();

        // --- 3. Total Samples (Filtered by Company) ---
        // This is the heaviest query. We must join Log -> Session -> User.
        var totalSamples = await (
            from n in db.tbl_network_log.AsNoTracking()
            join s in db.tbl_session.AsNoTracking() on n.session_id equals s.id
            join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
            where useUserScope
                ? s.user_id == currentUserId
                : (targetCompanyId == 0 || u.company_id == targetCompanyId)
            select n
        ).CountAsync();

        // --- 4. Total Users (Filtered by Company) ---
        var totalUsers = await db.tbl_user.AsNoTracking()
            .Where(u => useUserScope
                ? u.id == currentUserId
                : (targetCompanyId == 0 || u.company_id == targetCompanyId))
            .CountAsync();

        return new
        {
            totalSessions,
            totalOnlineSessions,
            totalSamples,
            totalUsers
        };
    }, cacheKey, ttlSeconds: 60); // Short TTL (1 min) as online status changes fast
}
        [HttpGet, Route("GetNetworkDurations")]
        public async Task<IActionResult> GetNetworkDurations(
       [FromQuery] DateTime? fromDate,
       [FromQuery] DateTime? toDate,
       [FromQuery] string provider = null,   // e.g. "a", "v", "j", "air", "vod", "jio"
       [FromQuery] string network = null,    // optional
       [FromQuery] int? company_id = null)   // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
          int targetCompanyId = GetTargetCompanyId(company_id);
    int currentUserId = GetCurrentUserId();
    bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
    if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}
            // 2. PREPARE FILTERS & CACHE KEY
            // =========================================================
            var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
            var normalizedNetwork = string.IsNullOrWhiteSpace(network) ? null : network.Trim().ToLowerInvariant();

            // Include CompanyID in cache key for isolation
            var cacheKey = $"NetDur:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{fromDate:yyyyMMdd}:{toDate:yyyyMMdd}:{normalizedProvider ?? "ALL"}:{normalizedNetwork ?? "ALL"}";

            var result = new List<object>();
            var conn = db.Database.GetDbConnection();
            var shouldClose = false;

            try
            {
                var cached = await TryGetCachedObjectAsync<List<object>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new
                    {
                        Status = 1,
                        CompanyID = targetCompanyId,
                        FromDate = fromDate,
                        ToDate = toDate,
                        ProviderFilter = provider,
                        NetworkFilter = network,
                        Count = cached.Count,
                        Data = cached
                    });
                }

                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                var sql = @"
                SELECT
                    t.provider_name,
                    t.network,
                    SUM(t.time_diff_seconds) AS total_duration_seconds
                FROM (
                    SELECT
                        l.m_alpha_long AS provider_name,
                        l.network,
                        l.session_id,
                        TIMESTAMPDIFF(
                            SECOND,
                            LAG(l.timestamp) OVER (
                                PARTITION BY l.session_id, l.network, l.m_alpha_long
                                ORDER BY l.timestamp
                            ),
                            l.timestamp
                        ) AS time_diff_seconds
                    FROM tbl_network_log l
                    JOIN tbl_session s ON l.session_id = s.id
                    JOIN tbl_user u ON s.user_id = u.id
                    WHERE (@global = 1 OR u.company_id = @companyId OR (@userId > 0 AND s.user_id = @userId))
                      AND (@fromDate IS NULL OR l.timestamp >= @fromDate)
                      AND (@toDate   IS NULL OR l.timestamp <= @toDate)
                      AND (@provider IS NULL OR LOWER(l.m_alpha_long) LIKE CONCAT('%', @provider, '%'))
                      AND (@network  IS NULL OR LOWER(l.network) = @network)
                ) AS t
                WHERE t.time_diff_seconds IS NOT NULL
                  AND t.time_diff_seconds > 0
                GROUP BY t.provider_name, t.network
                ORDER BY total_duration_seconds DESC;";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;

                Add(cmd, "@global", _userScope.IsSuperAdmin(User) && targetCompanyId == 0 ? 1 : 0);
                Add(cmd, "@companyId", targetCompanyId);
                Add(cmd, "@userId", useUserScope ? currentUserId : 0);
                Add(cmd, "@fromDate", fromDate);
                Add(cmd, "@toDate", toDate);
                Add(cmd, "@provider", normalizedProvider);
                Add(cmd, "@network", normalizedNetwork);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string providerVal = reader.IsDBNull(0) ? null : reader.GetString(0);
                    string networkVal = reader.IsDBNull(1) ? null : reader.GetString(1);
                    long durationSeconds = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));

                    result.Add(new
                    {
                        Provider = providerVal,
                        Network = networkVal,
                        TotalDurationSeconds = durationSeconds,
                        TotalDurationMinutes = Math.Round(durationSeconds / 60.0, 2),
                        TotalDurationHours = Math.Round(durationSeconds / 3600.0, 2)
                    });
                }

                await CacheObjectAsync(cacheKey, result, 300);

                return Ok(new
                {
                    Status = 1,
                    CompanyID = targetCompanyId,
                    FromDate = fromDate,
                    ToDate = toDate,
                    ProviderFilter = provider,
                    NetworkFilter = network,
                    Count = result.Count,
                    Data = result
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = $"Error calculating network durations: {ex.Message}" });
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }
        public class CoverageHoleDto
        {
            public long id { get; set; }
            public long session_id { get; set; }

            public double? lat { get; set; }
            public double? lon { get; set; }
            public string? network_id { get; set; }

            public double? rsrp { get; set; }
            public double? rsrq { get; set; }

            public double threshold_rsrp { get; set; }
            public double? threshold_rsrq { get; set; }
        }
        [HttpGet("holes")]
        public async Task<IActionResult> GetCoverageHoles()
        {
            // =====================================
            // 1 Get coveragehole_json from thresholds
            // =====================================
            var threshold = await db.thresholds
                .AsNoTracking()
                .Where(x => x.coveragehole_json != null && x.coveragehole_json != "")
                .OrderByDescending(x => x.id)
                .FirstOrDefaultAsync();

            if (threshold == null)
            {
                return Ok(new
                {
                    Status = 0,
                    Message = "coveragehole_json not found",
                    Data = new List<object>()
                });
            }

            // =====================================
            // 2? Parse coveragehole_json
            // =====================================
            double rsrpThreshold;
            double? rsrqThreshold = null;

            var raw = threshold.coveragehole_json.Trim();

            // Case: "-110"
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var onlyRsrp))
            {
                rsrpThreshold = onlyRsrp;
            }
            else
            {
                // Case: { "rsrp": -110, "rsrq": -18 }
                try
                {
                    var json = JObject.Parse(raw);

                    if (json["rsrp"] == null)
                    {
                        return Ok(new
                        {
                            Status = 0,
                            Message = "coveragehole_json missing rsrp",
                            RawValue = raw,
                            Data = new List<object>()
                        });
                    }

                    rsrpThreshold = json["rsrp"]!.Value<double>();
                    rsrqThreshold = json["rsrq"]?.Value<double>();
                }
                catch
                {
                    return Ok(new
                    {
                        Status = 0,
                        Message = "Invalid coveragehole_json format",
                        RawValue = raw,
                        Data = new List<object>()
                    });
                }
            }

            var thresholdHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(raw)));
            var cacheKey = $"coverageholes:{threshold.id}:{thresholdHash}";

            var cached = await TryGetCachedObjectAsync<List<object>>(cacheKey);
            if (cached != null)
            {
                return Ok(new
                {
                    Status = 1,
                    Message = "Coverage holes fetched successfully",
                    ThresholdUsed = new
                    {
                        rsrp = rsrpThreshold,
                        rsrq = rsrqThreshold
                    },
                    Count = cached.Count,
                    Data = cached
                });
            }

            // =====================================
            // 3? Query tbl_network_log (ONLY DB DATA)
            // =====================================
            var query = db.tbl_network_log
                .AsNoTracking()
                .Where(x =>
                    x.rsrp.HasValue &&
                    x.rsrp.Value < rsrpThreshold
                );

            if (rsrqThreshold.HasValue)
            {
                query = query.Where(x =>
                    x.rsrq.HasValue &&
                    x.rsrq.Value < rsrqThreshold.Value
                );
            }

            // =====================================
            // 4? Fetch required fields
            // =====================================
            var data = await query
                .Select(x => new
                {
                    x.id,
                    x.session_id,

                    //  FROM tbl_network_log ONLY
                    x.lat,
                    x.lon,
                    x.network,
                    x.m_alpha_long,

                    rsrp = (double?)x.rsrp,
                    rsrq = (double?)x.rsrq
                })
                .Take(5000)
                .ToListAsync();

            await CacheObjectAsync(cacheKey, data.Select(x => (object)x).ToList(), 300);

            // =====================================
            // 5? Final response
            // =====================================
            return Ok(new
            {
                Status = 1,
                Message = "Coverage holes fetched successfully",
                ThresholdUsed = new
                {
                    rsrp = rsrpThreshold,
                    rsrq = rsrqThreshold
                },
                Count = data.Count,
                Data = data
            });
        }

        private void TryParseCoverageHole(string raw, out double? rsrp, out double? rsrq)
        {
            rsrp = rsrq = null;

            // JSON object / array
            try
            {
                var token = JToken.Parse(raw);
                JObject obj = token.Type == JTokenType.Array
                    ? (JObject)token.First!
                    : (JObject)token;

                var r1 = obj.Properties()
                    .FirstOrDefault(p => p.Name.Equals("rsrp", StringComparison.OrdinalIgnoreCase));
                var r2 = obj.Properties()
                    .FirstOrDefault(p => p.Name.Equals("rsrq", StringComparison.OrdinalIgnoreCase));

                if (r1 != null) rsrp = r1.Value.Value<double>();
                if (r2 != null) rsrq = r2.Value.Value<double>();

                if (rsrp.HasValue) return;
            }
            catch { }

            // key:value text
            var m = System.Text.RegularExpressions.Regex.Match(
                raw,
                @"rsrp\s*[:=]\s*(-?\d+(\.\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (m.Success)
                rsrp = double.Parse(m.Groups[1].Value);

            m = System.Text.RegularExpressions.Regex.Match(
                raw,
                @"rsrq\s*[:=]\s*(-?\d+(\.\d+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (m.Success)
                rsrq = double.Parse(m.Groups[1].Value);
        }

[HttpGet("MonthlySamplesV2")]
public async Task<IActionResult> MonthlySamplesV2(
    [FromQuery] DateTime? from,
    [FromQuery] DateTime? to,
    [FromQuery] int? company_id = null)
{
    // 1. Resolve Company ID from the st.auth cookie claims securely
    int targetCompanyId = GetTargetCompanyId(company_id);
    int currentUserId = GetCurrentUserId();
    bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
    if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}

    // Default to last 90 days when no range is provided to avoid full-table scans.
    var effectiveTo = to ?? DateTime.UtcNow;
    var effectiveFrom = from ?? effectiveTo.AddDays(-90);
    if (effectiveFrom > effectiveTo)
    {
        var tmp = effectiveFrom;
        effectiveFrom = effectiveTo;
        effectiveTo = tmp;
    }

    // 2. Build a unique cache key for this specific request
    string fromKey = effectiveFrom.ToString("yyyyMMdd");
    string toKey = effectiveTo.ToString("yyyyMMdd");
    string cacheKey = $"MonthlySamples:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{fromKey}:{toKey}";

    // 3. Try to fetch data from Redis Cache
    if (_redis != null && _redis.IsConnected)
    {
        try 
        {
            var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
            if (cached != null) 
            {
                return Ok(new { Status = 1, Source = "REDIS", Data = cached });
            }
        }
        catch { /* Fallback to DB if Redis fails */ }
    }

    try
    {
        var monthlyData = new List<(int Year, int Month, int Count)>();

        if (targetCompanyId == 0 && !useUserScope)
        {
            var rows = await db.tbl_network_log.AsNoTracking()
                .Where(x => x.timestamp.HasValue
                         && x.timestamp.Value >= effectiveFrom
                         && x.timestamp.Value < effectiveTo.AddDays(1))
                .GroupBy(x => new { x.timestamp.Value.Year, x.timestamp.Value.Month })
                .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                .OrderBy(x => x.Year)
                .ThenBy(x => x.Month)
                .ToListAsync();

            monthlyData = rows.Select(r => (r.Year, r.Month, r.Count)).ToList();
        }
        else
        {
            var rows = await (
                from l in db.tbl_network_log.AsNoTracking()
                join s in db.tbl_session.AsNoTracking() on l.session_id equals s.id
                join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                where (useUserScope ? s.user_id == currentUserId : u.company_id == targetCompanyId)
                   && l.timestamp.HasValue
                   && l.timestamp.Value >= effectiveFrom
                   && l.timestamp.Value < effectiveTo.AddDays(1)
                group l by new { l.timestamp.Value.Year, l.timestamp.Value.Month } into g
                orderby g.Key.Year, g.Key.Month
                select new { g.Key.Year, g.Key.Month, Count = g.Count() }
            ).ToListAsync();

            monthlyData = rows.Select(r => (r.Year, r.Month, r.Count)).ToList();
        }

        // 5. Format result for the chart (YYYY-MM format)
        var result = monthlyData
            .Select(x => new
            {
                month = $"{x.Year:D4}-{x.Month:D2}",
                count = x.Count
            })
            .ToList<object>();

        // 6. Save result to Redis with a 10-minute TTL
        if (_redis != null && _redis.IsConnected && result.Any())
        {
            await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
        }

        return Ok(new { Status = 1, Source = "DATABASE", Data = result });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { Status = 0, Message = "Error fetching monthly samples: " + ex.Message });
    }
}
        
        
        
        
        [HttpGet("GetSessionTechMinutesFilter")]
        public async Task<IActionResult> GetSessionTechMinutesFilter(
            [FromQuery] long session_id,
            [FromQuery] string provider = null,   // jio / airtel / vi / vodafone
            [FromQuery] string tech = null        // 4g / 5g
        )
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (session_id <= 0)
                return BadRequest(new { Status = 0, Message = "Invalid session_id" });

            try
            {
                string normalizedProvider = string.IsNullOrWhiteSpace(provider)
                    ? null
                    : provider.Trim().ToLowerInvariant();

                string normalizedTech = string.IsNullOrWhiteSpace(tech)
                    ? null
                    : tech.Trim().ToLowerInvariant();

                // ========================================
                //  BUILD CACHE KEY
                // ========================================
                string cacheKey = $"sesstechmin:{session_id}:{normalizedProvider ?? "all"}:{normalizedTech ?? "all"}";

                // ========================================
                //  TRY GET FROM REDIS CACHE
                // ========================================
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cacheStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var cached = await _redis.GetObjectAsync<SessionTechMinutesResponse>(cacheKey);
                        cacheStopwatch.Stop();

                        if (cached != null)
                        {
                            totalStopwatch.Stop();
                            Console.WriteLine($" Cache HIT: {cacheKey}");
                            Console.WriteLine($"    Cache lookup: {cacheStopwatch.ElapsedMilliseconds}ms");
                            Console.WriteLine($"    Total time: {totalStopwatch.ElapsedMilliseconds}ms");

                            Response.Headers["X-Cache"] = "HIT";
                            Response.Headers["X-Cache-Lookup-Ms"] = cacheStopwatch.ElapsedMilliseconds.ToString();
                            Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();

                            return Ok(new
                            {
                                Status = 1,
                                SessionID = session_id,
                                ProviderFilter = provider,
                                TechFilter = tech,
                                Data = cached.Data,
                                CachedAt = cached.CachedAt
                            });
                        }

                        Console.WriteLine($" Cache MISS: {cacheKey}");
                    }
                    catch (Exception redisEx)
                    {
                        Console.WriteLine($" Redis read error: {redisEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine(" Redis not available, skipping cache lookup");
                }

                // ========================================
                //  FETCH FROM DATABASE
                // ========================================
                var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var conn = db.Database.GetDbConnection();
                bool shouldClose = false;
                var data = new List<SessionTechMinuteItem>();

                try
                {
                    if (conn.State != System.Data.ConnectionState.Open)
                    {
                        await conn.OpenAsync();
                        shouldClose = true;
                    }

                    string sql = @"
WITH base AS (
    SELECT
        session_id,
        m_alpha_long AS provider,
        CASE
            WHEN LOWER(network) LIKE '%5g%' OR LOWER(network) LIKE '%nr%' THEN '5G'
            WHEN LOWER(network) LIKE '%4g%' OR LOWER(network) LIKE '%lte%' THEN '4G'
            ELSE 'Unknown'
        END AS tech,
        timestamp,
        LAG(timestamp) OVER (
            PARTITION BY session_id
            ORDER BY timestamp
        ) AS prev_ts
    FROM tbl_network_log
    WHERE session_id = @sid
      AND primary_cell_info_1 LIKE '%mRegistered=YES%'
      AND (@provider IS NULL OR LOWER(m_alpha_long) LIKE CONCAT('%', @provider, '%'))
)
SELECT 
    provider,
    tech,
    SUM(TIMESTAMPDIFF(SECOND, prev_ts, timestamp)) AS seconds
FROM base
WHERE prev_ts IS NOT NULL
  AND (@tech IS NULL OR LOWER(tech) = @tech)
GROUP BY provider, tech;
";

                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 300;

                    Add(cmd, "@sid", session_id);
                    Add(cmd, "@provider", normalizedProvider);
                    Add(cmd, "@tech", normalizedTech);

                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        string prov = reader.IsDBNull(0) ? null : reader.GetString(0);
                        string t = reader.IsDBNull(1) ? null : reader.GetString(1);
                        long sec = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);

                        data.Add(new SessionTechMinuteItem
                        {
                            Provider = prov,
                            Technology = t,
                            Seconds = sec,
                            Minutes = Math.Round(sec / 60.0, 2),
                            Hours = Math.Round(sec / 3600.0, 2)
                        });
                    }
                }
                finally
                {
                    if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                        await conn.CloseAsync();
                }

                dbStopwatch.Stop();
                Console.WriteLine($"    Database query: {dbStopwatch.ElapsedMilliseconds}ms");
                Console.WriteLine($"    Records found: {data.Count}");

                // ========================================
                // BUILD RESPONSE & CACHE IT
                // ========================================
                var response = new SessionTechMinutesResponse
                {
                    Data = data,
                    CachedAt = DateTime.UtcNow
                };

                // Cache the response (5 minutes TTL)
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cacheWriteStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        await _redis.SetObjectAsync(cacheKey, response, ttlSeconds: 300);
                        cacheWriteStopwatch.Stop();

                        Console.WriteLine($" Cached: {cacheKey} (TTL: 300s)");
                        Console.WriteLine($"    Cache write: {cacheWriteStopwatch.ElapsedMilliseconds}ms");
                    }
                    catch (Exception redisEx)
                    {
                        Console.WriteLine($" Failed to cache: {redisEx.Message}");
                    }
                }

                totalStopwatch.Stop();
                Console.WriteLine($"   ?? Total time: {totalStopwatch.ElapsedMilliseconds}ms");

                Response.Headers["X-Cache"] = "MISS";
                Response.Headers["X-Database-Ms"] = dbStopwatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Total-Ms"] = totalStopwatch.ElapsedMilliseconds.ToString();
                Response.Headers["X-Record-Count"] = data.Count.ToString();

                return Ok(new
                {
                    Status = 1,
                    SessionID = session_id,
                    ProviderFilter = provider,
                    TechFilter = tech,
                    Data = data
                });
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                Console.WriteLine($" ERROR in GetSessionTechMinutesFilter: {ex.Message}");
                Console.WriteLine($" Stack Trace: {ex.StackTrace}");

                return StatusCode(500, new
                {
                    Status = 0,
                    Message = $"Error: {ex.Message}",
                    StackTrace = ex.StackTrace
                });
            }
        }

        // ========================================
        //  RESPONSE DTOs FOR CACHING
        // ========================================
        public class SessionTechMinutesResponse
        {
            public List<SessionTechMinuteItem> Data { get; set; } = new();
            public DateTime CachedAt { get; set; }
        }

        public class SessionTechMinuteItem
        {
            public string Provider { get; set; }
            public string Technology { get; set; }
            public long Seconds { get; set; }
            public double Minutes { get; set; }
            public double Hours { get; set; }
        }
        private static double ToDoubleSafe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            return double.TryParse(value, out var v) ? v : 0;
        }

        [HttpGet("OperatorSamplesV2")]
        public async Task<IActionResult> OperatorSamplesV2(
            string operatorName,
            string networkType,
            DateTime? from,
            DateTime? to,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = GetTargetCompanyId(company_id);
            int currentUserId = GetCurrentUserId();
            bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}
            var opSet = ParseCsv(operatorName);
            if (opSet != null)
            {
                opSet.Remove("ALL");
                opSet = opSet
                    .Select(NormalizeOperatorBucket)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (opSet.Count == 0) opSet = null;
            }

            var netSet = ParseCsv(networkType);
            if (netSet != null)
            {
                netSet.Remove("ALL");
                netSet = netSet
                    .Select(NormalizeNetworkBucket)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (netSet.Count == 0) netSet = null;
            }
       
            string fromKey = from?.ToString("yyyyMMdd") ?? "null";
            string toKey = to?.ToString("yyyyMMdd") ?? "null";
            string opKey = opSet != null
                ? string.Join(",", opSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : "ALL";
            string netKey = netSet != null
                ? string.Join(",", netSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : "ALL";

            string cacheKey = $"OpSamples:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{opKey}:{netKey}:{fromKey}:{toKey}";

            // =========================================================
            // 3. TRY REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<OpNetValueDto>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 4. EXECUTE SECURE SQL (ADO.NET)
            // =========================================================
            var result = new List<OpNetValueDto>();
            var conn = db.Database.GetDbConnection();
            bool shouldClose = false;

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandTimeout = 180;

                    var companyClause = targetCompanyId == 0 && !useUserScope
                        ? "1=1"
                        : (useUserScope ? "s.user_id = @userId" : "u.company_id = @companyId");
                    var sqlBuilder = new StringBuilder(@"
                SELECT 
                    t.operatorName,
                    t.network,
                    COUNT(*) AS value,
                    ROUND(AVG(COALESCE(t.rsrp, 0)), 2) AS avg_rsrp,
                    ROUND(AVG(COALESCE(t.rsrq, 0)), 2) AS avg_rsrq,
                    ROUND(AVG(COALESCE(t.sinr, 0)), 2) AS avg_sinr,
                    ROUND(AVG(COALESCE(t.mos, 0)), 2) AS avg_mos,
                    ROUND(AVG(COALESCE(t.jitter, 0)), 2) AS avg_jitter,
                    ROUND(AVG(COALESCE(t.packet_loss, 0)), 2) AS avg_packet_loss
                FROM (
                    SELECT
                        CASE
                            WHEN UPPER(TRIM(n.m_alpha_long)) LIKE '%AIRTEL%' THEN 'Airtel'
                            WHEN UPPER(TRIM(n.m_alpha_long)) LIKE '%JIO%' OR UPPER(TRIM(n.m_alpha_long)) LIKE '%JIOTRUE%' THEN 'Jio'
                            WHEN UPPER(TRIM(n.m_alpha_long)) = 'VI'
                              OR UPPER(TRIM(n.m_alpha_long)) LIKE '%VODAFONE%'
                              OR UPPER(TRIM(n.m_alpha_long)) LIKE '%IDEA%'
                              OR UPPER(TRIM(n.m_alpha_long)) LIKE '%VI INDIA%' THEN 'VI India'
                            WHEN UPPER(TRIM(n.m_alpha_long)) LIKE '%BSNL%' THEN 'BSNL'
                            ELSE TRIM(n.m_alpha_long)
                        END AS operatorName,
                        CASE
                            WHEN UPPER(TRIM(n.network)) LIKE '%5G%' 
                              OR UPPER(TRIM(n.network)) LIKE '%NR%'
                              OR UPPER(TRIM(n.network)) LIKE '%NSA%'
                              OR UPPER(TRIM(n.network)) = 'SA'
                              OR UPPER(TRIM(n.network)) LIKE '% SA%' THEN '5G'
                            WHEN UPPER(TRIM(n.network)) LIKE '%4G%' OR UPPER(TRIM(n.network)) LIKE '%LTE%' THEN '4G'
                            WHEN UPPER(TRIM(n.network)) LIKE '%3G%'
                              OR UPPER(TRIM(n.network)) LIKE '%WCDMA%'
                              OR UPPER(TRIM(n.network)) LIKE '%UMTS%'
                              OR UPPER(TRIM(n.network)) LIKE '%HSPA%' THEN '3G'
                            WHEN UPPER(TRIM(n.network)) LIKE '%2G%'
                              OR UPPER(TRIM(n.network)) LIKE '%EDGE%'
                              OR UPPER(TRIM(n.network)) LIKE '%GSM%'
                              OR UPPER(TRIM(n.network)) LIKE '%GPRS%' THEN '2G'
                            ELSE TRIM(n.network)
                        END AS network,
                        n.rsrp,
                        n.rsrq,
                        n.sinr,
                        n.mos,
                        n.jitter,
                        n.packet_loss
                    FROM tbl_network_log n
                    JOIN tbl_session s ON n.session_id = s.id
                    JOIN tbl_user u ON s.user_id = u.id
                    WHERE __COMPANY_CLAUSE__
                      AND n.m_alpha_long IS NOT NULL 
                      AND TRIM(n.m_alpha_long) <> ''
                      AND n.network IS NOT NULL 
                      AND TRIM(n.network) <> ''
                      AND (@from IS NULL OR n.timestamp >= @from)
                      AND (@to IS NULL OR n.timestamp < DATE_ADD(@to, INTERVAL 1 DAY))
                ) t
                WHERE 1=1
            ");

                    if (opSet is { Count: > 0 })
                    {
                        var opParams = new List<string>();
                        int i = 0;
                        foreach (var op in opSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            string paramName = $"@op{i++}";
                            var p = cmd.CreateParameter();
                            p.ParameterName = paramName;
                            p.Value = op;
                            cmd.Parameters.Add(p);
                            opParams.Add(paramName);
                        }

                        sqlBuilder.AppendLine($"  AND t.operatorName IN ({string.Join(", ", opParams)})");
                    }

                    if (netSet is { Count: > 0 })
                    {
                        var netParams = new List<string>();
                        int i = 0;
                        foreach (var net in netSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            string paramName = $"@net{i++}";
                            var p = cmd.CreateParameter();
                            p.ParameterName = paramName;
                            p.Value = net;
                            cmd.Parameters.Add(p);
                            netParams.Add(paramName);
                        }

                        sqlBuilder.AppendLine($"  AND t.network IN ({string.Join(", ", netParams)})");
                    }

                    sqlBuilder.AppendLine("GROUP BY t.operatorName, t.network");
                    sqlBuilder.AppendLine("ORDER BY value DESC;");

                    cmd.CommandText = sqlBuilder.ToString().Replace("__COMPANY_CLAUSE__", companyClause);

                    // Parameters
                    if (targetCompanyId > 0)
                    {
                        var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);
                    }
                    if (useUserScope)
                    {
                        var pUser = cmd.CreateParameter(); pUser.ParameterName = "@userId"; pUser.Value = currentUserId; cmd.Parameters.Add(pUser);
                    }

                    var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@from"; pFrom.Value = (object?)from ?? DBNull.Value; cmd.Parameters.Add(pFrom);

                    var pTo = cmd.CreateParameter(); pTo.ParameterName = "@to"; pTo.Value = (object?)to ?? DBNull.Value; cmd.Parameters.Add(pTo);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new OpNetValueDto
                            {
                                operatorName = reader.IsDBNull(0) ? null : reader.GetString(0),
                                network = reader.IsDBNull(1) ? null : reader.GetString(1),
                                value = reader.GetInt32(2),
                                avg_rsrp = reader.GetDouble(3),
                                avg_rsrq = reader.GetDouble(4),
                                avg_sinr = reader.GetDouble(5),
                                avg_mos = reader.GetDouble(6),
                                avg_jitter = reader.GetDouble(7),
                                avg_packet_loss = reader.GetDouble(8)
                            });
                        }
                    }
                }
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =========================================================
            // 5. SAVE TO REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
            }

            return Ok(new { Status = 1, Source = "DATABASE", Data = result });
        }
        [HttpGet("OperatorAvgThroughput10SecV2")]
        public async Task<IActionResult> OperatorAvgThroughput10SecV2(
            string operatorName,
            string networkType,
            DateTime? from,
            DateTime? to,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = 0;
            bool isAuthorized = false;

            if (company_id.HasValue && company_id.Value > 0)
            {
                targetCompanyId = company_id.Value;
                isAuthorized = true;
            }
            else
            {
                string token = string.Empty;
                if (Request.Headers.ContainsKey("Authorization"))
                {
                    token = Request.Headers["Authorization"].ToString();
                    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = token.Substring(7).Trim();
                }
                else if (Request.Query.ContainsKey("token"))
                {
                    token = Request.Query["token"].ToString();
                }

                if (!string.IsNullOrEmpty(token))
                {
                    var user = await db.tbl_user.AsNoTracking()
                        .Select(u => new { u.token, u.company_id, u.isactive })
                        .FirstOrDefaultAsync(u => u.token == token && u.isactive == 1);

                    if (user != null)
                    {
                        targetCompanyId = user.company_id ?? 0;
                        isAuthorized = true;
                    }
                }
            }

            if (!isAuthorized || targetCompanyId == 0)
            {
                return StatusCode(401, new { Status = 0, Message = "Unauthorized. Please provide either a valid 'company_id' or a valid 'token'." });
            }

            // =========================================================
            // 2. VALIDATE DATES (7-Day Hard Limit)
            // =========================================================
            var toDate = to ?? DateTime.UtcNow;
            var fromDate = from ?? toDate.AddDays(-1);

            if ((toDate - fromDate).TotalDays > 7)
            {
                return BadRequest(new { Status = 0, Message = "Maximum 7 days date range allowed" });
            }

            // =========================================================
            // 3. CACHE KEY (Includes Company ID)
            // =========================================================
            string opKey = operatorName?.ToLower() ?? "all";
            string netKey = networkType?.ToLower() ?? "all";
            string cacheKey = $"OpAvgTpt10:{targetCompanyId}:{opKey}:{netKey}:{fromDate:yyyyMMdd}:{toDate:yyyyMMdd}";

            // =========================================================
            // 4. TRY REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 5. EXECUTE SECURE SQL (ADO.NET)
            // =========================================================
            var result = new List<object>();
            var conn = db.Database.GetDbConnection();
            bool shouldClose = false;

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                // Set Session Timeouts for heavy query
                await db.Database.ExecuteSqlRawAsync("SET SESSION max_execution_time = 10000;");

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandTimeout = 20;

                    // JOIN Log -> Session -> User to strictly filter by u.company_id
                    // Note: We used REGEXP for number validation as it's more standard in MySQL than LIKE '%[^0-9]%'
                    string sql = @"
            SELECT
                t.m_alpha_long AS operatorName,
                t.network,

                COALESCE(ROUND(AVG(
                    CASE 
                        WHEN t.dl_tpt IS NOT NULL 
                         AND t.dl_tpt REGEXP '^[0-9]+(\\.[0-9]+)?$' 
                        THEN CAST(t.dl_tpt AS DECIMAL(10,2)) 
                    END
                ), 2), 0) AS avg_dl_tpt,

                COALESCE(ROUND(AVG(
                    CASE 
                        WHEN t.ul_tpt IS NOT NULL 
                         AND t.ul_tpt REGEXP '^[0-9]+(\\.[0-9]+)?$' 
                        THEN CAST(t.ul_tpt AS DECIMAL(10,2)) 
                    END
                ), 2), 0) AS avg_ul_tpt

            FROM tbl_network_log t
            JOIN tbl_session s ON t.session_id = s.id
            JOIN tbl_user u ON s.user_id = u.id
            WHERE u.company_id = @companyId      -- <--- SECURITY FILTER
              AND t.timestamp >= @fromDate
              AND t.timestamp < DATE_ADD(@toDate, INTERVAL 1 DAY)
              AND (@op IS NULL OR LOWER(t.m_alpha_long) LIKE CONCAT('%', @op, '%'))
              AND (@net IS NULL OR LOWER(t.network) LIKE CONCAT('%', @net, '%'))
            GROUP BY t.m_alpha_long, t.network
            ORDER BY avg_dl_tpt DESC
            LIMIT 50;";

                    cmd.CommandText = sql;

                    // Parameters
                    var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);
                    var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@fromDate"; pFrom.Value = fromDate; cmd.Parameters.Add(pFrom);
                    var pTo = cmd.CreateParameter(); pTo.ParameterName = "@toDate"; pTo.Value = toDate; cmd.Parameters.Add(pTo);

                    var pOp = cmd.CreateParameter();
                    pOp.ParameterName = "@op";
                    pOp.Value = string.IsNullOrWhiteSpace(operatorName) ? DBNull.Value : operatorName.ToLower();
                    cmd.Parameters.Add(pOp);

                    var pNet = cmd.CreateParameter();
                    pNet.ParameterName = "@net";
                    pNet.Value = string.IsNullOrWhiteSpace(networkType) ? DBNull.Value : networkType.ToLower();
                    cmd.Parameters.Add(pNet);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new
                            {
                                operatorName = reader.IsDBNull(0) ? null : reader.GetString(0),
                                network = reader.IsDBNull(1) ? null : reader.GetString(1),
                                avg_dl_tpt = reader.GetDecimal(2),
                                avg_ul_tpt = reader.GetDecimal(3)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + ex.Message });
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =========================================================
            // 6. SAVE TO REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
            }

            return Ok(new { Status = 1, Source = "DATABASE", Data = result });
        }

        public class ThroughputDto
        {
            public string operatorName { get; set; }
            public string network { get; set; }
            public double avg_dl_tpt { get; set; }
            public double avg_ul_tpt { get; set; }
        }


        [HttpGet("NetworkTypeDistributionV2")]
        public async Task<IActionResult> NetworkTypeDistributionV2(
    string operatorName,
    DateTime? from,
    DateTime? to,
    [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = 0;
            bool isAuthorized = false;

            if (company_id.HasValue && company_id.Value > 0)
            {
                targetCompanyId = company_id.Value;
                isAuthorized = true;
            }
            else
            {
                string token = string.Empty;
                if (Request.Headers.ContainsKey("Authorization"))
                {
                    token = Request.Headers["Authorization"].ToString();
                    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = token.Substring(7).Trim();
                }
                else if (Request.Query.ContainsKey("token"))
                {
                    token = Request.Query["token"].ToString();
                }

                if (!string.IsNullOrEmpty(token))
                {
                    var user = await db.tbl_user.AsNoTracking()
                        .Select(u => new { u.token, u.company_id, u.isactive })
                        .FirstOrDefaultAsync(u => u.token == token && u.isactive == 1);

                    if (user != null)
                    {
                        targetCompanyId = user.company_id ?? 0;
                        isAuthorized = true;
                    }
                }
            }

            if (!isAuthorized || targetCompanyId == 0)
            {
                return StatusCode(401, new { Status = 0, Message = "Unauthorized. Please provide either a valid 'company_id' or a valid 'token'." });
            }

            // =========================================================
            // 2. CACHE KEY (Includes Company ID)
            // =========================================================
            string fromKey = from?.ToString("yyyyMMdd") ?? "null";
            string toKey = to?.ToString("yyyyMMdd") ?? "null";
            string opKey = operatorName ?? "ALL";
            string cacheKey = $"netdist:{targetCompanyId}:{opKey}:{fromKey}:{toKey}";

            // =========================================================
            // 3. TRY REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<OpNetValueDto>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 4. EXECUTE SECURE SQL
            // =========================================================
            var result = new List<OpNetValueDto>();
            var conn = db.Database.GetDbConnection();
            bool shouldClose = false;

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                using (var cmd = conn.CreateCommand())
                {
                    // JOIN Log -> Session -> User to strictly filter by u.company_id
                    string sql = @"
                SELECT 
                    n.m_alpha_long AS operatorName,
                    n.network,
                    COUNT(*) AS value
                FROM tbl_network_log n
                JOIN tbl_session s ON n.session_id = s.id
                JOIN tbl_user u ON s.user_id = u.id
                WHERE u.company_id = @companyId
                  AND n.m_alpha_long IS NOT NULL 
                  AND n.m_alpha_long <> ''
                  AND n.network IS NOT NULL 
                  AND n.network <> ''
                  AND (@from IS NULL OR n.timestamp >= @from)
                  AND (@to IS NULL OR n.timestamp < DATE_ADD(@to, INTERVAL 1 DAY))
                  AND (@opName IS NULL OR n.m_alpha_long = @opName)
                GROUP BY n.m_alpha_long, n.network
                ORDER BY value DESC;
            ";

                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;

                    // Parameters
                    var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);

                    var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@from"; pFrom.Value = (object?)from ?? DBNull.Value; cmd.Parameters.Add(pFrom);

                    var pTo = cmd.CreateParameter(); pTo.ParameterName = "@to"; pTo.Value = (object?)to ?? DBNull.Value; cmd.Parameters.Add(pTo);

                    var pOp = cmd.CreateParameter(); pOp.ParameterName = "@opName"; pOp.Value = (object?)operatorName ?? DBNull.Value; cmd.Parameters.Add(pOp);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new OpNetValueDto
                            {
                                operatorName = reader.GetString(0),
                                network = reader.GetString(1),
                                value = reader.GetInt32(2)
                            });
                        }
                    }
                }
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =========================================================
            // 5. SAVE TO REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
            }

            return Ok(new { Status = 1, Source = "DATABASE", Data = result });
        }
        // ===================================================================================
        //  SHARED HELPER FOR ALL AVG KPI ENDPOINTS (SMART SECURITY & ISOLATION)
        // ===================================================================================
        private async Task<IActionResult> GetAverageMetricGeneric(
            string operatorName,
            string networkType,
            DateTime? from,
            DateTime? to,
            int? company_id,
            string metricColumn,
            string metricName)
        {
            // ---------------------------------------------------------
            // 1. NORMALIZE INPUTS (Fixes empty string issues)
            // ---------------------------------------------------------
            if (string.IsNullOrWhiteSpace(operatorName)) operatorName = null;
            if (string.IsNullOrWhiteSpace(networkType)) networkType = null;

            // ---------------------------------------------------------
            // 2. SMART SECURITY: RESOLVE COMPANY ID
            // ---------------------------------------------------------
            int targetCompanyId = 0;
            bool isAuthorized = false;

            // Priority A: Explicit Company ID
            if (company_id.HasValue && company_id.Value > 0)
            {
                targetCompanyId = company_id.Value;
                isAuthorized = true;
            }
            // Priority B: Token from Header/Query
            else
            {
                string token = string.Empty;
                if (Request.Headers.ContainsKey("Authorization"))
                    token = Request.Headers["Authorization"].ToString().Replace("Bearer ", "").Trim();
                else if (Request.Query.ContainsKey("token"))
                    token = Request.Query["token"].ToString();

                if (!string.IsNullOrEmpty(token))
                {
                    var user = await db.tbl_user.AsNoTracking()
                        .Select(u => new { u.token, u.company_id, u.isactive })
                        .FirstOrDefaultAsync(u => u.token == token && u.isactive == 1);

                    if (user != null)
                    {
                        targetCompanyId = user.company_id ?? 0;
                        isAuthorized = true;
                    }
                }
            }

            if (!isAuthorized || targetCompanyId == 0)
            {
                return StatusCode(401, new { Status = 0, Message = "Unauthorized. Please provide either a valid 'company_id' or a valid 'token'." });
            }

            // DEBUG: Add header so you can verify which company is being used
            Response.Headers["X-Debug-Company-ID"] = targetCompanyId.ToString();

            // ---------------------------------------------------------
            // 3. CACHE KEY (Unique per Company + Filters)
            // ---------------------------------------------------------
            string fromKey = from?.ToString("yyyyMMdd") ?? "null";
            string toKey = to?.ToString("yyyyMMdd") ?? "null";
            string opKey = operatorName ?? "ALL";
            string netKey = networkType ?? "ALL";

            string cacheKey = $"Avg{metricName}:{targetCompanyId}:{opKey}:{netKey}:{fromKey}:{toKey}";

            // ---------------------------------------------------------
            // 4. TRY REDIS
            // ---------------------------------------------------------
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                if (cached != null) return Ok(new { Status = 1, Source = "REDIS", Data = cached });
            }

            // ---------------------------------------------------------
            // 5. EXECUTE SECURE SQL (ADO.NET)
            // ---------------------------------------------------------
            try
            {
                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

                using (var cmd = conn.CreateCommand())
                {
                    // JOIN Log -> Session -> User to strictly enforce Company Isolation
                    string sql = $@"
                SELECT 
                    n.m_alpha_long AS Operator,
                    n.network AS Network,
                    AVG({metricColumn}) AS AvgValue,
                    COUNT(*) AS Count
                FROM tbl_network_log n
                JOIN tbl_session s ON n.session_id = s.id
                JOIN tbl_user u ON s.user_id = u.id
                WHERE u.company_id = @companyId
                  AND n.m_alpha_long IS NOT NULL
                  AND n.network IS NOT NULL
                  AND (@from IS NULL OR n.timestamp >= @from)
                  AND (@to IS NULL OR n.timestamp < DATE_ADD(@to, INTERVAL 1 DAY))
                  AND (@opName IS NULL OR n.m_alpha_long = @opName)
                  AND (@netType IS NULL OR n.network = @netType)
                  -- Ensure we don't average nulls
                  AND {metricColumn} IS NOT NULL
                GROUP BY n.m_alpha_long, n.network
                ORDER BY n.m_alpha_long, n.network";

                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;

                    // Parameters
                    var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);

                    var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@from"; pFrom.Value = (object?)from ?? DBNull.Value; cmd.Parameters.Add(pFrom);

                    var pTo = cmd.CreateParameter(); pTo.ParameterName = "@to"; pTo.Value = (object?)to ?? DBNull.Value; cmd.Parameters.Add(pTo);

                    var pOp = cmd.CreateParameter(); pOp.ParameterName = "@opName"; pOp.Value = (object?)operatorName ?? DBNull.Value; cmd.Parameters.Add(pOp);

                    var pNet = cmd.CreateParameter(); pNet.ParameterName = "@netType"; pNet.Value = (object?)networkType ?? DBNull.Value; cmd.Parameters.Add(pNet);

                    var result = new List<object>();
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new
                            {
                                Operator = reader.IsDBNull(0) ? null : reader.GetString(0),
                                Network = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Average = reader.IsDBNull(2) ? 0 : Math.Round(reader.GetDouble(2), 2),
                                Count = reader.GetInt32(3)
                            });
                        }
                    }

                    // 6. SAVE TO REDIS
                    if (_redis != null && _redis.IsConnected)
                    {
                        await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
                    }

                    return Ok(new { Status = 1, Source = "DATABASE", Data = result });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + ex.Message });
            }
        }
        [HttpGet("AvgRsrpV2")]
        public Task<IActionResult> AvgRsrpV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.rsrp", "Rsrp");

        [HttpGet("AvgRsrqV2")]
        public Task<IActionResult> AvgRsrqV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.rsrq", "Rsrq");

        [HttpGet("AvgSinrV2")]
        public Task<IActionResult> AvgSinrV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.sinr", "Sinr");

        [HttpGet("AvgMosV2")]
        public Task<IActionResult> AvgMosV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.mos", "Mos");

        [HttpGet("AvgJitterV2")]
        public Task<IActionResult> AvgJitterV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.jitter", "Jitter");

        [HttpGet("AvgLatencyV2")]
        public Task<IActionResult> AvgLatencyV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.latency", "Latency");

        [HttpGet("AvgPacketLossV2")]
        public Task<IActionResult> AvgPacketLossV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "n.packet_loss", "PacketLoss");

        [HttpGet("AvgDlTptV2")]
        public Task<IActionResult> AvgDlTptV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "CAST(n.dl_tpt AS DECIMAL(18,4))", "DlTpt");

        [HttpGet("AvgUlTptV2")]
        public Task<IActionResult> AvgUlTptV2(string operatorName, string networkType, DateTime? from, DateTime? to, [FromQuery] int? company_id = null)
            => GetAverageMetricGeneric(operatorName, networkType, from, to, company_id, "CAST(n.ul_tpt AS DECIMAL(18,4))", "UlTpt");
        [HttpGet("BandDistributionV2")]
        public async Task<IActionResult> BandDistributionV2(
            [FromQuery] string? operatorName,
            [FromQuery] string? networkType,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = GetTargetCompanyId(company_id);
            int currentUserId = GetCurrentUserId();
            bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
            if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope)
            {
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
            }

            var hasDateFilter = from.HasValue || to.HasValue;
            var effectiveTo = to ?? DateTime.UtcNow;
            var effectiveFrom = from ?? effectiveTo.AddDays(-21);
            if (hasDateFilter && effectiveFrom > effectiveTo)
            {
                var tmp = effectiveFrom;
                effectiveFrom = effectiveTo;
                effectiveTo = tmp;
            }

            // =========================================================
            // 3. CACHE KEY (Includes Company ID & Filters)
            // =========================================================
            string opKey = operatorName ?? "ALL";
            string netKey = networkType ?? "ALL";
            string fromKey = hasDateFilter ? effectiveFrom.ToString("yyyyMMdd") : "ALL";
            string toKey = hasDateFilter ? effectiveTo.ToString("yyyyMMdd") : "ALL";

            string cacheKey = $"BandDist:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{opKey}:{netKey}:{fromKey}:{toKey}";

            // =========================================================
            // 4. TRY REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 5. EXECUTE SECURE SQL (ADO.NET)
            // =========================================================
            var result = new List<object>();
            var conn = db.Database.GetDbConnection();
            bool shouldClose = false;

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandTimeout = 300;

                    string sqlCore = @"
            SELECT
                t.operatorName,
                t.network,
                t.band,
                COUNT(*) AS count
            FROM
            (
                SELECT
                    n.m_alpha_long AS operatorName,
                    n.network,
                    COALESCE(
                        NULLIF(n.band, '-1'),
                        CASE
                            WHEN n.earfcn BETWEEN 0 AND 599 THEN '1'
                            WHEN n.earfcn BETWEEN 1200 AND 1949 THEN '3'
                            WHEN n.earfcn BETWEEN 2400 AND 2649 THEN '5'
                            WHEN n.earfcn BETWEEN 2750 AND 3449 THEN '7'
                            WHEN n.earfcn BETWEEN 3450 AND 3799 THEN '8'
                            WHEN n.earfcn BETWEEN 37750 AND 38249 THEN '38'
                            WHEN n.earfcn BETWEEN 38650 AND 39649 THEN '40'
                            WHEN n.earfcn BETWEEN 39650 AND 41589 THEN '41'
                            WHEN n.earfcn BETWEEN 620000 AND 653333 THEN 'n78'
                            WHEN n.earfcn BETWEEN 151600 AND 160600 THEN 'n28'
                            WHEN n.earfcn BETWEEN 422000 AND 434000 THEN 'n40'
                            ELSE NULL
                        END
                    ) AS band
                FROM tbl_network_log n
                {0}
                WHERE {1}
                  AND (@applyDate = 0 OR (n.timestamp BETWEEN @from AND @to))
                  AND (@operatorName IS NULL OR n.m_alpha_long = @operatorName)
                  AND (@networkType IS NULL OR n.network = @networkType)
            ) t
            WHERE t.band IS NOT NULL
            GROUP BY t.operatorName, t.network, t.band
            ORDER BY count DESC;
            ";

                    if (targetCompanyId == 0 && !useUserScope)
                    {
                        cmd.CommandText = string.Format(
                            sqlCore,
                            "",
                            "1=1");
                    }
                    else
                    {
                        cmd.CommandText = string.Format(
                            sqlCore,
                            "JOIN tbl_session s ON n.session_id = s.id JOIN tbl_user u ON s.user_id = u.id",
                            useUserScope ? "s.user_id = @userId" : "u.company_id = @companyId");
                    }

                    // Parameters
                    if (targetCompanyId > 0)
                    {
                        var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);
                    }
                    if (useUserScope)
                    {
                        var pUser = cmd.CreateParameter(); pUser.ParameterName = "@userId"; pUser.Value = currentUserId; cmd.Parameters.Add(pUser);
                    }
                    var pApplyDate = cmd.CreateParameter(); pApplyDate.ParameterName = "@applyDate"; pApplyDate.Value = hasDateFilter ? 1 : 0; cmd.Parameters.Add(pApplyDate);
                    var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@from"; pFrom.Value = effectiveFrom; cmd.Parameters.Add(pFrom);
                    var pTo = cmd.CreateParameter(); pTo.ParameterName = "@to"; pTo.Value = effectiveTo; cmd.Parameters.Add(pTo);
                    var pOp = cmd.CreateParameter(); pOp.ParameterName = "@operatorName"; pOp.Value = (object?)operatorName ?? DBNull.Value; cmd.Parameters.Add(pOp);
                    var pNet = cmd.CreateParameter(); pNet.ParameterName = "@networkType"; pNet.Value = (object?)networkType ?? DBNull.Value; cmd.Parameters.Add(pNet);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new
                            {
                                operatorName = reader.IsDBNull(0) ? null : reader.GetString(0),
                                network = reader.IsDBNull(1) ? null : reader.GetString(1),
                                band = reader.IsDBNull(2) ? null : reader.GetString(2),
                                count = reader.GetInt32(3)
                            });
                        }
                    }
                }
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =========================================================
            // 6. SAVE TO REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
            }

            return Ok(new { Status = 1, Source = "DATABASE", Data = result });
        }
        public class HandsetDistResult
        {
            public string name { get; set; }
            public int value { get; set; }
            public double? avg_rsrp { get; set; }
            public double? avg_rsrq { get; set; }
            public double? avg_sinr { get; set; }
        }
        [HttpGet("HandsetDistributionV2")]
        public async Task<IActionResult> HandsetDistributionV2(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = GetTargetCompanyId(company_id);
            int currentUserId = GetCurrentUserId();
            bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}

            var hasDateFilter = from.HasValue || to.HasValue;
            var effectiveTo = to ?? DateTime.UtcNow;
            var effectiveFrom = from ?? effectiveTo.AddDays(-14);
            if (hasDateFilter && effectiveFrom > effectiveTo)
            {
                var tmp = effectiveFrom;
                effectiveFrom = effectiveTo;
                effectiveTo = tmp;
            }
            string fromKey = hasDateFilter ? effectiveFrom.ToString("yyyyMMdd") : "ALL";
            string toKey = hasDateFilter ? effectiveTo.ToString("yyyyMMdd") : "ALL";
            string cacheKey = $"HandsetDist:MakeOnly:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}:{fromKey}:{toKey}";

            // =========================================================
            // 3. TRY REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<HandsetDistResult>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 4. EXECUTE SECURE SQL
            // =========================================================
            try
            {
                db.Database.SetCommandTimeout(180);

                var dateClause = hasDateFilter
                    ? "AND n.timestamp >= @from AND n.timestamp < @to"
                    : string.Empty;

                var sqlTemplate = @"
        SELECT 
            COALESCE(
                NULLIF(TRIM(u.make), ''),
                'Unknown'
            ) AS name,
            COUNT(*) AS value,
            AVG(n.rsrp) AS avg_rsrp,
            AVG(n.rsrq) AS avg_rsrq,
            AVG(n.sinr) AS avg_sinr
        FROM tbl_network_log n
        JOIN tbl_session s ON n.session_id = s.id
        JOIN tbl_user u ON u.id = s.user_id
        WHERE {0}
          {1}
        GROUP BY COALESCE(
            NULLIF(TRIM(u.make), ''),
            'Unknown'
        )
        ORDER BY value DESC, name;
        ";

                var sql = string.Format(
                    sqlTemplate,
                    targetCompanyId == 0 && !useUserScope
                        ? "1=1"
                        : (useUserScope ? "s.user_id = @userId" : "u.company_id = @companyId"),
                    dateClause);

                var sqlParams = new List<MySqlParameter>();
                if (targetCompanyId > 0)
                {
                    sqlParams.Add(new MySqlParameter("@companyId", targetCompanyId));
                }
                if (useUserScope)
                {
                    sqlParams.Add(new MySqlParameter("@userId", currentUserId));
                }

                if (hasDateFilter)
                {
                    sqlParams.Add(new MySqlParameter("@from", effectiveFrom));
                    sqlParams.Add(new MySqlParameter("@to", effectiveTo.AddDays(1)));
                }

                var result = await db.Set<HandsetDistResult>()
                    .FromSqlRaw(sql, sqlParams.ToArray())
                    .AsNoTracking()
                    .ToListAsync();

                // =========================================================
                // 5. SAVE TO REDIS
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600); // 10 min cache
                }

                return Ok(new { Status = 1, Source = "DATABASE", Data = result });
            }
            catch (Exception ex)
            {
                // Log error if you have a logger, e.g.:
                // new Writelog(db).write_exception_log(...);
                return StatusCode(500, new { Status = 0, Message = "Error: " + ex.Message });
            }
        }
        public class IndoorOutdoorAvgDto
        {
            public string OperatorName { get; set; }
            public string LocationType { get; set; } // Indoor / Outdoor

            public double AvgRsrp { get; set; }
            public double AvgRsrq { get; set; }
            public double AvgSinr { get; set; }
            public double AvgMos { get; set; }

            public double AvgDlTpt { get; set; }
            public double AvgUlTpt { get; set; }

            public int SampleCount { get; set; }
        }
        [HttpGet("operator-indoor-outdoor-avg")]
        public async Task<IActionResult> GetOperatorIndoorOutdoorAvgAll(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int? company_id = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
           int targetCompanyId = GetTargetCompanyId(company_id);
if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User)) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}

            var effectiveTo = to ?? DateTime.UtcNow;
            var effectiveFrom = from ?? effectiveTo.AddDays(-14);
            if (effectiveFrom > effectiveTo)
            {
                var tmp = effectiveFrom;
                effectiveFrom = effectiveTo;
                effectiveTo = tmp;
            }

            string cacheKey = $"op-indoor-outdoor-avg:{targetCompanyId}:{effectiveFrom:yyyyMMdd}:{effectiveTo:yyyyMMdd}:raw:v3";

            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new
                    {
                        Status = 1,
                        Count = cached.Count,
                        Source = "REDIS",
                        Data = cached
                    });
                }
            }

            // =====================================================
            // 3. SECURE SQL (FILTER BY COMPANY)
            // =====================================================
            string sqlTemplate = @"
        SELECT
            l.m_alpha_long AS OperatorName,
            l.indoor_outdoor AS LocationType,

            ROUND(AVG(l.rsrp), 2) AS AvgRsrp,
            ROUND(AVG(l.rsrq), 2) AS AvgRsrq,

            -- SINR: negative -> 0
            ROUND(AVG(CASE WHEN l.sinr < 0 THEN 0 ELSE l.sinr END), 2) AS AvgSinr,

            ROUND(AVG(l.mos), 2) AS AvgMos,

            ROUND(AVG(CAST(l.dl_tpt AS DECIMAL(10,2))), 2) AS AvgDlTpt,
            ROUND(AVG(CAST(l.ul_tpt AS DECIMAL(10,2))), 2) AS AvgUlTpt,

            COUNT(*) AS SampleCount
        FROM {0}
        WHERE {1}
          AND l.timestamp >= @fromDate
          AND l.timestamp < @toDate
          AND l.indoor_outdoor IS NOT NULL
          AND l.m_alpha_long IS NOT NULL
        GROUP BY l.m_alpha_long, l.indoor_outdoor
        ORDER BY l.m_alpha_long, l.indoor_outdoor;
    ";

            var fromClause = targetCompanyId == 0
                ? "tbl_network_log l"
                : "tbl_user u STRAIGHT_JOIN tbl_session s ON s.user_id = u.id STRAIGHT_JOIN tbl_network_log l ON l.session_id = s.id";
            var whereClause = targetCompanyId == 0 ? "1=1" : "u.company_id = @companyId";
            var sql = string.Format(sqlTemplate, fromClause, whereClause);

            var conn = db.Database.GetDbConnection();
            bool shouldClose = false;
            var result = new List<object>();

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;

                    if (targetCompanyId > 0)
                    {
                        var p = cmd.CreateParameter();
                        p.ParameterName = "@companyId";
                        p.Value = targetCompanyId;
                        cmd.Parameters.Add(p);
                    }

                    var pFrom = cmd.CreateParameter();
                    pFrom.ParameterName = "@fromDate";
                    pFrom.Value = effectiveFrom;
                    cmd.Parameters.Add(pFrom);

                    var pTo = cmd.CreateParameter();
                    pTo.ParameterName = "@toDate";
                    pTo.Value = effectiveTo.AddDays(1);
                    cmd.Parameters.Add(pTo);

                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        result.Add(new
                        {
                            OperatorName = reader.GetString(0),
                            LocationType = reader.GetString(1),

                            AvgRsrp = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                            AvgRsrq = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
                            AvgSinr = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4),
                            AvgMos = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5),

                            AvgDlTpt = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6),
                            AvgUlTpt = reader.IsDBNull(7) ? (double?)null : reader.GetDouble(7),

                            SampleCount = reader.GetInt64(8)
                        });
                    }
                }
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =====================================================
            // 4. SAVE TO REDIS
            // =====================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
            }

            sw.Stop();

            return Ok(new
            {
                Status = 1,
                Count = result.Count,
                Source = "DATABASE",
                Data = result
            });
        }
        [HttpGet("box-plot/operators")]
        public async Task<IActionResult> GetOperatorBoxPlotDynamic(
            [FromQuery] string metric,
            [FromQuery] string? op,   // OPTIONAL (a / j / v OR empty)
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = GetTargetCompanyId(company_id);
if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User)) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}

            string column = metric?.ToLower() switch
            {
                "rsrp" => "l.rsrp",
                "rsrq" => "l.rsrq",
                "sinr" => "l.sinr",
                "mos" => "l.mos",
                "dl_tpt" => "CAST(l.dl_tpt AS DECIMAL(18,4))",
                "ul_tpt" => "CAST(l.ul_tpt AS DECIMAL(18,4))",
                _ => null
            };
            string bucketExpr = metric?.ToLower() switch
            {
                "rsrp" => "ROUND(l.rsrp, 1)",
                "rsrq" => "ROUND(l.rsrq, 1)",
                "sinr" => "ROUND(l.sinr, 1)",
                "mos" => "ROUND(l.mos, 2)",
                "dl_tpt" => "ROUND(CAST(l.dl_tpt AS DECIMAL(18,4)), 1)",
                "ul_tpt" => "ROUND(CAST(l.ul_tpt AS DECIMAL(18,4)), 1)",
                _ => null
            };

            if (column == null || bucketExpr == null) return BadRequest("Invalid metric");

            var effectiveTo = to ?? DateTime.UtcNow;
            var effectiveFrom = from ?? effectiveTo.AddDays(-14);
            if (effectiveFrom > effectiveTo)
            {
                var tmp = effectiveFrom;
                effectiveFrom = effectiveTo;
                effectiveTo = tmp;
            }

            // =====================================================
            // 3. OPERATOR FILTER (Updated with alias 'l.')
            // =====================================================
            string operatorWhere = op?.ToLower() switch
            {
                "a" => "l.m_alpha_long LIKE '%AIRTEL%'",
                "j" => "l.m_alpha_long LIKE '%JIO%'",
                "v" => "(l.m_alpha_long LIKE '%VI%' OR l.m_alpha_long LIKE '%VODAFONE%')",
                null or "" => "l.m_alpha_long IS NOT NULL",
                _ => null
            };

            if (operatorWhere == null) return BadRequest("Invalid operator");

            // =====================================================
            // 4. RANGE FILTER
            // =====================================================
            double? minRange = null;
            double? maxRange = null;

            switch (metric.ToLower())
            {
                case "rsrp": minRange = -140; maxRange = 0; break;
                case "rsrq": minRange = -26; maxRange = 0; break;
                case "sinr": minRange = -113; maxRange = 40; break;
            }

            string rangeWhere = "";
            if (minRange.HasValue && maxRange.HasValue)
            {
                rangeWhere = $" AND {column} BETWEEN {minRange.Value} AND {maxRange.Value} ";
            }

            // =====================================================
            // 5. CACHE KEY (Includes Company ID)
            // =====================================================
            string cacheKey = $"boxplot:v7:{targetCompanyId}:{metric}:{op ?? "ALL"}:{effectiveFrom:yyyyMMdd}:{effectiveTo:yyyyMMdd}";

            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new
                    {
                        Status = 1,
                        Source = "REDIS",
                        Metric = metric.ToUpper(),
                        Count = cached.Count,
                        Data = cached
                    });
                }
            }

            // =====================================================
            // 6. SECURE SQL QUERY
            // =====================================================
            // Bucketed distribution avoids sorting every raw sample per operator.
            var sqlTemplate = $@"
    WITH base AS (
        SELECT
            UPPER(TRIM(l.m_alpha_long)) AS operator,
            {bucketExpr} AS val
        FROM {{0}}
        WHERE {{1}}
          AND l.timestamp >= @fromDate
          AND l.timestamp < @toDate
          AND {column} IS NOT NULL
          AND {operatorWhere}
          {rangeWhere}
    ),
    freq AS (
        SELECT
            operator,
            val,
            COUNT(*) AS freq
        FROM base
        WHERE val IS NOT NULL
        GROUP BY operator, val
    ),
    dist AS (
        SELECT
            operator,
            val,
            freq,
            SUM(freq) OVER (
                PARTITION BY operator
                ORDER BY val
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ) AS cum_freq,
            SUM(freq) OVER (
                PARTITION BY operator
            ) AS total_freq
        FROM freq
    )
    SELECT
        operator,
        MIN(val) AS min_val,
        MIN(CASE WHEN cum_freq >= (total_freq * 0.25) THEN val END) AS q1,
        MIN(CASE WHEN cum_freq >= (total_freq * 0.50) THEN val END) AS median,
        MIN(CASE WHEN cum_freq >= (total_freq * 0.75) THEN val END) AS q3,
        MAX(val) AS max_val,
        MAX(total_freq) AS samples
    FROM dist
    GROUP BY operator
    HAVING MAX(total_freq) > 0
    ORDER BY operator;
    ";

            var fromClause = targetCompanyId == 0
                ? "tbl_network_log l"
                : "tbl_user u STRAIGHT_JOIN tbl_session s ON s.user_id = u.id STRAIGHT_JOIN tbl_network_log l ON l.session_id = s.id";
            var whereClause = targetCompanyId == 0 ? "1=1" : "u.company_id = @companyId";
            var sql = string.Format(sqlTemplate, fromClause, whereClause);

            // =====================================================
            // 7. EXECUTE SQL
            // =====================================================
            var conn = db.Database.GetDbConnection();
            var data = new List<object>();
            bool shouldClose = false;

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 240; // Heavy calculation

                    if (targetCompanyId > 0)
                    {
                        var p = cmd.CreateParameter();
                        p.ParameterName = "@companyId";
                        p.Value = targetCompanyId;
                        cmd.Parameters.Add(p);
                    }

                    var pFrom = cmd.CreateParameter();
                    pFrom.ParameterName = "@fromDate";
                    pFrom.Value = effectiveFrom;
                    cmd.Parameters.Add(pFrom);

                    var pTo = cmd.CreateParameter();
                    pTo.ParameterName = "@toDate";
                    pTo.Value = effectiveTo.AddDays(1);
                    cmd.Parameters.Add(pTo);

                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        data.Add(new
                        {
                            Operator = reader.GetString(0),
                            Min = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1),
                            Q1 = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                            Median = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
                            Q3 = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4),
                            Max = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5),
                            Samples = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6))
                        });
                    }
                }
            }
            finally
            {
                if (shouldClose && conn.State == System.Data.ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =====================================================
            // 8. SAVE TO REDIS
            // =====================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, data, ttlSeconds: 600);
            }

            return Ok(new
            {
                Status = 1,
                Source = "DATABASE",
                Metric = metric.ToUpper(),
                Count = data.Count,
                Data = data
            });
        }

        // Per-app KPIs (optimized: no full-table load in C#)
        // Per-app KPIs (optimized: no full-table load in C#)
        [HttpGet("AppQualityFlatV2")]
        public async Task<IActionResult> AppQualityFlatV2(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int? company_id = null) // <--- ADDED PARAMETER
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
            int targetCompanyId = GetTargetCompanyId(company_id);
if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User)) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}

            var effectiveTo = to ?? DateTime.UtcNow;
            var effectiveFrom = from ?? effectiveTo.AddDays(-30);
            string fromKey = effectiveFrom.ToString("yyyyMMdd");
            string toKey = effectiveTo.ToString("yyyyMMdd");
            string cacheKey = $"AppKPIs:{targetCompanyId}:{fromKey}:{toKey}";

            // =========================================================
            // 3. TRY REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<object>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 4. EXECUTE SECURE SQL
            // =========================================================
            var conn = db.Database.GetDbConnection();
            var result = new List<object>();
            bool shouldClose = false;

            try
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                // We use alias 'l' for log, 's' for session, 'u' for user
                // Added Date Filters and Company Filter in the inner query
                var sqlTemplate = @"
            SELECT
                appName,
                COUNT(*)                             AS sampleCount,
                AVG(rsrp)                            AS avgRsrp,
                AVG(rsrq)                            AS avgRsrq,
                AVG(sinr)                            AS avgSinr,
                AVG(mos)                             AS avgMos,
                AVG(jitter)                          AS avgJitter,
                AVG(latency)                         AS avgLatency,
                AVG(packet_loss)                     AS avgPacketLoss,
                AVG(NULLIF(dl_tpt,0))                AS avgDlTptMbps,
                AVG(NULLIF(ul_tpt,0))                AS avgUlTptMbps,
                MIN(timestamp)                       AS firstUsedAt,
                MAX(timestamp)                       AS lastUsedAt,
                TIMESTAMPDIFF(
                    SECOND,
                    MIN(timestamp),
                    MAX(timestamp)
                )                                    AS durationSeconds
            FROM
            (
                SELECT
                    l.timestamp,
                    l.rsrp, l.rsrq, l.sinr, l.mos,
                    l.jitter, l.latency, l.packet_loss,
                    l.dl_tpt, l.ul_tpt,
                    CASE
                        WHEN l.apps LIKE '%Whatsapp%' THEN 'Whatsapp'
                        WHEN l.apps LIKE '%Instagram%' THEN 'Instagram'
                        WHEN l.apps LIKE '%YT%' THEN 'YT'
                        WHEN l.apps LIKE '%Google Chrome%' THEN 'Google Chrome'
                        WHEN l.apps LIKE '%Google Search%' THEN 'Google Search'
                        WHEN l.apps LIKE '%FB%' THEN 'FB'
                        WHEN l.apps LIKE '%Gmail%' THEN 'Gmail'
                        WHEN l.apps LIKE '%Outlook%' THEN 'Outlook'
                        WHEN l.apps LIKE '%Spotify%' THEN 'Spotify'
                        WHEN l.apps LIKE '%Blinkit%' THEN 'Blinkit'
                        WHEN l.apps LIKE '%Jio Hotstar%' THEN 'Jio Hotstar'
                        WHEN l.apps LIKE '%Amazon Prime%' THEN 'Amazon Prime'
                        WHEN l.apps LIKE '%Netflix%' THEN 'Netflix'
                        
                        ELSE NULL
                    END AS appName
                FROM tbl_network_log l
                {0}
                WHERE {1}
                  AND l.apps IS NOT NULL
                  AND l.timestamp >= @fromDate
                  AND l.timestamp < @toDate
            ) t
            WHERE appName IS NOT NULL
            GROUP BY appName;
        ";

                var sql = targetCompanyId == 0
                    ? string.Format(sqlTemplate, "", "1=1")
                    : string.Format(sqlTemplate, "JOIN tbl_session s ON l.session_id = s.id JOIN tbl_user u ON s.user_id = u.id", "u.company_id = @companyId");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 600;

                // Add Parameters
                if (targetCompanyId > 0)
                {
                    var pComp = cmd.CreateParameter(); pComp.ParameterName = "@companyId"; pComp.Value = targetCompanyId; cmd.Parameters.Add(pComp);
                }

                var pFrom = cmd.CreateParameter(); pFrom.ParameterName = "@fromDate"; pFrom.Value = effectiveFrom; cmd.Parameters.Add(pFrom);

                var pTo = cmd.CreateParameter(); pTo.ParameterName = "@toDate"; pTo.Value = effectiveTo.AddDays(1); cmd.Parameters.Add(pTo);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var durationSeconds = reader.IsDBNull(13) ? 0 : Convert.ToInt32(reader.GetValue(13));
                    var ts = TimeSpan.FromSeconds(durationSeconds);

                    result.Add(new
                    {
                        appName = reader.IsDBNull(0) ? null : reader.GetString(0),
                        // usageDate kept null as per original code
                        usageDate = (DateTime?)null,

                        sampleCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),

                        avgRsrp = reader.IsDBNull(2) ? 0d : Math.Round(Clamp(reader.GetDouble(2), -140, -44), 2),
            avgRsrq = reader.IsDBNull(3) ? 0d : Math.Round(Clamp(reader.GetDouble(3), -34, -3), 2),
        avgSinr = reader.IsDBNull(4) ? 0d : Math.Round(Clamp(reader.GetDouble(4), -20, 40), 2),
        avgMos = reader.IsDBNull(5) ? 0d : Math.Round(Clamp(reader.GetDouble(5), 1, 5), 2),
                        avgJitter = reader.IsDBNull(6) ? 0d : Math.Round(reader.GetDouble(6), 2),
                        avgLatency = reader.IsDBNull(7) ? 0d : Math.Round(reader.GetDouble(7), 2),
                        avgPacketLoss = reader.IsDBNull(8) ? 0d : Math.Round(reader.GetDouble(8), 2),

                        avgDlTptMbps = reader.IsDBNull(9) ? 0d : Math.Round(reader.GetDouble(9), 2),
                        avgUlTptMbps = reader.IsDBNull(10) ? 0d : Math.Round(reader.GetDouble(10), 2),

                        firstUsedAt = reader.IsDBNull(11) ? (DateTime?)null : reader.GetDateTime(11),
                        lastUsedAt = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),

                        durationSeconds,
                        durationMinutes = (int)ts.TotalMinutes,
                        durationOnlySecs = ts.Seconds,
                        durationHHMMSS = ts.ToString(@"hh\:mm\:ss")
                    });
                }
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }

            // =========================================================
            // 5. SAVE TO REDIS
            // =========================================================
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.SetObjectAsync(cacheKey, result, ttlSeconds: 600);
            }

            return Ok(new { Status = 1, Source = "DATABASE", Data = result });
        }

        private double Clamp(double value, int min, int max)
{
    if (value < min) return min;
    if (value > max) return max;
    return value;
}
        //  RESPONSE DTOs FOR CACHING
        // ========================================
        public class AppQualityResponse
        {
            public List<AppQualityItem> data { get; set; } = new();
        }

        public class AppQualityItem
        {
            public string? appName { get; set; }
            public DateTime? usageDate { get; set; }
            public int sampleCount { get; set; }
            public double avgRsrp { get; set; }
            public double avgRsrq { get; set; }
            public double avgSinr { get; set; }
            public double avgMos { get; set; }
            public double avgJitter { get; set; }
            public double avgLatency { get; set; }
            public double avgPacketLoss { get; set; }
            public double avgDlTptMbps { get; set; }
            public double avgUlTptMbps { get; set; }
            public DateTime? firstUsedAt { get; set; }
            public DateTime? lastUsedAt { get; set; }
            public int durationSeconds { get; set; }
            public int durationMinutes { get; set; }
            public int durationOnlySecs { get; set; }
            public string durationHHMMSS { get; set; } = "00:00:00";
        }    // Discovery APIs (retained for V2 endpoint usage)

        [HttpGet("OperatorsV2")]
        public async Task<IActionResult> OperatorsV2([FromQuery] int? company_id = null)
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
           int targetCompanyId = GetTargetCompanyId(company_id);
           int currentUserId = GetCurrentUserId();
           bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);

     if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}

    // 2. CACHE KEY
    string cacheKey = $"OperatorsList:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}";

            
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<string>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 4. EXECUTE SECURE QUERY
            // =========================================================
            try
            {
                // Keep this discovery API fast by limiting to recent activity.
                var cutoff = DateTime.UtcNow.AddDays(-90);
                List<string> operators;

                if (targetCompanyId == 0 && !useUserScope)
                {
                    operators = await db.tbl_network_log.AsNoTracking()
                        .Where(l => !string.IsNullOrEmpty(l.m_alpha_long)
                                 && l.timestamp.HasValue
                                 && l.timestamp.Value >= cutoff)
                        .Select(l => l.m_alpha_long)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToListAsync();
                }
                else
                {
                    operators = await (
                        from s in db.tbl_session.AsNoTracking()
                        join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                        join l in db.tbl_network_log.AsNoTracking() on s.id equals l.session_id
                        where (useUserScope ? s.user_id == currentUserId : u.company_id == targetCompanyId)
                           && !string.IsNullOrEmpty(l.m_alpha_long)
                           && l.timestamp.HasValue
                           && l.timestamp.Value >= cutoff
                        select l.m_alpha_long
                    )
                    .Distinct()
                    .OrderBy(x => x)
                    .ToListAsync();
                }

                // =========================================================
                // 5. SAVE TO REDIS
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    await _redis.SetObjectAsync(cacheKey, operators, ttlSeconds: 600); // 10 min cache
                }

                return Ok(new { Status = 1, Source = "DATABASE", Data = operators });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + ex.Message });
            }
        }

        [HttpGet("NetworksV2")]
        public async Task<IActionResult> NetworksV2([FromQuery] int? company_id = null)
        {
            // =========================================================
            // 1. SMART SECURITY: RESOLVE COMPANY ID
            // =========================================================
           int targetCompanyId = GetTargetCompanyId(company_id);
           int currentUserId = GetCurrentUserId();
           bool useUserScope = UseCurrentUserScope(targetCompanyId, currentUserId);
    
   if (targetCompanyId == 0 && !_userScope.IsSuperAdmin(User) && !useUserScope) 
{
    return Unauthorized(new { Status = 0, Message = "Unauthorized. Invalid Company." });
}
    // 2. CACHE KEY
    string cacheKey = $"NetworksList:{targetCompanyId}:user:{(useUserScope ? currentUserId : 0)}";

           
            if (_redis != null && _redis.IsConnected)
            {
                var cached = await _redis.GetObjectAsync<List<string>>(cacheKey);
                if (cached != null)
                {
                    return Ok(new { Status = 1, Source = "REDIS", Data = cached });
                }
            }

            // =========================================================
            // 4. EXECUTE SECURE QUERY
            // =========================================================
            try
            {
                // Keep this discovery API fast by limiting to recent activity.
                var cutoff = DateTime.UtcNow.AddDays(-90);
                List<string> networks;

                if (targetCompanyId == 0 && !useUserScope)
                {
                    networks = await db.tbl_network_log.AsNoTracking()
                        .Where(l => !string.IsNullOrEmpty(l.network)
                                 && l.timestamp.HasValue
                                 && l.timestamp.Value >= cutoff)
                        .Select(l => l.network)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToListAsync();
                }
                else
                {
                    networks = await (
                        from s in db.tbl_session.AsNoTracking()
                        join u in db.tbl_user.AsNoTracking() on s.user_id equals u.id
                        join l in db.tbl_network_log.AsNoTracking() on s.id equals l.session_id
                        where (useUserScope ? s.user_id == currentUserId : u.company_id == targetCompanyId)
                           && !string.IsNullOrEmpty(l.network)
                           && l.timestamp.HasValue
                           && l.timestamp.Value >= cutoff
                        select l.network
                    )
                    .Distinct()
                    .OrderBy(x => x)
                    .ToListAsync();
                }

                // =========================================================
                // 5. SAVE TO REDIS
                // =========================================================
                if (_redis != null && _redis.IsConnected)
                {
                    await _redis.SetObjectAsync(cacheKey, networks, ttlSeconds: 600); // 10 min cache
                }

                return Ok(new { Status = 1, Source = "DATABASE", Data = networks });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + ex.Message });
            }
        }
    }
}

