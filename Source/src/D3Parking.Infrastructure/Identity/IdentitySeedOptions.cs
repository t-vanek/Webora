namespace D3Parking.Infrastructure.Identity;

public class IdentitySeedOptions
{
    public const string SectionName = "IdentitySeed";

    /// <summary>When set, a default administrator account is created with this email/username.</summary>
    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }

    public string AdminDisplayName { get; set; } = "Administrator";
}
