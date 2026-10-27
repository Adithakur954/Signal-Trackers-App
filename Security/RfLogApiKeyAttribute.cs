using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SignalTracker.Models;

namespace SignalTracker.Security;

// Uploads accept verified login claims or a scoped machine key. Reporting always
// requires its own service credential and never accepts browser cookies alone.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RfLogApiKeyAttribute(bool reporting = false) : Attribute, IAuthorizationFilter
{
    public const string ClientItem = "RfLogClientId";
    public const string RegionItem = "RfLogRegion";
    public static bool Matches(string? incoming, string expected) => expected.Length >= 32 && incoming != null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(incoming)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<RfLogIntegrationOptions>>().Value;
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!options.IsConfigured)
        {
            context.Result = new ObjectResult(new { message = "RF log storage integration is not configured." }) { StatusCode = 503 };
            return;
        }
        var key = context.HttpContext.Request.Headers["X-RF-Api-Key"].ToString();
        var clientId = context.HttpContext.Request.Headers["X-RF-Client-Id"].ToString();
        if (!reporting && context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            var user = context.HttpContext.User;
            var region = RegionAccess.Normalize(user.FindFirst("country_code")?.Value);
            if (int.TryParse(user.FindFirst("UserId")?.Value, out var userId) && userId > 0
                && int.TryParse(user.FindFirst("company_id")?.Value ?? user.FindFirst("CompanyId")?.Value, out var companyId)
                && companyId > 0 && region != null)
            {
                context.HttpContext.Items[ClientItem] = new RfLogActor($"user:{region}:{userId}", companyId, userId, region);
                return;
            }
            context.Result = new ForbidResult();
            return;
        }
        RfLogClient? client = null;
        var valid = reporting ? Matches(key, options.ReportingApiKey)
            : options.Clients.TryGetValue(clientId, out client) && client.CompanyId > 0
                && RegionAccess.Normalize(client.Region) != null && Matches(key, client.ApiKey);
        if (!valid)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid RF integration credentials." });
            return;
        }
        if (reporting)
        {
            var region = RegionAccess.Normalize(context.HttpContext.Request.Headers["X-RF-Region"].ToString());
            if (region == null)
            {
                context.Result = new BadRequestObjectResult(new { message = "X-RF-Region header must be IN or TW." });
                return;
            }
            context.HttpContext.Items[RegionItem] = region;
        }
        if (!reporting) context.HttpContext.Items[ClientItem] = new RfLogActor(clientId, client!.CompanyId, client.UserId, RegionAccess.Normalize(client.Region)!);
    }
}
