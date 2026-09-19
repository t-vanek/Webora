using D3Parking.Application.Parking;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class StartedReservationProtectionRuleTests
{
    [TestCase("2026-03-29", 23)]
    [TestCase("2026-09-15", 24)]
    [TestCase("2026-10-25", 25)]
    public void An_all_day_booking_is_protected_from_local_midnight_until_the_next_midnight(string date, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var (start, end) = SiteTime.Day(DateOnly.Parse(date), zone);
        var booking = new Reservation(Guid.NewGuid(), Guid.NewGuid(), start, end, false, start.AddDays(-1));
        Assert.Multiple(() =>
        {
            Assert.That((end - start).TotalHours, Is.EqualTo(hours));
            Assert.That(booking.IsProtectedFromDisplacement(start.AddTicks(-1)), Is.False);
            Assert.That(booking.IsProtectedFromDisplacement(start), Is.True);
            Assert.That(booking.IsProtectedFromDisplacement(end.AddTicks(-1)), Is.True);
            Assert.That(booking.IsProtectedFromDisplacement(end), Is.False);
        });
    }

    [TestCase(-1, false, true)]
    [TestCase(0, false, false)]
    [TestCase(60, false, false)]
    [TestCase(120, false, false)]
    [TestCase(-1, true, false)]
    public void Only_a_future_unstarted_booking_can_be_moved(int minutesFromStart, bool checkedIn, bool canMove)
    {
        var start = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
        var originalSpot = Guid.NewGuid();
        var targetSpot = Guid.NewGuid();
        var booking = new Reservation(originalSpot, Guid.NewGuid(), start, start.AddHours(2), false, start.AddDays(-1));
        if (checkedIn) booking.CheckIn(start.AddMinutes(-2));
        var sequence = booking.CalendarSequence;

        if (canMove)
        {
            Assert.DoesNotThrow(() => booking.MoveTo(targetSpot, start.AddMinutes(minutesFromStart)));
            Assert.That(booking.SpotId, Is.EqualTo(targetSpot));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => booking.MoveTo(targetSpot, start.AddMinutes(minutesFromStart)));
            Assert.That(booking.SpotId, Is.EqualTo(originalSpot));
            Assert.That(booking.CalendarSequence, Is.EqualTo(sequence));
        }
    }
}

