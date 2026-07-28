using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using SignalTracker.Configuration;
using SignalTracker.Middleware;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;
using SignalTracker.Services.ZipImport;
using StackExchange.Redis;
using System.Data;
using System.Threading.RateLimiting;

internal class Program
{
    private static string GetRateLimitPartitionKey(HttpContext context, bool preferUser = true)
    {
        if (preferUser)
        {
            var userId = context.User?.FindFirst("UserId")?.Value
                ?? context.User?.FindFirst("user_id")?.Value;

            if (!string.IsNullOrWhiteSpace(userId))
                return $"user:{userId}";
        }

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    private static void WarnIfConnectionStringMissing(IConfiguration configuration, string name)
    {
        var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(
            configuration.GetConnectionString(name));

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        Console.WriteLine(
            $"Missing database connection string '{name}'. " +
            $"Set 'ConnectionStrings:{name}' in configuration or environment variable 'ConnectionStrings__{name}'.");
    }

    private static void EnsureProjectGridSizeColumnExists(WebApplication app)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conn = db.Database.GetDbConnection();
            var shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose)
                conn.Open();

            try
            {
                using var exists = conn.CreateCommand();
                exists.CommandText = @"
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = DATABASE()
                      AND table_name = 'tbl_project'
                      AND column_name = 'grid_size';";

                var count = Convert.ToInt32(exists.ExecuteScalar());
                if (count > 0)
                    return;

                using var add = conn.CreateCommand();
                add.CommandText = "ALTER TABLE tbl_project ADD COLUMN grid_size VARCHAR(50) NULL;";
                add.ExecuteNonQuery();
                Console.WriteLine("Added missing column tbl_project.grid_size.");
            }
            finally
            {
                if (shouldClose)
                    conn.Close();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not ensure column tbl_project.grid_size: {ex.Message}");
        }
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

     
        builder.Services.AddScoped<UserScopeService>();
        builder.Services.AddScoped<LicenseFeatureService>();
        builder.Services.AddScoped<PythonBridgeService>();
        builder.Services.AddScoped<SitePredictionService>();
        builder.Services.AddScoped<ZipImportService>();
        builder.Services.AddScoped<IOtpService, OtpService>();
        builder.Services.AddScoped<IUserDeletionService, UserDeletionService>();
        builder.Services.AddHttpClient<ISmsService, SmsService>();
        builder.Services.AddSingleton<NetworkLogRealtimeNotifier>();
        builder.Services.AddHostedService<NetworkLogChangeWatcherService>();

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
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o =>
        {
            o.CustomSchemaIds(type => type.FullName?.Replace("+", "."));
            o.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Signal Tracker API",
                Version = "v1",
                Description = "OpenAPI documentation for Signal Tracker API endpoints."
            });
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.AddPolicy("Auth", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context, preferUser: false),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            o.AddPolicy("PasswordRecovery", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context, preferUser: false),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 15,
                    Window = TimeSpan.FromMinutes(3),
                    QueueLimit = 0
                }));

            o.AddPolicy("Otp", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context, preferUser: false),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            o.AddPolicy("Upload", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 15,
                    Window = TimeSpan.FromMinutes(30),
                    QueueLimit = 0
                }));

            o.AddPolicy("Report", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 12,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0
                }));

            o.AddPolicy("MobileIngestion", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context, preferUser: false),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1200,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            o.AddPolicy("PublicApi", context => RateLimitPartition.GetFixedWindowLimiter(
                GetRateLimitPartitionKey(context, preferUser: false),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
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
        WarnIfConnectionStringMissing(builder.Configuration, "MySqlConnection");
        WarnIfConnectionStringMissing(builder.Configuration, "MySqlConnection2");

        builder.Services.AddScoped<IDbConnectionProvider, DbConnectionProvider>();

        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var connectionProvider = sp.GetRequiredService<IDbConnectionProvider>();
            var connectionString = connectionProvider.GetConnectionString();
            var serverVersion = new MySqlServerVersion(new Version(8, 0, 29));

            options.UseMySql(connectionString, serverVersion, mysqlOptions =>
            {
                mysqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });
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
                        Console.WriteLine("Redis connection failed: operation failed (see server logs)");

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
            catch (Exception)
            {
                Console.WriteLine("Redis failed: operation failed (see server logs)");
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
        EnsureProjectGridSizeColumnExists(app);

        // ----------------------------------------------------
        // MIDDLEWARE PIPELINE
        // ----------------------------------------------------
        app.UseSignalTrackerSecurityHeaders();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(o =>
            {
                o.SwaggerEndpoint("/swagger/v1/swagger.json", "Signal Tracker API v1");
            });
        }

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
        app.UseRateLimiter();
        app.UseCors(SecurityServiceExtensions.CorsPolicyName);
        app.UseWebSockets();
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
