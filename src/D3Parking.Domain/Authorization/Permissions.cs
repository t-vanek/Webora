namespace D3Parking.Domain.Authorization;

/// <summary>
/// The catalog of fine-grained permissions. Each constant is the canonical permission name
/// stored as a role claim and checked by the authorization layer.
/// </summary>
/// <remarks>
/// The catalog is declared explicitly (rather than discovered by reflection) because the editor
/// needs two things reflection cannot give: a stable display order, and the dependency edges that
/// say which permission is useless without another — <c>Settings.Edit</c> without
/// <c>Settings.View</c> grants a save button on a page its holder cannot open.
/// </remarks>
public static class Permissions
{
    public static class Users
    {
        public const string View = "Users.View";
        public const string Create = "Users.Create";
        public const string Edit = "Users.Edit";
        public const string Delete = "Users.Delete";

        /// <summary>
        /// Change which roles an account holds. Deliberately separate from <see cref="Edit"/>:
        /// role assignment is the one user-administration action that can hand out privilege,
        /// so it is granted on its own and additionally bounded by the actor's own permissions.
        /// </summary>
        public const string AssignRoles = "Users.AssignRoles";
    }

    public static class Roles
    {
        public const string View = "Roles.View";
        public const string Create = "Roles.Create";
        public const string Edit = "Roles.Edit";
        public const string Delete = "Roles.Delete";
    }

    /// <summary>
    /// Administering the bundles roles are built from. Held apart from <see cref="Roles"/> on
    /// purpose: composing a role can only combine groups that already exist, so someone who may
    /// assemble roles still cannot widen what any group grants.
    /// </summary>
    public static class PermissionGroups
    {
        public const string View = "PermissionGroups.View";
        public const string Create = "PermissionGroups.Create";
        public const string Edit = "PermissionGroups.Edit";
        public const string Delete = "PermissionGroups.Delete";
    }

    public static class Settings
    {
        public const string View = "Settings.View";
        public const string Edit = "Settings.Edit";
    }

    public static class Audit
    {
        /// <summary>Read the account audit trail across all users.</summary>
        public const string View = "Audit.View";
    }

    public static class Parking
    {
        /// <summary>See the lot and spot availability. The baseline every parker needs.</summary>
        public const string View = "Parking.View";

        /// <summary>Book and manage one's own reservations.</summary>
        public const string Reserve = "Parking.Reserve";

        /// <summary>
        /// See one's earned achievements. The persisted value keeps its historical name so
        /// existing role assignments continue to work after removing the leaderboard.
        /// </summary>
        public const string ViewLeaderboard = "Parking.ViewLeaderboard";

        public const string ViewAchievements = ViewLeaderboard;

        /// <summary>Create, edit and retire parking spots, and assign their owners.</summary>
        public const string ManageSpots = "Parking.ManageSpots";

        /// <summary>Maintain the company vehicle registry and its driver pairings.</summary>
        public const string ManageFleet = "Parking.ManageFleet";

        /// <summary>Administer any user's reservations (override, cancel, resolve no-shows).</summary>
        public const string ManageReservations = "Parking.ManageReservations";

        /// <summary>Book and approve visitor parking.</summary>
        public const string ManageVisitors = "Parking.ManageVisitors";

        /// <summary>
        /// Review reported occupancy mismatches. Split from <see cref="ManageSpots"/> because the
        /// evidence includes photographs of third parties' cars and plates — personal data that
        /// maintaining the spot map does not warrant seeing.
        /// </summary>
        public const string ReviewMismatches = "Parking.ReviewMismatches";

        /// <summary>Read the lot analytics dashboard: occupancy, turned-away demand, trends.</summary>
        public const string ViewAnalytics = "Parking.ViewAnalytics";

        /// <summary>Tune parking planning, pricing and operational rules.</summary>
        public const string ManageIncentives = "Parking.ManageIncentives";

        /// <summary>Review collusion flags raised against named users.</summary>
        public const string ReviewCollusion = "Parking.ReviewCollusion";

        /// <summary>
        /// Hand an oversight case to another reviewer. Anyone who can see a case may take it; this
        /// is the one that lets someone put work on a colleague's desk, which is a decision about
        /// other people's time rather than about the case.
        /// </summary>
        public const string AssignOversight = "Parking.AssignOversight";

        /// <summary>
        /// Act against a person from an oversight case: a formal warning, or a deduction from their
        /// reputation and wallet. Held apart from the review permissions on purpose — judging
        /// whether a photograph shows a blocked spot and deciding what it should cost somebody are
        /// different jobs, and the second one is the one that needs a second pair of hands.
        /// </summary>
        public const string SanctionOversight = "Parking.SanctionOversight";

        /// <summary>Verify a user's home address, which decides their commute-based entitlements.</summary>
        public const string VerifyResidency = "Parking.VerifyResidency";
    }

    /// <summary>
    /// Area keys, used for display order and as the stem of the localization key. An area is the
    /// catalog's own taxonomy — a fixed shelf the permission sits on. It is not a
    /// <see cref="PermissionGroup"/>, which is an administrator-authored bundle that roles are
    /// built from and may draw on any number of areas.
    /// </summary>
    public static class AreaNames
    {
        public const string Parking = "Parking";
        public const string Users = "Users";
        public const string Roles = "Roles";
        public const string PermissionGroups = "PermissionGroups";
        public const string Audit = "Audit";
        public const string Settings = "Settings";
    }

