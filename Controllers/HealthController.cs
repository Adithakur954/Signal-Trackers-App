using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using SignalTracker.Models;
using SignalTracker.Security;

namespace SignalTracker.Controllers
{
    [AllowAnonymous]
    [PublicApiKey]
    [ApiController]
    [Route("healthz")]
    public class HealthController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly RedisService _redis;
        private readonly IConfiguration _configuration;

        public HealthController(ApplicationDbContext db, RedisService redis, IConfiguration configuration)
        {
            _db = db;
            _redis = redis;
            _configuration = configuration;
        }

        [HttpGet]
        [HttpGet("ready")]
        public async Task<IActionResult> Ready(CancellationToken ct)
        {
            var timestampUtc = DateTimeOffset.UtcNow;
            var checks = new Dictionary<string, object?>();
            var healthy = true;

            var mainDbWatch = Stopwatch.StartNew();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                var dbOk = await _db.Database.CanConnectAsync(timeoutCts.Token);
                mainDbWatch.Stop();

                checks["main_database"] = new
                {
                    status = dbOk ? "ok" : "down",
                    latencyMs = mainDbWatch.ElapsedMilliseconds
                };

                healthy &= dbOk;
            }
            catch (OperationCanceledException)
            {
                mainDbWatch.Stop();
                checks["main_database"] = new
                {
                    status = "timeout",
                    latencyMs = mainDbWatch.ElapsedMilliseconds
                };
                healthy = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Main database health check failed: {ex.Message}");
                mainDbWatch.Stop();
                checks["main_database"] = new
                {
                    status = "down",
                    latencyMs = mainDbWatch.ElapsedMilliseconds
                };
                healthy = false;
            }

            var twDbWatch = Stopwatch.StartNew();
            try
            {
                var twConnectionString = _configuration.GetConnectionString("MySqlConnection2");
                if (string.IsNullOrWhiteSpace(twConnectionString))
                {
                    twDbWatch.Stop();
                    checks["tw_database"] = new
                    {
                        status = "not_configured",
                        latencyMs = twDbWatch.ElapsedMilliseconds
                    };
                    healthy = false;
                }
                else
                {
                    await using var twConnection = new MySqlConnection(twConnectionString);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                    await twConnection.OpenAsync(timeoutCts.Token);
                    twDbWatch.Stop();

                    checks["tw_database"] = new
                    {
                        status = "ok",
                        latencyMs = twDbWatch.ElapsedMilliseconds
                    };
                }
            }
            catch (OperationCanceledException)
            {
                twDbWatch.Stop();
                checks["tw_database"] = new
                {
                    status = "timeout",
                    latencyMs = twDbWatch.ElapsedMilliseconds
                };
                healthy = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Secondary database health check failed: {ex.Message}");
                twDbWatch.Stop();
                checks["tw_database"] = new
                {
                    status = "down",
                    latencyMs = twDbWatch.ElapsedMilliseconds
                };
                healthy = false;
            }

            var redisConfigured = !string.IsNullOrWhiteSpace(_configuration.GetConnectionString("Redis"));
            checks["redis"] = new
            {
                configured = redisConfigured,
                status = redisConfigured
                    ? (_redis.IsConnected ? "ok" : "unavailable")
                    : "not_configured"
            };

            TimeSpan? uptime = null;
            try
            {
                uptime = DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch
            {
                // Uptime is informational only.
            }

            var payload = new
            {
                service = "SignalTracker",
                endpoint = "ready",
                status = healthy ? "ok" : "degraded",
                timestampUtc,
                uptimeSeconds = uptime?.TotalSeconds,
                checks
            };

            return healthy ? Ok(payload) : StatusCode(503, payload);
        }

        [HttpGet("live")]
        public IActionResult Live()
        {
            TimeSpan? uptime = null;
            try
            {
                uptime = DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch
            {
                // Uptime is informational only.
            }

            return Ok(new
            {
                service = "SignalTracker",
                endpoint = "live",
                status = "ok",
                timestampUtc = DateTimeOffset.UtcNow,
                uptimeSeconds = uptime?.TotalSeconds
            });
        }
    }
}
