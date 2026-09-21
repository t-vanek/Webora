using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Exercises the matcher against the production database engine, including a paused availability
/// read and a competing insert. No in-memory provider can establish these lock guarantees.
/// Set ConnectionStrings__SqlServer to a disposable test server; each test owns a unique database.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class QueueMatchingConsistencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = Now.AddHours(2);
    private static readonly IncentivePolicy Policy = new()
    {
        BaseReservationCost = 0,
        WeeklyReservationLimitEnabled = false,
    };

    private DbContextOptions<D3ParkingDbContext>? _options;
    private string _connectionString = string.Empty;

    [SetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; queue matching requires a real SQL Server.");
        }

        _connectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_QueueMatchingTests_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = Options();
        await using var db = new D3ParkingDbContext(_options);
        // Never delete or reuse the configured database, even before creating the fixture.
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_options is not null)
        {
            await using var db = new D3ParkingDbContext(_options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Adjacent_windows_can_hold_the_same_spot()
    {
        var spot = new ParkingSpot("Q-01", ParkingSpotType.Standard);
        var first = new QueueEntry(Guid.NewGuid(), Start, Start.AddHours(1), Now.AddMinutes(-2));
        var next = new QueueEntry(Guid.NewGuid(), Start.AddHours(1), Start.AddHours(2), Now.AddMinutes(-1));
        await SeedAsync(spot, first, next);

        var count = await Service().ProcessQueueAsync();

        await using var db = new D3ParkingDbContext(_options!);
        var entries = await db.QueueEntries.ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(2));
            Assert.That(entries.All(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId == spot.Id), Is.True,
                "A hold ends exclusively: the next window starts when the earlier one ends.");
        });
    }

    [Test]
    public async Task An_existing_hold_blocks_only_overlapping_windows()
    {
        var spot = new ParkingSpot("Q-02", ParkingSpotType.Standard);
        var held = new QueueEntry(Guid.NewGuid(), Start, Start.AddHours(1), Now.AddMinutes(-3));
        held.Offer(spot.Id, Now.AddMinutes(10));
        var overlap = new QueueEntry(Guid.NewGuid(), Start.AddMinutes(30), Start.AddHours(2), Now.AddMinutes(-2));
        var adjacent = new QueueEntry(Guid.NewGuid(), Start.AddHours(1), Start.AddHours(2), Now.AddMinutes(-1));
        await SeedAsync(spot, held, overlap, adjacent);

        Assert.That(await Service().ProcessQueueAsync(), Is.EqualTo(1));

        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.QueueEntries.Single(q => q.Id == overlap.Id).Status, Is.EqualTo(QueueEntryStatus.Waiting));
            Assert.That(db.QueueEntries.Single(q => q.Id == adjacent.Id).Status, Is.EqualTo(QueueEntryStatus.Offered));
        });
    }

    [Test]
    public async Task Independent_matchers_offer_one_spot_once_for_an_overlapping_window()
    {
        var spot = new ParkingSpot("Q-03", ParkingSpotType.Standard);
        var first = new QueueEntry(Guid.NewGuid(), Start, Start.AddHours(2), Now.AddMinutes(-2));
        var second = new QueueEntry(Guid.NewGuid(), Start.AddHours(1), Start.AddHours(3), Now.AddMinutes(-1));
        await SeedAsync(spot, first, second);

        var counts = await Task.WhenAll(Service().ProcessQueueAsync(), Service().ProcessQueueAsync());

        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(counts.Sum(), Is.EqualTo(1));
            Assert.That(db.QueueEntries.Single(q => q.Id == first.Id).Status, Is.EqualTo(QueueEntryStatus.Offered));
            Assert.That(db.QueueEntries.Single(q => q.Id == second.Id).Status, Is.EqualTo(QueueEntryStatus.Waiting));
        });
    }

    [Test]
    public async Task A_competing_booking_and_matcher_cannot_both_obtain_the_window()
    {
        var spot = new ParkingSpot("Q-04", ParkingSpotType.Standard);
        var waiter = new QueueEntry(Guid.NewGuid(), Start, Start.AddHours(2), Now.AddMinutes(-1));
        var parker = Guid.NewGuid();
        await SeedAsync(spot, waiter);

        var matching = Service().ProcessQueueAsync();
        var booking = Service().ReserveAsync(parker, spot.Id, Start, Start.AddHours(2));
        await Task.WhenAll(matching, booking);

        await using var db = new D3ParkingDbContext(_options!);
        var offered = await db.QueueEntries.AnyAsync(q => q.Id == waiter.Id && q.Status == QueueEntryStatus.Offered);
        var reserved = await db.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.Status == ReservationStatus.Reserved);
        Assert.Multiple(() =>
        {
            Assert.That(offered, Is.True, "The existing eligible waiter always has priority, regardless of which transaction starts first.");
            Assert.That(reserved, Is.False);
            Assert.That(booking.Result.Succeeded, Is.EqualTo(reserved));
            if (offered)
            {
                Assert.That(booking.Result.Errors.Any(e => e is "Parking_Error_SpotHeld" or "Parking_Error_QueueHasPriority"), Is.True);
            }
        });
    }

    [Test]
    public async Task Availability_read_prevents_a_competing_insert_until_the_offer_is_saved()
    {
        var spot = new ParkingSpot("Q-05", ParkingSpotType.Standard);
        var waiter = new QueueEntry(Guid.NewGuid(), Start, Start.AddHours(2), Now.AddMinutes(-1));
        await SeedAsync(spot, waiter);
        var barrier = new PauseAfterReservationSnapshot();
        var matching = Service(Options(barrier)).ProcessQueueAsync();

        try
        {
            await barrier.SnapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var competing = new D3ParkingDbContext(_options!);
            await competing.Database.OpenConnectionAsync();
            // A bounded SQL lock wait establishes that the engine protects the empty interval.
            // Before the fix this insert commits while the matcher still uses its empty snapshot.
            await competing.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 500");
            competing.Reservations.Add(new Reservation(spot.Id, Guid.NewGuid(), Start, Start.AddHours(2), false, Now));

            // EF's SQL Server execution strategy can wrap the update exception as a transient
            // InvalidOperationException. The SQL cause, not that wrapper, proves the range lock.
            var error = Assert.CatchAsync<Exception>(() => competing.SaveChangesAsync());
            Assert.That(error!.GetBaseException(), Is.TypeOf<SqlException>());
            Assert.That(((SqlException)error.GetBaseException()).Number, Is.EqualTo(1222),
                "The insert must be blocked by the matcher's SQL range lock, not fail for another reason.");
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await matching.WaitAsync(TimeSpan.FromSeconds(15));
        }

        var result = await Service().ReserveAsync(Guid.NewGuid(), spot.Id, Start, Start.AddHours(2));
        Assert.Multiple(() =>
        {
            Assert.That(matching.Result, Is.EqualTo(1));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Errors, Does.Contain("Parking_Error_SpotHeld"));
        });
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.Reservations.CountAsync(), Is.Zero);
    }

    private DbContextOptions<D3ParkingDbContext> Options(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(_connectionString);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private ReservationService Service(DbContextOptions<D3ParkingDbContext>? options = null) => new(
        new TestDbContextFactory(options ?? _options!), new FakeParkingSettings(Policy), new FakeSiteSettings(),
        new FixedTimeProvider(Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>());

    private async Task SeedAsync(ParkingSpot spot, params QueueEntry[] entries)
    {
        await using var db = new D3ParkingDbContext(_options!);
        db.ParkingSpots.Add(spot);
        db.QueueEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    private sealed class PauseAfterReservationSnapshot : DbCommandInterceptor
    {
        private int _paused;
        public TaskCompletionSource SnapshotRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // The candidate-spot read follows full materialization of the reservation snapshot.
            if (command.CommandText.Contains("FROM [ParkingSpots]", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                SnapshotRead.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
