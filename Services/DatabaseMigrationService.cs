using System.Data;
using Microsoft.EntityFrameworkCore;
using SignalTracker.Models;

namespace SignalTracker.Services;

public sealed class DatabaseMigrationService
{
    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(
        ApplicationDbContext db,
        IWebHostEnvironment environment,
        ILogger<DatabaseMigrationService> logger)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await EnsureMigrationTableAsync(connection, cancellationToken);

            var migrationsPath = Path.Combine(_environment.ContentRootPath, "Database", "Migrations");
            if (!Directory.Exists(migrationsPath))
                return;

            foreach (var migrationPath in Directory.EnumerateFiles(migrationsPath, "*.sql").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var migrationId = Path.GetFileName(migrationPath);
                if (await IsAppliedAsync(connection, migrationId, cancellationToken))
                    continue;

                var sql = await File.ReadAllTextAsync(migrationPath, cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "INSERT INTO __schema_migrations (migration_id) VALUES (@migrationId);";
                    var parameter = record.CreateParameter();
                    parameter.ParameterName = "@migrationId";
                    parameter.Value = migrationId;
                    record.Parameters.Add(parameter);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                _logger.LogInformation("Applied database migration {MigrationId}.", migrationId);
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task EnsureMigrationTableAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS __schema_migrations (
                migration_id VARCHAR(255) NOT NULL PRIMARY KEY,
                applied_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(System.Data.Common.DbConnection connection, string migrationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __schema_migrations WHERE migration_id = @migrationId;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@migrationId";
        parameter.Value = migrationId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }
}
