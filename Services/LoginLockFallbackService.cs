using System.Collections.Concurrent;
using System.Data;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;
using SignalTracker.Security;

namespace SignalTracker.Services;

public sealed class LoginLockFallbackService
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LoginLockFallbackService> _logger;
    private static readonly ConcurrentDictionary<string, bool> CheckedTables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim TableLock = new(1, 1);

    public LoginLockFallbackService(
        ApplicationDbContext db,
        IConfiguration configuration,
        ILogger<LoginLockFallbackService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await using var lease = CreateDbForKey(key);
            var db = lease.Db;
            await EnsureTableAsync(db, ct);
            await DeleteExpiredAsync(db, key, ct);

            var conn = db.Database.GetDbConnection();
            var close = conn.State != ConnectionState.Open;
            if (close) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT lock_value
                                    FROM tbl_login_lock
                                    WHERE lock_key = @key
                                      AND expires_at_utc > UTC_TIMESTAMP(6)
                                    LIMIT 1;";
                Add(cmd, "@key", key);
                var value = await cmd.ExecuteScalarAsync(ct);
                return value == null || value == DBNull.Value ? null : Convert.ToString(value);
            }
            finally
            {
                if (close) await conn.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            _logger.LogWarning(ex, "DB login lock read failed for {Key}.", key);
            return null;
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, int ttlSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var lease = CreateDbForKey(key);
            var db = lease.Db;
            await EnsureTableAsync(db, ct);
            var expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(ttlSeconds, 60));

            var conn = db.Database.GetDbConnection();
            var close = conn.State != ConnectionState.Open;
            if (close) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO tbl_login_lock (lock_key, lock_value, expires_at_utc, updated_at_utc)
                                    VALUES (@key, @value, @expires, UTC_TIMESTAMP(6))
                                    ON DUPLICATE KEY UPDATE
                                        lock_value = VALUES(lock_value),
                                        expires_at_utc = VALUES(expires_at_utc),
                                        updated_at_utc = UTC_TIMESTAMP(6);";
                Add(cmd, "@key", key);
                Add(cmd, "@value", value);
                Add(cmd, "@expires", expiresAt);
                await cmd.ExecuteNonQueryAsync(ct);
                return true;
            }
            finally
            {
                if (close) await conn.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            _logger.LogWarning(ex, "DB login lock write failed for {Key}.", key);
            return false;
        }
    }

    public async Task<RedisSetWhenNotExistsResult> TrySetStringWhenNotExistsAsync(string key, string value, int ttlSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var lease = CreateDbForKey(key);
            var db = lease.Db;
            await EnsureTableAsync(db, ct);
            await DeleteExpiredAsync(db, key, ct);

            var existing = await GetStringAsync(key, ct);
            if (!string.IsNullOrWhiteSpace(existing))
                return RedisSetWhenNotExistsResult.AlreadyExists;

            return await SetStringAsync(key, value, ttlSeconds, ct)
                ? RedisSetWhenNotExistsResult.Set
                : RedisSetWhenNotExistsResult.Unavailable;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            _logger.LogWarning(ex, "DB login lock acquire failed for {Key}.", key);
            return RedisSetWhenNotExistsResult.Unavailable;
        }
    }

    public async Task<bool> ExtendTtlAsync(string key, int ttlSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var lease = CreateDbForKey(key);
            var db = lease.Db;
            await EnsureTableAsync(db, ct);
            var expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(ttlSeconds, 60));

            var conn = db.Database.GetDbConnection();
            var close = conn.State != ConnectionState.Open;
            if (close) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE tbl_login_lock
                                    SET expires_at_utc = @expires,
                                        updated_at_utc = UTC_TIMESTAMP(6)
                                    WHERE lock_key = @key;";
                Add(cmd, "@key", key);
                Add(cmd, "@expires", expiresAt);
                return await cmd.ExecuteNonQueryAsync(ct) > 0;
            }
            finally
            {
                if (close) await conn.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            _logger.LogWarning(ex, "DB login lock TTL refresh failed for {Key}.", key);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await using var lease = CreateDbForKey(key);
            var db = lease.Db;
            await EnsureTableAsync(db, ct);
            var conn = db.Database.GetDbConnection();
            var close = conn.State != ConnectionState.Open;
            if (close) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM tbl_login_lock WHERE lock_key = @key;";
                Add(cmd, "@key", key);
                await cmd.ExecuteNonQueryAsync(ct);
                return true;
            }
            finally
            {
                if (close) await conn.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            _logger.LogWarning(ex, "DB login lock delete failed for {Key}.", key);
            return false;
        }
    }

    private async Task DeleteExpiredAsync(ApplicationDbContext db, string key, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var close = conn.State != ConnectionState.Open;
        if (close) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tbl_login_lock WHERE lock_key = @key AND expires_at_utc <= UTC_TIMESTAMP(6);";
            Add(cmd, "@key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (close) await conn.CloseAsync();
        }
    }

    private async Task EnsureTableAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var tableScope = db.Database.GetConnectionString() ?? db.Database.GetDbConnection().ConnectionString;
        if (CheckedTables.ContainsKey(tableScope)) return;

        await TableLock.WaitAsync(ct);
        try
        {
            if (CheckedTables.ContainsKey(tableScope)) return;

            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS tbl_login_lock (
                    lock_key VARCHAR(191) NOT NULL PRIMARY KEY,
                    lock_value LONGTEXT NOT NULL,
                    expires_at_utc DATETIME(6) NOT NULL,
                    updated_at_utc DATETIME(6) NOT NULL,
                    INDEX ix_tbl_login_lock_expires (expires_at_utc)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;", ct);
            CheckedTables[tableScope] = true;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private DbLease CreateDbForKey(string key)
    {
        var region = ExtractRegion(key);
        var connectionName = string.Equals(region, "TW", StringComparison.OrdinalIgnoreCase)
            ? "MySqlConnection2"
            : "MySqlConnection";
        var connectionString = MySqlConnectionStringHelper.EnsureZeroDateTimeHandling(
            _configuration.GetConnectionString(connectionName));

        if (string.IsNullOrWhiteSpace(connectionString))
            return new DbLease(_db, ownsDb: false);

        var currentConnection = _db.Database.GetConnectionString() ?? _db.Database.GetDbConnection().ConnectionString;
        if (string.Equals(currentConnection, connectionString, StringComparison.Ordinal))
            return new DbLease(_db, ownsDb: false);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 29)))
            .Options;
        return new DbLease(new ApplicationDbContext(options), ownsDb: true);
    }

    private static string? ExtractRegion(string key)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 4 ? RegionAccess.Normalize(parts[3]) : null;
    }

    private static void Add(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private sealed class DbLease : IAsyncDisposable
    {
        private readonly bool _ownsDb;

        public DbLease(ApplicationDbContext db, bool ownsDb)
        {
            Db = db;
            _ownsDb = ownsDb;
        }

        public ApplicationDbContext Db { get; }

        public async ValueTask DisposeAsync()
        {
            if (_ownsDb)
                await Db.DisposeAsync();
        }
    }
}
