using System.Security.Claims;

namespace SignalTracker.Security;

public static class RegionAccess
{
    public static string? Normalize(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TW" or "TWN" or "TAIWAN" => "TW",
        "IN" or "IND" or "INDIA" => "IN",
        _ => null
    };

    public static bool IsRequestAllowed(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true || ResourceAccess.IsSuperAdmin(context.User)) return true;
        var trusted = Normalize(context.User.FindFirst("country_code")?.Value);
        if (trusted == null) return false;
        foreach (var key in new[] { "country_code", "countryCode", "region" })
            if (context.Request.Query.TryGetValue(key, out var values)
                && values.Any(value => Normalize(value) != trusted)) return false;
        return !context.Request.Headers.TryGetValue("x-country-code", out var headers)
            || headers.All(value => Normalize(value) == trusted);
    }
}
