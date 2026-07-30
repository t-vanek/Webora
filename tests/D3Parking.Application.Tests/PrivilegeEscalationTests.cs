using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Exercises the boundary that keeps authorization administration from being a second way to become
/// an administrator: nobody may put a permission into a group they do not hold, compose a role from
/// a group that reaches past them, or assign a role stronger than their own. It also covers the
/// derived side — a role's claims must equal the union of its groups, and editing a group has to
/// reach everyone holding it. The last-administrator guard needs an installation with a known set
/// of administrators, so it lives in <see cref="LastAdministratorTests"/> with its own database.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class PrivilegeEscalationTests
{
    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    private Guid _parkingBasicsGroup;
    private Guid _accountAdminGroup;
    private Guid _roleAdminGroup;
    private Guid _fullControlGroup;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the escalation tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_PrivilegeEscalationTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(builder.ConnectionString));
        services.AddDataProtection();
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<D3ParkingDbContext>()
            .AddDefaultTokenProviders();
        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var dbSeed = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();

        _fullControlGroup = await AddGroupAsync(dbSeed, "FullControl", Permissions.All, builtIn: true);
        _parkingBasicsGroup = await AddGroupAsync(dbSeed, "ParkingBasics",
            [Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard]);
        _accountAdminGroup = await AddGroupAsync(dbSeed, "AccountAdministration",
            [Permissions.Users.View, Permissions.Users.Create, Permissions.Users.Edit, Permissions.Users.Delete]);
        _roleAdminGroup = await AddGroupAsync(dbSeed, "RoleAdministration",
        [
            Permissions.Roles.View, Permissions.Roles.Create, Permissions.Roles.Edit,
            Permissions.PermissionGroups.View, Permissions.PermissionGroups.Create, Permissions.PermissionGroups.Edit,
        ]);

        await CreateRoleAsync(scope, Roles.Administrator, [_fullControlGroup]);
        await CreateRoleAsync(scope, Roles.Employee, [_parkingBasicsGroup]);
        await CreateRoleAsync(scope, "RoleEditors", [_roleAdminGroup, _parkingBasicsGroup]);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Composing_a_role_materializes_the_union_of_its_groups()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "materializing-admin@example.test", Roles.Administrator);
        var role = await CreateRoleAsync(scope, "Materialized", []);

        var result = await CreateRoleAdminService(scope).SetGroupsAsync(
            role.Id, [_parkingBasicsGroup, _accountAdminGroup], admin.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));

        var claims = await GetPermissionsAsync(scope, role.Id);
        Assert.That(claims, Is.EquivalentTo(Permissions.Expand(
        [
            Permissions.Parking.View, Permissions.Parking.Reserve, Permissions.Parking.ViewLeaderboard,
            Permissions.Users.View, Permissions.Users.Create, Permissions.Users.Edit, Permissions.Users.Delete,
        ])));
    }

    [Test]
    public async Task Editing_a_group_reaches_every_role_composed_of_it()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "group-editing-admin@example.test", Roles.Administrator);
        var groupId = await AddGroupAsync(
            scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
            "AnalyticsOnly",
            [Permissions.Parking.ViewAnalytics]);
        var role = await CreateRoleAsync(scope, "AnalyticsRole", [groupId]);

        // The role's claims were derived once; widening the group has to re-derive them.
        Assert.That(await GetPermissionsAsync(scope, role.Id), Does.Not.Contain(Permissions.Parking.ManageSpots));

        var result = await CreateGroupService(scope).UpdateAsync(
            groupId, "AnalyticsOnly", null,
            [Permissions.Parking.ViewAnalytics, Permissions.Parking.ManageSpots], admin.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        Assert.That(await GetPermissionsAsync(scope, role.Id), Does.Contain(Permissions.Parking.ManageSpots));
    }

    [Test]
    public async Task A_group_editor_cannot_put_in_a_permission_they_do_not_hold()
    {
        await using var scope = _provider.CreateAsyncScope();
        var editor = await CreateUserAsync(scope, "group-editor@example.test", "RoleEditors");
        var groupId = await AddGroupAsync(
            scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
            "EditorTarget",
            [Permissions.Roles.View]);

        var result = await CreateGroupService(scope).UpdateAsync(
            groupId, "EditorTarget", null,
            [Permissions.Roles.View, Permissions.Users.Delete], editor.Id);

        Assert.That(result.Succeeded, Is.False);

        await using var verify = new D3ParkingDbContext(_options);
        var stored = await verify.PermissionGroups.Include(g => g.Entries)
            .FirstAsync(g => g.Id == groupId);
        Assert.That(stored.Permissions, Does.Not.Contain(Permissions.Users.Delete));
    }

    [Test]
    public async Task A_role_editor_cannot_compose_from_a_group_that_reaches_past_them()
    {
        await using var scope = _provider.CreateAsyncScope();
        var editor = await CreateUserAsync(scope, "role-editor@example.test", "RoleEditors");
        var role = await CreateRoleAsync(scope, "ComposeTarget", []);

        var result = await CreateRoleAdminService(scope).SetGroupsAsync(
            role.Id, [_accountAdminGroup], editor.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await GetPermissionsAsync(scope, role.Id), Is.Empty);
    }

    [Test]
    public async Task A_role_editor_can_compose_from_a_group_they_hold_themselves()
    {
        await using var scope = _provider.CreateAsyncScope();
        var editor = await CreateUserAsync(scope, "role-editor-ok@example.test", "RoleEditors");
        var role = await CreateRoleAsync(scope, "ComposableTarget", []);

        var result = await CreateRoleAdminService(scope).SetGroupsAsync(
            role.Id, [_parkingBasicsGroup], editor.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        Assert.That(await GetPermissionsAsync(scope, role.Id), Does.Contain(Permissions.Parking.Reserve));
    }

    [Test]
    public async Task A_group_stores_the_dependency_a_permission_is_useless_without()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "dependency-admin@example.test", Roles.Administrator);

        // Settings.Edit alone: reachable only through a hand-crafted request, since the editor
        // itself ticks the dependency.
        var result = await CreateGroupService(scope).CreateAsync(
            "SettingsWriters", null, [Permissions.Settings.Edit], admin.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));

        await using var verify = new D3ParkingDbContext(_options);
        var stored = await verify.PermissionGroups.Include(g => g.Entries)
            .FirstAsync(g => g.Name == "SettingsWriters");
        Assert.That(stored.Permissions, Does.Contain(Permissions.Settings.View));
    }

    [Test]
    public async Task Built_in_groups_cannot_be_edited_even_by_an_administrator()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "builtin-group-admin@example.test", Roles.Administrator);

        var result = await CreateGroupService(scope).UpdateAsync(
            _fullControlGroup, "FullControl", null, [Permissions.Parking.View], admin.Id);

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task A_group_in_use_by_a_role_cannot_be_deleted()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "group-deleting-admin@example.test", Roles.Administrator);
        var groupId = await AddGroupAsync(
            scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
            "InUse",
            [Permissions.Parking.View]);
        await CreateRoleAsync(scope, "UsesTheGroup", [groupId]);

        var result = await CreateGroupService(scope).DeleteAsync(groupId, admin.Id);

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task Built_in_role_compositions_cannot_be_edited_even_by_an_administrator()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "builtin-role-admin@example.test", Roles.Administrator);
        var employee = await FindRoleAsync(scope, Roles.Employee);

        var result = await CreateRoleAdminService(scope).SetGroupsAsync(employee.Id, [], admin.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await GetPermissionsAsync(scope, employee.Id), Does.Contain(Permissions.Parking.Reserve));
    }

    [Test]
    public async Task A_user_manager_cannot_assign_a_role_that_reaches_past_their_own()
    {
        await using var scope = _provider.CreateAsyncScope();
        var manager = await CreateUserAsync(scope, "user-manager@example.test", "RoleEditors");
        var victim = await CreateUserAsync(scope, "promotion-target@example.test");

        var result = await CreateUserAdminService(scope).SetRolesAsync(
            victim.Id, [Roles.Administrator], manager.Id);

        Assert.That(result.Succeeded, Is.False);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var reloaded = await userManager.FindByIdAsync(victim.Id.ToString());
        Assert.That(await userManager.IsInRoleAsync(reloaded!, Roles.Administrator), Is.False);
    }

    [Test]
    public async Task An_administrator_can_still_assign_any_role()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "assigning-admin@example.test", Roles.Administrator);
        var newcomer = await CreateUserAsync(scope, "newcomer@example.test");

        var result = await CreateUserAdminService(scope).SetRolesAsync(
            newcomer.Id, [Roles.Employee], admin.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
    }

    [Test]
    public async Task Changing_a_roles_composition_is_written_to_the_audit_trail()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateUserAsync(scope, "auditing-admin@example.test", Roles.Administrator);
        var role = await CreateRoleAsync(scope, "AuditedTarget", []);

        await CreateRoleAdminService(scope).SetGroupsAsync(role.Id, [_parkingBasicsGroup], admin.Id);

        await using var dbContext = new D3ParkingDbContext(_options);
        var recorded = await dbContext.AccountAuditEvents
            .Where(e => e.UserId == admin.Id && e.Type == AccountAuditEventType.RolePermissionsChanged)
            .ToListAsync();

        Assert.That(recorded, Is.Not.Empty);
        Assert.That(recorded.Any(e => e.Detail!.Contains("AuditedTarget")), Is.True);
        Assert.That(recorded.Any(e => e.Detail!.Contains("ParkingBasics")), Is.True);
    }

    private static async Task<Guid> AddGroupAsync(
        D3ParkingDbContext dbContext,
        string name,
        IReadOnlyList<string> permissions,
        bool builtIn = false)
    {
        var group = new PermissionGroup(name, null, permissions, builtIn);
        dbContext.PermissionGroups.Add(group);
        await dbContext.SaveChangesAsync();
        return group.Id;
    }

    private async Task<ApplicationRole> CreateRoleAsync(AsyncServiceScope scope, string name, IReadOnlyList<Guid> groupIds)
    {
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(name);
        if (role is null)
        {
            role = new ApplicationRole(name);
            await roleManager.CreateAsync(role);
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
        foreach (var groupId in groupIds)
        {
            dbContext.RolePermissionGroups.Add(new RolePermissionGroup(role.Id, groupId));
        }

        await dbContext.SaveChangesAsync();
        await CreateMaterializer(scope).MaterializeAsync(role.Id);
        return role;
    }

    private static async Task<ApplicationRole> FindRoleAsync(AsyncServiceScope scope, string name) =>
        (await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(name))!;

    private static async Task<ApplicationUser> CreateUserAsync(AsyncServiceScope scope, string email, string? role = null)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Status = AccountStatus.Active,
        };

        await userManager.CreateAsync(user, "Str0ng-Passw0rd!");
        if (role is not null)
        {
            await userManager.AddToRoleAsync(user, role);
        }

        return user;
    }

    private static async Task<IReadOnlyList<string>> GetPermissionsAsync(AsyncServiceScope scope, Guid roleId)
    {
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        return (await roleManager.GetClaimsAsync(role!))
            .Where(c => c.Type == D3ParkingClaimTypes.Permission)
            .Select(c => c.Value)
            .ToArray();
    }

    private static RolePermissionMaterializer CreateMaterializer(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        NullLogger<RolePermissionMaterializer>.Instance);

    private RoleAdminService CreateRoleAdminService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        CreateMaterializer(scope),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<RoleAdminService>.Instance);

    private PermissionGroupAdminService CreateGroupService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        CreateMaterializer(scope),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<PermissionGroupAdminService>.Instance);

    private UserAdminService CreateUserAdminService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<UserAdminService>.Instance);
}
