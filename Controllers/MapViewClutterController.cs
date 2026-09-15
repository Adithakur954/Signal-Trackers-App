using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace SignalTracker.Controllers;

public partial class MapViewController
{
    /// <summary>
    /// Returns only clutter tiles that spatially overlap the project's building
    /// polygons. Geometry is returned as WKT so the existing map clients can use it.
    /// </summary>
    [HttpGet("GetProjectBuildingClutterTiles")]
    public async Task<IActionResult> GetProjectBuildingClutterTiles(
        [FromQuery] long projectId,
        [FromQuery] long? buildingPolygonId = null,
        [FromQuery] int limit = 50000,
        CancellationToken cancellationToken = default)
    {
        if (projectId <= 0)
            return BadRequest(new { status = 0, message = "projectId must be positive." });
        if (buildingPolygonId is <= 0)
            return BadRequest(new { status = 0, message = "buildingPolygonId must be positive when supplied." });

        var isSuperAdmin = _userScope.IsSuperAdmin(User);
        var targetCompanyId = GetTargetCompanyId(null);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using (var access = connection.CreateCommand())
        {
            access.CommandText = "SELECT company_id FROM tbl_project WHERE id = @projectId LIMIT 1;";
            Add(access, "@projectId", projectId);
            var company = await access.ExecuteScalarAsync(cancellationToken);
            if (company == null || company == DBNull.Value)
                return NotFound(new { status = 0, message = "Project not found." });

            var projectCompanyId = Convert.ToInt32(company, System.Globalization.CultureInfo.InvariantCulture);
            if (!isSuperAdmin && projectCompanyId != targetCompanyId)
                return Forbid();
        }

        var rows = new List<object>();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        Add(command, "@projectId", projectId);
        Add(command, "@limit", Math.Clamp(limit, 1, 50000));

        var polygonFilter = buildingPolygonId.HasValue ? "AND sp.id = @buildingPolygonId" : "";
        if (buildingPolygonId.HasValue)
            Add(command, "@buildingPolygonId", buildingPolygonId.Value);

        // Saved polygons are the project's building polygons. If none exist,
        // use the project boundary as a single polygon instead.
        command.CommandText = $@"
WITH building_polygons AS (
    SELECT
        sp.id AS building_polygon_id,
        sp.name AS building_polygon_name,
        'tbl_savepolygon' AS building_polygon_source,
        ST_GeomFromText(ST_AsText(sp.region), 4326) AS building_geometry
    FROM tbl_savepolygon sp
    WHERE sp.project_id = @projectId
      AND sp.region IS NOT NULL
      {polygonFilter}
    UNION ALL
    SELECT
        p.id AS building_polygon_id,
        p.project_name AS building_polygon_name,
        'tbl_project' AS building_polygon_source,
        ST_GeomFromText(ST_AsText(p.polygon), 4326) AS building_geometry
    FROM tbl_project p
    WHERE p.id = @projectId
      AND p.polygon IS NOT NULL
      AND NOT EXISTS (
          SELECT 1 FROM tbl_savepolygon sp2
          WHERE sp2.project_id = @projectId AND sp2.region IS NOT NULL
      )
)
SELECT
    b.building_polygon_id,
    b.building_polygon_name,
    b.building_polygon_source,
    ST_AsText(b.building_geometry) AS building_polygon_wkt,
    c.id AS clutter_tile_id,
    c.grid_id AS cluster_tile,
    c.grid_id,
    c.clutter_class,
    c.land_cover_class,
    c.resolution_m,
    c.geometry_wkt AS clutter_polygon_wkt
FROM building_polygons b
INNER JOIN tbl_project_clutter_tile c
    ON c.project_id = @projectId
   AND c.is_active = 1
   AND c.geometry_wkt IS NOT NULL
   AND ST_IsValid(ST_GeomFromText(c.geometry_wkt, 4326)) = 1
   AND ST_Intersects(b.building_geometry, ST_GeomFromText(c.geometry_wkt, 4326))
ORDER BY b.building_polygon_id, c.id
LIMIT @limit;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            object? Value(int index) => reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(new
            {
                buildingPolygonId = Convert.ToInt64(Value(0), System.Globalization.CultureInfo.InvariantCulture),
                buildingPolygonName = Value(1)?.ToString(),
                buildingPolygonSource = Value(2)?.ToString(),
                buildingPolygonWkt = Value(3)?.ToString(),
                clutterTileId = Convert.ToInt64(Value(4), System.Globalization.CultureInfo.InvariantCulture),
                clusterTile = Value(5)?.ToString(),
                gridId = Value(6)?.ToString(),
                clutterClass = Value(7)?.ToString(),
                landCoverClass = Value(8)?.ToString(),
                resolutionM = Value(9) == null
                    ? (decimal?)null
                    : Convert.ToDecimal(Value(9), System.Globalization.CultureInfo.InvariantCulture),
                clutterPolygonWkt = Value(10)?.ToString()
            });
        }

        return Ok(new
        {
            status = 1,
            projectId,
            buildingPolygonId,
            matchedCount = rows.Count,
            data = rows
        });
    }
}
