using Microsoft.AspNetCore.Identity;
using D3Parking.Domain.Accounts;

namespace D3Parking.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.CreateVersion7();

    public string? DisplayName { get; set; }

    /// <summary>Team/department the user belongs to; groups the team leaderboard and peer comparison.</summary>
    public string? Department { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.PendingActivation;

    public DateTimeOffset? StatusChangedAtUtc { get; set; }

    public string? StatusReason { get; set; }

    /// <summary>The user's home address (self-entered) used to estimate their commute distance.</summary>
    public string? HomeAddress { get; set; }

    public double? HomeLatitude { get; set; }

    public double? HomeLongitude { get; set; }

    /// <summary>Distance from home to the parking lot in km; scales the shared-spot reward.</summary>
    public double? CommuteDistanceKm { get; set; }

    /// <summary>Whether an admin has verified the home address; the distance reward needs this.</summary>
    public bool HomeVerified { get; set; }

    /// <summary>
    /// The user's vehicle license plate (self-entered). Lets the admin match a plate recorded in
    /// a blocked-spot report to a person; comparison ignores spacing and case.
    /// </summary>
    public string? LicensePlate { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() => Id = Guid.CreateVersion7();

    public ApplicationRole(string roleName) : this() => Name = roleName;
}
