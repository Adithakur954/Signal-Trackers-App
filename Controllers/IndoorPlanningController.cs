using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class IndoorPlanningController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public IndoorPlanningController(ApplicationDbContext db)
        {
            _db = db;
        }

        [HttpGet("projects")]
        public async Task<IActionResult> GetProjects()
        {
            var projects = await _db.tbl_indoor_planning_floor
                .OrderByDescending(x => x.updated_at)
                .Select(x => new IndoorPlanningProjectDto
                {
                    Id = x.id,
                    Name = x.project_name,
                    FloorName = x.floor_name,
                    CreatedAt = x.created_at,
                    UpdatedAt = x.updated_at,
                })
                .ToListAsync();

            return Ok(projects);
        }

        [HttpGet("projects/{id:int}")]
        public async Task<IActionResult> GetProject(int id)
        {
            var project = await _db.tbl_indoor_planning_floor
                .Where(x => x.id == id)
                .Select(x => new IndoorPlanningProjectDto
                {
                    Id = x.id,
                    Name = x.project_name,
                    FloorName = x.floor_name,
                    PlanJson = x.plan_json,
                    CreatedAt = x.created_at,
                    UpdatedAt = x.updated_at,
                })
                .FirstOrDefaultAsync();

            return project == null ? NotFound(new { message = "Indoor planning project not found." }) : Ok(project);
        }

        [HttpPost("projects")]
        public async Task<IActionResult> CreateProject([FromBody] CreateIndoorPlanningProjectRequest request)
        {
            var name = string.IsNullOrWhiteSpace(request?.Name)
                ? $"Indoor Planning {DateTime.Now:dd MMM yyyy}"
                : request.Name.Trim();

            var now = DateTime.UtcNow;
            var project = new tbl_indoor_planning_floor
            {
                project_name = name,
                floor_name = string.IsNullOrWhiteSpace(request?.FloorName) ? "Level 1" : request.FloorName.Trim(),
                plan_json = string.IsNullOrWhiteSpace(request?.PlanJson) ? DefaultPlanJson : request.PlanJson,
                created_at = now,
                updated_at = now,
            };

            _db.tbl_indoor_planning_floor.Add(project);
            await _db.SaveChangesAsync();

            return Ok(new IndoorPlanningProjectDto
            {
                Id = project.id,
                Name = project.project_name,
                FloorName = project.floor_name,
                PlanJson = project.plan_json,
                CreatedAt = project.created_at,
                UpdatedAt = project.updated_at,
            });
        }

        [HttpPut("projects/{id:int}/floor")]
        public async Task<IActionResult> SaveFloor(int id, [FromBody] SaveIndoorPlanningFloorRequest request)
        {
            var project = await _db.tbl_indoor_planning_floor.FirstOrDefaultAsync(x => x.id == id);
            if (project == null) return NotFound(new { message = "Indoor planning project not found." });

            if (!string.IsNullOrWhiteSpace(request?.Name)) project.project_name = request.Name.Trim();
            if (!string.IsNullOrWhiteSpace(request?.FloorName)) project.floor_name = request.FloorName.Trim();
            project.plan_json = request?.PlanJson ?? project.plan_json;
            project.updated_at = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Floor saved.", id = project.id });
        }

        private const string DefaultPlanJson = "{\"siteName\":\"Default Indoor Project\",\"selectedFloorId\":\"level-1\",\"rooms\":[],\"doors\":[],\"windows\":[],\"sites\":[],\"wifiPoints\":[],\"furniture\":[]}";
    }

    public class CreateIndoorPlanningProjectRequest
    {
        public string? Name { get; set; }
        public string? FloorName { get; set; }
        public string? PlanJson { get; set; }
    }

    public class SaveIndoorPlanningFloorRequest
    {
        public string? Name { get; set; }
        public string? FloorName { get; set; }
        public string? PlanJson { get; set; }
    }

    public class IndoorPlanningProjectDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? FloorName { get; set; }
        public string? PlanJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
