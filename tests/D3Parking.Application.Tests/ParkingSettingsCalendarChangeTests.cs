using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Domain.Common;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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

        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new ParkingSettingsService(
            new TestDbContextFactory(_options),
            _cache,
            new FakeSiteSettings(),
            new FixedTimeProvider(Now),
            NullLogger<ParkingSettingsService>.Instance);
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
            spot.Id, "Visitor", null, null, userId, start, end, residentId, Now);
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

        var refused = await _service.UpdateAsync(changed, Guid.NewGuid());
        Assert.That(refused.Succeeded, Is.False);

        await using (var db = new D3ParkingDbContext(_options))
        {
            Assert.That((await db.Reservations.FindAsync(reservation.Id))!.Status,
                Is.EqualTo(ReservationStatus.Reserved));
        }

        var confirmed = await _service.UpdateAsync(changed, Guid.NewGuid(), true);
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
            Assert.Multiple(() =>
            {
                Assert.That(savedReservation!.Status, Is.EqualTo(ReservationStatus.Cancelled));
                Assert.That(savedQueue!.Status, Is.EqualTo(QueueEntryStatus.Cancelled));
                Assert.That(savedHandoff!.Status, Is.EqualTo(ResidentSpotHandoffStatus.Cancelled));
                Assert.That(savedVisitor!.Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
                Assert.That(savedRelease, Is.Null);
                Assert.That(savedScore!.Credits, Is.EqualTo(7));
                Assert.That(refundCount, Is.EqualTo(1));
            });
        }
    }
}
