using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Accounts;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Races two real service calls against SQL Server and asserts the money/state invariants the
/// rowversion tokens and serializable transactions exist to protect: one refund per charge, one
/// reward package per day, one active reservation per spot, no penalty for a driver who checked
/// in. Each test seeds its own users/spots, so a single shared database serves the fixture.
/// Requires the SQL Server from ConnectionStrings__SqlServer (the suite is skipped without it);
/// a dedicated database is created and dropped per run.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReservationConcurrencyTests
{
    // Fixed instant, late enough in the local (UTC) day that the resident auto-share cutoff
    // (08:00 hold + 30 min grace by default) has safely passed.
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private TestDbContextFactory _factory = null!;
    private ReservationService _reservations = null!;
    private ResidentSpotService _residentSpots = null!;
    private IncentivePolicy _policy = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the concurrency tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_ConcurrencyTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        _factory = new TestDbContextFactory(_options);
        // This fixture also exercises the legacy optional release reward. Planner defaults keep it
        // off, so the test opts in explicitly instead of depending on a punitive production default.
        _policy = IncentivePolicy.Default with
        {
            ReleasePoints = 10,
            MaxReleaseReward = 40,
            MaxRewardedReleasesPerDay = 2,
        };
        var parkingSettings = new FixedParkingSettings(_policy);
        var siteSettings = new FakeSiteSettings();
        var time = new FixedTimeProvider(Now);
        var notifications = new NullNotificationService();
        var messages = new PassthroughLocalizer<ParkingMessages>();

        _reservations = new ReservationService(_factory, parkingSettings, siteSettings, time, notifications, messages);
        _residentSpots = new ResidentSpotService(_factory, parkingSettings, siteSettings, time, notifications, messages);
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
    public async Task Concurrent_cancels_refund_exactly_once()
    {
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot("C-01", ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, Now.AddHours(4), Now.AddHours(8), false, Now, creditsCharged: 10);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
        });

        var results = await WhenAllTolerant(
            () => _reservations.CancelAsync(userId, reservation.Id),
            () => _reservations.CancelAsync(userId, reservation.Id));

        Assert.That(results.Count(r => r is { Succeeded: true }), Is.EqualTo(1),
            "Exactly one of the two cancels may report success.");

        await using var db = new D3ParkingDbContext(_options);
        var refunds = await db.PointsLedgerEntries
            .CountAsync(e => e.UserId == userId && e.Reason == IncentiveReason.ReservationRefund);
        var credits = await db.ParkerScores.Where(s => s.UserId == userId).Select(s => s.Credits).SingleAsync();
        Assert.That(refunds, Is.EqualTo(1), "A double-click must not write two refund ledger rows.");
        Assert.That(credits, Is.EqualTo(10), "The wallet must be credited the charge exactly once.");
    }

    [Test]
    public async Task Concurrent_releases_refund_and_reward_exactly_once()
    {
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot("R-01", ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, Now.AddHours(4), Now.AddHours(8), false, Now, creditsCharged: 10);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
        });

        var results = await WhenAllTolerant(
            () => _reservations.ReleaseAsync(userId, reservation.Id),
            () => _reservations.ReleaseAsync(userId, reservation.Id));

        Assert.That(results.Count(r => r is { Succeeded: true }), Is.EqualTo(1));

        await using var db = new D3ParkingDbContext(_options);
        var refunds = await db.PointsLedgerEntries
            .CountAsync(e => e.UserId == userId && e.Reason == IncentiveReason.ReservationRefund);
        var rewards = await db.PointsLedgerEntries
            .CountAsync(e => e.UserId == userId && e.Reason == IncentiveReason.ReleasedReservation);
        Assert.That(refunds, Is.EqualTo(1));
        Assert.That(rewards, Is.EqualTo(1), "The release reward must not be collected twice for one reservation.");
    }

    [Test]
    public async Task A_started_plan_stays_reserved_without_presence_penalties()
    {
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot("S-01", ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, Now.AddHours(-1), Now.AddHours(2), false, Now.AddHours(-2));
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
        });

        await _reservations.SendDueRemindersAsync();

        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync(),
            Is.EqualTo(ReservationStatus.Reserved));
        Assert.That(await db.PointsLedgerEntries.CountAsync(e => e.UserId == userId
            && (e.Reason == IncentiveReason.NoShowPenalty || e.Reason == IncentiveReason.QueueNoShowPenalty)), Is.Zero);
    }

    [Test]
    public async Task Concurrent_queue_joins_leave_a_single_active_entry()
    {
        // The waitlist only opens when nothing is bookable, so spots left behind by the other
        // tests (the fixture shares one database) must be out of the pool first.
        await using (var setup = new D3ParkingDbContext(_options))
        {
            await setup.ParkingSpots.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        }

        // The same user double-clicks Join for one window.
        var userId = Guid.NewGuid();

        var results = await WhenAllTolerant(
            () => _reservations.JoinQueueAsync(userId, Now.AddHours(1), Now.AddHours(3)),
            () => _reservations.JoinQueueAsync(userId, Now.AddHours(1), Now.AddHours(3)));

        await using var db = new D3ParkingDbContext(_options);
        var active = await db.QueueEntries.CountAsync(q => q.UserId == userId
            && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered));

        Assert.That(active, Is.EqualTo(1), "A double-click must not enqueue the user twice for one window.");
        Assert.That(results.Count(r => r is { Succeeded: true }), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public async Task Advance_plans_obey_the_weekly_limit_but_last_minute_capacity_stays_open()
    {
        var userId = Guid.NewGuid();
        var spots = new[]
        {
            new ParkingSpot($"WL-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard),
            new ParkingSpot($"WL-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard),
            new ParkingSpot($"WL-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard),
        };
        await SeedAsync(db => db.ParkingSpots.AddRange(spots));

        var friday = new DateTimeOffset(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);
        var saturday = friday.AddDays(1);
        var sunday = saturday.AddDays(1);

        Assert.That((await _reservations.ReserveAsync(userId, spots[0].Id, friday, friday.AddHours(8))).Succeeded, Is.True);
        Assert.That((await _reservations.ReserveAsync(userId, spots[1].Id, saturday, saturday.AddHours(8))).Succeeded, Is.True);

        var advance = await _reservations.ReserveAsync(userId, spots[2].Id, sunday, sunday.AddHours(8));
        Assert.That(advance.Succeeded, Is.False);
        Assert.That(advance.Errors, Does.Contain("Parking_Error_WeeklyReservationLimit"));

        var closeInService = new ReservationService(
            _factory, new FixedParkingSettings(_policy), new FakeSiteSettings(),
            new FixedTimeProvider(saturday.AddHours(4)), new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());
        Assert.That((await closeInService.ReserveAsync(userId, spots[2].Id, sunday, sunday.AddHours(8))).Succeeded,
            Is.True, "Unused Sunday capacity is inside the 24-hour last-minute window.");
    }

    private async Task SeedAsync(Action<D3ParkingDbContext> seed)
    {
        await using var db = new D3ParkingDbContext(_options);
        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs both operations concurrently. Serializable transactions may kill one side as a
    /// deadlock victim — that surfaces as an exception and counts as "did not succeed"; the
    /// invariants each test asserts afterwards must hold either way.
    /// </summary>
    private static async Task<IReadOnlyList<ParkingResult?>> WhenAllTolerant(params Func<Task<ParkingResult>>[] operations)
    {
        var tasks = operations.Select(async op =>
        {
            try
            {
                return await op();
            }
            catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is SqlException)
            {
                return null;
            }
        });
        return await Task.WhenAll(tasks);
    }



    private sealed class FixedParkingSettings(IncentivePolicy policy) : IParkingSettingsService
    {
        public Task<IncentivePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(policy);

        public Task<TimeSpan> GetSweepIntervalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GeoPoint?> GetLotLocationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ParkingSettingsDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlannerCapacityDto> GetPlannerCapacityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlannerCapacityDto(0, 0, 0, 0, 0));

        public Task<ParkingResult> UpdateAsync(ParkingSettingsDto settings, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AdaptPeakSurchargeAsync(double measuredOccupancy, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ParkingMapImageDto?> GetOrientationMapAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ParkingMapImageDto?>(null);

        public Task<ParkingResult> SetOrientationMapAsync(byte[] content, Guid actingUserId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ParkingResult> ClearOrientationMapAsync(Guid actingUserId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }



}
