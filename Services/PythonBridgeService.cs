using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignalTracker.DTO.PythonBridge;
using SignalTracker.Helper;
using SignalTracker.Models;
using System.Diagnostics;

namespace SignalTracker.Services
{
    public class PythonBridgeService
    {
        private const int DefaultBatchSize = 2000;
        private const int BridgeReadCacheTtlSeconds = 180;

        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _configuration;
        private readonly RedisService _redisService;
        private readonly ILogger<PythonBridgeService> _logger;

        private sealed class BridgeRowsCacheEntry
        {
            public int Limit { get; set; }
            public int Offset { get; set; }
            public List<Dictionary<string, object?>> Rows { get; set; } = new();
        }

        public PythonBridgeService(
            ApplicationDbContext db,
            IConfiguration configuration,
            RedisService redisService,
            ILogger<PythonBridgeService> logger)
        {
            _db = db;
            _configuration = configuration;
            _redisService = redisService;
            _logger = logger;
        }

        private static string BuildCacheKey(string scope, params object?[] parts)
        {
            var normalized = parts
                .Select(p => p == null ? "null" : Convert.ToString(p)?.Trim()?.ToLowerInvariant() ?? "null");
            return $"pybridge:{scope}:{string.Join(":", normalized)}";
        }

        private async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetCachedOrLoadRowsAsync(
            string cacheKey,
            int limit,
            int offset,
            Func<Task<List<Dictionary<string, object?>>>> loader,
            CancellationToken cancellationToken)
        {
            if (_redisService.IsConnected)
            {
                var cached = await _redisService.GetObjectAsync<BridgeRowsCacheEntry>(cacheKey);
                if (cached != null)
                {
                    _logger.LogInformation("PythonBridge cache hit: {CacheKey} rows={RowCount}", cacheKey, cached.Rows.Count);
                    return (cached.Limit, cached.Offset, cached.Rows);
                }
            }

            var sw = Stopwatch.StartNew();
            var rows = await loader();
            sw.Stop();

            _logger.LogInformation("PythonBridge DB fetch: {CacheKey} rows={RowCount} elapsedMs={ElapsedMs}", cacheKey, rows.Count, sw.ElapsedMilliseconds);

            if (_redisService.IsConnected)
            {
                var cacheEntry = new BridgeRowsCacheEntry
                {
                    Limit = limit,
                    Offset = offset,
                    Rows = rows
                };
                await _redisService.SetObjectAsync(cacheKey, cacheEntry, BridgeReadCacheTtlSeconds);
            }

            return (limit, offset, rows);
        }

        public bool IsAuthorized(string? incomingKey)
        {
            var configuredKey =
                _configuration["PythonBridge:ApiKey"]
                ?? Environment.GetEnvironmentVariable("PYTHON_BRIDGE_API_KEY");

            if (string.IsNullOrWhiteSpace(configuredKey))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(incomingKey))
            {
                return false;
            }

            return string.Equals(
                configuredKey.Trim(),
                incomingKey.Trim(),
                StringComparison.Ordinal
            );
        }

        private static object? RowValue(IDictionary<string, object?> row, string key)
        {
            if (!row.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is JsonElement json)
            {
                return json.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Undefined => null,
                    JsonValueKind.Number when json.TryGetInt64(out var longValue) => longValue,
                    JsonValueKind.Number when json.TryGetDouble(out var doubleValue) => doubleValue,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => json.GetString(),
                    _ => json.ToString()
                };
            }

