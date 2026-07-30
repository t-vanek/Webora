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

    /// <summary>
    /// The account's immutable object id in the external directory (Entra's <c>oid</c>), or null
    /// for a purely local account.
    /// </summary>
    /// <remarks>
    /// Kept on the user rather than only in <c>AspNetUserLogins</c> because provisioning has to find
    /// and update an account that has never signed in, and because the object id is the only stable
    /// handle — mail, UPN and display name all change when someone marries, moves team or is renamed.
    /// </remarks>
    public string? ExternalObjectId { get; set; }

    /// <summary>Which directory <see cref="ExternalObjectId"/> belongs to, or null when local.</summary>
    public string? ExternalProvider { get; set; }

    /// <summary>The directory tenant the account came from; guards against a cross-tenant token.</summary>
    public string? ExternalTenantId { get; set; }

    /// <summary>When the directory last confirmed this account, whether by sign-in or provisioning.</summary>
    public DateTimeOffset? ExternalSyncedAtUtc { get; set; }

    /// <summary>True when sign-in is delegated to an external directory rather than a local password.</summary>
    public bool IsFederated => ExternalProvider is not null;
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() => Id = Guid.CreateVersion7();

    public ApplicationRole(string roleName) : this() => Name = roleName;
}
