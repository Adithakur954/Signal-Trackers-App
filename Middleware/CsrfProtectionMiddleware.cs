using SignalTracker.Security;

namespace SignalTracker.Middleware;

public sealed class CsrfProtectionMiddleware
{
    private const string CookieName = "st.csrf";
    private const string HeaderName = "X-CSRF-TOKEN";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public CsrfProtectionMiddleware(RequestDelegate next, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _next = next;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        EnsureCsrfCookie(context);

        if (ShouldValidate(context) && !HasValidToken(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                Status = 0,
                Message = "Missing or invalid CSRF token."
            });
            return;
        }

        await _next(context);
    }

    private bool ShouldValidate(HttpContext context)
    {
        if (!_configuration.GetValue<bool>("Security:RequireCsrfHeader")) return false;
        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)
            && !HttpMethods.IsDelete(context.Request.Method))
        {
            return false;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<PublicApiKeyAttribute>() != null) return false;
        return context.User?.Identity?.IsAuthenticated == true;
    }

    private static bool HasValidToken(HttpContext context)
    {
        var cookieToken = context.Request.Cookies[CookieName];
        var headerToken = context.Request.Headers[HeaderName].FirstOrDefault();

        return !string.IsNullOrWhiteSpace(cookieToken)
            && !string.IsNullOrWhiteSpace(headerToken)
            && string.Equals(cookieToken, headerToken, StringComparison.Ordinal);
    }

    private void EnsureCsrfCookie(HttpContext context)
    {
        if (context.Request.Cookies.ContainsKey(CookieName)) return;

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("=", string.Empty, StringComparison.Ordinal);

        var usesHttps = RequestSecurity.RequestUsesHttps(context);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = false,
            IsEssential = true,
            Secure = usesHttps,
            SameSite = _environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None,
            Path = "/"
        });
    }
}


