namespace SignalTracker.Middleware;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseSignalTrackerSecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    public static IApplicationBuilder UsePartitionedCookieSupport(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CookiePartitionMiddleware>();
    }
}


