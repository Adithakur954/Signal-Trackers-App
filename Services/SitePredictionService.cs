using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SignalTracker.DTO.SitePrediction;
using SignalTracker.Models;

namespace SignalTracker.Services
{
    public class SitePredictionService
    {
        private static readonly string[] CacheInvalidationPatterns =
        {
            "mapview:*",
            "projectpolygons:*",
            "availablepolygons:*",
            "networklog:v2:*",
            "networklog:v3:*",
            "latlon:dist:*",
            "n78_simple_kpi:*",
            "n78_neighbours:*",
            "daterangelog:*"
        };

        private readonly ApplicationDbContext _db;
        private readonly RedisService _redis;

        public SitePredictionService(ApplicationDbContext db, RedisService redis)
        {
            _db = db;
            _redis = redis;
        }

        public async Task<IReadOnlyList<SitePredictionScenarioDto>> GetScenariosAsync(long projectId)
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await EnsureOptimizedTableAsync(conn);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    scenario,
                    MAX(COALESCE(updated_at, created_at)) AS latest_at,
                    COUNT(*) AS row_count
                FROM site_prediction_optimized
                WHERE tbl_project_id = @pid
                  AND scenario > 0
                GROUP BY scenario
                ORDER BY scenario;";
            Add(cmd, "@pid", projectId);

            var rows = new List<SitePredictionScenarioDto>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var scenario = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                if (scenario <= 0) continue;

