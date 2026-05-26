using Microsoft.AspNetCore.Identity;
using Webora.Domain.Accounts;

namespace Webora.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.CreateVersion7();

    public string? DisplayName { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.PendingActivation;

    public DateTimeOffset? StatusChangedAtUtc { get; set; }

    public string? StatusReason { get; set; }

    /// <summary>The user's home address (self-entered) used to estimate their commute distance.</summary>
    public string? HomeAddress { get; set; }

    public double? HomeLatitude { get; set; }

    public double? HomeLongitude { get; set; }

    /// <summary>Distance from home to the parking lot in km; scales the shared-spot reward.</summary>
    public double? CommuteDistanceKm { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() => Id = Guid.CreateVersion7();

    public ApplicationRole(string roleName) : this() => Name = roleName;
}
