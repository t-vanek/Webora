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
        _policy = IncentivePolicy.Default;
        var parkingSettings = new FixedParkingSettings(_policy);
        var siteSettings = new FixedSiteSettings();
        var time = new FixedTimeProvider(Now);
        var notifications = new NullNotificationService();
        var messages = new PassthroughLocalizer();

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
    public async Task Concurrent_completes_pay_one_reward_package()
    {
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot("K-01", ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, Now.AddHours(-1), Now.AddHours(2), false, Now.AddHours(-2));
        reservation.CheckIn(Now.AddMinutes(-30));
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
        });

        var results = await WhenAllTolerant(
            () => _reservations.CompleteAsync(userId, reservation.Id),
            () => _reservations.CompleteAsync(userId, reservation.Id));

        Assert.That(results.Count(r => r is { Succeeded: true }), Is.EqualTo(1));

        await using var db = new D3ParkingDbContext(_options);
        var markers = await db.PointsLedgerEntries
            .CountAsync(e => e.UserId == userId && e.Reason == IncentiveReason.ReservationCompleted);
        var streak = await db.ParkerScores.Where(s => s.UserId == userId).Select(s => s.CompletionStreak).SingleAsync();
        Assert.That(markers, Is.EqualTo(1), "The daily completion marker must exist exactly once.");
        Assert.That(streak, Is.EqualTo(1), "The completion streak must advance by one, not two.");
    }

    [Test]
    public async Task Check_in_and_no_show_sweep_agree_on_one_outcome()
    {
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot("S-01", ParkingSpotType.Standard);
        // Past the no-show grace: the sweep considers it due while check-in is still allowed.
        var overdue = _policy.NoShowGracePeriod + TimeSpan.FromMinutes(5);
        var reservation = new Reservation(spot.Id, userId, Now - overdue, Now.AddHours(2), false, Now - overdue);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
        });

        await WhenAllTolerant(
            () => _reservations.CheckInAsync(userId, reservation.Id),
            async () =>
            {
                await _reservations.SweepNoShowsAsync();
                return ParkingResult.Success;
            });

        await using var db = new D3ParkingDbContext(_options);
        var status = await db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync();
        var penalties = await db.PointsLedgerEntries
            .CountAsync(e => e.UserId == userId && e.Reason == IncentiveReason.NoShowPenalty);

        // Either the sweep won (no-show + penalty) or the driver won (checked in, no penalty).
        // The broken interleaving was CheckedIn *with* a penalty — a present driver punished.
        if (status == ReservationStatus.CheckedIn)
        {
            Assert.That(penalties, Is.Zero, "A driver whose check-in won must not carry a no-show penalty.");
        }
        else
        {
            Assert.That(status, Is.EqualTo(ReservationStatus.NoShow));
            Assert.That(penalties, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Owner_arrival_and_guest_booking_cannot_both_take_the_spot()
    {
        var ownerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var spot = new ParkingSpot("V-01", ParkingSpotType.Standard);
        spot.AssignOwner(ownerId);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(ownerId));
        });

        // Past the auto-share cutoff the guest may book the owner's spot for the rest of today
        // while the owner can still confirm arrival — the classic double-booking window.
        var results = await WhenAllTolerant(
            () => _reservations.ReserveAsync(guestId, spot.Id, Now.AddMinutes(30), Now.AddHours(3)),
            () => _residentSpots.ConfirmArrivalAsync(ownerId));

        await using var db = new D3ParkingDbContext(_options);
        var active = await db.Reservations.CountAsync(r => r.SpotId == spot.Id
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn));

        Assert.That(active, Is.EqualTo(1), "The spot must end the race with a single active reservation.");
        Assert.That(results.Count(r => r is { Succeeded: true }), Is.LessThanOrEqualTo(1));
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<D3ParkingDbContext> options) : IDbContextFactory<D3ParkingDbContext>
    {
        public D3ParkingDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedParkingSettings(IncentivePolicy policy) : IParkingSettingsService
    {
        public Task<IncentivePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(policy);

        public Task<TimeSpan> GetSweepIntervalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GeoPoint?> GetLotLocationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ParkingSettingsDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ParkingResult> UpdateAsync(ParkingSettingsDto settings, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AdaptPeakSurchargeAsync(double measuredOccupancy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedSiteSettings : ISiteSettingsService
    {
        public Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc);

        public Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> UpdateRegionalAsync(RegionalSettingsDto regional, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> UpdateEncodingAsync(EncodingSettingsDto encoding, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> UpdateAccountsAsync(AccountsSettingsDto accounts, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> UpdateGeneralAsync(GeneralSettingsDto general, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetDefaultLanguageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> GetPageCharsetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> GetEmailCharsetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetDefaultRoleAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteIdentityDto> GetIdentityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NullNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, NotificationEmailOptions? emailOptions, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NotificationDto>>([]);

        public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<NotificationPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MuteAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MuteUntilAsync(Guid userId, DateTimeOffset untilUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UnmuteAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetScopeAsync(Guid userId, NotificationScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetAvailabilityOptInAsync(Guid userId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubscribeToPushAsync(Guid userId, PushSubscriptionDto subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UnsubscribeFromPushAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PassthroughLocalizer : IStringLocalizer<ParkingMessages>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