                rows.Add(new SitePredictionScenarioDto
                {
                    scenario_id = scenario,
                    scenario_name = $"Scenario {scenario}",
                    status = "updated",
                    row_count = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
                    updated_at = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1))
                });
            }

            return rows;
        }

        public async Task<SitePredictionDeleteResult> DeleteScenarioAsync(DeleteSitePredictionScenarioRequest request)
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await EnsureOptimizedTableAsync(conn);

            await using var tx = await conn.BeginTransactionAsync();
            await using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = @"
                DELETE FROM site_prediction_optimized
                WHERE tbl_project_id = @pid
                  AND scenario = @scenario;";
            Add(deleteCmd, "@pid", request.ProjectId);
            Add(deleteCmd, "@scenario", request.Scenario);

            var deletedRows = await deleteCmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            await InvalidateCachesAsync();

            return new SitePredictionDeleteResult
            {
                Message = deletedRows > 0
                    ? $"Scenario {request.Scenario} deleted successfully."
                    : $"No rows found for Scenario {request.Scenario}.",
                RowsAffected = deletedRows,
                DeletedOptimizedRows = deletedRows,
                OptimizedOnly = true,
                RequestedProjectId = request.ProjectId,
                RequestedSite = request.Scenario.ToString(),
                RequestedDeleteEntireSite = true
            };
        }

        public async Task<SitePredictionDeleteResult> DeleteAsync(DeleteSitePredictionRequest request)
        {
            var siteValue = (request.Site ?? string.Empty).Trim();
            var sectorValue = (request.Sector ?? string.Empty).Trim();
            var cellIdValue = (request.CellId ?? string.Empty).Trim();
            var deleteBySourceId = request.SourceId.HasValue && request.SourceId.Value > 0;
            var deleteByCellId = !string.IsNullOrWhiteSpace(cellIdValue);
            var deleteEntireSite = request.DeleteEntireSite;
            var optimizedOnly = request.OptimizedOnly;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await EnsureOptimizedTableAsync(conn);

            if (optimizedOnly)
                return await DeleteOptimizedOnlyAsync(conn, request, siteValue, sectorValue, cellIdValue, deleteBySourceId, deleteByCellId, deleteEntireSite);

            await using var tx = await conn.BeginTransactionAsync();
            var sourceIds = await FindSourceIdsAsync(conn, tx, request, siteValue, sectorValue, cellIdValue, deleteBySourceId, deleteByCellId, deleteEntireSite);

            var sourceIdParamNames = sourceIds.Select((_, idx) => $"@id{idx}").ToList();
            var sourceIdInClause = string.Join(", ", sourceIdParamNames);

            var deletedSourceRows = 0;
            if (sourceIds.Count > 0)
            {
                await using var deleteSourceCmd = conn.CreateCommand();
                deleteSourceCmd.Transaction = tx;
                deleteSourceCmd.CommandText = $@"
                    DELETE FROM site_prediction
                    WHERE id IN ({sourceIdInClause});";
                for (var i = 0; i < sourceIds.Count; i += 1)
                    Add(deleteSourceCmd, sourceIdParamNames[i], sourceIds[i]);
                deletedSourceRows = await deleteSourceCmd.ExecuteNonQueryAsync();
            }

            var deletedOptimizedRows = await DeleteOptimizedRowsAsync(
                conn,
                tx,
                request,
                sourceIds,
                sourceIdParamNames,
                siteValue,
                sectorValue,
                cellIdValue,
                deleteByCellId,
                deleteEntireSite);

            await tx.CommitAsync();
            await InvalidateCachesAsync();

            return new SitePredictionDeleteResult
            {
                Message = deletedSourceRows > 0 || deletedOptimizedRows > 0
                    ? "Deleted successfully."
                    : "No matching rows found.",
                RowsAffected = deletedSourceRows + deletedOptimizedRows,
                DeletedSourceRows = deletedSourceRows,
                DeletedOptimizedRows = deletedOptimizedRows,
                DeletedSourceIds = sourceIds,
                RequestedProjectId = request.ProjectId,
                RequestedSourceId = request.SourceId,
                RequestedCellId = request.CellId,
                RequestedSite = request.Site,
                RequestedSector = request.Sector,
                RequestedDeleteEntireSite = request.DeleteEntireSite
            };
        }

        private async Task<SitePredictionDeleteResult> DeleteOptimizedOnlyAsync(
            DbConnection conn,
            DeleteSitePredictionRequest request,
            string siteValue,
            string sectorValue,
            string cellIdValue,
            bool deleteBySourceId,
            bool deleteByCellId,
            bool deleteEntireSite)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var whereParts = new List<string> { "spo.tbl_project_id = @pid" };
            if (deleteBySourceId)
            {
                whereParts.Add("spo.site_prediction_id = @sourceId");
            }
            else if (deleteByCellId)
            {
                whereParts.Add("CONVERT(spo.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci = @cellId");
            }
            else
            {
                whereParts.Add("CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @site");
                if (!deleteEntireSite)
                    whereParts.Add("CONVERT(spo.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @sector");
            }

            cmd.CommandText = $@"
                DELETE FROM site_prediction_optimized spo
                WHERE {string.Join(" AND ", whereParts)};";
            Add(cmd, "@pid", request.ProjectId);
            if (deleteBySourceId) Add(cmd, "@sourceId", request.SourceId!.Value);
            if (!deleteBySourceId && deleteByCellId) Add(cmd, "@cellId", cellIdValue);
            if (!deleteBySourceId && !deleteByCellId) Add(cmd, "@site", siteValue);
            if (!deleteBySourceId && !deleteByCellId && !deleteEntireSite) Add(cmd, "@sector", sectorValue);

            var deletedRows = await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            await InvalidateCachesAsync();

            return new SitePredictionDeleteResult
            {
                Message = deletedRows > 0
                    ? "Optimized rows deleted successfully."
                    : "No optimized rows matched the request.",
                RowsAffected = deletedRows,
                DeletedOptimizedRows = deletedRows,
                OptimizedOnly = true,
                RequestedProjectId = request.ProjectId,
                RequestedSourceId = request.SourceId,
                RequestedCellId = request.CellId,
                RequestedSite = request.Site,
                RequestedSector = request.Sector,
                RequestedDeleteEntireSite = request.DeleteEntireSite
            };
        }

        private static async Task<List<long>> FindSourceIdsAsync(
            DbConnection conn,
            DbTransaction tx,
            DeleteSitePredictionRequest request,
            string siteValue,
            string sectorValue,
            string cellIdValue,
            bool deleteBySourceId,
            bool deleteByCellId,
            bool deleteEntireSite)
        {
            var lookupParts = new List<string>();
            if (deleteBySourceId)
                lookupParts.Add("sp.id = @sourceId");

            if (deleteByCellId && !deleteEntireSite)
                lookupParts.Add("CONVERT(sp.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci = @cellId");

            if (!string.IsNullOrWhiteSpace(siteValue))
            {
                var siteParts = new List<string>
                {
                    "CONVERT(sp.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @site"
                };
                if (!deleteEntireSite)
                    siteParts.Add("CONVERT(sp.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @sector");
                lookupParts.Add($"({string.Join(" AND ", siteParts)})");
            }

            if (lookupParts.Count == 0)
                return new List<long>();

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                SELECT sp.id
                FROM site_prediction sp
                WHERE sp.tbl_project_id = @pid
                  AND ({string.Join(" OR ", lookupParts)});";
            Add(cmd, "@pid", request.ProjectId);
            if (deleteBySourceId) Add(cmd, "@sourceId", request.SourceId!.Value);
            if (deleteByCellId && !deleteEntireSite) Add(cmd, "@cellId", cellIdValue);
            if (!string.IsNullOrWhiteSpace(siteValue)) Add(cmd, "@site", siteValue);
            if (!string.IsNullOrWhiteSpace(siteValue) && !deleteEntireSite) Add(cmd, "@sector", sectorValue);

            var ids = new List<long>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!await reader.IsDBNullAsync(0))
                    ids.Add(Convert.ToInt64(reader.GetValue(0)));
            }

            return ids;
        }

        private static async Task<int> DeleteOptimizedRowsAsync(
            DbConnection conn,
            DbTransaction tx,
            DeleteSitePredictionRequest request,
            IReadOnlyList<long> sourceIds,
            IReadOnlyList<string> sourceIdParamNames,
            string siteValue,
            string sectorValue,
            string cellIdValue,
            bool deleteByCellId,
            bool deleteEntireSite)
        {
            var whereParts = new List<string>();
            if (sourceIds.Count > 0)
                whereParts.Add($"spo.site_prediction_id IN ({string.Join(", ", sourceIdParamNames)})");

            if (deleteByCellId && !deleteEntireSite)
                whereParts.Add("(spo.tbl_project_id = @pid AND CONVERT(spo.cell_id USING utf8mb4) COLLATE utf8mb4_unicode_ci = @cellId)");

            if (!string.IsNullOrWhiteSpace(siteValue))
            {
                var siteParts = new List<string>
                {
                    "spo.tbl_project_id = @pid",
                    "CONVERT(spo.site USING utf8mb4) COLLATE utf8mb4_unicode_ci = @site"
                };
                if (!deleteEntireSite)
                    siteParts.Add("CONVERT(spo.sector USING utf8mb4) COLLATE utf8mb4_unicode_ci = @sector");
                whereParts.Add($"({string.Join(" AND ", siteParts)})");
            }

            if (whereParts.Count == 0)
                return 0;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                DELETE FROM site_prediction_optimized spo
                WHERE {string.Join(" OR ", whereParts)};";
            for (var i = 0; i < sourceIds.Count; i += 1)
                Add(cmd, sourceIdParamNames[i], sourceIds[i]);
            if (deleteByCellId && !deleteEntireSite)
            {
                Add(cmd, "@pid", request.ProjectId);
                Add(cmd, "@cellId", cellIdValue);
            }
            if (!string.IsNullOrWhiteSpace(siteValue))
            {
                if (!deleteByCellId || deleteEntireSite)
                    Add(cmd, "@pid", request.ProjectId);
                Add(cmd, "@site", siteValue);
                if (!deleteEntireSite)
                    Add(cmd, "@sector", sectorValue);
            }

            return await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureOptimizedTableAsync(DbConnection conn)
        {
            await using (var createCmd = conn.CreateCommand())
            {
                createCmd.CommandText = "CREATE TABLE IF NOT EXISTS site_prediction_optimized LIKE site_prediction;";
                await createCmd.ExecuteNonQueryAsync();
            }

            var requiredColumns = new (string Name, string Definition)[]
            {
                ("scenario", "INT NOT NULL DEFAULT 1"),
                ("site_prediction_id", "INT NULL"),
                ("is_updated", "TINYINT(1) NOT NULL DEFAULT 1"),
                ("version", "INT NOT NULL DEFAULT 1"),
                ("status", "VARCHAR(20) NULL DEFAULT 'updated'"),
                ("created_at", "DATETIME NULL"),
                ("updated_at", "DATETIME NULL"),
                ("updated_by", "VARCHAR(255) NULL")
            };

            foreach (var column in requiredColumns)
            {
                await using var existsCmd = conn.CreateCommand();
                existsCmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND TABLE_NAME = 'site_prediction_optimized'
                      AND COLUMN_NAME = @columnName;";
                Add(existsCmd, "@columnName", column.Name);

                var existsObj = await existsCmd.ExecuteScalarAsync();
                var exists = existsObj != null && existsObj != DBNull.Value && Convert.ToInt32(existsObj) > 0;
                if (exists) continue;

                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE `site_prediction_optimized` ADD COLUMN `{column.Name}` {column.Definition};";
                await alterCmd.ExecuteNonQueryAsync();
            }
        }

        private async Task InvalidateCachesAsync()
        {
            if (_redis?.IsConnected != true)
                return;

            foreach (var pattern in CacheInvalidationPatterns)
            {
                try
                {
                    await _redis.DeleteByPatternAsync(pattern);
                }
                catch
                {
                    // Best effort only.
                }
            }
        }

        private static void Add(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
