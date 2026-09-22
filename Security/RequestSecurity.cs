using Microsoft.AspNetCore.Http;

namespace SignalTracker.Security;

public static class RequestSecurity
{
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLoopbackOrigin(Uri uri)
    {
        return uri.IsAbsoluteUri
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && IsLoopbackHost(uri.Host);
    }

    public static bool RequestUsesHttps(HttpContext context)
    {
        // ForwardedHeadersMiddleware updates the scheme only for trusted proxies.
        // Reading the raw header here would bypass that trust boundary.
        return context.Request.IsHttps;
    }

    public static void ApplyPerRequestCookieSettings(HttpContext context, CookieOptions options)
    {
        var usesHttps = RequestUsesHttps(context);

        if (!IsLoopbackHost(context.Request.Host.Host) && usesHttps)
        {
            options.SameSite = SameSiteMode.None;
            options.Secure = true;
            return;
        }

        options.SameSite = SameSiteMode.Lax;
        // Never downgrade a Secure cookie required by production configuration.
        options.Secure = options.Secure || usesHttps;
    }
}


