using System.Data;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class LoginLockFallbackService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LoginLockFallbackService> _logger;
    private static bool _tableChecked;
    private static readonly SemaphoreSlim TableLock = new(1, 1);

    public LoginLockFallbackService(ApplicationDbContext db, ILogger<LoginLockFallbackService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await EnsureTableAsync(ct);
            await DeleteExpiredAsync(key, ct);

            var conn = _db.Database.GetDbConnection();
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
            _logger.LogWarning(ex, "DB login lock read failed for {Key}.", key);
            return null;
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, int ttlSeconds, CancellationToken ct = default)
    {
        try
        {
            await EnsureTableAsync(ct);
            var expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(ttlSeconds, 60));

            var conn = _db.Database.GetDbConnection();
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
            _logger.LogWarning(ex, "DB login lock write failed for {Key}.", key);
            return false;
        }
    }

    public async Task<RedisSetWhenNotExistsResult> TrySetStringWhenNotExistsAsync(string key, string value, int ttlSeconds, CancellationToken ct = default)
    {
        try
        {
            await EnsureTableAsync(ct);
            await DeleteExpiredAsync(key, ct);
            var existing = await GetStringAsync(key, ct);
            if (!string.IsNullOrWhiteSpace(existing))
                return RedisSetWhenNotExistsResult.AlreadyExists;

            return await SetStringAsync(key, value, ttlSeconds, ct)
                ? RedisSetWhenNotExistsResult.Set
                : RedisSetWhenNotExistsResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB login lock acquire failed for {Key}.", key);
            return RedisSetWhenNotExistsResult.Unavailable;
        }
    }

    public async Task<bool> ExtendTtlAsync(string key, int ttlSeconds, CancellationToken ct = default)
    {
        try
        {
            await EnsureTableAsync(ct);
            var expiresAt = DateTime.UtcNow.AddSeconds(Math.Max(ttlSeconds, 60));

            var conn = _db.Database.GetDbConnection();
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
            _logger.LogWarning(ex, "DB login lock TTL refresh failed for {Key}.", key);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await EnsureTableAsync(ct);
            var conn = _db.Database.GetDbConnection();
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
            _logger.LogWarning(ex, "DB login lock delete failed for {Key}.", key);
            return false;
        }
    }

    private async Task DeleteExpiredAsync(string key, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
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

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableChecked) return;

        await TableLock.WaitAsync(ct);
        try
        {
            if (_tableChecked) return;

            await _db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS tbl_login_lock (
                    lock_key VARCHAR(191) NOT NULL PRIMARY KEY,
                    lock_value LONGTEXT NOT NULL,
                    expires_at_utc DATETIME(6) NOT NULL,
                    updated_at_utc DATETIME(6) NOT NULL,
                    INDEX ix_tbl_login_lock_expires (expires_at_utc)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;", ct);
            _tableChecked = true;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private static void Add(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}