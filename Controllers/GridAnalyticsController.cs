using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Helper;
using SignalTracker.Models;
using SignalTracker.Services;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class GridAnalyticsController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly RedisService _redis;
        private readonly UserScopeService _userScope;
        private readonly LicenseFeatureService _licenseFeatureService;
        private readonly IConfiguration _configuration;
        private const double METERS_PER_DEGREE_LAT = 111320.0;

        public GridAnalyticsController(
            ApplicationDbContext context,
            RedisService redis,
            UserScopeService userScope,
            LicenseFeatureService licenseFeatureService,
            IConfiguration configuration)
        {
            _db = context;
            _redis = redis;
            _userScope = userScope;
            _licenseFeatureService = licenseFeatureService;
            _configuration = configuration;
        }

        private bool IsPythonBridgeAuthorized()
        {
            var configuredKey =
                _configuration["PythonBridge:ApiKey"]
                ?? Environment.GetEnvironmentVariable("PYTHON_BRIDGE_API_KEY")
                ?? Environment.GetEnvironmentVariable("SIGNAL_TRACKERS_BRIDGE_KEY")
                ?? "s-tracer-local-bridge";

            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                return false;
            }

            Request.Headers.TryGetValue("X-Python-Bridge-Key", out var incoming);
            if (string.IsNullOrWhiteSpace(incoming.ToString()))
            {
                return false;
            }

            return string.Equals(
                configuredKey.Trim(),
                incoming.ToString().Trim(),
                StringComparison.Ordinal
            );
        }

        private int GetCurrentUserId()
        {
            var raw = User.FindFirstValue("UserId");
            return int.TryParse(raw, out var uid) ? uid : 0;
        }

        private async Task<bool> CanUseGridFeatureAsync()
        {
            if (_userScope.IsSuperAdmin(User))
                return true;

            var userId = GetCurrentUserId();
            if (userId <= 0)
                return false;

            return await _licenseFeatureService.HasFeatureAccessAsync(
                userId,
                LicenseFeatureService.FeatureGridFetch,
                defaultAllow: false);
        }

        // =====================================================================
        // POST api/GridAnalytics/ComputeAndStoreGridAnalytics
        // Evaluates the grid and stores results in the grid_analytics_results table
        // =====================================================================
        [HttpPost("ComputeAndStoreGridAnalytics")]
        public async Task<IActionResult> ComputeAndStoreGridAnalytics(
            [FromQuery] int projectId,
            [FromQuery] double? gridSize = null,
            [FromQuery] int? regionId = null,
            [FromQuery] int? company_id = null,
            [FromQuery] int? scenario_id = null)
        {
            if (!await CanUseGridFeatureAsync())
                return StatusCode(403, new { Status = 0, Message = "Feature disabled in license: grid_fetch", Code = "FEATURE_NOT_ENABLED" });

            var sw = Stopwatch.StartNew();

            // â”€â”€ 1. AUTH & COMPANY SCOPING â”€â”€
            int targetCompanyId = _userScope.GetTargetCompanyId(User, company_id);
            bool isSuperAdmin = _userScope.IsSuperAdmin(User);
            if (!isSuperAdmin && targetCompanyId == 0)
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve company context." });

            try
            {
                var conn = _db.Database.GetDbConnection();
                bool shouldClose = false;

                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                try
                {
                    // â”€â”€ ENSURE TABLE EXISTS â”€â”€
                    await using (var cmdCreate = conn.CreateCommand())
                    {
                        cmdCreate.CommandText = @"
                        CREATE TABLE IF NOT EXISTS grid_analytics_results (
                            id INT AUTO_INCREMENT PRIMARY KEY,
                            project_id INT NOT NULL,
                            region_id INT,
                            scenario_id INT NULL,
                            public_scenario_id INT NULL,
                            grid_size_meters DOUBLE NOT NULL,
                            grid_id VARCHAR(50) NOT NULL,
                            center_lat DOUBLE NOT NULL,
                            center_lon DOUBLE NOT NULL,
                            min_lat DOUBLE NOT NULL,
                            max_lat DOUBLE NOT NULL,
                            min_lon DOUBLE NOT NULL,
                            max_lon DOUBLE NOT NULL,
                            baseline_point_count INT NOT NULL,
                            optimized_point_count INT NOT NULL,

                            baseline_avg_rsrp DOUBLE, baseline_avg_rsrq DOUBLE, baseline_avg_sinr DOUBLE,
                            baseline_median_rsrp DOUBLE, baseline_median_rsrq DOUBLE, baseline_median_sinr DOUBLE,
                            baseline_min_rsrp DOUBLE, baseline_min_rsrq DOUBLE, baseline_min_sinr DOUBLE,
                            baseline_max_rsrp DOUBLE, baseline_max_rsrq DOUBLE, baseline_max_sinr DOUBLE,
                            baseline_mode_rsrp DOUBLE, baseline_mode_rsrq DOUBLE, baseline_mode_sinr DOUBLE,
                            baseline_best_operator_avg VARCHAR(100),
                            baseline_best_operator_min VARCHAR(100),
                            baseline_best_operator_max VARCHAR(100),

                            optimized_avg_rsrp DOUBLE, optimized_avg_rsrq DOUBLE, optimized_avg_sinr DOUBLE,
                            optimized_median_rsrp DOUBLE, optimized_median_rsrq DOUBLE, optimized_median_sinr DOUBLE,
                            optimized_min_rsrp DOUBLE, optimized_min_rsrq DOUBLE, optimized_min_sinr DOUBLE,
                            optimized_max_rsrp DOUBLE, optimized_max_rsrq DOUBLE, optimized_max_sinr DOUBLE,
                            optimized_mode_rsrp DOUBLE, optimized_mode_rsrq DOUBLE, optimized_mode_sinr DOUBLE,
                            optimized_best_operator_avg VARCHAR(100),
                            optimized_best_operator_min VARCHAR(100),
                            optimized_best_operator_max VARCHAR(100),

                            diff_avg_rsrp DOUBLE, diff_avg_rsrq DOUBLE, diff_avg_sinr DOUBLE,
                            diff_median_rsrp DOUBLE, diff_median_rsrq DOUBLE, diff_median_sinr DOUBLE,
                            diff_min_rsrp DOUBLE, diff_min_rsrq DOUBLE, diff_min_sinr DOUBLE,
                            diff_max_rsrp DOUBLE, diff_max_rsrq DOUBLE, diff_max_sinr DOUBLE,
                            diff_mode_rsrp DOUBLE, diff_mode_rsrq DOUBLE, diff_mode_sinr DOUBLE,

                            created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                        );";
                        await cmdCreate.ExecuteNonQueryAsync();
                    }

                    // Ensure newly introduced min/diff_min columns exist for older deployments.
                    var requiredColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["baseline_min_rsrp"] = "DOUBLE",
                        ["baseline_min_rsrq"] = "DOUBLE",
                        ["baseline_min_sinr"] = "DOUBLE",
                        ["optimized_min_rsrp"] = "DOUBLE",
                        ["optimized_min_rsrq"] = "DOUBLE",
                        ["optimized_min_sinr"] = "DOUBLE",
                        ["diff_min_rsrp"] = "DOUBLE",
                        ["diff_min_rsrq"] = "DOUBLE",
                        ["diff_min_sinr"] = "DOUBLE",
                        ["baseline_best_operator_avg"] = "VARCHAR(100)",
                        ["baseline_best_operator_min"] = "VARCHAR(100)",
                        ["baseline_best_operator_max"] = "VARCHAR(100)",
                        ["optimized_best_operator_avg"] = "VARCHAR(100)",
                        ["optimized_best_operator_min"] = "VARCHAR(100)",
                        ["optimized_best_operator_max"] = "VARCHAR(100)",
                        ["scenario_id"] = "INT NULL",
                        ["public_scenario_id"] = "INT NULL",
                    };

                    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using (var cmdCols = conn.CreateCommand())
                    {
                        cmdCols.CommandText = @"
                            SELECT COLUMN_NAME
                            FROM information_schema.columns
                            WHERE table_schema = DATABASE()
                              AND table_name = 'grid_analytics_results';";

                        await using var reader = await cmdCols.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            var colName = reader.IsDBNull(0) ? null : reader.GetString(0);
                            if (!string.IsNullOrWhiteSpace(colName))
                                existingColumns.Add(colName);
                        }
                    }

                    var missingClauses = requiredColumns
                        .Where(kv => !existingColumns.Contains(kv.Key))
                        .Select(kv => $"ADD COLUMN `{kv.Key}` {kv.Value}")
                        .ToList();

                    if (missingClauses.Count > 0)
                    {
                        await using var cmdAlter = conn.CreateCommand();
                        cmdAlter.CommandText =
                            $"ALTER TABLE grid_analytics_results {string.Join(", ", missingClauses)};";
                        await cmdAlter.ExecuteNonQueryAsync();
                    }

                    // Ensure index for fetch path (project_id + region_id).
                    bool hasProjectRegionIndex = false;
                    await using (var cmdIdxCheck = conn.CreateCommand())
                    {
                        cmdIdxCheck.CommandText = @"
                            SELECT COUNT(1)
                            FROM information_schema.statistics
                            WHERE table_schema = DATABASE()
                              AND table_name = 'grid_analytics_results'
                              AND index_name = 'idx_gar_project_region';";
                        hasProjectRegionIndex =
                            Convert.ToInt32(await cmdIdxCheck.ExecuteScalarAsync() ?? 0) > 0;
                    }

                    if (!hasProjectRegionIndex)
                    {
                        await using var cmdCreateIdx = conn.CreateCommand();
                        cmdCreateIdx.CommandText =
                            "CREATE INDEX idx_gar_project_region ON grid_analytics_results (project_id, region_id);";
                        await cmdCreateIdx.ExecuteNonQueryAsync();
                    }

                    // â”€â”€ 3. FETCH grid_size FROM tbl_project â”€â”€
                    double gridSizeMeters = gridSize ?? 0;
                    if (gridSizeMeters <= 0)
                    {
                        await using var cmdProj = conn.CreateCommand();
                        cmdProj.CommandText = "SELECT grid_size FROM tbl_project WHERE id = @pid";
                        AddParam(cmdProj, "@pid", projectId);
                        var gsRaw = await cmdProj.ExecuteScalarAsync();
                        if (gsRaw != null && gsRaw != DBNull.Value)
                            double.TryParse(gsRaw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out gridSizeMeters);
                    }
                    if (gridSizeMeters <= 0)
                        return BadRequest(new { Status = 0, Message = "grid_size not available. Pass gridSize query param (meters)." });

                    // â”€â”€ 4. SECURITY: project belongs to company â”€â”€
                    if (targetCompanyId > 0)
                    {
                        await using var cmdAcc = conn.CreateCommand();
                        cmdAcc.CommandText = "SELECT COUNT(1) FROM tbl_project WHERE id = @pid AND company_id = @cid";
                        AddParam(cmdAcc, "@pid", projectId);
                        AddParam(cmdAcc, "@cid", targetCompanyId);
                        var accRes = await cmdAcc.ExecuteScalarAsync();
                        if (accRes == null || Convert.ToInt32(accRes) == 0)
                            return Unauthorized(new { Status = 0, Message = "Project does not belong to your company." });
                    }

                    if (gridSize.HasValue && gridSize.Value > 0)
                    {
                        await using var cmdUpdateGrid = conn.CreateCommand();
                        cmdUpdateGrid.CommandText = "UPDATE tbl_project SET grid_size = @gridSize WHERE id = @pid";
                        AddParam(
                            cmdUpdateGrid,
                            "@gridSize",
                            gridSize.Value.ToString(CultureInfo.InvariantCulture)
                        );
                        AddParam(cmdUpdateGrid, "@pid", projectId);
                        await cmdUpdateGrid.ExecuteNonQueryAsync();
                    }

                    // â”€â”€ 5. FETCH PREDICTION DATA (raw ADO.NET) â”€â”€
                    // Use optimized + baseline-only logic similar to GetSitePredictionOptimised.
                    // Baseline rows are included only when there is no matching optimized row
                    // across stable identifiers (nodeb_id_cell_id, node_b_id+cell_id, site_id+cell_id)
                    // with lat/lon + cell_id fallback matching.
                    var resolvedScenarioId = await ResolveScenarioIdAsync(conn, projectId, scenario_id);
                    var baselinePts = await FetchPredictionData(conn, "lte_prediction_baseline_results", projectId);
                    var optimizedRawPts = await FetchPredictionData(
                        conn,
                        "lte_prediction_optimised_results",
                        projectId,
                        resolvedScenarioId
                    );
                    var optimizedSelectionKeys = await FetchOptimizedSelectionKeys(conn, projectId);
                    var optimizedPts = BuildStatusAwareOptimizedPoints(
                        baselinePts,
                        optimizedRawPts,
                        optimizedSelectionKeys);
                    var allPredictionPts = baselinePts.Concat(optimizedPts).ToList();
                    if (allPredictionPts.Count == 0)
                    {
                        return Ok(new GridAnalyticsResponse
                        {
                            Status = 1,
                            Message = "No baseline/optimized prediction points found for this project. Nothing to compute.",
                            Data = null
                        });
                    }

                    // â”€â”€ 6. RESOLVE GRID BOUNDARY â”€â”€
                    // If regionId is explicitly provided, use that region polygon.
                    // Otherwise, use project map_regions polygons (all active regions) as boundary.
                    // If no valid map_regions polygon exists, fallback to prediction bounds.
                    string boundarySource = "prediction_bounds";
                    var vertices = new List<(double Lat, double Lon)>();

                    if (regionId.HasValue && regionId.Value > 0)
                    {
                        string? polygonWkt = null;
                        await using var cmdPoly = conn.CreateCommand();
                        cmdPoly.CommandText = "SELECT ST_AsText(region) FROM map_regions WHERE id = @rid AND tbl_project_id = @pid AND status = 1 LIMIT 1";
                        AddParam(cmdPoly, "@rid", regionId.Value);
                        AddParam(cmdPoly, "@pid", projectId);
                        var polyRes = await cmdPoly.ExecuteScalarAsync();
                        if (polyRes != null && polyRes != DBNull.Value)
                            polygonWkt = polyRes.ToString();

                        if (string.IsNullOrWhiteSpace(polygonWkt))
                            return BadRequest(new { Status = 0, Message = "No polygon found for provided regionId." });

                        vertices = ParsePolygonWkt(polygonWkt);
                        if (vertices.Count < 3)
                            return BadRequest(new { Status = 0, Message = "Invalid polygon geometry for provided regionId." });

                        boundarySource = "map_region";
                    }
                    else
                    {
                        var projectRegionVertices = new List<(double Lat, double Lon)>();
                        await using var cmdPolyAll = conn.CreateCommand();
                        cmdPolyAll.CommandText =
                            "SELECT ST_AsText(region) FROM map_regions WHERE tbl_project_id = @pid AND status = 1";
                        AddParam(cmdPolyAll, "@pid", projectId);

                        await using (var rdrPoly = await cmdPolyAll.ExecuteReaderAsync())
                        {
                            while (await rdrPoly.ReadAsync())
                            {
                                if (rdrPoly.IsDBNull(0)) continue;
                                var wkt = rdrPoly.GetString(0);
                                var parsed = ParsePolygonWkt(wkt);
                                if (parsed.Count >= 3)
                                {
                                    projectRegionVertices.AddRange(parsed);
                                }
                            }
                        }

                        if (projectRegionVertices.Count >= 3)
                        {
                            // Use project polygon envelope to cover all project regions.
                            vertices = BuildBoundingPolygonFromVertices(projectRegionVertices);
                            boundarySource = "map_regions_project";
                        }
                        else
                        {
                            vertices = BuildBoundingPolygonFromPoints(allPredictionPts);
                            boundarySource = "prediction_bounds";
                        }
                    }

                    if (vertices.Count < 3)
                    {
                        return Ok(new GridAnalyticsResponse
                        {
                            Status = 1,
                            Message = "Unable to compute grid boundary from region or prediction data.",
                            Data = null
                        });
                    }

                    var (gridCells, gLat, gLon, mLat, mLon) = GenerateGrid(vertices, gridSizeMeters);
                    if (gridCells.Count == 0)
                        return Ok(new GridAnalyticsResponse { Status = 1, Message = $"No grid cells generated for boundary source: {boundarySource}." });

                    // â”€â”€ 7. MAP POINTS â†’ GRIDS â”€â”€
                    var baseByGrid = MapPointsToGrids(baselinePts, mLat, mLon, gLat, gLon, gridCells);
                    var optByGrid = MapPointsToGrids(optimizedPts, mLat, mLon, gLat, gLon, gridCells);
                    int baselineMappedPoints = baseByGrid.Values.Sum(v => v.Count);
                    int optimizedMappedPoints = optByGrid.Values.Sum(v => v.Count);

                    // Project map_regions boundary can still be stale/misaligned. If no point maps, fallback.
                    if (!regionId.HasValue && boundarySource == "map_regions_project" &&
                        baselineMappedPoints == 0 && optimizedMappedPoints == 0)
                    {
                        var fallbackVertices = BuildBoundingPolygonFromPoints(allPredictionPts);
                        if (fallbackVertices.Count >= 3)
                        {
                            var fallbackGrid = GenerateGrid(fallbackVertices, gridSizeMeters);
                            gridCells = fallbackGrid.cells;
                            gLat = fallbackGrid.gLat;
                            gLon = fallbackGrid.gLon;
                            mLat = fallbackGrid.minLat;
                            mLon = fallbackGrid.minLon;
                            boundarySource = "prediction_bounds_fallback";

                            baseByGrid = MapPointsToGrids(baselinePts, mLat, mLon, gLat, gLon, gridCells);
                            optByGrid = MapPointsToGrids(optimizedPts, mLat, mLon, gLat, gLon, gridCells);
                            baselineMappedPoints = baseByGrid.Values.Sum(v => v.Count);
                            optimizedMappedPoints = optByGrid.Values.Sum(v => v.Count);
                        }
                    }

                    // â”€â”€ 8. COMPUTE METRICS & DIFFERENCES â”€â”€
                    var resultsList = new List<grid_analytics_results>();
                    foreach (var cell in gridCells.Values)
                    {
                        var bData = baseByGrid.TryGetValue(cell.Key, out var bl) ? bl : new List<PredPoint>();
                        var oData = optByGrid.TryGetValue(cell.Key, out var ol) ? ol : new List<PredPoint>();
                        if (bData.Count == 0 && oData.Count == 0) continue;

                        var bm = ComputeMetrics(bData);
                        var om = ComputeMetrics(oData);
                        var diff = ComputeDiff(bm, om);
                        var baselineBestOperators = ComputeBestOperators(bData);
                        var optimizedBestOperators = ComputeBestOperators(oData);

                        resultsList.Add(new grid_analytics_results
                        {
                            project_id = projectId,
                            region_id = regionId,
                            scenario_id = resolvedScenarioId,
                            public_scenario_id = resolvedScenarioId,
                            grid_size_meters = gridSizeMeters,
                            grid_id = cell.GridId,
                            center_lat = cell.CenterLat, center_lon = cell.CenterLon,
                            min_lat = cell.MinLat, min_lon = cell.MinLon,
                            max_lat = cell.MaxLat, max_lon = cell.MaxLon,

                            baseline_point_count = bData.Count,
                            optimized_point_count = oData.Count,

                            baseline_avg_rsrp = bm.avg_rsrp, baseline_avg_rsrq = bm.avg_rsrq, baseline_avg_sinr = bm.avg_sinr,
                            baseline_median_rsrp = bm.median_rsrp, baseline_median_rsrq = bm.median_rsrq, baseline_median_sinr = bm.median_sinr,
                            baseline_min_rsrp = bm.min_rsrp, baseline_min_rsrq = bm.min_rsrq, baseline_min_sinr = bm.min_sinr,
                            baseline_max_rsrp = bm.max_rsrp, baseline_max_rsrq = bm.max_rsrq, baseline_max_sinr = bm.max_sinr,
                            baseline_mode_rsrp = bm.mode_rsrp, baseline_mode_rsrq = bm.mode_rsrq, baseline_mode_sinr = bm.mode_sinr,
                            baseline_best_operator_avg = baselineBestOperators.best_operator_avg,
                            baseline_best_operator_min = baselineBestOperators.best_operator_min,
                            baseline_best_operator_max = baselineBestOperators.best_operator_max,

                            optimized_avg_rsrp = om.avg_rsrp, optimized_avg_rsrq = om.avg_rsrq, optimized_avg_sinr = om.avg_sinr,
                            optimized_median_rsrp = om.median_rsrp, optimized_median_rsrq = om.median_rsrq, optimized_median_sinr = om.median_sinr,
                            optimized_min_rsrp = om.min_rsrp, optimized_min_rsrq = om.min_rsrq, optimized_min_sinr = om.min_sinr,
                            optimized_max_rsrp = om.max_rsrp, optimized_max_rsrq = om.max_rsrq, optimized_max_sinr = om.max_sinr,
                            optimized_mode_rsrp = om.mode_rsrp, optimized_mode_rsrq = om.mode_rsrq, optimized_mode_sinr = om.mode_sinr,
                            optimized_best_operator_avg = optimizedBestOperators.best_operator_avg,
                            optimized_best_operator_min = optimizedBestOperators.best_operator_min,
                            optimized_best_operator_max = optimizedBestOperators.best_operator_max,

                            diff_avg_rsrp = diff.diff_avg_rsrp, diff_avg_rsrq = diff.diff_avg_rsrq, diff_avg_sinr = diff.diff_avg_sinr,
                            diff_median_rsrp = diff.diff_median_rsrp, diff_median_rsrq = diff.diff_median_rsrq, diff_median_sinr = diff.diff_median_sinr,
                            diff_min_rsrp = diff.diff_min_rsrp, diff_min_rsrq = diff.diff_min_rsrq, diff_min_sinr = diff.diff_min_sinr,
                            diff_max_rsrp = diff.diff_max_rsrp, diff_max_rsrq = diff.diff_max_rsrq, diff_max_sinr = diff.diff_max_sinr,
                            diff_mode_rsrp = diff.diff_mode_rsrp, diff_mode_rsrq = diff.diff_mode_rsrq, diff_mode_sinr = diff.diff_mode_sinr,
                            created_at = DateTime.UtcNow
                        });
                    }

                    // â”€â”€ 9. REMOVE EXISTING AND STORE TO DATABASE â”€â”€
                    await using (var cmdDel = conn.CreateCommand())
                    {
                        if (regionId.HasValue && regionId.Value > 0)
                        {
                            cmdDel.CommandText = @"
                                DELETE FROM grid_analytics_results
                                WHERE project_id = @pid
                                  AND region_id = @rid
                                  AND (
                                      (@sid IS NULL AND COALESCE(public_scenario_id, scenario_id) IS NULL)
                                      OR COALESCE(public_scenario_id, scenario_id) = @sid
                                  )";
                            AddParam(cmdDel, "@rid", regionId.Value);
                            AddParam(cmdDel, "@pid", projectId);
                            AddParam(cmdDel, "@sid", resolvedScenarioId.HasValue ? resolvedScenarioId.Value : DBNull.Value);
                        }
                        else
                        {
                            cmdDel.CommandText = @"
                                DELETE FROM grid_analytics_results
                                WHERE project_id = @pid
                                  AND (region_id IS NULL OR region_id <= 0)
                                  AND (
                                      (@sid IS NULL AND COALESCE(public_scenario_id, scenario_id) IS NULL)
                                      OR COALESCE(public_scenario_id, scenario_id) = @sid
                                  )";
                            AddParam(cmdDel, "@pid", projectId);
                            AddParam(cmdDel, "@sid", resolvedScenarioId.HasValue ? resolvedScenarioId.Value : DBNull.Value);
                        }
                        await cmdDel.ExecuteNonQueryAsync();
                    }

                    if (resultsList.Any())
                    {
                        _db.grid_analytics_results.AddRange(resultsList);
                        await _db.SaveChangesAsync();
                    }

                    // Invalidate potentially cached read calls
                    if (_redis != null && _redis.IsConnected)
                    {
                        try { await _redis.DeleteByPatternAsync($"gridanalytics*:{projectId}:{regionId ?? 0}:*"); } catch { }
                    }

                    sw.Stop();
                    var response = new GridAnalyticsResponse
                    {
                        Status = 1,
                        Message = $"Grid analytics computed and stored. boundary={boundarySource}; baselinePts={baselinePts.Count}; optimizedPts={optimizedPts.Count}; optimizedRawPts={optimizedRawPts.Count}; optimizedKeySites={optimizedSelectionKeys.SiteIds.Count}; optimizedKeyCells={optimizedSelectionKeys.CellIds.Count}; baselineMapped={baselineMappedPoints}; optimizedMapped={optimizedMappedPoints}; totalGrids={gridCells.Count}; gridsWithData={resultsList.Count}.",
                        Data = new GridAnalyticsData
                        {
                            project_id = projectId, grid_size_meters = gridSizeMeters,
                            total_grids = gridCells.Count, total_grids_with_data = resultsList.Count,
                            total_baseline_points = baselinePts.Count, total_optimized_points = optimizedPts.Count,
                            grids = ConvertToGridCellResults(resultsList)
                        }
                    };
                    return Ok(response);
                }
                finally
                {
                    if (shouldClose && conn.State == ConnectionState.Open)
                        await conn.CloseAsync();
                }
            }
            catch (Exception)
            {
                sw.Stop();
                return StatusCode(500, new { Status = 0, Message = "An internal server error occurred." });
            }
        }

        // =====================================================================
        // POST api/GridAnalytics/SetProjectGridSize
        // Saves manual grid size preference to tbl_project.grid_size
        // =====================================================================
        [HttpPost("SetProjectGridSize")]
        public async Task<IActionResult> SetProjectGridSize(
            [FromQuery] int projectId,
            [FromQuery] double gridSize,
            [FromQuery] int? company_id = null)
        {
            if (!await CanUseGridFeatureAsync())
                return StatusCode(403, new { Status = 0, Message = "Feature disabled in license: grid_fetch", Code = "FEATURE_NOT_ENABLED" });

            int targetCompanyId = _userScope.GetTargetCompanyId(User, company_id);
            bool isSuperAdmin = _userScope.IsSuperAdmin(User);
            if (!isSuperAdmin && targetCompanyId == 0)
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve company context." });

            if (projectId <= 0)
                return BadRequest(new { Status = 0, Message = "Invalid projectId." });

            if (double.IsNaN(gridSize) || double.IsInfinity(gridSize) || gridSize <= 0)
                return BadRequest(new { Status = 0, Message = "gridSize must be a positive number." });

            try
            {
                var project = await _db.tbl_project.FirstOrDefaultAsync(p => p.id == projectId);
                if (project == null)
                    return NotFound(new { Status = 0, Message = "Project not found." });

                if (!isSuperAdmin && targetCompanyId > 0 && project.company_id != targetCompanyId)
                    return Unauthorized(new { Status = 0, Message = "Project does not belong to your company." });

                project.grid_size = gridSize.ToString(CultureInfo.InvariantCulture);
                await _db.SaveChangesAsync();

                return Ok(new
                {
                    Status = 1,
                    Message = "Project grid size updated.",
                    Data = new
                    {
                        project_id = projectId,
                        grid_size_meters = gridSize
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + SafeException.Get(ex) });
            }
        }

        // =====================================================================
        // GET api/GridAnalytics/GetGridAnalytics
        // Fetches stored grid analytics for a project from the DB
        // =====================================================================
        [AllowAnonymous]
        [HttpGet("GetGridAnalytics")]
        public async Task<IActionResult> GetGridAnalytics(
            [FromQuery] int projectId,
            [FromQuery] int? regionId = null,
            [FromQuery] int? company_id = null,
            [FromQuery] string? version = null,
            [FromQuery] int? scenario_id = null)
        {
            var bridgeAuthorized = IsPythonBridgeAuthorized();
            var userAuthenticated = User?.Identity?.IsAuthenticated == true;
            if (!bridgeAuthorized && !userAuthenticated)
            {
                return Unauthorized(new { Status = 0, Message = "Unauthorized." });
            }

            if (!bridgeAuthorized && !await CanUseGridFeatureAsync())
                return StatusCode(403, new { Status = 0, Message = "Feature disabled in license: grid_fetch", Code = "FEATURE_NOT_ENABLED" });

            var sw = Stopwatch.StartNew();

            // Auth & Scoping
            int targetCompanyId = bridgeAuthorized ? 0 : _userScope.GetTargetCompanyId(User, company_id);
            bool isSuperAdmin = bridgeAuthorized || _userScope.IsSuperAdmin(User);
            if (!isSuperAdmin && targetCompanyId == 0)
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve company context." });

            try
            {
                // Security check
                if (targetCompanyId > 0)
                {
                    bool access = await _db.tbl_project.AnyAsync(p => p.id == projectId && p.company_id == targetCompanyId);
                    if (!access)
                        return Unauthorized(new { Status = 0, Message = "Project does not belong to your company." });
                }

                var conn = _db.Database.GetDbConnection();
                await conn.OpenAsync();
                var normalizedVersion = (version ?? "original").Trim().ToLowerInvariant();
                var isScenarioGridVersion =
                    normalizedVersion == "updated" ||
                    normalizedVersion == "optimized" ||
                    normalizedVersion == "optimised" ||
                    normalizedVersion == "delta";
                var effectiveScenarioId = isScenarioGridVersion
                    ? await ResolveScenarioIdAsync(conn, projectId, scenario_id)
                    : null;
                string cacheKey = $"gridanalytics:v2:{projectId}:{regionId ?? 0}:{normalizedVersion}:{effectiveScenarioId?.ToString() ?? "none"}";
                if (_redis != null && _redis.IsConnected)
                {
                    try
                    {
                        var cached = await _redis.GetObjectAsync<GridAnalyticsResponse>(cacheKey);
                        if (cached != null)
                        {
                            await conn.CloseAsync();
                            sw.Stop();
                            Response.Headers["X-Cache"] = "HIT";
                            return Ok(cached);
                        }
                    }
                    catch { }
                }

                // Check Table Exists
                bool tableExists = false;
                await using (var cmdSchema = conn.CreateCommand())
                {
                    cmdSchema.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'grid_analytics_results';";
                    var count = Convert.ToInt32(await cmdSchema.ExecuteScalarAsync());
                    tableExists = count > 0;
                }
                
                if(!tableExists) {
                    await conn.CloseAsync();
                    return Ok(new GridAnalyticsResponse { Status = 0, Message = "Table does not exist. Call ComputeAndStoreGridAnalytics first." });
                }

                // Backward-compat: ensure scenario columns exist before EF query materializes model.
                var gridScenarioColumns = await GetTableColumnSetAsync(conn, "grid_analytics_results");
                if (!gridScenarioColumns.Contains("scenario_id"))
                {
                    await using var cmdAlterScenarioCol = conn.CreateCommand();
                    cmdAlterScenarioCol.CommandText =
                        "ALTER TABLE grid_analytics_results ADD COLUMN scenario_id INT NULL AFTER region_id;";
                    await cmdAlterScenarioCol.ExecuteNonQueryAsync();
                    gridScenarioColumns.Add("scenario_id");
                }

                if (!gridScenarioColumns.Contains("public_scenario_id"))
                {
                    await using var cmdAlterPublicScenarioCol = conn.CreateCommand();
                    cmdAlterPublicScenarioCol.CommandText =
                        "ALTER TABLE grid_analytics_results ADD COLUMN public_scenario_id INT NULL AFTER scenario_id;";
                    await cmdAlterPublicScenarioCol.ExecuteNonQueryAsync();
                }

                // Fetch directly from DB using EF
                List<grid_analytics_results> storedResults;
                var query = _db.grid_analytics_results.AsNoTracking().Where(g => g.project_id == projectId);
                if (regionId.HasValue && regionId.Value > 0)
                    query = query.Where(g => g.region_id == regionId);
                else
                    query = query.Where(g => g.region_id == null || g.region_id <= 0);

                if (isScenarioGridVersion)
                {
                    if (effectiveScenarioId.HasValue)
                        query = query.Where(g =>
                            g.public_scenario_id == effectiveScenarioId.Value ||
                            (g.public_scenario_id == null && g.scenario_id == effectiveScenarioId.Value));
                    else
                        query = query.Where(g => g.public_scenario_id == null && g.scenario_id == null);
                }

                storedResults = await query.ToListAsync();
                storedResults = SelectLatestGridGeneration(storedResults);
                
                await conn.CloseAsync();

                if (storedResults.Count == 0)
                {
                    return Ok(new GridAnalyticsResponse
                    {
                        Status = 1,
                        Message = "No stored grid analytics found for this project. Please call ComputeAndStoreGridAnalytics first.",
                        Data = null
                    });
                }

                var responseData = new GridAnalyticsData
                {
                    project_id = projectId,
                    grid_size_meters = storedResults.First().grid_size_meters,
                    total_grids_with_data = storedResults.Count,
                    total_baseline_points = storedResults.Sum(s => s.baseline_point_count),
                    total_optimized_points = storedResults.Sum(s => s.optimized_point_count),
                    grids = ConvertToGridCellResults(storedResults)
                };

                var response = new GridAnalyticsResponse
                {
                    Status = 1,
                    Message = "Grid analytics fetched successfully from storage.",
                    Data = responseData
                };

                if (_redis != null && _redis.IsConnected)
                {
                    try { await _redis.SetObjectAsync(cacheKey, response, ttlSeconds: 600); } catch { }
                }

                sw.Stop();
                Response.Headers["X-Cache"] = "MISS";
                Response.Headers["X-Total-Ms"] = sw.ElapsedMilliseconds.ToString();
                return Ok(response);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return StatusCode(500, new { Status = 0, Message = "Error: " + SafeException.Get(ex) });
            }
        }

        // =====================================================================
        // GET api/GridAnalytics/GetCoverageOptimizationSummary
        // Project-level summary from site_prediction vs site_prediction_optimized
        // =====================================================================
        [HttpGet("GetCoverageOptimizationSummary")]
        public async Task<IActionResult> GetCoverageOptimizationSummary(
            [FromQuery] int projectId,
            [FromQuery] int? company_id = null)
        {
            if (!await CanUseGridFeatureAsync())
                return StatusCode(403, new { Status = 0, Message = "Feature disabled in license: grid_fetch", Code = "FEATURE_NOT_ENABLED" });

            var sw = Stopwatch.StartNew();

            int targetCompanyId = _userScope.GetTargetCompanyId(User, company_id);
            bool isSuperAdmin = _userScope.IsSuperAdmin(User);
            if (!isSuperAdmin && targetCompanyId == 0)
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve company context." });

            try
            {
                if (projectId <= 0)
                    return BadRequest(new { Status = 0, Message = "projectId is required." });

                if (targetCompanyId > 0)
                {
                    bool access = await _db.tbl_project.AnyAsync(p => p.id == projectId && p.company_id == targetCompanyId);
                    if (!access)
                        return Unauthorized(new { Status = 0, Message = "Project does not belong to your company." });
                }

                var conn = _db.Database.GetDbConnection();
                bool shouldClose = false;
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync();
                    shouldClose = true;
                }

                try
                {
                    int baselineTotalRows;
                    int optimizedTotalRows;

                    await using (var cmdBaseCount = conn.CreateCommand())
                    {
                        cmdBaseCount.CommandText = "SELECT COUNT(1) FROM site_prediction WHERE tbl_project_id = @pid;";
                        AddParam(cmdBaseCount, "@pid", projectId);
                        baselineTotalRows = Convert.ToInt32(await cmdBaseCount.ExecuteScalarAsync() ?? 0);
                    }

                    bool optimizedTableExists;
                    await using (var cmdSchema = conn.CreateCommand())
                    {
                        cmdSchema.CommandText = @"
                            SELECT COUNT(1)
                            FROM information_schema.tables
                            WHERE table_schema = DATABASE()
                              AND table_name = 'site_prediction_optimized';";
                        optimizedTableExists = Convert.ToInt32(await cmdSchema.ExecuteScalarAsync() ?? 0) > 0;
                    }

                    if (!optimizedTableExists)
                    {
                        sw.Stop();
                        return Ok(new CoverageOptimizationSummaryResponse
                        {
                            Status = 1,
                            Message = "Optimized table not found. No optimized changes available yet.",
                            Data = new CoverageOptimizationSummaryData
                            {
                                project_id = projectId,
                                baseline_total_rows = baselineTotalRows,
                                optimized_total_rows = 0,
                                matched_optimized_rows = 0,
                                changed_row_count = 0,
                                unchanged_row_count = 0,
                                changed_sector_count = 0,
                                field_changes = new List<CoverageFieldChangeCount>(),
                                changed_sectors = new List<CoverageChangedSectorDetail>()
                            }
                        });
                    }

                    await using (var cmdOptCount = conn.CreateCommand())
                    {
                        cmdOptCount.CommandText = "SELECT COUNT(1) FROM site_prediction_optimized WHERE tbl_project_id = @pid;";
                        AddParam(cmdOptCount, "@pid", projectId);
                        optimizedTotalRows = Convert.ToInt32(await cmdOptCount.ExecuteScalarAsync() ?? 0);
                    }

                    var compareFields = new[]
                    {
                        "site", "sector", "cell_id",
                        "latitude", "longitude",
                        "tac", "pci",
                        "azimuth", "height", "bw", "m_tilt", "e_tilt",
                        "maximum_transmission_power_of_resource",
                        "real_transmit_power_of_resource",
                        "reference_signal_power",
                        "cellsize", "frequency", "band",
                        "uplink_center_frequency", "downlink_frequency",
                        "earfcn", "cluster", "Technology"
                    };

                    var fieldChangeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in compareFields) fieldChangeCounts[field] = 0;

                    int matchedOptimizedRows = 0;
                    int changedRowCount = 0;
                    var changedSectorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var changedSectorDetails = new List<CoverageChangedSectorDetail>();

                    await using var cmdCompare = conn.CreateCommand();
                    cmdCompare.CommandText = @"
                        SELECT
                            sp.id AS baseline_id,
                            sp.site AS baseline_site,
                            sp.sector AS baseline_sector,
                            sp.cell_id AS baseline_cell_id,
                            sp.latitude AS baseline_latitude,
                            sp.longitude AS baseline_longitude,
                            sp.tac AS baseline_tac,
                            sp.pci AS baseline_pci,
                            sp.azimuth AS baseline_azimuth,
                            sp.height AS baseline_height,
                            sp.bw AS baseline_bw,
                            sp.m_tilt AS baseline_m_tilt,
                            sp.e_tilt AS baseline_e_tilt,
                            sp.tx_power AS baseline_maximum_transmission_power_of_resource,
                            sp.real_transmit_power_of_resource AS baseline_real_transmit_power_of_resource,
                            sp.reference_signal_power AS baseline_reference_signal_power,
                            sp.cellsize AS baseline_cellsize,
                            sp.frequency AS baseline_frequency,
                            sp.band AS baseline_band,
                            sp.uplink_center_frequency AS baseline_uplink_center_frequency,
                            sp.downlink_frequency AS baseline_downlink_frequency,
                            sp.earfcn AS baseline_earfcn,
                            sp.cluster AS baseline_cluster,
                            sp.Technology AS baseline_Technology,
                            spo.id AS optimized_id,
                            spo.site AS optimized_site,
                            spo.sector AS optimized_sector,
                            spo.cell_id AS optimized_cell_id,
                            spo.latitude AS optimized_latitude,
                            spo.longitude AS optimized_longitude,
                            spo.tac AS optimized_tac,
                            spo.pci AS optimized_pci,
                            spo.azimuth AS optimized_azimuth,
                            spo.height AS optimized_height,
                            spo.bw AS optimized_bw,
                            spo.m_tilt AS optimized_m_tilt,
                            spo.e_tilt AS optimized_e_tilt,
                            spo.tx_power AS optimized_maximum_transmission_power_of_resource,
                            spo.real_transmit_power_of_resource AS optimized_real_transmit_power_of_resource,
                            spo.reference_signal_power AS optimized_reference_signal_power,
                            spo.cellsize AS optimized_cellsize,
                            spo.frequency AS optimized_frequency,
                            spo.band AS optimized_band,
                            spo.uplink_center_frequency AS optimized_uplink_center_frequency,
                            spo.downlink_frequency AS optimized_downlink_frequency,
                            spo.earfcn AS optimized_earfcn,
                            spo.cluster AS optimized_cluster,
                            spo.Technology AS optimized_Technology
                        FROM site_prediction sp
                        LEFT JOIN site_prediction_optimized spo
                            ON spo.id = (
                                SELECT o.id
                                FROM site_prediction_optimized o
                                WHERE o.tbl_project_id = sp.tbl_project_id
                                  AND (
                                    o.site_prediction_id = sp.id
                                    OR (
                                        (o.site_prediction_id IS NULL OR o.site_prediction_id = 0 OR o.site_prediction_id = o.tbl_project_id)
                                        AND (
                                            (
                                                o.cell_id IS NOT NULL
                                                AND sp.cell_id IS NOT NULL
                                                AND CONVERT(o.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                                    CONVERT(sp.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci
                                            )
                                            OR (
                                                o.site IS NOT NULL
                                                AND sp.site IS NOT NULL
                                                AND CONVERT(o.site USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                                    CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci
                                                AND (
                                                    o.sector IS NULL
                                                    OR sp.sector IS NULL
                                                    OR (
                                                        CONVERT(o.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci =
                                                        CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci
                                                    )
                                                )
                                            )
                                        )
                                    )
                                  )
                                ORDER BY
                                    CASE WHEN o.site_prediction_id = sp.id THEN 0 ELSE 1 END,
                                    o.id DESC
                                LIMIT 1
                            )
                        WHERE sp.tbl_project_id = @pid
                        ORDER BY sp.id DESC;";
                    AddParam(cmdCompare, "@pid", projectId);

                    await using (var reader = await cmdCompare.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var optimizedId = ReadReaderValue(reader, "optimized_id");
                            if (optimizedId == null) continue;
                            matchedOptimizedRows += 1;

                            var changedFields = new List<string>();
                            foreach (var field in compareFields)
                            {
                                var baselineValue = ReadReaderValue(reader, $"baseline_{field}");
                                var optimizedValue = ReadReaderValue(reader, $"optimized_{field}");
                                if (!AreEquivalentDbValues(baselineValue, optimizedValue))
                                {
                                    changedFields.Add(field);
                                    fieldChangeCounts[field] = fieldChangeCounts.TryGetValue(field, out var current)
                                        ? current + 1
                                        : 1;
                                }
                            }

                            if (changedFields.Count == 0) continue;
                            changedRowCount += 1;

                            var site = Convert.ToString(ReadReaderValue(reader, "optimized_site") ?? ReadReaderValue(reader, "baseline_site")) ?? "";
                            var sector = Convert.ToString(ReadReaderValue(reader, "optimized_sector") ?? ReadReaderValue(reader, "baseline_sector")) ?? "";
                            var cellId = Convert.ToString(ReadReaderValue(reader, "optimized_cell_id") ?? ReadReaderValue(reader, "baseline_cell_id")) ?? "";
                            var key = $"{site}|{sector}|{cellId}";
                            if (!string.IsNullOrWhiteSpace(site) || !string.IsNullOrWhiteSpace(sector) || !string.IsNullOrWhiteSpace(cellId))
                            {
                                changedSectorKeys.Add(key);
                            }

                            if (changedSectorDetails.Count < 250)
                            {
                                changedSectorDetails.Add(new CoverageChangedSectorDetail
                                {
                                    baseline_id = ToNullableLong(ReadReaderValue(reader, "baseline_id")),
                                    optimized_id = ToNullableLong(optimizedId),
                                    site = site,
                                    sector = sector,
                                    cell_id = cellId,
                                    changed_fields = changedFields
                                });
                            }
                        }
                    }

                    var summary = new CoverageOptimizationSummaryData
                    {
                        project_id = projectId,
                        baseline_total_rows = baselineTotalRows,
                        optimized_total_rows = optimizedTotalRows,
                        matched_optimized_rows = matchedOptimizedRows,
                        changed_row_count = changedRowCount,
                        unchanged_row_count = Math.Max(0, matchedOptimizedRows - changedRowCount),
                        changed_sector_count = changedSectorKeys.Count,
                        field_changes = fieldChangeCounts
                            .Where(kv => kv.Value > 0)
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => new CoverageFieldChangeCount
                            {
                                field = kv.Key,
                                count = kv.Value
                            })
                            .ToList(),
                        changed_sectors = changedSectorDetails
                    };

                    sw.Stop();
                    return Ok(new CoverageOptimizationSummaryResponse
                    {
                        Status = 1,
                        Message = "Coverage optimization summary fetched successfully.",
                        Data = summary
                    });
                }
                finally
                {
                    if (shouldClose && conn.State == ConnectionState.Open)
                        await conn.CloseAsync();
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return StatusCode(500, new { Status = 0, Message = "Error: " + SafeException.Get(ex) });
            }
        }


        // =====================================================================
        // HELPERS
        // =====================================================================
        private static object? ReadReaderValue(DbDataReader reader, string columnName)
        {
            int ordinal;
            try
            {
                ordinal = reader.GetOrdinal(columnName);
            }
            catch
            {
                return null;
            }

            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        private static long? ToNullableLong(object? value)
        {
            if (value == null || value == DBNull.Value) return null;
            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static bool AreEquivalentDbValues(object? left, object? right)
        {
            if (left == null || left == DBNull.Value)
            {
                if (right == null || right == DBNull.Value) return true;
                var rs = Convert.ToString(right, CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(rs);
            }
            if (right == null || right == DBNull.Value)
            {
                var ls = Convert.ToString(left, CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(ls);
            }

            if (TryToDouble(left, out var ld) && TryToDouble(right, out var rd))
            {
                return Math.Abs(ld - rd) <= 0.000001;
            }

            var leftString = Convert.ToString(left, CultureInfo.InvariantCulture)?.Trim() ?? "";
            var rightString = Convert.ToString(right, CultureInfo.InvariantCulture)?.Trim() ?? "";
            return string.Equals(leftString, rightString, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryToDouble(object? value, out double result)
        {
            result = 0;
            if (value == null || value == DBNull.Value) return false;

            switch (value)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case short s:
                    result = s;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case string str:
                    return double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out result)
                        || double.TryParse(str, NumberStyles.Any, CultureInfo.CurrentCulture, out result);
                default:
                    try
                    {
                        result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
            }
        }

        private static List<(double Lat, double Lon)> ParsePolygonWkt(string wkt)
        {
            var pts = new List<(double Lat, double Lon)>();
            var m = Regex.Match(wkt, @"\(\((.+?)\)\)", RegexOptions.Singleline);
            if (!m.Success) return pts;
            foreach (var pair in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = pair.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2
                    && double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lon)
                    && double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat))
                    pts.Add((lat, lon));
            }
            return pts;
        }

        private static (Dictionary<string, GridCell> cells, double gLat, double gLon, double minLat, double minLon)
            GenerateGrid(List<(double Lat, double Lon)> poly, double sizeMeters)
        {
            double minLat = poly.Min(p => p.Lat), maxLat = poly.Max(p => p.Lat);
            double minLon = poly.Min(p => p.Lon), maxLon = poly.Max(p => p.Lon);
            double centerLat = (minLat + maxLat) / 2.0;

            double gLat = sizeMeters / METERS_PER_DEGREE_LAT;
            double gLon = sizeMeters / (METERS_PER_DEGREE_LAT * Math.Cos(centerLat * Math.PI / 180.0));

            var cells = new Dictionary<string, GridCell>();
            int rows = (int)Math.Ceiling((maxLat - minLat) / gLat);
            int cols = (int)Math.Ceiling((maxLon - minLon) / gLon);

            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double cMinLat = minLat + r * gLat, cMaxLat = cMinLat + gLat;
                double cMinLon = minLon + c * gLon, cMaxLon = cMinLon + gLon;
                double cLat = (cMinLat + cMaxLat) / 2.0, cLon = (cMinLon + cMaxLon) / 2.0;

                if (PointInPolygon(cLat, cLon, poly))
                {
                    string key = $"R{r}C{c}";
                    cells[key] = new GridCell
                    {
                        Key = key, GridId = key, Row = r, Col = c,
                        MinLat = Math.Round(cMinLat, 8), MaxLat = Math.Round(cMaxLat, 8),
                        MinLon = Math.Round(cMinLon, 8), MaxLon = Math.Round(cMaxLon, 8),
                        CenterLat = Math.Round(cLat, 8), CenterLon = Math.Round(cLon, 8)
                    };
                }
            }
            return (cells, gLat, gLon, minLat, minLon);
        }

        private static bool PointInPolygon(double lat, double lon, List<(double Lat, double Lon)> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double yi = poly[i].Lat, xi = poly[i].Lon;
                double yj = poly[j].Lat, xj = poly[j].Lon;
                if (((yi > lat) != (yj > lat)) && (lon < (xj - xi) * (lat - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        [HttpGet("GetOptimizationScenarios")]
        public async Task<IActionResult> GetOptimizationScenarios(
            [FromQuery] int projectId,
            [FromQuery] int? company_id = null)
        {
            if (!await CanUseGridFeatureAsync())
                return StatusCode(403, new { Status = 0, Message = "Feature disabled in license: grid_fetch", Code = "FEATURE_NOT_ENABLED" });

            int targetCompanyId = _userScope.GetTargetCompanyId(User, company_id);
            bool isSuperAdmin = _userScope.IsSuperAdmin(User);
            if (!isSuperAdmin && targetCompanyId == 0)
                return Unauthorized(new { Status = 0, Message = "Unauthorized. Unable to resolve company context." });

            if (projectId <= 0)
                return BadRequest(new { Status = 0, Message = "projectId is required." });

            if (targetCompanyId > 0)
            {
                bool access = await _db.tbl_project.AnyAsync(p => p.id == projectId && p.company_id == targetCompanyId);
                if (!access)
                    return Unauthorized(new { Status = 0, Message = "Project does not belong to your company." });
            }

            try
            {
                var conn = _db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                    await conn.OpenAsync();

                var optimizedColumns = await GetTableColumnSetAsync(conn, "lte_prediction_optimised_results");
                var hasOptimizedPublicScenarioId = optimizedColumns.Contains("public_scenario_id");
                var optimizedScenarioExpr = hasOptimizedPublicScenarioId
                    ? "public_scenario_id"
                    : "scenario_id";
                var optimizedInternalScenarioExpr = hasOptimizedPublicScenarioId
                    ? "scenario_id"
                    : "NULL";

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT
                        opt.public_scenario_id AS scenario_id,
                        opt.public_scenario_id,
                        MAX(internal_scenario_id) AS internal_scenario_id,
                        COALESCE(
                            MAX(CASE WHEN meta.scenario_name IS NOT NULL AND meta.scenario_name <> '' THEN meta.scenario_name END),
                            CONCAT('Scenario ', opt.public_scenario_id)
                        ) AS scenario_name,
                        COALESCE(
                            MAX(CASE WHEN meta.status IS NOT NULL AND meta.status <> '' THEN meta.status END),
                            'stored'
                        ) AS status,
                        MAX(COALESCE(meta.created_at, opt.created_at)) AS created_at
                    FROM (
                        SELECT
                            {optimizedScenarioExpr} AS public_scenario_id,
                            {optimizedInternalScenarioExpr} AS internal_scenario_id,
                            created_at
                        FROM lte_prediction_optimised_results
                        WHERE project_id = @pid
                          AND {optimizedScenarioExpr} IS NOT NULL
                          AND {optimizedScenarioExpr} > 0
                    ) opt
                    LEFT JOIN lte_optimization_scenarios meta
                      ON meta.project_id = @pid
                     AND meta.scenario_id = opt.public_scenario_id
                    GROUP BY opt.public_scenario_id
                    ORDER BY opt.public_scenario_id ASC;";
                AddParam(cmd, "@pid", projectId);

                var rows = new List<object>();
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    var scenarioId = rdr.IsDBNull(0) ? 0 : Convert.ToInt32(rdr.GetValue(0));
                    var publicScenarioId = rdr.IsDBNull(1) ? scenarioId : Convert.ToInt32(rdr.GetValue(1));
                    var internalScenarioId = rdr.IsDBNull(2) ? (int?)null : Convert.ToInt32(rdr.GetValue(2));
                    var scenarioName = rdr.IsDBNull(3) ? "" : Convert.ToString(rdr.GetValue(3)) ?? "";
                    var status = rdr.IsDBNull(4) ? "" : Convert.ToString(rdr.GetValue(4)) ?? "";
                    var createdAt = rdr.IsDBNull(5) ? (DateTime?)null : Convert.ToDateTime(rdr.GetValue(5));
                    rows.Add(new
                    {
                        scenario_id = publicScenarioId,
                        public_scenario_id = publicScenarioId,
                        internal_scenario_id = internalScenarioId,
                        scenario_name = scenarioName,
                        status,
                        created_at = createdAt
                    });
                }

                return Ok(new
                {
                    Status = 1,
                    Message = "Optimization scenarios fetched.",
                    Data = rows
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = 0, Message = "Error: " + SafeException.Get(ex) });
            }
        }

        private static async Task<HashSet<string>> GetTableColumnSetAsync(DbConnection conn, string tableName)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @tableName;";
            AddParam(cmd, "@tableName", tableName);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
            }
            return columns;
        }

        private async Task<int?> ResolveScenarioIdAsync(DbConnection conn, int projectId, int? scenarioId)
        {
            if (scenarioId.HasValue && scenarioId.Value > 0)
                return scenarioId.Value;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT scenario_id
                FROM lte_optimization_scenarios
                WHERE project_id = @pid
                ORDER BY scenario_id DESC, id DESC
                LIMIT 1;";
            AddParam(cmd, "@pid", projectId);
            var obj = await cmd.ExecuteScalarAsync();
            if (obj == null || obj == DBNull.Value) return null;
            var parsed = Convert.ToInt32(obj);
            return parsed > 0 ? parsed : null;
        }

        private async Task<List<PredPoint>> FetchPredictionData(DbConnection conn, string table, int projectId, int? scenarioId = null)
        {
            var pts = new List<PredPoint>();
            await using var cmd = conn.CreateCommand();
            var isOptimizedTable = string.Equals(table, "lte_prediction_optimised_results", StringComparison.OrdinalIgnoreCase);
            var scenarioFilterColumn = "scenario_id";
            if (isOptimizedTable)
            {
                var columns = await GetTableColumnSetAsync(conn, table);
                if (columns.Contains("public_scenario_id"))
                {
                    scenarioFilterColumn = "public_scenario_id";
                }
            }
            var scenarioFilterClause = isOptimizedTable && scenarioId.HasValue
                ? $" AND `{scenarioFilterColumn}` = @sid"
                : "";
            var sourceQuery = $@"
SELECT
    lat,
    lon,
    pred_rsrp,
    pred_rsrq,
    pred_sinr,
    node_b_id,
    cell_id,
    site_id,
    nodeb_id_cell_id,
    operator,
    created_at
FROM `{table}`
WHERE project_id = @pid{scenarioFilterClause}";

            if (isOptimizedTable)
            {
                cmd.CommandText = $@"
WITH optimized_source AS (
    {sourceQuery}
),
optimized_ranked AS (
    SELECT
        optimized_source.*,
        ROW_NUMBER() OVER (
            PARTITION BY
                COALESCE(nodeb_id_cell_id, ''),
                COALESCE(node_b_id, ''),
                COALESCE(cell_id, ''),
                COALESCE(site_id, ''),
                lat,
                lon
            ORDER BY COALESCE(created_at, TIMESTAMP('1970-01-01 00:00:00')) DESC
        ) AS rn
    FROM optimized_source
)
SELECT
    lat,
    lon,
    pred_rsrp,
    pred_rsrq,
    pred_sinr,
    node_b_id,
    cell_id,
    site_id,
    nodeb_id_cell_id,
    operator
FROM optimized_ranked
WHERE rn = 1";
            }
            else
            {
                cmd.CommandText = sourceQuery;
            }
            AddParam(cmd, "@pid", projectId);
            if (isOptimizedTable && scenarioId.HasValue)
                AddParam(cmd, "@sid", scenarioId.Value);

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (rdr.IsDBNull(0) || rdr.IsDBNull(1))
                    continue;

                double lat;
                double lon;
                try
                {
                    lat = Convert.ToDouble(rdr.GetValue(0), CultureInfo.InvariantCulture);
                    lon = Convert.ToDouble(rdr.GetValue(1), CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                if (!IsValidLatLon(lat, lon))
                    continue;

                pts.Add(new PredPoint
                {
                    Lat = lat,
                    Lon = lon,
                    Rsrp = rdr.IsDBNull(2) ? null : Convert.ToDouble(rdr.GetValue(2)),
                    Rsrq = rdr.IsDBNull(3) ? null : Convert.ToDouble(rdr.GetValue(3)),
                    Sinr = rdr.IsDBNull(4) ? null : Convert.ToDouble(rdr.GetValue(4)),
                    NodeBId = rdr.IsDBNull(5) ? null : rdr.GetValue(5)?.ToString(),
                    CellId = rdr.IsDBNull(6) ? null : rdr.GetValue(6)?.ToString(),
                    SiteId = rdr.IsDBNull(7) ? null : rdr.GetValue(7)?.ToString(),
                    NodebIdCellId = rdr.IsDBNull(8) ? null : rdr.GetValue(8)?.ToString(),
                    Operator = rdr.IsDBNull(9) ? null : rdr.GetValue(9)?.ToString(),
                });
            }

            return pts;
        }

        private async Task<OptimizedSelectionKeys> FetchOptimizedSelectionKeys(
            DbConnection conn,
            int projectId)
        {
            var keys = new OptimizedSelectionKeys();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    COALESCE(
        NULLIF(TRIM(CAST(spo.site AS CHAR)), ''),
        NULLIF(TRIM(CAST(sp.site AS CHAR)), '')
    ) AS selected_site,
    COALESCE(
        NULLIF(TRIM(spo.cell_id), ''),
        NULLIF(TRIM(sp.cell_id), '')
    ) AS selected_cell_id,
    spo.status AS optimized_status
FROM site_prediction_optimized spo
LEFT JOIN site_prediction sp
    ON sp.id = spo.site_prediction_id
WHERE spo.tbl_project_id = @pid;";
            AddParam(cmd, "@pid", projectId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var statusRaw = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
                if (!IsOptimizedStatus(statusRaw))
                    continue;

                AddComparableKey(
                    keys.SiteIds,
                    reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString());
                AddComparableKey(
                    keys.CellIds,
                    reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString());
            }

            return keys;
        }

        private static List<PredPoint> BuildStatusAwareOptimizedPoints(
            List<PredPoint> baselineRows,
            List<PredPoint> optimizedRows,
            OptimizedSelectionKeys selectionKeys)
        {
            if (baselineRows.Count == 0 && optimizedRows.Count == 0)
                return new List<PredPoint>();

            // If no optimized badges/status rows are available, optimized should behave
            // as baseline (no changed sectors to substitute).
            if (!selectionKeys.HasAny)
                return baselineRows.ToList();

            var selectedOptimizedRows = optimizedRows
                .Where(row => IsRowInOptimizedSelection(row, selectionKeys))
                .ToList();

            // Keep baseline when optimized table doesn't have corresponding prediction points.
            if (selectedOptimizedRows.Count == 0)
                return baselineRows.ToList();

            var availableOptimizedKeys = BuildSelectionKeysFromPredictionPoints(selectedOptimizedRows);

            var baselineRowsToKeep = baselineRows
                .Where(row =>
                    !IsRowCoveredByOptimizedPrediction(row, selectionKeys, availableOptimizedKeys))
                .ToList();

            var merged = new List<PredPoint>(baselineRowsToKeep.Count + selectedOptimizedRows.Count);
            merged.AddRange(baselineRowsToKeep);
            merged.AddRange(selectedOptimizedRows);
            return merged;
        }

        private static bool IsRowCoveredByOptimizedPrediction(
            PredPoint row,
            OptimizedSelectionKeys selectionKeys,
            OptimizedSelectionKeys availableOptimizedKeys)
        {
            if (!IsRowInOptimizedSelection(row, selectionKeys))
                return false;

            var cellId = NormalizeComparableKey(row.CellId);
            if (cellId.Length > 0 && availableOptimizedKeys.CellIds.Contains(cellId))
                return true;

            var siteId = NormalizeComparableKey(row.SiteId);
            if (siteId.Length > 0 && availableOptimizedKeys.SiteIds.Contains(siteId))
                return true;

            var nodeBId = NormalizeComparableKey(row.NodeBId);
            if (nodeBId.Length > 0 && availableOptimizedKeys.SiteIds.Contains(nodeBId))
                return true;

            return false;
        }

        private static bool IsRowInOptimizedSelection(PredPoint row, OptimizedSelectionKeys selectionKeys)
        {
            var cellId = NormalizeComparableKey(row.CellId);
            if (cellId.Length > 0 && selectionKeys.CellIds.Contains(cellId))
                return true;

            var siteId = NormalizeComparableKey(row.SiteId);
            if (siteId.Length > 0 && selectionKeys.SiteIds.Contains(siteId))
                return true;

            var nodeBId = NormalizeComparableKey(row.NodeBId);
            if (nodeBId.Length > 0 && selectionKeys.SiteIds.Contains(nodeBId))
                return true;

            return false;
        }

        private static OptimizedSelectionKeys BuildSelectionKeysFromPredictionPoints(
            IEnumerable<PredPoint> rows)
        {
            var keys = new OptimizedSelectionKeys();

            foreach (var row in rows)
            {
                AddComparableKey(keys.CellIds, row.CellId);
                AddComparableKey(keys.SiteIds, row.SiteId);
                AddComparableKey(keys.SiteIds, row.NodeBId);
            }

            return keys;
        }

        private static bool IsOptimizedStatus(string? statusRaw)
        {
            var status = string.IsNullOrWhiteSpace(statusRaw)
                ? string.Empty
                : statusRaw.Trim().ToLowerInvariant();

            if (status.Length == 0)
                return true;

            if (status.Contains("baseline"))
                return false;
            if (status.Contains("original"))
                return false;
            if (status.Contains("revert"))
                return false;
            if (status.Contains("delete"))
                return false;

            return true;
        }

        private static void AddComparableKey(HashSet<string> target, string? value)
        {
            var normalized = NormalizeComparableKey(value);
            if (normalized.Length > 0)
                target.Add(normalized);
        }

        private static string NormalizeComparableKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asLong))
                return asLong.ToString(CultureInfo.InvariantCulture);

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble))
            {
                if (Math.Abs(asDouble - Math.Round(asDouble)) < 0.0000001)
                    return Math.Round(asDouble).ToString(CultureInfo.InvariantCulture);

                return asDouble.ToString("0.########", CultureInfo.InvariantCulture);
            }

            return trimmed.ToLowerInvariant();
        }

        private static List<(double Lat, double Lon)> BuildBoundingPolygonFromPoints(List<PredPoint> points)
        {
            var valid = (points ?? new List<PredPoint>())
                .Where(p => IsValidLatLon(p.Lat, p.Lon))
                .ToList();

            if (valid.Count == 0) return new List<(double Lat, double Lon)>();

            double minLat = valid.Min(p => p.Lat);
            double maxLat = valid.Max(p => p.Lat);
            double minLon = valid.Min(p => p.Lon);
            double maxLon = valid.Max(p => p.Lon);

            // Avoid zero-area polygons for edge cases where all points have same lat/lon.
            if (Math.Abs(maxLat - minLat) < 1e-9)
            {
                minLat -= 1e-6;
                maxLat += 1e-6;
            }
            if (Math.Abs(maxLon - minLon) < 1e-9)
            {
                minLon -= 1e-6;
                maxLon += 1e-6;
            }

            return new List<(double Lat, double Lon)>
            {
                (minLat, minLon),
                (minLat, maxLon),
                (maxLat, maxLon),
                (maxLat, minLon),
                (minLat, minLon),
            };
        }

        private static List<(double Lat, double Lon)> BuildBoundingPolygonFromVertices(
            List<(double Lat, double Lon)> vertices)
        {
            var valid = (vertices ?? new List<(double Lat, double Lon)>())
                .Where(v => IsValidLatLon(v.Lat, v.Lon))
                .ToList();

            if (valid.Count == 0) return new List<(double Lat, double Lon)>();

            double minLat = valid.Min(v => v.Lat);
            double maxLat = valid.Max(v => v.Lat);
            double minLon = valid.Min(v => v.Lon);
            double maxLon = valid.Max(v => v.Lon);

            if (Math.Abs(maxLat - minLat) < 1e-9)
            {
                minLat -= 1e-6;
                maxLat += 1e-6;
            }
            if (Math.Abs(maxLon - minLon) < 1e-9)
            {
                minLon -= 1e-6;
                maxLon += 1e-6;
            }

            return new List<(double Lat, double Lon)>
            {
                (minLat, minLon),
                (minLat, maxLon),
                (maxLat, maxLon),
                (maxLat, minLon),
                (minLat, minLon),
            };
        }

        private static bool IsValidLatLon(double lat, double lon)
        {
            if (double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon))
                return false;

            return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }

        private static Dictionary<string, List<PredPoint>> MapPointsToGrids(
            List<PredPoint> pts, double minLat, double minLon,
            double gLat, double gLon, Dictionary<string, GridCell> valid)
        {
            var dict = new Dictionary<string, List<PredPoint>>();
            foreach (var pt in pts)
            {
                int row = (int)((pt.Lat - minLat) / gLat);
                int col = (int)((pt.Lon - minLon) / gLon);
                string key = $"R{row}C{col}";
                if (!valid.ContainsKey(key)) continue;
                if (!dict.ContainsKey(key)) dict[key] = new List<PredPoint>();
                dict[key].Add(pt);
            }
            return dict;
        }

        private static GridMetrics ComputeMetrics(List<PredPoint> pts)
        {
            if (pts == null || pts.Count == 0) return new GridMetrics { point_count = 0 };
            var rp = pts.Where(p => p.Rsrp.HasValue).Select(p => p.Rsrp!.Value).ToList();
            var rq = pts.Where(p => p.Rsrq.HasValue).Select(p => p.Rsrq!.Value).ToList();
            var sn = pts.Where(p => p.Sinr.HasValue).Select(p => p.Sinr!.Value).ToList();
            return new GridMetrics
            {
                point_count = pts.Count,
                avg_rsrp = Avg(rp), avg_rsrq = Avg(rq), avg_sinr = Avg(sn),
                median_rsrp = Median(rp), median_rsrq = Median(rq), median_sinr = Median(sn),
                min_rsrp = Min(rp), min_rsrq = Min(rq), min_sinr = Min(sn),
                max_rsrp = Max(rp), max_rsrq = Max(rq), max_sinr = Max(sn),
                mode_rsrp = Mode(rp), mode_rsrq = Mode(rq), mode_sinr = Mode(sn)
            };
        }

        private static BestOperatorMetrics ComputeBestOperators(List<PredPoint> pts)
        {
            if (pts == null || pts.Count == 0) return new BestOperatorMetrics();

            var perOperator = pts
                .Where(p => p.Rsrp.HasValue && !string.IsNullOrWhiteSpace(p.Operator))
                .GroupBy(p => p.Operator!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var rsrpValues = g.Select(p => p.Rsrp!.Value).ToList();
                    return new
                    {
                        Operator = g.Key,
                        Avg = Avg(rsrpValues),
                        Min = Min(rsrpValues),
                        Max = Max(rsrpValues)
                    };
                })
                .ToList();

            return new BestOperatorMetrics
            {
                best_operator_avg = perOperator
                    .Where(x => x.Avg.HasValue)
                    .OrderByDescending(x => x.Avg!.Value)
                    .ThenBy(x => x.Operator, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.Operator,
                best_operator_min = perOperator
                    .Where(x => x.Min.HasValue)
                    .OrderByDescending(x => x.Min!.Value)
                    .ThenBy(x => x.Operator, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.Operator,
                best_operator_max = perOperator
                    .Where(x => x.Max.HasValue)
                    .OrderByDescending(x => x.Max!.Value)
                    .ThenBy(x => x.Operator, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.Operator,
            };
        }

        private static GridDifference ComputeDiff(GridMetrics b, GridMetrics o)
        {
            return new GridDifference
            {
                diff_avg_rsrp = D(o.avg_rsrp, b.avg_rsrp), diff_avg_rsrq = D(o.avg_rsrq, b.avg_rsrq), diff_avg_sinr = D(o.avg_sinr, b.avg_sinr),
                diff_median_rsrp = D(o.median_rsrp, b.median_rsrp), diff_median_rsrq = D(o.median_rsrq, b.median_rsrq), diff_median_sinr = D(o.median_sinr, b.median_sinr),
                diff_min_rsrp = D(o.min_rsrp, b.min_rsrp), diff_min_rsrq = D(o.min_rsrq, b.min_rsrq), diff_min_sinr = D(o.min_sinr, b.min_sinr),
                diff_max_rsrp = D(o.max_rsrp, b.max_rsrp), diff_max_rsrq = D(o.max_rsrq, b.max_rsrq), diff_max_sinr = D(o.max_sinr, b.max_sinr),
                diff_mode_rsrp = D(o.mode_rsrp, b.mode_rsrp), diff_mode_rsrq = D(o.mode_rsrq, b.mode_rsrq), diff_mode_sinr = D(o.mode_sinr, b.mode_sinr)
            };
        }

        private static List<GridCellResult> ConvertToGridCellResults(List<grid_analytics_results> stored)
        {
            var res = new List<GridCellResult>();
            foreach (var s in stored)
            {
                res.Add(new GridCellResult
                {
                    grid_id = s.grid_id,
                    center_lat = s.center_lat,
                    center_lon = s.center_lon,
                    min_lat = s.min_lat,
                    max_lat = s.max_lat,
                    min_lon = s.min_lon,
                    max_lon = s.max_lon,
                    baseline = new GridMetrics
                    {
                        point_count = s.baseline_point_count,
                        avg_rsrp = s.baseline_avg_rsrp, avg_rsrq = s.baseline_avg_rsrq, avg_sinr = s.baseline_avg_sinr,
                        median_rsrp = s.baseline_median_rsrp, median_rsrq = s.baseline_median_rsrq, median_sinr = s.baseline_median_sinr,
                        min_rsrp = s.baseline_min_rsrp, min_rsrq = s.baseline_min_rsrq, min_sinr = s.baseline_min_sinr,
                        max_rsrp = s.baseline_max_rsrp, max_rsrq = s.baseline_max_rsrq, max_sinr = s.baseline_max_sinr,
                        mode_rsrp = s.baseline_mode_rsrp, mode_rsrq = s.baseline_mode_rsrq, mode_sinr = s.baseline_mode_sinr,
                        best_operator_avg = s.baseline_best_operator_avg,
                        best_operator_min = s.baseline_best_operator_min,
                        best_operator_max = s.baseline_best_operator_max,
                    },
                    optimized = new GridMetrics
                    {
                        point_count = s.optimized_point_count,
                        avg_rsrp = s.optimized_avg_rsrp, avg_rsrq = s.optimized_avg_rsrq, avg_sinr = s.optimized_avg_sinr,
                        median_rsrp = s.optimized_median_rsrp, median_rsrq = s.optimized_median_rsrq, median_sinr = s.optimized_median_sinr,
                        min_rsrp = s.optimized_min_rsrp, min_rsrq = s.optimized_min_rsrq, min_sinr = s.optimized_min_sinr,
                        max_rsrp = s.optimized_max_rsrp, max_rsrq = s.optimized_max_rsrq, max_sinr = s.optimized_max_sinr,
                        mode_rsrp = s.optimized_mode_rsrp, mode_rsrq = s.optimized_mode_rsrq, mode_sinr = s.optimized_mode_sinr,
                        best_operator_avg = s.optimized_best_operator_avg,
                        best_operator_min = s.optimized_best_operator_min,
                        best_operator_max = s.optimized_best_operator_max,
                    },
                    difference = new GridDifference
                    {
                        diff_avg_rsrp = s.diff_avg_rsrp, diff_avg_rsrq = s.diff_avg_rsrq, diff_avg_sinr = s.diff_avg_sinr,
                        diff_median_rsrp = s.diff_median_rsrp, diff_median_rsrq = s.diff_median_rsrq, diff_median_sinr = s.diff_median_sinr,
                        diff_min_rsrp = s.diff_min_rsrp, diff_min_rsrq = s.diff_min_rsrq, diff_min_sinr = s.diff_min_sinr,
                        diff_max_rsrp = s.diff_max_rsrp, diff_max_rsrq = s.diff_max_rsrq, diff_max_sinr = s.diff_max_sinr,
                        diff_mode_rsrp = s.diff_mode_rsrp, diff_mode_rsrq = s.diff_mode_rsrq, diff_mode_sinr = s.diff_mode_sinr,
                    }
                });
            }
            return res;
        }

        private static List<grid_analytics_results> SelectLatestGridGeneration(List<grid_analytics_results> stored)
        {
            if (stored.Count <= 1)
                return stored;

            return stored
                .GroupBy(s => new
                {
                    ScenarioId = s.public_scenario_id ?? s.scenario_id,
                    GridSize = Math.Round(s.grid_size_meters, 3)
                })
                .OrderByDescending(g => g.Max(s => s.created_at ?? DateTime.MinValue))
                .ThenByDescending(g => g.Count())
                .First()
                .OrderBy(s => s.grid_id)
                .ToList();
        }

        private static double? Avg(List<double> v)
        {
            if (v.Count == 0) return null;
            // For telecom dB/dBm values (RSRP, RSRQ, SINR), the physically accurate
            // mean requires converting the logarithmic values to linear scale,
            // averaging them, and converting back to logarithmic scale.
            double sumLinear = 0;
            foreach (var val in v)
            {
                sumLinear += Math.Pow(10, val / 10.0);
            }
            double avgLinear = sumLinear / v.Count;
            return Math.Round(10 * Math.Log10(avgLinear), 2);
        }
        private static double? Min(List<double> v) => v.Count > 0 ? Math.Round(v.Min(), 2) : null;
        private static double? Max(List<double> v) => v.Count > 0 ? Math.Round(v.Max(), 2) : null;
        private static double? D(double? a, double? b) => (a.HasValue && b.HasValue) ? Math.Round(a.Value - b.Value, 2) : null;

        private static double? Median(List<double> v)
        {
            if (v.Count == 0) return null;
            var s = v.OrderBy(x => x).ToList();
            int n = s.Count;
            return n % 2 == 0 ? Math.Round((s[n / 2 - 1] + s[n / 2]) / 2.0, 2) : Math.Round(s[n / 2], 2);
        }

        private static double? Mode(List<double> v)
        {
            if (v.Count == 0) return null;
            return v.GroupBy(x => Math.Round(x, 0))
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                    .First().Key;
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        // =====================================================================
        // DTOs
        // =====================================================================
        private sealed class OptimizedSelectionKeys
        {
            public HashSet<string> SiteIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CellIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool HasAny => SiteIds.Count > 0 || CellIds.Count > 0;
        }

        private class PredPoint
        {
            public double Lat { get; set; }
            public double Lon { get; set; }
            public double? Rsrp { get; set; }
            public double? Rsrq { get; set; }
            public double? Sinr { get; set; }
            public string? NodeBId { get; set; }
            public string? CellId { get; set; }
            public string? SiteId { get; set; }
            public string? NodebIdCellId { get; set; }
            public string? Operator { get; set; }
        }

        private class GridCell
        {
            public string Key { get; set; } = "";
            public string GridId { get; set; } = "";
            public int Row { get; set; }
            public int Col { get; set; }
            public double MinLat { get; set; }
            public double MaxLat { get; set; }
            public double MinLon { get; set; }
            public double MaxLon { get; set; }
            public double CenterLat { get; set; }
            public double CenterLon { get; set; }
        }

        public class GridAnalyticsResponse
        {
            public int Status { get; set; }
            public string Message { get; set; } = "";
            public GridAnalyticsData? Data { get; set; }
        }

        public class GridAnalyticsData
        {
            public int project_id { get; set; }
            public double grid_size_meters { get; set; }
            public int total_grids { get; set; }
            public int total_grids_with_data { get; set; }
            public int total_baseline_points { get; set; }
            public int total_optimized_points { get; set; }
            public List<GridCellResult> grids { get; set; } = new();
        }

        public class GridCellResult
        {
            public string grid_id { get; set; } = "";
            public double center_lat { get; set; }
            public double center_lon { get; set; }
            public double min_lat { get; set; }
            public double min_lon { get; set; }
            public double max_lat { get; set; }
            public double max_lon { get; set; }
            public GridMetrics baseline { get; set; } = new();
            public GridMetrics optimized { get; set; } = new();
            public GridDifference difference { get; set; } = new();
        }

        public class GridMetrics
        {
            public int point_count { get; set; }
            public double? avg_rsrp { get; set; }
            public double? avg_rsrq { get; set; }
            public double? avg_sinr { get; set; }
            public double? median_rsrp { get; set; }
            public double? median_rsrq { get; set; }
            public double? median_sinr { get; set; }
            public double? min_rsrp { get; set; }
            public double? min_rsrq { get; set; }
            public double? min_sinr { get; set; }
            public double? max_rsrp { get; set; }
            public double? max_rsrq { get; set; }
            public double? max_sinr { get; set; }
            public double? mode_rsrp { get; set; }
            public double? mode_rsrq { get; set; }
            public double? mode_sinr { get; set; }
            public string? best_operator_avg { get; set; }
            public string? best_operator_min { get; set; }
            public string? best_operator_max { get; set; }
        }

        public class GridDifference
        {
            public double? diff_avg_rsrp { get; set; }
            public double? diff_avg_rsrq { get; set; }
            public double? diff_avg_sinr { get; set; }
            public double? diff_median_rsrp { get; set; }
            public double? diff_median_rsrq { get; set; }
            public double? diff_median_sinr { get; set; }
            public double? diff_min_rsrp { get; set; }
            public double? diff_min_rsrq { get; set; }
            public double? diff_min_sinr { get; set; }
            public double? diff_max_rsrp { get; set; }
            public double? diff_max_rsrq { get; set; }
            public double? diff_max_sinr { get; set; }
            public double? diff_mode_rsrp { get; set; }
            public double? diff_mode_rsrq { get; set; }
            public double? diff_mode_sinr { get; set; }
        }

        private class BestOperatorMetrics
        {
            public string? best_operator_avg { get; set; }
            public string? best_operator_min { get; set; }
            public string? best_operator_max { get; set; }
        }

        public class CoverageOptimizationSummaryResponse
        {
            public int Status { get; set; }
            public string Message { get; set; } = "";
            public CoverageOptimizationSummaryData? Data { get; set; }
        }

        public class CoverageOptimizationSummaryData
        {
            public int project_id { get; set; }
            public int baseline_total_rows { get; set; }
            public int optimized_total_rows { get; set; }
            public int matched_optimized_rows { get; set; }
            public int changed_row_count { get; set; }
            public int unchanged_row_count { get; set; }
            public int changed_sector_count { get; set; }
            public List<CoverageFieldChangeCount> field_changes { get; set; } = new();
            public List<CoverageChangedSectorDetail> changed_sectors { get; set; } = new();
        }

        public class CoverageFieldChangeCount
        {
            public string field { get; set; } = "";
            public int count { get; set; }
        }

        public class CoverageChangedSectorDetail
        {
            public long? baseline_id { get; set; }
            public long? optimized_id { get; set; }
            public string site { get; set; } = "";
            public string sector { get; set; } = "";
            public string cell_id { get; set; } = "";
            public List<string> changed_fields { get; set; } = new();
        }
    }
}


