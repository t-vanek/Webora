using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
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
        var claimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
        var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
        var takenByOther = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId != userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);

        OwnedSpotDayState state;
        if (takenByOther)
        {
            state = OwnedSpotDayState.SharedTaken;
        }
        else if (claimed)
        {
            state = OwnedSpotDayState.Claimed;
        }
        else if (releasedToday || policy.IsResidentAutoShareActive(today, now, timeZone))
        {
            state = OwnedSpotDayState.SharedFree;
        }
        else
        {
            state = OwnedSpotDayState.Held;
        }

        // The shown potential must pass the same monthly-allowance gate ReleaseAsync applies when
        // actually awarding — otherwise a fresh owner (allowance 0) or an exhausted month is
        // promised points that the release then pays out as zero.
        var monthFloor = new DateOnly(today.Year, today.Month, 1);
        var monthCeil = monthFloor.AddMonths(1);
        var rewardedThisMonth = await dbContext.SpotReleases.CountAsync(
            r => r.OwnerId == userId && r.AwardedPoints > 0 && r.Date >= monthFloor && r.Date < monthCeil,
            cancellationToken);
        var potential = rewardedThisMonth < spot.MonthlyShareAllowance
            ? policy.ComputeShareReward(policy.ResidentShareCutoff(today, timeZone), now, spot.MonthlyShareAllowance)
            : 0;
        return new OwnedSpotDto(spot.Id, spot.Code, spot.Type, spot.MonthlyShareAllowance,
            policy.ResidentMaxShareAllowance, state, releasedToday, potential);
    }

    // RetryAsync re-runs a serializable-transaction loser (deadlock victim) from scratch, so the
    // race with a guest booking resolves to a friendly failure instead of an error page.
    public Task<ParkingResult> ConfirmArrivalAsync(Guid userId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ConfirmArrivalCoreAsync(userId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ConfirmArrivalCoreAsync(Guid userId, CancellationToken cancellationToken)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // This is the one write into Reservations outside ReserveCoreAsync, and it needs the same
        // protection: at read-committed the takenByOther check and the insert are two separate
        // steps, so around the auto-share cutoff a guest booking this spot and the owner confirming
        // arrival could both pass their checks and both insert — two active reservations on one
        // spot. Serializable makes the checks take range locks, exactly like ReserveCoreAsync.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        // A deactivated spot is out of the pool entirely — the owner cannot park on it either
        // (ReserveCoreAsync rejects inactive spots the same way).
        if (!spot.IsActive)
        {
            return ParkingResult.Failure("Parking_Error_SpotInactive");
        }

        var (dayStart, dayEnd) = SiteTime.Day(today, timeZone);

        var alreadyClaimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
        if (alreadyClaimed)
        {
            return ParkingResult.Success;
        }

        if (await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_AlreadyReleased");
        }

        var takenByOther = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId != userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
        if (takenByOther)
        {
            return ParkingResult.Failure("Parking_Error_SpotTakenToday");
        }

        var reservation = new Reservation(spot.Id, userId, dayStart, dayEnd, false, now);
        reservation.CheckIn(now);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ParkingResult.Success;
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

        // The monthly-cap read (rewardedPerMonth below) and the reward inserts must be one atomic
        // step: at read-committed two parallel releases of disjoint ranges both count zero rewarded
        // days and both award up to the full allowance — the unique (SpotId, Date) index cannot
        // catch that because the dates differ. Serializable range-locks the owner's release rows,
        // the same pattern the daily caps in ReservationService use.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

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

        // The monthly share allowance is a hard cap on how many days a month a resident is rewarded
        // for sharing (not just a multiplier), so releasing a long future range can't farm points.
        var allowance = spot.MonthlyShareAllowance;
        // Count rewarded days across the WHOLE months the range touches, not just up to toDate —
        // otherwise releasing an earlier range after a later one (Sep 1–5 after Sep 10–14) would
        // not see the later rewards and the monthly cap could be exceeded at will.
        var monthFloor = new DateOnly(fromDate.Year, fromDate.Month, 1);
        var monthCeil = new DateOnly(toDate.Year, toDate.Month, 1).AddMonths(1);
        var rewardedPerMonth = (await dbContext.SpotReleases
                .Where(r => r.OwnerId == userId && r.AwardedPoints > 0 && r.Date >= monthFloor && r.Date < monthCeil)
                .Select(r => r.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(d => (d.Year, d.Month))
            .ToDictionary(g => g.Key, g => g.Count());

        ParkerScore? score = null;
        var releasedCount = 0;
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (alreadyReleased.Contains(date) || claimedDays.Contains(date))
            {
                continue;
            }

            var monthKey = (date.Year, date.Month);
            var points = rewardedPerMonth.GetValueOrDefault(monthKey) < allowance
                ? policy.ComputeShareReward(policy.ResidentShareCutoff(date, timeZone), now, allowance)
                : 0;
            dbContext.SpotReleases.Add(new SpotRelease(spot.Id, userId, date, now, points));
            releasedCount++;

            if (points > 0)
            {
                rewardedPerMonth[monthKey] = rewardedPerMonth.GetValueOrDefault(monthKey) + 1;
                score ??= await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
                score.RewardSharing(points, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ResidentSpotShared, points, null, now, $"{spot.Code} {date:yyyy-MM-dd}"));
            }
        }

        if (releasedCount == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToRelease");
        }

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

    public async Task<ParkingResult> SetShareAllowanceAsync(Guid userId, int allowance, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        spot.SetShareAllowance(Math.Clamp(allowance, 0, policy.ResidentMaxShareAllowance));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<int> SendDueHoldRemindersAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);
        var cutoff = policy.ResidentShareCutoff(today, timeZone);
        var remindFrom = cutoff - policy.ReminderLeadTime;

        // Only inside the lead window just before the cutoff, while the resident can still act.
        if (now < remindFrom || now >= cutoff)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var (dayStart, dayEnd) = SiteTime.Day(today, timeZone);

        // Inactive spots are out of the pool entirely — nagging their owners to "confirm or
        // release" a spot nobody could book would only teach them to ignore the reminders.
        var candidates = await dbContext.ParkingSpots
            .Where(s => s.IsActive && s.OwnerId != null && s.LastResidentReminderDate != today)
            .ToListAsync(cancellationToken);

        var toNotify = new List<(Guid OwnerId, string Code)>();
        foreach (var spot in candidates)
        {
            var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
            if (releasedToday)
            {
                continue;
            }

            var claimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == spot.OwnerId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
            if (claimed)
            {
                continue;
            }

            spot.MarkResidentReminded(today);
            toNotify.Add((spot.OwnerId!.Value, spot.Code));
        }

        if (toNotify.Count == 0)
        {
            return 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var (ownerId, code) in toNotify)
        {
            await notifications.NotifyAsync(ownerId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_HoldReminder_Title"],
                messages["Parking_Notify_HoldReminder_Body", code],
                cancellationToken);
        }

        return toNotify.Count;
    }

    public async Task<int> NotifyDueAutoSharesAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);

        // Auto-share only kicks in once the hold cutoff has passed for today.
        if (now < policy.ResidentShareCutoff(today, timeZone))
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var (dayStart, dayEnd) = SiteTime.Day(today, timeZone);

        // Same reason as the hold reminders: a deactivated spot never auto-shares into the pool,
        // so telling its owner it just did would be false.
        var candidates = await dbContext.ParkingSpots
            .Where(s => s.IsActive && s.OwnerId != null && s.LastAutoShareNoticeDate != today)
            .ToListAsync(cancellationToken);

        var toNotify = new List<(Guid OwnerId, string Code)>();
        foreach (var spot in candidates)
        {
            // A deliberate release isn't an "auto" share; the resident chose it.
            var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
            if (releasedToday)
            {
                continue;
            }

            var claimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == spot.OwnerId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
            if (claimed)
            {
                continue;
            }

            spot.MarkAutoShareNoticed(today);
            toNotify.Add((spot.OwnerId!.Value, spot.Code));
        }

        if (toNotify.Count == 0)
        {
            return 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var (ownerId, code) in toNotify)
        {
            await notifications.NotifyAsync(ownerId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_AutoShared_Title"],
                messages["Parking_Notify_AutoShared_Body", code],
                cancellationToken);
        }

        return toNotify.Count;
    }

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
            // not shield the reward from being reversed. Cancelled and no-show bookings still count
            // as demand (a no-show is already penalised separately in SweepNoShowsAsync).
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