            return value;
        }

        private static DateTime? RowDate(IDictionary<string, object?> row, string key)
        {
            var value = RowValue(row, key);
            if (value == null || value == DBNull.Value)
            {
                return null;
            }
            if (value is DateTime dateTime)
            {
                return dateTime;
            }
            return DateTime.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetDriveTestRowsAsync(
            DriveTestRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var sessionIds = request.SessionIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (sessionIds.Count == 0)
            {
                throw new ArgumentException("No valid SessionIds provided.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);
            var operatorFilter = request.Operator?.Trim();
            var hasOperatorFilter = !string.IsNullOrWhiteSpace(operatorFilter);
            var primaryOnly = request.PrimaryOnly;

            var servingSql = @"
                SELECT
                    lat, lon, rsrp, rsrq, sinr, cell_id, nodeb_id, band, network, pci, earfcn,
                    m_alpha_long, m_alpha_short, `primary`, session_id
                FROM tbl_network_log
                WHERE session_id IN ({0})
                  {1}
                  {2}";

            var neighbourSql = @"
                SELECT
                    lat, lon, rsrp, rsrq, sinr, cell_id, nodeb_id, band, network, pci, earfcn,
                    m_alpha_long, m_alpha_short, `primary`,
                    session_id
                FROM tbl_network_log_neighbour
                WHERE session_id IN ({0})
                  {1}
                  {2}";

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            var inClause = PythonBridgeDbTool.BuildInClause(command, sessionIds, "sid");
            var operatorClause = hasOperatorFilter
                ? "AND LOWER(COALESCE(m_alpha_long, m_alpha_short)) = LOWER(@operator)"
                : string.Empty;
            var primaryClause = primaryOnly
                ? "AND LOWER(COALESCE(`primary`, '')) = 'yes'"
                : string.Empty;
            var servingQuery = string.Format(servingSql, inClause, operatorClause, primaryClause);
            var neighbourQuery = string.Format(neighbourSql, inClause, operatorClause, primaryClause);

            command.CommandText = request.IncludeNeighbour
                ? $"{servingQuery} UNION ALL {neighbourQuery} LIMIT @lim OFFSET @off;"
                : $"{servingQuery} LIMIT @lim OFFSET @off;";

            PythonBridgeDbTool.AddParam(command, "@lim", limit);
            PythonBridgeDbTool.AddParam(command, "@off", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);

            return (limit, offset, rows);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetLteTiltBaselineResultsAsync(
            LteTiltBaselineRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);
            var operatorFilter = request.Operator?.Trim();
            var hasOperatorFilter = !string.IsNullOrWhiteSpace(operatorFilter)
                && !string.Equals(operatorFilter, "all", StringComparison.OrdinalIgnoreCase);

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = $@"
                SELECT
                    node_b_id,
                    cell_id,
                    operator,
                    pred_rsrp,
                    pred_rsrq,
                    pred_sinr,
                    lat,
                    lon
                FROM lte_prediction_baseline_results
                WHERE project_id = @pid
                {(hasOperatorFilter ? "AND operator = @operator" : string.Empty)}
                ORDER BY id
                LIMIT @lim OFFSET @off;";

            PythonBridgeDbTool.AddParam(command, "@pid", request.ProjectId);
            if (hasOperatorFilter)
            {
                PythonBridgeDbTool.AddParam(command, "@operator", operatorFilter!);
            }
            PythonBridgeDbTool.AddParam(command, "@lim", limit);
            PythonBridgeDbTool.AddParam(command, "@off", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
            return (limit, offset, rows);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetLteTiltAntennaRowsAsync(
            LteTiltAntennaRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT *
                FROM site_prediction
                WHERE tbl_project_id = @pid
                ORDER BY id
                LIMIT @lim OFFSET @off;";

            PythonBridgeDbTool.AddParam(command, "@pid", request.ProjectId);
            PythonBridgeDbTool.AddParam(command, "@lim", limit);
            PythonBridgeDbTool.AddParam(command, "@off", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
            return (limit, offset, rows);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetLtePredictionGeoFeaturesAsync(
            LtePredictionGeoFeatureRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);
            var region = string.IsNullOrWhiteSpace(request.Region) ? "india" : request.Region.Trim().ToLowerInvariant();
            var cacheKey = BuildCacheKey("lte_geo", request.ProjectId, region, limit, offset);

            return await GetCachedOrLoadRowsAsync(
                cacheKey,
                limit,
                offset,
                async () =>
                {
                    var conn = _db.Database.GetDbConnection();
                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync(cancellationToken);
                    }

                    await using var command = conn.CreateCommand();
                    command.CommandText = @"
                SELECT
                    project_id,
                    region,
                    operator,
                    grid_id,
                    lat,
                    lon,
                    nodeb_id_cell_id,
                    proxy_site_id,
                    clutter_class,
                    morphology_cluster,
                    building_count,
                    building_area_ratio,
                    avg_building_area_m2,
                    road_length_m,
                    green_ratio,
                    water_ratio,
                    los_blocker_count,
                    los_blocked_ratio,
                    max_blocker_height_m,
                    diffraction_proxy_db,
                    nlos_flag,
                    terrain_elevation_m,
                    terrain_slope_deg,
                    proxy_site_elevation_m,
                    terrain_relief_to_site_m,
                    site_count_250m,
                    site_count_500m,
                    serving_distance_m,
                    nearest_site_distance_m,
                    mean_nearest3_site_distance_m,
                    azimuth_delta_deg,
                    polygon_alignment,
                    building_alignment,
                    geo_source
                FROM lte_prediction_geo_features
                WHERE project_id = @pid
                  AND region = @region
                ORDER BY nodeb_id_cell_id, lat, lon
                LIMIT @lim OFFSET @off;";

                    PythonBridgeDbTool.AddParam(command, "@pid", request.ProjectId);
                    PythonBridgeDbTool.AddParam(command, "@region", region);
                    PythonBridgeDbTool.AddParam(command, "@lim", limit);
                    PythonBridgeDbTool.AddParam(command, "@off", offset);

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
                },
                cancellationToken);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetLteSitePredictionRowsAsync(
            LteSitePredictionRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);
            var operatorFilter = request.Operator?.Trim();
            var hasOperatorFilter = !string.IsNullOrWhiteSpace(operatorFilter)
                && !string.Equals(operatorFilter, "all", StringComparison.OrdinalIgnoreCase);
            var cacheKey = BuildCacheKey("lte_site_pred", request.ProjectId, operatorFilter ?? "all", limit, offset);

            return await GetCachedOrLoadRowsAsync(
                cacheKey,
                limit,
                offset,
                async () =>
                {
                    var conn = _db.Database.GetDbConnection();
                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync(cancellationToken);
                    }

                    await using var command = conn.CreateCommand();
                    command.CommandText = $@"
                SELECT *
                FROM site_prediction
                WHERE tbl_project_id = @pid
                {(hasOperatorFilter ? "AND LOWER(cluster) = LOWER(@operator)" : string.Empty)}
                ORDER BY id
                LIMIT @lim OFFSET @off;";

                    PythonBridgeDbTool.AddParam(command, "@pid", request.ProjectId);
                    if (hasOperatorFilter)
                    {
                        PythonBridgeDbTool.AddParam(command, "@operator", operatorFilter!);
                    }
                    PythonBridgeDbTool.AddParam(command, "@lim", limit);
                    PythonBridgeDbTool.AddParam(command, "@off", offset);

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
                },
                cancellationToken);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetLteBuildingRowsAsync(
            LteBuildingRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT
                    id,
                    name,
                    region,
                    project_id,
                    area,
                    geometry,
                    ST_AsText(region) AS region_wkt,
                    ST_AsText(geometry) AS geometry_wkt
                FROM tbl_savepolygon
                WHERE project_id = @pid
                ORDER BY id
                LIMIT @lim OFFSET @off;";

            PythonBridgeDbTool.AddParam(command, "@pid", request.ProjectId);
            PythonBridgeDbTool.AddParam(command, "@lim", limit);
            PythonBridgeDbTool.AddParam(command, "@off", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
            return (limit, offset, rows);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetLteBaselineRowsAsync(
            LteBaselineRowsRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);
            var cacheKey = BuildCacheKey("lte_baseline", request.ProjectId, limit, offset);

            return await GetCachedOrLoadRowsAsync(
                cacheKey,
                limit,
                offset,
                async () =>
                {
                    var conn = _db.Database.GetDbConnection();
                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync(cancellationToken);
                    }

                    await using var command = conn.CreateCommand();
                    command.CommandText = @"
                SELECT *
                FROM lte_prediction_baseline_results
                WHERE project_id = @pid
                ORDER BY id
                LIMIT @lim OFFSET @off;";

                    PythonBridgeDbTool.AddParam(command, "@pid", request.ProjectId);
                    PythonBridgeDbTool.AddParam(command, "@lim", limit);
                    PythonBridgeDbTool.AddParam(command, "@off", offset);

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
                },
                cancellationToken);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetSitePredictionOptimizedAsync(
            long projectId,
            string? operatorName,
            int requestedLimit,
            int requestedOffset,
            CancellationToken cancellationToken = default
        )
        {
            if (projectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var limit = Math.Clamp(requestedLimit, 1, 50000);
            var offset = Math.Max(requestedOffset, 0);
            var normalizedOperator = operatorName?.Trim();
            var hasOperatorFilter = !string.IsNullOrWhiteSpace(normalizedOperator)
                && !string.Equals(normalizedOperator, "all", StringComparison.OrdinalIgnoreCase);
            var cacheKey = BuildCacheKey("site_pred_opt", projectId, normalizedOperator ?? "all", limit, offset);

            return await GetCachedOrLoadRowsAsync(
                cacheKey,
                limit,
                offset,
                async () =>
                {
                    var conn = _db.Database.GetDbConnection();
                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync(cancellationToken);
                    }

                    await using var command = conn.CreateCommand();
                    command.CommandText = $@"
                SELECT *
                FROM site_prediction_optimized
                WHERE tbl_project_id = @pid
                {(hasOperatorFilter ? "AND cluster = @operator" : string.Empty)}
                ORDER BY id
                LIMIT @lim OFFSET @off;";

                    PythonBridgeDbTool.AddParam(command, "@pid", projectId);
                    if (hasOperatorFilter)
                    {
                        PythonBridgeDbTool.AddParam(command, "@operator", normalizedOperator!);
                    }
                    PythonBridgeDbTool.AddParam(command, "@lim", limit);
                    PythonBridgeDbTool.AddParam(command, "@off", offset);

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
                },
                cancellationToken);
        }

        public async Task<int> SavePredictionDataAsync(
            PredictionDataBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<PredictionDataRow>();
            if (rows.Count == 0)
            {
                return 0;
            }

            if (request.ReplaceProjectData)
            {
                await _db.tbl_prediction_data
                    .Where(x => x.tbl_project_id == (int)request.ProjectId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            var now = DateTime.UtcNow;
            var previousAutoDetect = _db.ChangeTracker.AutoDetectChangesEnabled;
            _db.ChangeTracker.AutoDetectChangesEnabled = false;

            try
            {
                var inserted = 0;

                foreach (var batch in rows.Chunk(DefaultBatchSize))
                {
                    var entities = batch.Select(r => new tbl_prediction_data
                    {
                        tbl_project_id = (int)request.ProjectId,
                        lat = r.lat.HasValue ? (float?)r.lat.Value : null,
                        lon = r.lon.HasValue ? (float?)r.lon.Value : null,
                        rsrp = r.rsrp.HasValue ? (float?)r.rsrp.Value : null,
                        rsrq = r.rsrq.HasValue ? (float?)r.rsrq.Value : null,
                        sinr = r.sinr.HasValue ? (float?)r.sinr.Value : null,
                        serving_cell = r.serving_cell,
                        band = r.band,
                        earfcn = r.earfcn,
                        pci = r.pci,
                        network = r.network,
                        azimuth = r.azimuth,
                        tx_power = r.tx_power,
                        height = r.height,
                        reference_signal_power = r.reference_signal_power,
                        mtilt = r.mtilt,
                        etilt = r.etilt,
                        timestamp = now
                    }).ToList();

                    await _db.tbl_prediction_data.AddRangeAsync(entities, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    inserted += entities.Count;
                    _db.ChangeTracker.Clear();
                }

                return inserted;
            }
            finally
            {
                _db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            }
        }

        public async Task<int> SaveLtePredictionResultsAsync(
            LtePredictionBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<LtePredictionRow>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var previousAutoDetect = _db.ChangeTracker.AutoDetectChangesEnabled;
            _db.ChangeTracker.AutoDetectChangesEnabled = false;

            try
            {
                var inserted = 0;

                foreach (var batch in rows.Chunk(DefaultBatchSize))
                {
                    var now = DateTime.UtcNow;
                    var entities = batch.Select(r => new tbl_lte_prediction_results
                    {
                        ProjectId = request.ProjectId,
                        JobId = request.JobId ?? string.Empty,
                        Lat = r.lat ?? 0.0,
                        Lon = r.lon ?? 0.0,
                        PredRsrp = r.pred_rsrp,
                        PredRsrq = r.pred_rsrq,
                        PredSinr = r.pred_sinr,
                        SiteId = r.site_id,
                        CreatedAt = now
                    }).ToList();

                    await _db.Tbl_lte_prediction_results.AddRangeAsync(entities, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    inserted += entities.Count;
                    _db.ChangeTracker.Clear();
                }

                return inserted;
            }
            finally
            {
                _db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            }
        }

        public async Task<int> SaveLtePredictionRefinedAsync(
            LtePredictionRefinedBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<LtePredictionRefinedRow>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var previousAutoDetect = _db.ChangeTracker.AutoDetectChangesEnabled;
            _db.ChangeTracker.AutoDetectChangesEnabled = false;

            try
            {
                var inserted = 0;

                foreach (var batch in rows.Chunk(DefaultBatchSize))
                {
                    var now = DateTime.UtcNow;
                    var entities = batch.Select(r => new tbl_lte_prediction_results_refined
                    {
                        project_id = request.ProjectId,
                        job_id = request.JobId ?? string.Empty,
                        lat = r.lat ?? 0.0,
                        lon = r.lon ?? 0.0,
                        site_id = r.site_id,
                        pred_rsrp_top2_avg = r.pred_rsrp_top2_avg,
                        pred_rsrp_top3_avg = r.pred_rsrp_top3_avg,
                        measured_dt_rsrp = r.measured_dt_rsrp,
                        created_at = now
                    }).ToList();

                    await _db.tbl_lte_prediction_results_refined.AddRangeAsync(entities, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    inserted += entities.Count;
                    _db.ChangeTracker.Clear();
                }

                return inserted;
            }
            finally
            {
                _db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            }
        }

        public async Task<int> SaveLtePredictionOptimisedResultsAsync(
            LtePredictionOptimisedBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<LtePredictionOptimisedRow>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            var inserted = 0;

            foreach (var row in rows)
            {
                await using var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO lte_prediction_optimised_results
                    (project_id, job_id, lat, lon, pred_rsrp, pred_rsrq, pred_sinr,
                     node_b_id, cell_id, Technology, `operator`, created_at, site_id, nodeb_id_cell_id, scenario_id)
                    VALUES
                    (@project_id, @job_id, @lat, @lon, @pred_rsrp, @pred_rsrq, @pred_sinr,
                     @node_b_id, @cell_id, @technology, @operator, @created_at, @site_id, @nodeb_id_cell_id, @scenario_id);";

                PythonBridgeDbTool.AddParam(command, "@project_id", request.ProjectId);
                PythonBridgeDbTool.AddParam(command, "@job_id", request.JobId);
                PythonBridgeDbTool.AddParam(command, "@lat", row.lat);
                PythonBridgeDbTool.AddParam(command, "@lon", row.lon);
                PythonBridgeDbTool.AddParam(command, "@pred_rsrp", row.pred_rsrp);
                PythonBridgeDbTool.AddParam(command, "@pred_rsrq", row.pred_rsrq);
                PythonBridgeDbTool.AddParam(command, "@pred_sinr", row.pred_sinr);
                PythonBridgeDbTool.AddParam(command, "@node_b_id", row.node_b_id);
                PythonBridgeDbTool.AddParam(command, "@cell_id", row.cell_id);
                PythonBridgeDbTool.AddParam(command, "@technology", row.Technology ?? "4G");
                PythonBridgeDbTool.AddParam(command, "@operator", row.@operator ?? row.operator_name);
                PythonBridgeDbTool.AddParam(command, "@created_at", row.created_at ?? DateTime.UtcNow);
                PythonBridgeDbTool.AddParam(command, "@site_id", row.site_id);
                PythonBridgeDbTool.AddParam(command, "@nodeb_id_cell_id", row.nodeb_id_cell_id);
                PythonBridgeDbTool.AddParam(command, "@scenario_id", row.scenario_id);

                inserted += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }

        public async Task<int> SaveLtePredictionBaselineResultsAsync(
            DictionaryRowsBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<Dictionary<string, object?>>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            var inserted = 0;

            foreach (var batch in rows.Chunk(300))
            {
                await using var command = conn.CreateCommand();
                command.Transaction = transaction;

                var valuesSql = new List<string>();
                for (var i = 0; i < batch.Length; i++)
                {
                    var row = batch[i];
                    valuesSql.Add($@"(@project_id{i}, @job_id{i}, @lat{i}, @lat_6dp{i}, @lon{i}, @lon_6dp{i},
                     @pred_rsrp{i}, @pred_rsrq{i}, @pred_sinr{i}, @node_b_id{i}, @cell_id{i},
                     @operator{i}, @created_at{i}, @site_id{i}, @nodeb_id_cell_id{i}, @technology{i})");

                    PythonBridgeDbTool.AddParam(command, $"@project_id{i}", RowValue(row, "project_id") ?? request.ProjectId);
                    PythonBridgeDbTool.AddParam(command, $"@job_id{i}", RowValue(row, "job_id") ?? request.JobId);
                    PythonBridgeDbTool.AddParam(command, $"@lat{i}", RowValue(row, "lat"));
                    PythonBridgeDbTool.AddParam(command, $"@lat_6dp{i}", RowValue(row, "lat_6dp"));
                    PythonBridgeDbTool.AddParam(command, $"@lon{i}", RowValue(row, "lon"));
                    PythonBridgeDbTool.AddParam(command, $"@lon_6dp{i}", RowValue(row, "lon_6dp"));
                    PythonBridgeDbTool.AddParam(command, $"@pred_rsrp{i}", RowValue(row, "pred_rsrp"));
                    PythonBridgeDbTool.AddParam(command, $"@pred_rsrq{i}", RowValue(row, "pred_rsrq"));
                    PythonBridgeDbTool.AddParam(command, $"@pred_sinr{i}", RowValue(row, "pred_sinr"));
                    PythonBridgeDbTool.AddParam(command, $"@node_b_id{i}", RowValue(row, "node_b_id"));
                    PythonBridgeDbTool.AddParam(command, $"@cell_id{i}", RowValue(row, "cell_id"));
                    PythonBridgeDbTool.AddParam(command, $"@operator{i}", RowValue(row, "operator"));
                    PythonBridgeDbTool.AddParam(command, $"@created_at{i}", RowDate(row, "created_at") ?? DateTime.UtcNow);
                    PythonBridgeDbTool.AddParam(command, $"@site_id{i}", RowValue(row, "site_id"));
                    PythonBridgeDbTool.AddParam(command, $"@nodeb_id_cell_id{i}", RowValue(row, "nodeb_id_cell_id"));
                    PythonBridgeDbTool.AddParam(command, $"@technology{i}", RowValue(row, "Technology") ?? RowValue(row, "technology") ?? "4G");
                }

                command.CommandText = $@"
                    INSERT INTO lte_prediction_baseline_results
                    (project_id, job_id, lat, lat_6dp, lon, lon_6dp,
                     pred_rsrp, pred_rsrq, pred_sinr, node_b_id, cell_id,
                     `operator`, created_at, site_id, nodeb_id_cell_id, Technology)
                    VALUES
                    {string.Join(",", valuesSql)}
                    ON DUPLICATE KEY UPDATE
                     job_id = VALUES(job_id),
                     lat = VALUES(lat),
                     lat_6dp = VALUES(lat_6dp),
                     lon = VALUES(lon),
                     lon_6dp = VALUES(lon_6dp),
                     pred_rsrp = VALUES(pred_rsrp),
                     pred_rsrq = VALUES(pred_rsrq),
                     pred_sinr = VALUES(pred_sinr),
                     node_b_id = VALUES(node_b_id),
                     cell_id = VALUES(cell_id),
                     `operator` = VALUES(`operator`),
                     created_at = VALUES(created_at),
                     site_id = VALUES(site_id),
                     nodeb_id_cell_id = VALUES(nodeb_id_cell_id),
                     Technology = VALUES(Technology);";

                await command.ExecuteNonQueryAsync(cancellationToken);
                inserted += batch.Length;
            }

            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }

        public async Task<int> SaveLtePredictionGeoFeaturesAsync(
            DictionaryRowsBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<Dictionary<string, object?>>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            var inserted = 0;

            foreach (var batch in rows.Chunk(150))
            {
                await using var deleteCommand = conn.CreateCommand();
                deleteCommand.Transaction = transaction;

                var deleteClauses = new List<string>();
                for (var i = 0; i < batch.Length; i++)
                {
                    var row = batch[i];
                    deleteClauses.Add($@"(project_id = @d_project_id{i}
                      AND region = @d_region{i}
                      AND nodeb_id_cell_id = @d_nodeb_id_cell_id{i}
                      AND lat = @d_lat{i}
                      AND lon = @d_lon{i})");

                    PythonBridgeDbTool.AddParam(deleteCommand, $"@d_project_id{i}", RowValue(row, "project_id") ?? request.ProjectId);
                    PythonBridgeDbTool.AddParam(deleteCommand, $"@d_region{i}", RowValue(row, "region") ?? request.Region ?? "india");
                    PythonBridgeDbTool.AddParam(deleteCommand, $"@d_nodeb_id_cell_id{i}", RowValue(row, "nodeb_id_cell_id"));
                    PythonBridgeDbTool.AddParam(deleteCommand, $"@d_lat{i}", RowValue(row, "lat"));
                    PythonBridgeDbTool.AddParam(deleteCommand, $"@d_lon{i}", RowValue(row, "lon"));
                }

                deleteCommand.CommandText = $@"
                    DELETE FROM lte_prediction_geo_features
                    WHERE {string.Join(" OR ", deleteClauses)};";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

                await using var insertCommand = conn.CreateCommand();
                insertCommand.Transaction = transaction;
                var valuesSql = new List<string>();
                for (var i = 0; i < batch.Length; i++)
                {
                    var row = batch[i];
                    valuesSql.Add($@"(@project_id{i}, @baseline_job_id{i}, @region{i}, @operator{i}, @grid_id{i}, @lat{i}, @lon{i},
                     @nodeb_id_cell_id{i}, @proxy_site_id{i}, @clutter_class{i}, @morphology_cluster{i},
                     @building_count{i}, @building_area_ratio{i}, @avg_building_area_m2{i}, @road_length_m{i},
                     @green_ratio{i}, @water_ratio{i}, @los_blocker_count{i}, @los_blocked_ratio{i},
                     @max_blocker_height_m{i}, @diffraction_proxy_db{i}, @nlos_flag{i}, @terrain_elevation_m{i},
                     @terrain_slope_deg{i}, @proxy_site_elevation_m{i}, @terrain_relief_to_site_m{i},
                     @site_count_250m{i}, @site_count_500m{i}, @serving_distance_m{i},
                     @nearest_site_distance_m{i}, @mean_nearest3_site_distance_m{i}, @azimuth_delta_deg{i},
                     @polygon_alignment{i}, @building_alignment{i}, @geo_source{i}, @created_at{i}, @updated_at{i})");

                    var region = Convert.ToString(RowValue(row, "region") ?? request.Region ?? "india")!.ToLowerInvariant();
                    PythonBridgeDbTool.AddParam(insertCommand, $"@project_id{i}", RowValue(row, "project_id") ?? request.ProjectId);
                    PythonBridgeDbTool.AddParam(insertCommand, $"@baseline_job_id{i}", RowValue(row, "baseline_job_id") ?? request.JobId);
                    PythonBridgeDbTool.AddParam(insertCommand, $"@region{i}", region);
                    PythonBridgeDbTool.AddParam(insertCommand, $"@operator{i}", RowValue(row, "operator"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@grid_id{i}", RowValue(row, "grid_id"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@lat{i}", RowValue(row, "lat"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@lon{i}", RowValue(row, "lon"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@nodeb_id_cell_id{i}", RowValue(row, "nodeb_id_cell_id"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@proxy_site_id{i}", RowValue(row, "proxy_site_id"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@clutter_class{i}", RowValue(row, "clutter_class"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@morphology_cluster{i}", RowValue(row, "morphology_cluster"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@building_count{i}", RowValue(row, "building_count"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@building_area_ratio{i}", RowValue(row, "building_area_ratio"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@avg_building_area_m2{i}", RowValue(row, "avg_building_area_m2"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@road_length_m{i}", RowValue(row, "road_length_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@green_ratio{i}", RowValue(row, "green_ratio"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@water_ratio{i}", RowValue(row, "water_ratio"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@los_blocker_count{i}", RowValue(row, "los_blocker_count"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@los_blocked_ratio{i}", RowValue(row, "los_blocked_ratio"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@max_blocker_height_m{i}", RowValue(row, "max_blocker_height_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@diffraction_proxy_db{i}", RowValue(row, "diffraction_proxy_db"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@nlos_flag{i}", RowValue(row, "nlos_flag"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@terrain_elevation_m{i}", RowValue(row, "terrain_elevation_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@terrain_slope_deg{i}", RowValue(row, "terrain_slope_deg"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@proxy_site_elevation_m{i}", RowValue(row, "proxy_site_elevation_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@terrain_relief_to_site_m{i}", RowValue(row, "terrain_relief_to_site_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@site_count_250m{i}", RowValue(row, "site_count_250m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@site_count_500m{i}", RowValue(row, "site_count_500m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@serving_distance_m{i}", RowValue(row, "serving_distance_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@nearest_site_distance_m{i}", RowValue(row, "nearest_site_distance_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@mean_nearest3_site_distance_m{i}", RowValue(row, "mean_nearest3_site_distance_m"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@azimuth_delta_deg{i}", RowValue(row, "azimuth_delta_deg"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@polygon_alignment{i}", RowValue(row, "polygon_alignment"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@building_alignment{i}", RowValue(row, "building_alignment"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@geo_source{i}", RowValue(row, "geo_source"));
                    PythonBridgeDbTool.AddParam(insertCommand, $"@created_at{i}", RowDate(row, "created_at") ?? DateTime.UtcNow);
                    PythonBridgeDbTool.AddParam(insertCommand, $"@updated_at{i}", RowDate(row, "updated_at") ?? DateTime.UtcNow);
                }

                insertCommand.CommandText = $@"
                    INSERT INTO lte_prediction_geo_features
                    (project_id, baseline_job_id, region, `operator`, grid_id, lat, lon,
                     nodeb_id_cell_id, proxy_site_id, clutter_class, morphology_cluster,
                     building_count, building_area_ratio, avg_building_area_m2, road_length_m,
                     green_ratio, water_ratio, los_blocker_count, los_blocked_ratio,
                     max_blocker_height_m, diffraction_proxy_db, nlos_flag, terrain_elevation_m,
                     terrain_slope_deg, proxy_site_elevation_m, terrain_relief_to_site_m,
                     site_count_250m, site_count_500m, serving_distance_m,
                     nearest_site_distance_m, mean_nearest3_site_distance_m, azimuth_delta_deg,
                     polygon_alignment, building_alignment, geo_source, created_at, updated_at)
                    VALUES
                    {string.Join(",", valuesSql)};";

                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                inserted += batch.Length;
            }

            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }

        public async Task<int> DeleteLtePredictionGeoFeaturesAsync(
            DictionaryRowsBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<Dictionary<string, object?>>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            var deleted = 0;

            foreach (var row in rows)
            {
                await using var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    DELETE FROM lte_prediction_geo_features
                    WHERE project_id = @project_id
                      AND region = @region
                      AND nodeb_id_cell_id = @nodeb_id_cell_id
                      AND lat = @lat
                      AND lon = @lon;";

                PythonBridgeDbTool.AddParam(command, "@project_id", RowValue(row, "project_id") ?? request.ProjectId);
                PythonBridgeDbTool.AddParam(command, "@region", RowValue(row, "region") ?? request.Region ?? "india");
                PythonBridgeDbTool.AddParam(command, "@nodeb_id_cell_id", RowValue(row, "nodeb_id_cell_id"));
                PythonBridgeDbTool.AddParam(command, "@lat", RowValue(row, "lat"));
                PythonBridgeDbTool.AddParam(command, "@lon", RowValue(row, "lon"));

                deleted += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }

        public async Task<int> GetNextRfOptimizationScenarioIdAsync(
            long projectId,
            CancellationToken cancellationToken = default
        )
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(scenario_id), 0) + 1 FROM rf_optimization_results WHERE project_id = @pid;";
            PythonBridgeDbTool.AddParam(command, "@pid", projectId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null || value == DBNull.Value ? 1 : Convert.ToInt32(value);
        }

        public async Task<string?> GetLatestLteBaselineJobIdAsync(
            long projectId,
            CancellationToken cancellationToken = default
        )
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT job_id
                FROM lte_prediction_baseline_results
                WHERE project_id = @pid
                ORDER BY created_at DESC
                LIMIT 1;";
            PythonBridgeDbTool.AddParam(command, "@pid", projectId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        public async Task<int> GetNextLteOptimizationScenarioIdAsync(
            long projectId,
            CancellationToken cancellationToken = default
        )
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT COALESCE(MAX(scenario_id), 0) + 1
                FROM lte_optimization_scenarios
                WHERE project_id = @pid;";
            PythonBridgeDbTool.AddParam(command, "@pid", projectId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value == null || value == DBNull.Value ? 1 : Convert.ToInt32(value);
        }

        public async Task<(long RowId, int ScenarioId)> CreateLteOptimizationScenarioAsync(
            LteOptimizationScenarioCreateRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ProjectId <= 0)
            {
                throw new ArgumentException("ProjectId is required.");
            }

            var scenarioId = await GetNextLteOptimizationScenarioIdAsync(request.ProjectId, cancellationToken);
            if (scenarioId > 6)
            {
                throw new InvalidOperationException(
                    $"Maximum scenario limit reached for project_id={request.ProjectId}. Only 6 scenarios are allowed per project."
                );
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                INSERT INTO lte_optimization_scenarios (
                    project_id, scenario_id, baseline_job_id, scenario_name, scenario_description,
                    region, operator, target_type, target_id, impact_radius_m,
                    neighbor_site_count, max_interference_sites, delta_lat, delta_lon,
                    delta_azimuth, delta_electrical_tilt, delta_mechanical_tilt,
                    delta_tx_power, delta_antenna_height, status, created_by
                ) VALUES (
                    @project_id, @scenario_id, @baseline_job_id, @scenario_name, @scenario_description,
                    @region, @operator, @target_type, @target_id, @impact_radius_m,
                    @neighbor_site_count, @max_interference_sites, @delta_lat, @delta_lon,
                    @delta_azimuth, @delta_electrical_tilt, @delta_mechanical_tilt,
                    @delta_tx_power, @delta_antenna_height, @status, @created_by
                );";

            PythonBridgeDbTool.AddParam(command, "@project_id", request.ProjectId);
            PythonBridgeDbTool.AddParam(command, "@scenario_id", scenarioId);
            PythonBridgeDbTool.AddParam(command, "@baseline_job_id", request.BaselineJobId);
            PythonBridgeDbTool.AddParam(command, "@scenario_name", request.ScenarioName);
            PythonBridgeDbTool.AddParam(command, "@scenario_description", request.ScenarioDescription);
            PythonBridgeDbTool.AddParam(command, "@region", request.Region ?? "india");
            PythonBridgeDbTool.AddParam(command, "@operator", request.Operator);
            PythonBridgeDbTool.AddParam(command, "@target_type", request.TargetType);
            PythonBridgeDbTool.AddParam(command, "@target_id", request.TargetId);
            PythonBridgeDbTool.AddParam(command, "@impact_radius_m", request.ImpactRadiusM);
            PythonBridgeDbTool.AddParam(command, "@neighbor_site_count", request.NeighborSiteCount);
            PythonBridgeDbTool.AddParam(command, "@max_interference_sites", request.MaxInterferenceSites);
            PythonBridgeDbTool.AddParam(command, "@delta_lat", request.DeltaLat);
            PythonBridgeDbTool.AddParam(command, "@delta_lon", request.DeltaLon);
            PythonBridgeDbTool.AddParam(command, "@delta_azimuth", request.DeltaAzimuth);
            PythonBridgeDbTool.AddParam(command, "@delta_electrical_tilt", request.DeltaElectricalTilt);
            PythonBridgeDbTool.AddParam(command, "@delta_mechanical_tilt", request.DeltaMechanicalTilt);
            PythonBridgeDbTool.AddParam(command, "@delta_tx_power", request.DeltaTxPower);
            PythonBridgeDbTool.AddParam(command, "@delta_antenna_height", request.DeltaAntennaHeight);
            PythonBridgeDbTool.AddParam(command, "@status", request.Status ?? "created");
            PythonBridgeDbTool.AddParam(command, "@created_by", request.CreatedBy ?? "backend");

            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var idCommand = conn.CreateCommand();
            idCommand.CommandText = "SELECT LAST_INSERT_ID();";
            var rowId = await idCommand.ExecuteScalarAsync(cancellationToken);
            return (Convert.ToInt64(rowId), scenarioId);
        }

        public async Task UpdateLteOptimizationScenarioStatusAsync(
            LteOptimizationScenarioStatusRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.ScenarioRowId <= 0)
            {
                throw new ArgumentException("ScenarioRowId is required.");
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                UPDATE lte_optimization_scenarios
                SET status = @status,
                    baseline_job_id = COALESCE(baseline_job_id, @baseline_job_id),
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @scenario_row_id;";
            PythonBridgeDbTool.AddParam(command, "@status", request.Status);
            PythonBridgeDbTool.AddParam(command, "@baseline_job_id", request.BaselineJobId);
            PythonBridgeDbTool.AddParam(command, "@scenario_row_id", request.ScenarioRowId);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> SaveRfOptimizationResultsAsync(
            RfOptimizationBulkRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var rows = request.Rows ?? new List<RfOptimizationRow>();
            if (rows.Count == 0)
            {
                return 0;
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            var inserted = 0;

            foreach (var row in rows)
            {
                await using var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO rf_optimization_results
                    (project_id, scenario_id, `operator`, cell_id, technology, parameter,
                     current_value, recommended_value, reason, swap_sector_detected,
                     rsrp_threshold, rsrq_threshold, sinr_threshold, created_at)
                    VALUES
                    (@project_id, @scenario_id, @operator, @cell_id, @technology, @parameter,
                     @current_value, @recommended_value, @reason, @swap_sector_detected,
                     @rsrp_threshold, @rsrq_threshold, @sinr_threshold, @created_at);";

                PythonBridgeDbTool.AddParam(command, "@project_id", request.ProjectId);
                PythonBridgeDbTool.AddParam(command, "@scenario_id", request.ScenarioId);
                PythonBridgeDbTool.AddParam(command, "@operator", row.@operator);
                PythonBridgeDbTool.AddParam(command, "@cell_id", row.cell_id);
                PythonBridgeDbTool.AddParam(command, "@technology", row.technology);
                PythonBridgeDbTool.AddParam(command, "@parameter", row.parameter);
                PythonBridgeDbTool.AddParam(command, "@current_value", row.current_value);
                PythonBridgeDbTool.AddParam(command, "@recommended_value", row.recommended_value);
                PythonBridgeDbTool.AddParam(command, "@reason", row.reason);
                PythonBridgeDbTool.AddParam(command, "@swap_sector_detected", row.swap_sector_detected);
                PythonBridgeDbTool.AddParam(command, "@rsrp_threshold", row.rsrp_threshold);
                PythonBridgeDbTool.AddParam(command, "@rsrq_threshold", row.rsrq_threshold);
                PythonBridgeDbTool.AddParam(command, "@sinr_threshold", row.sinr_threshold);
                PythonBridgeDbTool.AddParam(command, "@created_at", row.created_at ?? DateTime.UtcNow);

                inserted += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }

        public async Task<(bool ProjectExists, long SiteNoMlCount)> PredictionDebugSummaryAsync(
            long projectId,
            CancellationToken cancellationToken = default
        )
        {
            var projectExists = await _db.tbl_project
                .AsNoTracking()
                .AnyAsync(p => p.id == projectId, cancellationToken);

            var siteCount = 0L;
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM site_noMl WHERE project_id = @pid";
            PythonBridgeDbTool.AddParam(command, "@pid", projectId);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar != null && scalar != DBNull.Value)
            {
                siteCount = Convert.ToInt64(scalar);
            }

            return (projectExists, siteCount);
        }

        public async Task<tbl_project?> GetProjectAsync(
            long projectId,
            CancellationToken cancellationToken = default
        )
        {
            return await _db.tbl_project
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.id == projectId, cancellationToken);
        }

        public async Task<List<Dictionary<string, object?>>> GetProjectRegionsAsync(
            long projectId,
            CancellationToken cancellationToken = default
        )
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT
                    id,
                    name,
                    ST_AsText(region) AS region_wkt,
                    area
                FROM map_regions
                WHERE tbl_project_id = @pid
                  AND status = 1
                ORDER BY id;";
            PythonBridgeDbTool.AddParam(command, "@pid", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
        }

        public async Task<(int Limit, int Offset, List<Dictionary<string, object?>> Rows)> GetReportNetworkLogsAsync(
            SessionIdsPagedRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var sessionIds = request.SessionIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (sessionIds.Count == 0)
            {
                throw new ArgumentException("No valid SessionIds provided.");
            }

            var limit = Math.Clamp(request.Limit, 1, 50000);
            var offset = Math.Max(request.Offset, 0);

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            var inClause = PythonBridgeDbTool.BuildInClause(command, sessionIds, "sid");
            command.CommandText = $@"
                SELECT *
                FROM tbl_network_log
                WHERE session_id IN ({inClause})
                ORDER BY session_id, timestamp, id
                LIMIT @lim OFFSET @off;";
            PythonBridgeDbTool.AddParam(command, "@lim", limit);
            PythonBridgeDbTool.AddParam(command, "@off", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
            return (limit, offset, rows);
        }

        public async Task<List<Dictionary<string, object?>>> GetSessionsAsync(
            IReadOnlyList<long> sessionIds,
            CancellationToken cancellationToken = default
        )
        {
            var validIds = sessionIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (validIds.Count == 0)
            {
                return new List<Dictionary<string, object?>>();
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            await using var command = conn.CreateCommand();
            var inClause = PythonBridgeDbTool.BuildInClause(command, validIds, "sid");
            command.CommandText = $@"
                SELECT id, start_time, end_time, distance
                FROM tbl_session
                WHERE id IN ({inClause})
                ORDER BY start_time, id;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await PythonBridgeDbTool.ReadRowsAsync(reader, cancellationToken);
        }

        public async Task<tbl_user?> GetUserByIdAsync(
            int userId,
            CancellationToken cancellationToken = default
        )
        {
            return await _db.tbl_user
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.id == userId, cancellationToken);
        }

        public async Task<thresholds?> GetUserThresholdsAsync(
            int userId,
            CancellationToken cancellationToken = default
        )
        {
            var userSetting = await _db.thresholds
                .AsNoTracking()
                .Where(x => x.user_id == userId && x.is_default == 0)
                .OrderByDescending(x => x.id)
                .FirstOrDefaultAsync(cancellationToken);

            if (userSetting != null)
            {
                return userSetting;
            }

            var defaultSetting = await _db.thresholds
                .AsNoTracking()
                .Where(x => x.is_default == 1 && (x.user_id == null || x.user_id == 0))
                .OrderByDescending(x => x.id)
                .FirstOrDefaultAsync(cancellationToken);

            if (defaultSetting != null)
            {
                return defaultSetting;
            }

            return await _db.thresholds
                .AsNoTracking()
                .OrderBy(x => x.id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<bool> UpdateProjectDownloadPathAsync(
            long projectId,
            string downloadPath,
            CancellationToken cancellationToken = default
        )
        {
            var project = await _db.tbl_project.FirstOrDefaultAsync(
                p => p.id == projectId,
                cancellationToken
            );

            if (project == null)
            {
                return false;
            }

            project.Download_path = downloadPath;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
