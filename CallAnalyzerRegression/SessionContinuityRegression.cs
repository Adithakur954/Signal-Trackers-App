using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SignalTracker.Controllers;
using SignalTracker.Middleware;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;

internal static class SessionContinuityRegression
{
    public static async Task RunAsync()
    {
        var configuration = new ConfigurationBuilder().Build();
        Check(SessionSecurity.KeepSignedIn(configuration), "Persistent sign-in must be enabled by default");
        Check(SessionSecurity.CookieLifetime(configuration) == TimeSpan.FromDays(365), "Persistent cookie lifetime changed");
        var timedHttp = new DefaultHttpContext();
        var responseFeature = new StartingResponseFeature();
        timedHttp.Features.Set<IHttpResponseFeature>(responseFeature);
        await new SessionValidationUnavailableMiddleware(context =>
        {
            context.Items["st.authentication.ms"] = 12.5d;
            context.Response.Headers["Server-Timing"] = "total;dur=2";
            return Task.CompletedTask;
        }).InvokeAsync(timedHttp);
        await responseFeature.FireStartingAsync();
        var timings = timedHttp.Response.Headers["Server-Timing"].ToString();
        Check(timings.Contains("total;dur=2") && timings.Contains("authentication;dur=12.5") && timings.Contains("request;dur="),
            "Request timing must retain controller measurements and include authentication");
        var legacy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Security:KeepSignedInUntilLogout"] = "false", ["Security:SessionIdleMinutes"] = "5" }).Build();
        Check(SessionSecurity.IdleSeconds(legacy) == 300 && SessionSecurity.CookieLifetime(legacy) == TimeSpan.FromMinutes(5),
            "Explicit idle-timeout configuration must still work");

        var authentication = new RecordingAuthentication();
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IAuthenticationService>(authentication).AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        CookieValidatePrincipalContext Context()
        {
            var http = new DefaultHttpContext { RequestServices = services, Session = new TestSession() };
            http.Response.Body = new MemoryStream();
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
            { new Claim("UserId", "1"), new Claim("country_code", "IN"), new Claim("LoginLockValue", "old") }, "test"));
            return new CookieValidatePrincipalContext(http,
                new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)), new CookieAuthenticationOptions(),
                new AuthenticationTicket(user, new AuthenticationProperties(), "Cookies"));
        }
        var key = SessionSecurity.LockKey(1, "IN");
        var current = Context();
        await SessionSecurity.ValidateLoginSessionAsync(current, new TestStore(LoginSessionState.Current), key, "old", 1000, true);
        Check(current.Principal != null && authentication.SignOuts == 0, "A valid DB session must survive Redis unavailability");
        var started = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
        current.Properties.Items[SessionSecurity.StartedProperty] = started;
        current.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        current.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(5);
        SessionSecurity.RenewPersistentTicket(current);
        Check(current.ShouldRenew && current.Properties.IsPersistent
            && current.Properties.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(364)
            && current.Properties.Items[SessionSecurity.StartedProperty] == started,
            "An old six-hour ticket must renew with the persistent lifetime without changing original sign-in time");

        var keepAliveHttp = Context().HttpContext;
        keepAliveHttp.User = current.Principal!;
        var home = (HomeController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HomeController));
        home.ControllerContext = new ControllerContext { HttpContext = keepAliveHttp };
        Check(home.KeepAlive() is OkObjectResult, "A valid auth cookie must not depend on recycled in-memory session values");

        var unavailable = Context();
        var middleware = new SessionValidationUnavailableMiddleware(_ => SessionSecurity.ValidateLoginSessionAsync(
            unavailable, new TestStore(LoginSessionState.Unavailable), key, "old", 1000, true));
        await middleware.InvokeAsync(unavailable.HttpContext);
        Check(unavailable.HttpContext.Response.StatusCode == 503 && authentication.SignOuts == 0
            && unavailable.Principal != null && !unavailable.HttpContext.Response.Headers.ContainsKey("Set-Cookie"),
            "Temporary validation failure must block the request without deleting the cookie");
        var missingConfiguration = Context();
        missingConfiguration.Properties.Items[SessionSecurity.StartedProperty] = DateTimeOffset.UtcNow.ToString("O");
        var configMiddleware = new SessionValidationUnavailableMiddleware(_ => SessionSecurity.ValidateAsync(
            missingConfiguration, true, TimeSpan.FromDays(1), 1000));
        await configMiddleware.InvokeAsync(missingConfiguration.HttpContext);
        Check(missingConfiguration.HttpContext.Response.StatusCode == 503 && authentication.SignOuts == 0,
            "Temporary server configuration failure must not delete the cookie");
        foreach (var state in new[] { LoginSessionState.Missing, LoginSessionState.Replaced })
        {
            var invalid = Context();
            await SessionSecurity.ValidateLoginSessionAsync(invalid, new TestStore(state), key, "old", 1000, true);
            Check(invalid.Principal == null && invalid.HttpContext.Response.Headers.ContainsKey("X-Session-Invalidated"),
                "Logout/replacement must invalidate the old principal");
        }

        var stateDb = new LockDatabase();
        using var connection = new LockConnection(stateDb);
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 29))).Options);
        var accountSql = SessionSecurity.AccountQuery(db, 1, DateTime.UtcNow.Date).ToQueryString();
        Check(accountSql.Contains("EXISTS") && accountSql.Contains("tbl_company_user_license_issued") && accountSql.Contains("WHERE"),
            "User and authoritative license validation must translate into one scoped SQL query");
        var locks = new LoginLockFallbackService(db, configuration, NullLogger<LoginLockFallbackService>.Instance);
        Check(await locks.TrySetStringWhenNotExistsAsync(key, "old", 1000) == RedisSetWhenNotExistsResult.Set,
            "First login must acquire the DB session");
        Check(await locks.TrySetStringWhenNotExistsAsync(key, "other", 1000) == RedisSetWhenNotExistsResult.AlreadyExists,
            "Second ordinary login must not silently replace ownership");
        stateDb.Rows[key] = ("old", DateTime.UtcNow.AddHours(-1));
        var readsBeforeRenewal = stateDb.Reads;
        var transactionsBeforeRenewal = stateDb.Transactions;
        Check(await locks.ValidateAndRenewAsync(key, "old", 1000, true) == LoginSessionState.Current
            && stateDb.Rows[key].Expires > DateTime.UtcNow, "Persistent session must survive an old idle TTL");
        Check(stateDb.Reads == readsBeforeRenewal && stateDb.Transactions == transactionsBeforeRenewal,
            "A matching login must renew without a transaction or preliminary read");
        stateDb.Rows[key] = ("old", DateTime.UtcNow.AddHours(-1));
        Check(await locks.ValidateAndRenewAsync(key, "old", 1000, false) == LoginSessionState.Missing,
            "Opt-in idle expiry must still be enforced");
        await locks.SetStringAsync(key, "new", 1000);
        Check(await locks.ValidateAndRenewAsync(key, "old", 1000, true) == LoginSessionState.Replaced
            && stateDb.Rows[key].Value == "new", "Old renewal must never overwrite replacement ownership");
        await locks.DeleteIfMatchesAsync(key, "old");
        Check(stateDb.Rows[key].Value == "new", "Old logout must not revoke the replacement session");
        stateDb.Fail = true;
        Check(await locks.ValidateAndRenewAsync(key, "new", 1000, true) == LoginSessionState.Unavailable,
            "Database failure must be distinct from a missing/revoked session");
        stateDb.Fail = false;

        var logoutContext = Context();
        logoutContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim("UserId", "1"), new Claim("country_code", "IN"), new Claim("LoginLockValue", "new") }, "test"));
        var controller = new AuthController(db, NullLogger<AuthController>.Instance, configuration, null!, new RedisService(null), locks)
        { ControllerContext = new ControllerContext { HttpContext = logoutContext.HttpContext } };
        Check(await controller.Logout() is OkObjectResult && !stateDb.Rows.ContainsKey(key),
            "Explicit logout must revoke the DB session when Redis is offline");
        Check(await locks.ValidateAndRenewAsync(key, "new", 1000, true) == LoginSessionState.Missing,
            "An explicitly logged-out cookie must never recreate its session");
        Console.WriteLine("Session continuity: persistent sign-in, outage 503, takeover, owner-safe renewal and offline logout checks passed.");
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class StartingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = new();
        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));
        public async Task FireStartingAsync()
        {
            foreach (var (callback, state) in _callbacks.AsEnumerable().Reverse()) await callback(state);
        }
    }

    private sealed class TestStore(LoginSessionState state) : ILoginSessionStore
    {
        public Task<LoginSessionState> ValidateAndRenewAsync(string key, string expectedValue, int ttlSeconds,
            bool allowExpired, CancellationToken cancellationToken = default) => Task.FromResult(state);
    }
    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public int SignOuts;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { SignOuts++; return Task.CompletedTask; }
    }
    private sealed class TestSession : ISession
    {
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => Array.Empty<string>();
        public void Clear() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public void Set(string key, byte[] value) { }
        public bool TryGetValue(string key, out byte[] value) { value = Array.Empty<byte>(); return false; }
    }
    private sealed class LockDatabase
    {
        public Dictionary<string, (string Value, DateTime Expires)> Rows { get; } = new();
        public bool Fail;
        public int Reads;
        public int Transactions;
    }
    private sealed class LockConnection(LockDatabase database) : DbConnection
    {
        private ConnectionState _state;
        [AllowNull] public override string ConnectionString { get; set; } = "Server=localhost;Database=SessionContinuityTest;User ID=test";
        public override string Database => "SessionContinuityTest";
        public override string DataSource => "localhost";
        public override string ServerVersion => "8.0.29";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        { database.Transactions++; return new LockTransaction(this); }
        protected override DbCommand CreateDbCommand() => new LockCommand(this, database);
    }
    private sealed class LockTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }
    private sealed class LockCommand(DbConnection connection, LockDatabase database) : DbCommand
    {
        private readonly MySqlCommand _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
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
        private string Key => Convert.ToString(_parameters.Parameters["@key"].Value)!;
        private string Value => Convert.ToString(_parameters.Parameters["@value"].Value)!;
        public override int ExecuteNonQuery()
        {
            if (database.Fail) throw new IOException("Simulated DB outage");
            var sql = CommandText.TrimStart();
            if (sql.StartsWith("CREATE TABLE")) return 0;
            if (sql.StartsWith("DELETE"))
            {
                if (!database.Rows.TryGetValue(Key, out var row)) return 0;
                var remove = sql.Contains("@value") ? row.Value == Value : row.Expires <= DateTime.UtcNow;
                return remove && database.Rows.Remove(Key) ? 1 : 0;
            }
            if (sql.StartsWith("INSERT IGNORE") && database.Rows.ContainsKey(Key)) return 0;
            if (sql.StartsWith("UPDATE"))
            {
                if (!database.Rows.TryGetValue(Key, out var row) || row.Value != Value) return 0;
                if (_parameters.Parameters.Contains("@allowExpired")
                    && Convert.ToInt32(_parameters.Parameters["@allowExpired"].Value) == 0 && row.Expires <= DateTime.UtcNow) return 0;
                database.Rows[Key] = (Value, Convert.ToDateTime(_parameters.Parameters["@expires"].Value));
                return 1;
            }
            if (sql.StartsWith("INSERT"))
            {
                database.Rows[Key] = (Value, Convert.ToDateTime(_parameters.Parameters["@expires"].Value));
                return 1;
            }
            throw new NotSupportedException(sql);
        }
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            database.Reads++;
            if (database.Fail) throw new IOException("Simulated DB outage");
            var table = new DataTable();
            table.Columns.Add("lock_value", typeof(string));
            table.Columns.Add("live", typeof(bool));
            if (database.Rows.TryGetValue(Key, out var row)) table.Rows.Add(row.Value, row.Expires > DateTime.UtcNow);
            return table.CreateDataReader();
        }
    }
}
