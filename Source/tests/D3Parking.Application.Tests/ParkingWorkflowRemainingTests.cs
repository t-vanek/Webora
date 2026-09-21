using D3Parking.Application.Parking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Common;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public sealed class ParkingWorkflowRemainingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);
    private static readonly IncentivePolicy Policy = new()
    {
        ReservationTimeMode = ReservationTimeMode.AllDay,
        PublicHolidayReservationsAllowed = true,
        WeeklyReservationLimitEnabled = false,
        ResidentPlanHorizonDays = 7,
    };
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [SetUp]
    public async Task SetUp()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured)) Assert.Ignore("This regression suite requires local SQL Server.");
        var builder = new SqlConnectionStringBuilder(configured)
        { InitialCatalog = $"D3Parking_Remaining_{Guid.NewGuid():N}" };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [TestCase(false), TestCase(true)]
    public async Task Existing_queue_intent_can_be_claimed_after_calendar_or_mode_changes(bool changeMode)
    {
        var spot = await Spot();
        var (start, end) = SiteTime.Day(Today, TimeZoneInfo.Utc);
        var queued = new QueueEntry(Guid.NewGuid(), start, end, Now.AddDays(-1));
        await Seed(db => db.QueueEntries.Add(queued));
        var changed = Policy with
        {
            SameDayReservationsAllowed = false,
            ReservationTimeMode = changeMode ? ReservationTimeMode.TimeWindow : ReservationTimeMode.AllDay,
            AllowedReservationWeekdays = Weekday.Everyday & ~Today.DayOfWeek.ToWeekday(),
        };
        var service = Reservations(changed);
        Assert.That(await service.ProcessQueueAsync(), Is.EqualTo(1));
        Assert.That((await service.ClaimQueueOfferAsync(queued.UserId, queued.Id)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        var booking = await db.Reservations.SingleAsync();
        Assert.That((booking.StartUtc, booking.EndUtc, booking.SpotId), Is.EqualTo((start, end, spot.Id)));
        Assert.That((await service.ReserveAsync(Guid.NewGuid(), spot.Id, start, end)).Succeeded, Is.False,
            "The exception belongs to the existing intent, not to new booking requests.");
    }

    [Test]
    public async Task A_manually_kept_day_survives_changed_and_identical_plans_until_explicitly_released()
    {
        var owner = Guid.NewGuid();
        var spot = await Spot(owner: owner);
        var service = Residents();
        Assert.That((await service.SetUsagePlanAsync(owner, Weekday.None, true)).Succeeded, Is.True);
        Assert.That((await service.ReclaimAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        Assert.That((await service.SetUsagePlanAsync(owner, Weekday.Workdays, true)).Succeeded, Is.True);
        Assert.That((await service.SetUsagePlanAsync(owner, Weekday.None, true)).Succeeded, Is.True);
        Assert.That((await service.SetUsagePlanAsync(owner, Weekday.None, true)).Succeeded, Is.True);
        await service.ApplyDuePlanReleasesAsync();
        await using (var db = new D3ParkingDbContext(_options))
        {
            Assert.That(await db.ResidentDayHolds.AnyAsync(h => h.UserId == owner && h.Date == Tomorrow), Is.True);
            Assert.That(await db.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == Tomorrow), Is.False);
        }
        Assert.That((await service.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.ResidentDayHolds.AnyAsync(h => h.UserId == owner && h.Date == Tomorrow), Is.False);
        Assert.That((await check.SpotReleases.SingleAsync(r => r.SpotId == spot.Id && r.Date == Tomorrow)).Source,
            Is.EqualTo(SpotReleaseSource.Manual));
    }

    [Test]
    public async Task Plan_preview_and_save_return_only_free_automatic_days_and_preserve_manual_booked_and_offered_days()
    {
        var owner = Guid.NewGuid();
        var spot = await Spot(owner: owner);
        await Seed(db =>
        {
            var tracked = db.ParkingSpots.Single(s => s.Id == spot.Id);
            tracked.SetUsagePlan(Weekday.None, true);
            for (var i = 1; i <= 4; i++) db.SpotReleases.Add(new SpotRelease(spot.Id, owner, Today.AddDays(i), Now, 0,
                i == 4 ? SpotReleaseSource.Manual : SpotReleaseSource.UsagePlan));
            var (start, end) = SiteTime.Day(Today.AddDays(2), TimeZoneInfo.Utc);
            db.Reservations.Add(new Reservation(spot.Id, Guid.NewGuid(), start, end, false, Now));
            (start, end) = SiteTime.Day(Today.AddDays(3), TimeZoneInfo.Utc);
            var hold = new QueueEntry(Guid.NewGuid(), start, end, Now);
            hold.Offer(spot.Id, Now.AddMinutes(30));
            db.QueueEntries.Add(hold);
        });
        var preview = await Residents().PreviewUsagePlanAsync(owner, Weekday.Everyday, false);
        Assert.That(preview.Returned, Is.EqualTo(new[] { Tomorrow }));
        Assert.That(preview.PreservedBookings, Is.EqualTo(new[] { Today.AddDays(2), Today.AddDays(3) }));
        await using (var untouched = new D3ParkingDbContext(_options))
            Assert.That(await untouched.SpotReleases.CountAsync(), Is.EqualTo(4));
        Assert.That((await Residents().SetUsagePlanAsync(owner, Weekday.Everyday, false)).Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.SpotReleases.OrderBy(r => r.Date).Select(r => r.Date).ToListAsync(),
            Is.EqualTo(new[] { Today.AddDays(2), Today.AddDays(3), Today.AddDays(4) }));
    }

    [Test]
    public async Task Queue_confirmation_releases_only_the_held_own_day_in_the_claimed_window()
    {
        var owner = Guid.NewGuid();
        var own = await Spot("OWN", owner: owner);
        var alternative = await Spot("ALT");
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var queue = new QueueEntry(owner, start, end, Now);
        queue.Offer(alternative.Id, Now.AddMinutes(30));
        await Seed(db => db.QueueEntries.Add(queue));
        var service = Reservations(Policy with { ResidentAlternativeBookingPolicy = ResidentAlternativeBookingPolicy.ConfirmRelease });
        var first = await service.ClaimQueueOfferAsync(owner, queue.Id);
        Assert.That(first.Errors, Does.Contain("Parking_AlternativeSpot_ReleaseConfirmationRequired"));
        await using (var unchanged = new D3ParkingDbContext(_options))
        {
            Assert.That(await unchanged.SpotReleases.CountAsync(), Is.Zero);
            Assert.That((await unchanged.QueueEntries.SingleAsync()).Status, Is.EqualTo(QueueEntryStatus.Offered));
        }
        Assert.That((await service.ClaimQueueOfferAsync(owner, queue.Id, true)).Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        var release = await check.SpotReleases.SingleAsync();
        Assert.That((release.SpotId, release.OwnerId, release.Date), Is.EqualTo((own.Id, owner, Tomorrow)));
        Assert.That((await check.QueueEntries.SingleAsync()).Status, Is.EqualTo(QueueEntryStatus.Claimed));
        Assert.That(await check.Reservations.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Co_resident_can_book_another_members_released_day_as_shared_capacity()
    {
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var spot = await SharedSpot(user, other);
        await Seed(db => db.SpotReleases.Add(new SpotRelease(spot.Id, other, Tomorrow, Now, 0)));
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        Assert.That((await Reservations().GetAvailableSpotsAsync(start, end)).Select(s => s.Id), Does.Contain(spot.Id));
        var day = (await Residents().GetMyOwnedSpotAsync(user))!.DaySchedule.Single(d => d.Date == Tomorrow);
        Assert.That(day.IsAssignedToCurrentUser, Is.False);
        Assert.That(day.CanReclaim, Is.False);
        Assert.That((await Reservations().ReserveAsync(user, spot.Id, start, end)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That((await db.Reservations.SingleAsync()).CountsTowardWeeklyLimit, Is.True);
    }

    [Test]
    public async Task Waiting_user_has_priority_even_before_matcher_runs()
    {
        var spot = await Spot();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-1));
        await Seed(db => db.QueueEntries.Add(queue));
        var result = await Reservations().ReserveAsync(Guid.NewGuid(), spot.Id, start, end);
        Assert.That(result.Errors, Does.Contain("Parking_Error_QueueHasPriority"));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.Reservations.CountAsync(), Is.Zero);
        Assert.That((await db.QueueEntries.SingleAsync()).Status, Is.EqualTo(QueueEntryStatus.Offered));
    }

    [Test]
    public async Task Manual_resident_release_immediately_offers_capacity_to_the_waiter()
    {
        var owner = Guid.NewGuid();
        var spot = await Spot(owner: owner);
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-1));
        await Seed(db => db.QueueEntries.Add(queue));
        Assert.That((await Residents(matchImmediately: true).ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That((await db.QueueEntries.SingleAsync()).OfferedSpotId, Is.EqualTo(spot.Id));
    }

    [Test]
    public async Task A_type_filtered_queue_does_not_pin_incompatible_capacity()
    {
        var motorcycle = await Spot(type: ParkingSpotType.Motorcycle);
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var user = Guid.NewGuid();
        Assert.That((await Reservations().JoinQueueAsync(user, start, end, ParkingSpotType.ElectricCharging)).Succeeded, Is.True);
        Assert.That(await Reservations().ProcessQueueAsync(), Is.Zero);
        var electric = await Spot(type: ParkingSpotType.ElectricCharging);
        Assert.That(await Reservations().ProcessQueueAsync(), Is.EqualTo(1));
        await using var db = new D3ParkingDbContext(_options);
        var queue = await db.QueueEntries.SingleAsync();
        Assert.That(queue.OfferedSpotId, Is.EqualTo(electric.Id));
        Assert.That(queue.RequiredSpotType, Is.EqualTo(ParkingSpotType.ElectricCharging));
    }

    [Test]
    public async Task Queue_skips_an_unaffordable_waiter_but_preserves_their_position()
    {
        await Spot();
        var policy = Policy with { BaseReservationCost = 10, MonthlyCreditAllowance = 10 };
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var poor = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-2));
        var next = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-1));
        var score = new ParkerScore(poor.UserId);
        score.GrantCreditIfDue(10, ParkerScore.PeriodOf(Now, policy.BudgetRenewalPeriod, TimeZoneInfo.Utc), Now);
        score.ChargeCredits(10, Now);
        await Seed(db => { db.ParkerScores.Add(score); db.QueueEntries.AddRange(poor, next); });
        Assert.That(await Reservations(policy).ProcessQueueAsync(), Is.EqualTo(1));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That((await db.QueueEntries.SingleAsync(q => q.Id == next.Id)).Status, Is.EqualTo(QueueEntryStatus.Offered));
        Assert.That((await db.QueueEntries.SingleAsync(q => q.Id == poor.Id)).CreatedAtUtc, Is.EqualTo(poor.CreatedAtUtc));
    }

    [TestCase(true, false), TestCase(true, true), TestCase(false, false)]
    public async Task Ending_a_started_day_preserves_quota_and_elapsed_history_while_future_cancellation_frees_quota(bool started, bool exactlyAtStart)
    {
        var spot = await Spot();
        var user = Guid.NewGuid();
        var date = started ? Today : Tomorrow;
        var (start, end) = SiteTime.Day(date, TimeZoneInfo.Utc);
        var policy = Policy with { WeeklyReservationLimitEnabled = true, WeeklyReservationLimit = 1 };
        var now = exactlyAtStart ? start : Now;
        var service = Reservations(policy, now: now);
        Assert.That((await service.ReserveAsync(user, spot.Id, start, end)).Succeeded, Is.True);
        var reservation = (await service.GetMyReservationsAsync(user)).Single();
        var preview = await service.PreviewEndAsync(user, reservation.Id);
        Assert.That(preview!.Started, Is.EqualTo(started));
        Assert.That((await service.CancelAsync(user, reservation.Id)).Succeeded, Is.True);
        var history = (await service.GetMyReservationsAsync(user)).Single();
        Assert.That(history.Status, Is.EqualTo(started ? ReservationStatus.Released : ReservationStatus.Cancelled));
        if (started) Assert.That(history.EffectiveEndUtc, Is.EqualTo(now));
        (start, end) = SiteTime.Day(Today.AddDays(2), TimeZoneInfo.Utc);
        var next = await service.ReserveAsync(user, spot.Id, start, end);
        Assert.That(next.Succeeded, Is.EqualTo(!started));
        if (started) Assert.That(next.Errors, Does.Contain("Parking_Error_WeeklyReservationLimit_NoLastMinute"));
    }

    [Test]
    public async Task Refund_deadline_is_frozen_at_booking_and_preview_matches_the_actual_refund()
    {
        var spot = await Spot();
        var user = Guid.NewGuid();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var original = Policy with { BaseReservationCost = 10, MonthlyCreditAllowance = 30, ReleaseCutoff = TimeSpan.FromHours(2) };
        var booked = Reservations(original);
        Assert.That((await booked.ReserveAsync(user, spot.Id, start, end)).Succeeded, Is.True);
        var reservation = (await booked.GetMyReservationsAsync(user)).Single();
        var changed = Reservations(original with { ReleaseCutoff = TimeSpan.FromDays(7) });
        var preview = await changed.PreviewEndAsync(user, reservation.Id);
        Assert.That(preview!.RefundCredits, Is.EqualTo(10));
        Assert.That(preview.RefundDeadlineUtc, Is.EqualTo(start.AddHours(-2)));
        Assert.That((await changed.CancelAsync(user, reservation.Id)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That((await db.ParkerScores.SingleAsync()).Credits, Is.EqualTo(30));
        Assert.That(await db.PointsLedgerEntries.CountAsync(e => e.Reason == IncentiveReason.ReservationRefund), Is.EqualTo(1));
    }

    [Test]
    public async Task Voucher_preview_does_not_promise_a_restore_when_holder_already_has_another_usable_voucher()
    {
        var spot = await Spot();
        var user = Guid.NewGuid();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var booking = new Reservation(spot.Id, user, start, end, false, Now);
        var redeemed = new ApologyVoucher(user, Guid.NewGuid(), Now, Now.AddDays(30));
        redeemed.Approve(Guid.NewGuid(), Now, TimeSpan.FromDays(30));
        redeemed.Redeem(booking.Id, 10, Now);
        await Seed(db => { db.Reservations.Add(booking); db.ApologyVouchers.AddRange(redeemed,
            new ApologyVoucher(user, Guid.NewGuid(), Now, Now.AddDays(30))); });
        Assert.That((await Reservations().PreviewEndAsync(user, booking.Id))!.RestoreVoucher, Is.False);
        Assert.That((await Reservations().CancelAsync(user, booking.Id)).Succeeded, Is.True);
        await using var check = new D3ParkingDbContext(_options);
        Assert.That((await check.ApologyVouchers.FindAsync(redeemed.Id))!.RedeemedReservationId, Is.EqualTo(booking.Id));
    }

    [TestCase(ParkingSpotType.Standard), TestCase(ParkingSpotType.ElectricCharging)]
    public async Task A_report_blocks_the_original_spot_and_relocates_only_to_the_same_type(ParkingSpotType type)
    {
        var blocked = await Spot("BLOCKED", type);
        var wrong = await Spot("A-WRONG", ParkingSpotType.Motorcycle);
        var right = await Spot("Z-RIGHT", type);
        var user = Guid.NewGuid();
        var (start, end) = SiteTime.Day(Today, TimeZoneInfo.Utc);
        var booking = new Reservation(blocked.Id, user, start, end, false, Now.AddDays(-1));
        await Seed(db => db.Reservations.Add(booking));
        var result = await Reservations().ReportBlockedSpotAsync(user, booking.Id, true, Photo());
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(result.RelocatedToSpotCode, Is.EqualTo(right.Code));
        Assert.That((await Reservations().GetAvailableSpotsAsync(start, end)).Select(s => s.Id), Does.Not.Contain(blocked.Id));
        Assert.That((await Reservations().ReserveAsync(Guid.NewGuid(), blocked.Id, start, end)).Errors,
            Does.Contain("Parking_Error_SpotTemporarilyBlocked"));
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now, type);
        await Seed(db => db.QueueEntries.Add(queue));
        Assert.That(await Reservations().ProcessQueueAsync(), Is.Zero);
    }

    [Test]
    public async Task An_implicit_resident_can_report_and_relocate_without_retaining_two_capacity_claims()
    {
        var owner = Guid.NewGuid();
        var own = await Spot("OWN", owner: owner);
        var replacement = await Spot("REPLACEMENT");
        var result = await Reservations().ReportBlockedResidentSpotAsync(owner, own.Id, true, Photo());
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(result.RelocatedToSpotCode, Is.EqualTo(replacement.Code));
        var state = await Residents().GetMyOwnedSpotAsync(owner);
        Assert.That(state!.TodayState, Is.EqualTo(OwnedSpotDayState.Unavailable));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.SpotReleases.AnyAsync(r => r.SpotId == own.Id && r.Date == Today), Is.True);
        Assert.That(await db.Reservations.CountAsync(r => r.UserId == owner && r.Status == ReservationStatus.Reserved), Is.EqualTo(1));
        Assert.That((await db.Reservations.SingleAsync(r => r.Status == ReservationStatus.Reserved)).CountsTowardWeeklyLimit, Is.False);
    }

    [Test]
    public async Task Only_an_authorized_manager_can_clear_a_physical_block_before_its_stored_end()
    {
        var spot = await Spot();
        var (start, end) = SiteTime.Day(Today, TimeZoneInfo.Utc);
        await Seed(db => db.OccupancyMismatches.Add(new OccupancyMismatch(spot.Id, Guid.NewGuid(), Guid.NewGuid(), start, end, Now)));
        var manager = await Actor(Permissions.Parking.ManageSpots);
        var service = Spots();
        Assert.That((await service.ResolveTemporaryBlockAsync(spot.Id, Guid.NewGuid())).Succeeded, Is.False);
        Assert.That((await Reservations().GetAvailableSpotsAsync(start, end)), Is.Empty);
        Assert.That((await service.ResolveTemporaryBlockAsync(spot.Id, manager)).Succeeded, Is.True);
        Assert.That((await Reservations().GetAvailableSpotsAsync(start, end)).Single().Id, Is.EqualTo(spot.Id));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That((await db.OccupancyMismatches.SingleAsync()).ResolvedAtUtc, Is.EqualTo(Now));
        Assert.That(await db.AccountAuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Deactivation_is_visible_and_notified_to_an_implicit_resident()
    {
        var owner = Guid.NewGuid();
        var spot = await Spot(owner: owner);
        Assert.That((await Spots().SetActiveAsync(spot.Id, false)).Succeeded, Is.True);
        var owned = await Residents().GetMyOwnedSpotAsync(owner);
        Assert.That(owned!.IsActive, Is.False);
        Assert.That(owned.TodayState, Is.EqualTo(OwnedSpotDayState.Unavailable));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.Notifications.AnyAsync(n => n.UserId == owner), Is.True);
    }

    [Test]
    public async Task Queue_offer_inbox_and_email_are_atomic_and_failure_leaves_no_delivery_or_hold()
    {
        await Spot();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now);
        await Seed(db => db.QueueEntries.Add(queue));
        var failing = new DbContextOptionsBuilder<D3ParkingDbContext>(_options).AddInterceptors(new FailAfterSave()).Options;
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Reservations(options: failing).ProcessQueueAsync());
        await using (var db = new D3ParkingDbContext(_options))
        {
            Assert.That((await db.QueueEntries.SingleAsync()).Status, Is.EqualTo(QueueEntryStatus.Waiting));
            Assert.That(await db.Notifications.CountAsync(), Is.Zero);
            Assert.That(await db.NotificationEmailDeliveries.CountAsync(), Is.Zero);
        }
        Assert.That(await Reservations().ProcessQueueAsync(), Is.EqualTo(1));
        Assert.That(await Reservations().ProcessQueueAsync(), Is.Zero);
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.Notifications.CountAsync(), Is.EqualTo(1));
        Assert.That(await check.NotificationEmailDeliveries.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Muting_external_delivery_cannot_erase_the_parking_inbox_history()
    {
        await Spot();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now);
        var prefs = new NotificationPreferences(queue.UserId);
        prefs.MuteIndefinitely();
        await Seed(db =>
        {
            db.QueueEntries.Add(queue); db.NotificationPreferences.Add(prefs);
            db.NotificationDeliveryRules.Add(new NotificationDeliveryRule(NotificationCategory.SelfService,
                NotificationLevel.Warning, false, false, NotificationEmailMode.Always));
        });
        Assert.That(await Reservations().ProcessQueueAsync(), Is.EqualTo(1));
        await using var check = new D3ParkingDbContext(_options);
        Assert.That(await check.Notifications.CountAsync(n => n.UserId == queue.UserId), Is.EqualTo(1));
        Assert.That(await check.NotificationEmailDeliveries.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task A_short_remaining_window_caps_the_offer_deadline_at_the_booking_end()
    {
        await Spot();
        var queue = new QueueEntry(Guid.NewGuid(), Now.AddHours(-1), Now.AddMinutes(3), Now.AddHours(-2));
        await Seed(db => db.QueueEntries.Add(queue));
        Assert.That(await Reservations(Policy with { QueueOfferMinutes = 30 }).ProcessQueueAsync(), Is.EqualTo(1));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That((await db.QueueEntries.SingleAsync()).OfferExpiresAtUtc, Is.EqualTo(queue.EndUtc));
    }

    [Test]
    public async Task Undelivered_offer_releases_capacity_without_demoting_or_repeatedly_blocking_the_waiter()
    {
        await Spot();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var first = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-2));
        var next = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-1));
        await Seed(db => db.QueueEntries.AddRange(first, next));
        Assert.That(await Reservations().ProcessQueueAsync(), Is.EqualTo(1));
        Guid deliveryId;
        await using (var db = new D3ParkingDbContext(_options))
        {
            deliveryId = (await db.QueueEntries.FindAsync(first.Id))!.OfferEmailDeliveryId!.Value;
            (await db.NotificationEmailDeliveries.FindAsync(deliveryId))!.MarkFailed(Now, "SMTP unavailable");
            await db.SaveChangesAsync();
        }
        Assert.That(await Reservations(now: Now.AddMinutes(31)).ProcessQueueAsync(), Is.EqualTo(1));
        await using (var db = new D3ParkingDbContext(_options))
        {
            var waiting = (await db.QueueEntries.FindAsync(first.Id))!;
            Assert.That(waiting.Status, Is.EqualTo(QueueEntryStatus.Waiting));
            Assert.That(waiting.CreatedAtUtc, Is.EqualTo(first.CreatedAtUtc));
            Assert.That((await db.QueueEntries.FindAsync(next.Id))!.Status, Is.EqualTo(QueueEntryStatus.Offered));
            (await db.QueueEntries.FindAsync(next.Id))!.Cancel();
            await db.SaveChangesAsync();
        }
        Assert.That(await Reservations(now: Now.AddMinutes(32)).ProcessQueueAsync(), Is.Zero,
            "A failed delivery must not produce a fresh unclaimable hold every maintenance sweep.");
        await using (var db = new D3ParkingDbContext(_options))
        {
            (await db.NotificationEmailDeliveries.FindAsync(deliveryId))!.MarkSent(Now.AddMinutes(33));
            await db.SaveChangesAsync();
        }
        Assert.That(await Reservations(now: Now.AddMinutes(34)).ProcessQueueAsync(), Is.EqualTo(1));
        await using var check = new D3ParkingDbContext(_options);
        Assert.That((await check.QueueEntries.FindAsync(first.Id))!.Status, Is.EqualTo(QueueEntryStatus.Offered));
    }

    [Test]
    public async Task A_deactivated_offer_is_withdrawn_without_losing_its_fifo_position()
    {
        var spot = await Spot();
        var (start, end) = SiteTime.Day(Tomorrow, TimeZoneInfo.Utc);
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now.AddMinutes(-1));
        queue.Offer(spot.Id, Now.AddMinutes(30));
        await Seed(db => { db.QueueEntries.Add(queue); db.ParkingSpots.Single(s => s.Id == spot.Id).Deactivate(); });
        Assert.That(await Reservations().ProcessQueueAsync(), Is.Zero);
        await using var db = new D3ParkingDbContext(_options);
        var saved = await db.QueueEntries.SingleAsync();
        Assert.That(saved.Status, Is.EqualTo(QueueEntryStatus.Waiting));
        Assert.That(saved.CreatedAtUtc, Is.EqualTo(queue.CreatedAtUtc));
        Assert.That(await db.Notifications.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Occupancy_includes_implicit_residents_queue_holds_and_physical_blocks()
    {
        var resident = await Spot("HELD", owner: Guid.NewGuid());
        var held = await Spot("QUEUE");
        var blocked = await Spot("BLOCKED");
        var free = await Spot("FREE");
        var (start, end) = SiteTime.Day(Today, TimeZoneInfo.Utc);
        var queue = new QueueEntry(Guid.NewGuid(), start, end, Now);
        queue.Offer(held.Id, Now.AddMinutes(30));
        await Seed(db => { db.QueueEntries.Add(queue); db.OccupancyMismatches.Add(
            new OccupancyMismatch(blocked.Id, Guid.NewGuid(), Guid.NewGuid(), start, end, Now)); });
        var quote = await Reservations().GetQuoteAsync(Guid.NewGuid(), start, end);
        Assert.That(quote.OccupancyPercent, Is.EqualTo(75));
        Assert.That((await Reservations().GetAvailableSpotsAsync(start, end)).Single().Id, Is.EqualTo(free.Id));
    }

    [Test]
    public async Task Bulk_release_preview_counts_only_assigned_dates_and_names_the_actual_next_assigned_day()
    {
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        await SharedSpot(user, other);
        var preview = await Residents().PreviewReleaseAsync(user, Today, Tomorrow);
        Assert.That(preview.Error, Is.Null);
        Assert.That(preview.Dates, Is.EqualTo(new[] { Today }));
        Assert.That(preview.NextAssignedDate, Is.EqualTo(Today.AddDays(2)));
        Assert.That((await Residents().ReleaseAsync(user, Today, Tomorrow)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.SpotReleases.Select(r => r.Date).ToListAsync(), Is.EqualTo(preview.Dates));
    }

    private ReservationService Reservations(IncentivePolicy? policy = null, DbContextOptions<D3ParkingDbContext>? options = null,
        DateTimeOffset? now = null) =>
        new(new TestDbContextFactory(options ?? _options), new FakeParkingSettings(policy ?? Policy), new FakeSiteSettings(),
            new FixedTimeProvider(now ?? Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>());
    private ResidentSpotService Residents(bool matchImmediately = false) =>
        new(new TestDbContextFactory(_options), new FakeParkingSettings(Policy), new FakeSiteSettings(),
            new FixedTimeProvider(Now), new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>(),
            matchImmediately ? Reservations() : null);
    private ParkingSpotService Spots() => new(new TestDbContextFactory(_options), new NullNotificationService(),
        new FakeParkingSettings(Policy), new FakeSiteSettings(), new FixedTimeProvider(Now), new PassthroughLocalizer<ParkingMessages>());
    private async Task<ParkingSpot> Spot(string? code = null, ParkingSpotType type = ParkingSpotType.Standard, Guid? owner = null)
    {
        var spot = new ParkingSpot(code ?? $"S-{Guid.NewGuid():N}"[..12], type);
        if (owner is { } id) spot.AssignOwner(id);
        await Seed(db => db.ParkingSpots.Add(spot));
        return spot;
    }
    private async Task<ParkingSpot> SharedSpot(Guid firstUser, Guid secondUser)
    {
        var spot = await Spot();
        var first = new ParkingSpotResident(spot.Id, firstUser, Now.AddDays(-2));
        var second = new ParkingSpotResident(spot.Id, secondUser, Now.AddDays(-1));
        await Seed(db =>
        {
            db.ParkingSpotResidents.AddRange(first, second);
            for (var i = 0; i < 8; i++) db.SpotDayAssignments.Add(new SpotDayAssignment(spot.Id,
                i % 2 == 0 ? first.Id : second.Id, Today.AddDays(i), Now));
        });
        return spot;
    }
    private async Task<Guid> Actor(string permission)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"actor-{Guid.NewGuid():N}", Status = AccountStatus.Active };
        var role = new ApplicationRole($"role-{Guid.NewGuid():N}");
        await Seed(db =>
        {
            db.Users.Add(user); db.Roles.Add(role);
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            db.RoleClaims.Add(new IdentityRoleClaim<Guid> { RoleId = role.Id, ClaimType = D3ParkingClaimTypes.Permission, ClaimValue = permission });
        });
        return user.Id;
    }
    private async Task Seed(Action<D3ParkingDbContext> seed)
    {
        await using var db = new D3ParkingDbContext(_options);
        seed(db); await db.SaveChangesAsync();
    }
    private static BlockedSpotPhoto Photo() => new([0xff, 0xd8, 0xff, 0xe0, .. Guid.NewGuid().ToByteArray()], "image/jpeg");
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected failure before commit.");
    }
}
