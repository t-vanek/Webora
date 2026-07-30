using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using D3Parking.Application.Parking;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the spot manager's dashboard: the tile state precedence (an arrived driver outranks a
/// booking, an inactive spot outranks everything), utilization that counts asphalt rather than
/// paper, and the two overrides — a full no-fault refund on cancel, and a move that re-points the
/// booking without touching the wallet. Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class LotDashboardTests
{
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    // 2026-09-15 is a Tuesday; the shared FakeSiteSettings pins the site zone to UTC.
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the dashboard tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_LotDashboardTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
    }

    [SetUp]
    public async Task ResetAsync()
    {
        // Every test asserts on lot-wide counts, so each needs the lot to itself.
        await using var dbContext = new D3ParkingDbContext(_options);
        dbContext.PointsLedgerEntries.RemoveRange(dbContext.PointsLedgerEntries);
        dbContext.AccountAuditEvents.RemoveRange(dbContext.AccountAuditEvents);
        dbContext.Reservations.RemoveRange(dbContext.Reservations);
        dbContext.SpotReleases.RemoveRange(dbContext.SpotReleases);
        dbContext.VisitorBookings.RemoveRange(dbContext.VisitorBookings);
        dbContext.ParkerScores.RemoveRange(dbContext.ParkerScores);
        dbContext.ParkingSpots.RemoveRange(dbContext.ParkingSpots);
        await dbContext.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task An_arrived_driver_outranks_a_booking_and_an_inactive_spot_outranks_both()
    {
        var free = await CreateSpotAsync("A-1");
        var booked = await CreateSpotAsync("A-2");
        var occupied = await CreateSpotAsync("A-3");
        var closed = await CreateSpotAsync("A-4", active: false);
        await BookAsync(booked, Guid.NewGuid());
        await BookAsync(occupied, Guid.NewGuid(), checkedIn: true);
        // A booking on the closed spot must not win over its being out of the lot.
        await BookAsync(closed, Guid.NewGuid(), checkedIn: true);

        var board = await CreateDashboard().GetBoardAsync(Today);
        var tiles = board.Spots.ToDictionary(t => t.Code, t => t.State);

        Assert.Multiple(() =>
        {
            Assert.That(tiles["A-1"], Is.EqualTo(SpotBoardState.Free));
            Assert.That(tiles["A-2"], Is.EqualTo(SpotBoardState.Booked));
            Assert.That(tiles["A-3"], Is.EqualTo(SpotBoardState.Occupied));
            Assert.That(tiles["A-4"], Is.EqualTo(SpotBoardState.Inactive),
                "A deactivated spot is out of the lot whatever is recorded against it.");
        });
    }

    [Test]
    public async Task A_resident_spot_reads_as_held_until_it_is_actually_shared()
    {
        var owner = Guid.NewGuid();
        var held = await CreateSpotAsync("B-1", ownerId: owner);
        var shared = await CreateSpotAsync("B-2", ownerId: Guid.NewGuid());
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var spot = await dbContext.ParkingSpots.FindAsync(shared);
            dbContext.SpotReleases.Add(new SpotRelease(shared, spot!.OwnerId!.Value, Today, Noon, 0));
            await dbContext.SaveChangesAsync();
        }

        // Before the hold cutoff, so auto-share cannot be what makes the difference.
        var morning = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
        var board = await CreateDashboard(morning).GetBoardAsync(Today);
        var tiles = board.Spots.ToDictionary(t => t.Code, t => t.State);

        Assert.That(tiles["B-1"], Is.EqualTo(SpotBoardState.ResidentHeld));
        Assert.That(tiles["B-2"], Is.EqualTo(SpotBoardState.ResidentShared));
        Assert.That(board.Overview.ResidentSpots, Is.EqualTo(2));
        Assert.That(board.Overview.OccupancyPercent, Is.Zero,
            "A held resident spot is neither taken nor offerable, so it must not read as a full lot.");
        _ = held;
    }

    [Test]
    public async Task Rows_carry_their_section_and_land_ordered_by_section_then_by_number()
    {
        await CreateSpotAsync("P2-10");
        await CreateSpotAsync("P2-2");
        await CreateSpotAsync("A-1");
        await CreateSpotAsync("7");

        var board = await CreateDashboard().GetBoardAsync(Today);

        Assert.That(board.Spots.Select(s => s.Code), Is.EqualTo(new[] { "7", "A-1", "P2-2", "P2-10" }),
            "Sectionless codes first, then by section, and inside a section codes read as numbers.");
        Assert.That(board.Spots.Single(s => s.Code == "P2-10").Section, Is.EqualTo("P2"));
        Assert.That(board.Spots.Single(s => s.Code == "7").Section, Is.Empty,
            "A code with no letter prefix has no section rather than being a section of its own.");
    }

    [Test]
    public async Task The_summary_trends_are_a_fixed_window_with_a_point_for_every_day()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateSpotAsync("M-1", ownerId: owner);
        await CreateSpotAsync("M-2");
        await BookAsync(spot, Guid.NewGuid(), day: Today.AddDays(-2), status: ReservationStatus.Completed);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            // Shared and taken, then shared and nobody came.
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(-2), Noon, 0));
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(-1), Noon, 0));
            await dbContext.SaveChangesAsync();
        }

        var trends = await CreateDashboard().GetSummaryTrendsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(trends.Occupancy, Has.Count.EqualTo(LotBoard.SummaryTrendDays),
                "A sparkline needs a point per day, busy or not.");
            Assert.That(trends.Mismatches, Has.Count.EqualTo(LotBoard.SummaryTrendDays));
            Assert.That(trends.WastedShares, Has.Count.EqualTo(LotBoard.SummaryTrendDays));
            Assert.That(trends.Occupancy[^1].Date, Is.EqualTo(Today), "Oldest first, so the last point is today.");
        });

        Assert.That(trends.Occupancy.Single(d => d.Date == Today.AddDays(-2)).Occupied, Is.EqualTo(1));
        Assert.That(trends.Occupancy.Single(d => d.Date == Today.AddDays(-1)).Occupied, Is.Zero);
        Assert.That(trends.WastedShares.Where(d => d.Count > 0).Select(d => d.Date),
            Is.EqualTo(new[] { Today.AddDays(-1) }),
            "Only the shared day nobody took is waste; the one that was taken worked.");
    }

    [Test]
    public async Task Utilization_counts_days_that_were_honoured_not_days_that_were_merely_booked()
    {
        var spot = await CreateSpotAsync("C-1");
        var user = Guid.NewGuid();
        // Three days booked, but only one of them was ever stood on.
        await BookAsync(spot, user, day: Today.AddDays(-3), status: ReservationStatus.Completed);
        await BookAsync(spot, user, day: Today.AddDays(-2), status: ReservationStatus.NoShow);
        await BookAsync(spot, user, day: Today.AddDays(-1), status: ReservationStatus.Cancelled);

        var analytics = await CreateDashboard().GetAnalyticsAsync(Today.AddDays(-4), Today);
        var stats = analytics.Spots.Single(s => s.Code == "C-1");

        Assert.That(stats.WindowDays, Is.EqualTo(5));
        Assert.That(stats.BookedDays, Is.EqualTo(1),
            "A no-show occupied the spot only on paper and a cancel never occupied it at all.");
        Assert.That(stats.UtilizationPercent, Is.EqualTo(20));
        Assert.That(stats.NoShows, Is.EqualTo(1));
    }

    [Test]
    public async Task A_shared_day_nobody_booked_counts_as_wasted_only_once_it_has_passed()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateSpotAsync("D-1", ownerId: owner);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            // Past and unclaimed, past and taken, and still ahead of us.
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(-2), Noon, 0));
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(-1), Noon, 0));
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(3), Noon, 0));
            await dbContext.SaveChangesAsync();
        }

        await BookAsync(spot, Guid.NewGuid(), day: Today.AddDays(-1), status: ReservationStatus.Completed);

        var analytics = await CreateDashboard().GetAnalyticsAsync(Today.AddDays(-5), Today.AddDays(5));
        var stats = analytics.Spots.Single(s => s.Code == "D-1");

        Assert.That(stats.SharedDays, Is.EqualTo(3));
        Assert.That(stats.UnusedSharedDays, Is.EqualTo(1),
            "Only the past day nobody took is waste: the taken one worked and the future one is still an offer.");
    }

    [Test]
    public async Task Demand_is_measured_in_occupied_hours_not_in_bookings()
    {
        var spot = await CreateSpotAsync("E-1");
        // One booking, 09:00–12:00 on a Monday: three hours of pressure, not one.
        var monday = new DateOnly(2026, 9, 14);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            dbContext.Reservations.Add(new Reservation(spot, Guid.NewGuid(),
                new DateTimeOffset(monday.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
                new DateTimeOffset(monday.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero),
                false, Noon));
            await dbContext.SaveChangesAsync();
        }

        var analytics = await CreateDashboard().GetAnalyticsAsync(monday, Today);

        Assert.That(analytics.Demand.Where(c => c.DayOfWeek == DayOfWeek.Monday).Select(c => c.Hour),
            Is.EqualTo(new[] { 9, 10, 11 }),
            "The hour a booking ends is not occupied by it.");
        Assert.That(analytics.PeakDayOfWeek, Is.EqualTo(DayOfWeek.Monday));
    }

    [Test]
    public async Task Cancelling_as_the_manager_refunds_in_full_however_late_it_is()
    {
        var spot = await CreateSpotAsync("F-1");
        var holder = Guid.NewGuid();
        var admin = Guid.NewGuid();
        // Starting in ten minutes: a holder cancelling this late would forfeit the charge.
        var reservationId = await BookAsync(spot, holder, startUtc: Noon.AddMinutes(10), credits: 25);

        var result = await CreateDashboard().CancelReservationAsync(reservationId, admin);
        Assert.That(result.Succeeded, Is.True);

        await using var dbContext = new D3ParkingDbContext(_options);
        var reservation = await dbContext.Reservations.FindAsync(reservationId);
        Assert.That(reservation!.Status, Is.EqualTo(ReservationStatus.Cancelled));
        var score = await dbContext.ParkerScores.FindAsync(holder);
        Assert.That(score!.Credits, Is.EqualTo(25),
            "The lot was wrong, not the driver, so the whole charge comes back regardless of timing.");
        Assert.That(await dbContext.AccountAuditEvents.AnyAsync(e => e.UserId == holder
            && e.Actor == $"admin:{admin}"), Is.True, "An override must leave an audit trail on the holder.");
    }

    [Test]
    public async Task A_checked_in_booking_cannot_be_cancelled_but_can_be_moved()
    {
        var from = await CreateSpotAsync("G-1");
        var to = await CreateSpotAsync("G-2");
        var holder = Guid.NewGuid();
        var reservationId = await BookAsync(from, holder, checkedIn: true, credits: 15);
        var dashboard = CreateDashboard();

        var cancel = await dashboard.CancelReservationAsync(reservationId, Guid.NewGuid());
        Assert.That(cancel.Succeeded, Is.False,
            "Cancelling a booking someone already arrived on is not a legal lifecycle move.");
        Assert.That(cancel.Errors, Is.EqualTo(new[] { "Parking_Error_InvalidState" }));

        Assert.That((await dashboard.MoveReservationAsync(reservationId, to, Guid.NewGuid())).Succeeded, Is.True);

        await using var dbContext = new D3ParkingDbContext(_options);
        var moved = await dbContext.Reservations.FindAsync(reservationId);
        Assert.Multiple(() =>
        {
            Assert.That(moved!.SpotId, Is.EqualTo(to));
            Assert.That(moved.Status, Is.EqualTo(ReservationStatus.CheckedIn), "A move keeps the check-in.");
            Assert.That(moved.CreditsCharged, Is.EqualTo(15), "A move carries the price with it.");
        });
        Assert.That(await dbContext.ParkerScores.FindAsync(holder), Is.Null,
            "Nothing moves in the wallet: the booking is re-pointed, not refunded and re-charged.");
    }

    [Test]
    public async Task A_booking_cannot_be_moved_onto_an_occupied_or_inactive_spot()
    {
        var from = await CreateSpotAsync("H-1");
        var taken = await CreateSpotAsync("H-2");
        var closed = await CreateSpotAsync("H-3", active: false);
        var reservationId = await BookAsync(from, Guid.NewGuid());
        await BookAsync(taken, Guid.NewGuid());
        var dashboard = CreateDashboard();

        Assert.That((await dashboard.MoveReservationAsync(reservationId, taken, Guid.NewGuid())).Errors,
            Is.EqualTo(new[] { "Parking_Error_SpotTaken" }));
        Assert.That((await dashboard.MoveReservationAsync(reservationId, closed, Guid.NewGuid())).Errors,
            Is.EqualTo(new[] { "Parking_Error_SpotInactive" }));
        Assert.That((await dashboard.MoveReservationAsync(reservationId, from, Guid.NewGuid())).Errors,
            Is.EqualTo(new[] { "Parking_Error_SameSpot" }));

        var targets = await dashboard.GetMoveTargetsAsync(reservationId);
        Assert.That(targets, Is.Empty, "Neither a taken nor a closed spot is offered as a target.");
    }

    [Test]
    public async Task Move_targets_read_as_numbers_not_as_strings()
    {
        var from = await CreateSpotAsync("J-1");
        await CreateSpotAsync("J-10");
        await CreateSpotAsync("J-2");
        var reservationId = await BookAsync(from, Guid.NewGuid());

        var targets = await CreateDashboard().GetMoveTargetsAsync(reservationId);

        Assert.That(targets.Select(t => t.Code), Is.EqualTo(new[] { "J-2", "J-10" }),
            "A database string sort would offer J-10 first; the manager reads codes as numbers.");
    }

    [Test]
    public async Task The_spot_detail_lists_bookings_visitors_and_unclaimed_shared_days()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateSpotAsync("I-1", ownerId: owner);
        await BookAsync(spot, Guid.NewGuid(), day: Today.AddDays(1));
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            // Two shared days, one of which the booking above already covers.
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(1), Noon, 0));
            dbContext.SpotReleases.Add(new SpotRelease(spot, owner, Today.AddDays(2), Noon, 0));
            await dbContext.SaveChangesAsync();
        }

        var detail = await CreateDashboard().GetSpotDetailAsync(spot, Today, 7);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Calendar.Count(e => e.Kind == SpotCalendarKind.Reservation), Is.EqualTo(1));
        Assert.That(detail.Calendar.Where(e => e.Kind == SpotCalendarKind.ResidentRelease).Select(e => e.Date),
            Is.EqualTo(new[] { Today.AddDays(2) }),
            "A shared day someone booked is shown as that booking, not twice.");
        Assert.That(detail.Calendar.Single(e => e.Kind == SpotCalendarKind.Reservation).CanCancel, Is.True);
    }

    [Test]
    public async Task A_visitor_spot_with_a_reception_booking_shows_the_guest()
    {
        var spot = await CreateVisitorSpotAsync("V-1");
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            dbContext.VisitorBookings.Add(new VisitorBooking(spot, "Jana Dvořáková", "Acme", null, null,
                new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
                new DateTimeOffset(Today.ToDateTime(new TimeOnly(11, 0)), TimeSpan.Zero),
                Guid.NewGuid(), Noon));
            await dbContext.SaveChangesAsync();
        }

        var board = await CreateDashboard().GetBoardAsync(Today);
        var tile = board.Spots.Single(t => t.Code == "V-1");

        Assert.That(tile.State, Is.EqualTo(SpotBoardState.VisitorBooked));
        Assert.That(tile.HolderName, Is.EqualTo("Jana Dvořáková"),
            "The manager must see who the spot is held for without opening another screen.");
        Assert.That(board.Overview.VisitorBookings, Is.EqualTo(1));

        // A visitor spot is never a move target: it lives outside the employee pool entirely.
        var elsewhere = await CreateSpotAsync("V-9");
        var reservationId = await BookAsync(elsewhere, Guid.NewGuid(), day: Today.AddDays(1));
        Assert.That((await CreateDashboard().GetMoveTargetsAsync(reservationId)).Select(t => t.Code),
            Does.Not.Contain("V-1"));
    }

    [Test]
    public async Task The_daily_curve_has_one_point_per_day_counting_only_honoured_bookings()
    {
        var a = await CreateSpotAsync("K-1");
        var b = await CreateSpotAsync("K-2");
        var user = Guid.NewGuid();
        // Two spots taken the same day, one taken the next, and a no-show that occupied nothing.
        await BookAsync(a, user, day: Today.AddDays(-2), status: ReservationStatus.Completed);
        await BookAsync(b, user, day: Today.AddDays(-2), status: ReservationStatus.Completed);
        await BookAsync(a, user, day: Today.AddDays(-1), status: ReservationStatus.Completed);
        await BookAsync(b, user, day: Today.AddDays(-1), status: ReservationStatus.NoShow);

        var analytics = await CreateDashboard().GetAnalyticsAsync(Today.AddDays(-2), Today);

        Assert.That(analytics.Daily.Select(d => d.Date),
            Is.EqualTo(new[] { Today.AddDays(-2), Today.AddDays(-1), Today }),
            "One point per day of the window, oldest first — a chart cannot draw gaps it never got.");
        Assert.That(analytics.Daily.Select(d => d.Occupied), Is.EqualTo(new[] { 2, 1, 0 }));
        Assert.That(analytics.Daily[0].Capacity, Is.EqualTo(2), "Capacity is today's bookable count.");
        Assert.That(analytics.Daily[0].OccupancyPercent, Is.EqualTo(100));
    }

    [Test]
    public async Task A_spots_trend_covers_every_day_of_the_window_busy_or_not()
    {
        var spot = await CreateSpotAsync("L-1");
        await BookAsync(spot, Guid.NewGuid(), day: Today.AddDays(-1), status: ReservationStatus.Completed);

        var detail = await CreateDashboard().GetSpotDetailAsync(spot, Today, 7);

        Assert.That(detail!.Trend, Has.Count.EqualTo(30), "A 30-day strip needs 30 columns.");
        Assert.That(detail.Trend[^1].Date, Is.EqualTo(Today), "Oldest first, so the last point is today.");
        Assert.That(detail.Trend.Where(d => d.Busy).Select(d => d.Date),
            Is.EqualTo(new[] { Today.AddDays(-1) }));
    }

    [Test]
    public async Task A_missing_spot_has_no_detail()
    {
        Assert.That(await CreateDashboard().GetSpotDetailAsync(Guid.NewGuid(), Today, 7), Is.Null);
    }

    // A fresh cache per instance, so one test's cached aggregate can never answer another's question.
    private LotDashboardService CreateDashboard(DateTimeOffset? now = null) =>
        new(new TestDbContextFactory(_options),
            new FakeParkingSettings(),
            new FakeSiteSettings(),
            new FixedTimeProvider(now ?? Noon),
            new NullNotificationService(),
            new MemoryCache(new MemoryCacheOptions()),
            new PassthroughLocalizer<ParkingMessages>());

    private async Task<Guid> CreateSpotAsync(string code, bool active = true, Guid? ownerId = null,
        ParkingSpotType type = ParkingSpotType.Standard)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, type);
        if (ownerId is not null)
        {
            spot.AssignOwner(ownerId);
        }

        if (!active)
        {
            spot.Deactivate();
        }

        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }

    private async Task<Guid> CreateVisitorSpotAsync(string code) =>
        await CreateSpotAsync(code, type: ParkingSpotType.Visitor);

    /// <summary>An 8–17 booking on the given local day (today by default), in whatever state.</summary>
    private async Task<Guid> BookAsync(Guid spotId, Guid userId, DateOnly? day = null, bool checkedIn = false,
        ReservationStatus? status = null, DateTimeOffset? startUtc = null, int credits = 0)
    {
        var date = day ?? Today;
        var start = startUtc ?? new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        var end = new DateTimeOffset(date.ToDateTime(new TimeOnly(17, 0)), TimeSpan.Zero);

        await using var dbContext = new D3ParkingDbContext(_options);
        var reservation = new Reservation(spotId, userId, start, end < start ? start.AddHours(1) : end, false, Noon, credits);
        if (checkedIn || status == ReservationStatus.Completed)
        {
            reservation.CheckIn(Noon);
        }

        switch (status)
        {
            case ReservationStatus.Completed:
                reservation.Complete(Noon);
                break;
            case ReservationStatus.NoShow:
                reservation.MarkNoShow();
                break;
            case ReservationStatus.Cancelled:
                reservation.Cancel();
                break;
        }

        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        return reservation.Id;
    }
}
