using D3Parking.Domain.Authorization;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// Which permission groups each built-in role is composed of, applied by the seeder.
/// </summary>
/// <remarks>
/// Roles no longer name permissions: they name groups, and their effective permissions are the
/// union of those groups (closed over the catalog's dependencies). That is what makes the answer to
/// "why does the front desk hold ManageVisitors" a group name rather than an archaeology exercise.
/// <para>
/// The composition is re-asserted on every start, which is what lets a release move a permission
/// between groups and have it land on existing installations. It also means the built-in roles are
/// not editable by hand — the roles UI presents them read-only and offers duplication instead.
/// </para>
/// </remarks>
public static class DefaultRoleGroups
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Map { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Roles.Administrator] = [PermissionGroupNames.FullControl],

            // Owns the physical lot, the evidence that resolves a wrongly occupied spot, and the
            // analytics that explain the lot's pressure. No reach into accounts, roles or settings.
            [Roles.LotManager] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.ParkingBooking,
                PermissionGroupNames.LotOperations, PermissionGroupNames.MismatchReview,
                PermissionGroupNames.LotAnalytics,
            ],

            [Roles.FrontDesk] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.ParkingBooking,
                PermissionGroupNames.VisitorDesk,
            ],

            [Roles.IncentiveCoordinator] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.ParkingBooking,
                PermissionGroupNames.IncentiveScheme, PermissionGroupNames.LotAnalytics,
            ],

            // Numbers only — no booking of its own, and no account list.
            [Roles.Analyst] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.LotAnalytics,
            ],

            // Role assignment is safe to grant here: the service bounds every assignment to roles
            // no stronger than the actor's own, so this role hands out Employee, not Administrator.
            [Roles.UserManager] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.ParkingBooking,
                PermissionGroupNames.AccountAdministration, PermissionGroupNames.RoleAssignment,
                PermissionGroupNames.ResidencyVerification, PermissionGroupNames.AuditReading,
            ],

            // Reads everything administration does — the reading halves only, never the writing ones.
            [Roles.Auditor] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.AccountsRead,
                PermissionGroupNames.RolesRead, PermissionGroupNames.AuditReading,
                PermissionGroupNames.SiteSettingsRead, PermissionGroupNames.LotAnalytics,
            ],

            [Roles.Employee] =
            [
                PermissionGroupNames.ParkingAccess, PermissionGroupNames.ParkingBooking,
            ],
        };

    /// <summary>The effective permissions a built-in role ends up with, for tests and diagnostics.</summary>
    public static IReadOnlyList<string> EffectivePermissions(string role) =>
        Map.TryGetValue(role, out var groups)
            ? Permissions.Expand(groups.SelectMany(g => DefaultPermissionGroups.Map[g]))
            : [];
}
