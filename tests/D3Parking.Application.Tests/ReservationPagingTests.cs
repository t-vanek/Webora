using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the paged read of a driver's booking history. The unpaged read stops at 200 rows and says
/// nothing about it, so a long-serving driver's older bookings were simply unreachable; the paged one
/// carries the total and walks a stale page index back instead of showing an empty table.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReservationPagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private ReservationService _reservations = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the paging tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_ReservationPagingTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var factory = new TestDbContextFactory(_options);
        _reservations = new ReservationService(factory, new FakeParkingSettings(),
            new FakeSiteSettings(), new FixedTimeProvider(Now), new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());
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
    public async Task A_page_carries_the_total_and_walks_newest_first()
    {
        var user = Guid.NewGuid();
        var spotId = await SeedAsync(user, bookings: 25);

        var first = await _reservations.GetMyReservationsPageAsync(user, pageIndex: 0, pageSize: 10);
        var last = await _reservations.GetMyReservationsPageAsync(user, pageIndex: 2, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(first.TotalCount, Is.EqualTo(25), "The total is what lets the pager exist at all.");
            Assert.That(first.Items, Has.Count.EqualTo(10));
            Assert.That(last.Items, Has.Count.EqualTo(5), "The tail page is short, not padded.");
            Assert.That(first.PageIndex, Is.Zero);
            Assert.That(last.PageIndex, Is.EqualTo(2));
        });

        // Newest first, and the pages do not overlap or skip.
        Assert.That(first.Items[0].StartUtc, Is.GreaterThan(first.Items[^1].StartUtc));
        var second = await _reservations.GetMyReservationsPageAsync(user, pageIndex: 1, pageSize: 10);
        var seen = first.Items.Concat(second.Items).Concat(last.Items).Select(r => r.Id).ToList();
        Assert.That(seen.Distinct().Count(), Is.EqualTo(25), "Every booking appears exactly once across the pages.");
        _ = spotId;
    }

    [Test]
    public async Task A_page_index_past_the_end_walks_back_to_the_last_real_page()
    {
        var user = Guid.NewGuid();
        await SeedAsync(user, bookings: 12);

        var beyond = await _reservations.GetMyReservationsPageAsync(user, pageIndex: 99, pageSize: 10);

        Assert.That(beyond.PageIndex, Is.EqualTo(1),
            "Asking past the end must land on the last page, not render an empty table.");
        Assert.That(beyond.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task A_driver_with_no_bookings_gets_an_empty_first_page()
    {
        var page = await _reservations.GetMyReservationsPageAsync(Guid.NewGuid(), pageIndex: 3, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(page.TotalCount, Is.Zero);
            Assert.That(page.Items, Is.Empty);
            Assert.That(page.PageIndex, Is.Zero, "Nothing to page through means page one.");
        });
    }

    [Test]
    public async Task The_page_size_is_clamped_server_side()
    {
        var user = Guid.NewGuid();
        await SeedAsync(user, bookings: 30);

        var huge = await _reservations.GetMyReservationsPageAsync(user, pageIndex: 0, pageSize: 10_000);
        var zero = await _reservations.GetMyReservationsPageAsync(user, pageIndex: 0, pageSize: 0);

        Assert.That(huge.PageSize, Is.EqualTo(100), "A caller must not be able to ask for the whole table.");
        Assert.That(zero.PageSize, Is.EqualTo(1), "Nor for nothing at all.");
    }

    /// <summary>One spot and <paramref name="bookings"/> completed bookings, one per day going back.</summary>
    private async Task<Guid> SeedAsync(Guid userId, int bookings)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot($"PG-{Guid.NewGuid().ToString("N")[..4]}", ParkingSpotType.Standard);
        dbContext.ParkingSpots.Add(spot);

        for (var i = 0; i < bookings; i++)
        {
            var start = Now.AddDays(-i - 1);
            dbContext.Reservations.Add(new Reservation(spot.Id, userId, start, start.AddHours(8), false, start));
        }

        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
