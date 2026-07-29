using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SignalTracker.Models.ZipImport;
using SignalTracker.Services.ZipImport;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public sealed class ZipImportController : ControllerBase
    {
        private readonly ZipImportService _zipImportService;

        public ZipImportController(ZipImportService zipImportService)
        {
            _zipImportService = zipImportService;
        }

        [HttpPost("Upload")]
        [EnableRateLimiting("Upload")]
        [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024)]
        public async Task<IActionResult> Upload([FromForm] ZipImportRequest request, CancellationToken cancellationToken)
        {
            if (request.ZipFile == null || request.ZipFile.Length == 0)
                return BadRequest(new { success = false, message = "ZIP file is required." });

            if (!Path.GetExtension(request.ZipFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { success = false, message = "Only .zip files are supported." });

            var userId = GetCurrentUserId();
            if (userId <= 0)
                return Unauthorized(new { success = false, message = "Unable to resolve logged-in user." });

            var result = await _zipImportService.ImportAsync(
                request.ZipFile,
                userId,
                request.SessionId,
                request.Notes,
                cancellationToken);

            return Ok(result);
        }

        private int GetCurrentUserId()
        {
            return TryParseInt(User?.FindFirst("UserId")?.Value)
                ?? TryParseInt(User?.FindFirst("user_id")?.Value)
                ?? HttpContext.Session.GetInt32("UserID")
                ?? 0;
        }

        private static int? TryParseInt(string? value)
        {
            return int.TryParse(value, out var parsed) ? parsed : null;
        }
    }
}
