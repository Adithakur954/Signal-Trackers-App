using Microsoft.Extensions.Caching.Memory;
using SignalTracker.Models;

namespace SignalTracker.Services;

// Callers must obtain the current database version before using this cache.
public sealed class VersionedNetworkLogCache : IDisposable
{
    private readonly MemoryCache _memory;
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly TimeProvider _time;
    private long _generation;

    public VersionedNetworkLogCache(long sizeLimit, TimeProvider? time = null)
    {
        _memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimit });
        _time = time ?? TimeProvider.System;
    }

    public sealed class Entry<T> where T : class
    {
        public T Value { get; set; } = default!;
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public sealed record Result<T>(T Value, bool Hit, string Store) where T : class;

    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<Result<T>> GetOrCreateAsync<T>(string key, RedisService? redis,
        Func<Task<T>> build, Func<T, long> size, bool bypass = false) where T : class
    {
        var gate = _gates[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length)];
        await gate.WaitAsync();
        try
        {
            var generation = Volatile.Read(ref _generation);
            var localKey = (generation, key);
            if (!bypass)
            {
                var read = redis?.IsConnected == true
                    ? await redis.GetObjectWithStatusAsync<Entry<T>>(key)
                    : new RedisCacheReadResult<Entry<T>>(null, RedisCacheReadStatus.Unavailable);
                if (read.Status == RedisCacheReadStatus.Hit && read.Value?.Value != null
                    && read.Value.ExpiresAtUtc > _time.GetUtcNow())
                {
                    Remember(localKey, read.Value, size(read.Value.Value));
                    return new(read.Value.Value, true, "REDIS");
                }

                if (read.Status == RedisCacheReadStatus.Unavailable
                    && _memory.TryGetValue(localKey, out Entry<T>? local)
                    && local?.Value != null && local.ExpiresAtUtc > _time.GetUtcNow())
                    return new(local.Value, true, "MEMORY");

                // A real Redis miss may be an explicit cross-instance invalidation.
                _memory.Remove(localKey);
            }

            var value = await build();
            var entry = new Entry<T> { Value = value, ExpiresAtUtc = _time.GetUtcNow().AddMinutes(5) };
            if (generation == Volatile.Read(ref _generation))
            {
                Remember(localKey, entry, size(value));
                if (redis?.IsConnected == true)
                    await redis.SetObjectAsync(key, entry, ttlSeconds: 300);
            }
            return new(value, false, "DATABASE");
        }
        finally
        {
            gate.Release();
        }
    }

    private void Remember<T>(object key, Entry<T> entry, long size) where T : class
        => _memory.Set(key, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = entry.ExpiresAtUtc,
            Size = Math.Max(1, size)
        });

    public void Dispose()
    {
        _memory.Dispose();
        foreach (var gate in _gates) gate.Dispose();
    }
}
