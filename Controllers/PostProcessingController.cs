using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.DTO.PostProcessing;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public sealed class PostProcessingController : ControllerBase
    {
        private readonly PostProcessingCapabilityService _capabilityService;
        private readonly VoiceKpiService _voiceKpiService;

        public PostProcessingController(PostProcessingCapabilityService capabilityService, VoiceKpiService voiceKpiService)
        {
            _capabilityService = capabilityService;
            _voiceKpiService = voiceKpiService;
        }

        [HttpGet("capabilities")]
        public async Task<IActionResult> GetCapabilities(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 100000)
        {
            var ids = ParseSessionIds(sessionId, sessionIds, sessionIdsAlt);
            if (ids.Count == 0 && !uploadId.HasValue)
                return BadRequest(new { Status = 0, Message = "sessionId, sessionIds, or uploadId is required." });

            var result = await _capabilityService.GetCapabilitiesAsync(ids, uploadId, take, HttpContext.RequestAborted);
            return Ok(new
            {
                Status = 1,
                Message = "OK",
                Data = result
            });
        }

        [HttpGet("voice-kpis")]
        public async Task<IActionResult> GetVoiceKpis(
            [FromQuery] int? sessionId = null,
            [FromQuery] string? sessionIds = null,
            [FromQuery(Name = "session_ids")] string? sessionIdsAlt = null,
            [FromQuery] int? uploadId = null,
            [FromQuery] int take = 100000)
        {
            var ids = ParseSessionIds(sessionId, sessionIds, sessionIdsAlt);
            if (ids.Count == 0 && !uploadId.HasValue)
                return BadRequest(new { Status = 0, Message = "sessionId, sessionIds, or uploadId is required." });

            var result = await _voiceKpiService.GetVoiceKpisAsync(new PostProcessingRequest
            {
                SessionId = sessionId,
                SessionIds = ids,
                UploadId = uploadId,
                Take = take
            }, HttpContext.RequestAborted);

            return Ok(result);
        }

        [HttpPost("voice-kpis")]
        public async Task<IActionResult> PostVoiceKpis([FromBody] PostProcessingRequest request)
        {
            request ??= new PostProcessingRequest();
            var ids = (request.SessionIds ?? new List<int>())
                .Concat(request.SessionId.HasValue ? new[] { request.SessionId.Value } : Array.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (ids.Count == 0 && !request.UploadId.HasValue)
                return BadRequest(new { Status = 0, Message = "SessionId, SessionIds, or UploadId is required." });

            request.SessionIds = ids;
            request.SessionId = null;
            var result = await _voiceKpiService.GetVoiceKpisAsync(request, HttpContext.RequestAborted);
            return Ok(result);
        }

        private static List<int> ParseSessionIds(int? sessionId, string? sessionIds, string? sessionIdsAlt)
        {
            var ids = new List<int>();
            if (sessionId.HasValue && sessionId.Value > 0)
                ids.Add(sessionId.Value);

            foreach (var raw in new[] { sessionIds, sessionIdsAlt })
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                ids.AddRange(raw.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(token => int.TryParse(token.Trim(), out var id) ? id : 0)
                    .Where(id => id > 0));
            }

            return ids.Distinct().Take(250).ToList();
        }
    }
}
