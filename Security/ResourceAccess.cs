using System.Security.Claims;
using SignalTracker.Models;

namespace SignalTracker.Security;

public static class ResourceAccess
{
    public static int UserId(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
        && int.TryParse(user.FindFirst("UserId")?.Value, out var id) && id > 0 ? id : 0;
    public static int CompanyId(ClaimsPrincipal user) => int.TryParse(user.FindFirst("CompanyId")?.Value, out var id)
        && id > 0 ? id : 0;
    public static bool IsSuperAdmin(ClaimsPrincipal user) => UserId(user) > 0 && user.HasClaim("UserTypeId", "3");

    public static IQueryable<tbl_project> Projects(IQueryable<tbl_project> projects, ClaimsPrincipal user)
    {
        var userId = UserId(user);
        if (userId == 0) return projects.Where(project => false);
        if (IsSuperAdmin(user)) return projects;
        var companyId = CompanyId(user);
        return projects.Where(project => (companyId > 0 && project.company_id == companyId)
            || ((project.company_id == null || project.company_id == 0) && project.created_by_user_id == userId));
    }

    public static IQueryable<tbl_indoor_planning_floor> IndoorPlans(
        IQueryable<tbl_indoor_planning_floor> plans, IQueryable<tbl_user> users, ClaimsPrincipal user)
    {
        var userId = UserId(user);
        if (userId == 0) return plans.Where(plan => false);
        if (IsSuperAdmin(user)) return plans;
        var companyId = CompanyId(user);
        if (companyId == 0) return plans.Where(plan => plan.created_by_user_id == userId);
        var companyUsers = users.Where(member => member.company_id == companyId).Select(member => member.id);
        return plans.Where(plan => plan.created_by_user_id.HasValue && companyUsers.Contains(plan.created_by_user_id.Value));
    }
}

public sealed class ResourceAccessDeniedException : Exception
{
    public ResourceAccessDeniedException() : base("Resource not found.") { }
}
