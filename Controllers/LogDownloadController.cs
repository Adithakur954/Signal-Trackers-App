using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SignalTrackers.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LogDownloadController : ControllerBase
    {
        private static readonly HttpClient HttpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
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
            if (!TryGetAllowedUri(url, out var uri, out var validationError))
            {
                return BadRequest(validationError);
            }

            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var upstream = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return upstream.IsSuccessStatusCode
                ? Ok()
                : StatusCode((int)upstream.StatusCode);
        }

        private async Task<IActionResult> ProxyDownloadAsync(string url, string? fileName, CancellationToken cancellationToken)
        {
            if (!TryGetAllowedUri(url, out var uri, out var validationError))
            {
                return BadRequest(validationError);
            }

            using var upstream = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!upstream.IsSuccessStatusCode)
            {
                var errorBody = await upstream.Content.ReadAsStringAsync(cancellationToken);
                return StatusCode((int)upstream.StatusCode, string.IsNullOrWhiteSpace(errorBody) ? "Upstream download failed." : errorBody);
            }

            var responseFileName = NormalizeFileName(string.IsNullOrWhiteSpace(fileName)
                ? Path.GetFileName(uri.LocalPath)
                : fileName);

            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = contentType;
            Response.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(responseFileName)}";

            await upstream.Content.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
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

            if (!string.Equals(parsedUri.Host, "apistracer.vinfocom.co.in", StringComparison.OrdinalIgnoreCase) ||
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
