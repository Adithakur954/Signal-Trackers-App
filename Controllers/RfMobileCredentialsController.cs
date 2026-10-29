using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

// Provisioning is an administrator operation, never a public mobile-number login.
[ApiController, AllowAnonymous, Route("api/rf-logs/mobile-credentials"), RfLogApiKey, RfLogRecipientAdmin]
[ServiceFilter(typeof(RfLogErrorFilter)), RequestSizeLimit(16384)]
public sealed class RfMobileCredentialsController(RfMobileCredentialService service, IRfMobileCredentialStore store) : ControllerBase
{
    private RfLogActor Admin => (RfLogActor)HttpContext.Items[RfLogApiKeyAttribute.ClientItem]!;

    [HttpPost]
    public async Task<IActionResult> Issue(RfMobileCredentialRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var issued = await service.IssueAsync(Admin, request, ct);
        // The raw credential is returned exactly once and never persisted/logged.
        return Ok(new { credential = RfMobileCredentialService.Metadata(issued.Credential),
            tokenType = "Bearer", uploadToken = issued.Token });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new
    {
        region = Admin.Region, companyId = Admin.CompanyId,
        credentials = (await store.ListAsync(Admin.Region, Admin.CompanyId, ct)).Select(RfMobileCredentialService.Metadata)
    });

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await store.RevokeAsync(Admin.Region, Admin.CompanyId, id.ToString("N"), ct)
            ? NoContent() : NotFound(new { message = "RF mobile credential not found." });
}
