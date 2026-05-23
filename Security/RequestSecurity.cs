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
        if (context.Request.IsHttps) return true;

        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
        return string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase);
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
        options.Secure = usesHttps;
    }
}


