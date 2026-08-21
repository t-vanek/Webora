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
        var resident = await FindResidentSpotAsync(dbContext, userId, cancellationToken);
        if (resident is null)
        {
            return null;
        }
        var spot = resident.Spot;
        var horizonEnd = policy.ResidentPlanHorizonEnd(today);
        var assignedDates = await AssignedDatesAsync(dbContext, spot, userId, today, horizonEnd, cancellationToken);
        var assignedToday = assignedDates.Contains(today);

        var (dayStart, dayEnd) = SiteTime.Day(today, timeZone);
        var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
        var takenByOther = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId != userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.EndUtc > now
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);

        OwnedSpotDayState state;
        if (!assignedToday)
        {
            state = OwnedSpotDayState.NotAssigned;
        }
        else if (takenByOther)
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
        var potential = (await PlanReleaseRewardsAsync(dbContext, spot, userId, today, today, policy, timeZone, now, cancellationToken,
                date => assignedDates.Contains(date)))
            .Sum(day => day.Points);

        // Today-or-later released days, each marked so the UI can keep confirmed guest plans
        // read-only while still allowing an unbooked day to return to the resident.
        var upcomingDates = await dbContext.SpotReleases
            .Where(r => r.SpotId == spot.Id && r.OwnerId == userId && r.Date >= today)
            .OrderBy(r => r.Date)
            .Select(r => r.Date)
            .ToListAsync(cancellationToken);
        var upcoming = new List<ReleasedDayDto>(upcomingDates.Count);
        if (upcomingDates.Count > 0)
        {
            var (horizonStart, _) = SiteTime.Day(upcomingDates[0], timeZone);
            var (_, releaseHorizonEnd) = SiteTime.Day(upcomingDates[^1], timeZone);
            var guestBookings = await dbContext.Reservations
                .Where(r => r.SpotId == spot.Id && r.UserId != userId
                    && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                    && r.EndUtc > now
                    && r.StartUtc < releaseHorizonEnd && r.EndUtc > horizonStart)
                .Select(r => new { r.StartUtc, r.EndUtc })
                .ToListAsync(cancellationToken);
            foreach (var date in upcomingDates)
            {
                var (start, end) = SiteTime.Day(date, timeZone);
                upcoming.Add(new ReleasedDayDto(date, guestBookings.Any(b => b.StartUtc < end && b.EndUtc > start)));
            }
        }

        return new OwnedSpotDto(spot.Id, spot.Code, spot.Type, state, releasedToday, potential, upcoming,
            resident.Membership?.PlannedUseDays ?? spot.PlannedUseDays,
            resident.Membership?.AutoReleaseUnplannedDays ?? spot.AutoReleaseUnplannedDays,
            horizonEnd.DayNumber - today.DayNumber, assignedDates.Order().ToList());
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

        if (toDate > policy.ResidentPlanHorizonEnd(today))
        {
            return ParkingResult.Failure("Parking_Error_ReservationHorizon");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The read and inserts stay atomic so two concurrent releases cannot both reward the same day.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var resident = await FindResidentSpotAsync(dbContext, userId, cancellationToken);
        if (resident is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }
        var spot = resident.Spot;
        var assignedDates = await AssignedDatesAsync(dbContext, spot, userId, fromDate, toDate, cancellationToken);

        var plan = await PlanReleaseRewardsAsync(dbContext, spot, userId, fromDate, toDate, policy, timeZone, now, cancellationToken,
            date => assignedDates.Contains(date));
        if (plan.Count == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToRelease");
        }

        await AwardReleasePlanAsync(dbContext, spot, userId, plan, now, SpotReleaseSource.Manual, cancellationToken);

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
        if (toDate < fromDate || fromDate < today
            || toDate.DayNumber - fromDate.DayNumber >= policy.MaxReleaseRangeDays
            || toDate > policy.ResidentPlanHorizonEnd(today))
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var resident = await FindResidentSpotAsync(dbContext, userId, cancellationToken);
        if (resident is null)
        {
            return 0;
        }
        var spot = resident.Spot;
        var assignedDates = await AssignedDatesAsync(dbContext, spot, userId, fromDate, toDate, cancellationToken);

        // No serializable transaction here: the preview is advisory and the actual award is
        // re-derived inside ReleaseCoreAsync's transaction.
        var plan = await PlanReleaseRewardsAsync(dbContext, spot, userId, fromDate, toDate, policy, timeZone, now, cancellationToken,
            date => assignedDates.Contains(date));
        return plan.Sum(day => day.Points);
    }

    /// <summary>
    /// Writes a planned release: one <see cref="SpotRelease"/> per day, plus the reward and its ledger
    /// entry for the days that earn one. Shared by the manual release and the usage planner so a
    /// planned day is credited exactly like a hand-picked one.
    /// </summary>
    private static async Task AwardReleasePlanAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot, Guid ownerId,
        List<(DateOnly Date, int Points)> plan, DateTimeOffset now, SpotReleaseSource source,
        CancellationToken cancellationToken)
    {
        ParkerScore? score = null;
        foreach (var (date, points) in plan)
        {
            dbContext.SpotReleases.Add(new SpotRelease(spot.Id, ownerId, date, now, points, source));
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
            if (!policy.IsReservationDateAllowed(date)
                || alreadyReleased.Contains(date)
                || claimedDays.Contains(date)
                || include?.Invoke(date) == false)
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

        // The overlap check and release deletion are one serializable step, so a reservation cannot
        // slip in between confirming that a released day is free and returning it to the resident.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var resident = await FindResidentSpotAsync(dbContext, userId, cancellationToken);
        if (resident is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }
        var spot = resident.Spot;

        var releases = await dbContext.SpotReleases
            .Where(r => r.SpotId == spot.Id && r.OwnerId == userId && r.Date >= fromDate && r.Date <= toDate)
            .ToListAsync(cancellationToken);
        if (releases.Count == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToReclaim");
        }

        var (rangeStart, _) = SiteTime.Day(fromDate, timeZone);
        var (_, rangeEnd) = SiteTime.Day(toDate, timeZone);
        var guestBookings = await dbContext.Reservations
            .Where(r => r.SpotId == spot.Id && r.UserId != userId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.EndUtc > now
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .ToListAsync(cancellationToken);

        var movedGuests = new List<(Guid UserId, string ToCode)>();
        var queuedGuests = new List<Guid>();
        var cancelledGuests = new List<Guid>();
        var assignedReplacements = new List<(Guid SpotId, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var reservation in guestBookings)
        {
            var overlappingReleases = releases.Where(release =>
            {
                var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
                return reservation.StartUtc < dayEnd && reservation.EndUtc > dayStart;
            }).ToList();
            if (overlappingReleases.Count == 0)
            {
                continue;
            }

            if (policy.ResidentReclaimPolicy == ResidentReclaimPolicy.ConfirmedBookingProtected)
            {
                return ParkingResult.Failure("Parking_Error_ResidentDayAlreadyBooked");
            }

            var manualBinding = policy.ManualReleasesAreBinding
                && policy.ResidentReclaimPolicy != ResidentReclaimPolicy.AbsolutePriority
                && overlappingReleases.Any(r => r.Source == SpotReleaseSource.Manual);
            var beforeDeadline = policy.IsBeforeResidentProtectionDeadline(reservation.StartUtc, now, timeZone);
            if (policy.ResidentReclaimPolicy == ResidentReclaimPolicy.AdvancePriority && !beforeDeadline)
            {
                return ParkingResult.Failure("Parking_Error_ResidentReclaimManagerRequired");
            }
            var mayDisplaceWithoutReplacement = !manualBinding && policy.ResidentReclaimPolicy switch
            {
                ResidentReclaimPolicy.AdvancePriority => beforeDeadline,
                ResidentReclaimPolicy.AdvanceOrReplacement => beforeDeadline,
                ResidentReclaimPolicy.AbsolutePriority => true,
                _ => false,
            };

            // Moving is always preferred: the colleague keeps the same reservation, time, price,
            // voucher and check-in state. It is safe even after the protection deadline.
            var replacement = await FindSafeReplacementAsync(
                dbContext, reservation, spot.Id, spot.Type, timeZone, now, assignedReplacements, cancellationToken);
            if (replacement is not null)
            {
                reservation.MoveTo(replacement.Value.SpotId, now);
                reservation.AttributeSharedCapacity(replacement.Value.SharedByResidentId);
                assignedReplacements.Add((replacement.Value.SpotId, reservation.StartUtc, reservation.EndUtc));
                movedGuests.Add((reservation.UserId, replacement.Value.Code));
                dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
                    reservation.UserId, AccountAuditEventType.ReservationOverridden, $"resident:{userId}",
                    $"Resident reclaim moved reservation {reservation.Id} from {spot.Code} to {replacement.Value.Code}.", now));
                continue;
            }

            if (!mayDisplaceWithoutReplacement)
            {
                return ParkingResult.Failure(policy.ResidentReclaimPolicy == ResidentReclaimPolicy.ReplacementOnly
                    ? "Parking_Error_ResidentReplacementUnavailable"
                    : "Parking_Error_ResidentReclaimManagerRequired");
            }

            if (policy.ResidentNoReplacementAction == ResidentNoReplacementAction.Deny)
            {
                return ParkingResult.Failure("Parking_Error_ResidentReplacementUnavailable");
            }

            if (policy.ResidentNoReplacementAction == ResidentNoReplacementAction.ManagerOnly
                || reservation.Status == ReservationStatus.CheckedIn)
            {
                return ParkingResult.Failure("Parking_Error_ResidentReclaimManagerRequired");
            }

            // This is an explicitly configured provisional-plan fallback. The colleague loses no
            // budget or voucher; administration decides whether they also return to the waitlist.
            reservation.Cancel(now);
            if (reservation.CreditsCharged > 0)
            {
                var guestScore = await GetOrCreateScoreAsync(dbContext, reservation.UserId, cancellationToken);
                guestScore.RefundCredits(reservation.CreditsCharged, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId, IncentiveReason.ReservationRefund, reservation.CreditsCharged,
                    reservation.Id, now, "resident reclaim"));
            }
            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);

            if (policy.ResidentNoReplacementAction == ResidentNoReplacementAction.CancelAndQueue)
            {
                var alreadyQueued = await dbContext.QueueEntries.AnyAsync(q => q.UserId == reservation.UserId
                    && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
                    && q.StartUtc < reservation.EndUtc && q.EndUtc > reservation.StartUtc, cancellationToken);
                if (!alreadyQueued)
                {
                    dbContext.QueueEntries.Add(new QueueEntry(
                        reservation.UserId, reservation.StartUtc, reservation.EndUtc, reservation.CreatedAtUtc));
                }
                queuedGuests.Add(reservation.UserId);
            }
            else
            {
                cancelledGuests.Add(reservation.UserId);
            }
            dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
                reservation.UserId, AccountAuditEventType.ReservationOverridden, $"resident:{userId}",
                policy.ResidentNoReplacementAction == ResidentNoReplacementAction.CancelAndQueue
                    ? $"Resident reclaim cancelled and queued reservation {reservation.Id} on {spot.Code}."
                    : $"Resident reclaim cancelled reservation {reservation.Id} on {spot.Code} without replacement.", now));
        }

        // Every still-free released day in the requested range returns to the resident.
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

        foreach (var (guestId, toCode) in movedGuests)
        {
            await notifications.NotifyAsync(guestId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_ResidentMoved_Title"],
                messages.ForEconomy(policy, "Parking_Notify_ResidentMoved_Body", spot.Code, toCode),
                cancellationToken);
        }

        foreach (var guestId in queuedGuests.Distinct())
        {
            await notifications.NotifyAsync(guestId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_ResidentQueued_Title"],
                messages.ForEconomy(policy, "Parking_Notify_ResidentQueued_Body", spot.Code),
                cancellationToken);
        }

        foreach (var guestId in cancelledGuests.Distinct())
        {
            await notifications.NotifyAsync(guestId, NotificationCategory.Administrative, NotificationLevel.Critical,
                messages["Parking_Notify_ResidentCancelled_Title"],
                messages.ForEconomy(policy, "Parking_Notify_ResidentCancelled_Body", spot.Code),
                cancellationToken);
        }

        return ParkingResult.Success;
    }

    private static async Task<(Guid SpotId, string Code, Guid? SharedByResidentId)?> FindSafeReplacementAsync(
        D3ParkingDbContext dbContext, Reservation reservation, Guid reclaimedSpotId, ParkingSpotType requestedType,
        TimeZoneInfo timeZone, DateTimeOffset now,
        IReadOnlyList<(Guid SpotId, DateTimeOffset Start, DateTimeOffset End)> assignedReplacements,
        CancellationToken cancellationToken)
    {
        var candidates = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Id != reclaimedSpotId && s.Type == requestedType && s.Type != ParkingSpotType.Visitor)
            .Select(s => new { s.Id, s.Code, s.OwnerId })
            .ToListAsync(cancellationToken);
        var residentSpotIds = (await dbContext.ParkingSpotResidents.AsNoTracking()
            .Where(r => r.RemovedAtUtc == null)
            .Select(r => r.SpotId)
            .ToListAsync(cancellationToken)).ToHashSet();

        // Permanent shared capacity is the least disruptive replacement. A released resident spot
        // is valid too, but stays the fallback so one reclaim does not needlessly consume another
        // resident's temporary capacity.
        foreach (var candidate in candidates
                     .OrderBy(c => c.OwnerId is not null || residentSpotIds.Contains(c.Id))
                     .ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase))
        {
            if (assignedReplacements.Any(a => a.SpotId == candidate.Id
                && a.Start < reservation.EndUtc && a.End > reservation.StartUtc))
            {
                continue;
            }

            var occupied = await dbContext.Reservations.AnyAsync(r => r.Id != reservation.Id && r.SpotId == candidate.Id
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < reservation.EndUtc && r.EndUtc > reservation.StartUtc, cancellationToken);
            if (occupied)
            {
                continue;
            }

            var held = await dbContext.QueueEntries.AnyAsync(q => q.Status == QueueEntryStatus.Offered
                && q.OfferedSpotId == candidate.Id && q.OfferExpiresAtUtc > now
                && q.StartUtc < reservation.EndUtc && q.EndUtc > reservation.StartUtc, cancellationToken);
            if (held)
            {
                continue;
            }

            var hasResident = candidate.OwnerId is not null || residentSpotIds.Contains(candidate.Id);
            Guid? sharedByResidentId = null;
            if (hasResident)
            {
                var firstDate = SiteTime.Today(reservation.StartUtc, timeZone);
                var lastDate = SiteTime.Today(reservation.EndUtc.AddTicks(-1), timeZone);
                var releases = await dbContext.SpotReleases.AsNoTracking()
                    .Where(r => r.SpotId == candidate.Id && r.Date >= firstDate && r.Date <= lastDate)
                    .Select(r => new { r.Date, r.OwnerId })
                    .ToListAsync(cancellationToken);
                var coversEveryDay = true;
                for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
                {
                    if (releases.All(r => r.Date != date))
                    {
                        coversEveryDay = false;
                        break;
                    }
                }
                if (!coversEveryDay)
                {
                    continue;
                }
                sharedByResidentId = releases[0].OwnerId;
            }

            return (candidate.Id, candidate.Code, sharedByResidentId);
        }

        return null;
    }

    private static async Task RestoreVoucherAsync(
        D3ParkingDbContext dbContext, Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken)
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
        var resident = await FindResidentSpotAsync(dbContext, userId, cancellationToken);
        if (resident is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        if (resident.Membership is null)
        {
            resident.Spot.SetUsagePlan(plannedUseDays, autoReleaseUnplannedDays);
        }
        else
        {
            resident.Membership.SetUsagePlan(plannedUseDays, autoReleaseUnplannedDays);
        }
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

        await WithdrawInvalidAutomaticReleasesAsync(policy, timeZone, now, today, horizonEnd, cancellationToken);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Inactive spots never reach the pool, so planning them would only produce release rows
        // nobody can book.
        var candidates = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.OwnerId != null && s.AutoReleaseUnplannedDays
                && !dbContext.ParkingSpotResidents.Any(r => r.SpotId == s.Id && r.RemovedAtUtc == null))
            .Select(s => new { s.Id, s.PlanAppliedThrough })
            .ToListAsync(cancellationToken);

        var residentCandidates = await (from resident in dbContext.ParkingSpotResidents.AsNoTracking()
                                        join spot in dbContext.ParkingSpots.AsNoTracking() on resident.SpotId equals spot.Id
                                        where resident.RemovedAtUtc == null && resident.AutoReleaseUnplannedDays && spot.IsActive
                                        select new { resident.Id, resident.PlanAppliedThrough })
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

        foreach (var candidate in residentCandidates)
        {
            if (candidate.PlanAppliedThrough >= horizonEnd)
            {
                continue;
            }

            released += await OptimisticConcurrency.RetryAsync(
                () => ApplyPlanForResidentAsync(candidate.Id, today, horizonEnd, policy, timeZone, now, cancellationToken),
                cancellationToken);
        }

        return released;
    }

    private async Task WithdrawInvalidAutomaticReleasesAsync(IncentivePolicy policy, TimeZoneInfo timeZone,
        DateTimeOffset now, DateOnly today, DateOnly horizonEnd, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var candidates = (await dbContext.SpotReleases
                .Where(r => r.Source == SpotReleaseSource.UsagePlan && r.Date >= today)
                .ToListAsync(cancellationToken))
            .Where(r => r.Date > horizonEnd || !policy.IsReservationDateAllowed(r.Date))
            .ToList();
        if (candidates.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var spotIds = candidates.Select(r => r.SpotId).Distinct().ToList();
        var firstDate = candidates.Min(r => r.Date);
        var lastDate = candidates.Max(r => r.Date);
        var (firstUtc, _) = SiteTime.Day(firstDate, timeZone);
        var (_, lastUtc) = SiteTime.Day(lastDate, timeZone);
        var bookings = await dbContext.Reservations.AsNoTracking()
            .Where(r => spotIds.Contains(r.SpotId)
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < lastUtc && r.EndUtc > firstUtc)
            .Select(r => new { r.SpotId, r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);
        var codes = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        foreach (var release in candidates)
        {
            var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
            if (bookings.Any(r => r.SpotId == release.SpotId && r.StartUtc < dayEnd && r.EndUtc > dayStart))
            {
                continue;
            }

            if (release.AwardedPoints > 0)
            {
                var score = await GetOrCreateScoreAsync(dbContext, release.OwnerId, cancellationToken);
                score.RevokeSharePoints(release.AwardedPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    release.OwnerId, IncentiveReason.ResidentShareReclaimed, -release.AwardedPoints, null, now,
                    $"{codes.GetValueOrDefault(release.SpotId, string.Empty)} {release.Date:yyyy-MM-dd}"));
            }

            dbContext.SpotReleases.Remove(release);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> ApplyPlanForResidentAsync(Guid residentId, DateOnly today, DateOnly horizonEnd,
        IncentivePolicy policy, TimeZoneInfo timeZone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var resident = await dbContext.ParkingSpotResidents
            .FirstOrDefaultAsync(r => r.Id == residentId, cancellationToken);
        if (resident is null || resident.RemovedAtUtc is not null || !resident.AutoReleaseUnplannedDays)
        {
            return 0;
        }

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == resident.SpotId, cancellationToken);
        if (spot is null || !spot.IsActive)
        {
            return 0;
        }

        var fromDate = resident.PlanAppliedThrough is { } appliedThrough
            ? Max(today, appliedThrough.AddDays(1))
            : today;
        if (fromDate > horizonEnd)
        {
            return 0;
        }

        var assignedDates = await AssignedDatesAsync(
            dbContext, spot, resident.UserId, fromDate, horizonEnd, cancellationToken);
        var plan = await PlanReleaseRewardsAsync(dbContext, spot, resident.UserId, fromDate, horizonEnd,
            policy, timeZone, now, cancellationToken,
            date => assignedDates.Contains(date) && !resident.PlannedUseDays.Includes(date));

        await AwardReleasePlanAsync(dbContext, spot, resident.UserId, plan, now, SpotReleaseSource.UsagePlan, cancellationToken);
        resident.MarkPlanApplied(horizonEnd);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (plan.Count > 0)
        {
            await notifications.NotifyAsync(resident.UserId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_PlanReleased_Title"],
                messages.ForEconomy(policy, "Parking_Notify_PlanReleased_Body", spot.Code, plan.Count, plan.Sum(day => day.Points)),
                cancellationToken);
        }

        return plan.Count;
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

        await AwardReleasePlanAsync(dbContext, spot, ownerId, plan, now, SpotReleaseSource.UsagePlan, cancellationToken);
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

    private static async Task<ResidentSpotContext?> FindResidentSpotAsync(
        D3ParkingDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await dbContext.ParkingSpotResidents
            .FirstOrDefaultAsync(r => r.UserId == userId && r.RemovedAtUtc == null, cancellationToken);
        if (membership is not null)
        {
            var memberSpot = await dbContext.ParkingSpots
                .FirstOrDefaultAsync(s => s.Id == membership.SpotId, cancellationToken);
            return memberSpot is null ? null : new ResidentSpotContext(memberSpot, membership);
        }

        // Compatibility for tests and databases that have not run the membership backfill yet.
        var legacySpot = await dbContext.ParkingSpots
            .FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        return legacySpot is null ? null : new ResidentSpotContext(legacySpot, null);
    }

    /// <summary>
    /// Resolves the physical entitlement for each day. Explicit rows are authoritative; days without
    /// an override rotate deterministically across active residents. A single resident retains the
    /// historical all-days entitlement without requiring assignment rows for every calendar day.
    /// </summary>
    private static async Task<HashSet<DateOnly>> AssignedDatesAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot, Guid userId,
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
        await ResidentAllocation.AssignedDatesAsync(dbContext, spot, userId, fromDate, toDate, cancellationToken);

    private sealed record ResidentSpotContext(ParkingSpot Spot, ParkingSpotResident? Membership);
}
