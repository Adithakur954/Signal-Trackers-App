using System.Net.Mail;
using Dapper;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MySqlConnector;
using SignalTracker.Models;

namespace SignalTracker.Services;

public interface IRfLogEmailSender
{
    Task SendAsync(RfLogEmailDelivery delivery, RfLogUpload upload, byte[] pdf, CancellationToken ct);
}

// Reuses the existing regional SMTP configuration, but never its default recipient list.
public sealed class RfLogSmtpSender(IConfiguration configuration, IOptions<RfLogIntegrationOptions> options) : IRfLogEmailSender
{
    public async Task SendAsync(RfLogEmailDelivery delivery, RfLogUpload upload, byte[] pdf, CancellationToken ct)
    {
        // Validate region even when settings are explicitly configured in the environment.
        _ = RfLogUploadStore.ConnectionStringName(delivery.Region);
        if (!options.Value.Smtp.TryGetValue(delivery.Region, out var settings))
        {
            var connectionString = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(delivery.Region));
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Regional mail database is not configured.");
            await using var connection = new MySqlConnection(connectionString);
            settings = await connection.QueryFirstOrDefaultAsync<RfLogSmtpOptions>(new CommandDefinition("""
                SELECT SMTPServer Host,SMTPPort Port,UserName,Password,UserName FromAddress,SSLayer EnableSsl
                FROM m_email_setting WHERE m_Status_ID=1 ORDER BY ID DESC LIMIT 1
                """, cancellationToken: ct));
        }
        if (settings == null || string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535
            || !MailAddress.TryCreate(settings.FromAddress, out var from))
            throw new InvalidOperationException("Active regional SMTP settings are missing or invalid.");

        using var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress("SignalTracker", from.Address));
        mail.To.Add(new MailboxAddress(delivery.Name, delivery.Email));
        mail.Subject = $"RF drive report - Session {upload.Metadata.SessionId}";
        mail.MessageId = $"rf-{delivery.Region.ToLowerInvariant()}-{delivery.Id}@{from.Host}";
        var body = new BodyBuilder
        {
            TextBody = $"Hello{(string.IsNullOrWhiteSpace(delivery.Name) ? "" : " " + delivery.Name)},\n\nYour RF drive report for session {upload.Metadata.SessionId} is ready. The PDF report is attached.\n\nSignalTracker"
        };
        body.Attachments.Add($"RF-Report-{upload.Metadata.SessionId}-{upload.Id}.pdf", pdf, new ContentType("application", "pdf"));
        mail.Body = body.ToMessageBody();
        using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 60000 };
        await smtp.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings), ct);
        if (!string.IsNullOrWhiteSpace(settings.UserName)) await smtp.AuthenticateAsync(settings.UserName, settings.Password, ct);
        await smtp.SendAsync(mail, ct);
        // A failed QUIT after the SMTP server accepted DATA must not retry a successful email.
        try { await smtp.DisconnectAsync(true, ct); }
        catch { /* Client disposal closes the connection; the message is already accepted. */ }
    }

    public static SecureSocketOptions SocketOptions(RfLogSmtpOptions settings) => !settings.EnableSsl
        ? SecureSocketOptions.None
        : settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}

public sealed class RfLogReportEmailProcessor(IRfLogEmailStore emails, IRfLogUploadStore uploads,
    IRfLogStorage storage, IRfLogEmailSender sender, IOptions<RfLogIntegrationOptions> options,
    ILogger<RfLogReportEmailProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(string region, CancellationToken ct)
    {
        // Reconciliation makes enqueue durable across a crash immediately after PDF completion.
        await emails.EnqueueAsync(region, ct);
        var claim = await emails.ClaimAsync(region, ct);
        if (claim == null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2)); // Shorter than the five-minute delivery lease.
        var delivery = claim.Delivery;
        var success = false;
        string? error = null;
        try
        {
            var upload = await uploads.GetAsync(delivery.UploadId, region, timeout.Token);
            var prefix = $"rf-logs/reports/{region}/{delivery.CompanyId}/{delivery.UploadId}/";
            if (delivery.Region != region || upload == null || upload.Region != region || upload.CompanyId != delivery.CompanyId
                || upload.Status != "completed" || upload.ReportReference != delivery.ReportReference
                || !delivery.ReportReference.StartsWith(prefix, StringComparison.Ordinal)
                || delivery.ReportReference.Contains("..", StringComparison.Ordinal)
                || !delivery.ReportReference.EndsWith("/report.pdf", StringComparison.Ordinal))
                throw new InvalidDataException("Completed report reference does not match this recipient's region/company.");
            var pdf = await storage.ReadReportAsync(delivery.ReportReference, options.Value.MaxEmailAttachmentBytes, timeout.Token);
            if (!await emails.CanSendAsync(claim, timeout.Token))
                throw new InvalidDataException("Recipient is disabled or the email delivery lease is no longer current.");
            await sender.SendAsync(delivery, upload, pdf, timeout.Token);
            success = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return true; }
        catch (Exception ex)
        {
            // Email addresses, SMTP credentials and provider exception text stay out of logs/API errors.
            logger.LogWarning("RF report email {Region}:{DeliveryId} attempt {Attempt} failed ({Type}).", region, delivery.Id, delivery.Attempts, ex.GetType().Name);
            error = ex is InvalidDataException ? ex.Message : "Email delivery failed. Verify this region's SMTP/storage settings and retry.";
        }
        await emails.FinishAsync(claim, success, error, ct);
        return true;
    }
}

public sealed class RfLogReportEmailWorker(IServiceScopeFactory scopes, IOptions<RfLogIntegrationOptions> options,
    ILogger<RfLogReportEmailWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(RunAsync("IN", ct), RunAsync("TW", ct));

    private async Task RunAsync(string region, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (options.Value.IsConfigured && options.Value.ReportEmailsEnabled)
                {
                    using var scope = scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<RfLogReportEmailProcessor>().ProcessNextAsync(region, ct)) continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("RF report email queue {Region} unavailable ({Type}); other regions continue independently.", region, ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }
}
