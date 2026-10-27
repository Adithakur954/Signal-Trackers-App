using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RfLogRecipientAdminAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true
            || !int.TryParse(user.FindFirst("UserTypeId")?.Value ?? user.FindFirst("m_user_type_id")?.Value, out var role)
            || role is not (UserScopeService.ROLE_ADMIN or UserScopeService.ROLE_SUPER_ADMIN))
            context.Result = new ObjectResult(new { message = "A logged-in company administrator is required to manage report email recipients." }) { StatusCode = 403 };
    }
}

[ApiController, AllowAnonymous, Route("api/rf-logs"), RfLogApiKey, RfLogRecipientAdmin]
[ServiceFilter(typeof(RfLogErrorFilter)), RequestSizeLimit(16384)]
public sealed class RfLogRecipientsController(IRfLogEmailStore store) : ControllerBase
{
    private RfLogActor Actor => (RfLogActor)HttpContext.Items[RfLogApiKeyAttribute.ClientItem]!;

    [HttpGet("recipients")]
    public async Task<IActionResult> Recipients(CancellationToken ct) =>
        Ok(new { region = Actor.Region, companyId = Actor.CompanyId, recipients = await store.RecipientsAsync(Actor.Region, Actor.CompanyId, ct) });

    [HttpPost("recipients")]
    public async Task<IActionResult> Register(RfLogRecipientRequest request, CancellationToken ct) =>
        Ok(new { region = Actor.Region, companyId = Actor.CompanyId, recipient = await store.RegisterAsync(Actor.Region, Actor.CompanyId, request, ct) });

    [HttpDelete("recipients/{id:guid}")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) =>
        await store.DisableAsync(Actor.Region, Actor.CompanyId, id.ToString("D"), ct) ? NoContent() : NotFound(new { message = "Report recipient not found." });

    [HttpGet("email-deliveries")]
    public async Task<IActionResult> Deliveries([FromQuery] Guid? uploadId, CancellationToken ct) =>
        Ok(new { region = Actor.Region, companyId = Actor.CompanyId, deliveries = await store.DeliveriesAsync(Actor.Region, Actor.CompanyId, uploadId?.ToString("D"), ct) });

    [HttpPost("email-deliveries/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
        await store.RetryAsync(Actor.Region, Actor.CompanyId, id.ToString("D"), ct)
            ? Ok(new { message = "Report email queued for retry." })
            : Conflict(new { message = "Only a failed email delivery for an active recipient in your company can be retried." });
}
