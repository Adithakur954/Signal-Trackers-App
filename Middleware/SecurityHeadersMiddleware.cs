namespace SignalTracker.Middleware;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _environment;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment)
    {
        _next = next;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        headers.TryAdd("Cross-Origin-Resource-Policy", "same-origin");
        headers.TryAdd("X-Permitted-Cross-Domain-Policies", "none");
        headers.TryAdd("X-XSS-Protection", "0");
        headers.TryAdd("Content-Security-Policy", "base-uri 'self'; object-src 'none'; frame-ancestors 'self'");
        headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";

        if (!_environment.IsDevelopment())
        {
            context.Response.OnStarting(() =>
            {
                RemoveDiagnosticHeaders(context.Response.Headers);
                return Task.CompletedTask;
            });
        }

        await _next(context);
    }

    private static void RemoveDiagnosticHeaders(IHeaderDictionary headers)
    {
        var names = headers.Keys
            .Where(name =>
                name.StartsWith("X-Cache", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Debug", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Db-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Database", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Total", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Row", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Record", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Sample", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in names)
        {
            headers.Remove(name);
        }
    }
}


