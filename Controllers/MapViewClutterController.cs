using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
    private static string NormalizeClutterClass(string? clutterClass, string? landCoverClass, out string source)
    {
        var raw = !string.IsNullOrWhiteSpace(clutterClass) ? clutterClass.Trim()
            : !string.IsNullOrWhiteSpace(landCoverClass) ? landCoverClass.Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            source = "unavailable";
            return "Unclassified";
        }

        var value = raw.ToLowerInvariant();
        var compactValue = new string(value.Where(char.IsLetterOrDigit).ToArray());
        source = !string.IsNullOrWhiteSpace(clutterClass) ? "tbl_project_clutter_tile.clutter_class"
            : "tbl_project_clutter_tile.land_cover_class";

        if (compactValue.Contains("denseurban") || value.Contains("high density"))
            return "Dense Urban";
        if (compactValue.Contains("suburban") || compactValue.Contains("periurban"))
            return "Suburban";
        if (value.Contains("water") || value.Contains("river") || value.Contains("lake") || value.Contains("sea"))
            return "Water";
        if (value.Contains("vegetation") || value.Contains("forest") || value.Contains("wood")
            || value.Contains("crop") || value.Contains("grass") || value.Contains("shrub")
            || value.Contains("green") || value.Contains("park") || value.Contains("garden"))
            return "Vegetation";
        if (value.Contains("rural") || value.Contains("countryside") || value.Contains("open")
            || value.Contains("bare") || value.Contains("agricultur"))
            return "Rural/Open";
        if (value.Contains("urban") || value.Contains("city") || value.Contains("residential")
            || value.Contains("building") || value.Contains("built") || value.Contains("roof")
            || value.Contains("structure") || value.Contains("road") || value.Contains("street")
            || value.Contains("highway") || value.Contains("motorway") || value.Contains("freeway")
            || value.Contains("rail") || value.Contains("transport"))
            return "Urban";

        source = "unmapped";
        return "Unclassified";
    }

    /// <summary>
    /// Returns active classified clutter tiles for the project. Geometry is returned
    /// as WKT and paged so clients can render each page as it arrives.
    /// </summary>
    [HttpGet("GetProjectClutterTiles")]
    [HttpGet("GetProjectBuildingClutterTiles")]
    public async Task<IActionResult> GetProjectBuildingClutterTiles(
        [FromQuery] long projectId,
        [FromQuery] long? buildingPolygonId = null,
        [FromQuery] int limit = 1000,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (projectId <= 0)
            return BadRequest(new { status = 0, message = "projectId must be positive." });
        if (buildingPolygonId is <= 0)
            return BadRequest(new { status = 0, message = "buildingPolygonId must be positive when supplied." });
        if (offset < 0)
            return BadRequest(new { status = 0, message = "offset cannot be negative." });

        var isSuperAdmin = _userScope.IsSuperAdmin(User);
        var targetCompanyId = GetTargetCompanyId(null);
        // Use the country-aware connection provider. The EF context is tied to
        // the main database, while Taiwan projects live in TaiwanDB.
        var connection = new MySqlConnection(_connectionProvider.GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        object? company;
        await using (var access = connection.CreateCommand())
        {
            access.CommandText = "SELECT company_id FROM tbl_project WHERE id = @projectId LIMIT 1;";
            Add(access, "@projectId", projectId);
            company = await access.ExecuteScalarAsync(cancellationToken);
        }

        // Older login cookies may not contain country_code, and project IDs can
        // exist in both databases. For this endpoint, an empty clutter dataset
        // in the provider-selected database is also a reason to try TaiwanDB.
        var shouldTryTaiwan = !string.Equals(connection.Database, "TaiwanDB", StringComparison.OrdinalIgnoreCase);
        if (shouldTryTaiwan && company != null && company != DBNull.Value)
        {
            await using var clutterCheck = connection.CreateCommand();
            clutterCheck.CommandText = @"
SELECT COUNT(*)
FROM tbl_project_clutter_tile
WHERE project_id = @projectId AND is_active = 1 AND geometry_wkt IS NOT NULL;";
            Add(clutterCheck, "@projectId", projectId);
            var clutterCount = await clutterCheck.ExecuteScalarAsync(cancellationToken);
            shouldTryTaiwan = Convert.ToInt64(clutterCount, System.Globalization.CultureInfo.InvariantCulture) == 0;
        }

        if ((company == null || company == DBNull.Value || shouldTryTaiwan)
            && !string.Equals(connection.Database, "TaiwanDB", StringComparison.OrdinalIgnoreCase))
        {
            await connection.DisposeAsync();
            connection = new MySqlConnection(MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(
                _configuration.GetConnectionString("MySqlConnection2")));
            await connection.OpenAsync(cancellationToken);
            await using var twAccess = connection.CreateCommand();
            twAccess.CommandText = "SELECT company_id FROM tbl_project WHERE id = @projectId LIMIT 1;";
            Add(twAccess, "@projectId", projectId);
            company = await twAccess.ExecuteScalarAsync(cancellationToken);
        }

        if (company == null || company == DBNull.Value)
            return NotFound(new { status = 0, message = "Project not found." });

        var projectCompanyId = Convert.ToInt32(company, System.Globalization.CultureInfo.InvariantCulture);
        if (!isSuperAdmin && projectCompanyId != targetCompanyId)
            return Forbid();

        await using var ownedConnection = connection;

        static string? ReadTextValue(System.Data.Common.DbDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            var value = reader.GetValue(ordinal);
            return value switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        var maxRows = Math.Clamp(limit, 1, 5000);
        var rows = new List<object>();
        long totalCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = @"
SELECT COUNT(*) FROM tbl_project_clutter_tile
WHERE project_id = @projectId AND is_active = 1 AND geometry_wkt IS NOT NULL;";
            Add(countCommand, "@projectId", projectId);
            totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 60;
            Add(command, "@projectId", projectId);
            Add(command, "@limit", maxRows);
            Add(command, "@offset", offset);
            command.CommandText = @"
SELECT id, grid_id, clutter_class, land_cover_class, resolution_m, geometry_wkt
FROM tbl_project_clutter_tile
WHERE project_id = @projectId AND is_active = 1 AND geometry_wkt IS NOT NULL
ORDER BY id
LIMIT @limit OFFSET @offset;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rawClutterClass = ReadTextValue(reader, 2);
                var rawLandCoverClass = ReadTextValue(reader, 3);
                var normalizedClass = NormalizeClutterClass(rawClutterClass, rawLandCoverClass, out var classSource);

                rows.Add(new
                {
                    clutterTileId = reader.GetInt64(0),
                    clusterTile = ReadTextValue(reader, 1),
                    gridId = ReadTextValue(reader, 1),
                    clutterClass = normalizedClass,
                    clutterClassSource = classSource,
                    rawClutterClass,
                    landCoverClass = rawLandCoverClass,
                    resolutionM = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4),
                    clutterPolygonWkt = ReadTextValue(reader, 5)
                });
            }
        }

        var nextOffset = offset + rows.Count;
        var hasMore = nextOffset < totalCount;

        return Ok(new
        {
            status = 1,
            projectId,
            buildingPolygonId,
            database = connection.Database,
            matchedCount = totalCount,
            returnedCount = rows.Count,
            offset,
            limit = maxRows,
            hasMore,
            nextOffset,
            data = rows
        });
    }
}
