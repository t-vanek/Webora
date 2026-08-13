using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The installation must never be left with nobody able to administer it. Each test starts from an
/// empty database and puts a known set of administrators in place: "last administrator" is a claim
/// about the whole installation, so it cannot be asserted in a fixture where other tests are
/// creating administrators of their own.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class LastAdministratorTests
{
    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the last-administrator tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_LastAdministratorTests",
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
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        await roleManager.CreateAsync(new ApplicationRole(Roles.Administrator));
        await roleManager.CreateAsync(new ApplicationRole(Roles.Employee));
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
    public async Task The_last_administrator_cannot_take_their_own_role_away()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateAdminAsync(scope, "sole@example.test");

        var result = await CreateUserAdminService(scope).SetRolesAsync(admin.Id, [], admin.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await IsAdministratorAsync(scope, admin.Id), Is.True);
    }

    [Test]
    public async Task The_last_administrator_cannot_swap_their_role_for_another_one()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateAdminAsync(scope, "swapping@example.test");

        // Swapping is the same removal wearing a different hat, and is refused the same way.
        var result = await CreateUserAdminService(scope).SetRolesAsync(admin.Id, [Roles.Employee], admin.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await IsAdministratorAsync(scope, admin.Id), Is.True);
    }

    [Test]
    public async Task Another_administrator_cannot_strip_the_last_one_either()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateAdminAsync(scope, "target@example.test");
        var actor = await CreateUserAsync(scope, "actor@example.test");

        var result = await CreateUserAdminService(scope).SetRolesAsync(admin.Id, [], actor.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await IsAdministratorAsync(scope, admin.Id), Is.True);
    }

    [Test]
    public async Task A_blocked_co_administrator_does_not_count_as_cover()
    {
        await using var scope = _provider.CreateAsyncScope();
        var active = await CreateAdminAsync(scope, "active@example.test");
        var blocked = await CreateAdminAsync(scope, "blocked@example.test", AccountStatus.Blocked);

        // Two rows in the role, but only one account that could sign in and administer anything.
        var result = await CreateUserAdminService(scope).SetRolesAsync(active.Id, [], active.Id);

        Assert.That(result.Succeeded, Is.False,
            "A blocked administrator cannot administer anything, so it must not count as cover.");
        Assert.That(await IsAdministratorAsync(scope, active.Id), Is.True);
        Assert.That(await IsAdministratorAsync(scope, blocked.Id), Is.True);
    }

    [Test]
    public async Task A_deactivated_co_administrator_does_not_count_as_cover()
    {
        await using var scope = _provider.CreateAsyncScope();
        var active = await CreateAdminAsync(scope, "still-here@example.test");
        await CreateAdminAsync(scope, "gone@example.test", AccountStatus.Deactivated);

        var result = await CreateUserAdminService(scope).SetRolesAsync(active.Id, [], active.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await IsAdministratorAsync(scope, active.Id), Is.True);
    }

    [Test]
    public async Task With_a_second_active_administrator_the_role_can_be_given_up()
    {
        await using var scope = _provider.CreateAsyncScope();
        var leaving = await CreateAdminAsync(scope, "leaving@example.test");
        await CreateAdminAsync(scope, "staying@example.test");

        var result = await CreateUserAdminService(scope).SetRolesAsync(leaving.Id, [Roles.Employee], leaving.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(" ", result.Errors));
        Assert.That(await IsAdministratorAsync(scope, leaving.Id), Is.False);
    }

    [Test]
    public async Task The_last_administrator_cannot_be_deleted()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateAdminAsync(scope, "undeletable@example.test");
        var actor = await CreateUserAsync(scope, "deleter@example.test");

        var result = await CreateUserAdminService(scope).DeleteAsync(admin.Id, actor.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(await IsAdministratorAsync(scope, admin.Id), Is.True);
    }

    [Test]
    public async Task The_detail_screen_is_told_the_account_is_the_last_administrator()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = await CreateAdminAsync(scope, "flagged@example.test");
        var ordinary = await CreateUserAsync(scope, "ordinary@example.test");

        var service = CreateUserAdminService(scope);

        // The flag is what lets the screen lock the checkbox up front rather than letting someone
        // uncheck it and discover the refusal on save.
        Assert.That((await service.GetAsync(admin.Id))!.IsLastAdministrator, Is.True);
        Assert.That((await service.GetAsync(ordinary.Id))!.IsLastAdministrator, Is.False);
    }

    [Test]
    public async Task Deleting_an_employee_cascades_live_resources_and_keeps_anonymous_history()
    {
        await using var scope = _provider.CreateAsyncScope();
        var actor = await CreateAdminAsync(scope, "lifecycle-admin@example.test");
        var employee = await CreateUserAsync(scope, "departing@example.test");
        var now = DateTimeOffset.UtcNow;

        await using (var seed = new D3ParkingDbContext(_options))
        {
            var spot = new ParkingSpot("LIFE-1", ParkingSpotType.Standard);
            spot.AssignOwner(employee.Id);
            var reservation = new Reservation(spot.Id, employee.Id, now.AddDays(2), now.AddDays(2).AddHours(8), false, now, 12);
            var queue = new QueueEntry(employee.Id, now.AddDays(3), now.AddDays(3).AddHours(8), now);
            var vehicle = new CompanyVehicle("LIFE-001", VehicleType.Company, "Lifecycle car",
                employee.Email, spot.Id, null, now);
            vehicle.Pair(employee.Id, now);

            seed.AddRange(spot, reservation, queue, vehicle,
                new Notification(employee.Id, NotificationCategory.Administrative, NotificationLevel.Info,
                    "Departure", "Pending", now),
                new AccountAuditEvent(employee.Id, AccountAuditEventType.Activated, "system", null, now));
            await seed.SaveChangesAsync();
        }

        var service = CreateUserAdminService(scope);
        var impact = await service.GetDeletionImpactAsync(employee.Id);
        Assert.Multiple(() =>
        {
            Assert.That(impact, Is.Not.Null);
            Assert.That(impact!.OwnedSpots, Is.EqualTo(1));
            Assert.That(impact.PairedVehicles, Is.EqualTo(1));
            Assert.That(impact.ActiveReservations, Is.EqualTo(1));
            Assert.That(impact.ActiveQueueEntries, Is.EqualTo(1));
        });

        var result = await service.DeleteAsync(employee.Id, actor.Id);
        Assert.That(result.Succeeded, Is.True, string.Join("; ", result.Errors));

        await using var verify = new D3ParkingDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(verify.Users.Any(u => u.Id == employee.Id), Is.False);
            Assert.That(verify.ParkingSpots.Single(s => s.Code == "LIFE-1").OwnerId, Is.Null);
            Assert.That(verify.CompanyVehicles.Single(v => v.Plate == "LIFE-001").PairedUserId, Is.Null);
            Assert.That(verify.CompanyVehicles.Single(v => v.Plate == "LIFE-001").DriverEmail, Is.Null);
            Assert.That(verify.Reservations.Single(r => r.UserId == employee.Id).Status, Is.EqualTo(ReservationStatus.Cancelled));
            Assert.That(verify.QueueEntries.Single(q => q.UserId == employee.Id).Status, Is.EqualTo(QueueEntryStatus.Cancelled));
            Assert.That(verify.Notifications.Any(n => n.UserId == employee.Id), Is.False);
            Assert.That(verify.AccountAuditEvents.Any(a => a.UserId == employee.Id && a.Type == AccountAuditEventType.Deleted), Is.True);
        });
    }

    private static async Task<ApplicationUser> CreateAdminAsync(
        AsyncServiceScope scope,
        string email,
        AccountStatus status = AccountStatus.Active)
    {
        var user = await CreateUserAsync(scope, email, status);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await userManager.AddToRoleAsync(user, Roles.Administrator);
        return user;
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        AsyncServiceScope scope,
        string email,
        AccountStatus status = AccountStatus.Active)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Status = status,
        };

        var created = await userManager.CreateAsync(user, "Str0ng-Passw0rd!");
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<bool> IsAdministratorAsync(AsyncServiceScope scope, Guid userId)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is not null && await userManager.IsInRoleAsync(user, Roles.Administrator);
    }

    private UserAdminService CreateUserAdminService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<UserAdminService>.Instance);
}
