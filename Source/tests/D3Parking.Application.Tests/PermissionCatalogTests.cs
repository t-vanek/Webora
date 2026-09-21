using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Guards the shape of the authorization model itself: that dependencies close both ways, that the
/// built-in groups and the roles composed from them only name things the catalog still declares,
/// and that a role's effective permissions are exactly the union of its groups. A role holding
/// <c>Settings.Edit</c> without <c>Settings.View</c> is not a theoretical problem — it is a save
/// button on a page its holder cannot open.
/// </summary>
[TestFixture]
public class PermissionCatalogTests
{
    [Test]
    public void Expand_pulls_in_the_permission_a_grant_is_useless_without()
    {
        var expanded = Permissions.Expand([Permissions.Settings.Edit]);

        Assert.That(expanded, Does.Contain(Permissions.Settings.View));
        Assert.That(expanded, Does.Contain(Permissions.Settings.Edit));
    }

    [Test]
    public void Expand_drops_names_the_catalog_does_not_declare()
    {
        var expanded = Permissions.Expand([Permissions.Parking.View, "Pages.Publish", "Media.Upload"]);

        Assert.That(expanded, Is.EqualTo(new[] { Permissions.Parking.View }));
    }

    [Test]
    public void Expand_emits_catalog_order_so_stored_sets_and_diffs_read_consistently()
    {
        var expanded = Permissions.Expand([Permissions.Roles.Delete, Permissions.Parking.Reserve]);
        var catalogOrder = Permissions.All.Where(expanded.Contains).ToArray();

        Assert.That(expanded, Is.EqualTo(catalogOrder));
    }

    [Test]
    public void DependentsOf_is_the_inverse_of_the_requires_chain()
    {
        var dependents = Permissions.DependentsOf(Permissions.Settings.View);

        Assert.That(dependents, Does.Contain(Permissions.Settings.Edit));
        // A permission is not its own dependent, or unchecking it could never settle.
        Assert.That(dependents, Does.Not.Contain(Permissions.Settings.View));
    }

    [Test]
    public void Every_declared_dependency_points_at_a_permission_in_the_catalog()
    {
        foreach (var permission in Permissions.All)
        {
            var requires = Permissions.RequiredBy(permission);
            if (requires is not null)
            {
                Assert.That(Permissions.IsKnown(requires), Is.True,
                    $"{permission} requires {requires}, which is not in the catalog.");
            }
        }
    }

    [Test]
    public void No_permission_is_declared_twice()
    {
        Assert.That(Permissions.All, Is.Unique);
    }

    [Test]
    public void Every_permission_belongs_to_exactly_one_area()
    {
        var areas = Permissions.Areas.SelectMany(a => a.Permissions).ToArray();

        Assert.That(areas, Is.EquivalentTo(Permissions.All));
        Assert.That(areas, Is.Unique);
    }

    [Test]
    public void Every_built_in_group_names_permissions_the_catalog_still_declares()
    {
        foreach (var (group, permissions) in DefaultPermissionGroups.Map)
        {
            foreach (var permission in permissions)
            {
                Assert.That(Permissions.IsKnown(permission), Is.True,
                    $"Group {group} contains {permission}, which is not in the catalog.");
            }
        }
    }

    [Test]
    public void Every_built_in_group_has_a_definition_and_vice_versa()
    {
        Assert.That(DefaultPermissionGroups.Map.Keys, Is.EquivalentTo(PermissionGroupNames.Defaults));
    }

    [Test]
    public void Every_built_in_role_is_composed_only_of_built_in_groups()
    {
        foreach (var (role, groups) in DefaultRoleGroups.Map)
        {
            foreach (var group in groups)
            {
                Assert.That(DefaultPermissionGroups.Map.ContainsKey(group), Is.True,
                    $"Role {role} is composed of {group}, which is not a built-in group.");
            }
        }
    }

    [Test]
    public void Every_built_in_role_has_a_composition()
    {
        Assert.That(DefaultRoleGroups.Map.Keys, Is.EquivalentTo(Roles.Defaults));
    }

