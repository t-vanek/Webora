namespace D3Parking.Domain.Authorization;

/// <summary>
/// The built-in roles. Each one models a real job around the lot rather than a generic tier, so
/// that granting it answers "what is this person here to do" instead of "how much power do they
/// get". Anything that does not fit is a custom role, most easily made by duplicating the closest
/// built-in one.
/// </summary>
public static class Roles
{
    /// <summary>Full control, including users, roles and site settings.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Owns the physical lot: the spot map, ownership, the fleet, reported mismatches.</summary>
    public const string LotManager = "LotManager";

    /// <summary>Reception: visitor parking and day-to-day reservation overrides.</summary>
    public const string FrontDesk = "FrontDesk";

    /// <summary>Owns the incentive scheme: point rules, tiers and collusion review.</summary>
    public const string IncentiveCoordinator = "IncentiveCoordinator";

    /// <summary>Reads the lot analytics and nothing else — no personal data, no writes.</summary>
    public const string Analyst = "Analyst";

    /// <summary>HR-side account administration: creates, edits and offboards people.</summary>
    public const string UserManager = "UserManager";

    /// <summary>Read-only oversight across administration, including the audit trail.</summary>
    public const string Auditor = "Auditor";

    /// <summary>The baseline every parker gets: see the lot, book, appear on the leaderboard.</summary>
    public const string Employee = "Employee";

    /// <summary>The built-in roles created by the seeder. They cannot be renamed or deleted.</summary>
    public static IReadOnlyList<string> Defaults { get; } =
        [Administrator, LotManager, FrontDesk, IncentiveCoordinator, Analyst, UserManager, Auditor, Employee];

    /// <summary>
    /// Roles seeded by earlier versions that no longer carry meaning. They are left in place —
    /// deleting a role would silently strip whoever still holds it — but they are no longer
    /// built-in, so an administrator can retire them once nobody is in them.
    /// </summary>
    public static IReadOnlyList<string> Retired { get; } = ["Editor", "Viewer"];

    /// <summary>The role handed to self-registered accounts when the site has not chosen one.</summary>
    public const string RegistrationDefault = Employee;

    public static bool IsDefault(string roleName) =>
        Defaults.Contains(roleName, StringComparer.Ordinal);
}
