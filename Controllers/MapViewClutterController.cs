using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;
using SignalTracker.Services;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
    private sealed class SwapCoordinateAxesFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int index)
        {
            var x = sequence.GetX(index);
            var y = sequence.GetY(index);
            sequence.SetOrdinate(index, Ordinate.X, y);
            sequence.SetOrdinate(index, Ordinate.Y, x);
        }
    }

    private static bool LooksLikeLatitudeLongitude(Envelope envelope)
    {
        return envelope.MinX >= -90 && envelope.MaxX <= 90
            && envelope.MinY >= 90 && envelope.MaxY <= 180;
    }

    private static string? NormalizeClutterClass(string? clutterClass, string? landCoverClass, string? polygonName,
        string polygonSource, out string source)
    {
        var raw = string.Join(" ", new[] { clutterClass, landCoverClass, polygonName }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            source = "unavailable";
            return null;
        }

        var value = raw.ToLowerInvariant();
        var compactValue = new string(value.Where(char.IsLetterOrDigit).ToArray());
        source = !string.IsNullOrWhiteSpace(clutterClass) ? "tbl_project_clutter_tile.clutter_class"
            : !string.IsNullOrWhiteSpace(landCoverClass) ? "tbl_project_clutter_tile.land_cover_class"
            : polygonSource;

        if (value.Contains("water") || value.Contains("river") || value.Contains("lake") || value.Contains("sea"))
            return "water";
        if (value.Contains("railway") || value.Contains("railroad") || value.Contains("rail line"))
            return "railway";
        if (value.Contains("highway") || value.Contains("motorway") || value.Contains("freeway"))
            return "highway";
        if (value.Contains("road") || value.Contains("street") || value.Contains("transport"))
            return "road";
        if (value.Contains("building") || value.Contains("built") || value.Contains("roof")
            || value.Contains("structure"))
            return "building";
        if (compactValue.Contains("suburban") || compactValue.Contains("periurban"))
            return "suburban";
        if (compactValue.Contains("denseurban") || value.Contains("high density"))
            return "dense urban";
        if (value.Contains("urban") || value.Contains("city") || value.Contains("residential"))
            return "urban";
        if (value.Contains("rural") || value.Contains("countryside"))
            return "rural";
        if (value.Contains("open land") || value.Contains("open area") || value == "open")
            return "open";
        if (value.Contains("green") || value.Contains("park") || value.Contains("garden"))
            return "green";
        if (value.Contains("vegetation") || value.Contains("forest") || value.Contains("wood")
            || value.Contains("crop") || value.Contains("grass") || value.Contains("shrub"))
            return "vegetation";

        // A save polygon is the building geometry source in this endpoint.
        if (string.Equals(polygonSource, "tbl_savepolygon", StringComparison.OrdinalIgnoreCase))
            return "building";

        source = "unmapped";
        return raw;
    }

    private static readonly SemaphoreSlim ClutterSpatialSetupLock = new(1, 1);
    private static volatile bool ClutterSpatialSetupComplete;

    private async Task<bool> EnsureClutterSpatialSupportAsync(System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (ClutterSpatialSetupComplete) return true;
        await ClutterSpatialSetupLock.WaitAsync(cancellationToken);
        try
        {
            if (ClutterSpatialSetupComplete) return true;

            async Task<object?> ScalarAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 600;
                return await command.ExecuteScalarAsync(cancellationToken);
            }

            try
            {
                var column = await ScalarAsync(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'tbl_project_clutter_tile'
  AND COLUMN_NAME = 'geometry_geom';");
                if (Convert.ToInt32(column, System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    await using var addColumn = connection.CreateCommand();
                    addColumn.CommandText = "ALTER TABLE tbl_project_clutter_tile ADD COLUMN geometry_geom GEOMETRY NULL;";
                    addColumn.CommandTimeout = 600;
                    await addColumn.ExecuteNonQueryAsync(cancellationToken);
                }

                // The primary-key predicate keeps this update compatible with
                // MySQL safe-update mode. It runs only once for old rows.
                await using (var fill = connection.CreateCommand())
                {
                    fill.CommandText = @"
UPDATE tbl_project_clutter_tile
SET geometry_geom = ST_GeomFromText(geometry_wkt, 0)
WHERE id > 0 AND geometry_geom IS NULL AND geometry_wkt IS NOT NULL;";
                    fill.CommandTimeout = 600;
                    await fill.ExecuteNonQueryAsync(cancellationToken);
                }

                var missing = await ScalarAsync(@"
SELECT COUNT(*) FROM tbl_project_clutter_tile
WHERE geometry_wkt IS NOT NULL AND geometry_geom IS NULL;");
                if (Convert.ToInt64(missing, System.Globalization.CultureInfo.InvariantCulture) != 0)
                    return false;

                await using (var notNull = connection.CreateCommand())
                {
                    notNull.CommandText = "ALTER TABLE tbl_project_clutter_tile MODIFY COLUMN geometry_geom GEOMETRY NOT NULL;";
                    notNull.CommandTimeout = 600;
                    await notNull.ExecuteNonQueryAsync(cancellationToken);
                }

                var spatialIndex = await ScalarAsync(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'tbl_project_clutter_tile'
  AND INDEX_NAME = 'sx_clutter_geometry';");
                if (Convert.ToInt32(spatialIndex, System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    await using var addIndex = connection.CreateCommand();
                    addIndex.CommandText = "ALTER TABLE tbl_project_clutter_tile ADD SPATIAL INDEX sx_clutter_geometry (geometry_geom);";
                    addIndex.CommandTimeout = 600;
                    await addIndex.ExecuteNonQueryAsync(cancellationToken);
                }

                ClutterSpatialSetupComplete = true;
                return true;
            }
            catch
            {
                // Keep the endpoint usable if an old row has malformed WKT or
                // the database user cannot perform schema maintenance.
                return false;
            }
        }
        finally
        {
            ClutterSpatialSetupLock.Release();
        }
    }

    /// <summary>
    /// Returns only clutter tiles that spatially overlap the project's building
    /// polygons. Geometry is returned as WKT so the existing map clients can use it.
    /// </summary>
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

        var polygonFilter = buildingPolygonId.HasValue ? "AND sp.id = @buildingPolygonId" : "";
        var mapRegionFilter = buildingPolygonId.HasValue ? "AND mr.id = @buildingPolygonId" : "";
        var projectPolygonFilter = buildingPolygonId.HasValue ? "AND 1 = 0" : "";
        var buildingPolygons = new List<(long Id, string? Name, string Source, string Wkt, NtsGeometry Geometry)>();
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

        await using (var buildingCommand = connection.CreateCommand())
        {
            buildingCommand.CommandTimeout = 30;
            Add(buildingCommand, "@projectId", projectId);
            if (buildingPolygonId.HasValue)
                Add(buildingCommand, "@buildingPolygonId", buildingPolygonId.Value);
            buildingCommand.CommandText = $@"
SELECT sp.id, CONVERT(sp.name USING utf8mb4), 'tbl_savepolygon', ST_AsText(sp.region)
FROM tbl_savepolygon sp
WHERE sp.project_id = @projectId AND sp.region IS NOT NULL {polygonFilter}
UNION ALL
SELECT mr.id, CAST('Project region' AS CHAR CHARACTER SET utf8mb4), 'map_regions', ST_AsText(mr.region)
FROM map_regions mr
WHERE mr.tbl_project_id = @projectId AND mr.region IS NOT NULL {mapRegionFilter}
  AND NOT EXISTS (SELECT 1 FROM tbl_savepolygon sp3 WHERE sp3.project_id = @projectId AND sp3.region IS NOT NULL)
UNION ALL
SELECT p.id, CONVERT(p.project_name USING utf8mb4), 'tbl_project', ST_AsText(p.polygon)
FROM tbl_project p
WHERE p.id = @projectId AND p.polygon IS NOT NULL {projectPolygonFilter}
  AND NOT EXISTS (SELECT 1 FROM tbl_savepolygon sp4 WHERE sp4.project_id = @projectId AND sp4.region IS NOT NULL)
  AND NOT EXISTS (SELECT 1 FROM map_regions mr2 WHERE mr2.tbl_project_id = @projectId AND mr2.region IS NOT NULL);";

            var wktReader = new WKTReader();
            await using var buildingReader = await buildingCommand.ExecuteReaderAsync(cancellationToken);
            while (await buildingReader.ReadAsync(cancellationToken))
            {
                if (buildingReader.IsDBNull(3)) continue;
                try
                {
                    var wkt = ReadTextValue(buildingReader, 3);
                    if (string.IsNullOrWhiteSpace(wkt)) continue;
                    var geometry = wktReader.Read(wkt);
                    if (LooksLikeLatitudeLongitude(geometry.EnvelopeInternal))
                    {
                        geometry.Apply(new SwapCoordinateAxesFilter());
                        wkt = new WKTWriter().Write(geometry);
                    }
                    buildingPolygons.Add((buildingReader.GetInt64(0), ReadTextValue(buildingReader, 1),
                        ReadTextValue(buildingReader, 2) ?? "unknown", wkt, geometry));
                }
                catch (Exception) { /* Skip malformed building geometry instead of failing the whole API. */ }
            }
        }

        var rows = new List<object>();
        var returnedBuildingIds = new HashSet<long>();
        var matchedBeforePage = 0;
        var hasMore = false;
        var candidateTileCount = 0;
        var tileBounds = new Envelope();
        foreach (var building in buildingPolygons)
            tileBounds.ExpandToInclude(building.Geometry.EnvelopeInternal);
        var buildingBounds = tileBounds;
        tileBounds = new Envelope();
        if (buildingPolygons.Count > 0)
        {
            // Use an in-memory spatial index for the building polygons. The
            // imported native geometry column is not reliable for older rows,
            // so relying on its MBR can incorrectly return zero candidates.
            var buildingIndex = new STRtree<(long Id, string? Name, string Source, string Wkt, NtsGeometry Geometry)>();
            foreach (var building in buildingPolygons)
                buildingIndex.Insert(building.Geometry.EnvelopeInternal, building);
            buildingIndex.Build();

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 60;
            Add(command, "@projectId", projectId);
            command.CommandText = @"
SELECT id, grid_id, clutter_class, land_cover_class, resolution_m, geometry_wkt
FROM tbl_project_clutter_tile
WHERE project_id = @projectId AND geometry_wkt IS NOT NULL
ORDER BY id;";

            // A single response containing tens of thousands of WKT polygons
            // can exhaust browser memory. Callers can request subsequent pages.
            var maxRows = Math.Clamp(limit, 1, 5000);
            var tileReader = new WKTReader();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidateTileCount++;
                if (reader.IsDBNull(5)) continue;
                NtsGeometry tileGeometry;
                string tileWkt;
                try
                {
                    tileWkt = ReadTextValue(reader, 5) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(tileWkt)) continue;
                    tileGeometry = tileReader.Read(tileWkt);
                    tileBounds.ExpandToInclude(tileGeometry.EnvelopeInternal);
                }
                catch (Exception) { continue; }

                foreach (var building in buildingIndex.Query(tileGeometry.EnvelopeInternal))
                {
                    if (!building.Geometry.EnvelopeInternal.Intersects(tileGeometry.EnvelopeInternal)
                        || !building.Geometry.Intersects(tileGeometry)) continue;

                    if (matchedBeforePage++ < offset) continue;
                    if (rows.Count >= maxRows)
                    {
                        hasMore = true;
                        break;
                    }

                    var rawClutterClass = ReadTextValue(reader, 2);
                    var rawLandCoverClass = ReadTextValue(reader, 3);
                    var normalizedClass = NormalizeClutterClass(rawClutterClass, rawLandCoverClass,
                        building.Name, building.Source, out var classSource);

                    rows.Add(new
                    {
                        buildingPolygonId = building.Id,
                        buildingPolygonName = building.Name,
                        buildingPolygonSource = building.Source,
                        clutterTileId = reader.GetInt64(0),
                        clusterTile = ReadTextValue(reader, 1),
                        gridId = ReadTextValue(reader, 1),
                        clutterClass = normalizedClass,
                        clutterClassSource = classSource,
                        rawClutterClass,
                        landCoverClass = rawLandCoverClass,
                        resolutionM = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4),
                        clutterPolygonWkt = tileWkt
                    });
                    returnedBuildingIds.Add(building.Id);
                }
                if (hasMore) break;
            }
        }

        return Ok(new
        {
            status = 1,
            projectId,
            buildingPolygonId,
            database = connection.Database,
            buildingPolygonCount = buildingPolygons.Count,
            candidateTileCount,
            buildingBounds = new { minX = buildingBounds.MinX, minY = buildingBounds.MinY, maxX = buildingBounds.MaxX, maxY = buildingBounds.MaxY },
            tileBounds = new { minX = tileBounds.MinX, minY = tileBounds.MinY, maxX = tileBounds.MaxX, maxY = tileBounds.MaxY },
            matchedCount = rows.Count,
            offset,
            limit = Math.Clamp(limit, 1, 5000),
            hasMore,
            nextOffset = offset + rows.Count,
            // Send each building geometry once. Repeating a large building WKT
            // on every intersecting tile can exhaust the browser memory.
            buildingPolygons = buildingPolygons.Select(building => new
            {
                buildingPolygonId = building.Id,
                buildingPolygonName = building.Name,
                buildingPolygonSource = building.Source,
                buildingPolygonWkt = building.Wkt
            }).Where(building => returnedBuildingIds.Contains(building.buildingPolygonId)).ToArray(),
            data = rows
        });
    }
}