    [Test]
    public void A_roles_effective_permissions_are_the_union_of_its_groups()
    {
        var expected = Permissions.Expand(
        [
            .. DefaultPermissionGroups.Map[PermissionGroupNames.ParkingAccess],
            .. DefaultPermissionGroups.Map[PermissionGroupNames.ParkingBooking],
            .. DefaultPermissionGroups.Map[PermissionGroupNames.VisitorDesk],
        ]);

        Assert.That(DefaultRoleGroups.EffectivePermissions(Roles.FrontDesk), Is.EquivalentTo(expected));
    }

    [Test]
    public void Composition_preserves_what_each_built_in_role_used_to_grant()
    {
        // The permission sets the roles carried before groups existed. Composing them out of
        // bundles was meant to change how a role is expressed, not what it lets anyone do.
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Roles.LotManager] =
            [
                Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard,
                Permissions.Parking.ManageSpots, Permissions.Parking.ManageFleet,
                Permissions.Parking.ReviewMismatches, Permissions.Parking.ViewAnalytics,
            ],
            [Roles.FrontDesk] =
            [
                Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard,
                Permissions.Parking.ManageReservations, Permissions.Parking.ManageVisitors,
            ],
            [Roles.IncentiveCoordinator] =
            [
                Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard,
                Permissions.Parking.ManageIncentives, Permissions.Parking.ReviewCollusion,
                Permissions.Parking.ViewAnalytics,
            ],
            [Roles.Analyst] =
            [
                Permissions.Parking.View, Permissions.Parking.ViewLeaderboard, Permissions.Parking.ViewAnalytics,
            ],
            [Roles.UserManager] =
            [
                Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard,
                Permissions.Parking.VerifyResidency, Permissions.Users.View, Permissions.Users.Create,
                Permissions.Users.Edit, Permissions.Users.Delete, Permissions.Users.AssignRoles,
                Permissions.Audit.View,
            ],
            [Roles.Auditor] =
            [
                Permissions.Parking.View, Permissions.Parking.ViewLeaderboard, Permissions.Parking.ViewAnalytics,
                Permissions.Users.View, Permissions.Roles.View, Permissions.Audit.View, Permissions.Settings.View,
            ],
            [Roles.Employee] =
            [
                Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard,
            ],
        };

        foreach (var (role, permissions) in expected)
        {
            Assert.That(DefaultRoleGroups.EffectivePermissions(role), Is.EquivalentTo(permissions),
                $"Role {role} no longer grants what it did before it was composed from groups.");
        }
    }

    [Test]
    public void Administrator_holds_the_whole_catalog()
    {
        Assert.That(DefaultRoleGroups.EffectivePermissions(Roles.Administrator), Is.EquivalentTo(Permissions.All));
    }

    [Test]
    public void Retired_roles_are_no_longer_built_in_so_an_administrator_can_clear_them_out()
    {
        foreach (var retired in Roles.Retired)
        {
            Assert.That(Roles.IsDefault(retired), Is.False);
            Assert.That(DefaultRoleGroups.Map.ContainsKey(retired), Is.False);
        }
    }

    [Test]
    public void Only_the_administrator_role_can_reach_users_roles_and_settings_at_once()
    {
        // The point of splitting the roles: no non-administrator built-in role combines account
        // administration, role administration and site settings, which together are administrator
        // in all but name.
        foreach (var role in Roles.Defaults.Where(r => r != Roles.Administrator))
        {
            var permissions = DefaultRoleGroups.EffectivePermissions(role);
            var reach = new[] { Permissions.Users.View, Permissions.Roles.Edit, Permissions.Settings.Edit }
                .Count(permissions.Contains);

            Assert.That(reach, Is.LessThan(3), $"Role {role} is an administrator under another name.");
        }
    }

    [Test]
    public void Composing_roles_and_defining_groups_are_separate_permissions()
    {
        // The whole point of the split: someone who may assemble roles cannot widen what a bundle
        // grants, so role administration alone can never mint permission nobody had.
        var roleAdmin = DefaultPermissionGroups.Map[PermissionGroupNames.RoleAdministration];

        Assert.That(roleAdmin, Does.Contain(Permissions.Roles.Edit));
        Assert.That(roleAdmin, Does.Not.Contain(Permissions.PermissionGroups.Edit));
        Assert.That(roleAdmin, Does.Not.Contain(Permissions.PermissionGroups.Create));
    }
}
