using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using D3Parking.Application.Identity;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Covers the one path from "the directory says this person exists" to an account here: creating,
/// linking, renaming, applying mapped roles, and the two rules the directory does not know about —
/// that a hand-granted role survives a sync, and that nobody can be left unable to administer the
/// installation.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class EntraDirectoryTests
{
    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private EntraIdOptions _entra = null!;

    private Guid _employeeRoleId;
    private Guid _lotManagerRoleId;

    [SetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the directory tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_EntraDirectoryTests",
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

        _entra = new EntraIdOptions
        {
            Enabled = true,
            TenantId = "tenant-a",
            ClientId = "client",
            LinkByVerifiedEmail = true,
            AllowJustInTimeProvisioning = true,
        };

        await using var scope = _provider.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var name in new[] { Roles.Administrator, Roles.Employee, Roles.LotManager })
        {
            await roleManager.CreateAsync(new ApplicationRole(name));
        }

        _employeeRoleId = (await roleManager.FindByNameAsync(Roles.Employee))!.Id;
        _lotManagerRoleId = (await roleManager.FindByNameAsync(Roles.LotManager))!.Id;

        var dbSeed = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
        dbSeed.ExternalRoleMappings.Add(new ExternalRoleMapping(ExternalProviders.EntraId, "Parking.Employee", _employeeRoleId));
        dbSeed.ExternalRoleMappings.Add(new ExternalRoleMapping(ExternalProviders.EntraId, "Parking.LotManager", _lotManagerRoleId));
        await dbSeed.SaveChangesAsync();
    }

    [TearDown]
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
    public async Task A_first_sign_in_creates_the_account_and_applies_its_mapped_roles()
    {
        await using var scope = _provider.CreateAsyncScope();

        var result = await CreateService(scope).SyncAsync(Identity("oid-1", "new@example.test", roles: ["Parking.LotManager"]));

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        Assert.That(result.Created, Is.True);
        Assert.That(await RolesOfAsync(scope, result.UserId), Does.Contain(Roles.LotManager));
    }

    [Test]
    public async Task A_created_account_has_no_local_password()
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await CreateService(scope).SyncAsync(Identity("oid-nopass", "nopass@example.test"));

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(result.UserId.ToString());

        // Sign-in belongs to the directory; a local password would be a second door nobody watches.
        Assert.That(await userManager.HasPasswordAsync(user!), Is.False);
        Assert.That(user!.IsFederated, Is.True);
    }

    [Test]
    public async Task The_object_id_identifies_the_account_even_after_a_rename()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);

        var first = await service.SyncAsync(Identity("oid-rename", "before@example.test", displayName: "Before"));
        var second = await service.SyncAsync(Identity("oid-rename", "after@example.test", displayName: "After"));

        Assert.That(second.UserId, Is.EqualTo(first.UserId), "A rename must not fork the account.");
        Assert.That(second.Created, Is.False);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(second.UserId.ToString());
        Assert.That(user!.Email, Is.EqualTo("after@example.test"));
        Assert.That(user.DisplayName, Is.EqualTo("After"));
    }

    [Test]
    public async Task An_existing_account_with_a_confirmed_email_is_adopted_rather_than_duplicated()
    {
        await using var scope = _provider.CreateAsyncScope();
        var local = await CreateLocalUserAsync(scope, "known@example.test", emailConfirmed: true);

        var result = await CreateService(scope).SyncAsync(Identity("oid-link", "known@example.test"));

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        Assert.That(result.Linked, Is.True);
        Assert.That(result.UserId, Is.EqualTo(local.Id));
    }

    [Test]
    public async Task An_adopted_account_keeps_the_password_it_registered_with()
    {
        await using var scope = _provider.CreateAsyncScope();
        var local = await CreateLocalUserAsync(scope, "keeps-password@example.test", emailConfirmed: true);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await CreateService(scope).SyncAsync(Identity("oid-keeps", "keeps-password@example.test"));

        Assert.That(result.Linked, Is.True);

        // The two ways in coexist on one account: adoption adds the directory as a way to sign in,
        // it does not take away the one the person chose. The self-service rules downstream key on
        // "has a password" precisely so this account can still change and recover it.
        var reloaded = await userManager.FindByIdAsync(local.Id.ToString());
        Assert.That(await userManager.HasPasswordAsync(reloaded!), Is.True);
        Assert.That(await userManager.CheckPasswordAsync(reloaded!, "Str0ng-Passw0rd!"), Is.True,
            "Linking must not silently disable the password the account already signs in with.");
    }

    [Test]
    public async Task An_account_whose_email_was_never_confirmed_is_not_adopted()
    {
        await using var scope = _provider.CreateAsyncScope();
        await CreateLocalUserAsync(scope, "unconfirmed@example.test", emailConfirmed: false);

        // Otherwise anyone able to get a directory account with a given address would inherit
        // whatever that address had registered here.
        var result = await CreateService(scope).SyncAsync(Identity("oid-unconfirmed", "unconfirmed@example.test"));

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task An_identity_from_another_tenant_is_refused()
    {
        await using var scope = _provider.CreateAsyncScope();

        var result = await CreateService(scope).SyncAsync(
            Identity("oid-foreign", "foreign@example.test") with { TenantId = "tenant-b" });

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task An_unknown_identity_is_refused_when_just_in_time_creation_is_off()
    {
        _entra.AllowJustInTimeProvisioning = false;
        _entra.LinkByVerifiedEmail = false;

        await using var scope = _provider.CreateAsyncScope();
        var result = await CreateService(scope).SyncAsync(Identity("oid-jit", "stranger@example.test"));

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task A_revoked_app_role_is_taken_back_on_the_next_sync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);

        var created = await service.SyncAsync(Identity("oid-revoke", "revoke@example.test", roles: ["Parking.LotManager"]));
        Assert.That(await RolesOfAsync(scope, created.UserId), Does.Contain(Roles.LotManager));

        await service.SyncAsync(Identity("oid-revoke", "revoke@example.test", roles: []));

        Assert.That(await RolesOfAsync(scope, created.UserId), Does.Not.Contain(Roles.LotManager));
    }

    [Test]
    public async Task A_hand_granted_role_survives_a_directory_sync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);
        var created = await service.SyncAsync(Identity("oid-manual", "manual@example.test", roles: ["Parking.Employee"]));

        // An administrator adds something the directory knows nothing about.
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(created.UserId.ToString());
        await userManager.AddToRoleAsync(user!, Roles.LotManager);

        await service.SyncAsync(Identity("oid-manual", "manual@example.test", roles: ["Parking.Employee"]));

        var roles = await RolesOfAsync(scope, created.UserId);
        Assert.That(roles, Does.Contain(Roles.LotManager), "Only what the directory granted may be taken back.");
        Assert.That(roles, Does.Contain(Roles.Employee));
    }

    [Test]
    public async Task An_app_role_with_no_mapping_is_ignored()
    {
        await using var scope = _provider.CreateAsyncScope();

        var result = await CreateService(scope).SyncAsync(
            Identity("oid-unmapped", "unmapped@example.test", roles: ["Something.Else"]));

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        Assert.That(await RolesOfAsync(scope, result.UserId), Is.Empty);
    }

    [Test]
    public async Task Provisioning_without_roles_does_not_revoke_what_the_account_holds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);
        var created = await service.SyncAsync(Identity("oid-scim", "scim@example.test", roles: ["Parking.LotManager"]));

        // A SCIM push carries no roles at all; reading that as an empty set would strip everyone.
        await service.SyncAsync(Identity("oid-scim", "scim@example.test") with { Roles = null });

        Assert.That(await RolesOfAsync(scope, created.UserId), Does.Contain(Roles.LotManager));
    }

    [Test]
    public async Task Deprovisioning_blocks_the_account_and_ends_its_sessions()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);
        var created = await service.SyncAsync(Identity("oid-leaver", "leaver@example.test"));

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var before = await userManager.GetSecurityStampAsync((await userManager.FindByIdAsync(created.UserId.ToString()))!);

        var result = await service.SetActiveAsync(ExternalProviders.EntraId, "oid-leaver", active: false);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        var after = await userManager.FindByIdAsync(created.UserId.ToString());
        Assert.That(after!.Status, Is.EqualTo(AccountStatus.Blocked));
        Assert.That(await userManager.GetSecurityStampAsync(after), Is.Not.EqualTo(before),
            "Blocking must rotate the security stamp so live sessions stop validating.");
    }

    [Test]
    public async Task Deprovisioning_the_last_administrator_is_refused()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);
        var created = await service.SyncAsync(Identity("oid-sole-admin", "sole@example.test"));

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(created.UserId.ToString());
        await userManager.AddToRoleAsync(user!, Roles.Administrator);

        // The directory does not know this installation needs somebody able to administer it.
        var result = await service.SetActiveAsync(ExternalProviders.EntraId, "oid-sole-admin", active: false);

        Assert.That(result.Succeeded, Is.False);
        var reloaded = await userManager.FindByIdAsync(created.UserId.ToString());
        Assert.That(reloaded!.Status, Is.EqualTo(AccountStatus.Active));
    }

    [Test]
    public async Task A_sync_never_strips_the_administrator_role_from_the_last_administrator()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);

        var dbContext = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
        var administratorRoleId = await dbContext.Roles.Where(r => r.Name == Roles.Administrator)
            .Select(r => r.Id).FirstAsync();
        dbContext.ExternalRoleMappings.Add(new ExternalRoleMapping(ExternalProviders.EntraId, "Parking.Admin", administratorRoleId));
        await dbContext.SaveChangesAsync();

        var created = await service.SyncAsync(Identity("oid-admin", "admin@example.test", roles: ["Parking.Admin"]));
        Assert.That(await RolesOfAsync(scope, created.UserId), Does.Contain(Roles.Administrator));

        // The app role assignment is revoked in Entra — obeying it would lock everyone out.
        await service.SyncAsync(Identity("oid-admin", "admin@example.test", roles: []));

        Assert.That(await RolesOfAsync(scope, created.UserId), Does.Contain(Roles.Administrator));
    }

    [Test]
    public async Task Reactivation_brings_a_returning_account_back()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);
        var created = await service.SyncAsync(Identity("oid-returner", "returner@example.test"));

        await service.SetActiveAsync(ExternalProviders.EntraId, "oid-returner", active: false);
        var result = await service.SetActiveAsync(ExternalProviders.EntraId, "oid-returner", active: true);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(created.UserId.ToString());
        Assert.That(user!.Status, Is.EqualTo(AccountStatus.Active));
    }

    private static ExternalIdentity Identity(
        string objectId,
        string email,
        string? displayName = null,
        IReadOnlyList<string>? roles = null) =>
        new(ExternalProviders.EntraId, objectId, email, "tenant-a", displayName, Department: null, Roles: roles);

    private static async Task<ApplicationUser> CreateLocalUserAsync(AsyncServiceScope scope, string email, bool emailConfirmed)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            Status = AccountStatus.Active,
        };

        var created = await userManager.CreateAsync(user, "Str0ng-Passw0rd!");
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<IReadOnlyList<string>> RolesOfAsync(AsyncServiceScope scope, Guid userId)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? [] : (await userManager.GetRolesAsync(user)).ToArray();
    }

    private EntraDirectoryService CreateService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        // Read through a lambda rather than captured, so a test that flips a switch mid-case (see
        // the just-in-time cases) still changes what the service under test sees.
        new FixedEntraSettings(() => _entra),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<EntraDirectoryService>.Instance);

    /// <summary>Serves the fixture's options as the effective settings; nothing here writes.</summary>
    private sealed class FixedEntraSettings(Func<EntraIdOptions> current) : IEntraSettingsService
    {
        public Task<EntraIdOptions> GetEffectiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current());

        public EntraIdOptions GetEffective() => current();

        public Task<EntraSettingsView> GetForAdminAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Accounts.AccountResult> UpdateAsync(
            EntraSettingsUpdate update, Guid actingUserId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
