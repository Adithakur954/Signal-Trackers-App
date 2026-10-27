using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Dapper;
using MySqlConnector;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class RfLogEmailRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("RF report email: " + message);
    }

    internal static async Task RunAsync(RfLogUpload india, RfLogUpload taiwan,
        RfLogIntegrationRegression.FakeStorage storage, RfLogIntegrationOptions options)
    {
        var uploads = new RfLogIntegrationRegression.MemoryStore();
        var emails = new MemoryEmailStore(uploads, options);
        var first = await emails.RegisterAsync("IN", 7, new() { Email = "first@example.invalid", Name = "First" }, default);
        var second = await emails.RegisterAsync("IN", 7, new() { Email = "second@example.invalid" }, default);
        await emails.RegisterAsync("TW", 7, new() { Email = "taiwan@example.invalid" }, default);
        await emails.RegisterAsync("IN", 8, new() { Email = "other-company@example.invalid" }, default);
        Check((await emails.RegisterAsync("IN", 7, new() { Email = "FIRST@example.invalid" }, default)).Id == first.Id,
            "case-insensitive registration reuses a single subscription");
        await uploads.RegisterAsync(india with { CreatedAtUtc = DateTime.UtcNow }, default);
        await uploads.RegisterAsync(taiwan with { CreatedAtUtc = DateTime.UtcNow }, default);
        var sender = new FakeSender { FailingEmail = first.Email };
        var processor = new RfLogReportEmailProcessor(emails, uploads, storage, sender, Options.Create(options), NullLogger<RfLogReportEmailProcessor>.Instance);
        var artifactCount = storage.Reports.Count;
        await processor.ProcessNextAsync("IN", default);
        Check(emails.Jobs.Count == 2 && sender.Sent.Count == 0, "only this company's registered India recipients are queued; SMTP failure stays pending");
        await processor.ProcessNextAsync("IN", default);
        Check(sender.Sent.Count == 1 && sender.Sent[0].Email == second.Email, "one failed recipient does not block another recipient");
        Check(!await processor.ProcessNextAsync("IN", default), "failed mail observes backoff; sent mail is not reclaimed");
        sender.FailingEmail = null;
        emails.MakeDue();
        await processor.ProcessNextAsync("IN", default);
        Check(sender.Sent.Count == 2 && sender.Sent.Count(job => job.Email == second.Email) == 1,
            "retry sends only the failed recipient without resending successful mail");
        Check(!await processor.ProcessNextAsync("IN", default), "reconciliation cannot enqueue a completed delivery twice");
        await processor.ProcessNextAsync("TW", default);
        Check(sender.Sent.Count == 3 && sender.Sent.Last().Region == "TW", "Taiwan recipients and reports remain independent");
        Check(storage.Reports.Count == artifactCount, "email retries do not regenerate reports or rewrite artifacts");
        Check(!await emails.RetryAsync("IN", 8, emails.Jobs[0].Id, default), "another company cannot retry delivery");

        var late = await emails.RegisterAsync("IN", 7, new() { Email = "late@example.invalid" }, default);
        late.CreatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        await emails.EnqueueAsync("IN", default);
        Check(emails.Jobs.Count == 3, "new subscriptions do not email historical reports");
        var freshId = Guid.NewGuid().ToString("D");
        var freshReference = $"rf-logs/reports/IN/7/{freshId}/test/report.pdf";
        storage.Reports[freshReference] = storage.Reports[india.ReportReference!];
        await uploads.RegisterAsync(india with { Id = freshId, ReportReference = freshReference, CreatedAtUtc = DateTime.UtcNow.AddSeconds(2),
            Metadata = india.Metadata with { UploadId = Guid.Parse(freshId) } }, default);
        await emails.EnqueueAsync("IN", default);
        await emails.DisableAsync("IN", 7, first.Id, default);
        await emails.DisableAsync("IN", 7, second.Id, default);
        await emails.DisableAsync("IN", 7, late.Id, default);
        Check(!await processor.ProcessNextAsync("IN", default) && emails.Jobs.Where(job => job.UploadId == freshId).All(job => job.Status == "cancelled"),
            "removing recipients cancels their queued mail");
        Check(sender.Sent.Count == 3, "disabled subscribers receive no queued PDF");

        var dead = emails.Jobs.First();
        dead.Status = "dead_letter";
        Check(!await emails.RetryAsync("IN", 7, dead.Id, default), "disabled recipients cannot be retried");
        await emails.RegisterAsync("IN", 7, new() { Email = dead.Email }, default);
        Check(await emails.RetryAsync("IN", 7, dead.Id, default), "operator can requeue dead-letter delivery for an active subscriber");
        await CheckSmtpAsync(india, storage.Reports[india.ReportReference!]);
        Console.WriteLine("RF email pipeline passed: registered recipients, company/region isolation, PDF attachment, recipient-specific retries, deduplication and unsubscribe.");
    }

    private static async Task CheckSmtpAsync(RfLogUpload upload, byte[] pdf)
    {
        Check(RfLogSmtpSender.SocketOptions(new() { Port = 465 }) == MailKit.Security.SecureSocketOptions.SslOnConnect,
            "GoDaddy's port 465 uses immediate TLS");
        Check(RfLogSmtpSender.SocketOptions(new() { Port = 587 }) == MailKit.Security.SecureSocketOptions.StartTls,
            "port 587 requires STARTTLS without plaintext downgrade");
        Check(RfLogSmtpSender.SocketOptions(new() { EnableSsl = false }) == MailKit.Security.SecureSocketOptions.None,
            "loopback-only test SMTP explicitly disables TLS");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var recipients = new List<string>();
        var content = new StringBuilder();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost RF SMTP capture");
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                { await writer.WriteLineAsync("250-localhost"); await writer.WriteLineAsync("250 SIZE 104857600"); }
                else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                { recipients.Add(line); await writer.WriteLineAsync("250 recipient accepted"); }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 send message");
                    while (await reader.ReadLineAsync(timeout.Token) is { } body && body != ".") content.AppendLine(body);
                    await writer.WriteLineAsync("250 message captured locally");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                else await writer.WriteLineAsync("250 OK");
            }
        }, timeout.Token);
        try
        {
            var options = new RfLogIntegrationOptions { Smtp = new() { ["IN"] = new()
            {
                Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                FromAddress = "reports@example.invalid", EnableSsl = false
            } } };
            var sender = new RfLogSmtpSender(new ConfigurationBuilder().Build(), Options.Create(options));
            var delivery = new RfLogEmailDelivery { Id = Guid.NewGuid().ToString("D"), Region = "IN", CompanyId = 7, Email = "recipient@example.invalid", Name = "RF team" };
            await sender.SendAsync(delivery, upload, pdf, timeout.Token);
            await server;
            var message = content.ToString();
            Check(recipients.Count == 1 && recipients[0].Contains(delivery.Email), "real SMTP transport addresses only the registered recipient");
            Check(message.Contains("Content-Type: application/pdf", StringComparison.OrdinalIgnoreCase)
                && message.Contains(Convert.ToBase64String(pdf.AsSpan(0, 12))), "real SMTP MIME includes the generated PDF attachment");
            Check(message.Contains($"<rf-in-{delivery.Id}@example.invalid>", StringComparison.OrdinalIgnoreCase), "stable delivery Message-ID reaches SMTP server");
            try
            {
                await sender.SendAsync(new() { Region = "TW" }, upload, pdf, timeout.Token);
                throw new Exception("Expected missing Taiwan SMTP rejection");
            }
            catch (InvalidOperationException) { }
            Console.WriteLine("RF SMTP transport passed: loopback-only SMTP, one recipient, PDF MIME attachment, stable Message-ID and no regional fallback.");
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }

    internal static async Task CheckHttpAsync(HttpClient http)
    {
        using var normal = await http.GetAsync("/api/rf-logs/recipients");
        Check(normal.StatusCode == HttpStatusCode.Forbidden, "ordinary users cannot manage company report subscriptions");
        http.DefaultRequestHeaders.Add("X-Test-Admin", "true");
        using var added = await http.PostAsJsonAsync("/api/rf-logs/recipients", new { email = "reports@example.invalid", name = "RF team" });
        Check(added.IsSuccessStatusCode, "company admin can register a recipient with existing login/CSRF");
        using var created = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("recipient").GetProperty("id").GetString();
        http.DefaultRequestHeaders.Remove("X-Test-Region");
        using var india = await http.GetAsync("/api/rf-logs/recipients");
        using var indiaJson = JsonDocument.Parse(await india.Content.ReadAsStringAsync());
        Check(indiaJson.RootElement.GetProperty("recipients").GetArrayLength() == 0, "Taiwan recipients cannot appear in India registration API");
        using var wrongDelete = await http.DeleteAsync($"/api/rf-logs/recipients/{id}");
        Check(wrongDelete.StatusCode == HttpStatusCode.NotFound, "India admin cannot remove a Taiwan recipient with the same company ID");
        http.DefaultRequestHeaders.Add("X-Test-Region", "TW");
        http.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var csrf = await http.DeleteAsync($"/api/rf-logs/recipients/{id}");
        Check(csrf.StatusCode == HttpStatusCode.BadRequest, "recipient removal enforces CSRF");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "test-csrf");
        using var removed = await http.DeleteAsync($"/api/rf-logs/recipients/{id}");
        Check(removed.StatusCode == HttpStatusCode.NoContent, "own-region admin can unsubscribe a recipient");
        Console.WriteLine("RF email HTTP passed: admin registration, region isolation, CSRF and unsubscribe.");
    }

    // Explicit operator regression for the newly migrated, empty integration tables.
    // Never sends mail and removes only its own UUID fixtures. Refuses an enabled integration.
    internal static async Task RunDatabaseAsync(string? leftoverFixtureId = null)
    {
        var secretsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", "SignalTracker.LocalDevelopment", "secrets.json");
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile(secretsPath, optional: true).AddEnvironmentVariables().Build();
        Check(!configuration.GetValue<bool>("RfLogIntegration:Enabled"), "database fixture regression requires the integration to be disabled");
        var wrapped = Options.Create(new RfLogIntegrationOptions { MaxEmailAttempts = 2 });
        var emails = new RfLogEmailStore(configuration, wrapped);
        var uploads = new RfLogUploadStore(configuration, wrapped);
        foreach (var region in new[] { "IN", "TW" })
        {
            await using var connection = new MySqlConnection(configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region)));
            await connection.OpenAsync();
            // Optional recovery of one explicitly identified row from an interrupted test.
            if (leftoverFixtureId != null)
            {
                Check(Guid.TryParse(leftoverFixtureId, out _), "cleanup requires an explicit fixture UUID");
                await connection.ExecuteAsync("DELETE FROM rf_log_report_recipients WHERE id=@leftoverFixtureId AND company_id=@companyId AND name='Database regression only' AND email LIKE 'rf-check-%@example.invalid'",
                    new { leftoverFixtureId, companyId = int.MaxValue });
            }
            Check(await connection.ExecuteScalarAsync<int>("SELECT (SELECT COUNT(*) FROM rf_log_report_recipients)+(SELECT COUNT(*) FROM rf_log_email_deliveries)") == 0,
                "database fixture regression requires empty email/subscription tables; existing data is never changed");
            const int companyId = int.MaxValue;
            var uploadIds = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
            string? recipientId = null;
            var request = new RfLogRecipientRequest { Email = $"rf-check-{Guid.NewGuid():N}@example.invalid", Name = "Database regression only" };
            try
            {
                var recipient = await emails.RegisterAsync(region, companyId, request, default);
                recipientId = recipient.Id;
                Check((await emails.RegisterAsync(region, companyId, request with { Email = request.Email.ToUpperInvariant() }, default)).Id == recipientId,
                    "real database subscription idempotency");
                Check((await emails.RecipientsAsync(region, companyId - 1, default)).Count == 0, "real database company isolation");
                async Task InsertCompleted(string id)
                {
                    await uploads.RegisterAsync(new() { Id = id, Region = region, CompanyId = companyId, ClientId = "rf-email-regression",
                        StagingKey = $"rf-logs/checks/{id}.zip", Metadata = new() { UploadId = Guid.Parse(id), FileName = "regression.zip", FileSizeBytes = 100, SessionId = 1, UserId = 1 } }, default);
                    await connection.ExecuteAsync("UPDATE rf_log_uploads SET status='completed', report_reference=@report WHERE id=@id AND company_id=@companyId AND region=@region",
                        new { id, companyId, region, report = $"rf-logs/reports/{region}/{companyId}/{id}/check/report.pdf" });
                }
                await InsertCompleted(uploadIds[0]);
                var storedUpload = await uploads.GetAsync(uploadIds[0], region, default);
                Check(storedUpload?.Metadata.UploadId == Guid.Parse(uploadIds[0]) && storedUpload.Status == "completed",
                    "real SQL upload UUID and JSON metadata materialize correctly");
                Check(!await uploads.OwnerExistsAsync(storedUpload!.Metadata, companyId, region, default),
                    "fixture metadata cannot bypass real session/company ownership");
                await emails.EnqueueAsync(region, default);
                await emails.EnqueueAsync(region, default);
                Check((await emails.DeliveriesAsync(region, companyId, uploadIds[0], default)).Count == 1, "real SQL queue deduplicates completed reports");
                var claim = await emails.ClaimAsync(region, default) ?? throw new Exception("Expected SQL email claim");
                Check(await emails.CanSendAsync(claim, default), "real SQL active lease is current");
                Check(!await emails.RetryAsync(region, companyId - 1, claim.Delivery.Id, default), "cross-company retry denied by SQL");
                await emails.FinishAsync(claim, false, "Simulated SMTP failure; no mail sent", default);
                Check(await emails.ClaimAsync(region, default) == null, "real SQL email backoff");
                await connection.ExecuteAsync("UPDATE rf_log_email_deliveries SET next_attempt=UTC_TIMESTAMP(6) WHERE id=@id AND company_id=@companyId", new { id = claim.Delivery.Id, companyId });
                var second = await emails.ClaimAsync(region, default) ?? throw new Exception("Expected retry SQL claim");
                Check(!await emails.CanSendAsync(claim, default), "real SQL old delivery token is fenced");
                await emails.FinishAsync(second, false, "Simulated final failure; no mail sent", default);
                Check((await emails.DeliveriesAsync(region, companyId, uploadIds[0], default)).Single().Status == "dead_letter", "real SQL attempts become dead-letter");
                Check(await emails.RetryAsync(region, companyId, second.Delivery.Id, default), "real SQL operator retry");
                var last = await emails.ClaimAsync(region, default) ?? throw new Exception("Expected manual retry claim");
                await emails.FinishAsync(last, true, null, default);
                Check((await emails.DeliveriesAsync(region, companyId, uploadIds[0], default)).Single().Status == "sent", "real SQL delivery accepted state");
                await InsertCompleted(uploadIds[1]);
                await emails.EnqueueAsync(region, default);
                var cancelled = await emails.ClaimAsync(region, default) ?? throw new Exception("Expected cancellation SQL claim");
                await emails.DisableAsync(region, companyId, recipientId, default);
                Check(!await emails.CanSendAsync(cancelled, default), "real SQL unsubscribe fences in-progress delivery");
                Check((await emails.DeliveriesAsync(region, companyId, uploadIds[1], default)).Single().Status == "cancelled", "real SQL unsubscribe cancels queued email");
                Console.WriteLine($"{region}: real MySQL email registration, queue, leases, retries and unsubscribe passed; no SMTP call made.");
                var hasSmtpTable = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='m_email_setting'") > 0;
                var hasSmtp = hasSmtpTable && await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM m_email_setting WHERE m_Status_ID=1 AND SMTPServer<>'' AND SMTPPort<>'' AND UserName<>''") > 0;
                Console.WriteLine($"{region}: existing active SMTP settings {(hasSmtp ? "present (delivery not tested)" : "not configured")}.");
            }
            finally
            {
                await connection.ExecuteAsync("DELETE FROM rf_log_email_deliveries WHERE upload_id IN @uploadIds AND company_id=@companyId AND region=@region", new { uploadIds, companyId, region }, commandTimeout: 10);
                await connection.ExecuteAsync("DELETE FROM rf_log_report_recipients WHERE email=@Email AND company_id=@companyId AND region=@region", new { request.Email, companyId, region }, commandTimeout: 10);
                await connection.ExecuteAsync("DELETE FROM rf_log_uploads WHERE id IN @uploadIds AND company_id=@companyId AND region=@region", new { uploadIds, companyId, region }, commandTimeout: 10);
            }
        }
        Console.WriteLine("Regional MySQL email regression passed; all test rows removed.");
    }

    private sealed class FakeSender : IRfLogEmailSender
    {
        public string? FailingEmail;
        public List<RfLogEmailDelivery> Sent { get; } = new();
        public Task SendAsync(RfLogEmailDelivery delivery, RfLogUpload upload, byte[] pdf, CancellationToken ct)
        {
            Check(pdf.Length > 5 && pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "attachment contains the generated PDF");
            if (delivery.Email == FailingEmail) throw new IOException("Simulated SMTP outage");
            Sent.Add(delivery);
            return Task.CompletedTask;
        }
    }

    internal sealed class MemoryEmailStore(RfLogIntegrationRegression.MemoryStore uploads, RfLogIntegrationOptions options) : IRfLogEmailStore
    {
        private readonly Dictionary<(string Region, int Company, string Email), RfLogRecipient> recipients = new();
        private readonly Dictionary<string, string> tokens = new();
        private readonly Dictionary<string, DateTime> due = new();
        public List<RfLogEmailDelivery> Jobs { get; } = new();
        public void MakeDue() { foreach (var job in Jobs) due[job.Id] = DateTime.MinValue; }
        public Task<IReadOnlyList<RfLogRecipient>> RecipientsAsync(string region, int companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RfLogRecipient>>(recipients.Where(item => item.Key.Region == region && item.Key.Company == companyId).Select(item => item.Value).ToArray());
        public Task<RfLogRecipient> RegisterAsync(string region, int companyId, RfLogRecipientRequest request, CancellationToken ct)
        {
            var email = RfLogEmailStore.NormalizeEmail(request);
            if (!recipients.TryGetValue((region, companyId, email), out var row))
            {
                row = new() { Id = Guid.NewGuid().ToString("D"), CreatedAtUtc = DateTime.UtcNow };
                recipients[(region, companyId, email)] = row;
            }
            else if (!row.Enabled) row.CreatedAtUtc = DateTime.UtcNow;
            row.Email = email; row.Name = request.Name; row.Enabled = true;
            return Task.FromResult(row);
        }
        public Task<bool> DisableAsync(string region, int companyId, string id, CancellationToken ct)
        {
            var row = recipients.FirstOrDefault(pair => pair.Key.Region == region && pair.Key.Company == companyId && pair.Value.Id == id).Value;
            if (row != null)
            {
                row.Enabled = false;
                foreach (var job in Jobs.Where(job => job.Region == region && job.CompanyId == companyId && job.RecipientId == id && job.Status == "pending"))
                {
                    job.Status = "cancelled";
                    tokens.Remove(job.Id);
                }
            }
            return Task.FromResult(row != null);
        }
        public Task EnqueueAsync(string region, CancellationToken ct)
        {
            foreach (var upload in uploads.Rows.Values.Where(row => row.Region == region && row.Status == "completed"))
            foreach (var recipient in recipients.Where(pair => pair.Key.Region == region && pair.Key.Company == upload.CompanyId
                && pair.Value.Enabled && pair.Value.CreatedAtUtc <= upload.CreatedAtUtc).Select(pair => pair.Value))
            {
                if (Jobs.Any(job => job.Region == region && job.UploadId == upload.Id && job.RecipientId == recipient.Id)) continue;
                Jobs.Add(new() { Id = Guid.NewGuid().ToString("D"), UploadId = upload.Id, RecipientId = recipient.Id,
                    Region = region, CompanyId = upload.CompanyId, Email = recipient.Email, Name = recipient.Name,
                    ReportReference = upload.ReportReference!, Status = "pending" });
            }
            return Task.CompletedTask;
        }
        private bool Active(RfLogEmailDelivery job) => recipients.TryGetValue((job.Region, job.CompanyId, job.Email), out var recipient) && recipient.Enabled;
        public Task<RfLogEmailClaim?> ClaimAsync(string region, CancellationToken ct)
        {
            foreach (var job in Jobs.Where(job => job.Region == region && job.Status == "pending"))
            {
                if (!Active(job)) { job.Status = "cancelled"; continue; }
                if (tokens.ContainsKey(job.Id) || due.GetValueOrDefault(job.Id) > DateTime.UtcNow) continue;
                job.Attempts++;
                var token = Guid.NewGuid().ToString("N");
                tokens[job.Id] = token;
                return Task.FromResult<RfLogEmailClaim?>(new(job, token));
            }
            return Task.FromResult<RfLogEmailClaim?>(null);
        }
        public Task<bool> CanSendAsync(RfLogEmailClaim claim, CancellationToken ct) => Task.FromResult(Active(claim.Delivery) && tokens.GetValueOrDefault(claim.Delivery.Id) == claim.Token);
        public Task FinishAsync(RfLogEmailClaim claim, bool success, string? error, CancellationToken ct)
        {
            var job = claim.Delivery;
            if (tokens.GetValueOrDefault(job.Id) != claim.Token) return Task.CompletedTask;
            job.Status = success ? "sent" : job.Attempts >= options.MaxEmailAttempts ? "dead_letter" : "pending";
            job.Error = error;
            if (success) job.SentAtUtc = DateTime.UtcNow;
            due[job.Id] = DateTime.UtcNow.AddSeconds(30);
            tokens.Remove(job.Id);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RfLogEmailDelivery>> DeliveriesAsync(string region, int companyId, string? uploadId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RfLogEmailDelivery>>(Jobs.Where(job => job.Region == region && job.CompanyId == companyId && (uploadId == null || job.UploadId == uploadId)).ToArray());
        public Task<bool> RetryAsync(string region, int companyId, string id, CancellationToken ct)
        {
            var job = Jobs.FirstOrDefault(job => job.Region == region && job.CompanyId == companyId && job.Id == id && job.Status == "dead_letter" && Active(job));
            if (job == null) return Task.FromResult(false);
            job.Status = "pending"; job.Attempts = 0; job.Error = null; due[job.Id] = DateTime.MinValue;
            return Task.FromResult(true);
        }
    }
}
