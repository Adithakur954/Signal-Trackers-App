using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SignalTracker.Models;
using SignalTracker.Security;
using SignalTracker.Services;

namespace SignalTracker.Controllers;

[ApiController, AllowAnonymous, Route("api/rf-logs/mobile-enrollments")]
[ServiceFilter(typeof(RfLogErrorFilter)), RequestSizeLimit(16384), EnableRateLimiting("PublicApi")]
public sealed class RfMobileEnrollmentsController(RfMobileEnrollmentService service) : ControllerBase
{
    private string Proof()
    {
        Response.Headers.CacheControl = "no-store";
        var values = Request.Headers["X-RF-Enrollment-Secret"];
        if (values.Count != 1 || !RfMobileEnrollmentService.ValidProof(values[0] ?? ""))
            throw new RfLogRequestException(400, "X-RF-Enrollment-Secret must contain one 64-character lowercase hex installation secret.");
        return values[0]!;
    }

    [HttpPost]
    public async Task<IActionResult> RequestEnrollment(RfMobileEnrollmentRequest request, CancellationToken ct) =>
        StatusCode(202, service.Metadata(await service.RequestAsync(request, Proof(), ct)));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Status(Guid id, [FromQuery] string region, CancellationToken ct) =>
        Ok(service.Metadata(await service.StatusAsync(region, id, Proof(), ct)));

    [HttpPost("{id:guid}/token")]
    public async Task<IActionResult> Token(Guid id, RfMobileRenewalRequest request, CancellationToken ct) =>
        Ok(await service.TokenAsync(id, request, Proof(), ct));
}

[ApiController, AllowAnonymous, Route("api/rf-logs/mobile-enrollment-requests"), RfLogApiKey, RfLogRecipientAdmin]
[ServiceFilter(typeof(RfLogErrorFilter)), RequestSizeLimit(16384)]
public sealed class RfMobileEnrollmentAdminController(RfMobileEnrollmentService service, IRfMobileEnrollmentStore store) : ControllerBase
{
    private RfLogActor Admin => (RfLogActor)HttpContext.Items[RfLogApiKeyAttribute.ClientItem]!;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new
    {
        region = Admin.Region, companyId = Admin.CompanyId,
        requests = (await store.ListAsync(Admin.Region, Admin.CompanyId, ct)).Select(service.Metadata)
    });

    [HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decide(Guid id, RfMobileEnrollmentDecision request, CancellationToken ct) =>
        Ok(service.Metadata(await service.DecideAsync(Admin, id, request.Decision, ct)));
}
