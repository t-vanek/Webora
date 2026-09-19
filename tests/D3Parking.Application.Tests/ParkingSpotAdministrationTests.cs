using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>Real SQL Server only. Creates an isolated database; never uses the configured catalog.</summary>
[TestFixture]
[NonParallelizable]
public class ParkingSpotAdministrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
    private DbContextOptions<D3ParkingDbContext>? _options;
    private ParkingSpotService _service = null!;
    private Guid _manager;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
            Assert.Ignore("ConnectionStrings__SqlServer is not set; admin consistency requires real SQL Server.");
        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_AdminTests_{Guid.NewGuid():N}",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        _manager = await AddActor(db, Permissions.Parking.ManageSpots);
        _service = Service(_options);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [Test]
    public async Task Stale_details_and_activity_cannot_overwrite_a_newer_edit()
    {
        var original = await SeedSpot();
        var first = await _service.UpdateDetailsAsync(original.Id, "NEW-" + original.Code, original.Type,
            "new note", 2, original.Version, _manager);
        var second = await _service.UpdateDetailsAsync(original.Id, original.Code, ParkingSpotType.Motorcycle,
            original.Notes, 1, original.Version, _manager);
        var deactivate = await _service.SetActiveCheckedAsync(original.Id, false, original.Version, _manager);
        var saved = await _service.GetAsync(original.Id);
        Assert.Multiple(() =>
        {
            Assert.That(first.Succeeded, Is.True);
            Assert.That(second.Errors, Does.Contain("Parking_Error_ConcurrentChange"));
            Assert.That(deactivate.Errors, Does.Contain("Parking_Error_ConcurrentChange"));
            Assert.That(saved!.Code, Is.EqualTo("NEW-" + original.Code));
            Assert.That(saved.Notes, Is.EqualTo("new note"));
            Assert.That(saved.ResidentCapacity, Is.EqualTo(2));
            Assert.That(saved.IsActive, Is.True);
        });
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.AccountAuditEvents.CountAsync(a => a.Detail != null && a.Detail.Contains(original.Id.ToString())), Is.EqualTo(1));
    }

    [TestCase(Permissions.Parking.ViewAnalytics, AccountStatus.Active)]
    [TestCase(Permissions.Parking.ManageSpots, AccountStatus.Blocked)]
    public async Task Direct_service_calls_require_an_active_manager(string permission, AccountStatus status)
    {
        var original = await SeedSpot();
        await using var db = new D3ParkingDbContext(_options!);
        var actor = await AddActor(db, permission, status);
        var update = await _service.UpdateDetailsAsync(original.Id, original.Code, original.Type, "unauthorized", 2, original.Version, actor);
        var deactivate = await _service.SetActiveCheckedAsync(original.Id, false, original.Version, actor);
        var absent = await _service.SetActiveCheckedAsync(Guid.NewGuid(), false, original.Version, actor);
        Assert.Multiple(() =>
        {
            Assert.That(update.Errors, Does.Contain("Parking_Error_AccessDenied"));
            Assert.That(deactivate.Errors, Does.Contain("Parking_Error_AccessDenied"));
            Assert.That(absent.Errors, Does.Contain("Parking_Error_AccessDenied"));
        });
        Assert.That((await _service.GetAsync(original.Id))!.IsActive, Is.True);
        Assert.That(await db.AccountAuditEvents.AnyAsync(a => a.UserId == actor), Is.False);
    }

    [Test]
    public async Task Permission_revoked_after_loading_is_rechecked_at_save()
    {
        var original = await SeedSpot();
        await using var db = new D3ParkingDbContext(_options!);
        var actor = await AddActor(db, Permissions.Parking.ManageSpots);
        await db.UserRoles.Where(r => r.UserId == actor).ExecuteDeleteAsync();
        var result = await _service.SetActiveCheckedAsync(original.Id, false, original.Version, actor);
        Assert.That(result.Errors, Does.Contain("Parking_Error_AccessDenied"));
        Assert.That((await _service.GetAsync(original.Id))!.IsActive, Is.True);
    }

    [Test]
    public async Task Two_edits_of_one_snapshot_have_one_winner()
    {
        var original = await SeedSpot();
        var results = await Task.WhenAll(
            _service.UpdateDetailsAsync(original.Id, original.Code, ParkingSpotType.Motorcycle, "first", 2, original.Version, _manager),
            _service.UpdateDetailsAsync(original.Id, original.Code, ParkingSpotType.ElectricCharging, "second", 3, original.Version, _manager));
        Assert.That(results.Count(r => r.Succeeded), Is.EqualTo(1));
        Assert.That(results.Single(r => !r.Succeeded).Errors, Does.Contain("Parking_Error_ConcurrentChange"));
    }

    [Test]
    public async Task Retyping_a_booked_spot_rolls_back_all_requested_details()
    {
        var original = await SeedSpot();
        await using var db = new D3ParkingDbContext(_options!);
        db.Reservations.Add(new Reservation(original.Id, Guid.NewGuid(), Now.AddHours(1), Now.AddHours(2), false, Now));
        await db.SaveChangesAsync();
        var result = await _service.UpdateDetailsAsync(original.Id, "NEW-" + original.Code, ParkingSpotType.Visitor,
            "new note", 4, original.Version, _manager);
        Assert.That(result.Errors, Does.Contain("Parking_Error_TypeChangeBooked"));
        var saved = await _service.GetAsync(original.Id);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.Code, Is.EqualTo(original.Code));
            Assert.That(saved.Type, Is.EqualTo(original.Type));
            Assert.That(saved.ResidentCapacity, Is.EqualTo(original.ResidentCapacity));
        });
    }

    [Test]
    public async Task Capacity_rejection_keeps_other_details_and_resident_memberships()
    {
        var original = await SeedSpot();
        await using var db = new D3ParkingDbContext(_options!);
        var spot = await db.ParkingSpots.SingleAsync(s => s.Id == original.Id);
        spot.SetResidentCapacity(2);
        db.ParkingSpotResidents.AddRange(new ParkingSpotResident(spot.Id, Guid.NewGuid(), Now),
            new ParkingSpotResident(spot.Id, Guid.NewGuid(), Now));
        await db.SaveChangesAsync();
        original = (await _service.GetAsync(original.Id))!;
        var result = await _service.UpdateDetailsAsync(original.Id, "NEW-" + original.Code, original.Type,
            "not saved", 1, original.Version, _manager);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ResidentCapacityBelowCount"));
        Assert.That((await _service.GetAsync(original.Id))!.Code, Is.EqualTo(original.Code));
        Assert.That(await db.ParkingSpotResidents.CountAsync(r => r.SpotId == original.Id), Is.EqualTo(2));
    }

    [Test]
    public async Task Visitor_booking_and_queue_hold_both_prevent_crossing_booking_pools()
    {
        var original = await SeedSpot();
        await using var db = new D3ParkingDbContext(_options!);
        var spot = await db.ParkingSpots.SingleAsync(s => s.Id == original.Id);
        spot.ChangeType(ParkingSpotType.Visitor);
        db.VisitorBookings.Add(new VisitorBooking(spot.Id, "Test visitor", null, null, null,
            Now.AddHours(1), Now.AddHours(2), _manager, Now));
        await db.SaveChangesAsync();
        original = (await _service.GetAsync(spot.Id))!;
        var visitor = await _service.UpdateDetailsAsync(original.Id, original.Code, ParkingSpotType.Standard,
            null, 1, original.Version, _manager);
        Assert.That(visitor.Errors, Does.Contain("Parking_Error_TypeChangeBooked"));

        var shared = await SeedSpot();
        var queue = new QueueEntry(Guid.NewGuid(), Now.AddHours(1), Now.AddHours(2), Now);
        queue.Offer(shared.Id, Now.AddMinutes(15));
        db.QueueEntries.Add(queue);
        await db.SaveChangesAsync();
        var held = await _service.UpdateDetailsAsync(shared.Id, shared.Code, ParkingSpotType.Visitor,
            null, 1, shared.Version, _manager);
        Assert.That(held.Errors, Does.Contain("Parking_Error_TypeChangeBooked"));
    }

    [Test]
    public async Task Membership_without_legacy_owner_still_prevents_a_visitor_spot()
    {
        var original = await SeedSpot();
        await using var db = new D3ParkingDbContext(_options!);
        db.ParkingSpotResidents.Add(new ParkingSpotResident(original.Id, Guid.NewGuid(), Now));
        await db.SaveChangesAsync();
        var result = await _service.UpdateDetailsAsync(original.Id, original.Code, ParkingSpotType.Visitor,
            null, 1, original.Version, _manager);
        Assert.That(result.Errors, Does.Contain("Parking_Error_VisitorSpotNoOwner"));
    }

    [Test]
    public async Task Concurrent_retyping_and_reserving_cannot_leave_a_booking_in_the_wrong_pool()
    {
        var original = await SeedSpot();
        var retyping = _service.UpdateDetailsAsync(original.Id, original.Code, ParkingSpotType.Visitor,
            null, 1, original.Version, _manager);
        var booking = Reservations().ReserveAsync(Guid.NewGuid(), original.Id, Now.AddHours(1), Now.AddHours(2));
        await Task.WhenAll(retyping, booking);
        var saved = (await _service.GetAsync(original.Id))!;
        await using var db = new D3ParkingDbContext(_options!);
        var reserved = await db.Reservations.AnyAsync(r => r.SpotId == original.Id && r.Status == ReservationStatus.Reserved);
        Assert.Multiple(() =>
        {
            Assert.That(retyping.Result.Succeeded && booking.Result.Succeeded, Is.False);
            Assert.That(saved.Type == ParkingSpotType.Visitor && reserved, Is.False);
            Assert.That(booking.Result.Succeeded, Is.EqualTo(reserved));
        });
    }

    [Test]
    public async Task Failed_save_keeps_details_and_audit_unchanged()
    {
        var original = await SeedSpot();
        var failingOptions = new DbContextOptionsBuilder<D3ParkingDbContext>(_options!)
            .AddInterceptors(new FailAfterSave()).Options;
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Service(failingOptions).UpdateDetailsAsync(
            original.Id, "NEW-" + original.Code, original.Type, "not saved", 2, original.Version, _manager));
        Assert.That((await _service.GetAsync(original.Id))!.Code, Is.EqualTo(original.Code));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.AccountAuditEvents.AnyAsync(a => a.Detail != null && a.Detail.Contains(original.Id.ToString())), Is.False);
    }

    [Test]
    public async Task Deactivation_preserves_reservations_and_records_actor_and_target_once()
    {
        var original = await SeedSpot();
        var booking = new Reservation(original.Id, Guid.NewGuid(), Now.AddHours(1), Now.AddHours(2), false, Now);
        await using var db = new D3ParkingDbContext(_options!);
        db.Reservations.Add(booking);
        await db.SaveChangesAsync();
        var result = await _service.SetActiveCheckedAsync(original.Id, false, original.Version, _manager);
        Assert.That(result.Succeeded, Is.True);
        var duplicate = await _service.SetActiveCheckedAsync(original.Id, false, original.Version, _manager);
        Assert.That(duplicate.Errors, Does.Contain("Parking_Error_ConcurrentChange"));
        Assert.That((await _service.GetAsync(original.Id))!.IsActive, Is.False);
        var newBooking = await Reservations().ReserveAsync(Guid.NewGuid(), original.Id, Now.AddHours(3), Now.AddHours(4));
        Assert.That(newBooking.Succeeded, Is.False, "Deactivation must prevent a new booking, not only change the admin display.");
        Assert.That(await db.Reservations.AsNoTracking().Where(r => r.Id == booking.Id).Select(r => r.Status).SingleAsync(), Is.EqualTo(ReservationStatus.Reserved));
        var audit = await db.AccountAuditEvents.SingleAsync(a => a.Detail != null && a.Detail.Contains(original.Id.ToString()));
        Assert.That(audit.Actor, Is.EqualTo($"admin:{_manager}"));
    }

    private async Task<D3Parking.Application.Parking.ParkingSpotDto> SeedSpot()
    {
        await using var db = new D3ParkingDbContext(_options!);
        var spot = new ParkingSpot($"S-{Guid.NewGuid():N}"[..12], ParkingSpotType.Standard);
        db.ParkingSpots.Add(spot);
        await db.SaveChangesAsync();
        return (await _service.GetAsync(spot.Id))!;
    }

    private static async Task<Guid> AddActor(D3ParkingDbContext db, string permission, AccountStatus status = AccountStatus.Active)
    {
        var user = new ApplicationUser { UserName = $"actor-{Guid.NewGuid():N}", Status = status };
        var role = new ApplicationRole($"role-{Guid.NewGuid():N}");
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
        db.RoleClaims.Add(new IdentityRoleClaim<Guid> { RoleId = role.Id, ClaimType = D3ParkingClaimTypes.Permission, ClaimValue = permission });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static ParkingSpotService Service(DbContextOptions<D3ParkingDbContext> options) => new(
        new TestDbContextFactory(options), new NullNotificationService(), new FakeParkingSettings(),
        new FakeSiteSettings(), new FixedTimeProvider(Now), new PassthroughLocalizer<ParkingMessages>());

    private ReservationService Reservations() => new(new TestDbContextFactory(_options!),
        new FakeParkingSettings(new D3Parking.Domain.Parking.Incentives.IncentivePolicy
        {
            BaseReservationCost = 0,
            WeeklyReservationLimitEnabled = false,
        }), new FakeSiteSettings(), new FixedTimeProvider(Now), new NullNotificationService(),
        new PassthroughLocalizer<ParkingMessages>());

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected failure before transaction commit.");
    }
}
