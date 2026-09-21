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
[NonParallelizable]
public class ResidentReclaimConflictTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);
    private static readonly IncentivePolicy Policy = new()
    {
        BaseReservationCost = 0,
        WeeklyReservationLimitEnabled = false,
        PublicHolidayReservationsAllowed = true,
    };
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
            Assert.Ignore("ConnectionStrings__SqlServer is required for resident reclaim conflict tests.");

        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_ReclaimConflictTests_{Guid.NewGuid():N}",
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

    [TestCase(ReservationTimeMode.AllDay)]
    [TestCase(ReservationTimeMode.TimeWindow)]
    public async Task Reclaim_requires_cancelling_the_alternative_and_cancel_restores_the_free_resident_day(
        ReservationTimeMode mode)
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        var policy = Policy with { ReservationTimeMode = mode };
        var (dayStart, dayEnd) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var start = mode == ReservationTimeMode.AllDay ? dayStart : dayStart.AddHours(8);
        var end = mode == ReservationTimeMode.AllDay ? dayEnd : dayStart.AddHours(17);
        var reservations = Reservations(policy);
        var residents = Residents(policy);
        var booked = await reservations.ReserveAsync(owner, alternative.Id, start, end);
        Assert.That(booked.Succeeded, Is.True);
        Assert.That(booked.ResidentSpotAutomaticallyReleased, Is.True);

        var result = await residents.ReclaimAsync(owner, Tomorrow, Tomorrow);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ResidentReclaimAlternativeReservation"));

        Guid reservationId;
        await using (var check = new D3ParkingDbContext(_options))
        {
            var reservation = await check.Reservations.SingleAsync(r => r.UserId == owner);
            reservationId = reservation.Id;
            Assert.That(reservation.Status, Is.EqualTo(ReservationStatus.Reserved));
            Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == own.Id && r.Date == Tomorrow), Is.True);
        }
        Assert.That((await residents.GetMyOwnedSpotAsync(owner))!.DaySchedule.Single(d => d.Date == Tomorrow).State,
            Is.EqualTo(D3Parking.Application.Parking.OwnedSpotDayState.SharedFree));

        var cancelled = await reservations.CancelAsync(owner, reservationId);
        Assert.That(cancelled.Succeeded, Is.True);
        Assert.That(cancelled.ResidentSpotAutomaticallyReturned, Is.True);
        Assert.That((await residents.GetMyOwnedSpotAsync(owner))!.DaySchedule.Single(d => d.Date == Tomorrow).State,
            Is.EqualTo(D3Parking.Application.Parking.OwnedSpotDayState.Held));
    }

    [TestCase(ReservationStatus.Reserved)]
    [TestCase(ReservationStatus.CheckedIn)]
    public async Task A_live_alternative_blocks_the_entire_reclaim_without_affecting_guests_or_queue_holds(
        ReservationStatus status)
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        var booking = new Reservation(alternative.Id, owner, Now.AddHours(-3), Now.AddHours(6), false, Now.AddDays(-1));
        if (status == ReservationStatus.CheckedIn) booking.CheckIn(Now.AddHours(-3));
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var guest = new Reservation(own.Id, Guid.NewGuid(), start, end, false, Now.AddDays(-1));
        var hold = new QueueEntry(Guid.NewGuid(), Now, Now.AddHours(4), Now.AddMinutes(-10));
        hold.Offer(own.Id, Now.AddMinutes(30));
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.Reservations.AddRange(booking, guest);
            db.QueueEntries.Add(hold);
            db.SpotReleases.AddRange(
                new SpotRelease(own.Id, owner, Today, Now.AddDays(-1), 0, SpotReleaseSource.Manual),
                new SpotRelease(own.Id, owner, Tomorrow, Now.AddDays(-1), 0, SpotReleaseSource.Manual));
            await db.SaveChangesAsync();
        }

        var result = await Residents().ReclaimAsync(owner, Today, Tomorrow);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ResidentReclaimAlternativeReservation"));
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.SpotReleases.CountAsync(r => r.SpotId == own.Id), Is.EqualTo(2));
        Assert.That((await check.Reservations.SingleAsync(r => r.Id == guest.Id)).SpotId, Is.EqualTo(own.Id));
        Assert.That((await check.Reservations.SingleAsync(r => r.Id == booking.Id)).Status, Is.EqualTo(status));
        Assert.That((await check.QueueEntries.SingleAsync(q => q.Id == hold.Id)).Status, Is.EqualTo(QueueEntryStatus.Offered));
    }

    [TestCase(ReservationStatus.Cancelled)]
    [TestCase(ReservationStatus.Released)]
    [TestCase(ReservationStatus.Completed)]
    [TestCase(ReservationStatus.NoShow)]
    public async Task An_inactive_alternative_does_not_block_reclaim(ReservationStatus status)
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var booking = new Reservation(alternative.Id, owner, start, end, false, Now.AddDays(-1));
        switch (status)
        {
            case ReservationStatus.Cancelled: booking.Cancel(Now); break;
            case ReservationStatus.Released: booking.Release(Now); break;
            case ReservationStatus.NoShow: booking.MarkNoShow(Now); break;
            case ReservationStatus.Completed:
                booking.CheckIn(Now.AddMinutes(-1));
                booking.Complete(Now);
                break;
        }
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.Reservations.Add(booking);
            db.SpotReleases.Add(new SpotRelease(own.Id, owner, Tomorrow, Now, 0, SpotReleaseSource.Manual));
            await db.SaveChangesAsync();
        }

        Assert.That((await Residents().ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == own.Id), Is.False);
    }

    [Test]
    public async Task Only_the_days_actually_returned_are_checked_for_alternative_bookings()
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        var (start, end) = SiteTime.Day(Today, TimeZoneInfo.Utc);
        var booking = new Reservation(alternative.Id, owner, start, end, false, Now.AddDays(-1));
        var handoff = ResidentSpotHandoff.CreateOffer(own.Id, owner, Guid.NewGuid(), start, end, Now.AddDays(-1), Now.AddHours(1));
        await using (var db = new D3ParkingDbContext(_options))
        {
            // Today is privately offered, not publicly released or accepted, so it is not part
            // of this return. The alternative ends exactly where tomorrow's reclaimed day starts.
            db.ResidentSpotHandoffs.Add(handoff);
            db.Reservations.Add(booking);
            db.SpotReleases.Add(new SpotRelease(own.Id, owner, Tomorrow, Now, 0, SpotReleaseSource.Manual));
            await db.SaveChangesAsync();
        }

        Assert.That((await Residents().ReclaimAsync(owner, Today, Tomorrow)).Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == own.Id), Is.False);
        Assert.That((await check.Reservations.SingleAsync(r => r.Id == booking.Id)).Status, Is.EqualTo(ReservationStatus.Reserved));
    }

    [Test]
    public async Task An_ended_alternative_does_not_block_returning_the_rest_of_today()
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.Reservations.Add(new Reservation(alternative.Id, owner, Now.AddHours(-3), Now, false, Now.AddDays(-1)));
            db.SpotReleases.Add(new SpotRelease(own.Id, owner, Today, Now.AddDays(-1), 0, SpotReleaseSource.Manual));
            await db.SaveChangesAsync();
        }
        Assert.That((await Residents().ReclaimAsync(owner, Today, Today)).Succeeded, Is.True);
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task An_alternative_also_blocks_reclaiming_an_accepted_private_handoff(int handoffDays)
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        var (start, _) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var end = start.AddDays(handoffDays);
        var recipient = Guid.NewGuid();
        var guest = new Reservation(own.Id, recipient, start, end, false, Now);
        var handoff = ResidentSpotHandoff.CreateOffer(own.Id, owner, recipient, start, end, Now, Now.AddHours(1));
        handoff.Accept(guest.Id, Now);
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.ResidentSpotHandoffs.Add(handoff);
            // Reclaiming even the first day moves the whole handoff, including its last day.
            db.Reservations.AddRange(guest, new Reservation(alternative.Id, owner, end.AddDays(-1), end, false, Now));
            await db.SaveChangesAsync();
        }

        var result = await Residents().ReclaimAsync(owner, Tomorrow, Tomorrow);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ResidentReclaimAlternativeReservation"));
        await using var check = new D3ParkingDbContext(_options);
        Assert.That((await check.Reservations.SingleAsync(r => r.Id == guest.Id)).SpotId, Is.EqualTo(own.Id));
    }

    [Test]
    public async Task A_reservation_on_the_same_physical_spot_does_not_block_reclaim()
    {
        var (owner, own, _) = await CreateSpotsAsync();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.Reservations.Add(new Reservation(own.Id, owner, start, end, false, Now));
            db.SpotReleases.Add(new SpotRelease(own.Id, owner, Tomorrow, Now, 0, SpotReleaseSource.Manual));
            await db.SaveChangesAsync();
        }
        Assert.That((await Residents().ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
    }

    [Test]
    public async Task Reclaim_racing_an_alternative_booking_cannot_leave_two_capacity_claims()
    {
        var (owner, own, alternative) = await CreateSpotsAsync();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.SpotReleases.Add(new SpotRelease(own.Id, owner, Tomorrow, Now, 0, SpotReleaseSource.Manual));
            await db.SaveChangesAsync();
        }
        var reclaim = Residents().ReclaimAsync(owner, Tomorrow, Tomorrow);
        var reserve = Reservations(Policy with { ReservationTimeMode = ReservationTimeMode.AllDay })
            .ReserveAsync(owner, alternative.Id, start, end);
        await Task.WhenAll(reclaim, reserve);
        Assert.That(reserve.Result.Succeeded, Is.True);

        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.Reservations.AnyAsync(r => r.UserId == owner && r.SpotId == alternative.Id
            && r.Status == ReservationStatus.Reserved), Is.True);
        Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == own.Id && r.Date == Tomorrow), Is.True,
            "Whichever transaction wins, the alternative reservation must leave the resident day released.");
    }

    private ReservationService Reservations(IncentivePolicy? policy = null) => new(
        new TestDbContextFactory(_options), new FakeParkingSettings(policy ?? Policy), new FakeSiteSettings(),
        new FixedTimeProvider(Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>());

    private ResidentSpotService Residents(IncentivePolicy? policy = null) => new(
        new TestDbContextFactory(_options), new FakeParkingSettings(policy ?? Policy), new FakeSiteSettings(),
        new FixedTimeProvider(Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>());

    private async Task<(Guid Owner, ParkingSpot Own, ParkingSpot Alternative)> CreateSpotsAsync()
    {
        var owner = Guid.NewGuid();
        var code = owner.ToString("N")[..10];
        var own = new ParkingSpot($"RC-{code}-A", ParkingSpotType.Standard);
        var alternative = new ParkingSpot($"RC-{code}-B", ParkingSpotType.Standard);
        own.AssignOwner(owner);
        await using var db = new D3ParkingDbContext(_options);
        db.ParkingSpots.AddRange(own, alternative);
        db.ParkingSpotResidents.Add(new ParkingSpotResident(own.Id, owner, Now.AddDays(-30)));
        await db.SaveChangesAsync();
        return (owner, own, alternative);
    }
}
