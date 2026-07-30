using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins down the resident's right of first refusal on their own spot: a released day can be taken
/// back (with the share reward returned) until a guest books it, a firm guest booking always
/// stands, and a mere waitlist hold yields — whether the resident reclaims, confirms arrival on an
/// auto-shared day, or books their own spot outright. Withdrawn waiters keep their queue position.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class ResidentPriorityTests
{
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    // The tests pick their own "now"; the shared FakeSiteSettings pins the site zone to UTC.
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);
    private static readonly DateTimeOffset BeforeCutoff = new(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterCutoff = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the resident priority tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_ResidentPriorityTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
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
    public async Task Reclaiming_a_released_day_restores_the_hold_and_returns_the_reward()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-01", owner, allowance: 5);
        var residents = CreateResidentService(now: BeforeCutoff);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        int awarded;
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            awarded = (await dbContext.SpotReleases.SingleAsync(r => r.SpotId == spot)).AwardedPoints;
            Assert.That(awarded, Is.GreaterThan(0), "The release must have earned a reward for the reclaim to return.");
        }

        var status = await residents.GetMyOwnedSpotAsync(owner);
        Assert.That(status!.UpcomingReleases, Is.EqualTo(new[] { new ReleasedDayDto(Tomorrow, false) }),
            "The released day must surface as reclaimable before anyone books it.");

        Assert.That((await residents.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            Assert.That(await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.False,
                "A reclaimed day is no longer shared — the spot is held for its resident again.");
            var reversal = await dbContext.PointsLedgerEntries
                .SingleAsync(e => e.UserId == owner && e.Reason == IncentiveReason.ResidentShareReclaimed);
            Assert.That(reversal.Points, Is.EqualTo(-awarded),
                "Taking the day back must return exactly the reward the release earned.");
        }
    }

    [Test]
    public async Task A_guest_booking_on_the_released_day_blocks_the_reclaim()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-02", owner, allowance: 5);
        var residents = CreateResidentService(now: BeforeCutoff);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (dayStart, dayEnd) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            dbContext.Reservations.Add(new Reservation(spot, Guid.NewGuid(), dayStart, dayEnd, false, BeforeCutoff));
            await dbContext.SaveChangesAsync();
        }

        var refused = await residents.ReclaimAsync(owner, Tomorrow, Tomorrow);
        Assert.That(refused.Errors, Does.Contain("Parking_Error_NothingToReclaim"),
            "A firm guest booking is never evicted — the day stays shared.");

        await using (var check = new D3ParkingDbContext(_options))
        {
            Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.True);
        }

        var status = await CreateResidentService(now: BeforeCutoff).GetMyOwnedSpotAsync(owner);
        Assert.That(status!.UpcomingReleases, Is.EqualTo(new[] { new ReleasedDayDto(Tomorrow, true) }),
            "The taken day must be flagged so the UI offers no false reclaim.");
    }

    [Test]
    public async Task Reclaim_withdraws_a_waitlist_hold_and_the_waiter_keeps_their_position()
    {
        var owner = Guid.NewGuid();
        var waiter = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-03", owner, allowance: 5);
        var joined = BeforeCutoff.AddHours(-2);
        Guid entryId;

        var residents = CreateResidentService(now: BeforeCutoff, notifications: out var sent);
        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (dayStart, dayEnd) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            var entry = new QueueEntry(waiter, dayStart.AddHours(8), dayEnd.AddHours(-8), joined);
            entry.Offer(spot, BeforeCutoff.AddMinutes(15));
            dbContext.QueueEntries.Add(entry);
            await dbContext.SaveChangesAsync();
            entryId = entry.Id;
        }

        Assert.That((await residents.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True,
            "A hold is a pending offer, not a booking — the resident's claim outranks it.");

        await using (var check = new D3ParkingDbContext(_options))
        {
            var entry = await check.QueueEntries.SingleAsync(q => q.Id == entryId);
            Assert.That(entry.Status, Is.EqualTo(QueueEntryStatus.Waiting));
            Assert.That(entry.OfferedSpotId, Is.Null);
            Assert.That(entry.CreatedAtUtc, Is.EqualTo(joined),
                "The waiter lost the hold through no fault of theirs; their queue position must survive.");
        }

        Assert.That(sent.Sent, Does.Contain((waiter, "Parking_Notify_QueueHoldReclaimed_Title")));
    }

    [Test]
    public async Task Confirming_arrival_on_an_auto_shared_day_withdraws_the_hold()
    {
        var owner = Guid.NewGuid();
        var waiter = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-04", owner, allowance: 0);
        Guid entryId;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var entry = new QueueEntry(waiter, AfterCutoff.AddMinutes(-30), AfterCutoff.AddHours(6), AfterCutoff.AddHours(-1));
            entry.Offer(spot, AfterCutoff.AddMinutes(15));
            dbContext.QueueEntries.Add(entry);
            await dbContext.SaveChangesAsync();
            entryId = entry.Id;
        }

        var residents = CreateResidentService(now: AfterCutoff, notifications: out var sent);
        Assert.That((await residents.ConfirmArrivalAsync(owner)).Succeeded, Is.True,
            "Past the cutoff the spot auto-shares, but until somebody books it the owner confirms it back.");

        await using (var check = new D3ParkingDbContext(_options))
        {
            Assert.That(await check.Reservations.AnyAsync(r => r.SpotId == spot && r.UserId == owner
                && r.Status == ReservationStatus.CheckedIn), Is.True);
            var entry = await check.QueueEntries.SingleAsync(q => q.Id == entryId);
            Assert.That(entry.Status, Is.EqualTo(QueueEntryStatus.Waiting));
        }

        Assert.That(sent.Sent, Does.Contain((waiter, "Parking_Notify_QueueHoldReclaimed_Title")));
    }

    [Test]
    public async Task An_owner_booking_their_own_spot_outranks_the_hold_that_would_stop_anyone_else()
    {
        var owner = Guid.NewGuid();
        var waiter = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-05", owner, allowance: 5);
        Guid entryId;
        DateTimeOffset windowStart, windowEnd;

        var residents = CreateResidentService(now: BeforeCutoff);
        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (dayStart, dayEnd) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            (windowStart, windowEnd) = (dayStart.AddHours(8), dayEnd.AddHours(-8));
            var entry = new QueueEntry(waiter, windowStart, windowEnd, BeforeCutoff.AddHours(-1));
            entry.Offer(spot, BeforeCutoff.AddMinutes(15));
            dbContext.QueueEntries.Add(entry);
            await dbContext.SaveChangesAsync();
            entryId = entry.Id;
        }

        var sent = new RecordingNotificationService();
        var reservations = new ReservationService(
            new TestDbContextFactory(_options), new FakeParkingSettings(), new FakeSiteSettings(),
            new FixedTimeProvider(BeforeCutoff), sent, new PassthroughLocalizer<ParkingMessages>());
        var result = await reservations.ReserveAsync(owner, spot, windowStart, windowEnd);
        Assert.That(result.Succeeded, Is.True,
            $"The owner must never see Parking_Error_SpotHeld on their own spot (got: {string.Join(";", result.Errors)}).");

        await using (var check = new D3ParkingDbContext(_options))
        {
            var entry = await check.QueueEntries.SingleAsync(q => q.Id == entryId);
            Assert.That(entry.Status, Is.EqualTo(QueueEntryStatus.Waiting));
            Assert.That(entry.OfferedSpotId, Is.Null);
        }

        Assert.That(sent.Sent, Does.Contain((waiter, "Parking_Notify_QueueHoldReclaimed_Title")));
    }

    private ResidentSpotService CreateResidentService(DateTimeOffset now) =>
        CreateResidentService(now, out _);

    private ResidentSpotService CreateResidentService(DateTimeOffset now, out RecordingNotificationService notifications)
    {
        notifications = new RecordingNotificationService();
        return new ResidentSpotService(
            new TestDbContextFactory(_options),
            new FakeParkingSettings(),
            new FakeSiteSettings(),
            new FixedTimeProvider(now),
            notifications,
            new PassthroughLocalizer<ParkingMessages>());
    }

    private async Task<Guid> CreateOwnedSpotAsync(string code, Guid ownerId, int allowance)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, ParkingSpotType.Standard);
        spot.AssignOwner(ownerId);
        spot.SetShareAllowance(allowance);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
