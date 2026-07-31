using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the paged read behind the spot administration screen. The unpaged read draws the whole lot,
/// and every row of that screen carries an inline resident picker holding an option per user, so the
/// page grew as the lot times the directory. Paging it moves two things into the service: the natural
/// code order (D3-2 before D3-10), which no database collation produces, and the promise that a spot
/// just created is on the page that comes back.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class ParkingSpotPagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private ParkingSpotService _spots = null!;

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
            InitialCatalog = "D3Parking_ParkingSpotPagingTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        _spots = new ParkingSpotService(new TestDbContextFactory(_options), new NullNotificationService(),
            new FakeSiteSettings(), new FixedTimeProvider(Now), new PassthroughLocalizer<ParkingMessages>());
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

    [SetUp]
    public async Task ResetAsync()
    {
        if (_options is null)
        {
            return;
        }

        await using var dbContext = new D3ParkingDbContext(_options);
        dbContext.ParkingSpots.RemoveRange(dbContext.ParkingSpots);
        await dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task A_page_walks_the_lot_in_natural_code_order_and_carries_the_total()
    {
        await SeedAsync("P2", 1, 25);

        var first = await _spots.ListPageAsync(pageIndex: 0, pageSize: 10);
        var second = await _spots.ListPageAsync(pageIndex: 1, pageSize: 10);
        var last = await _spots.ListPageAsync(pageIndex: 2, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(first.TotalCount, Is.EqualTo(25), "The total is what lets the pager exist at all.");
            Assert.That(first.Items, Has.Count.EqualTo(10));
            Assert.That(last.Items, Has.Count.EqualTo(5), "The tail page is short, not padded.");
        });

        // The point of sorting in the service: a database string sort would put P2-10 second.
        Assert.That(first.Items.Select(s => s.Code),
            Is.EqualTo(new[] { "P2-1", "P2-2", "P2-3", "P2-4", "P2-5", "P2-6", "P2-7", "P2-8", "P2-9", "P2-10" }));
        Assert.That(second.Items[0].Code, Is.EqualTo("P2-11"), "The pages join up where the previous one stopped.");

        var seen = first.Items.Concat(second.Items).Concat(last.Items).Select(s => s.Id).ToList();
        Assert.That(seen.Distinct().Count(), Is.EqualTo(25), "Every spot appears exactly once across the pages.");
    }

    [Test]
    public async Task A_page_index_past_the_end_walks_back_to_the_last_real_page()
    {
        await SeedAsync("A", 1, 12);

        var beyond = await _spots.ListPageAsync(pageIndex: 99, pageSize: 10);

        Assert.That(beyond.PageIndex, Is.EqualTo(1),
            "Asking past the end must land on the last page, not render an empty table.");
        Assert.That(beyond.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task An_empty_lot_gets_an_empty_first_page()
    {
        var page = await _spots.ListPageAsync(pageIndex: 3, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(page.TotalCount, Is.Zero);
            Assert.That(page.Items, Is.Empty);
            Assert.That(page.PageIndex, Is.Zero, "Nothing to page through means page one.");
        });
    }

    [Test]
    public async Task The_search_narrows_the_total_the_pager_is_built_from()
    {
        await SeedAsync("P2", 1, 25);

        // P2-1 plus P2-10 … P2-19: the total has to be the matches, not the lot, or the pager offers
        // pages that hold nothing.
        var matches = await _spots.ListPageAsync(pageIndex: 0, pageSize: 100, search: "P2-1");
        var lowercase = await _spots.ListPageAsync(pageIndex: 0, pageSize: 100, search: "p2-1");
        var nothing = await _spots.ListPageAsync(pageIndex: 0, pageSize: 100, search: "Q9");

        Assert.Multiple(() =>
        {
            Assert.That(matches.TotalCount, Is.EqualTo(11));
            Assert.That(matches.Items.Select(s => s.Code), Has.All.Contains("P2-1"));
            Assert.That(lowercase.TotalCount, Is.EqualTo(11), "Searching a code is not a case-sensitive act.");
            Assert.That(nothing.TotalCount, Is.Zero);
            Assert.That(nothing.Items, Is.Empty);
        });
    }

    [Test]
    public async Task A_jump_lands_on_the_page_holding_the_code_whatever_page_was_asked_for()
    {
        await SeedAsync("P2", 1, 25);

        var jumped = await _spots.ListPageAsync(pageIndex: 0, pageSize: 10, jumpToCode: "p2-23");

        Assert.Multiple(() =>
        {
            Assert.That(jumped.PageIndex, Is.EqualTo(2), "P2-23 is the 23rd spot in code order, so page three.");
            Assert.That(jumped.Items.Select(s => s.Code), Does.Contain("P2-23"));
        });
    }

    [Test]
    public async Task A_jump_to_a_code_that_is_not_there_leaves_the_page_where_it_was()
    {
        await SeedAsync("P2", 1, 25);

        var stayed = await _spots.ListPageAsync(pageIndex: 1, pageSize: 10, jumpToCode: "Q9-1");

        Assert.That(stayed.PageIndex, Is.EqualTo(1), "A code the lot does not hold is not a reason to move.");
    }

    [Test]
    public async Task The_page_size_is_clamped_server_side()
    {
        await SeedAsync("A", 1, 30);

        var huge = await _spots.ListPageAsync(pageIndex: 0, pageSize: 10_000);
        var zero = await _spots.ListPageAsync(pageIndex: 0, pageSize: 0);

        Assert.Multiple(() =>
        {
            Assert.That(huge.PageSize, Is.EqualTo(100), "A caller must not be able to ask for the whole table.");
            Assert.That(zero.PageSize, Is.EqualTo(1), "Nor for nothing at all.");
        });
    }

    /// <summary>Spots <paramref name="from"/>…<paramref name="to"/> of one section, created out of order.</summary>
    private async Task SeedAsync(string section, int from, int to)
    {
        await using var dbContext = new D3ParkingDbContext(_options);

        // Descending on purpose: insertion order must not be what the page comes back in.
        for (var i = to; i >= from; i--)
        {
            dbContext.ParkingSpots.Add(new ParkingSpot($"{section}-{i}", ParkingSpotType.Standard));
        }

        await dbContext.SaveChangesAsync();
    }
}
