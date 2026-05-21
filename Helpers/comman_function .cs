using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using SignalTracker.Models;
using System.Text;
//using System.Web.Security;
using Newtonsoft.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Rendering;
using SignalTracker.Helper;
using SignalTracker;
using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Net.Mime;

public class CommonFunction
{
    private readonly ApplicationDbContext db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public int UserId { get; private set; } = 0;
    public int UserType { get; private set; } = 0;
    public string UserName { get; private set; } = "";

    public CommonFunction(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        db = context;
        _httpContextAccessor = httpContextAccessor;

        var http = _httpContextAccessor?.HttpContext;
        var session = http?.Session;
        if (session == null) return;

        // UserID
        UserId = session.GetInt32("UserID") ?? 0;

        // UserType (prefer int; fallback to string with TryParse)
        var userTypeInt = session.GetInt32("UserType");
        if (userTypeInt.HasValue)
        {
            UserType = userTypeInt.Value;
        }
        else
        {
            var uTypeStr = session.GetString("UserType");
            if (!string.IsNullOrWhiteSpace(uTypeStr) && int.TryParse(uTypeStr, out var parsed))
            {
                UserType = parsed;
                // normalize to int in session for future reads
                session.SetInt32("UserType", parsed);
            }
            else
            {
                // remove bad value if present and default
                session.Remove("UserType");
                UserType = 0;
            }
        }

        // UserName (actually assign it)
        var uName = session.GetString("UserName");
        if (!string.IsNullOrWhiteSpace(uName))
        {
            UserName = uName!;
        }
        else
        {
            // fallback to identity name if available
            var identityName = http?.User?.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(identityName))
                UserName = identityName!;
        }
    }

// In SignalTracker.Helper.CommonFunction class

