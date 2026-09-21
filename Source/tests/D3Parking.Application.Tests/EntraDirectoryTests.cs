using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using D3Parking.Application.Identity;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Parking;
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
    private FailAfterVisitorAuditSave _failure = null!;

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
            InitialCatalog = $"D3Parking_EntraDirectoryTests_{Guid.NewGuid():N}",
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
        _failure = new FailAfterVisitorAuditSave();
        services.AddLogging();
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(builder.ConnectionString).AddInterceptors(_failure));
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
    public async Task A_rename_onto_an_address_another_account_holds_leaves_the_account_findable()
    {
        Guid userId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var created = await CreateService(scope).SyncAsync(Identity("oid-rename", "original@example.test"));
            Assert.That(created.Succeeded, Is.True);
            userId = created.UserId;

            await CreateLocalUserAsync(scope, "taken@example.test", emailConfirmed: true);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            // The directory now asserts, for this account, an address somebody else already holds
            // here. Identity refuses the update — and the refusal must not be discarded, or the
            // account is written with the new Email and the old NormalizedEmail and stops being
            // findable under either.
            var renamed = await CreateService(scope).SyncAsync(Identity("oid-rename", "taken@example.test"));

            Assert.That(renamed.Succeeded, Is.True,
                "A refused rename is not a reason to refuse the sign-in itself.");
        }

        await using (var verify = _provider.CreateAsyncScope())
        {
            var userManager = verify.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var reloaded = await userManager.FindByIdAsync(userId.ToString());
            Assert.That(reloaded!.Email, Is.EqualTo("original@example.test"),
                "The rename must be rolled back whole, not half-applied.");

            var found = await userManager.FindByEmailAsync("original@example.test");
            Assert.That(found?.Id, Is.EqualTo(userId),
                "Email and NormalizedEmail must not drift apart — the account has to stay findable.");
        }
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

    /// <remarks>
    /// The four cases below pin what "the directory said nothing about roles" means, which differs
    /// by caller and by whether the account is being created. Getting it wrong is quiet in both
    /// directions: too eager and a revoked assignment comes back on the next sign-in, too shy and
    /// the default-roles setting does nothing while promising otherwise, leaving a first-time
    /// account signed in to an empty application.
    /// </remarks>
    [Test]
    public async Task A_first_sign_in_with_no_app_role_falls_back_to_the_default_roles()
    {
        _entra.DefaultRoles = [Roles.Employee];

        await using var scope = _provider.CreateAsyncScope();

        // A sign-in by someone the directory assigned no app role: an empty array, never null.
        var result = await CreateService(scope).SyncAsync(
            Identity("oid-default", "default@example.test", roles: []));

        Assert.That(result.Created, Is.True);
        Assert.That(await RolesOfAsync(scope, result.UserId), Does.Contain(Roles.Employee));
    }

    [Test]
    public async Task An_app_role_the_directory_did_send_wins_over_the_default_roles()
    {
        _entra.DefaultRoles = [Roles.Employee];

        await using var scope = _provider.CreateAsyncScope();
        var result = await CreateService(scope).SyncAsync(
            Identity("oid-default-override", "override@example.test", roles: ["Parking.LotManager"]));

        var roles = await RolesOfAsync(scope, result.UserId);
        Assert.That(roles, Does.Contain(Roles.LotManager));
        Assert.That(roles, Does.Not.Contain(Roles.Employee),
            "The baseline is for accounts the directory does not classify, not an addition to those it does.");
    }

    [Test]
    public async Task The_default_roles_do_not_come_back_when_an_app_role_is_later_revoked()
    {
        _entra.DefaultRoles = [Roles.Employee];

        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);

        var created = await service.SyncAsync(Identity("oid-default-revoke", "revoke-default@example.test", roles: []));
        Assert.That(await RolesOfAsync(scope, created.UserId), Does.Contain(Roles.Employee));

        // Not a first sign-in any more: an empty array is now the directory revoking what it granted.
        await service.SyncAsync(Identity("oid-default-revoke", "revoke-default@example.test", roles: []));

        Assert.That(await RolesOfAsync(scope, created.UserId), Is.Empty,
            "After the account exists, an empty role set means exactly that.");
    }

    [Test]
    public async Task Administrator_is_never_handed_out_as_a_default_role()
    {
        // The settings page refuses this, but a value pinned in configuration never passes through
        // that validation — and it would make everyone in the tenant an administrator here.
        _entra.DefaultRoles = [Roles.Administrator, Roles.Employee];

        await using var scope = _provider.CreateAsyncScope();
        var result = await CreateService(scope).SyncAsync(
            Identity("oid-default-admin", "admin-default@example.test", roles: []));

        var roles = await RolesOfAsync(scope, result.UserId);
        Assert.That(roles, Does.Not.Contain(Roles.Administrator));
        Assert.That(roles, Does.Contain(Roles.Employee));
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
    public async Task Deprovisioning_rejects_an_outer_read_committed_transaction_before_changing_the_account_or_visits()
    {
        Guid userId;
        Guid hostedId;
        Guid createdId;
        string? stampBefore;
        int auditCountBefore;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = CreateService(scope);
            var created = await service.SyncAsync(Identity("oid-weaker-transaction", "weaker-transaction@example.test",
                roles: ["Parking.Employee"]));
            Assert.That(created.Succeeded, Is.True);
            userId = created.UserId;
            var db = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
            stampBefore = (await db.Users.SingleAsync(u => u.Id == userId)).SecurityStamp;
            var now = DateTimeOffset.UtcNow;
            var otherHost = Guid.NewGuid();
            var hosted = new VisitorBooking(Guid.NewGuid(), "Synthetic hosted visitor", null, null, userId,
                now.AddDays(1), now.AddDays(1).AddHours(1), otherHost, now);
            var createdForOtherHost = new VisitorBooking(Guid.NewGuid(), "Synthetic created visitor", null, null, otherHost,
                now.AddDays(2), now.AddDays(2).AddHours(1), userId, now);
            hostedId = hosted.Id;
            createdId = createdForOtherHost.Id;
            db.VisitorBookings.AddRange(hosted, createdForOtherHost);
            await db.SaveChangesAsync();
            auditCountBefore = await db.AccountAuditEvents.CountAsync(a => a.UserId == userId);

            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.SetActiveAsync(ExternalProviders.EntraId, "oid-weaker-transaction", active: false));
            Assert.That(exception!.Message, Is.EqualTo("Directory status changes require a serializable transaction."));
            Assert.That(db.ChangeTracker.HasChanges(), Is.False);
            // Commit volajícího prokáže, že nulové změny nejsou jen důsledkem rollbacku testu.
            await transaction.CommitAsync();
        }

        await using var verify = new D3ParkingDbContext(_options);
        var user = await verify.Users.SingleAsync(u => u.Id == userId);
        var bookings = await verify.VisitorBookings.ToDictionaryAsync(v => v.Id);
        Assert.Multiple(() =>
        {
            Assert.That(user.Status, Is.EqualTo(AccountStatus.Active));
            Assert.That(user.SecurityStamp, Is.EqualTo(stampBefore));
            Assert.That(bookings[hostedId].Status, Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That(bookings[hostedId].HostUserId, Is.EqualTo(userId));
            Assert.That(bookings[createdId].Status, Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That(bookings[createdId].HostUserId, Is.Not.Null);
            Assert.That(verify.UserRoles.Any(r => r.UserId == userId && r.RoleId == _employeeRoleId), Is.True);
            Assert.That(verify.ExternalRoleAssignments.Any(a => a.UserId == userId), Is.True);
            Assert.That(verify.AccountAuditEvents.Count(a => a.UserId == userId), Is.EqualTo(auditCountBefore));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Deprovisioning_cancels_hosted_and_created_visits_once_including_an_inactive_retry(bool alreadyBlocked)
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = CreateService(scope);
        var created = await service.SyncAsync(Identity("oid-visitor-leaver", "visitor-leaver@example.test"));
        if (alreadyBlocked)
            Assert.That((await service.SetActiveAsync(ExternalProviders.EntraId, "oid-visitor-leaver", false)).Succeeded, Is.True);

        var now = DateTimeOffset.UtcNow;
        var otherAccount = Guid.NewGuid();
        var hosted = new VisitorBooking(Guid.NewGuid(), "Synthetic hosted visitor", null, null, created.UserId,
            now.AddDays(1), now.AddDays(1).AddHours(1), otherAccount, now);
        var createdForOtherHost = new VisitorBooking(Guid.NewGuid(), "Synthetic other host", null, null, otherAccount,
            now.AddDays(2), now.AddDays(2).AddHours(1), created.UserId, now);
        var ended = new VisitorBooking(Guid.NewGuid(), "Synthetic ended visitor", null, null, created.UserId,
            now.AddDays(-2), now.AddDays(-2).AddHours(1), created.UserId, now.AddDays(-3));
        await using (var seed = new D3ParkingDbContext(_options))
        {
            seed.AddRange(hosted, createdForOtherHost, ended);
            await seed.SaveChangesAsync();
        }

        Assert.That((await service.SetActiveAsync(ExternalProviders.EntraId, "oid-visitor-leaver", false)).Succeeded, Is.True);
        Assert.That((await service.SetActiveAsync(ExternalProviders.EntraId, "oid-visitor-leaver", false)).Succeeded, Is.True);

        await using var verify = new D3ParkingDbContext(_options);
        var bookings = await verify.VisitorBookings.ToDictionaryAsync(v => v.Id);
        var audits = await verify.AccountAuditEvents.Where(a => a.UserId == created.UserId
            && a.Type == AccountAuditEventType.ReservationOverridden).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(verify.Users.Single(u => u.Id == created.UserId).Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(bookings[hosted.Id].Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(bookings[createdForOtherHost.Id].Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(bookings[ended.Id].Status, Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That(bookings[ended.Id].HostUserId, Is.EqualTo(created.UserId));
            Assert.That(audits, Has.Count.EqualTo(2));
            Assert.That(audits.All(a => a.Actor == "system"), Is.True);
            Assert.That(audits.Any(a => a.Detail!.Contains(hosted.Id.ToString())), Is.True);
            Assert.That(audits.Any(a => a.Detail!.Contains(createdForOtherHost.Id.ToString())), Is.True);
            Assert.That(audits.All(a => a.Detail is not null && !a.Detail.Contains("Synthetic")), Is.True);
            Assert.That(verify.AccountAuditEvents.Count(a => a.UserId == created.UserId
                && a.Type == AccountAuditEventType.Blocked), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_failure_after_visitor_cancellation_rolls_back_directory_status_sessions_roles_and_audit()
    {
        Guid userId;
        Guid bookingId;
        string? stampBefore;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = CreateService(scope);
            var created = await service.SyncAsync(Identity("oid-atomic-leaver", "atomic-leaver@example.test", roles: ["Parking.Employee"]));
            userId = created.UserId;
            var db = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
            stampBefore = (await db.Users.SingleAsync(u => u.Id == userId)).SecurityStamp;
            var now = DateTimeOffset.UtcNow;
            var booking = new VisitorBooking(Guid.NewGuid(), "Synthetic rollback visitor", null, null, userId,
                now.AddDays(1), now.AddDays(1).AddHours(1), userId, now);
            bookingId = booking.Id;
            db.VisitorBookings.Add(booking);
            await db.SaveChangesAsync();

            _failure.Armed = true;
            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.SetActiveAsync(ExternalProviders.EntraId, "oid-atomic-leaver", false));
            Assert.That(exception!.Message, Is.EqualTo("Synthetic failure after visitor audit save."));
        }

        await using (var verify = new D3ParkingDbContext(_options))
        {
            var user = await verify.Users.SingleAsync(u => u.Id == userId);
            Assert.Multiple(() =>
            {
                Assert.That(user.Status, Is.EqualTo(AccountStatus.Active));
                Assert.That(user.SecurityStamp, Is.EqualTo(stampBefore));
                Assert.That(verify.UserRoles.Any(r => r.UserId == userId && r.RoleId == _employeeRoleId), Is.True);
                Assert.That(verify.ExternalRoleAssignments.Any(a => a.UserId == userId), Is.True);
                Assert.That(verify.VisitorBookings.Single(v => v.Id == bookingId).Status, Is.EqualTo(VisitorBookingStatus.Booked));
                Assert.That(verify.AccountAuditEvents.Any(a => a.UserId == userId
                    && (a.Type == AccountAuditEventType.ReservationOverridden || a.Type == AccountAuditEventType.Blocked)), Is.False);
            });
        }

        await using var retryScope = _provider.CreateAsyncScope();
        Assert.That((await CreateService(retryScope).SetActiveAsync(ExternalProviders.EntraId, "oid-atomic-leaver", false)).Succeeded, Is.True);
        await using var final = new D3ParkingDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(final.Users.Single(u => u.Id == userId).Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(final.VisitorBookings.Single(v => v.Id == bookingId).Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(final.AccountAuditEvents.Count(a => a.UserId == userId
                && a.Type == AccountAuditEventType.ReservationOverridden), Is.EqualTo(1));
        });
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

    private sealed class FailAfterVisitorAuditSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<AccountAuditEvent>()
                .Any(e => e.Entity.Type == AccountAuditEventType.ReservationOverridden
                    && e.Entity.Detail != null && e.Entity.Detail.Contains("employee departure")))
            {
                Armed = false;
                throw new InvalidOperationException("Synthetic failure after visitor audit save.");
            }
            return ValueTask.FromResult(result);
        }
    }

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
