using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.DTO.SitePrediction;
using SignalTracker.Helper;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SitePredictionController : ControllerBase
    {
        private readonly SitePredictionService _sitePredictionService;

        public SitePredictionController(SitePredictionService sitePredictionService)
        {
            _sitePredictionService = sitePredictionService;
        }

        [HttpGet("GetScenarios")]
        public async Task<IActionResult> GetScenarios([FromQuery] long projectId)
        {
            if (projectId <= 0)
                return BadRequest(new { Status = 0, Message = "projectId is required." });

            try
            {
                var rows = await _sitePredictionService.GetScenariosAsync(projectId);
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

        [HttpPost("DeleteScenario")]
        public async Task<IActionResult> DeleteScenario([FromBody] DeleteSitePredictionScenarioRequest? request)
        {
            if (request == null)
                return BadRequest(new { Status = 0, Message = "Invalid payload." });
            if (request.ProjectId <= 0)
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            if (request.Scenario <= 0)
                return BadRequest(new { Status = 0, Message = "scenario must be greater than 0" });

            try
            {
                var result = await _sitePredictionService.DeleteScenarioAsync(request);
                return Ok(result);
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

        [HttpPost("Delete")]
        public async Task<IActionResult> Delete([FromBody] DeleteSitePredictionRequest? request)
        {
            if (request == null)
                return BadRequest(new { Status = 0, Message = "Invalid payload." });
            if (request.ProjectId <= 0)
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });

            var hasSourceId = request.SourceId.HasValue && request.SourceId.Value > 0;
            var hasCellId = !string.IsNullOrWhiteSpace(request.CellId);
            var hasSite = !string.IsNullOrWhiteSpace(request.Site);
            if (!hasSourceId && !hasCellId && !hasSite)
                return BadRequest(new { Status = 0, Message = "Either SourceId, CellId, or Site is required." });
            if (!hasSourceId && !hasCellId && !request.DeleteEntireSite && string.IsNullOrWhiteSpace(request.Sector))
                return BadRequest(new { Status = 0, Message = "CellId or Sector is required when deleting a single sector." });

            try
            {
                var result = await _sitePredictionService.DeleteAsync(request);
                return Ok(result);
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
    }
}
