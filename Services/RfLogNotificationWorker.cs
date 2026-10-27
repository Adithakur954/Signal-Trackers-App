using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class RfLogNotificationWorker(IServiceScopeFactory scopes, IHttpClientFactory http,
    IOptions<RfLogIntegrationOptions> configuration, ILogger<RfLogNotificationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(DispatchRegionAsync("IN", stoppingToken), DispatchRegionAsync("TW", stoppingToken));

    private async Task DispatchRegionAsync(string region, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = configuration.Value;
                if (!options.IsConfigured || options.WebhookSigningSecret.Length < 32
                    || !Uri.TryCreate(options.ReportingWebhookUrl, UriKind.Absolute, out var target)
                    || target.Scheme != "https" || !string.IsNullOrEmpty(target.UserInfo))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IRfLogUploadStore>();
                var claimed = await store.ClaimNotificationAsync(region, stoppingToken);
                if (claimed == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }
                var (upload, token) = claimed.Value;
                var success = false;
                try
                {
                    var body = JsonSerializer.Serialize(scope.ServiceProvider.GetRequiredService<RfLogIntegrationService>().Event(upload));
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    using var request = new HttpRequestMessage(HttpMethod.Post, target)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Add("X-RF-Event-Id", upload.Id);
                    request.Headers.Add("X-RF-Region", region);
                    request.Headers.Add("X-RF-Timestamp", timestamp);
                    request.Headers.Add("X-RF-Signature", "sha256=" + RfLogIntegrationService.SignWebhook(options.WebhookSigningSecret, timestamp, body));
                    using var response = await http.CreateClient("RfLogWebhook").SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                    success = response.IsSuccessStatusCode;
                    if (!success) logger.LogWarning("RF event {Id} notification attempt {Attempt}: HTTP {Status}",
                        upload.Id, upload.NotificationAttempts, (int)response.StatusCode);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("RF event {Id} notification attempt failed ({Type})", upload.Id, ex.GetType().Name);
                }
                await store.FinishNotificationAsync(upload.Id, region, token, success, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "RF notification dispatcher for {Region} unavailable; its durable events will be retried.", region);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
