namespace D3Parking.Domain.Authorization;

/// <summary>
/// The built-in permission groups. Each one is a job's worth of permissions rather than a slice of
/// the catalog, so composing a role reads like a job description: reception is
/// <see cref="ParkingAccess"/> + <see cref="ParkingBooking"/> + <see cref="VisitorDesk"/>.
/// </summary>
public static class PermissionGroupNames
{
    /// <summary>Everything. The group behind the Administrator role.</summary>
    public const string FullControl = "FullControl";

    /// <summary>Seeing the lot and the leaderboard, without the right to book.</summary>
    public const string ParkingAccess = "ParkingAccess";

    /// <summary>Booking a spot for oneself.</summary>
    public const string ParkingBooking = "ParkingBooking";

    /// <summary>The physical lot: the spot map and the vehicle registry.</summary>
    public const string LotOperations = "LotOperations";

    /// <summary>Reported occupancy mismatches, including their photographic evidence.</summary>
    public const string MismatchReview = "MismatchReview";

    /// <summary>The lot dashboard: occupancy, trends, turned-away demand.</summary>
    public const string LotAnalytics = "LotAnalytics";

    /// <summary>Visitor parking and fixing other people's bookings.</summary>
    public const string VisitorDesk = "VisitorDesk";

    /// <summary>The incentive scheme and the flags it raises.</summary>
    public const string IncentiveScheme = "IncentiveScheme";

    /// <summary>Confirming a home address, which decides commute-based entitlements.</summary>
    public const string ResidencyVerification = "ResidencyVerification";

    /// <summary>Reading the account list.</summary>
    public const string AccountsRead = "AccountsRead";

    /// <summary>The account lifecycle: create, edit, offboard.</summary>
    public const string AccountAdministration = "AccountAdministration";

    /// <summary>Changing which roles an account holds.</summary>
    public const string RoleAssignment = "RoleAssignment";

    /// <summary>Reading the roles and what they grant.</summary>
    public const string RolesRead = "RolesRead";

    /// <summary>Composing roles out of groups.</summary>
    public const string RoleAdministration = "RoleAdministration";

    /// <summary>Defining what the groups themselves contain.</summary>
    public const string PermissionGroupAdministration = "PermissionGroupAdministration";

    /// <summary>Reading the audit trail.</summary>
    public const string AuditReading = "AuditReading";

    /// <summary>Reading the site settings.</summary>
    public const string SiteSettingsRead = "SiteSettingsRead";

    /// <summary>Changing the site settings.</summary>
    public const string SiteSettingsAdministration = "SiteSettingsAdministration";

    /// <summary>Groups created by the seeder. They cannot be renamed, edited or deleted.</summary>
    public static IReadOnlyList<string> Defaults { get; } =
    [
        FullControl, ParkingAccess, ParkingBooking, LotOperations, MismatchReview, LotAnalytics,
        VisitorDesk, IncentiveScheme, ResidencyVerification, AccountsRead, AccountAdministration,
        RoleAssignment, RolesRead, RoleAdministration, PermissionGroupAdministration, AuditReading,
        SiteSettingsRead, SiteSettingsAdministration,
    ];

    public static bool IsDefault(string name) => Defaults.Contains(name, StringComparer.Ordinal);
}