[TestFixture]
[NonParallelizable]
public class StartedReservationProtectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly IncentivePolicy Policy = new()
    {
        ReservationTimeMode = ReservationTimeMode.AllDay,
        BaseReservationCost = 0,
        WeeklyReservationLimitEnabled = false,
        PublicHolidayReservationsAllowed = true,
    };
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured)) Assert.Ignore("SQL Server is required for started booking protection tests.");
        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_StartedBookingTests_{Guid.NewGuid():N}",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private static IEnumerable<TestCaseData> PriorityConfigurations()
    {
        foreach (var reclaim in Enum.GetValues<ResidentReclaimPolicy>())
        foreach (var fallback in Enum.GetValues<ResidentNoReplacementAction>())
        foreach (var replacement in new[] { false, true })
        foreach (var (source, binding) in new[]
        {
            (SpotReleaseSource.Manual, false), (SpotReleaseSource.Manual, true),
            (SpotReleaseSource.UsagePlan, true), (SpotReleaseSource.AlternativeBooking, true),
        })
            yield return new TestCaseData(reclaim, fallback, replacement, source, binding);
    }

    [TestCaseSource(nameof(PriorityConfigurations))]
    public async Task No_priority_configuration_can_move_or_cancel_todays_all_day_booking(
        ResidentReclaimPolicy reclaim, ResidentNoReplacementAction fallback, bool replacement,
        SpotReleaseSource source, bool binding)
    {
        var (owner, spot, booking) = await SeedAsync(Today, source, replacement);
        var residents = Residents(Policy with
        {
            ResidentReclaimPolicy = reclaim,
            ResidentNoReplacementAction = fallback,
            ManualReleasesAreBinding = binding,
        });
        var result = await residents.ReclaimAsync(owner, Today, Today);
        Assert.That(result.Errors, Does.Contain("Parking_Error_StartedReservationProtected"));
        await using var db = new D3ParkingDbContext(_options);
        var saved = await db.Reservations.SingleAsync(r => r.Id == booking.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(saved.SpotId, Is.EqualTo(spot.Id));
            Assert.That(saved.Status, Is.EqualTo(ReservationStatus.Reserved));
            Assert.That(saved.StartUtc, Is.EqualTo(booking.StartUtc));
            Assert.That(saved.EndUtc, Is.EqualTo(booking.EndUtc));
            Assert.That(saved.CalendarSequence, Is.Zero);
        });
        Assert.That(await db.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == Today), Is.True);
        Assert.That(await db.QueueEntries.AnyAsync(q => q.UserId == booking.UserId), Is.False);
        Assert.That(await db.PointsLedgerEntries.AnyAsync(e => e.ReservationId == booking.Id), Is.False);
        Assert.That(await db.AccountAuditEvents.AnyAsync(e => e.UserId == booking.UserId), Is.False);
        var day = (await residents.GetMyOwnedSpotAsync(owner))!.DaySchedule.Single(d => d.Date == Today);
        Assert.That(day.CanReclaim, Is.False);
        Assert.That(day.ReclaimBlockedByStartedBooking, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task The_holder_can_voluntarily_give_up_todays_booking(bool cancel)
    {
        var (owner, spot, booking) = await SeedAsync(Today, SpotReleaseSource.Manual, false);
        var reservations = Reservations();
        var result = cancel
            ? await reservations.CancelAsync(booking.UserId, booking.Id)
            : await reservations.ReleaseAsync(booking.UserId, booking.Id);
        Assert.That(result.Succeeded, Is.True);
        Assert.That((await Residents().ReclaimAsync(owner, Today, Today)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == Today), Is.False);
    }

    [Test]
    public async Task Switching_to_time_windows_cannot_unlock_a_stored_all_day_booking()
    {
        var (owner, _, _) = await SeedAsync(Today, SpotReleaseSource.UsagePlan, true);
        var result = await Residents(Policy with
        {
            ReservationTimeMode = ReservationTimeMode.TimeWindow,
            ResidentReclaimPolicy = ResidentReclaimPolicy.AbsolutePriority,
        }).ReclaimAsync(owner, Today, Today);
        Assert.That(result.Errors, Does.Contain("Parking_Error_StartedReservationProtected"));
    }

    [Test]
    public async Task A_future_booking_can_still_be_moved_under_the_configured_policy()
    {
        var tomorrow = Today.AddDays(1);
        var (owner, spot, booking) = await SeedAsync(tomorrow, SpotReleaseSource.UsagePlan, true);
        Assert.That((await Residents(Policy with { ResidentReclaimPolicy = ResidentReclaimPolicy.ReplacementOnly })
            .ReclaimAsync(owner, tomorrow, tomorrow)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        var saved = await db.Reservations.SingleAsync(r => r.Id == booking.Id);
        Assert.That(saved.SpotId, Is.Not.EqualTo(spot.Id));
        Assert.That(saved.Status, Is.EqualTo(ReservationStatus.Reserved));
    }

    [Test]
    public async Task A_bulk_return_is_atomic_and_the_next_booking_can_be_handled_separately()
    {
        var (owner, spot, current) = await SeedAsync(Today, SpotReleaseSource.UsagePlan, true);
        var tomorrow = Today.AddDays(1);
        var (start, end) = SiteTime.Day(tomorrow, TimeZoneInfo.Utc);
        var next = new Reservation(spot.Id, current.UserId, start, end, false, Now);
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.Reservations.Add(next);
            db.SpotReleases.Add(new SpotRelease(spot.Id, owner, tomorrow, Now, 0, SpotReleaseSource.UsagePlan));
            await db.SaveChangesAsync();
        }

        var residents = Residents(Policy with { ResidentReclaimPolicy = ResidentReclaimPolicy.AbsolutePriority });
        Assert.That((await residents.ReclaimAsync(owner, Today, tomorrow)).Errors,
            Does.Contain("Parking_Error_StartedReservationProtected"));
        await using (var check = new D3ParkingDbContext(_options))
        {
            Assert.That(await check.Reservations.CountAsync(r => r.SpotId == spot.Id && r.Status == ReservationStatus.Reserved), Is.EqualTo(2));
            Assert.That(await check.SpotReleases.CountAsync(r => r.SpotId == spot.Id), Is.EqualTo(2));
            Assert.That(await check.AccountAuditEvents.AnyAsync(e => e.UserId == current.UserId), Is.False);
        }

        Assert.That((await residents.ReclaimAsync(owner, tomorrow, tomorrow)).Succeeded, Is.True);
        await using var final = new D3ParkingDbContext(_options);
        Assert.That((await final.Reservations.SingleAsync(r => r.Id == current.Id)).SpotId, Is.EqualTo(spot.Id));
        Assert.That((await final.Reservations.SingleAsync(r => r.Id == next.Id)).SpotId, Is.Not.EqualTo(spot.Id));
        Assert.That((await final.SpotReleases.SingleAsync(r => r.SpotId == spot.Id)).Date, Is.EqualTo(Today));
    }

    [Test]
    public async Task A_booking_created_during_today_still_covers_the_whole_day_and_is_protected_immediately()
    {
        var (owner, spot, seeded) = await SeedAsync(Today, SpotReleaseSource.Manual, false);
        await using (var db = new D3ParkingDbContext(_options))
            await db.Reservations.Where(r => r.Id == seeded.Id).ExecuteDeleteAsync();
        var (start, end) = SiteTime.Day(Today, TimeZoneInfo.Utc);
        var result = await Reservations().ReserveAsync(seeded.UserId, spot.Id, start, end);
        Assert.That(result.Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        var saved = await check.Reservations.SingleAsync(r => r.SpotId == spot.Id);
        Assert.That(saved.StartUtc, Is.EqualTo(start));
        Assert.That(saved.EndUtc, Is.EqualTo(end));
        Assert.That((await Residents().ReclaimAsync(owner, Today, Today)).Errors,
            Does.Contain("Parking_Error_StartedReservationProtected"));
    }

    private ResidentSpotService Residents(IncentivePolicy? policy = null) => new(
        new TestDbContextFactory(_options), new FakeParkingSettings(policy ?? Policy), new FakeSiteSettings(),
        new FixedTimeProvider(Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>());

    private ReservationService Reservations() => new(
        new TestDbContextFactory(_options), new FakeParkingSettings(Policy), new FakeSiteSettings(),
        new FixedTimeProvider(Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>());

    private async Task<(Guid Owner, ParkingSpot Spot, Reservation Booking)> SeedAsync(
        DateOnly day, SpotReleaseSource source, bool replacement)
    {
        await using var db = new D3ParkingDbContext(_options);
        await db.ParkingSpots.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
        var owner = Guid.NewGuid();
        var code = owner.ToString("N")[..10];
        var spot = new ParkingSpot($"LOCK-{code}", ParkingSpotType.Standard);
        spot.AssignOwner(owner);
        db.ParkingSpots.Add(spot);
        db.ParkingSpotResidents.Add(new ParkingSpotResident(spot.Id, owner, Now.AddDays(-30)));
        if (replacement) db.ParkingSpots.Add(new ParkingSpot($"ALT-{code}", ParkingSpotType.Standard));
        var (start, end) = SiteTime.Day(day, TimeZoneInfo.Utc);
        var booking = new Reservation(spot.Id, Guid.NewGuid(), start, end, false, Now);
        db.Reservations.Add(booking);
        db.SpotReleases.Add(new SpotRelease(spot.Id, owner, day, Now.AddDays(-1), 0, source));
        await db.SaveChangesAsync();
        return (owner, spot, booking);
    }
}
