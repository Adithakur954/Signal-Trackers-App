using Microsoft.Extensions.Primitives;

namespace SignalTracker.Middleware;

public sealed class CookiePartitionMiddleware
{
    private readonly RequestDelegate _next;

    public CookiePartitionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            if (context.Response.Headers.TryGetValue("Set-Cookie", out var cookies))
            {
                var updated = cookies
                    .Select(cookie =>
                        cookie.Contains("SameSite=None", StringComparison.OrdinalIgnoreCase)
                        && cookie.Contains("Secure", StringComparison.OrdinalIgnoreCase)
                        && !cookie.Contains("Partitioned", StringComparison.OrdinalIgnoreCase)
                            ? cookie + "; Partitioned"
                            : cookie)
                    .ToArray();

                context.Response.Headers["Set-Cookie"] = new StringValues(updated);
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
