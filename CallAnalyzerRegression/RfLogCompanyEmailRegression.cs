using System.Reflection;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using SignalTracker.Models;
using SignalTracker.Services;

internal static class RfLogCompanyEmailRegression
{
    // Real production registration/queue SQL against connection-local temporary shadows.
    // No permanent rows are changed, workers launched or SMTP messages sent.
    internal static async Task RunDatabaseAsync()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", "SignalTracker.LocalDevelopment", "secrets.json"), optional: true)
            .AddEnvironmentVariables().Build();
        foreach (var region in new[] { "IN", "TW" })
        {
            var value = configuration.GetConnectionString(RfLogUploadStore.ConnectionStringName(region))
                ?? throw new InvalidOperationException($"{region} database is not configured.");
            var settings = new MySqlConnectionStringBuilder(value)
                { GuidFormat = MySqlGuidFormat.None, DateTimeKind = MySqlDateTimeKind.Utc };
            await using var connection = new MySqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            foreach (var resource in new[] { "SignalTracker.RfLogSchema.sql", "SignalTracker.RfLogEmailSchema.sql" })
            {
                using var stream = typeof(RfLogUploadStore).Assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                var schema = (await reader.ReadToEndAsync()).Replace("CREATE TABLE IF NOT EXISTS", "CREATE TEMPORARY TABLE");
                await connection.ExecuteAsync(schema);
            }
            using (var stream = typeof(RfLogUploadStore).Assembly.GetManifestResourceStream("SignalTracker.RfLogCompanyEmailSchema.sql")!)
            using (var reader = new StreamReader(stream))
                await connection.ExecuteAsync(await reader.ReadToEndAsync());
            await connection.ExecuteAsync("CREATE TEMPORARY TABLE tbl_company(id INT PRIMARY KEY,email VARCHAR(254),company_name VARCHAR(200),status INT)");
            await connection.ExecuteAsync("CREATE TEMPORARY TABLE rf_log_email_seen(region VARCHAR(2),upload_id CHAR(36),recipient_id CHAR(36))");
            foreach (var table in new[] { "rf_log_uploads", "rf_log_report_recipients", "rf_log_email_deliveries", "tbl_company" })
            {
                var definition = await connection.QuerySingleAsync<dynamic>($"SHOW CREATE TABLE {table}");
                Check(((IDictionary<string, object>)definition).Values.Any(v => v.ToString()!.Contains("CREATE TEMPORARY TABLE")), "Fixture table must be temporary.");
            }
            await connection.ExecuteAsync("""
                INSERT INTO tbl_company VALUES(7,'  Company@Example.invalid  ','Company RF',1),(8,'other@example.invalid','Other company',1),
                  (9,'','No mailbox',1),(10,'not-an-email','Invalid mailbox',1),(11,'deleted@example.invalid','Deleted company',99)
                """);
            var upload = NewUpload(region, 7);
            await RfLogUploadStore.RegisterOnConnectionAsync(connection, upload, default);
            var recipient = await connection.QuerySingleAsync<dynamic>("SELECT * FROM rf_log_report_recipients");
            var recipientId = (string)recipient.id;
            Check((string)recipient.email == "company@example.invalid" && Convert.ToBoolean(recipient.company_default),
                "Upload registration automatically subscribes only its company profile email.");
            var firstCreated = (DateTime)recipient.created_at;
            await RfLogUploadStore.RegisterOnConnectionAsync(connection, upload, default);
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_report_recipients") == 1
                && await connection.ExecuteScalarAsync<DateTime>("SELECT created_at FROM rf_log_report_recipients") == firstCreated,
                "Registration retries preserve the same recipient and subscription window.");
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_report_recipients r JOIN rf_log_uploads u ON u.company_id=r.company_id AND u.region=r.region WHERE r.created_at<=u.created_at") == 1,
                "Company recipient is committed before the upload becomes visible to workers.");

            async Task Complete(RfLogUpload item) => await connection.ExecuteAsync("""
                UPDATE rf_log_uploads SET status='completed',report_reference=@report WHERE id=@Id AND region=@Region
                """, new { item.Id, item.Region, report = $"rf-logs/reports/{item.Region}/{item.CompanyId}/{item.Id}/test/report.pdf" });
            async Task Enqueue()
            {
                // MySQL cannot read and write one TEMPORARY table in the same statement.
                // Mirror just the already-delivered lookup, retaining the production queue
                // selection, company/profile checks and actual delivery unique constraint.
                await connection.ExecuteAsync("DELETE FROM rf_log_email_seen; INSERT INTO rf_log_email_seen SELECT region,upload_id,recipient_id FROM rf_log_email_deliveries");
                await connection.ExecuteAsync(Sql("EnqueueSql").Replace("FROM rf_log_email_deliveries d WHERE", "FROM rf_log_email_seen d WHERE"), new { region });
            }
            await Complete(upload);
            await Enqueue(); await Enqueue();
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_email_deliveries") == 1,
                "Completed PDF queues exactly one company email, including repeated completion/reconciliation.");

            // A manually subscribed address identical to the company email is promoted, not duplicated.
            await connection.ExecuteAsync("""
                INSERT INTO rf_log_report_recipients(id,region,company_id,email,name,enabled,created_at,company_default)
                VALUES(@id,@region,8,'other@example.invalid','Already subscribed',1,UTC_TIMESTAMP(6),0)
                """, new { id = Guid.NewGuid().ToString("D"), region });
            var other = NewUpload(region, 8);
            await RfLogUploadStore.RegisterOnConnectionAsync(connection, other, default);
            await Complete(other); await Enqueue();
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_report_recipients WHERE company_id=8") == 1,
                "Existing optional recipient and company mailbox reuse one case-insensitive subscription.");
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_email_deliveries WHERE company_id=8") == 1,
                "Another company's PDF has only its own company recipient.");
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_report_recipients WHERE region<>@region", new { region }) == 0,
                "Regional database receives only its own subscriptions.");

            var deliveryId = await connection.ExecuteScalarAsync<string>("SELECT id FROM rf_log_email_deliveries WHERE company_id=7");
            var token = Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync("UPDATE rf_log_email_deliveries SET lease_token=@token,lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 5 MINUTE) WHERE id=@deliveryId", new { token, deliveryId });
            async Task<bool> CanSend() => await connection.ExecuteScalarAsync<int>(Sql("CanSendSql"),
                new { Id = deliveryId, Region = region, CompanyId = 7, Token = token, Email = "company@example.invalid" }) == 1;
            Check(await CanSend(), "Current company mailbox may receive its generated report.");
            await connection.ExecuteAsync("UPDATE tbl_company SET email='new@example.invalid' WHERE id=7");
            Check(!await CanSend(), "Company profile changes fence an already claimed delivery to the old address.");
            await connection.ExecuteAsync("UPDATE rf_log_email_deliveries SET lease_until=NULL WHERE id=@deliveryId", new { deliveryId });
            await connection.ExecuteAsync(Sql("CancelInactiveSql"), new { region });
            Check(await connection.ExecuteScalarAsync<string>("SELECT status FROM rf_log_email_deliveries WHERE id=@deliveryId", new { deliveryId }) == "cancelled",
                "Changed company address cancels the obsolete queued delivery.");
            var fresh = NewUpload(region, 7);
            await RfLogUploadStore.RegisterOnConnectionAsync(connection, fresh, default);
            await Complete(fresh); await Enqueue();
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_report_recipients WHERE company_id=7 AND enabled=1 AND email='new@example.invalid'") == 1
                && await connection.ExecuteScalarAsync<int>("SELECT enabled FROM rf_log_report_recipients WHERE id=@recipientId", new { recipientId }) == 0,
                "Next drive uses the updated company mailbox and disables the former company mailbox.");
            Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_email_deliveries WHERE upload_id=@Id", new { upload.Id }) == 1,
                "New company mailbox is not backfilled with historical reports.");
            foreach (var company in new[] { 9, 10, 11, 999 })
            {
                var missing = NewUpload(region, company);
                await RfLogUploadStore.RegisterOnConnectionAsync(connection, missing, default);
                await Complete(missing); await Enqueue();
                Check(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rf_log_report_recipients WHERE company_id=@company", new { company }) == 0,
                    "Missing, invalid or deleted company mailbox never receives a report or blocks ZIP/PDF processing.");
            }
            Console.WriteLine($"{region}: automatic company email passed: transactional registration, queue deduplication, profile changes, company/region scope, no historical backfill and missing mailboxes. Temporary tables only; no SMTP sent.");
        }
    }

    private static RfLogUpload NewUpload(string region, int companyId)
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString("D"), Region = region, CompanyId = companyId, ClientId = "company-email-test",
            StagingKey = $"rf-logs/staging/{id}.zip", Metadata = new() { UploadId = id, FileName = "test.zip", FileSizeBytes = 100, SessionId = 1, UserId = 1 } };
    }
    private static string Sql(string field) => (string)typeof(RfLogEmailStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Company report email: " + message);
    }
}
