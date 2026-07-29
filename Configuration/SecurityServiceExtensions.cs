using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using SignalTracker.Security;
using SignalTracker.Models;

namespace SignalTracker.Configuration;

public static class SecurityServiceExtensions
{
    public const string CorsPolicyName = "AllowReactApp";
    private const string UserLoginLockKeyPrefix = "auth:login-lock:user:";

    public static void AddSignalTrackerCors(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var allowedOrigins = ResolveAllowedOrigins(configuration);
        var allowNullOrigin = configuration.GetValue<bool>("Security:AllowNullOrigin");
        var allowLoopbackOrigins = configuration.GetValue("Security:AllowLoopbackOrigins", environment.IsDevelopment());

        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicyName, policy =>
            {
                policy.SetIsOriginAllowed(origin =>
                    (allowNullOrigin && string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                    || allowedOrigins.Contains(origin)
                    || (allowLoopbackOrigins
                        && Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                        && RequestSecurity.IsLoopbackOrigin(originUri)))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    public static void AddSignalTrackerDataProtection(this IServiceCollection services, WebApplicationBuilder builder)
    {
        var keysPath = HostingConfiguration.ResolveDataProtectionKeysPath(builder);
        Directory.CreateDirectory(keysPath);

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
            .SetApplicationName("SignalTracker");
    }

    public static void AddSignalTrackerCookieAuth(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var sessionMinutes = Math.Clamp(configuration.GetValue("Security:SessionIdleMinutes", 60), 15, 300);
        var isDevelopment = environment.IsDevelopment();
        var cookieSameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.None;
        var cookieSecurePolicy = isDevelopment ? CookieSecurePolicy.None : CookieSecurePolicy.Always;

        services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromMinutes(sessionMinutes);
            options.Cookie.Name = "st.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = cookieSameSite;
            options.Cookie.SecurePolicy = cookieSecurePolicy;
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "st.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = cookieSameSite;
                options.Cookie.SecurePolicy = cookieSecurePolicy;
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionMinutes);
                options.SlidingExpiration = true;

                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };

                options.Events.OnSigningIn = ctx =>
                {
                    RequestSecurity.ApplyPerRequestCookieSettings(ctx.HttpContext, ctx.CookieOptions);
                    return Task.CompletedTask;
                };

                options.Events.OnValidatePrincipal = async ctx =>
                {
                    var userId = ctx.Principal?.FindFirst("UserId")?.Value;
                    var cookieLockValue = ctx.Principal?.FindFirst("LoginLockValue")?.Value;

                    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(cookieLockValue))
                    {
                        return;
                    }

                    var redis = ctx.HttpContext.RequestServices.GetService<RedisService>();
                    if (redis?.IsConnected != true)
                    {
                        return;
                    }

                    var currentLockValue = await redis.GetStringAsync($"{UserLoginLockKeyPrefix}{userId}");
                    if (!string.Equals(currentLockValue, cookieLockValue, StringComparison.Ordinal))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });

        services.AddAuthorization();

        services.Configure<CookiePolicyOptions>(options =>
        {
            options.MinimumSameSitePolicy = cookieSameSite;
            options.HttpOnly = HttpOnlyPolicy.Always;
            options.Secure = cookieSecurePolicy;
            options.OnAppendCookie = context => RequestSecurity.ApplyPerRequestCookieSettings(context.Context, context.CookieOptions);
            options.OnDeleteCookie = context => RequestSecurity.ApplyPerRequestCookieSettings(context.Context, context.CookieOptions);
        });
    }

    public static int? AddSignalTrackerForwardingAndHttps(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            var knownProxies = configuration.GetSection("Security:ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
            var knownNetworks = configuration.GetSection("Security:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();

            if (knownProxies.Length > 0)
            {
                options.KnownProxies.Clear();
                foreach (var proxy in knownProxies)
                {
                    if (System.Net.IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }
            }

            if (knownNetworks.Length > 0)
            {
                options.KnownNetworks.Clear();
                foreach (var network in knownNetworks)
                {
                    var parts = network.Split('/');
                    if (parts.Length == 2
                        && System.Net.IPAddress.TryParse(parts[0], out var networkAddress)
                        && int.TryParse(parts[1], out var prefixLength))
                    {
                        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(networkAddress, prefixLength));
                    }
                }
            }
        });

        var httpsRedirectionPort = HostingConfiguration.GetHttpsRedirectionPort(configuration);
        if (httpsRedirectionPort.HasValue)
        {
            services.AddHttpsRedirection(options => options.HttpsPort = httpsRedirectionPort.Value);
        }

        return httpsRedirectionPort;
    }

    private static HashSet<string> ResolveAllowedOrigins(IConfiguration configuration)
    {
        var configuredOrigins = configuration
            .GetSection("Security:AllowedOrigins")
            .Get<string[]>();

        var envOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var origins = configuredOrigins?.Length > 0
            ? configuredOrigins
            : envOrigins;

        origins ??=
        [
            "https://singnaltracker.netlify.app",
            "https://stracer.vinfocom.co.in",
            "https://s-traccceer.vinfocom.co.in"
        ];

        return origins
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}




