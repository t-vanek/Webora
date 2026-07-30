using D3Parking.Domain.Authorization;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// The contents of the built-in permission groups, applied by the seeder.
/// </summary>
/// <remarks>
/// Deliberately overlapping: <see cref="PermissionGroupNames.AccountsRead"/> is a subset of
/// <see cref="PermissionGroupNames.AccountAdministration"/> so an auditor can be given the reading
/// half without the writing half. A role's effective permissions are the union of its groups, so
/// overlap costs nothing and keeps each group a single, nameable job.
/// </remarks>
public static class DefaultPermissionGroups
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Map { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [PermissionGroupNames.FullControl] = Permissions.All,

            [PermissionGroupNames.ParkingAccess] =
                [Permissions.Parking.View, Permissions.Parking.ViewLeaderboard],

            [PermissionGroupNames.ParkingBooking] = [Permissions.Parking.Reserve],

            [PermissionGroupNames.LotOperations] =
                [Permissions.Parking.ManageSpots, Permissions.Parking.ManageFleet],

            [PermissionGroupNames.MismatchReview] = [Permissions.Parking.ReviewMismatches],

            [PermissionGroupNames.LotAnalytics] = [Permissions.Parking.ViewAnalytics],

            [PermissionGroupNames.VisitorDesk] =
                [Permissions.Parking.ManageReservations, Permissions.Parking.ManageVisitors],

            [PermissionGroupNames.IncentiveScheme] =
                [Permissions.Parking.ManageIncentives, Permissions.Parking.ReviewCollusion],

            [PermissionGroupNames.ResidencyVerification] = [Permissions.Parking.VerifyResidency],

            [PermissionGroupNames.AccountsRead] = [Permissions.Users.View],

            [PermissionGroupNames.AccountAdministration] =
            [
                Permissions.Users.View, Permissions.Users.Create,
                Permissions.Users.Edit, Permissions.Users.Delete,
            ],

            [PermissionGroupNames.RoleAssignment] = [Permissions.Users.AssignRoles],

            [PermissionGroupNames.RolesRead] = [Permissions.Roles.View],

            [PermissionGroupNames.RoleAdministration] =
            [
                Permissions.Roles.View, Permissions.Roles.Create,
                Permissions.Roles.Edit, Permissions.Roles.Delete,
                Permissions.PermissionGroups.View,
            ],

            [PermissionGroupNames.PermissionGroupAdministration] =
            [
                Permissions.PermissionGroups.View, Permissions.PermissionGroups.Create,
                Permissions.PermissionGroups.Edit, Permissions.PermissionGroups.Delete,
            ],

            [PermissionGroupNames.AuditReading] = [Permissions.Audit.View],

            [PermissionGroupNames.SiteSettingsRead] = [Permissions.Settings.View],

            [PermissionGroupNames.SiteSettingsAdministration] =
                [Permissions.Settings.View, Permissions.Settings.Edit],
        };
}
