using D3Parking.Domain.Accounts;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Domain.Common;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ParkingSettingsCalendarChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private ParkingSettingsService _service = null!;
    private MemoryCache _cache = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the settings test needs SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_SettingsCalendarTests",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new ParkingSettingsService(
            new TestDbContextFactory(_options),
            _cache,
            new FakeSiteSettings(),
            new FixedTimeProvider(Now),
            NullLogger<ParkingSettingsService>.Instance);
    }

    [SetUp]
    public async Task ResetDatabaseAsync()
    {
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        _cache.Clear();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        _cache?.Dispose();
        if (_options is not null)
        {
            await using var db = new D3ParkingDbContext(_options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Restricting_a_weekday_requires_confirmation_and_atomically_invalidates_future_records()
    {
        var current = await _service.GetAsync();
        var saturday = new DateOnly(2026, 8, 29);
        var start = SiteTime.At(saturday, new TimeOnly(9, 0), TimeZoneInfo.Utc);
        var end = SiteTime.At(saturday, new TimeOnly(10, 0), TimeZoneInfo.Utc);
        var userId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var spot = new ParkingSpot("CFG-01", ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, start, end, false, Now, creditsCharged: 7);
        var queue = new QueueEntry(userId, start, end, Now);
        var handoff = ResidentSpotHandoff.CreateOffer(
            spot.Id, residentId, userId, start, end, Now, Now.AddDays(1));
        var visitor = new VisitorBooking(
            spot.Id, "Synthetic visitor", "Synthetic company", "SYN0001", userId, start, end, residentId, Now);
        var release = new SpotRelease(spot.Id, residentId, saturday, Now, 0);

        await using (var db = new D3ParkingDbContext(_options))
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
            db.QueueEntries.Add(queue);
            db.ResidentSpotHandoffs.Add(handoff);
            db.VisitorBookings.Add(visitor);
            db.SpotReleases.Add(release);
            await db.SaveChangesAsync();
        }

        var changed = current with
        {
            ReservationTimeMode = ReservationTimeMode.TimeWindow,
            AllowedReservationWeekdays = Weekday.Workdays,
        };
        var impact = await _service.GetCalendarChangeImpactAsync(changed);

        Assert.Multiple(() =>
        {
            Assert.That(impact.Reservations, Is.EqualTo(1));
            Assert.That(impact.QueueEntries, Is.EqualTo(1));
            Assert.That(impact.Handoffs, Is.EqualTo(1));
            Assert.That(impact.VisitorBookings, Is.EqualTo(1));
            Assert.That(impact.SpotReleases, Is.EqualTo(1));
        });

        var actingUserId = Guid.NewGuid();
        var refused = await _service.UpdateAsync(changed, actingUserId);
        Assert.That(refused.Succeeded, Is.False);

        await using (var db = new D3ParkingDbContext(_options))
        {
            Assert.That((await db.Reservations.FindAsync(reservation.Id))!.Status,
                Is.EqualTo(ReservationStatus.Reserved));
            Assert.That((await db.VisitorBookings.FindAsync(visitor.Id))!.Status,
                Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That((await db.ParkingSettings.SingleAsync()).AllowedReservationWeekdays,
                Is.EqualTo(current.AllowedReservationWeekdays));
            Assert.That(await db.AccountAuditEvents.CountAsync(), Is.Zero,
                "An unconfirmed change must not leave a settings or cancellation audit.");
        }

        var confirmed = await _service.UpdateAsync(changed, actingUserId, true);
        Assert.That(confirmed.Succeeded, Is.True);

        await using (var db = new D3ParkingDbContext(_options))
        {
            var savedReservation = await db.Reservations.FindAsync(reservation.Id);
            var savedQueue = await db.QueueEntries.FindAsync(queue.Id);
            var savedHandoff = await db.ResidentSpotHandoffs.FindAsync(handoff.Id);
            var savedVisitor = await db.VisitorBookings.FindAsync(visitor.Id);
            var savedRelease = await db.SpotReleases.FindAsync(release.Id);
            var savedScore = await db.ParkerScores.FindAsync(userId);
            var refundCount = await db.PointsLedgerEntries.CountAsync(e =>
                e.ReservationId == reservation.Id && e.Reason == IncentiveReason.ReservationRefund);
            var visitorAudits = await db.AccountAuditEvents
                .Where(e => e.Type == AccountAuditEventType.ReservationOverridden)
                .ToListAsync();
            var settingsAudits = await db.AccountAuditEvents
                .Where(e => e.Type == AccountAuditEventType.SettingsChanged)
                .ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(savedReservation!.Status, Is.EqualTo(ReservationStatus.Cancelled));
                Assert.That(savedQueue!.Status, Is.EqualTo(QueueEntryStatus.Cancelled));
                Assert.That(savedHandoff!.Status, Is.EqualTo(ResidentSpotHandoffStatus.Cancelled));
                Assert.That(savedVisitor!.Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
                Assert.That(savedRelease, Is.Null);
                Assert.That(savedScore!.Credits, Is.EqualTo(7));
                Assert.That(refundCount, Is.EqualTo(1));
                Assert.That(visitorAudits, Has.Count.EqualTo(1));
                Assert.That(settingsAudits, Has.Count.EqualTo(1));
            });

            var audit = visitorAudits.Single();
            Assert.Multiple(() =>
            {
                Assert.That(audit.UserId, Is.EqualTo(actingUserId));
                Assert.That(audit.Actor, Is.EqualTo($"admin:{actingUserId}"));
                Assert.That(audit.OccurredAtUtc, Is.EqualTo(Now));
                Assert.That(audit.Detail, Is.EqualTo(
                    $"Visitor booking {visitor.Id}: cancelled; spot={spot.Id}; start={start:O}; end={end:O}; reason=calendar configuration change."));
                Assert.That(audit.Detail, Does.Not.Contain(visitor.VisitorName));
                Assert.That(audit.Detail, Does.Not.Contain(visitor.Company));
                Assert.That(audit.Detail, Does.Not.Contain(visitor.LicensePlate));
                Assert.That(settingsAudits.Single().Actor, Is.EqualTo($"admin:{actingUserId}"));
            });
        }
    }

    [Test]
    public async Task Failure_after_saving_before_commit_rolls_back_settings_visitor_and_audits()
    {
        var current = await _service.GetAsync();
        var saturday = new DateOnly(2026, 8, 29);
        var start = SiteTime.At(saturday, TimeOnly.MinValue, TimeZoneInfo.Utc);
        var end = SiteTime.At(saturday.AddDays(1), TimeOnly.MinValue, TimeZoneInfo.Utc);
        var actorId = Guid.NewGuid();
        var spot = new ParkingSpot("CFG-ROLLBACK", ParkingSpotType.Visitor);
        var visitor = new VisitorBooking(
            spot.Id, "Synthetic visitor", null, null, null, start, end, actorId, Now);
        await using (var db = new D3ParkingDbContext(_options))
        {
            db.ParkingSpots.Add(spot);
            db.VisitorBookings.Add(visitor);
            await db.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<D3ParkingDbContext>(_options)
            .AddInterceptors(new FailAfterSave())
            .Options;
        var failingService = new ParkingSettingsService(
            new TestDbContextFactory(failingOptions), _cache, new FakeSiteSettings(),
            new FixedTimeProvider(Now), NullLogger<ParkingSettingsService>.Instance);
        var changed = current with { AllowedReservationWeekdays = Weekday.Workdays };
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failingService.UpdateAsync(changed, actorId, true));

        await using var check = new D3ParkingDbContext(_options);
        Assert.That((await check.VisitorBookings.FindAsync(visitor.Id))!.Status,
            Is.EqualTo(VisitorBookingStatus.Booked));
        Assert.That((await check.ParkingSettings.SingleAsync()).AllowedReservationWeekdays,
            Is.EqualTo(current.AllowedReservationWeekdays));
        Assert.That(await check.AccountAuditEvents.CountAsync(), Is.Zero,
            "Neither audit may survive a rolled back configuration change.");
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic failure after saving before commit.");
    }
}
