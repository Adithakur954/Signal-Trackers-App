using SignalTracker.Security;
using System.Diagnostics;
using System.Globalization;

namespace SignalTracker.Middleware;

public sealed class SessionValidationUnavailableMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestWatch = Stopwatch.StartNew();
        context.Response.OnStarting(() =>
        {
            var timings = new List<string>();
            var existing = context.Response.Headers["Server-Timing"].ToString();
            if (!string.IsNullOrWhiteSpace(existing)) timings.Add(existing);
            if (context.Items["st.authentication.ms"] is double authMs)
                timings.Add("authentication;dur=" + authMs.ToString("F1", CultureInfo.InvariantCulture));
            timings.Add("request;dur=" + requestWatch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
            context.Response.Headers["Server-Timing"] = string.Join(", ", timings);
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
        }
        catch (SessionValidationUnavailableException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Session verification is temporarily unavailable. Please retry.",
                retryable = true
            });
        }
    }
}
