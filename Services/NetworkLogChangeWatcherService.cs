using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Helper;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class NetworkLogChangeWatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NetworkLogRealtimeNotifier _notifier;
    private readonly ILogger<NetworkLogChangeWatcherService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<long, string> _versions = new();

    public NetworkLogChangeWatcherService(
        IServiceScopeFactory scopeFactory,
        NetworkLogRealtimeNotifier notifier,
        IConfiguration configuration,
        ILogger<NetworkLogChangeWatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _logger = logger;

        var seconds = Math.Clamp(configuration.GetValue("Realtime:NetworkLogPollSeconds", 3), 1, 60);
        _pollInterval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckForChangesAsync(stoppingToken);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckForChangesAsync(CancellationToken ct)
    {
        var watchedSessionIds = _notifier.GetWatchedSessionIds();
        if (watchedSessionIds.Count == 0)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            var versions = await ReadVersionsAsync(conn, watchedSessionIds, includeUpdatedAt: true, ct);
            foreach (var item in versions)
            {
                if (!_versions.TryGetValue(item.Key, out var previous))
                {
                    _versions[item.Key] = item.Value;
                    continue;
                }

                if (string.Equals(previous, item.Value, StringComparison.Ordinal))
                    continue;

                _versions[item.Key] = item.Value;
                await _notifier.NotifySessionChangedAsync(item.Key, item.Value, ct);
            }

            var active = watchedSessionIds.ToHashSet();
            foreach (var staleId in _versions.Keys.Where(id => !active.Contains(id)).ToArray())
                _versions.Remove(staleId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Network log realtime watcher failed: {Message}", SafeException.Get(ex));
        }
    }

    private static async Task<Dictionary<long, string>> ReadVersionsAsync(
        DbConnection conn,
        IReadOnlyCollection<long> sessionIds,
        bool includeUpdatedAt,
        CancellationToken ct)
    {
        try
        {
            return await ReadVersionsCoreAsync(conn, sessionIds, includeUpdatedAt, ct);
        }
        catch when (includeUpdatedAt)
        {
            return await ReadVersionsCoreAsync(conn, sessionIds, includeUpdatedAt: false, ct);
        }
    }

    private static async Task<Dictionary<long, string>> ReadVersionsCoreAsync(
        DbConnection conn,
        IReadOnlyCollection<long> sessionIds,
        bool includeUpdatedAt,
        CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        var parameterNames = new List<string>();
        var index = 0;
        foreach (var sessionId in sessionIds)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@sid{index++}";
            parameter.Value = sessionId;
            command.Parameters.Add(parameter);
            parameterNames.Add(parameter.ParameterName);
        }

        command.CommandText = includeUpdatedAt
            ? $@"
                SELECT
                    session_id,
                    COUNT(*) AS row_count,
                    COALESCE(MAX(id), 0) AS max_id,
                    COALESCE(UNIX_TIMESTAMP(MAX(updated_at)), 0) AS max_updated_at,
                    COALESCE(UNIX_TIMESTAMP(MAX(timestamp)), 0) AS max_timestamp
                FROM tbl_network_log
                WHERE session_id IN ({string.Join(",", parameterNames)})
                GROUP BY session_id;"
            : $@"
                SELECT
                    session_id,
                    COUNT(*) AS row_count,
                    COALESCE(MAX(id), 0) AS max_id,
                    0 AS max_updated_at,
                    COALESCE(UNIX_TIMESTAMP(MAX(timestamp)), 0) AS max_timestamp
                FROM tbl_network_log
                WHERE session_id IN ({string.Join(",", parameterNames)})
                GROUP BY session_id;";

        var result = new Dictionary<long, string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sessionId = Convert.ToInt64(reader["session_id"]);
            var version = string.Join("-",
                Convert.ToInt64(reader["row_count"]),
                Convert.ToInt64(reader["max_id"]),
                Convert.ToInt64(reader["max_updated_at"]),
                Convert.ToInt64(reader["max_timestamp"]));
            result[sessionId] = version;
        }

        return result;
    }
}
