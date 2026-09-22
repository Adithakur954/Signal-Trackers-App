using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;
using SignalTracker.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Services;
using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SignalTrackers.Controllers
{
    [Authorize]
    [EnableRateLimiting("Report")]
    [ApiController]
    [Route("api/[controller]")]
    public class LogDownloadController : ControllerBase
    {
        private const long MaxDownloadBytes = 500L * 1024 * 1024;
        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _configuration;

        public LogDownloadController(ApplicationDbContext db, IConfiguration configuration)
        {
            _db = db;
            _configuration = configuration;
        }

        private async Task<bool> CanAccessAnyAsync(CancellationToken cancellationToken, params Uri[] uris)
        {
            if (ResourceAccess.IsSuperAdmin(User)) return true;
            var allowedUrls = uris
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var candidates = await ResourceAccess.Projects(_db.tbl_project, User)
                .Where(project => allowedUrls.Contains(project.Download_path))
                .Select(project => project.Download_path).ToListAsync(cancellationToken);
            // Preserve exact URL matching even if the database uses a case-insensitive collation.
            return candidates.Any(value => allowedUrls.Any(allowedUrl => string.Equals(value, allowedUrl, StringComparison.Ordinal)));
        }

        private static readonly HttpClient HttpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        });



        [HttpGet("download")]
        public Task<IActionResult> DownloadFromUrl(
            [FromQuery] string? url,
            [FromQuery] string? fileName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Task.FromResult<IActionResult>(BadRequest("The log URL is required."));
            }

            return ProxyDownloadAsync(url, fileName, cancellationToken);
        }

        [HttpHead("download")]
        public async Task<IActionResult> CheckDownload(
            [FromQuery] string? url,
            CancellationToken cancellationToken)
        {
            if (!TryGetAllowedUri(url, out var requestedUri, out var validationError))
            {
                return BadRequest(validationError);
            }

            if (!TryResolveRegionLogUri(requestedUri, out var uri, out validationError))
            {
                return BadRequest(validationError);
            }

            if (!await CanAccessAnyAsync(cancellationToken, requestedUri, uri)) return NotFound("Log not found.");
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var upstream = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return upstream.IsSuccessStatusCode
                ? Ok()
                : StatusCode((int)upstream.StatusCode);
        }

        private async Task<IActionResult> ProxyDownloadAsync(string url, string? fileName, CancellationToken cancellationToken)
        {
            if (!TryGetAllowedUri(url, out var requestedUri, out var validationError))
            {
                return BadRequest(validationError);
            }

            if (!TryResolveRegionLogUri(requestedUri, out var uri, out validationError))
            {
                return BadRequest(validationError);
            }

            if (!await CanAccessAnyAsync(cancellationToken, requestedUri, uri)) return NotFound("Log not found.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            cancellationToken = timeout.Token;
            using var upstream = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!upstream.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, "Upstream download failed.");
            }

            if (upstream.Content.Headers.ContentLength > MaxDownloadBytes)
                return StatusCode(StatusCodes.Status502BadGateway, "Upstream file exceeds the download limit.");

            var responseFileName = NormalizeFileName(!string.Equals(requestedUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal)
                ? Path.GetFileName(uri.LocalPath)
                : string.IsNullOrWhiteSpace(fileName)
                ? Path.GetFileName(uri.LocalPath)
                : fileName);

            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = contentType;
            Response.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(responseFileName)}";

            await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    HttpContext.Abort();
                    return new EmptyResult();
                }
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return new EmptyResult();
        }

        private bool TryResolveRegionLogUri(Uri requestedUri, out Uri resolvedUri, out string error)
        {
            resolvedUri = requestedUri;
            error = string.Empty;

            var logId = TryGetRemoteLogId(requestedUri);
            if (!logId.HasValue)
            {
                return true;
            }

            var regionUrl = RemoteLogZipUrlResolver.BuildUrl(_configuration, HttpContext, logId.Value);
            if (string.IsNullOrWhiteSpace(regionUrl))
            {
                return true;
            }

            if (!TryGetAllowedUri(regionUrl, out resolvedUri, out var validationError))
            {
                error = $"Configured remote log ZIP URL is invalid. {validationError}";
                return false;
            }

            return true;
        }

        private static int? TryGetRemoteLogId(Uri uri)
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            var match = Regex.Match(fileName, @"^log_(\d+)(?:_tw)?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && int.TryParse(match.Groups[1].Value, out var logId) && logId > 0
                ? logId
                : null;
        }

        private static bool TryGetAllowedUri(string? url, out Uri uri, out string error)
        {
            uri = null!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
            {
                error = "A valid absolute URL is required.";
                return false;
            }

            if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "Only HTTPS URLs are allowed.";
                return false;
            }

            if (parsedUri.Port != 443 || !string.IsNullOrEmpty(parsedUri.UserInfo)
                || !string.Equals(parsedUri.Host, "apistracer.vinfocom.co.in", StringComparison.OrdinalIgnoreCase) ||
                !parsedUri.AbsolutePath.StartsWith("/uploaded_zippedlogs/", StringComparison.OrdinalIgnoreCase))
            {
                error = "This endpoint only proxies the uploaded zip logs host and path.";
                return false;
            }

            uri = parsedUri;
            error = string.Empty;
            return true;
        }

        private static string NormalizeFileName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "download.zip";
            }

            var cleaned = Path.GetFileName(value.Trim()).Replace("\"", string.Empty);
            return string.IsNullOrWhiteSpace(cleaned) ? "download.zip" : cleaned;
        }
    }
}
