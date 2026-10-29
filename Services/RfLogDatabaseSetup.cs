using Dapper;
using MySqlConnector;

namespace SignalTracker.Services;

public static class RfLogDatabaseSetup
{
    // Explicit operator command only; never runs as part of web application startup.
    public static async Task<int> RunAsync(IConfiguration configuration, bool apply, CancellationToken ct = default)
    {
        var failures = 0;
        foreach (var region in new[] { "IN", "TW" })
        {
            try
            {
                var name = RfLogUploadStore.ConnectionStringName(region);
                var value = configuration.GetConnectionString(name);
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is not configured.");
                var settings = new MySqlConnectionStringBuilder(value) { ConnectionTimeout = 10 };
                await using var connection = new MySqlConnection(settings.ConnectionString);
                await connection.OpenAsync(ct);
                if (apply)
                {
                    foreach (var resourceName in new[] { "SignalTracker.RfLogSchema.sql", "SignalTracker.RfLogEmailSchema.sql", "SignalTracker.RfMobileCredentialSchema.sql" })
                    {
                        using var resource = typeof(RfLogDatabaseSetup).Assembly.GetManifestResourceStream(resourceName)
                            ?? throw new InvalidOperationException("Embedded RF schema is missing.");
                        using var reader = new StreamReader(resource);
                        await connection.ExecuteAsync(new CommandDefinition(await reader.ReadToEndAsync(ct), cancellationToken: ct));
                    }
                    if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                        SELECT COUNT(*) FROM information_schema.columns
                        WHERE table_schema=DATABASE() AND table_name='rf_log_report_recipients' AND column_name='company_default'
                        """, cancellationToken: ct)) == 0)
                    {
                        using var resource = typeof(RfLogDatabaseSetup).Assembly.GetManifestResourceStream("SignalTracker.RfLogCompanyEmailSchema.sql")!;
                        using var reader = new StreamReader(resource);
                        await connection.ExecuteAsync(new CommandDefinition(await reader.ReadToEndAsync(ct), cancellationToken: ct));
                    }
                }
                var columns = (await connection.QueryAsync<string>(new CommandDefinition("""
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema=DATABASE() AND table_name='rf_log_uploads'
                    """, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var required = new[] { "id", "region", "metadata", "status", "object_key", "report_token", "notify_token", "report_reference", "notify_after" };
                var missing = required.Where(column => !columns.Contains(column)).ToArray();
                Console.WriteLine($"{region}: connected to database '{settings.Database}'; RF schema {(missing.Length == 0 ? "ready" : "missing: " + string.Join(", ", missing))}.");
                if (missing.Length > 0) failures++;
                foreach (var (table, requiredColumns) in new[]
                {
                    ("rf_log_report_recipients", new[] { "id", "region", "company_id", "email", "name", "enabled", "company_default", "created_at" }),
                    ("rf_log_email_deliveries", new[] { "id", "upload_id", "recipient_id", "region", "company_id", "report_reference", "status", "attempts", "next_attempt", "lease_token", "lease_until", "created_at", "sent_at", "last_error" }),
                    ("rf_mobile_upload_credentials", new[] { "id", "region", "company_id", "user_id", "mobile_number", "company_code", "device_label", "token_hash", "created_at", "expires_at", "revoked_at" })
                })
                {
                    var actual = (await connection.QueryAsync<string>(new CommandDefinition("SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table", new { table }, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var absent = requiredColumns.Where(column => !actual.Contains(column)).ToArray();
                    Console.WriteLine($"{region}: {table} {(absent.Length == 0 ? "ready" : "missing: " + string.Join(", ", absent))}.");
                    if (absent.Length > 0) failures++;
                }
            }
            catch (Exception ex)
            {
                // Connection strings and passwords are never printed.
                Console.Error.WriteLine($"{region}: database {(apply ? "migration" : "check")} failed ({ex.GetType().Name}): {ex.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
