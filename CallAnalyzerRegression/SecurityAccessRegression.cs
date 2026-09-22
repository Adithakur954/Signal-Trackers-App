using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SignalTracker.Controllers;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;

internal static class SecurityAccessRegression
{
    private static ClaimsPrincipal User(int id = 1, int company = 10, int role = 1, string region = "IN", string password = "stored-test-hash") =>
        new(new ClaimsIdentity([new Claim("UserId", id.ToString()), new Claim("CompanyId", company.ToString()),
            new Claim("UserTypeId", role.ToString()), new Claim("country_code", region),
            new Claim("CredentialVersion", SessionSecurity.CredentialVersion(password))], "regression"));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        var projects = new[]
        {
            new tbl_project { id = 1, company_id = 10 },
            new tbl_project { id = 2, company_id = 20, created_by_user_id = 1 },
            new tbl_project { id = 3, company_id = null, created_by_user_id = 1 },
            new tbl_project { id = 4, company_id = null, created_by_user_id = 2 },
            new tbl_project { id = 5, company_id = null }
        }.AsQueryable();
        Check(ResourceAccess.Projects(projects, User()).Select(row => row.id).SequenceEqual(new[] { 1, 3 }), "User must see own company and own unassigned project only.");
        Check(ResourceAccess.Projects(projects, User(company: 0)).Select(row => row.id).SequenceEqual(new[] { 3 }), "Missing company must not grant global scope.");
        Check(!ResourceAccess.Projects(projects, new ClaimsPrincipal()).Any(), "Anonymous project scope must be empty.");
        Check(ResourceAccess.Projects(projects, User(role: 3)).Count() == 5, "Super-admin project scope must remain available.");
        var users = new[] { new tbl_user { id = 1, company_id = 10 }, new tbl_user { id = 2, company_id = 20 }, new tbl_user { id = 3, company_id = 10 } }.AsQueryable();
        var plans = new[] { new tbl_indoor_planning_floor { id = 1, created_by_user_id = 1 }, new tbl_indoor_planning_floor { id = 2, created_by_user_id = 2 },
            new tbl_indoor_planning_floor { id = 3, created_by_user_id = null }, new tbl_indoor_planning_floor { id = 4, created_by_user_id = 3 } }.AsQueryable();
        Check(ResourceAccess.IndoorPlans(plans, users, User()).Select(row => row.id).SequenceEqual(new[] { 1, 4 }), "Indoor company access must exclude foreign and orphan plans.");
        Check(ResourceAccess.IndoorPlans(plans, users, User(company: 0)).Select(row => row.id).SequenceEqual(new[] { 1 }), "Companyless indoor access must remain creator-only.");
        Check(ResourceAccess.IndoorPlans(plans, users, User(role: 3)).Count() == 4, "Super-admin must retain orphan-plan access for assignment.");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySqlConnection"] = "Server=localhost;Database=IN_TEST;User ID=test;",
            ["ConnectionStrings:MySqlConnection2"] = "Server=localhost;Database=TW_TEST;User ID=test;"
        }).Build();
        foreach (var region in new[] { "IN", "TW" })
        {
            var context = new DefaultHttpContext { User = User(region: region) };
            context.Request.QueryString = new QueryString(region == "IN" ? "?region=TW" : "?region=IN");
            Check(!RegionAccess.IsRequestAllowed(context), "Cross-region request must be rejected.");
            var provider = new DbConnectionProvider(new HttpContextAccessor { HttpContext = context }, configuration);
            var selected = new MySqlConnector.MySqlConnectionStringBuilder(provider.GetConnectionString()).Database;
            Check(selected == region + "_TEST", "Database selection must ignore an ordinary user's forged region.");
            context.Request.Path = "/admin/saveuserdetails";
            Check(new MySqlConnector.MySqlConnectionStringBuilder(provider.GetConnectionString()).Database == region + "_TEST", "Legacy admin routing must not move ordinary users across regions.");
            context.Request.QueryString = new QueryString("?region=" + region);
            Check(RegionAccess.IsRequestAllowed(context), "Own-region request must work.");
            context.Request.Headers["x-country-code"] = region == "IN" ? "TW" : "IN";
            Check(!RegionAccess.IsRequestAllowed(context), "Conflicting region header must be rejected.");
        }
        Check(SessionSecurity.LockKey(1, "IN") != SessionSecurity.LockKey(1, "TW"), "Regional login locks must not collide.");
        var storedUser = new tbl_user { id = 1, company_id = 10, m_user_type_id = 1, isactive = 1, password = "stored-test-hash" };
        Check(SessionSecurity.IsCurrent(User(), storedUser), "Current credential state must validate.");
        storedUser.password = "changed-test-hash";
        Check(!SessionSecurity.IsCurrent(User(), storedUser), "Password reset must invalidate the old principal.");
        storedUser.password = "stored-test-hash"; storedUser.isactive = 0;
        Check(!SessionSecurity.IsCurrent(User(), storedUser), "Deactivation must invalidate the old principal.");
        storedUser.isactive = 1; storedUser.m_user_type_id = 2;
        Check(!SessionSecurity.IsCurrent(User(), storedUser), "Role changes must invalidate stale claims.");
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties();
        Check(!SessionSecurity.WithinAbsoluteLifetime(properties, TimeSpan.FromDays(1), now), "Legacy ticket without absolute start must be rejected.");
        properties.Items[SessionSecurity.StartedProperty] = now.AddHours(-1).ToString("O");
        Check(SessionSecurity.WithinAbsoluteLifetime(properties, TimeSpan.FromDays(1), now), "Unexpired ticket must validate.");
        properties.Items[SessionSecurity.StartedProperty] = now.AddDays(-2).ToString("O");
        Check(!SessionSecurity.WithinAbsoluteLifetime(properties, TimeSpan.FromDays(1), now), "Absolute expiry must survive sliding renewal.");

        var protection = new EphemeralDataProtectionProvider();
        var uid = Guid.NewGuid().ToString();
        var token = PasswordResetTokens.Issue(protection, uid);
        Check(PasswordResetTokens.TryRead(protection, token, out var recovered) && recovered == uid, "Valid reset token must round-trip.");
        var tampered = token[..20] + (token[20] == 'a' ? 'b' : 'a') + token[21..];
        Check(!PasswordResetTokens.TryRead(protection, tampered, out _), "Tampered reset token must fail.");
        var expired = protection.CreateProtector("SignalTracker.PasswordReset.v2").ToTimeLimitedDataProtector().Protect(uid, TimeSpan.FromSeconds(-1));
        Check(!PasswordResetTokens.TryRead(protection, expired, out _), "Expired reset token must fail.");
        Check(!PasswordResetTokens.TryRead(protection, uid + ".999999999999", out _), "Legacy unsigned timestamp token must fail.");

        // Check query translation without opening a database connection.
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql("Server=localhost;Database=TEST;User ID=test;", new MySqlServerVersion(new Version(8, 0, 29))).Options);
        Check(ResourceAccess.Projects(db.tbl_project, User()).ToQueryString().Contains("WHERE"), "Project scope must translate to SQL.");
        Check(ResourceAccess.IndoorPlans(db.tbl_indoor_planning_floor, db.tbl_user, User()).ToQueryString().Contains("WHERE"), "Indoor scope must translate to SQL.");
        var http = new DefaultHttpContext { User = User(role: 2) };
        var company = new CompanyController(db, new UserScopeService(new HttpContextAccessor { HttpContext = http }), null!, configuration, null!)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await company.CreateCompanyUser(new CompanyController.CreateCompanyUserRequest
        { name = "Regression", email = "test@example.invalid", password = "test-only-input", m_user_type_id = 3 });
        Check(result is ObjectResult { StatusCode: 403 }, "Company admin must not create a super-admin; reject before database access.");
        var urlValidator = typeof(SignalTrackers.Controllers.LogDownloadController).GetMethod("TryGetAllowedUri",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var invalidUrl in new[]
        {
            "http://apistracer.vinfocom.co.in/uploaded_zippedlogs/test.zip",
            "https://apistracer.vinfocom.co.in.attacker.invalid/uploaded_zippedlogs/test.zip",
            "https://apistracer.vinfocom.co.in:8443/uploaded_zippedlogs/test.zip",
            "https://user:pass@apistracer.vinfocom.co.in/uploaded_zippedlogs/test.zip",
            "https://apistracer.vinfocom.co.in/uploaded_zippedlogs/../private.zip"
        })
        {
            object?[] arguments = [invalidUrl, null, null];
            Check(!(bool)urlValidator.Invoke(null, arguments)!, "Disallowed log URL must fail validation without an outbound request.");
        }
        object?[] validArguments = ["https://apistracer.vinfocom.co.in/uploaded_zippedlogs/test.zip", null, null];
        Check((bool)urlValidator.Invoke(null, validArguments)!, "The approved log URL shape must remain valid (resource authorization is separate).");
        Console.WriteLine("Resource, region, session-state, reset-token, SQL-translation, and role-escalation regressions passed (no database connections).");
    }
}
