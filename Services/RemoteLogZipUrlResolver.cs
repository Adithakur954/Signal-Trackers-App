using System.Globalization;
using SignalTracker.Security;

namespace SignalTracker.Services;

public static class RemoteLogZipUrlResolver
{
    private const string IndiaTemplateKey = "L3EventImport:RemoteLogZipUrlTemplate";
    private const string TaiwanTemplateKey = "L3EventImport:TaiwanRemoteLogZipUrlTemplate";

    public static string? BuildUrl(IConfiguration configuration, HttpContext? httpContext, int logId)
    {
        var template = ResolveTemplate(configuration, httpContext);
        return string.IsNullOrWhiteSpace(template)
            ? null
            : template.Replace("{logId}", logId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveTemplate(IConfiguration configuration, HttpContext? httpContext)
    {
        var region = ResolveRegion(httpContext);
        if (string.Equals(region, "TW", StringComparison.OrdinalIgnoreCase))
        {
            var taiwanTemplate = configuration[TaiwanTemplateKey];
            if (!string.IsNullOrWhiteSpace(taiwanTemplate))
                return taiwanTemplate;
        }

        return configuration[IndiaTemplateKey];
    }

    private static string? ResolveRegion(HttpContext? httpContext)
    {
        if (httpContext == null) return null;

        var fromUser = RegionAccess.Normalize(httpContext.User.FindFirst("country_code")?.Value);
        if (!string.IsNullOrWhiteSpace(fromUser))
            return fromUser;

        foreach (var key in new[] { "country_code", "countryCode", "region" })
        {
            if (httpContext.Request.Query.TryGetValue(key, out var values))
            {
                var fromQuery = RegionAccess.Normalize(values.FirstOrDefault());
                if (!string.IsNullOrWhiteSpace(fromQuery))
                    return fromQuery;
            }
        }

        if (httpContext.Request.Headers.TryGetValue("x-country-code", out var headers))
        {
            var fromHeader = RegionAccess.Normalize(headers.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(fromHeader))
                return fromHeader;
        }

        return httpContext.Session == null
            ? null
            : RegionAccess.Normalize(httpContext.Session.GetString("country_code"));
    }
}
