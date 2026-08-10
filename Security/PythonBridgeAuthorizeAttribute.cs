using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace SignalTracker.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PythonBridgeAuthorizeAttribute : Attribute, IAuthorizationFilter
{
    private const string HeaderName = "X-Python-Bridge-Key";
    private const string AlternateHeaderName = "X-API-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var environment = context.HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var configuredKey =
            configuration["PythonBridge:ApiKey"]
            ?? Environment.GetEnvironmentVariable("PYTHON_BRIDGE_API_KEY")
            ?? Environment.GetEnvironmentVariable("SIGNAL_TRACKERS_BRIDGE_KEY");

        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (environment.IsDevelopment() && IsLoopbackRequest(context))
            {
                return;
            }

            context.Result = new ObjectResult(new
            {
                Status = 0,
                Message = "Python bridge access is not configured."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        if (HeaderMatches(context, HeaderName, configuredKey) ||
            HeaderMatches(context, AlternateHeaderName, configuredKey))
        {
            return;
        }

        context.Result = new UnauthorizedObjectResult(new
        {
            Status = 0,
            Message = "Invalid or missing bridge key."
        });
    }

    private static bool IsLoopbackRequest(AuthorizationFilterContext context)
    {
        var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
        return remoteIp != null && System.Net.IPAddress.IsLoopback(remoteIp);
    }

    private static bool HeaderMatches(AuthorizationFilterContext context, string headerName, string configuredKey)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(headerName, out StringValues values))
        {
            return false;
        }

        return values.Any(value => string.Equals(value, configuredKey, StringComparison.Ordinal));
    }
}
