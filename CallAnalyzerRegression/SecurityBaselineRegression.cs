using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalTracker.Controllers;
using SignalTracker.Security;

internal static class SecurityBaselineRegression
{
    public static async Task RunAsync(string outputPath)
    {
        // Discover MVC metadata without running SignalTracker.Program, hosted services,
        // startup schema changes, or any controller/database operations.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MapViewController).Assembly.GetName().Name,
            EnvironmentName = "Production",
            Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(MapViewController).Assembly);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization(SecurityPolicies.Configure);
        await using var app = builder.Build();
        var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .OrderBy(action => action.AttributeRouteInfo?.Template)
            .ThenBy(action => action.ActionName).ToArray();
        WriteInventory(actions, outputPath);
        await VerifyAuthorizationAsync(app.Services, actions);
        await VerifyCookieTransportAsync();
        await SecurityAccessRegression.RunAsync();
        Console.WriteLine($"Security baseline passed: {actions.Length} MVC route descriptors in {outputPath}.");
        Console.WriteLine("Verified indoor authentication, global-admin authorization, and cookie/proxy transport boundaries.");
        Console.WriteLine("Ownership, deployed middleware behavior, external infrastructure, and live exploitability are not verified by these checks.");
    }

    private static void WriteInventory(ControllerActionDescriptor[] actions, string outputPath)
    {
        var rows = new List<string>
        {
            "Controller,Action,Route,Methods,AuthorizationMetadata,Roles,Policies,Filters,RateLimitPolicy,RequestLimitMetadata,OwnershipReview"
        };
        foreach (var action in actions)
        {
            var authorization = action.EndpointMetadata.OfType<IAuthorizeData>().ToArray();
            var anonymous = action.EndpointMetadata.OfType<IAllowAnonymous>().Any();
            var filters = action.FilterDescriptors.Select(filter => filter.Filter is TypeFilterAttribute typeFilter
                ? typeFilter.ImplementationType.Name : filter.Filter.GetType().Name).Distinct();
            var methods = action.ActionConstraints?.OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods).Distinct() ?? [];
            var rate = action.EndpointMetadata.OfType<EnableRateLimitingAttribute>().LastOrDefault()?.PolicyName ?? "None declared";
            var limits = action.EndpointMetadata.Where(item => item is RequestSizeLimitAttribute
                or DisableRequestSizeLimitAttribute or RequestFormLimitsAttribute).Select(item => item.GetType().Name);
            rows.Add(string.Join(",", new[]
            {
                action.ControllerName, action.ActionName, action.AttributeRouteInfo?.Template ?? "(conventional)",
                string.Join("|", methods.DefaultIfEmpty("ANY")),
                anonymous ? "AllowAnonymous (inspect custom filters)" : authorization.Length > 0
                    ? "Authorize" : "No Authorize metadata (inspect filters/action)",
                string.Join("|", authorization.Select(item => item.Roles).Where(value => !string.IsNullOrEmpty(value))),
                string.Join("|", authorization.Select(item => item.Policy).Where(value => !string.IsNullOrEmpty(value))),
                string.Join("|", filters), rate, string.Join("|", limits), "Not verified"
            }.Select(Csv)));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllLines(outputPath, rows, new UTF8Encoding(false));
        Console.WriteLine($"Inventory: {actions.Select(action => action.ControllerName).Distinct().Count()} controllers; " +
            $"{actions.Count(action => !action.EndpointMetadata.OfType<IAuthorizeData>().Any() && !action.EndpointMetadata.OfType<IAllowAnonymous>().Any())} descriptors without standard auth metadata (custom checks require review).");
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static async Task VerifyAuthorizationAsync(IServiceProvider services, ControllerActionDescriptor[] actions)
    {
        // Expected permissions are independent of the discovered metadata: removing
        // an attribute must fail the regression rather than remove a test case.
        var expected = new Dictionary<string, string>();
        foreach (var name in new[] { "GetProjects", "GetProject", "CreateProject", "SaveFloor" }) expected["IndoorPlanning." + name] = "Authenticated";
        foreach (var name in new[] { "DownloadFromUrl", "CheckDownload" }) expected["LogDownload." + name] = "Authenticated";
        foreach (var name in new[] { "GetRedisKeys", "GetKeyInfo", "ExtendTtl", "DeleteKey", "FlushRedis", "UserResetPassword",
            "SaveUserDetails", "DeleteUser", "ActivateUser", "InactivateUser", "DeleteUserPermanent" }) expected["Admin." + name] = SecurityPolicies.SuperAdmin;
        foreach (var name in new[] { "CreateCompanyUser", "UpdateUser", "RevokeUser", "DeleteUser" }) expected["Company." + name] = SecurityPolicies.CompanyAdmin;
        foreach (var name in new[] { "GrantLicense", "RevokeLicense", "UpdateIssuedLicense" }) expected["Company." + name] = SecurityPolicies.SuperAdmin;
        var targets = actions.Where(action => expected.ContainsKey(action.ControllerName + "." + action.ActionName)).ToArray();
        Require(targets.Length == expected.Count, "Every protected contract must remain discoverable.");
        foreach (var action in targets)
        {
            foreach (var userType in new[] { 0, 1, 2, 3 })
            {
                using var scope = services.CreateScope();
                var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                context.Response.Body = new MemoryStream();
                if (userType != 0)
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("UserId", "42"), new Claim("CompanyId", "10"), new Claim("UserTypeId", userType.ToString())], "regression"));
                context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                    new EndpointMetadataCollection(action.EndpointMetadata), action.DisplayName));
                var invoked = false;
                var middleware = new AuthorizationMiddleware(_ =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                }, scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>());
                await middleware.Invoke(context);
                var required = expected[action.ControllerName + "." + action.ActionName];
                var requiresSuperAdmin = required == SecurityPolicies.SuperAdmin;
                var requiresCompanyAdmin = required == SecurityPolicies.CompanyAdmin;
                var allowed = userType != 0 && (!requiresSuperAdmin || userType == 3) && (!requiresCompanyAdmin || userType is 2 or 3);
                Require(invoked == allowed, $"Unexpected authorization for {action.ActionName}, user type={userType}.");
                if (!allowed)
                    Require(context.Response.StatusCode == (userType == 0 ? 401 : 403),
                        $"Unexpected denial status for {action.ActionName}, user type={userType}.");
            }
        }
        Console.WriteLine($"Authorization matrix: {targets.Length * 4} route/identity cases passed.");
    }

    private static async Task VerifyCookieTransportAsync()
    {
        var remoteHttp = Context("http", "app.example", "203.0.113.10");
        var productionCookie = new CookieOptions { Secure = true, SameSite = SameSiteMode.None };
        RequestSecurity.ApplyPerRequestCookieSettings(remoteHttp, productionCookie);
        Require(productionCookie.Secure, "HTTP must not downgrade a production Secure cookie.");

        var localHttp = Context("http", "localhost", "127.0.0.1");
        var developmentCookie = new CookieOptions();
        RequestSecurity.ApplyPerRequestCookieSettings(localHttp, developmentCookie);
        Require(!developmentCookie.Secure && developmentCookie.SameSite == SameSiteMode.Lax,
            "Local HTTP development cookie behavior must be preserved.");
        var secureLocalCookie = new CookieOptions { Secure = true };
        RequestSecurity.ApplyPerRequestCookieSettings(localHttp, secureLocalCookie);
        Require(secureLocalCookie.Secure, "Loopback must not downgrade an explicitly Secure cookie.");

        var https = Context("https", "app.example", "203.0.113.10");
        var httpsCookie = new CookieOptions();
        RequestSecurity.ApplyPerRequestCookieSettings(https, httpsCookie);
        Require(httpsCookie.Secure && httpsCookie.SameSite == SameSiteMode.None, "HTTPS cross-origin cookie behavior must be preserved.");
        foreach (var trusted in new[] { false, true })
        {
            var context = Context("http", "app.example", trusted ? "127.0.0.1" : "203.0.113.10");
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            Require(!RequestSecurity.RequestUsesHttps(context), "Raw forwarded headers must not establish HTTPS trust.");
            var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
                NullLoggerFactory.Instance, Options.Create(options));
            await middleware.Invoke(context);
            Require(RequestSecurity.RequestUsesHttps(context) == trusted, "Only trusted proxy forwarding may establish HTTPS.");
        }
    }

    private static DefaultHttpContext Context(string scheme, string host, string ip)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
