
using SignalTracker.Controllers;
using SignalTracker.Helper;
using StackExchange.Redis;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SignalTracker.Models
{
    public enum RedisSetWhenNotExistsResult
    {
        Set,
        AlreadyExists,
        Unavailable
    }

    public class RedisService
    {
        private const byte StoredJsonMarker = 0;
        private const byte StoredGzipMarker = 1;
        private const int CompressionThresholdBytes = 8192;

        private readonly IConnectionMultiplexer? _multiplexer;
        private readonly IDatabase? _db;

        public RedisService(IConnectionMultiplexer? multiplexer)
        {
            _multiplexer = multiplexer;
            _db = multiplexer?.GetDatabase();
        }

        public bool IsConnected => _multiplexer?.IsConnected ?? false;

        private static TimeSpan CacheCommandTimeout { get; } = TimeSpan.FromMilliseconds(750);

        private static async Task<T> WithTimeoutAsync<T>(Task<T> task, T fallback)
        {
            var completed = await Task.WhenAny(task, Task.Delay(CacheCommandTimeout));
            return ReferenceEquals(completed, task) ? await task : fallback;
        }

        // ---------------- BASIC ----------------

        public async Task<bool> PingAsync()
        {
            if (_db == null) return false;

            try
            {
                var pong = await _db.PingAsync();
                return pong.TotalMilliseconds >= 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis PingAsync error: {SafeException.Get(ex)}");
                return false;
            }
        }

        public async Task<T?> GetObjectAsync<T>(string key) where T : class
        {
            if (_db == null) return null;

            try
            {
                var value = await WithTimeoutAsync(_db.StringGetAsync(key), RedisValue.Null);
                if (value.IsNullOrEmpty) return null;

                var payload = (byte[])value!;
                if (payload.Length == 0)
                    return null;

                if (payload[0] == StoredGzipMarker)
                {
                    var jsonBytes = DecompressPayload(payload.AsSpan(1));
                    return JsonSerializer.Deserialize<T>(jsonBytes);
                }

                if (payload[0] == StoredJsonMarker)
                {
                    return JsonSerializer.Deserialize<T>(payload.AsSpan(1));
                }

                // Backward compatibility: older cache entries were stored as plain JSON text.
                var legacyJson = Encoding.UTF8.GetString(payload);
                return JsonSerializer.Deserialize<T>(legacyJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis GetObjectAsync error [{key}]: {SafeException.Get(ex)}");
                return null;
            }
        }

        public async Task<bool> SetObjectAsync<T>(string key, T value, int ttlSeconds = 300) where T : class
        {
            if (_db == null) return false;

            try
            {
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value);
                var payload = CreateStoredPayload(jsonBytes);
                return await WithTimeoutAsync(
                    _db.StringSetAsync(key, payload, TimeSpan.FromSeconds(ttlSeconds)),
                    false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis SetObjectAsync error [{key}]: {SafeException.Get(ex)}");
                return false;
            }
        }

        public async Task<string?> GetStringAsync(string key)
        {
            if (_db == null) return null;

            try
            {
                var value = await WithTimeoutAsync(_db.StringGetAsync(key), RedisValue.Null);
                return value.IsNullOrEmpty ? null : value.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis GetStringAsync error [{key}]: {SafeException.Get(ex)}");
                return null;
            }
        }

        public async Task<bool> SetStringAsync(string key, string value, int ttlSeconds = 300)
        {
            if (_db == null) return false;

            try
            {
                return await WithTimeoutAsync(
                    _db.StringSetAsync(key, value, TimeSpan.FromSeconds(ttlSeconds)),
                    false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis SetStringAsync error [{key}]: {SafeException.Get(ex)}");
                return false;
            }
        }

        public async Task<bool> TrySetStringAsync(string key, string value, int ttlSeconds = 300)
        {
            if (_db == null) return false;

            try
            {
                return await WithTimeoutAsync(
                    _db.StringSetAsync(key, value, TimeSpan.FromSeconds(ttlSeconds), when: When.NotExists),
                    false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis TrySetStringAsync error [{key}]: {SafeException.Get(ex)}");
                return false;
            }
        }

        public async Task<RedisSetWhenNotExistsResult> TrySetStringWhenNotExistsAsync(string key, string value, int ttlSeconds = 300)
        {
            if (_db == null) return RedisSetWhenNotExistsResult.Unavailable;

            try
            {
                var task = _db.StringSetAsync(key, value, TimeSpan.FromSeconds(ttlSeconds), when: When.NotExists);
                var completed = await Task.WhenAny(task, Task.Delay(CacheCommandTimeout));
                if (!ReferenceEquals(completed, task))
                    return RedisSetWhenNotExistsResult.Unavailable;

                return await task
                    ? RedisSetWhenNotExistsResult.Set
                    : RedisSetWhenNotExistsResult.AlreadyExists;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis TrySetStringWhenNotExistsAsync error [{key}]: {SafeException.Get(ex)}");
                return RedisSetWhenNotExistsResult.Unavailable;
            }
        }

        public async Task<bool> DeleteAsync(string key)
        {
            if (_db == null) return false;

            try
            {
                return await WithTimeoutAsync(_db.KeyDeleteAsync(key), false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis DeleteAsync error [{key}]: {SafeException.Get(ex)}");
                return false;
            }
        }

        // ---------------- FIX #1: GET KEYS ----------------

        public async Task<List<string>> GetKeysAsync(string pattern, int maxCount = 1000, int count = 0)
        {
            var keys = new List<string>();

            if (_multiplexer == null)
                return keys;

            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var endpoint in _multiplexer.GetEndPoints())
                {
                    var server = _multiplexer.GetServer(endpoint);

                    await foreach (var key in server.KeysAsync(pattern: pattern))
                    {
                        if (!seen.Add(key.ToString()))
                            continue;

                        keys.Add(key.ToString());
                        if (keys.Count >= maxCount)
                            return keys;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis GetKeysAsync error: {SafeException.Get(ex)}");
            }

            return keys;
        }

        // ---------------- TTL ----------------

        public async Task<TimeSpan?> GetTtlAsync(string key)
        {
            if (_db == null) return null;
            return await _db.KeyTimeToLiveAsync(key);
        }

        public async Task<bool> ExtendTtlAsync(string key, int ttlSeconds)
        {
            if (_db == null) return false;
            return await WithTimeoutAsync(_db.KeyExpireAsync(key, TimeSpan.FromSeconds(ttlSeconds)), false);
        }

        // ---------------- MAINTENANCE ----------------

        public async Task<bool> FlushAllAsync()
        {
            if (_multiplexer == null) return false;

            foreach (var ep in _multiplexer.GetEndPoints())
            {
                var server = _multiplexer.GetServer(ep);
                await server.FlushDatabaseAsync();
            }

            return true;
        }
        

        internal async Task<long> DeleteByPatternAsync(string v)
        {
            if (_multiplexer == null)
                return 0;

            try
            {
                const int batchSize = 500;
                var batch = new List<RedisKey>(batchSize);
                long deleted = 0;

                foreach (var endpoint in _multiplexer.GetEndPoints())
                {
                    var server = _multiplexer.GetServer(endpoint);

                    await foreach (var key in server.KeysAsync(pattern: v))
                    {
                        batch.Add(key);
                        if (batch.Count >= batchSize)
                        {
                            if (_db != null)
                                deleted += await WithTimeoutAsync(_db.KeyDeleteAsync(batch.ToArray()), 0L);
                            batch.Clear();
                        }
                    }
                }

                if (batch.Count > 0 && _db != null)
                {
                    deleted += await WithTimeoutAsync(_db.KeyDeleteAsync(batch.ToArray()), 0L);
                }

                return deleted;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis DeleteByPatternAsync error: {SafeException.Get(ex)}");
                return 0;
            }
        }

        internal async Task<IEnumerable<object>> GetKeysAsync(object pattern, object limit)
        {
            throw new NotImplementedException();
        }

        internal async Task DeleteKeyAsync(string redisKey)
        {
            if (_db == null)
                return;

            await WithTimeoutAsync(_db.KeyDeleteAsync(redisKey, flags: CommandFlags.FireAndForget), true);
        }

        internal async Task<bool> SetObjectAsync(object cacheKey, MapViewController.NetworkLogFullResponse cacheModel, int ttlSeconds)
        {
            throw new NotImplementedException();
        }

        private static byte[] CreateStoredPayload(ReadOnlySpan<byte> jsonBytes)
        {
            if (jsonBytes.Length >= CompressionThresholdBytes)
            {
                using var compressed = new MemoryStream();
                compressed.WriteByte(StoredGzipMarker);

                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                {
                    gzip.Write(jsonBytes);
                }

                return compressed.ToArray();
            }

            var payload = new byte[jsonBytes.Length + 1];
            payload[0] = StoredJsonMarker;
            jsonBytes.CopyTo(payload.AsSpan(1));
            return payload;
        }

        private static byte[] DecompressPayload(ReadOnlySpan<byte> compressedBytes)
        {
            using var input = new MemoryStream(compressedBytes.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }
}
