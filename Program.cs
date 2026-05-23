using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Configuration;
using SignalTracker.Middleware;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;
using StackExchange.Redis;

internal class Program
{
    private static string RequireConnectionString(IConfiguration configuration, string name)
    {
        var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(
            configuration.GetConnectionString(name));

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException(
            $"Missing database connection string '{name}'. " +
            $"Set 'ConnectionStrings:{name}' in configuration or environment variable 'ConnectionStrings__{name}'.");
    }

    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        if (builder.Environment.IsDevelopment())
        {
            var userSecretsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft",
                "UserSecrets",
                "SignalTracker.LocalDevelopment",
                "secrets.json");

            builder.Configuration.AddJsonFile(userSecretsPath, optional: true, reloadOnChange: true);
        }

        // ----------------------------------------------------
        // CONTROLLERS & APPLICATION SERVICES
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
        builder.Services.AddControllersWithViews(o =>
            {
                o.Filters.Add<ProductionErrorResponseFilter>();
            })
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = null;
            });

        // ----------------------------------------------------
        // SECURITY, CORS, COOKIES & DATA PROTECTION
        // ----------------------------------------------------
        builder.Services.AddSignalTrackerCors(builder.Configuration, builder.Environment);
        builder.Services.AddSignalTrackerDataProtection(builder);
        builder.Services.AddSignalTrackerCookieAuth(builder.Configuration, builder.Environment);
        var httpsRedirectionPort = builder.Services.AddSignalTrackerForwardingAndHttps(builder.Configuration);

        // ----------------------------------------------------
        // DATABASE (DYNAMIC SELECTION)
        // ----------------------------------------------------
        var validatedMainDbConnection = RequireConnectionString(builder.Configuration, "MySqlConnection");
        var validatedTwDbConnection = RequireConnectionString(builder.Configuration, "MySqlConnection2");
        _ = validatedMainDbConnection;
        _ = validatedTwDbConnection;

        builder.Services.AddScoped<IDbConnectionProvider, DbConnectionProvider>();

        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var connectionProvider = sp.GetRequiredService<IDbConnectionProvider>();
            var connectionString = connectionProvider.GetConnectionString();
            var serverVersion = new MySqlServerVersion(new Version(8, 0, 29));

            options.UseMySql(connectionString, serverVersion);
        });

        Console.WriteLine("Dynamic Database Provider configured");

        // Ensure the upload root exists both at runtime and in the deployed app.
        var uploadedExcelsPath = Path.Combine(builder.Environment.ContentRootPath, "UploadedExcels");
        Directory.CreateDirectory(uploadedExcelsPath);

        // ----------------------------------------------------
        // REDIS CONFIGURATION (SAFE FALLBACK)
        // ----------------------------------------------------
        var redisConnString = builder.Configuration.GetConnectionString("Redis");

        if (string.IsNullOrWhiteSpace(redisConnString))
        {
            Console.WriteLine("Redis not configured. Using in-memory cache.");
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton(_ => new RedisService(null));
        }
        else
        {
            try
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnString, true);
                redisOptions.AbortOnConnectFail = false;
                redisOptions.ConnectTimeout = 3000;
                redisOptions.SyncTimeout = 3000;
                redisOptions.AsyncTimeout = 3000;
                redisOptions.ConnectRetry = 5;
                redisOptions.KeepAlive = 15;
                redisOptions.ConfigCheckSeconds = 15;
                redisOptions.ReconnectRetryPolicy = new ExponentialRetry(2000);

                builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
                {
                    var mux = ConnectionMultiplexer.Connect(redisOptions);

                    mux.ConnectionFailed += (_, e) =>
                        Console.WriteLine($"Redis connection failed: {e.Exception?.Message}");

                    mux.ConnectionRestored += (_, _) =>
                        Console.WriteLine("Redis connection restored");

                    Console.WriteLine(mux.IsConnected
                        ? "Redis connected"
                        : "Redis client created, but Redis is not connected yet");
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
                Console.WriteLine($"Redis failed: {ex.Message}");
                builder.Services.AddDistributedMemoryCache();
                builder.Services.AddSingleton(_ => new RedisService(null));
            }
        }

        // ----------------------------------------------------
        // LARGE FILE UPLOADS (500MB)
        // ----------------------------------------------------
        var maxUploadBytes = builder.Configuration.GetValue<long?>("Security:MaxUploadBytes") ?? 104_857_600L;
        if (maxUploadBytes <= 0 || maxUploadBytes > 524_288_000L)
        {
            maxUploadBytes = 104_857_600L;
        }

        builder.Services.Configure<FormOptions>(o =>
        {
            o.ValueLengthLimit = int.MaxValue;
            o.MultipartBodyLengthLimit = maxUploadBytes;
            o.MultipartHeadersLengthLimit = int.MaxValue;
        });

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = maxUploadBytes;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(20);
        });

        // ----------------------------------------------------
        // BUILD APP
        // ----------------------------------------------------
        var app = builder.Build();

        // ----------------------------------------------------
        // MIDDLEWARE PIPELINE
        // ----------------------------------------------------
        app.UseSignalTrackerSecurityHeaders();

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

        // Backward-compatible alias for clients calling the new route name.
        // Must run before UseRouting so endpoint matching uses rewritten path.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/api/MapView/GetSubSessionAnalyticsWithStatus", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Request.Path = "/api/MapView/GetSubSessionAnalytics";
                ctx.Request.QueryString = ctx.Request.QueryString.Add("includeStatus", "1");
            }

            await next();
        });

        app.UseRouting();
        app.UseCors(SecurityServiceExtensions.CorsPolicyName);
        app.UseCookiePolicy();
        app.UseSession();

        // Keep session alive while authenticated pages are in active use.
        app.Use(async (ctx, next) =>
        {
            ctx.Session.Set("st.pulse", BitConverter.GetBytes(DateTime.UtcNow.Ticks));
            await next();
        });

        app.UseAuthentication();
        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.UseAuthorization();
        app.UsePartitionedCookieSupport();

        // ----------------------------------------------------
        // ROUTES
        // ----------------------------------------------------
        app.MapControllers();

        Console.WriteLine("Application started successfully");
        app.Run();
    }
}
