using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.DTO.PythonBridge;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PythonBridgeController : ControllerBase
    {
        private readonly PythonBridgeService _pythonBridgeService;

        public PythonBridgeController(PythonBridgeService pythonBridgeService)
        {
            _pythonBridgeService = pythonBridgeService;
        }

        private bool IsAuthorized()
        {
            Request.Headers.TryGetValue("X-Python-Bridge-Key", out var incoming);
            return _pythonBridgeService.IsAuthorized(incoming.ToString());
        }

        private IActionResult? EnsureAuthorized()
        {
            if (IsAuthorized())
            {
                return null;
            }
            return Unauthorized(new { Status = 0, Message = "Invalid bridge key." });
        }

        [AllowAnonymous]
        [HttpPost("GetDriveTestRows")]
        public async Task<IActionResult> GetDriveTestRows([FromBody] DriveTestRowsRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.SessionIds == null || request.SessionIds.Count == 0)
            {
                return BadRequest(new { Status = 0, Message = "SessionIds are required." });
            }

            var sessionIds = request.SessionIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (sessionIds.Count == 0)
            {
                return BadRequest(new { Status = 0, Message = "No valid SessionIds provided." });
            }

            request.SessionIds = sessionIds;

            var result = await _pythonBridgeService.GetDriveTestRowsAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new
            {
                Status = 1,
                Count = result.Rows.Count,
                Limit = result.Limit,
                Offset = result.Offset,
                Data = result.Rows
            });
        }

        [AllowAnonymous]
        [HttpGet("GetLteTiltBaselineResults")]
        public async Task<IActionResult> GetLteTiltBaselineResults([FromQuery] LteTiltBaselineRowsRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            }

            var result = await _pythonBridgeService.GetLteTiltBaselineResultsAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new
            {
                Status = 1,
                Count = result.Rows.Count,
                Limit = result.Limit,
                Offset = result.Offset,
                Data = result.Rows
            });
        }

        [AllowAnonymous]
        [HttpGet("GetSitePredictionOptimized")]
        public async Task<IActionResult> GetSitePredictionOptimized(
            [FromQuery] long projectId,
            [FromQuery] string? operatorName,
            [FromQuery(Name = "operator")] string? operatorAlias,
            [FromQuery] int limit = 20000,
            [FromQuery] int offset = 0)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (projectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "projectId is required." });
            }

            var result = await _pythonBridgeService.GetSitePredictionOptimizedAsync(
                projectId,
                operatorName ?? operatorAlias,
                limit,
                offset,
                HttpContext.RequestAborted
            );

            return Ok(new
            {
                Status = 1,
                Count = result.Rows.Count,
                Limit = result.Limit,
                Offset = result.Offset,
                Data = result.Rows
            });
        }

        [AllowAnonymous]
        [HttpPost("SavePredictionData")]
        public async Task<IActionResult> SavePredictionData([FromBody] PredictionDataBulkRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            }

            if (request.ProjectId > int.MaxValue)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is out of supported range." });
            }

            var rows = request.Rows ?? new List<PredictionDataRow>();
            if (rows.Count == 0)
            {
                return Ok(new { Status = 1, Inserted = 0 });
            }

            request.Rows = rows;
            var inserted = await _pythonBridgeService.SavePredictionDataAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Inserted = inserted });
        }

        [AllowAnonymous]
        [HttpPost("SaveLtePredictionResults")]
        public async Task<IActionResult> SaveLtePredictionResults([FromBody] LtePredictionBulkRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            }

            if (request.Rows == null || request.Rows.Count == 0)
            {
                return Ok(new { Status = 1, Inserted = 0 });
            }

            var inserted = await _pythonBridgeService.SaveLtePredictionResultsAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Inserted = inserted });
        }

        [AllowAnonymous]
        [HttpPost("SaveLtePredictionRefined")]
        public async Task<IActionResult> SaveLtePredictionRefined([FromBody] LtePredictionRefinedBulkRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            }

            if (request.Rows == null || request.Rows.Count == 0)
            {
                return Ok(new { Status = 1, Inserted = 0 });
            }

            var inserted = await _pythonBridgeService.SaveLtePredictionRefinedAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Inserted = inserted });
        }

        [AllowAnonymous]
        [HttpPost("SaveLtePredictionOptimisedResults")]
        public async Task<IActionResult> SaveLtePredictionOptimisedResults([FromBody] LtePredictionOptimisedBulkRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            }

            if (request.Rows == null || request.Rows.Count == 0)
            {
                return Ok(new { Status = 1, Inserted = 0 });
            }

            var inserted = await _pythonBridgeService.SaveLtePredictionOptimisedResultsAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Inserted = inserted });
        }

        [AllowAnonymous]
        [HttpGet("GetNextRfOptimizationScenarioId")]
        public async Task<IActionResult> GetNextRfOptimizationScenarioId([FromQuery] long projectId)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (projectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "projectId is required." });
            }

            var scenarioId = await _pythonBridgeService.GetNextRfOptimizationScenarioIdAsync(
                projectId,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, ScenarioId = scenarioId });
        }

        [AllowAnonymous]
        [HttpPost("SaveRfOptimizationResults")]
        public async Task<IActionResult> SaveRfOptimizationResults([FromBody] RfOptimizationBulkRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "ProjectId is required." });
            }

            if (request.Rows == null || request.Rows.Count == 0)
            {
                return Ok(new { Status = 1, Inserted = 0 });
            }

            var inserted = await _pythonBridgeService.SaveRfOptimizationResultsAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Inserted = inserted });
        }

        [AllowAnonymous]
        [HttpGet("PredictionDebugSummary")]
        public async Task<IActionResult> PredictionDebugSummary([FromQuery] long projectId)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (projectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "projectId is required" });
            }

            var summary = await _pythonBridgeService.PredictionDebugSummaryAsync(
                projectId,
                HttpContext.RequestAborted
            );

            return Ok(new
            {
                Status = 1,
                project_exists = summary.ProjectExists,
                site_noMl_count = summary.SiteNoMlCount,
                source = "signal-trackers"
            });
        }

        [AllowAnonymous]
        [HttpGet("GetProject")]
        public async Task<IActionResult> GetProject([FromQuery] long projectId)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (projectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "projectId is required." });
            }

            var project = await _pythonBridgeService.GetProjectAsync(
                projectId,
                HttpContext.RequestAborted
            );

            if (project == null)
            {
                return NotFound(new { Status = 0, Message = "Project not found." });
            }

            return Ok(new { Status = 1, Data = project });
        }

        [AllowAnonymous]
        [HttpGet("GetProjectRegions")]
        public async Task<IActionResult> GetProjectRegions([FromQuery] long projectId)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (projectId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "projectId is required." });
            }

            var rows = await _pythonBridgeService.GetProjectRegionsAsync(
                projectId,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Count = rows.Count, Data = rows });
        }

        [AllowAnonymous]
        [HttpPost("GetReportNetworkLogs")]
        public async Task<IActionResult> GetReportNetworkLogs([FromBody] SessionIdsPagedRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.SessionIds == null || request.SessionIds.Count == 0)
            {
                return BadRequest(new { Status = 0, Message = "SessionIds are required." });
            }

            var result = await _pythonBridgeService.GetReportNetworkLogsAsync(
                request,
                HttpContext.RequestAborted
            );

            return Ok(new
            {
                Status = 1,
                Count = result.Rows.Count,
                Limit = result.Limit,
                Offset = result.Offset,
                Data = result.Rows
            });
        }

        [AllowAnonymous]
        [HttpPost("GetSessions")]
        public async Task<IActionResult> GetSessions([FromBody] SessionIdsPagedRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.SessionIds == null || request.SessionIds.Count == 0)
            {
                return BadRequest(new { Status = 0, Message = "SessionIds are required." });
            }

            var rows = await _pythonBridgeService.GetSessionsAsync(
                request.SessionIds,
                HttpContext.RequestAborted
            );

            return Ok(new { Status = 1, Count = rows.Count, Data = rows });
        }

        [AllowAnonymous]
        [HttpGet("GetUser")]
        public async Task<IActionResult> GetUser([FromQuery] int userId)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (userId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "userId is required." });
            }

            var user = await _pythonBridgeService.GetUserByIdAsync(
                userId,
                HttpContext.RequestAborted
            );

            if (user == null)
            {
                return NotFound(new { Status = 0, Message = "User not found." });
            }

            return Ok(new { Status = 1, Data = user });
        }

        [AllowAnonymous]
        [HttpGet("GetUserThresholds")]
        public async Task<IActionResult> GetUserThresholds([FromQuery] int userId)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (userId <= 0)
            {
                return BadRequest(new { Status = 0, Message = "userId is required." });
            }

            var thresholds = await _pythonBridgeService.GetUserThresholdsAsync(
                userId,
                HttpContext.RequestAborted
            );

            if (thresholds == null)
            {
                return NotFound(new { Status = 0, Message = "Thresholds not found." });
            }

            return Ok(new { Status = 1, Data = thresholds });
        }

        [AllowAnonymous]
        [HttpPost("UpdateProjectDownloadPath")]
        public async Task<IActionResult> UpdateProjectDownloadPath([FromBody] ProjectDownloadPathUpdateRequest request)
        {
            var authResult = EnsureAuthorized();
            if (authResult is not null) return authResult;

            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.DownloadPath))
            {
                return BadRequest(new { Status = 0, Message = "ProjectId and DownloadPath are required." });
            }

            var updated = await _pythonBridgeService.UpdateProjectDownloadPathAsync(
                request.ProjectId,
                request.DownloadPath.Trim(),
                HttpContext.RequestAborted
            );

            if (!updated)
            {
                return NotFound(new { Status = 0, Message = "Project not found." });
            }

            return Ok(new { Status = 1, Updated = true });
        }
    }
}
