using Microsoft.AspNetCore.Mvc;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Route("api/data-deletion")]
    public sealed class DataDeletionController : ControllerBase
    {
        private readonly IOtpService _otpService;
        private readonly IUserDeletionService _userDeletionService;

        public DataDeletionController(IOtpService otpService, IUserDeletionService userDeletionService)
        {
            _otpService = otpService;
            _userDeletionService = userDeletionService;
        }

        [HttpPost("send-otp")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Otp")]
        public async Task<IActionResult> SendOtp([FromBody] SendDataDeletionOtpRequest model, CancellationToken ct)
        {
            var result = await _otpService.SendDeletionOtpAsync(model.phone_number, HttpContext, ct);
            if (!result.Success)
            {
                if (string.Equals(result.Message, "User not found", StringComparison.OrdinalIgnoreCase))
                    return NotFound(new { success = false, message = result.Message });

                return BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new { success = true, message = result.Message });
        }

        [HttpPost("verify-otp")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("Otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyDataDeletionOtpRequest model, CancellationToken ct)
        {
            var result = await _otpService.VerifyDeletionOtpAsync(model.phone_number, model.otp, HttpContext, ct);
            if (!result.Success)
                return BadRequest(new { success = false, message = result.Message });

            return Ok(new
            {
                success = true,
                message = result.Message,
                deletion_token = result.DeletionToken
            });
        }

        [HttpGet("data-preview")]
        public async Task<IActionResult> GetDataPreview(CancellationToken ct)
        {
            var token = ExtractDeletionToken();
            var result = await _userDeletionService.GetPreviewAsync(token, HttpContext, ct);

            if (!result.Success)
                return BadRequest(new { success = false, message = result.Message });

            return Ok(new
            {
                success = true,
                message = result.Message,
                preview = result.Preview
            });
        }

        [HttpPost("request-deletion")]
        public async Task<IActionResult> RequestDeletion([FromBody] RequestDataDeletionRequest model, CancellationToken ct)
        {
            var token = !string.IsNullOrWhiteSpace(model.deletion_token)
                ? model.deletion_token
                : ExtractDeletionToken();

            var result = await _userDeletionService.RequestDeletionAsync(
                token,
                model.confirm_permanent_deletion,
                HttpContext,
                ct);

            if (!result.Success)
                return BadRequest(new { success = false, message = result.Message });

            return Ok(new { success = true, message = result.Message });
        }

        private string ExtractDeletionToken()
        {
            var auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return auth["Bearer ".Length..].Trim();

            if (Request.Headers.TryGetValue("X-Deletion-Token", out var headerToken))
                return headerToken.ToString();

            return string.Empty;
        }
    }

    public sealed class SendDataDeletionOtpRequest
    {
        public string phone_number { get; set; } = string.Empty;
    }

    public sealed class VerifyDataDeletionOtpRequest
    {
        public string phone_number { get; set; } = string.Empty;
        public string otp { get; set; } = string.Empty;
    }

    public sealed class RequestDataDeletionRequest
    {
        public string? deletion_token { get; set; }
        public bool confirm_permanent_deletion { get; set; }
    }
}


