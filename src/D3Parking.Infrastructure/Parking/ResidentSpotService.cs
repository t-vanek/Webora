using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Common;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class ResidentSpotService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : IResidentSpotService
{
    public async Task<OwnedSpotDto?> GetMyOwnedSpotAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return null;
        }

        var (dayStart, dayEnd) = SiteTime.Day(today, timeZone);
        var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
        var takenByOther = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId != userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.EndUtc > now
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);

        OwnedSpotDayState state;
        if (takenByOther)
        {
            state = OwnedSpotDayState.SharedTaken;
        }
        else if (releasedToday)
        {
            state = OwnedSpotDayState.SharedFree;
        }
        else
        {
            state = OwnedSpotDayState.Held;
        }

        // The shown potential is exactly what ReleaseAsync(today, today) would award, including the
        // same skipped-day and advance-notice rules.
        var potential = (await PlanReleaseRewardsAsync(dbContext, spot, userId, today, today, policy, timeZone, now, cancellationToken))
            .Sum(day => day.Points);

        // Today-or-later released days, each marked with whether taking it back will displace a
        // guest plan. Both free and taken days remain reclaimable by the assigned resident.
        var upcomingDates = await dbContext.SpotReleases
            .Where(r => r.SpotId == spot.Id && r.Date >= today)
            .OrderBy(r => r.Date)
            .Select(r => r.Date)
            .ToListAsync(cancellationToken);
        var upcoming = new List<ReleasedDayDto>(upcomingDates.Count);
        if (upcomingDates.Count > 0)
        {
            var (horizonStart, _) = SiteTime.Day(upcomingDates[0], timeZone);
            var (_, horizonEnd) = SiteTime.Day(upcomingDates[^1], timeZone);
            var guestBookings = await dbContext.Reservations
                .Where(r => r.SpotId == spot.Id && r.UserId != userId
                    && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                    && r.EndUtc > now
                    && r.StartUtc < horizonEnd && r.EndUtc > horizonStart)
                .Select(r => new { r.StartUtc, r.EndUtc })
                .ToListAsync(cancellationToken);
            foreach (var date in upcomingDates)
            {
                var (start, end) = SiteTime.Day(date, timeZone);
                upcoming.Add(new ReleasedDayDto(date, guestBookings.Any(b => b.StartUtc < end && b.EndUtc > start)));
            }
        }

        return new OwnedSpotDto(spot.Id, spot.Code, spot.Type, state, releasedToday, potential, upcoming,
            spot.PlannedUseDays, spot.AutoReleaseUnplannedDays,
            policy.ResidentPlanHorizonEnd(today).DayNumber - today.DayNumber);
    }

    public Task<ParkingResult> ReleaseAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReleaseCoreAsync(userId, fromDate, toDate, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ReleaseCoreAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        if (toDate < fromDate)
        {
            return ParkingResult.Failure("Parking_Error_InvalidRange");
        }

        if (fromDate < today)
        {
            return ParkingResult.Failure("Parking_Error_PastDate");
        }

        if (toDate.DayNumber - fromDate.DayNumber >= policy.MaxReleaseRangeDays)
        {
            return ParkingResult.Failure("Parking_Error_RangeTooLong");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The read and inserts stay atomic so two concurrent releases cannot both reward the same day.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        var plan = await PlanReleaseRewardsAsync(dbContext, spot, userId, fromDate, toDate, policy, timeZone, now, cancellationToken);
        if (plan.Count == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToRelease");
        }

        await AwardReleasePlanAsync(dbContext, spot, userId, plan, now, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            // A concurrent release landed the same day between our check and our save; the unique
            // (SpotId, Date) index is the last line of defence. The days are released either way.
            // Anything else (a lost deadlock) propagates to the retry wrapper for a fresh attempt.
            return ParkingResult.Failure("Parking_Error_AlreadyReleased");
        }

        return ParkingResult.Success;
    }

    public async Task<int> PreviewReleaseRewardAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        // A range ReleaseAsync would reject outright awards nothing, so it previews as nothing —
        // the same checks in the same order.
        if (toDate < fromDate || fromDate < today || toDate.DayNumber - fromDate.DayNumber >= policy.MaxReleaseRangeDays)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return 0;
        }

        // No serializable transaction here: the preview is advisory and the actual award is
        // re-derived inside ReleaseCoreAsync's transaction.
        var plan = await PlanReleaseRewardsAsync(dbContext, spot, userId, fromDate, toDate, policy, timeZone, now, cancellationToken);
        return plan.Sum(day => day.Points);
    }

    /// <summary>
    /// Writes a planned release: one <see cref="SpotRelease"/> per day, plus the reward and its ledger
    /// entry for the days that earn one. Shared by the manual release and the usage planner so a
    /// planned day is credited exactly like a hand-picked one.
    /// </summary>
    private static async Task AwardReleasePlanAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot, Guid ownerId,
        List<(DateOnly Date, int Points)> plan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ParkerScore? score = null;
        foreach (var (date, points) in plan)
        {
            dbContext.SpotReleases.Add(new SpotRelease(spot.Id, ownerId, date, now, points));
            if (points > 0)
            {
                score ??= await GetOrCreateScoreAsync(dbContext, ownerId, cancellationToken);
                score.RewardSharing(points, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    ownerId, IncentiveReason.ResidentSpotShared, points, null, now, $"{spot.Code} {date:yyyy-MM-dd}"));
            }
        }
    }

    /// <summary>
    /// The days in [fromDate, toDate] a release would newly share, each with the points it would
    /// earn. The single home of the reward maths — the actual release, its UI preview and the
    /// owned-spot card all consume this plan, so the promise and the payout cannot drift apart.
    /// <paramref name="include"/> narrows the range to a subset of its days (the usage planner passes
    /// "not a planned-use weekday").
    /// </summary>
    private static async Task<List<(DateOnly Date, int Points)>> PlanReleaseRewardsAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot, Guid userId, DateOnly fromDate, DateOnly toDate,
        IncentivePolicy policy, TimeZoneInfo timeZone, DateTimeOffset now, CancellationToken cancellationToken,
        Func<DateOnly, bool>? include = null)
    {
        var alreadyReleased = (await dbContext.SpotReleases
            .Where(r => r.SpotId == spot.Id && r.Date >= fromDate && r.Date <= toDate)
            .Select(r => r.Date)
            .ToListAsync(cancellationToken)).ToHashSet();

        var (rangeStart, _) = SiteTime.Day(fromDate, timeZone);
        var (_, rangeEnd) = SiteTime.Day(toDate, timeZone);
        // A day the resident has taken for themselves cannot also be shared — a merely reserved (not
        // yet arrived) booking counts too, otherwise they could be paid for "sharing" a spot their
        // own reservation still blocks.
        var claimedDays = (await dbContext.Reservations
            .Where(r => r.SpotId == spot.Id && r.UserId == userId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken))
            // A reservation can span local days; every covered day is claimed, not just the first —
            // otherwise the tail days could be released (and rewarded) while still self-occupied.
            .SelectMany(r =>
            {
                var first = SiteTime.Today(r.StartUtc, timeZone);
                var last = SiteTime.Today(r.EndUtc.AddTicks(-1), timeZone);
                return Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays);
            })
            .ToHashSet();

        var plan = new List<(DateOnly Date, int Points)>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (alreadyReleased.Contains(date) || claimedDays.Contains(date) || include?.Invoke(date) == false)
            {
                continue;
            }

            var points = policy.ComputeShareReward(policy.ResidentShareCutoff(date, timeZone), now);
            plan.Add((date, points));
        }

        return plan;
    }

    public Task<ParkingResult> ReclaimAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReclaimCoreAsync(userId, fromDate, toDate, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ReclaimCoreAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        if (toDate < fromDate)
        {
            return ParkingResult.Failure("Parking_Error_InvalidRange");
        }

        if (fromDate < today)
        {
            return ParkingResult.Failure("Parking_Error_PastDate");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The guest cancellation and release deletion are one serializable step. This gives the
        // resident final claim without ever leaving a live guest plan on a day that is no longer shared.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        var releases = await dbContext.SpotReleases
            .Where(r => r.SpotId == spot.Id && r.Date >= fromDate && r.Date <= toDate)
            .ToListAsync(cancellationToken);
        if (releases.Count == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToReclaim");
        }

        var (rangeStart, _) = SiteTime.Day(fromDate, timeZone);
        var (_, rangeEnd) = SiteTime.Day(toDate, timeZone);
        var guestBookings = await dbContext.Reservations
            .Where(r => r.SpotId == spot.Id && r.UserId != userId
                && r.Status == ReservationStatus.Reserved
                && r.EndUtc > now
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .ToListAsync(cancellationToken);

        // Every released day in the requested range returns to the resident. If a guest planned
        // the spot meanwhile, the plan is cancelled as a no-fault override with a full refund.
        ParkerScore? score = null;
        foreach (var release in releases)
        {
            if (release.AwardedPoints > 0)
            {
                score ??= await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
                score.RevokeSharePoints(release.AwardedPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ResidentShareReclaimed, -release.AwardedPoints, null, now,
                    $"{spot.Code} {release.Date:yyyy-MM-dd}"));
            }
        }

        var displaced = new List<(Guid UserId, int RefundedCredits)>();
        foreach (var reservation in guestBookings)
        {
            var overlapsReclaimedDay = releases.Any(release =>
            {
                var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
                return reservation.StartUtc < dayEnd && reservation.EndUtc > dayStart;
            });
            if (!overlapsReclaimedDay)
            {
                continue;
            }

            reservation.Cancel();
            if (reservation.CreditsCharged > 0)
            {
                var guestScore = await GetOrCreateScoreAsync(dbContext, reservation.UserId, cancellationToken);
                guestScore.RefundCredits(reservation.CreditsCharged, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId, IncentiveReason.ReservationRefund, reservation.CreditsCharged,
                    reservation.Id, now, $"Resident reclaimed {spot.Code}"));
            }

            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);
            dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
                reservation.UserId, AccountAuditEventType.ReservationOverridden, $"resident:{userId}",
                $"Resident reclaimed spot {spot.Code}; cancelled reservation {reservation.Id} " +
                $"({reservation.StartUtc:u}–{reservation.EndUtc:u}) and refunded {reservation.CreditsCharged} credits.",
                now));
            displaced.Add((reservation.UserId, reservation.CreditsCharged));
        }

        dbContext.SpotReleases.RemoveRange(releases);

        // A waitlist hold is only a pending offer, not a booking — the resident's right of first
        // refusal outranks it. The withdrawn waiter keeps their queue position (no requeue) and
        // the maintenance loop hands them the next freed spot.
        var reclaimedDays = releases.Select(r => r.Date).ToHashSet();
        var withdrawn = new List<Guid>();
        var holds = await dbContext.QueueEntries
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId == spot.Id
                && q.StartUtc < rangeEnd && q.EndUtc > rangeStart)
            .ToListAsync(cancellationToken);
        foreach (var hold in holds)
        {
            var first = SiteTime.Today(hold.StartUtc, timeZone);
            var last = SiteTime.Today(hold.EndUtc.AddTicks(-1), timeZone);
            for (var date = first; date <= last; date = date.AddDays(1))
            {
                if (reclaimedDays.Contains(date))
                {
                    hold.WithdrawOffer();
                    withdrawn.Add(hold.UserId);
                    break;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var waiterId in withdrawn)
        {
            await notifications.NotifyAsync(waiterId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_QueueHoldReclaimed_Title"],
                messages["Parking_Notify_QueueHoldReclaimed_Body", spot.Code],
                cancellationToken);
        }

        foreach (var guest in displaced)
        {
            await notifications.NotifyAsync(guest.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_ResidentReclaimed_Title"],
                messages.ForEconomy(policy, "Parking_Notify_ResidentReclaimed_Body", spot.Code, guest.RefundedCredits),
                cancellationToken);
        }

        return ParkingResult.Success;
    }

    private static async Task RestoreVoucherAsync(
        D3ParkingDbContext dbContext,
        Guid reservationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var voucher = await dbContext.ApologyVouchers
            .FirstOrDefaultAsync(v => v.RedeemedReservationId == reservationId, cancellationToken);
        if (voucher is null)
        {
            return;
        }

        var holdsAnotherUsable = await dbContext.ApologyVouchers.AnyAsync(v =>
            v.UserId == voucher.UserId && v.Id != voucher.Id
            && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now
            && (v.Status == ApologyVoucherStatus.PendingApproval || v.Status == ApologyVoucherStatus.Approved),
            cancellationToken);
        if (!holdsAnotherUsable)
        {
            voucher.Restore();
        }
    }

    public async Task<ParkingResult> SetUsagePlanAsync(Guid userId, Weekday plannedUseDays,
        bool autoReleaseUnplannedDays, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        spot.SetUsagePlan(plannedUseDays, autoReleaseUnplannedDays);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<int> ApplyDuePlanReleasesAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);
        var horizonEnd = policy.ResidentPlanHorizonEnd(today);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Inactive spots never reach the pool, so planning them would only produce release rows
        // nobody can book.
        var candidates = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.OwnerId != null && s.AutoReleaseUnplannedDays)
            .Select(s => new { s.Id, s.PlanAppliedThrough })
            .ToListAsync(cancellationToken);

        var released = 0;
        foreach (var candidate in candidates)
        {
            // The watermark already covers the whole horizon: nothing new came into view since the
            // last run. This is what keeps a five-minute sweep from re-deciding the same days.
            if (candidate.PlanAppliedThrough >= horizonEnd)
            {
                continue;
            }

            // Per spot, so one resident losing a race (or hitting the unique (SpotId, Date) index
            // against a manual release) cannot roll back or skip the others.
            released += await OptimisticConcurrency.RetryAsync(
                () => ApplyPlanForSpotAsync(candidate.Id, today, horizonEnd, policy, timeZone, now, cancellationToken),
                cancellationToken);
        }

        return released;
    }

    private async Task<int> ApplyPlanForSpotAsync(Guid spotId, DateOnly today, DateOnly horizonEnd,
        IncentivePolicy policy, TimeZoneInfo timeZone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Same protection as ReleaseCoreAsync, for the same reason: the monthly-cap read inside
        // PlanReleaseRewardsAsync and the reward inserts have to be one atomic step, or the planner
        // and a manual release running together cannot both award the same day.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        // Re-checked inside the transaction: the scan is a separate read, and between the two the
        // admin may have deactivated or reassigned the spot, or the resident switched the plan off.
        if (spot is null || !spot.IsActive || spot.OwnerId is not { } ownerId || !spot.AutoReleaseUnplannedDays)
        {
            return 0;
        }

        // The plan is authoritative from today onward. A planned-use day stays held; an unplanned
        // day is released explicitly, so no same-day arrival confirmation is required.
        var fromDate = spot.PlanAppliedThrough is { } appliedThrough
            ? Max(today, appliedThrough.AddDays(1))
            : today;
        if (fromDate > horizonEnd)
        {
            return 0;
        }

        var plan = await PlanReleaseRewardsAsync(dbContext, spot, ownerId, fromDate, horizonEnd,
            policy, timeZone, now, cancellationToken, date => !spot.PlannedUseDays.Includes(date));

        await AwardReleasePlanAsync(dbContext, spot, ownerId, plan, now, cancellationToken);
        // Advanced even when the plan released nothing (every new day is a planned-use one), so the
        // horizon is not rescanned until it moves on again.
        spot.MarkPlanApplied(horizonEnd);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (plan.Count == 0)
        {
            return 0;
        }

        await notifications.NotifyAsync(ownerId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages["Parking_Notify_PlanReleased_Title"],
            messages.ForEconomy(policy, "Parking_Notify_PlanReleased_Body", spot.Code, plan.Count, plan.Sum(day => day.Points)),
            cancellationToken);

        return plan.Count;
    }

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;

    public async Task<int> ReconcileUnusedSharesAsync(CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var due = await dbContext.SpotReleases
            .Where(r => r.ReconciledAtUtc == null && r.AwardedPoints > 0 && r.Date < today)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var spotIds = due.Select(r => r.SpotId).Distinct().ToList();
        var codes = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        var notices = new List<(Guid OwnerId, string Code, int Points)>();
        foreach (var release in due)
        {
            release.MarkReconciled(now);

            var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
            // A booking by someone else that day means there was demand for the spot; only a day
            // nobody ever booked counts as an unused share and reverses the reward. The resident's
            // own booking is excluded — it does not make the spot available to anyone, so it must
            // not shield the reward from being reversed. Cancelled and legacy no-show bookings still
            // count as evidence that somebody requested the shared capacity.
            var hadDemand = await dbContext.Reservations.AnyAsync(r => r.SpotId == release.SpotId
                && r.UserId != release.OwnerId
                && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
            if (hadDemand)
            {
                continue;
            }

            var score = await GetOrCreateScoreAsync(dbContext, release.OwnerId, cancellationToken);
            score.RevokeSharePoints(release.AwardedPoints, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                release.OwnerId, IncentiveReason.ResidentShareUnused, -release.AwardedPoints, null, now,
                $"{codes.GetValueOrDefault(release.SpotId, string.Empty)} {release.Date:yyyy-MM-dd}"));
            notices.Add((release.OwnerId, codes.GetValueOrDefault(release.SpotId, string.Empty), release.AwardedPoints));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var (ownerId, code, points) in notices)
        {
            await notifications.NotifyAsync(ownerId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_ShareUnused_Title"],
                messages["Parking_Notify_ShareUnused_Body", code, points],
                cancellationToken);
        }

        return notices.Count;
    }

    private static async Task<ParkerScore> GetOrCreateScoreAsync(D3ParkingDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var score = await dbContext.ParkerScores.FindAsync([userId], cancellationToken);
        if (score is null)
        {
            score = new ParkerScore(userId);
            dbContext.ParkerScores.Add(score);
        }

        return score;
    }
}
