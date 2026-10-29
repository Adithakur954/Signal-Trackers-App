using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SignalTracker.Models;
using SignalTracker.Services;

namespace SignalTracker.Security;

// Only uploader routes accept installation credentials. Reporting and administrator
// routes continue to use their existing, separate authentication mechanisms.
[AttributeUsage(AttributeTargets.Method)]
public sealed class RfLogUploaderAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var authorization = http.Request.Headers.Authorization;
        if (authorization.Count == 0)
        {
            new RfLogApiKeyAttribute().OnAuthorization(context);
            return;
        }
        http.Response.Headers.CacheControl = "no-store";
        if (!http.RequestServices.GetRequiredService<IOptions<RfLogIntegrationOptions>>().Value.IsConfigured)
        {
            context.Result = new ObjectResult(new { message = "RF log storage integration is not configured." }) { StatusCode = 503 };
            return;
        }
        if (authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Deny(context);
            return;
        }
        try
        {
            var service = http.RequestServices.GetRequiredService<RfMobileCredentialService>();
            var actor = await service.AuthenticateAsync(authorization[0]![7..], http.RequestAborted);
            if (actor == null) { Deny(context); return; }
            http.Items[RfLogApiKeyAttribute.ClientItem] = actor;
        }
        catch (Exception ex) when (ex is MySqlException or RfLogRequestException)
        {
            http.RequestServices.GetRequiredService<ILogger<RfLogUploaderAttribute>>()
                .LogError(ex, "RF mobile credential validation unavailable");
            context.Result = new ObjectResult(new { message = "RF mobile authorization is unavailable. Verify database configuration and schema migration." }) { StatusCode = 503 };
        }
    }

    private static void Deny(AuthorizationFilterContext context)
    {
        context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
        context.Result = new UnauthorizedObjectResult(new { message = "A valid RF mobile upload credential is required." });
    }
}
