using Microsoft.AspNetCore.Authorization;

namespace SignalTracker.Security;

public static class SecurityPolicies
{
    public const string SuperAdmin = "SuperAdmin";
    public const string CompanyAdmin = "CompanyAdmin";

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(CompanyAdmin, policy => policy.RequireAuthenticatedUser()
            .RequireAssertion(context => ResourceAccess.IsSuperAdmin(context.User)
                || (ResourceAccess.UserId(context.User) > 0 && ResourceAccess.CompanyId(context.User) > 0
                    && context.User.HasClaim("UserTypeId", "2"))));
        // Both supported login flows issue this canonical user-type claim.
        // Global operations must not rely on a controller name or a UI restriction.
        options.AddPolicy(SuperAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("UserTypeId", "3"));
    }
}