public int GetCompanyId()
{
    // 1. Try to get from Claims (if using JWT/Auth)
    var user = _httpContextAccessor.HttpContext?.User;
    var companyIdClaim = user?.FindFirst("CompanyId")?.Value;

    if (!string.IsNullOrEmpty(companyIdClaim) && int.TryParse(companyIdClaim, out int cid))
    {
        return cid;
    }

    // 2. Fallback: Database lookup using the current UserID
    // Note: This assumes you have access to the DB context in this class. 
    // If not, we will handle the lookup inside the Controller methods.
    
    // For now, let's assume we will fetch it in the controller if this helper doesn't exist.
    // If you already have the UserId, we can use that.
    return 0; // Default or Error
}
    public bool SessionCheck(string userType = "")
{
    var httpContext = _httpContextAccessor.HttpContext;

    // 1. Check if the user is authenticated via the Cookie Identity
    if (httpContext?.User.Identity?.IsAuthenticated == true)
    {
        // 2. Read values directly from Claims (Already in memory from the login)
        var userEmail = httpContext.User.Identity.Name;
        var userIdClaim = httpContext.User.FindFirst("UserId")?.Value;
        var userTypeClaim = httpContext.User.FindFirst("UserTypeId")?.Value;
        var countryCodeClaim = httpContext.User.FindFirst("country_code")?.Value;

        if (int.TryParse(userIdClaim, out int id) && int.TryParse(userTypeClaim, out int type))
        {
            // 3. Sync these values to the current Session and Local variables
            httpContext.Session.SetString("Email", userEmail ?? "");
            httpContext.Session.SetInt32("UserID", id);
            httpContext.Session.SetInt32("UserType", type);
            if (!string.IsNullOrWhiteSpace(countryCodeClaim))
                httpContext.Session.SetString("country_code", countryCodeClaim.Trim().ToUpperInvariant());

            UserId = id;
            UserType = type;
            UserName = userEmail ?? "";

            // 4. Validate roles if a userType was requested
            if (!string.IsNullOrEmpty(userType))
            {
                var allowed = userType.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim())
                                      .Any(s => s == UserType.ToString());
                return allowed;
            }
            return true;
        }
    }
    return false;
}
    public string CreateToken(string ip)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var email = httpContext?.Session.GetString("Email");
        if (!string.IsNullOrEmpty(email) && SessionCheck())
        {
            var uid = httpContext!.Session.GetInt32("UserID") ?? 0;
            var utype = httpContext.Session.GetInt32("UserType") ?? 0;

            var tokenData = $"{uid}#{utype}#{ip}#{DateTime.Now:MM-dd-yyyy HH:mm:ss}";
            var token = AESEncrytDecry.Encrypt(tokenData);
            httpContext.Session.SetString("token", token);

            var user = db.tbl_user.FirstOrDefault(u => u.email == email);
            if (user != null)
            {
                user.token = token;
                db.SaveChanges();
            }
            return token;
        }
        return string.Empty;
    }

    public ReturnAPIResponse MatchToken(string token)
    {
        var objRet = new ReturnAPIResponse { Status = 0, Message = "Invalid token, kindly login again." };
        var httpContext = _httpContextAccessor.HttpContext;

        if (SessionCheck())
        {
            var session = httpContext!.Session;
            var serverToken = session.GetString("token") ?? "";
            var userTypeInt = session.GetInt32("UserType");
            if (userTypeInt.HasValue)
                objRet.UserType = userTypeInt.Value;
            else
            {
                var userTypeStr = session.GetString("UserType");
                if (int.TryParse(userTypeStr, out var parsed))
                    objRet.UserType = parsed;
            }

            if (string.IsNullOrEmpty(serverToken))
            {
                var email = session.GetString("Email") ?? "";
                if (!string.IsNullOrEmpty(email))
                {
                    var user = db.tbl_user.FirstOrDefault(a => a.email == email);
                    if (user != null)
                    {
                        serverToken = user.token;
                        objRet.UserType = user.m_user_type_id;
                    }
                }
                else
                {
                    objRet.Status = 3;
                    objRet.Message = "Your session has been expired, kindly login again.";
                }
            }

            if (!string.IsNullOrEmpty(serverToken) && !string.IsNullOrEmpty(token))
            {
                var clientToken = AESEncrytDecry.Decrypt(token);
                var srvToken = AESEncrytDecry.Decrypt(serverToken);
                var ctn = clientToken.Split('#');
                var stn = srvToken.Split('#');
                if (ctn.Length == stn.Length)
                {
                    objRet.Status = 1;
                    for (int i = 0; i < ctn.Length; i++)
                    {
                        if (ctn[i] != stn[i])
                        {
                            objRet.Message = "Token missmatch or invalid user.";
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            httpContext?.Session.Clear();
        }
        return objRet;
    }
    public static string GetMimeType(string filePath)
    {
        var mimeTypes = new Dictionary<string, string>
    {
       { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".xls", "application/vnd.ms-excel" },
        { ".csv", "text/csv" },
        { ".pdf", "application/pdf" },
        { ".txt", "text/plain" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".bmp", "image/bmp" },
        { ".webp", "image/webp" },
        { ".svg", "image/svg+xml" },
        { ".doc", "application/msword" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".ppt", "application/vnd.ms-powerpoint" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { ".zip", "application/zip" }
    };

        var ext = Path.GetExtension(filePath).ToLower();
        return mimeTypes.ContainsKey(ext) ? mimeTypes[ext] : "application/octet-stream";
    }
    public static bool IsValidImageFormat(String path)
    {
        using (FileStream fs = File.OpenRead(path))
        {
            byte[] header = new byte[10];
            fs.Read(header, 0, 10);

            foreach (var pattern in new byte[][] {
                    Encoding.ASCII.GetBytes("BM"),
                    Encoding.ASCII.GetBytes("GIF"),
                    new byte[] { 137, 80, 78, 71 },     // PNG
                    new byte[] { 73, 73, 42 },          // TIFF
                    new byte[] { 77, 77, 42 },          // TIFF
                    new byte[] { 255, 216, 255, 224 },  // jpeg
                    new byte[] { 255, 216, 255, 225 }   // jpeg canon
            })
            {
                if (pattern.SequenceEqual(header.Take(pattern.Length)))
                    return true;
            }
        }

        return false;
    }

    public bool CompareFy(string FilledFy, string caomareFy)
    {
        bool isGreater = false;
        try
        {
            if (string.IsNullOrEmpty(FilledFy) || string.IsNullOrEmpty(caomareFy))
                return false;
            string[] a = FilledFy.Split('-');
            string[] b = caomareFy.Split('-');
            if (a.Length == 2 && b.Length == 2)
            {
                int aa = 0, bb = 0;
                int.TryParse(a[1], out aa);
                int.TryParse(b[1], out bb);
                if (bb >= aa)
                    isGreater = true;
            }
        }
        catch (Exception ex)
        {

        }
        return isGreater;
    }
    public string GetPrevFY_OftheSelectedFY(string fy)
    {
        string prevFY = "";
        if (fy == "2016-17")
            prevFY = "2015-16";
        else if (fy == "2017-18")
            prevFY = "2016-17";
        else if (fy == "2018-19")
            prevFY = "2017-18";
        else if (fy == "2019-20")
            prevFY = "2018-19";
        else if (fy == "2020-21")
            prevFY = "2019-20";
        else if (fy == "2021-22")
            prevFY = "2020-21";
        else if (fy == "2022-23")
            prevFY = "2021-22";

        return prevFY;
    }
    public static string Sha256Hash(string input)
{
    using (SHA256 sha256 = SHA256.Create())
    {
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        StringBuilder sb = new StringBuilder();
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
    }
    public List<string> getLastFyYear(string fy, int fyCount)
    {
        List<string> result = new List<string>();
        if (!string.IsNullOrEmpty(fy))
            result.Add(fy);
        for (int i = 0; i < fyCount; i++)
        {
            string pevfy = GetPrevFY_OftheSelectedFY(fy);
            if (string.IsNullOrEmpty(pevfy))
                break;
            result.Add(pevfy);
            fy = pevfy;
        }
        List<string> OrderList = new List<string>();
        for (int i = result.Count - 1; i >= 0; i--)
        {
            string ofy = result[i];
            OrderList.Add(ofy);
        }
        return OrderList;
    }
    public static bool ValidateCSVZipFile(string contentType)
    {
        if (contentType == "text/csv" || contentType == "application/csv" || contentType == "application/vnd.ms-excel" || contentType == "application/octet-stream" || contentType == "application/zip" || contentType == "application/x-zip-compressed")
            return true;
        else
            return false;
    }
    public static bool ValidateGeoJsonFile(string contentType)
    {
        return contentType == "application/geo+json" ||        // standard MIME type for GeoJSON
               contentType == "application/json" ||            // often used when content type is not specific
               contentType == "text/json" ||                   // legacy/alternate JSON MIME
               contentType == "application/octet-stream";      // fallback (e.g., from some browsers/tools)
    }
}