    // Declaration order is display order: the parking floor first, administration behind it.
    private static readonly PermissionDefinition[] Catalog =
    [
        new(Parking.View, AreaNames.Parking),
        new(Parking.Reserve, AreaNames.Parking, Parking.View),
        new(Parking.ViewAchievements, AreaNames.Parking),
        new(Parking.ManageSpots, AreaNames.Parking, Parking.View),
        new(Parking.ManageFleet, AreaNames.Parking, Parking.View),
        new(Parking.ManageReservations, AreaNames.Parking, Parking.View),
        new(Parking.ManageVisitors, AreaNames.Parking, Parking.View),
        new(Parking.ReviewMismatches, AreaNames.Parking, Parking.View),
        new(Parking.ViewAnalytics, AreaNames.Parking, Parking.View),
        new(Parking.ManageIncentives, AreaNames.Parking, Parking.View),
        new(Parking.ReviewCollusion, AreaNames.Parking, Parking.View),
        new(Parking.AssignOversight, AreaNames.Parking, Parking.View),
        new(Parking.SanctionOversight, AreaNames.Parking, Parking.View),
        new(Parking.VerifyResidency, AreaNames.Parking, Users.View),

        new(Users.View, AreaNames.Users),
        new(Users.Create, AreaNames.Users, Users.View),
        new(Users.Edit, AreaNames.Users, Users.View),
        new(Users.Delete, AreaNames.Users, Users.View),
        new(Users.AssignRoles, AreaNames.Users, Users.View),

        new(Roles.View, AreaNames.Roles),
        new(Roles.Create, AreaNames.Roles, Roles.View),
        new(Roles.Edit, AreaNames.Roles, Roles.View),
        new(Roles.Delete, AreaNames.Roles, Roles.View),

        // Composing a role means picking groups, so seeing the groups is the floor for role work.
        new(PermissionGroups.View, AreaNames.PermissionGroups, Roles.View),
        new(PermissionGroups.Create, AreaNames.PermissionGroups, PermissionGroups.View),
        new(PermissionGroups.Edit, AreaNames.PermissionGroups, PermissionGroups.View),
        new(PermissionGroups.Delete, AreaNames.PermissionGroups, PermissionGroups.View),

        new(Audit.View, AreaNames.Audit, Users.View),

        new(Settings.View, AreaNames.Settings),
        new(Settings.Edit, AreaNames.Settings, Settings.View),
    ];

    private static readonly IReadOnlyList<PermissionArea> GroupedPermissions = Catalog
        .GroupBy(p => p.Area)
        .Select(g => new PermissionArea(g.Key, g.Select(p => p.Name).ToArray()))
        .ToArray();

    private static readonly IReadOnlyList<string> AllPermissions =
        Catalog.Select(p => p.Name).ToArray();

    private static readonly IReadOnlyDictionary<string, PermissionDefinition> ByName =
        Catalog.ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>The permission catalog by area, in display order.</summary>
    public static IReadOnlyList<PermissionArea> Areas => GroupedPermissions;

    /// <summary>Every permission declared in the catalog.</summary>
    public static IReadOnlyList<string> All => AllPermissions;

    /// <summary>True when <paramref name="permission"/> is a known permission from the catalog.</summary>
    public static bool IsKnown(string permission) => ByName.ContainsKey(permission);

    /// <summary>
    /// The permission <paramref name="permission"/> is useless without, or null when it stands
    /// alone. Only one level is declared; use <see cref="Expand"/> to walk the whole chain.
    /// </summary>
    public static string? RequiredBy(string permission) =>
        ByName.TryGetValue(permission, out var definition) ? definition.Requires : null;

    /// <summary>
    /// Every permission that (directly or through a chain) requires <paramref name="permission"/>.
    /// Taking a permission away has to take these with it, or the set is left holding a grant that
    /// cannot be reached.
    /// </summary>
    public static IReadOnlyList<string> DependentsOf(string permission) =>
        AllPermissions.Where(candidate => Chain(candidate).Skip(1).Contains(permission, StringComparer.Ordinal)).ToArray();

    private static IEnumerable<string> Chain(string permission)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = permission; current is not null && seen.Add(current); current = RequiredBy(current))
        {
            yield return current;
        }
    }

    /// <summary>
    /// Closes <paramref name="permissions"/> over their dependencies, so a set that grants
    /// <c>Settings.Edit</c> also grants the <c>Settings.View</c> needed to reach the page.
    /// Unknown names are dropped — the catalog is the only authority on what exists.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> permissions)
    {
        var closure = permissions.SelectMany(Chain).ToHashSet(StringComparer.Ordinal);

        // Emit in catalog order so stored claim sets and diffs read consistently.
        return AllPermissions.Where(closure.Contains).ToArray();
    }
}

/// <summary>A permission and where it sits in the catalog.</summary>
/// <param name="Name">The canonical permission name stored as a role claim.</param>
/// <param name="Area">The area it belongs to, from <see cref="Permissions.AreaNames"/>.</param>
/// <param name="Requires">The permission it is useless without, if any.</param>
public sealed record PermissionDefinition(string Name, string Area, string? Requires = null);

/// <summary>An area of the catalog — all the permissions on one shelf (e.g. every "Users.*").</summary>
public sealed record PermissionArea(string Name, IReadOnlyList<string> Permissions);
