using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace SignalTracker.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PublicApiKeyAttribute : Attribute, IAuthorizationFilter
{
    private const string HeaderName = "X-Public-Api-Key";
    private const string AlternateHeaderName = "X-API-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var environment = context.HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var configuredKey = configuration["Security:PublicApiKey"];

        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (environment.IsDevelopment())
            {
                return;
            }

            context.Result = new ObjectResult(new
            {
                Status = 0,
                Message = "Public API key is not configured."
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
            Message = "Invalid or missing API key."
        });
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


