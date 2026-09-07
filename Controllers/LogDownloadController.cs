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

        private async Task<IActionResult> ProxyDownloadAsync(string url, string? fileName, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return BadRequest("A valid absolute URL is required.");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Only HTTPS URLs are allowed.");
            }

            if (!string.Equals(uri.Host, "apistracer.vinfocom.co.in", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/uploaded_zippedlogs/", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("This endpoint only proxies the uploaded zip logs host and path.");
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

