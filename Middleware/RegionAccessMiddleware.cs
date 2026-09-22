using SignalTracker.Security;

namespace SignalTracker.Middleware;

public sealed class RegionAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!RegionAccess.IsRequestAllowed(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { Status = 0, Message = "Region access denied. Sign in again if your region has changed." });
            return;
        }
        await next(context);
    }
}
