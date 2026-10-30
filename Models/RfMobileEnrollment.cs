using System.ComponentModel.DataAnnotations;

namespace SignalTracker.Models;

public sealed class RfMobileEnrollmentRequest
{
    public Guid EnrollmentId { get; set; }
    [Required, RegularExpression("^(IN|TW)$")] public string Region { get; set; } = "";
    [Range(1, int.MaxValue)] public int UserId { get; set; }
    [Required, StringLength(40)] public string MobileNumber { get; set; } = "";
    [Required, StringLength(100)] public string CompanyCode { get; set; } = "";
    [Required, StringLength(100)] public string DeviceLabel { get; set; } = "";
}

public sealed class RfMobileRenewalRequest
{
    [Required, RegularExpression("^(IN|TW)$")] public string Region { get; set; } = "";
    public Guid RenewalId { get; set; }
    public Guid? PreviousRenewalId { get; set; }
}

public sealed class RfMobileEnrollmentDecision
{
    [Required, RegularExpression("^(approve|deny)$")] public string Decision { get; set; } = "";
}

public sealed record RfMobileEnrollment
{
    public string Id { get; init; } = "";
    public string Region { get; init; } = "";
    public int UserId { get; init; }
    public int CompanyId { get; init; }
    public string MobileNumber { get; init; } = "";
    public string CompanyCode { get; init; } = "";
    public string DeviceLabel { get; init; } = "";
    public string ProofHash { get; init; } = "";
    public string Status { get; init; } = "pending";
    public DateTime CreatedAtUtc { get; init; }
    public DateTime RequestExpiresAtUtc { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public DateTime? ApprovalExpiresAtUtc { get; init; }
    public string? LatestRenewalId { get; init; }
    public bool OwnerActive { get; init; }
}

public sealed record RfEnrollmentMutation(RfMobileEnrollment Enrollment, RfMobileCredential? Credential = null);
