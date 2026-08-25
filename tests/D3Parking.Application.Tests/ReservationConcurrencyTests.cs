using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Accounts;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Infrastructure.Identity;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Races two real service calls against SQL Server and asserts the money/state invariants the
/// rowversion tokens and serializable transactions exist to protect: one refund per charge, one
/// one release outcome per day, one active reservation per spot, no penalty for a driver who checked
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
    public async Task Concurrent_releases_refund_once_and_award_no_points()
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
        Assert.That(rewards, Is.Zero, "Releasing a reservation never awards points.");
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
    public async Task Weekly_limit_has_no_last_minute_bypass()
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
        Assert.That(advance.Errors, Does.Contain("Parking_Error_WeeklyReservationLimit_NoLastMinute"));

        var closeInService = new ReservationService(
            _factory, new FixedParkingSettings(_policy), new FakeSiteSettings(),
            new FixedTimeProvider(saturday.AddHours(4)), new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());
        var closeIn = await closeInService.ReserveAsync(userId, spots[2].Id, sunday, sunday.AddHours(8));
        Assert.That(closeIn.Succeeded, Is.False,
            "Approaching the start must not silently change which days consume the weekly quota.");
    }

    [Test]
    public async Task Accepting_a_named_offer_atomically_creates_the_recipient_reservation()
    {
        var resident = new ApplicationUser
        {
            Email = $"resident-{Guid.NewGuid():N}@test.local",
            UserName = $"resident-{Guid.NewGuid():N}@test.local",
            Status = AccountStatus.Active,
        };
        var recipient = new ApplicationUser
        {
            Email = $"recipient-{Guid.NewGuid():N}@test.local",
            UserName = $"recipient-{Guid.NewGuid():N}@test.local",
            Status = AccountStatus.Active,
        };
        var spot = new ParkingSpot($"HO-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard);
        spot.AssignOwner(resident.Id);
        var start = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        var handoff = ResidentSpotHandoff.CreateOffer(
            spot.Id, resident.Id, recipient.Id, start, start.AddHours(8), Now, Now.AddHours(6));

        await SeedAsync(db =>
        {
            db.Users.AddRange(resident, recipient);
            db.ParkingSpots.Add(spot);
            db.ResidentSpotHandoffs.Add(handoff);
            GrantParkingReserve(db, recipient.Id);
        });

        var result = await _reservations.AcceptHandoffAsync(recipient.Id, handoff.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(", ", result.Errors));
        await using var db = new D3ParkingDbContext(_options);
        var persisted = await db.ResidentSpotHandoffs.SingleAsync(h => h.Id == handoff.Id);
        var reservation = await db.Reservations.SingleAsync(r => r.Id == persisted.ReservationId);
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Status, Is.EqualTo(ResidentSpotHandoffStatus.Accepted));
            Assert.That(reservation.UserId, Is.EqualTo(recipient.Id));
            Assert.That(reservation.SpotId, Is.EqualTo(spot.Id));
            Assert.That(reservation.SharedByResidentId, Is.EqualTo(resident.Id));
        });
    }

    [Test]
    public async Task Resident_approval_automatically_converts_a_user_request_to_a_reservation()
    {
        var resident = new ApplicationUser
        {
            Email = $"request-resident-{Guid.NewGuid():N}@test.local",
            UserName = $"request-resident-{Guid.NewGuid():N}@test.local",
            Status = AccountStatus.Active,
        };
        var requester = new ApplicationUser
        {
            Email = $"requester-{Guid.NewGuid():N}@test.local",
            UserName = $"requester-{Guid.NewGuid():N}@test.local",
            Status = AccountStatus.Active,
        };
        var spot = new ParkingSpot($"HR-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard);
        spot.AssignOwner(resident.Id);
        var start = new DateTimeOffset(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);
        var handoff = ResidentSpotHandoff.CreateRequest(
            spot.Id, resident.Id, requester.Id, start, start.AddHours(8), Now, Now.AddHours(6), 0);

        await SeedAsync(db =>
        {
            db.Users.AddRange(resident, requester);
            db.ParkingSpots.Add(spot);
            db.ResidentSpotHandoffs.Add(handoff);
            GrantParkingReserve(db, requester.Id);
        });

        var result = await _reservations.AcceptHandoffAsync(resident.Id, handoff.Id);

        Assert.That(result.Succeeded, Is.True, string.Join(", ", result.Errors));
        await using var db = new D3ParkingDbContext(_options);
        var persisted = await db.ResidentSpotHandoffs.SingleAsync(h => h.Id == handoff.Id);
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Status, Is.EqualTo(ResidentSpotHandoffStatus.Accepted));
            Assert.That(persisted.ReservationId, Is.Not.Null);
            Assert.That(db.Reservations.Any(r => r.Id == persisted.ReservationId
                && r.UserId == requester.Id && r.SpotId == spot.Id), Is.True);
        });
    }

    [Test]
    public async Task Handoff_cannot_create_a_reservation_after_the_recipient_loses_parking_access()
    {
        var resident = new ApplicationUser
        {
            Email = $"revoked-resident-{Guid.NewGuid():N}@test.local",
            UserName = $"revoked-resident-{Guid.NewGuid():N}@test.local",
            Status = AccountStatus.Active,
        };
        var recipient = new ApplicationUser
        {
            Email = $"revoked-recipient-{Guid.NewGuid():N}@test.local",
            UserName = $"revoked-recipient-{Guid.NewGuid():N}@test.local",
            Status = AccountStatus.Active,
        };
        var spot = new ParkingSpot($"HX-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard);
        spot.AssignOwner(resident.Id);
        var start = new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.Zero);
        var handoff = ResidentSpotHandoff.CreateOffer(
            spot.Id, resident.Id, recipient.Id, start, start.AddHours(8), Now, Now.AddHours(6));

        // The persisted offer represents one that was valid when it was sent. No live role remains
        // for the recipient by the time they try to accept it.
        await SeedAsync(db =>
        {
            db.Users.AddRange(resident, recipient);
            db.ParkingSpots.Add(spot);
            db.ResidentSpotHandoffs.Add(handoff);
        });

        var result = await _reservations.AcceptHandoffAsync(recipient.Id, handoff.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Handoff_Error_RecipientUnavailable"));
        await using var db = new D3ParkingDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(db.Reservations.Any(r => r.UserId == recipient.Id && r.SpotId == spot.Id), Is.False);
            Assert.That(db.ResidentSpotHandoffs.Where(h => h.Id == handoff.Id).Select(h => h.Status).Single(),
                Is.EqualTo(ResidentSpotHandoffStatus.Offered));
        });
    }

    [Test]
    public async Task Resident_assignment_does_not_bypass_the_global_booking_calendar()
    {
        var residentId = Guid.NewGuid();
        var spot = new ParkingSpot($"GB-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard);
        spot.AssignOwner(residentId);
        await SeedAsync(db => db.ParkingSpots.Add(spot));

        var workdaysOnly = _policy with
        {
            AllowedReservationWeekdays = Weekday.Workdays,
            WeeklyReservationLimitEnabled = false,
        };
        var service = new ReservationService(
            _factory, new FixedParkingSettings(workdaysOnly), new FakeSiteSettings(),
            new FixedTimeProvider(Now), new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());
        var sunday = new DateTimeOffset(2026, 7, 19, 8, 0, 0, TimeSpan.Zero);

        var result = await service.ReserveAsync(residentId, spot.Id, sunday, sunday.AddHours(8));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ReservationWeekdayNotAllowed"));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.Reservations.AnyAsync(r => r.SpotId == spot.Id), Is.False,
            "Owning the spot must not create a booking on a date disabled by configuration.");
    }

    [Test]
    public async Task Named_handoff_does_not_bypass_the_global_booking_calendar()
    {
        var residentId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var spot = new ParkingSpot($"GH-{Guid.NewGuid():N}"[..10], ParkingSpotType.Standard);
        spot.AssignOwner(residentId);
        var sunday = new DateTimeOffset(2026, 7, 19, 8, 0, 0, TimeSpan.Zero);
        var handoff = ResidentSpotHandoff.CreateOffer(
            spot.Id, residentId, recipientId, sunday, sunday.AddHours(8), Now, Now.AddHours(6));
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ResidentSpotHandoffs.Add(handoff);
        });

        var service = new ReservationService(
            _factory,
            new FixedParkingSettings(_policy with
            {
                AllowedReservationWeekdays = Weekday.Workdays,
                WeeklyReservationLimitEnabled = false,
            }),
            new FakeSiteSettings(), new FixedTimeProvider(Now), new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());

        var result = await service.AcceptHandoffAsync(recipientId, handoff.Id);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ReservationWeekdayNotAllowed"));
        await using var db = new D3ParkingDbContext(_options);
        Assert.Multiple(() =>
        {
            Assert.That(db.Reservations.Any(r => r.SpotId == spot.Id), Is.False);
            Assert.That(db.ResidentSpotHandoffs.Single(h => h.Id == handoff.Id).Status,
                Is.EqualTo(ResidentSpotHandoffStatus.Offered));
        });
    }

    private static void GrantParkingReserve(D3ParkingDbContext db, Guid userId)
    {
        var role = new ApplicationRole($"Parker-{Guid.NewGuid():N}");
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = role.Id });
        db.RoleClaims.Add(new IdentityRoleClaim<Guid>
        {
            RoleId = role.Id,
            ClaimType = D3ParkingClaimTypes.Permission,
            ClaimValue = Permissions.Parking.Reserve,
        });
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
