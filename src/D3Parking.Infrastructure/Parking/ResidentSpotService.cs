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
        var planHorizonEnd = policy.ResidentPlanHorizonEnd(today);
        var scheduleEnd = today.AddDays(Math.Clamp(policy.ReservationHorizonDays, 1, 366));
        var assignedUsers = await ResidentAllocation.AssignedUsersAsync(
            dbContext, spot, today, scheduleEnd, cancellationToken);
        var assignedDates = assignedUsers
            .Where(day => day.Key <= planHorizonEnd && day.Value == userId)
            .Select(day => day.Key)
            .ToHashSet();

        var releases = await dbContext.SpotReleases.AsNoTracking()
            .Where(r => r.SpotId == spot.Id && r.Date >= today && r.Date <= scheduleEnd)
            .OrderBy(r => r.Date)
            .Select(r => new { r.Date, r.OwnerId })
            .ToListAsync(cancellationToken);

        var (horizonStart, _) = SiteTime.Day(today, timeZone);
        var (_, horizonEndUtc) = SiteTime.Day(scheduleEnd, timeZone);
        var bookings = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.SpotId == spot.Id
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.EndUtc > now && r.StartUtc < horizonEndUtc && r.EndUtc > horizonStart)
            .Select(r => new { r.Id, r.UserId, r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);

        // A private handoff does not create a public release row, but it still contributes a named
        // booking and remains reclaimable by the resident who handed the day over.
        var directHandoffs = await (
            from handoff in dbContext.ResidentSpotHandoffs.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on handoff.ReservationId equals reservation.Id
            where handoff.SpotId == spot.Id
                && handoff.Status == ResidentSpotHandoffStatus.Accepted
                && reservation.SpotId == spot.Id
                && (reservation.Status == ReservationStatus.Reserved || reservation.Status == ReservationStatus.CheckedIn)
                && reservation.EndUtc > now && reservation.StartUtc < horizonEndUtc
            select new { reservation.Id, handoff.ResidentId })
            .ToListAsync(cancellationToken);

        var directByReservation = directHandoffs
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First().ResidentId);
        var userIds = assignedUsers.Values
            .Concat(releases.Select(r => r.OwnerId))
            .Concat(bookings.Select(r => r.UserId))
            .Distinct()
            .ToList();
        var users = await dbContext.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new ParkingUserLabelDto(u.Id, u.DisplayName, u.Email))
            .ToDictionaryAsync(u => u.UserId, cancellationToken);

        ParkingUserLabelDto Label(Guid id) => users.GetValueOrDefault(id)
            ?? new ParkingUserLabelDto(id, null, null);

        var days = new List<ResidentSpotDayDto>(scheduleEnd.DayNumber - today.DayNumber + 1);
        for (var date = today; date <= scheduleEnd; date = date.AddDays(1))
        {
            var (dayStart, dayEnd) = SiteTime.Day(date, timeZone);
            assignedUsers.TryGetValue(date, out var assignedUserId);
            var release = releases.FirstOrDefault(r => r.Date == date);
            var dayBookings = bookings
                .Where(b => b.StartUtc < dayEnd && b.EndUtc > dayStart)
                .OrderBy(b => b.StartUtc)
                .Select(b => new ResidentSpotBookingDto(
                    b.Id, Label(b.UserId), b.StartUtc, b.EndUtc, directByReservation.ContainsKey(b.Id)))
                .ToList();
            var externallyBooked = dayBookings.Any(b => b.User.UserId != assignedUserId);
            var directResidentId = bookings
                .Where(b => b.StartUtc < dayEnd && b.EndUtc > dayStart)
                .Select(b => directByReservation.GetValueOrDefault(b.Id))
                .FirstOrDefault(id => id != Guid.Empty);
            var releasedById = release?.OwnerId ?? (directResidentId == Guid.Empty ? null : directResidentId);

            var releasedCapacity = release is not null || directResidentId != Guid.Empty || externallyBooked;
            var allocationState = releasedCapacity
                ? ResidentAllocationState.Released
                : assignedUserId == userId
                    ? ResidentAllocationState.AssignedToCurrentUser
                    : assignedUserId != Guid.Empty
                        ? ResidentAllocationState.AssignedToOtherResident
                        : ResidentAllocationState.Unknown;
            var bookingState = dayBookings.Count switch
            {
                0 => ResidentBookingState.None,
                > 1 => ResidentBookingState.MultipleReservations,
                _ when dayBookings[0].User.UserId == userId => ResidentBookingState.ReservedByCurrentUser,
                _ => ResidentBookingState.ReservedByOtherUser,
            };
            var state = allocationState switch
            {
                ResidentAllocationState.Released when bookingState != ResidentBookingState.None =>
                    OwnedSpotDayState.SharedTaken,
                ResidentAllocationState.Released => OwnedSpotDayState.SharedFree,
                ResidentAllocationState.AssignedToCurrentUser => OwnedSpotDayState.Held,
                ResidentAllocationState.AssignedToOtherResident => OwnedSpotDayState.NotAssigned,
                _ => OwnedSpotDayState.Unknown,
            };

            days.Add(new ResidentSpotDayDto(
                date,
                state,
                allocationState,
                bookingState,
                assignedUserId == Guid.Empty ? null : Label(assignedUserId),
                releasedById is { } ownerId ? Label(ownerId) : null,
                dayBookings,
                assignedUserId == userId,
                releasedById == userId));
        }

        // Keep the compact compatibility view used by release controls and existing clients. It is
        // now derived from the richer schedule so both representations always agree.
        var upcoming = new List<ReleasedDayDto>();
        foreach (var day in days.Where(day => day.CanReclaim))
        {
            var directOnly = releases.All(release => release.Date != day.Date)
                && day.Bookings.Any(booking => booking.DirectHandoff);
            if (day.State is OwnedSpotDayState.SharedFree or OwnedSpotDayState.SharedTaken)
            {
                upcoming.Add(new ReleasedDayDto(
                    day.Date,
                    day.Bookings.Any(booking => booking.User.UserId != userId),
                    directOnly));
            }
        }

        var todayDay = days.First();

        return new OwnedSpotDto(spot.Id, spot.Code, spot.Type, todayDay.State,
            releases.Any(r => r.Date == today), upcoming,
            resident.Membership?.PlannedUseDays ?? spot.PlannedUseDays,
            resident.Membership?.AutoReleaseUnplannedDays ?? spot.AutoReleaseUnplannedDays,
            planHorizonEnd.DayNumber - today.DayNumber, assignedDates.Order().ToList(), days);
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

        var (handoffRangeStart, _) = SiteTime.Day(fromDate, timeZone);
        var (_, handoffRangeEnd) = SiteTime.Day(toDate, timeZone);
        var pendingHandoff = await dbContext.ResidentSpotHandoffs.AnyAsync(h => h.SpotId == spot.Id
            && (h.Status == ResidentSpotHandoffStatus.PendingResident || h.Status == ResidentSpotHandoffStatus.Offered)
            && h.ExpiresAtUtc > now && h.StartUtc < handoffRangeEnd && h.EndUtc > handoffRangeStart,
            cancellationToken);
        if (pendingHandoff)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_AlreadyPending");
        }

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

    /// <summary>
    /// Writes one planned <see cref="SpotRelease"/> per day. Releases no longer award points;
    /// helpful use of a released day is recognized later by an achievement.
    /// </summary>
    private static Task AwardReleasePlanAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot, Guid ownerId,
        List<(DateOnly Date, int Points)> plan, DateTimeOffset now, SpotReleaseSource source,
        CancellationToken cancellationToken)
    {
        foreach (var (date, _) in plan)
        {
            dbContext.SpotReleases.Add(new SpotRelease(spot.Id, ownerId, date, now, 0, source));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The days in [fromDate, toDate] that would be newly shared. The integer remains in the
    /// internal tuple for storage compatibility and is always zero because users collect only
    /// achievements, never points.
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

        var pendingPrivateWindows = await dbContext.ResidentSpotHandoffs.AsNoTracking()
            .Where(h => h.SpotId == spot.Id
                && (h.Status == ResidentSpotHandoffStatus.PendingResident || h.Status == ResidentSpotHandoffStatus.Offered)
                && h.ExpiresAtUtc > now && h.StartUtc < rangeEnd && h.EndUtc > rangeStart)
            .Select(h => new { h.StartUtc, h.EndUtc })
            .ToListAsync(cancellationToken);
        var acceptedPrivateWindows = await (
            from handoff in dbContext.ResidentSpotHandoffs.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking() on handoff.ReservationId equals reservation.Id
            where handoff.SpotId == spot.Id && handoff.Status == ResidentSpotHandoffStatus.Accepted
                && reservation.SpotId == spot.Id
                && (reservation.Status == ReservationStatus.Reserved || reservation.Status == ReservationStatus.CheckedIn)
                && reservation.StartUtc < rangeEnd && reservation.EndUtc > rangeStart
            select new { reservation.StartUtc, reservation.EndUtc })
            .ToListAsync(cancellationToken);
        var privateDays = pendingPrivateWindows.Concat(acceptedPrivateWindows)
            .SelectMany(window =>
            {
                var first = SiteTime.Today(window.StartUtc, timeZone);
                var last = SiteTime.Today(window.EndUtc.AddTicks(-1), timeZone);
                return Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays);
            })
            .ToHashSet();

        var plan = new List<(DateOnly Date, int Points)>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (!policy.IsReservationDateAllowed(date)
                || alreadyReleased.Contains(date)
                || claimedDays.Contains(date)
                || privateDays.Contains(date)
                || include?.Invoke(date) == false)
            {
                continue;
            }

            plan.Add((date, 0));
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

        var directHandoffs = await (
            from handoff in dbContext.ResidentSpotHandoffs
            join reservation in dbContext.Reservations
                on handoff.ReservationId equals reservation.Id
            where handoff.ResidentId == userId && handoff.SpotId == spot.Id
                && handoff.Status == ResidentSpotHandoffStatus.Accepted
                && reservation.SpotId == spot.Id
                && (reservation.Status == ReservationStatus.Reserved || reservation.Status == ReservationStatus.CheckedIn)
            select new { Handoff = handoff, Reservation = reservation })
            .ToListAsync(cancellationToken);
        var directReservationIds = directHandoffs
            .Where(pair =>
            {
                var first = SiteTime.Today(pair.Reservation.StartUtc, timeZone);
                var last = SiteTime.Today(pair.Reservation.EndUtc.AddTicks(-1), timeZone);
                return first <= toDate && last >= fromDate;
            })
            .Select(pair => pair.Reservation.Id)
            .ToHashSet();

        if (releases.Count == 0 && directReservationIds.Count == 0)
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
            var directHandoff = directReservationIds.Contains(reservation.Id);
            if (overlappingReleases.Count == 0 && !directHandoff)
            {
                continue;
            }

            if (policy.ResidentReclaimPolicy == ResidentReclaimPolicy.ConfirmedBookingProtected)
            {
                return ParkingResult.Failure("Parking_Error_ResidentDayAlreadyBooked");
            }

            var manualBinding = policy.ManualReleasesAreBinding
                && policy.ResidentReclaimPolicy != ResidentReclaimPolicy.AbsolutePriority
                && (directHandoff || overlappingReleases.Any(r => r.Source == SpotReleaseSource.Manual));
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

        // Every still-free released day in the requested range returns to the resident. Earned
        // achievements and historical reputation data are never reduced.
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
        var firstBookableDate = policy.FirstBookableDate(today);
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
                () => ApplyPlanForSpotAsync(candidate.Id, firstBookableDate, horizonEnd, policy, timeZone, now, cancellationToken),
                cancellationToken);
        }

        foreach (var candidate in residentCandidates)
        {
            if (candidate.PlanAppliedThrough >= horizonEnd)
            {
                continue;
            }

            released += await OptimisticConcurrency.RetryAsync(
                () => ApplyPlanForResidentAsync(candidate.Id, firstBookableDate, horizonEnd, policy, timeZone, now, cancellationToken),
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
            .Where(r => r.Date < policy.FirstBookableDate(today)
                || r.Date > horizonEnd
                || !policy.IsReservationDateAllowed(r.Date))
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
        foreach (var release in candidates)
        {
            var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
            if (bookings.Any(r => r.SpotId == release.SpotId && r.StartUtc < dayEnd && r.EndUtc > dayStart))
            {
                continue;
            }

            dbContext.SpotReleases.Remove(release);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> ApplyPlanForResidentAsync(Guid residentId, DateOnly firstBookableDate, DateOnly horizonEnd,
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
            ? Max(firstBookableDate, appliedThrough.AddDays(1))
            : firstBookableDate;
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

    private async Task<int> ApplyPlanForSpotAsync(Guid spotId, DateOnly firstBookableDate, DateOnly horizonEnd,
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

        // The plan starts on the first date new bookings may use. A planned-use day stays held;
        // an unplanned day is released explicitly.
        var fromDate = spot.PlanAppliedThrough is { } appliedThrough
            ? Max(firstBookableDate, appliedThrough.AddDays(1))
            : firstBookableDate;
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
