using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SignalTracker.Helper;

namespace SignalTracker.Services;

public sealed class NetworkLogRealtimeNotifier
{
    private const int MaxInboundMessageBytes = 16 * 1024;

    private sealed class Subscription
    {
        public required WebSocket Socket { get; init; }
        public HashSet<long> SessionIds { get; set; } = new();
    }

    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<long> GetWatchedSessionIds()
    {
        return _subscriptions.Values
            .SelectMany(x => x.SessionIds)
            .Distinct()
            .ToArray();
    }

    public async Task AcceptAsync(WebSocket socket, IEnumerable<long> initialSessionIds, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _subscriptions[id] = new Subscription
        {
            Socket = socket,
            SessionIds = NormalizeSessionIds(initialSessionIds)
        };

        await SendAsync(socket, new
        {
            type = "subscribed",
            sessionIds = _subscriptions[id].SessionIds
        }, ct);

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextAsync(socket, buffer, ct);
                if (message == null)
                    break;

                var sessionIds = TryReadSessionIds(message);
                if (sessionIds.Count > 0 && _subscriptions.TryGetValue(id, out var subscription))
                {
                    subscription.SessionIds = sessionIds;
                    await SendAsync(socket, new
                    {
                        type = "subscribed",
                        sessionIds
                    }, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Network log websocket closed: {SafeException.Get(ex)}");
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                }
                catch
                {
                    // The transport may already be gone; removal from _subscriptions is the important cleanup.
                }
            }

            socket.Dispose();
        }
    }

    public Task NotifySessionChangedAsync(long sessionId, string version, CancellationToken ct = default)
    {
        return NotifySessionsChangedAsync(new[] { sessionId }, version, ct);
    }

    public async Task NotifySessionsChangedAsync(IEnumerable<long> sessionIds, string version, CancellationToken ct = default)
    {
        var changedIds = NormalizeSessionIds(sessionIds);
        if (changedIds.Count == 0)
            return;

        var payload = new
        {
            type = "networkLogChanged",
            sessionIds = changedIds,
            version,
            changedAt = DateTimeOffset.UtcNow
        };

        var staleIds = new List<Guid>();
        foreach (var pair in _subscriptions)
        {
            var subscription = pair.Value;
            if (!subscription.SessionIds.Overlaps(changedIds))
                continue;

            if (subscription.Socket.State != WebSocketState.Open)
            {
                staleIds.Add(pair.Key);
                continue;
            }

            try
            {
                await SendAsync(subscription.Socket, payload, ct);
            }
            catch
            {
                staleIds.Add(pair.Key);
            }
        }

        foreach (var id in staleIds)
            _subscriptions.TryRemove(id, out _);
    }

    private async Task SendAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxInboundMessageBytes)
                return null;
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static HashSet<long> TryReadSessionIds(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("sessionIds", out var sessionIds))
                return new HashSet<long>();

            if (sessionIds.ValueKind == JsonValueKind.Array)
            {
                return NormalizeSessionIds(sessionIds.EnumerateArray()
                    .Select(x => x.TryGetInt64(out var value) ? value : 0));
            }

            if (sessionIds.ValueKind == JsonValueKind.String)
            {
                return ParseSessionIds(sessionIds.GetString());
            }
        }
        catch
        {
        }

        return new HashSet<long>();
    }

    public static HashSet<long> ParseSessionIds(string? value)
    {
        return NormalizeSessionIds((value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => long.TryParse(x, out var id) ? id : 0));
    }

    private static HashSet<long> NormalizeSessionIds(IEnumerable<long> sessionIds)
    {
        return sessionIds
            .Where(x => x > 0)
            .Distinct()
            .ToHashSet();
    }
}
