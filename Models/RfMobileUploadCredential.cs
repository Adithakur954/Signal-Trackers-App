using System.ComponentModel.DataAnnotations;

namespace SignalTracker.Models;

public sealed class RfMobileCredentialRequest
{
    [Required, StringLength(40)] public string MobileNumber { get; set; } = "";
    [Required, StringLength(100)] public string CompanyCode { get; set; } = "";
    [Required, StringLength(100)] public string DeviceLabel { get; set; } = "";
    [Range(1, int.MaxValue)] public int? UserId { get; set; }
    [Range(1, 365)] public int ValidDays { get; set; } = 90;
}

public sealed record RfMobileCredential
{
    public string Id { get; init; } = "";
    public string Region { get; init; } = "";
    public int CompanyId { get; init; }
    public int UserId { get; init; }
    public string MobileNumber { get; init; } = "";
    public string CompanyCode { get; init; } = "";
    public string DeviceLabel { get; init; } = "";
    public string TokenHash { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
}

public sealed record RfMobileOwner(int UserId, int CompanyId);
