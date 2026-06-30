using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

[ApiController]
[Authorize]
public sealed class NetworkLogRealtimeController : ControllerBase
{
    private readonly NetworkLogRealtimeNotifier _notifier;

    public NetworkLogRealtimeController(NetworkLogRealtimeNotifier notifier)
    {
        _notifier = notifier;
    }

    [HttpGet("/ws/network-log")]
    public async Task Connect([FromQuery(Name = "session_ids")] string? sessionIds)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("WebSocket request expected.");
            return;
        }

        var watchedSessionIds = NetworkLogRealtimeNotifier.ParseSessionIds(sessionIds);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await _notifier.AcceptAsync(socket, watchedSessionIds, HttpContext.RequestAborted);
    }
}
