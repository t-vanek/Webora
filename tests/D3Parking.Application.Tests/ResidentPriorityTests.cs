using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins down resident/shared-spot priority: an unbooked released day can return to the resident,
/// a confirmed guest plan remains dependable, and a pending waitlist hold can be withdrawn without
/// costing the waiter their position.
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
    private static readonly IncentivePolicy RewardPolicy = new()
    {
        ResidentReleasePointsPerHour = 2,
        ResidentReleaseMaxPoints = 40,
    };

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
    public async Task Reclaiming_a_released_day_restores_the_hold_without_touching_reputation()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-01", owner);
        var residents = CreateResidentService(now: BeforeCutoff);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var awarded = (await dbContext.SpotReleases.SingleAsync(r => r.SpotId == spot)).AwardedPoints;
            Assert.That(awarded, Is.Zero, "Sharing never grants points up front.");
        }

        var status = await residents.GetMyOwnedSpotAsync(owner);
        Assert.That(status!.UpcomingReleases, Is.EqualTo(new[] { new ReleasedDayDto(Tomorrow, false) }),
            "The released day must surface as reclaimable before anyone books it.");

        Assert.That((await residents.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            Assert.That(await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.False,
                "A reclaimed day is no longer shared — the spot is held for its resident again.");
            Assert.That(await dbContext.PointsLedgerEntries
                .AnyAsync(e => e.UserId == owner && e.Reason == IncentiveReason.ResidentShareReclaimed), Is.False,
                "Taking a day back cannot reduce reputation because sharing awarded none.");
        }
    }

    [Test]
    public async Task A_confirmed_guest_plan_cannot_be_reclaimed_by_the_resident()
    {
        var owner = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-02", owner);
        var residents = CreateResidentService(now: BeforeCutoff, notifications: out var sent,
            policy: RewardPolicy with { ResidentReclaimPolicy = ResidentReclaimPolicy.ConfirmedBookingProtected });
        Guid reservationId;

        await using (var users = new D3ParkingDbContext(_options))
        {
            users.Users.AddRange(
                new ApplicationUser
                {
                    Id = owner,
                    UserName = "jan.novak@example.test",
                    NormalizedUserName = "JAN.NOVAK@EXAMPLE.TEST",
                    Email = "jan.novak@example.test",
                    NormalizedEmail = "JAN.NOVAK@EXAMPLE.TEST",
                    DisplayName = "Jan Novák",
                },
                new ApplicationUser
                {
                    Id = guest,
                    UserName = "petra.svobodova@example.test",
                    NormalizedUserName = "PETRA.SVOBODOVA@EXAMPLE.TEST",
                    Email = "petra.svobodova@example.test",
                    NormalizedEmail = "PETRA.SVOBODOVA@EXAMPLE.TEST",
                    DisplayName = "Petra Svobodová",
                });
            await users.SaveChangesAsync();
        }

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (dayStart, dayEnd) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            var score = new ParkerScore(guest);
            score.GrantCreditIfDue(10, ParkerScore.PeriodOf(BeforeCutoff), BeforeCutoff);
            score.ChargeCredits(7, BeforeCutoff);
            var reservation = new Reservation(spot, guest, dayStart, dayEnd, false, BeforeCutoff, creditsCharged: 7);
            dbContext.ParkerScores.Add(score);
            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        var before = await residents.GetMyOwnedSpotAsync(owner);
        var namedDay = before!.DaySchedule.Single(day => day.Date == Tomorrow);
        Assert.Multiple(() =>
        {
            Assert.That(before.UpcomingReleases, Is.EqualTo(new[] { new ReleasedDayDto(Tomorrow, true) }));
            Assert.That(namedDay.State, Is.EqualTo(OwnedSpotDayState.SharedTaken));
            Assert.That(namedDay.AllocationState, Is.EqualTo(ResidentAllocationState.Released));
            Assert.That(namedDay.BookingState, Is.EqualTo(ResidentBookingState.ReservedByOtherUser));
            Assert.That(namedDay.AssignedResident?.Name, Is.EqualTo("Jan Novák"));
            Assert.That(namedDay.Bookings.Single().User.Name, Is.EqualTo("Petra Svobodová"));
            Assert.That(namedDay.Bookings.Single().ReservationId, Is.EqualTo(reservationId));
            Assert.That(namedDay.CanReclaim, Is.True);
        });

        var result = await residents.ReclaimAsync(owner, Tomorrow, Tomorrow);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ResidentDayAlreadyBooked"));

        await using (var check = new D3ParkingDbContext(_options))
        {
            Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.True,
                "The released day must stay shared while the confirmed guest plan exists.");
            Assert.That((await check.Reservations.SingleAsync(r => r.Id == reservationId)).Status,
                Is.EqualTo(ReservationStatus.Reserved));
            Assert.That((await check.ParkerScores.SingleAsync(s => s.UserId == guest)).Credits, Is.EqualTo(3),
                "Refusing the reclaim must not alter the guest's planning budget.");
            Assert.That(await check.PointsLedgerEntries.AnyAsync(e => e.UserId == guest
                && e.ReservationId == reservationId && e.Reason == IncentiveReason.ReservationRefund), Is.False);
        }

        Assert.That(sent.Sent, Is.Empty);
    }

    [Test]
    public async Task Hybrid_reclaim_moves_the_guest_and_keeps_their_plan_unchanged()
    {
        var owner = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-10", owner, ParkingSpotType.ElectricCharging);
        var replacement = await CreateSharedSpotAsync("RP-11", ParkingSpotType.ElectricCharging);
        var policy = RewardPolicy with { ResidentReclaimPolicy = ResidentReclaimPolicy.AdvanceOrReplacement };
        var residents = CreateResidentService(BeforeCutoff, out var sent, policy);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        Guid reservationId;
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            var reservation = new Reservation(spot, guest, start, end, false, BeforeCutoff, 7);
            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        Assert.That((await residents.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);

        await using (var check = new D3ParkingDbContext(_options))
        {
            var reservation = await check.Reservations.SingleAsync(r => r.Id == reservationId);
            Assert.Multiple(() =>
            {
                Assert.That(reservation.SpotId, Is.EqualTo(replacement));
                Assert.That(reservation.Status, Is.EqualTo(ReservationStatus.Reserved));
                Assert.That(reservation.CreditsCharged, Is.EqualTo(7));
            });
            Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.False);
        }
        Assert.That(sent.Sent, Does.Contain((guest, "Parking_Notify_ResidentMoved_Title")));
    }

    [Test]
    public async Task Hybrid_auto_plan_before_deadline_refunds_and_queues_when_no_replacement_exists()
    {
        var owner = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-12", owner, ParkingSpotType.Motorcycle);
        var policy = RewardPolicy with
        {
            ResidentReclaimPolicy = ResidentReclaimPolicy.AdvanceOrReplacement,
            ManualReleasesAreBinding = true,
            ResidentProtectionDeadlineMode = ResidentProtectionDeadlineMode.PreviousDayAtTime,
            ResidentProtectionPreviousDayTime = new TimeOnly(18, 0),
            ResidentNoReplacementAction = ResidentNoReplacementAction.CancelAndQueue,
        };
        var residents = CreateResidentService(BeforeCutoff, out var sent, policy);
        Guid reservationId;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            dbContext.SpotReleases.Add(new SpotRelease(
                spot, owner, Tomorrow, BeforeCutoff, 0, SpotReleaseSource.UsagePlan));
            var score = new ParkerScore(guest);
            score.GrantCreditIfDue(10, ParkerScore.PeriodOf(BeforeCutoff), BeforeCutoff);
            score.ChargeCredits(7, BeforeCutoff);
            var reservation = new Reservation(spot, guest, start, end, false, BeforeCutoff, 7);
            dbContext.ParkerScores.Add(score);
            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        Assert.That((await residents.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);

        await using (var check = new D3ParkingDbContext(_options))
        {
            Assert.That((await check.Reservations.SingleAsync(r => r.Id == reservationId)).Status,
                Is.EqualTo(ReservationStatus.Cancelled));
            Assert.That((await check.ParkerScores.SingleAsync(s => s.UserId == guest)).Credits, Is.EqualTo(10));
            Assert.That(await check.QueueEntries.AnyAsync(q => q.UserId == guest && q.Status == QueueEntryStatus.Waiting), Is.True);
        }
        Assert.That(sent.Sent, Does.Contain((guest, "Parking_Notify_ResidentQueued_Title")));
    }

    [Test]
    public async Task Absolute_priority_can_cancel_without_replacement_and_only_notify_the_guest()
    {
        var owner = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-14", owner, ParkingSpotType.Motorcycle);
        var policy = RewardPolicy with
        {
            ResidentReclaimPolicy = ResidentReclaimPolicy.AbsolutePriority,
            ManualReleasesAreBinding = true,
            ResidentNoReplacementAction = ResidentNoReplacementAction.CancelAndNotify,
        };
        var residents = CreateResidentService(BeforeCutoff, out var sent, policy);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        Guid reservationId;
        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
            var reservation = new Reservation(spot, guest, start, end, false, BeforeCutoff);
            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        Assert.That((await residents.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);

        await using (var check = new D3ParkingDbContext(_options))
        {
            Assert.That((await check.Reservations.SingleAsync(r => r.Id == reservationId)).Status,
                Is.EqualTo(ReservationStatus.Cancelled));
            Assert.That(await check.QueueEntries.AnyAsync(q => q.UserId == guest
                && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)), Is.False);
            Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.False);
        }
        Assert.That(sent.Sent, Does.Contain((guest, "Parking_Notify_ResidentCancelled_Title")));
    }

    [Test]
    public async Task A_checked_in_guest_is_never_cancelled_when_no_replacement_exists()
    {
        var owner = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-13", owner, ParkingSpotType.Disabled);
        var policy = RewardPolicy with
        {
            ResidentReclaimPolicy = ResidentReclaimPolicy.AbsolutePriority,
            ManualReleasesAreBinding = false,
            ResidentNoReplacementAction = ResidentNoReplacementAction.CancelAndQueue,
        };
        var residents = CreateResidentService(BeforeCutoff, out _, policy);
        Guid reservationId;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var reservation = new Reservation(
                spot, guest, BeforeCutoff.AddHours(-1), BeforeCutoff.AddHours(3), false, BeforeCutoff.AddDays(-1));
            reservation.CheckIn(BeforeCutoff.AddMinutes(-30));
            dbContext.SpotReleases.Add(new SpotRelease(
                spot, owner, Today, BeforeCutoff.AddDays(-1), 0, SpotReleaseSource.UsagePlan));
            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        var result = await residents.ReclaimAsync(owner, Today, Today);
        Assert.That(result.Errors, Does.Contain("Parking_Error_ResidentReclaimManagerRequired"));

        await using var check = new D3ParkingDbContext(_options);
        Assert.That((await check.Reservations.SingleAsync(r => r.Id == reservationId)).Status,
            Is.EqualTo(ReservationStatus.CheckedIn));
        Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == spot && r.Date == Today), Is.True);
    }

    [Test]
    public async Task Reclaim_withdraws_a_waitlist_hold_and_the_waiter_keeps_their_position()
    {
        var owner = Guid.NewGuid();
        var waiter = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-03", owner);
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
    public async Task An_owner_booking_their_own_spot_outranks_the_hold_that_would_stop_anyone_else()
    {
        var owner = Guid.NewGuid();
        var waiter = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-05", owner);
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

    [Test]
    public async Task A_resident_still_sees_all_their_active_and_future_reservations()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("RP-06", owner);
        Guid activeId;
        Guid futureId;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var shared = new ParkingSpot("RP-07", ParkingSpotType.Standard);
            dbContext.ParkingSpots.Add(shared);
            var active = new Reservation(shared.Id, owner,
                BeforeCutoff.AddHours(-1), BeforeCutoff.AddHours(1), false, BeforeCutoff.AddDays(-1));
            var future = new Reservation(shared.Id, owner,
                BeforeCutoff.AddDays(2), BeforeCutoff.AddDays(2).AddHours(8), false, BeforeCutoff);
            dbContext.Reservations.AddRange(active, future);
            await dbContext.SaveChangesAsync();
            (activeId, futureId) = (active.Id, future.Id);
        }

        var reservations = new ReservationService(
            new TestDbContextFactory(_options), new FakeParkingSettings(), new FakeSiteSettings(),
            new FixedTimeProvider(BeforeCutoff), new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());

        var mine = await reservations.GetMyReservationsAsync(owner, upcomingOnly: true);

        Assert.That(mine.Select(r => r.Id), Is.EquivalentTo(new[] { activeId, futureId }),
            "Owning a resident spot must not hide either the current or a future booking.");
    }

    [Test]
    public async Task A_two_week_release_is_created_and_taken_back_as_one_range()
    {
        var owner = Guid.NewGuid();
        var spot = await CreateOwnedSpotAsync("RP-08", owner);
        var residents = CreateResidentService(BeforeCutoff, out _, RewardPolicy with
        {
            PublicHolidayReservationsAllowed = true,
        });
        var from = Tomorrow;
        var to = from.AddDays(13);

        Assert.That((await residents.ReleaseAsync(owner, from, to)).Succeeded, Is.True);

        await using (var released = new D3ParkingDbContext(_options))
        {
            Assert.That(await released.SpotReleases.CountAsync(r => r.SpotId == spot), Is.EqualTo(14),
                "A two-week absence must release every selected calendar day.");
        }

        Assert.That((await residents.ReclaimAsync(owner, from, to)).Succeeded, Is.True);

        await using var reclaimed = new D3ParkingDbContext(_options);
        Assert.That(await reclaimed.SpotReleases.AnyAsync(r => r.SpotId == spot), Is.False,
            "The resident can take the complete two-week range back in one operation.");
    }

    [Test]
    public async Task Booking_an_alternative_atomically_releases_the_assigned_resident_day()
    {
        var resident = Guid.NewGuid();
        var residentSpot = await CreateOwnedSpotAsync("RP-ALT-A", resident);
        var alternativeSpot = await CreateSharedSpotAsync("RP-ALT-B");
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var reservations = CreateReservationService(RewardPolicy with
        {
            ReservationTimeMode = ReservationTimeMode.AllDay,
            ResidentAlternativeBookingPolicy = ResidentAlternativeBookingPolicy.AutoRelease,
        });

        var result = await reservations.ReserveAsync(resident, alternativeSpot, start, end);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.ResidentSpotAutomaticallyReleased, Is.True);
        });
        Guid alternativeReservationId;
        await using (var check = new D3ParkingDbContext(_options))
        {
            var release = await check.SpotReleases.SingleAsync(r => r.SpotId == residentSpot && r.Date == Tomorrow);
            alternativeReservationId = await check.Reservations
                .Where(r => r.SpotId == alternativeSpot && r.UserId == resident)
                .Select(r => r.Id)
                .SingleAsync();
            Assert.Multiple(() =>
            {
                Assert.That(release.OwnerId, Is.EqualTo(resident));
                Assert.That(release.Source, Is.EqualTo(SpotReleaseSource.AlternativeBooking));
            });
        }

        var cancelled = await reservations.CancelAsync(resident, alternativeReservationId);
        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Succeeded, Is.True);
            Assert.That(cancelled.ResidentSpotAutomaticallyReturned, Is.True);
        });
        await using var restored = new D3ParkingDbContext(_options);
        Assert.That(await restored.SpotReleases.AnyAsync(r => r.SpotId == residentSpot && r.Date == Tomorrow), Is.False,
            "A still-free automatic release returns to the resident when the alternative booking ends.");
    }

    [Test]
    public async Task Alternative_booking_confirmation_changes_nothing_until_it_is_accepted()
    {
        var resident = Guid.NewGuid();
        var residentSpot = await CreateOwnedSpotAsync("RP-CONF-A", resident);
        var alternativeSpot = await CreateSharedSpotAsync("RP-CONF-B");
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var reservations = CreateReservationService(RewardPolicy with
        {
            ReservationTimeMode = ReservationTimeMode.AllDay,
            ResidentAlternativeBookingPolicy = ResidentAlternativeBookingPolicy.ConfirmRelease,
        });

        var pending = await reservations.ReserveAsync(resident, alternativeSpot, start, end);
        Assert.Multiple(() =>
        {
            Assert.That(pending.Succeeded, Is.False);
            Assert.That(pending.Errors, Does.Contain("Parking_AlternativeSpot_ReleaseConfirmationRequired"));
        });
        await using (var unchanged = new D3ParkingDbContext(_options))
        {
            Assert.That(await unchanged.SpotReleases.AnyAsync(r => r.SpotId == residentSpot), Is.False);
            Assert.That(await unchanged.Reservations.AnyAsync(r => r.SpotId == alternativeSpot), Is.False);
        }

        var confirmed = await reservations.ReserveAsync(
            resident, alternativeSpot, start, end, confirmResidentRelease: true);
        Assert.Multiple(() =>
        {
            Assert.That(confirmed.Succeeded, Is.True);
            Assert.That(confirmed.ResidentSpotAutomaticallyReleased, Is.True);
        });
    }

    [Test]
    public async Task Alternative_booking_can_be_disabled_by_configuration()
    {
        var resident = Guid.NewGuid();
        var residentSpot = await CreateOwnedSpotAsync("RP-DENY-A", resident);
        var alternativeSpot = await CreateSharedSpotAsync("RP-DENY-B");
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var reservations = CreateReservationService(RewardPolicy with
        {
            ReservationTimeMode = ReservationTimeMode.AllDay,
            ResidentAlternativeBookingPolicy = ResidentAlternativeBookingPolicy.Deny,
        });

        var result = await reservations.ReserveAsync(resident, alternativeSpot, start, end);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Errors, Does.Contain("Parking_Error_AlternativeSpotDenied"));
        });
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.SpotReleases.AnyAsync(r => r.SpotId == residentSpot), Is.False);
    }

    private ResidentSpotService CreateResidentService(DateTimeOffset now) =>
        CreateResidentService(now, out _);

    private ResidentSpotService CreateResidentService(DateTimeOffset now, out RecordingNotificationService notifications,
        IncentivePolicy? policy = null)
    {
        notifications = new RecordingNotificationService();
        return new ResidentSpotService(
            new TestDbContextFactory(_options),
            new FakeParkingSettings(policy ?? RewardPolicy),
            new FakeSiteSettings(),
            new FixedTimeProvider(now),
            notifications,
            new PassthroughLocalizer<ParkingMessages>());
    }

    private ReservationService CreateReservationService(IncentivePolicy policy) => new(
        new TestDbContextFactory(_options), new FakeParkingSettings(policy), new FakeSiteSettings(),
        new FixedTimeProvider(BeforeCutoff), new NullNotificationService(),
        new PassthroughLocalizer<ParkingMessages>());

    private async Task<Guid> CreateOwnedSpotAsync(string code, Guid ownerId,
        ParkingSpotType type = ParkingSpotType.Standard)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, type);
        spot.AssignOwner(ownerId);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }

    private async Task<Guid> CreateSharedSpotAsync(string code,
        ParkingSpotType type = ParkingSpotType.Standard)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, type);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
