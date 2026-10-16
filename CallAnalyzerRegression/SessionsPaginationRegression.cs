using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
using SignalTracker.Controllers;
using SignalTracker.Models;
using SignalTracker.Services;
using StackExchange.Redis;

internal static class SessionsPaginationRegression
{
    internal static async Task RunAsync()
    {
        var scenario = new Scenario();
        var database = Proxy<IDatabase>((method, args) => method.Name switch
        {
            "StringGetAsync" => Task.FromResult(scenario.Cache.GetValueOrDefault((RedisKey)args![0]!, RedisValue.Null)),
            "StringSetAsync" => Store(args!),
            _ => throw new NotSupportedException(method.Name)
        });
        Task<bool> Store(object?[] args)
        {
            scenario.Cache[(RedisKey)args[0]!] = (RedisValue)args[1]!;
            return Task.FromResult(true);
        }
        var mux = Proxy<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            "get_IsConnected" => scenario.Connected,
            "GetDatabase" => database,
            _ => throw new NotSupportedException(method.Name)
        });
        var redis = new RedisService(mux);
        using var memory = new MemoryCache(new MemoryCacheOptions());

        async Task<JsonElement> Fetch(int page, int size = 100, int company = 45, string schema = "SessionsRegression",
            bool includeTotalCount = true, bool invalidate = false)
        {
            using var connection = new TestConnection(scenario, schema);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 29))).Options;
            using var db = new ApplicationDbContext(options);
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("UserTypeId", "3"), new Claim("UserId", "1"), new Claim("country_code", "IN")
                }, "test"))
            };
            http.Session = Proxy<ISession>((method, _) => method.Name == "TryGetValue" ? false : null);
            var accessor = new HttpContextAccessor { HttpContext = http };
            var controller = new AdminController(db, accessor, memory, redis, new UserScopeService(accessor))
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
            if (invalidate)
                await (Task)typeof(AdminController).GetMethod("InvalidateCalculatedCachesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(controller, null)!;
            var result = await controller.GetSessions(company_id: company, page: page, pageSize: size, includeTotalCount: includeTotalCount);
            Check(result is OkObjectResult, "GetSessions failed: " + JsonSerializer.Serialize((result as ObjectResult)?.Value));
            return JsonSerializer.SerializeToElement(((OkObjectResult)result).Value);
        }

        var first = await Fetch(1);
        var second = await Fetch(2);
        Check(first.GetProperty("TotalCount").GetInt32() == 250, "Incorrect total");
        Check(second.GetProperty("Data")[0].GetProperty("id").GetInt32() == 150, "Page two starts at wrong ID");
        Check(first.GetProperty("Data").EnumerateArray().Select(row => row.GetProperty("id").GetInt32())
            .Intersect(second.GetProperty("Data").EnumerateArray().Select(row => row.GetProperty("id").GetInt32())).Count() == 0,
            "Pages overlap");
        Check(scenario.IndexBuilds == 1 && scenario.PageReads == 2, "Next page rebuilt the matching index");
        var cached = await Fetch(2);
        Check(cached.GetProperty("Source").GetString() == "REDIS" && scenario.PageReads == 2, "Page cache was not used");
        Check(cached.GetProperty("Data")[0].GetProperty("notes").GetString() == "hello", "Cached note cleanup changed");
        var pageKey = scenario.Cache.Keys.Single(key => key.ToString().EndsWith(":page:2:size:100", StringComparison.Ordinal));
        scenario.Cache.TryRemove(pageKey, out _);
        var invalidatedPage = await Fetch(2);
        Check(invalidatedPage.GetProperty("Source").GetString() == "DATABASE" && scenario.PageReads == 3,
            "A Redis invalidation resurrected a stale local page");
        var smaller = await Fetch(2, size: 50);
        Check(smaller.GetProperty("Data")[0].GetProperty("id").GetInt32() == 200 && scenario.IndexBuilds == 1,
            "Changing page size rebuilt index or broke pagination");
        var last = await Fetch(int.MaxValue);
        Check(last.GetProperty("Page").GetInt32() == 3 && last.GetProperty("Data").GetArrayLength() == 50,
            "Out-of-range page did not clamp safely");

        await Fetch(1, company: 46);
        Check(scenario.IndexBuilds == 2, "Company caches were mixed");
        await Fetch(1, company: 46, schema: "DifferentDatabase");
        Check(scenario.IndexBuilds == 3, "Database caches were mixed");

        var builds = scenario.IndexBuilds;
        await Task.WhenAll(Enumerable.Range(1, 6).Select(page => Fetch(page, size: 10, company: 47)));
        Check(scenario.IndexBuilds == builds + 1, "Concurrent requests rebuilt the same matching index");

        var indexKey = scenario.Cache.Keys.First(key => key.ToString().EndsWith(":index", StringComparison.Ordinal));
        var expired = await redis.GetObjectAsync<AdminController.SessionsIndexCache>(indexKey.ToString());
        expired!.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await redis.SetObjectAsync(indexKey.ToString(), expired);
        memory.Compact(1.0);
        // Use the scope encoded by the cached index, without changing the requested filter set.
        var scopeCases = new[] { (45, "SessionsRegression"), (46, "SessionsRegression"), (46, "DifferentDatabase"), (47, "SessionsRegression") };
        builds = scenario.IndexBuilds;
        foreach (var (company, schema) in scopeCases) await Fetch(7, size: 17, company: company, schema: schema);
        Check(scenario.IndexBuilds == builds + 1, "Expired matching index was reused");
        var reads = scenario.PageReads;
        var empty = await Fetch(1, company: 48);
        Check(empty.GetProperty("TotalCount").GetInt32() == 0 && empty.GetProperty("TotalPages").GetInt32() == 0
            && empty.GetProperty("Data").GetArrayLength() == 0 && scenario.PageReads == reads,
            "Empty matching set should return no sessions without a page query");
        var indexRows = scenario.IndexRowsRead;
        var fast = await Fetch(2, company: 50, includeTotalCount: false);
        Check(fast.GetProperty("TotalCount").ValueKind == JsonValueKind.Null
            && fast.GetProperty("TotalPages").ValueKind == JsonValueKind.Null
            && fast.GetProperty("HasMore").GetBoolean() && !fast.GetProperty("IsTotalCountExact").GetBoolean(),
            "Fast mode must not invent totals");
        Check(fast.GetProperty("Data")[0].GetProperty("id").GetInt32() == 150 && scenario.IndexRowsRead == indexRows + 101,
            "Fast mode scanned all matches or ignored page offset");
        var fastLast = await Fetch(3, company: 50, includeTotalCount: false);
        Check(!fastLast.GetProperty("HasMore").GetBoolean() && fastLast.GetProperty("Data").GetArrayLength() == 50,
            "Fast mode last-page detection is incorrect");
        var fastEmpty = await Fetch(1, company: 48, includeTotalCount: false);
        Check(!fastEmpty.GetProperty("HasMore").GetBoolean() && fastEmpty.GetProperty("Data").GetArrayLength() == 0,
            "Fast mode empty-result detection is incorrect");

        scenario.Connected = false;
        await Fetch(1, company: 49);
        builds = scenario.IndexBuilds;
        var fallback = await Fetch(1, company: 49);
        Check(fallback.GetProperty("Source").GetString() == "MEMORY", "Redis outage did not use memory fallback");
        await Fetch(2, company: 49);
        Check(scenario.IndexBuilds == builds, "Memory fallback rebuilt matching IDs on next page");
        await Fetch(1, company: 49, invalidate: true);
        Check(scenario.IndexBuilds == builds + 1, "Mutation did not invalidate memory caches while Redis was unavailable");
        Console.WriteLine("Sessions pagination: cache reuse, scope isolation, expiry, concurrency and response checks passed.");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class Scenario
    {
        public ConcurrentDictionary<RedisKey, RedisValue> Cache { get; } = new();
        public int IndexBuilds;
        public int PageReads;
        public int IndexRowsRead;
        public bool Connected = true;
    }

    private sealed class TestConnection(Scenario scenario, string schema) : DbConnection
    {
        private ConnectionState _state;
        [AllowNull]
        public override string ConnectionString { get; set; } = $"Server=localhost;Database={schema};User ID=test";
        public override string Database => schema;
        public override string DataSource => "localhost";
        public override string ServerVersion => "8.0.29";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new TestCommand(this, scenario);
    }

    private sealed class TestCommand(DbConnection connection, Scenario scenario) : DbCommand
    {
        private readonly MySqlCommand _parameters = new();
        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new MySqlParameter();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new InvalidOperationException("GetSessions must never execute DDL");
        public override object ExecuteScalar()
        {
            Check(CommandText.Contains("INFORMATION_SCHEMA.TABLES"), "Unexpected scalar query: " + CommandText);
            return 1;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            Check(CommandText.Contains("nl.session_id = s.id") && CommandText.Contains("mRegistered=YES"),
                "Valid network-log filter was omitted");
            var table = new DataTable();
            if (CommandText.TrimStart().StartsWith("SELECT s.id", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref scenario.IndexBuilds);
                await Task.Delay(30, cancellationToken);
                table.Columns.Add("id", typeof(int));
                var emptyScope = DbParameterCollection.Contains("@targetCompanyId")
                    && Convert.ToInt32(DbParameterCollection["@targetCompanyId"].Value) == 48;
                if (!emptyScope)
                {
                    IEnumerable<int> ids = Enumerable.Range(1, 250).Reverse();
                    if (DbParameterCollection.Contains("@fetchCount"))
                        ids = ids.Skip(Convert.ToInt32(DbParameterCollection["@pageOffset"].Value))
                            .Take(Convert.ToInt32(DbParameterCollection["@fetchCount"].Value));
                    foreach (var id in ids) table.Rows.Add(id);
                }
                Interlocked.Add(ref scenario.IndexRowsRead, table.Rows.Count);
            }
            else
            {
                Interlocked.Increment(ref scenario.PageReads);
                var columns = new[] { "id", "company_id", "start_time", "end_time", "notes", "start_lat", "start_lon",
                    "end_lat", "end_lon", "capture_frequency", "distance_km", "start_address", "end_address", "type",
                    "CreatedBy", "mobile", "make", "model", "os", "operator_name" };
                foreach (var column in columns) table.Columns.Add(column, typeof(object));
                foreach (DbParameter parameter in DbParameterCollection)
                {
                    if (!parameter.ParameterName.StartsWith("@pageId", StringComparison.Ordinal)) continue;
                    var row = table.NewRow();
                    row["id"] = parameter.Value!;
                    row["notes"] = "hello@@internal";
                    table.Rows.Add(row);
                }
            }
            return table.CreateDataReader();
        }
    }
}
