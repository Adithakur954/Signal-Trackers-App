using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using SignalTracker.Controllers;
using SignalTracker.Models;

internal static class NetworkDiagnosticBatchRegression
{
    internal static void Run(IEnumerable<string> zipPaths)
    {
        using var connection = new CaptureConnection();
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 29))).Options);
        using var transaction = connection.BeginTransaction();
        context.Database.UseTransaction(transaction);
        var controller = new ProcessCSVController(context, null!, null!);
        var temp = Path.GetTempFileName();
        try
        {
            foreach (var isL3 in new[] { true, false })
            foreach (var count in new[] { 0, 1, 199, 200, 201, 401 })
            {
                WriteFixture(temp, count, "packet | UL-DCCH | cause=otherFailure");
                var result = Import(controller, connection, temp, isL3);
                Require(result.Success == (count > 0) && result.Inserted == count, "Boundary row count");
                Require(connection.Calls == (count + 199) / 200, "Boundary batch count");
                Require(connection.Rows.Select(r => Convert.ToInt32(r["row_no"])).SequenceEqual(Enumerable.Range(1, count)), "Row order");
                Require(connection.Rows.All(r => Equals(r["channel"], "UL-DCCH") && Equals(r["direction"], "UL") && Equals(r["cause"], "otherFailure")), "Fallback fields");
            }

            // Large details must split before the row-count limit and remain intact.
            var largeDetail = new string('x', 300_000) + " | DL-DCCH";
            WriteFixture(temp, 3, largeDetail);
            var large = Import(controller, connection, temp, true);
            Require(large.Success && large.Inserted == 3 && connection.Calls == 3, "Payload bound");
            Require(connection.Rows.All(r => Equals(r["detail"], largeDetail)), "Large details preserved");

            // Failed batches must not inflate the successful-row counter.
            WriteFixture(temp, 450, "UL-DCCH");
            connection.FailOnCall = 3;
            var failed = Import(controller, connection, temp, true);
            Require(!failed.Success && failed.Inserted == 400 && connection.Rows.Count == 400, "Failed batch accounting");
            connection.FailOnCall = 0;

            foreach (var zipPath in zipPaths)
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    && (e.Name.StartsWith("L3_", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("Event_", StringComparison.OrdinalIgnoreCase))))
                {
                    entry.ExtractToFile(temp, true);
                    var isL3 = entry.Name.StartsWith("L3_", StringComparison.OrdinalIgnoreCase);
                    var imported = Import(controller, connection, temp, isL3);
                    Require(imported.Success, $"Import {entry.Name}");
                    using var reader = new StreamReader(temp);
                    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
                    var originals = csv.GetRecords<dynamic>().Select(r => ((IDictionary<string, object?>)r)
                        .ToDictionary(p => p.Key, p => p.Value)).ToList();
                    var expectedRows = originals.Count(row => isL3 || !(Equals(row.GetValueOrDefault("event"), "CallState")
                        && Regex.IsMatch(Convert.ToString(row.GetValueOrDefault("detail")) ?? "", @"^\s*Idle\s*\(ended\)\s*$", RegexOptions.IgnoreCase)));
                    Require(imported.Inserted == expectedRows && connection.Rows.Count == expectedRows, "Fixture row count");
                    foreach (var stored in connection.Rows)
                    {
                        var original = originals[Convert.ToInt32(stored["row_no"]) - 1];
                        var expected = DiagnosticFieldRegression.Build(isL3, original);
                        foreach (var field in new[] { "Channel", "Direction", "Cause" })
                            Require(Equals(stored[field.ToLowerInvariant()], DiagnosticFieldRegression.Value(expected, field)), $"Fixture {field}");
                        Require(Equals(stored["timestamp_text"], original.GetValueOrDefault("timestamp")), "Fixture timestamp");
                    }
                    Console.WriteLine($"{entry.Name}: {imported.Inserted} rows, {connection.Calls} INSERT calls; fields verified.");
                }
            }
            Console.WriteLine("Network diagnostic batch regressions passed (no database writes).");
        }
        finally { File.Delete(temp); }
    }

    private static (bool Success, int Inserted) Import(ProcessCSVController controller, CaptureConnection connection, string file, bool isL3)
    {
        connection.Rows.Clear();
        connection.Calls = 0;
        var method = typeof(ProcessCSVController).GetMethod(isL3 ? "ProcessL3DiagnosticFile" : "ProcessEventDiagnosticFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = { 1, 2, file, 0, null };
        var success = (bool)method.Invoke(controller, args)!;
        return (success, (int)args[3]!);
    }

    private static void WriteFixture(string path, int count, string detail)
    {
        using var writer = new StreamWriter(path);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var header in new[] { "timestamp", "message", "event", "detail" }) csv.WriteField(header);
        csv.NextRecord();
        for (var i = 0; i < count; i++)
        {
            csv.WriteField("12:00:00.000"); csv.WriteField("Sample"); csv.WriteField("Sample"); csv.WriteField(detail); csv.NextRecord();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureConnection : DbConnection
    {
        internal List<Dictionary<string, object?>> Rows { get; } = new();
        internal int Calls;
        internal int FailOnCall;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "regression";
        public override string DataSource => "regression";
        public override string ServerVersion => "8.0.29";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new CaptureTransaction(this);
        protected override DbCommand CreateDbCommand() => new CaptureCommand(this);
    }

    private sealed class CaptureTransaction(CaptureConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class CaptureCommand(CaptureConnection connection) : DbCommand
    {
        private readonly MySqlCommand parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new MySqlParameter();
        public override int ExecuteNonQuery()
        {
            Require(DbTransaction is CaptureTransaction, "Upload transaction retained");
            if (++connection.Calls == connection.FailOnCall) throw new InvalidOperationException("Simulated write failure");
            var sql = Regex.Match(CommandText, @"INSERT INTO \w+\s*\(([^)]+)\)\s*VALUES\s*(.*);", RegexOptions.Singleline);
            Require(sql.Success, "Expected parameterized batch INSERT");
            var columns = sql.Groups[1].Value.Split(',').Select(s => s.Trim()).ToArray();
            var rows = Regex.Matches(sql.Groups[2].Value, @"\(([^)]+)\)");
            Require(rows.Count <= 200, "Row-count bound");
            foreach (Match row in rows)
            {
                var names = row.Groups[1].Value.Split(',').Select(s => s.Trim()).ToArray();
                Require(columns.Length == names.Length, "Column/parameter count");
                connection.Rows.Add(columns.Select((column, i) => (column, parameters.Parameters[names[i]].Value))
                    .ToDictionary(p => p.column, p => p.Value is DBNull ? null : p.Value));
            }
            return rows.Count;
        }
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        public override void Cancel() { }
        public override void Prepare() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) parameters.Dispose();
            base.Dispose(disposing);
        }
    }
}
