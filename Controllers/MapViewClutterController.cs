using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
    private static readonly Dictionary<string, string[]> SavedSourceLayerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["buildings"] = new[] { "overture_building", "osm_building" },
        ["roads"] = new[] { "overture_road" },
        ["highways"] = new[] { "overture_highway" },
        ["railways"] = new[] { "overture_railway" },
        ["water"] = new[] { "overture_water" },
        ["land_use"] = new[] { "overture_land_use" },
        ["land_cover"] = new[] { "overture_land_cover" },
    };

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

    /// <summary>
    /// Returns real source geometry stored in tbl_savepolygon for optional map overlays.
    /// Roads/railways/highways are returned from the geometry column so the client
    /// can render them as lines; polygon-like sources fall back to region.
    /// </summary>
    [HttpGet("GetProjectSavedSourceGeometries")]
    public async Task<IActionResult> GetProjectSavedSourceGeometries(
        [FromQuery] long projectId,
        [FromQuery] string? layer = null,
        [FromQuery] int limit = 5000,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (projectId <= 0)
            return BadRequest(new { status = 0, message = "projectId must be positive." });
        if (offset < 0)
            return BadRequest(new { status = 0, message = "offset cannot be negative." });

        var layerKey = string.IsNullOrWhiteSpace(layer) ? "all" : layer.Trim().ToLowerInvariant();
        if (layerKey != "all" && !SavedSourceLayerNames.ContainsKey(layerKey))
            return BadRequest(new { status = 0, message = "layer must be one of all, buildings, roads, highways, railways, water, land_use, land_cover." });

        var isSuperAdmin = _userScope.IsSuperAdmin(User);
        var targetCompanyId = GetTargetCompanyId(null);
        var connection = new MySqlConnection(_connectionProvider.GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        object? company;
        await using (var access = connection.CreateCommand())
        {
            access.CommandText = "SELECT company_id FROM tbl_project WHERE id = @projectId LIMIT 1;";
            Add(access, "@projectId", projectId);
            company = await access.ExecuteScalarAsync(cancellationToken);
        }

        var shouldTryTaiwan = !string.Equals(connection.Database, "TaiwanDB", StringComparison.OrdinalIgnoreCase);
        if (shouldTryTaiwan && company != null && company != DBNull.Value)
        {
            await using var sourceCheck = connection.CreateCommand();
            sourceCheck.CommandText = "SELECT COUNT(*) FROM tbl_savepolygon WHERE project_id = @projectId AND is_active = 1;";
            Add(sourceCheck, "@projectId", projectId);
            var sourceCount = await sourceCheck.ExecuteScalarAsync(cancellationToken);
            shouldTryTaiwan = Convert.ToInt64(sourceCount, System.Globalization.CultureInfo.InvariantCulture) == 0;
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

        var maxRows = Math.Clamp(limit, 1, 5000);
        var rows = new List<object>();
        long totalCount;
        var where = new StringBuilder("WHERE s.project_id = @projectId AND s.is_active = 1");
        var prefixes = layerKey == "all"
            ? SavedSourceLayerNames.SelectMany(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : SavedSourceLayerNames[layerKey];

        where.Append(" AND (");
        for (var i = 0; i < prefixes.Length; i++)
        {
            if (i > 0) where.Append(" OR ");
            where.Append($"s.source_name = @source{i} OR s.name = @source{i} OR s.name LIKE @sourcePrefix{i}");
        }
        // Older building imports use overture_auto_<projectId> without a source tag.
        // Include only that exact legacy name; other unclassified polygons stay excluded.
        if (layerKey is "all" or "buildings")
        {
            where.Append(" OR ((s.source_name IS NULL OR s.source_name = '') AND s.name = CONCAT('overture_auto_', @projectId))");
        }
        where.Append(')');

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM tbl_savepolygon s {where};";
            Add(countCommand, "@projectId", projectId);
            for (var i = 0; i < prefixes.Length; i++)
            {
                Add(countCommand, $"@source{i}", prefixes[i]);
                Add(countCommand, $"@sourcePrefix{i}", prefixes[i] + "_%");
            }
            totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 60;
            command.CommandText = $@"
SELECT
    s.id,
    s.name,
    s.source_name,
    s.area,
    s.height_m,
    ST_GeometryType(
        CASE
            WHEN boundary.project_geom IS NULL THEN COALESCE(s.geometry, s.region)
            ELSE ST_Intersection(COALESCE(s.geometry, s.region), boundary.project_geom)
        END
    ) AS geometry_type,
    ST_AsText(
        CASE
            WHEN boundary.project_geom IS NULL THEN COALESCE(s.geometry, s.region)
            ELSE ST_Intersection(COALESCE(s.geometry, s.region), boundary.project_geom)
        END
    ) AS geometry_wkt
FROM tbl_savepolygon s
LEFT JOIN (
    SELECT region AS project_geom
    FROM map_regions
    WHERE tbl_project_id = @projectId AND status = 1
    ORDER BY id DESC
    LIMIT 1
) boundary ON TRUE
{where}
HAVING geometry_wkt IS NOT NULL AND geometry_wkt <> 'GEOMETRYCOLLECTION EMPTY'
ORDER BY s.id
LIMIT @limit OFFSET @offset;";
            Add(command, "@projectId", projectId);
            Add(command, "@limit", maxRows);
            Add(command, "@offset", offset);
            for (var i = 0; i < prefixes.Length; i++)
            {
                Add(command, $"@source{i}", prefixes[i]);
                Add(command, $"@sourcePrefix{i}", prefixes[i] + "_%");
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                var sourceName = reader.IsDBNull(2) ? null : reader.GetString(2);
                rows.Add(new
                {
                    id,
                    name,
                    sourceName,
                    layer = ResolveSavedSourceLayer(name, sourceName),
                    area = reader.IsDBNull(3) ? (double?)null : Convert.ToDouble(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                    heightM = reader.IsDBNull(4) ? (double?)null : Convert.ToDouble(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture),
                    geometryType = reader.IsDBNull(5) ? null : reader.GetString(5),
                    geometryWkt = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }

        var nextOffset = offset + maxRows;
        return Ok(new
        {
            status = 1,
            projectId,
            layer = layerKey,
            database = connection.Database,
            matchedCount = totalCount,
            returnedCount = rows.Count,
            offset,
            limit = maxRows,
            hasMore = nextOffset < totalCount,
            nextOffset,
            data = rows
        });
    }

    private static string ResolveSavedSourceLayer(string? name, string? sourceName)
    {
        var value = $"{sourceName} {name}".ToLowerInvariant();
        if (value.Contains("overture_road")) return "roads";
        if (value.Contains("overture_highway")) return "highways";
        if (value.Contains("overture_railway")) return "railways";
        if (value.Contains("overture_water")) return "water";
        if (value.Contains("overture_land_use")) return "land_use";
        if (value.Contains("overture_land_cover")) return "land_cover";
        return "buildings";
    }
}
