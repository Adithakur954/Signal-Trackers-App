using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using StackExchange.Redis;
using SignalTracker.Models;
using SignalTracker.Services;

internal class Program
{
    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequestUsesHttps(HttpContext context)
    {
        if (context.Request.IsHttps) return true;

        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
        return string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDataProtectionKeysPath(WebApplicationBuilder builder)
    {
        var configuredPath = builder.Configuration["DataProtection:KeysPath"]
                             ?? Environment.GetEnvironmentVariable("DATAPROTECTION_KEYS_PATH");

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // Expands environment variables like %LOCALAPPDATA% to avoid literal relative path bugs in IIS
            configuredPath = Environment.ExpandEnvironmentVariables(configuredPath);
        }
        else
        {
            if (builder.Environment.IsDevelopment())
            {
                configuredPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
            }
            else
            {
                // Fallback specifically for production environment running on IIS
                configuredPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                    "SignalTracker", 
                    "DataProtectionKeys"
                );
            }
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredPath));
    }

    private static int? GetHttpsRedirectionPort(IConfiguration configuration)
    {
        var configuredPort = configuration.GetValue<int?>("HttpsRedirection:HttpsPort");
        if (configuredPort.HasValue) return configuredPort;

        var envPort = Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT")
                      ?? Environment.GetEnvironmentVariable("HTTPS_PORT");

        return int.TryParse(envPort, out var parsedPort) ? parsedPort : null;
    }

    private static void ApplyPerRequestCookieSettings(HttpContext context, CookieOptions options)
    {
        var requestHost = context.Request.Host.Host;
        var isLoopbackHost = IsLoopbackHost(requestHost);
        var usesHttps = RequestUsesHttps(context);

        if (!isLoopbackHost && usesHttps)
        {
            options.SameSite = SameSiteMode.None;
            options.Secure = true;
            return;
        }

        options.SameSite = SameSiteMode.Lax;
        options.Secure = usesHttps;
    }

    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var isDevelopment = builder.Environment.IsDevelopment();
        var cookieSameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.None;
        var cookieSecurePolicy = isDevelopment ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        var allowedExactOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://singnaltracker.netlify.app",
            "https://stracer.vinfocom.co.in",
            "https://s-traccceer.vinfocom.co.in"
        };

        static bool IsLoopbackElectronOrigin(Uri uri)
        {
            if (!uri.IsAbsoluteUri) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;

            var host = uri.Host;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase);
        }

        // ----------------------------------------------------
        // CONTROLLERS & JSON
        // ----------------------------------------------------
        builder.Services.AddScoped<UserScopeService>();
        builder.Services.AddScoped<LicenseFeatureService>();
        builder.Services.AddScoped<PythonBridgeService>();
        builder.Services.AddScoped<IOtpService, OtpService>();
        builder.Services.AddScoped<IUserDeletionService, UserDeletionService>();
        builder.Services.AddHttpClient<ISmsService, SmsService>();

        if (builder.Configuration.GetValue<bool>("UserDeletionCleanup:Enabled"))
        {
            builder.Services.AddHostedService<UserDeletionCleanupService>();
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddControllersWithViews()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = null;
            });

        // ----------------------------------------------------
        // CORS (React)
        // ----------------------------------------------------
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowReactApp", policy =>
            {
                policy.SetIsOriginAllowed(origin =>
                    string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase)
                    || allowedExactOrigins.Contains(origin)
                    || (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && IsLoopbackElectronOrigin(originUri)))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });

        // ----------------------------------------------------
        // DATABASE (DYNAMIC SELECTION)
        // ----------------------------------------------------
        builder.Services.AddScoped<IDbConnectionProvider, DbConnectionProvider>();

        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var connectionProvider = sp.GetRequiredService<IDbConnectionProvider>();
            var connectionString = connectionProvider.GetConnectionString();
            var serverVersion = new MySqlServerVersion(new Version(8, 0, 29));

            options.UseMySql(connectionString, serverVersion);
        });

        Console.WriteLine("✅ Dynamic Database Provider configured");

        // ----------------------------------------------------
        // DATA PROTECTION
        // ----------------------------------------------------
        var dpKeysPath = ResolveDataProtectionKeysPath(builder);
        Directory.CreateDirectory(dpKeysPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
            .SetApplicationName("SignalTracker");

        // Ensure the upload root exists both at runtime and in the deployed app.
        var uploadedExcelsPath = Path.Combine(builder.Environment.ContentRootPath, "UploadedExcels");
        Directory.CreateDirectory(uploadedExcelsPath);

        // ----------------------------------------------------
        // REDIS CONFIGURATION (Safe + Fallback)
        // ----------------------------------------------------
        var redisConnString = builder.Configuration.GetConnectionString("Redis");

        if (string.IsNullOrWhiteSpace(redisConnString))
        {
            Console.WriteLine("⚠️ Redis not configured. Using in-memory cache.");
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton(_ => new RedisService(null));
        }
        else
        {
            try
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnString, true);
                redisOptions.AbortOnConnectFail = false;
                redisOptions.ConnectTimeout = 15000;
                redisOptions.SyncTimeout = 15000;
                redisOptions.AsyncTimeout = 15000;
                redisOptions.ConnectRetry = 5;
                redisOptions.KeepAlive = 15;
                redisOptions.ConfigCheckSeconds = 15;
                redisOptions.ReconnectRetryPolicy = new ExponentialRetry(2000);
 
                builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
                {
                    var mux = ConnectionMultiplexer.Connect(redisOptions);

                    mux.ConnectionFailed += (_, e) =>
                        Console.WriteLine($"❌ Redis connection failed: {e.Exception?.Message}");

                    mux.ConnectionRestored += (_, _) =>
                        Console.WriteLine("✅ Redis connection restored");

                    Console.WriteLine(mux.IsConnected
                        ? "✅ Redis connected"
                        : "⚠️ Redis client created, but Redis is not connected yet");
                    return mux;
                });

                builder.Services.AddSingleton(sp =>
                {
                    var mux = sp.GetRequiredService<IConnectionMultiplexer>();
                    return new RedisService(mux);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Redis failed: {ex.Message}");
                builder.Services.AddDistributedMemoryCache();
                builder.Services.AddSingleton(_ => new RedisService(null));
            }
        }

        // ----------------------------------------------------
        // SESSION
        // ----------------------------------------------------
        builder.Services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromMinutes(300);
            options.Cookie.Name = "st.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = cookieSameSite;
            options.Cookie.SecurePolicy = cookieSecurePolicy;
        });

        // ----------------------------------------------------
        // AUTHENTICATION
        // ----------------------------------------------------
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "st.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = cookieSameSite;
                options.Cookie.SecurePolicy = cookieSecurePolicy;
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(300);
                options.SlidingExpiration = true;

                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = 403;
                    return Task.CompletedTask;
                };

                options.Events.OnSigningIn = ctx =>
                {
                    ApplyPerRequestCookieSettings(ctx.HttpContext, ctx.CookieOptions);
                    return Task.CompletedTask;
                };
            });

        builder.Services.AddAuthorization();

        // ----------------------------------------------------
        // LARGE FILE UPLOADS (500MB)
        // ----------------------------------------------------
        builder.Services.Configure<FormOptions>(o =>
        {
            o.ValueLengthLimit = int.MaxValue;
            o.MultipartBodyLengthLimit = 524288000;
            o.MultipartHeadersLengthLimit = int.MaxValue;
        });

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 524288000;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(20);
        });

        // ----------------------------------------------------
        // COOKIE POLICY
        // ----------------------------------------------------
        builder.Services.Configure<CookiePolicyOptions>(o =>
        {
            o.MinimumSameSitePolicy = cookieSameSite;
            o.HttpOnly = HttpOnlyPolicy.Always;
            o.Secure = cookieSecurePolicy;
            o.OnAppendCookie = context => ApplyPerRequestCookieSettings(context.Context, context.CookieOptions);
            o.OnDeleteCookie = context => ApplyPerRequestCookieSettings(context.Context, context.CookieOptions);
        });

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        var httpsRedirectionPort = GetHttpsRedirectionPort(builder.Configuration);
        if (httpsRedirectionPort.HasValue)
        {
            builder.Services.AddHttpsRedirection(options =>
            {
                options.HttpsPort = httpsRedirectionPort.Value;
            });
        }

        // ----------------------------------------------------
        // BUILD APP
        // ----------------------------------------------------
        var app = builder.Build();

        // ----------------------------------------------------
        // MIDDLEWARE PIPELINE
        // ----------------------------------------------------

        // Disable caching
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await next();
        });

        // Exception handling
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
            app.UseHsts();
        }

        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment() || httpsRedirectionPort.HasValue)
        {
            app.UseHttpsRedirection();
        }

        app.UseStaticFiles();
        app.UseRouting();
        app.UseCors("AllowReactApp");
        app.UseCookiePolicy();
        app.UseSession();

        // Keep session alive
        app.Use(async (ctx, next) =>
        {
            ctx.Session.Set("st.pulse", BitConverter.GetBytes(DateTime.UtcNow.Ticks));
            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        // Add Partitioned flag to cookies
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                if (ctx.Response.Headers.TryGetValue("Set-Cookie", out var cookies))
                {
                    var updated = cookies
                        .Select(c =>
                            c.Contains("SameSite=None", StringComparison.OrdinalIgnoreCase) &&
                            c.Contains("Secure", StringComparison.OrdinalIgnoreCase) &&
                            !c.Contains("Partitioned", StringComparison.OrdinalIgnoreCase)
                                ? c + "; Partitioned"
                                : c)
                        .ToArray();

                    ctx.Response.Headers["Set-Cookie"] = new StringValues(updated);
                }
                return Task.CompletedTask;
            });

            await next();
        });

        // ----------------------------------------------------
        // ROUTES
        // ----------------------------------------------------
        app.MapControllers();

        Console.WriteLine(" Application started successfully");
        app.Run();
    }
}