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

        // The shown potential is exactly what ReleaseAsync(today, today) would award — the same
        // skipped days and the same monthly-allowance gate — otherwise a fresh owner (allowance 0)
        // or an exhausted month is promised points that the release then pays out as zero.
        var potential = (await PlanReleaseRewardsAsync(dbContext, spot, userId, today, today, policy, timeZone, now, cancellationToken))
            .Sum(day => day.Points);

        // Today-or-later released days, each marked with whether a guest already booked it — the
        // free ones are what the resident's right of first refusal can still take back.
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
                    && r.StartUtc < horizonEnd && r.EndUtc > horizonStart)
                .Select(r => new { r.StartUtc, r.EndUtc })
                .ToListAsync(cancellationToken);
            foreach (var date in upcomingDates)
            {
                var (start, end) = SiteTime.Day(date, timeZone);
                upcoming.Add(new ReleasedDayDto(date, guestBookings.Any(b => b.StartUtc < end && b.EndUtc > start)));
            }
        }

        return new OwnedSpotDto(spot.Id, spot.Code, spot.Type, spot.MonthlyShareAllowance,
            policy.ResidentMaxShareAllowance, state, releasedToday, potential, upcoming,
            spot.PlannedUseDays, spot.AutoReleaseUnplannedDays,
            policy.ResidentPlanHorizonEnd(today).DayNumber - today.DayNumber);
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

        // An auto-shared spot may already be held for a waitlist offer; the owner's right of
        // first refusal outranks the pending offer. Withdrawn waiters keep their position and
        // are notified below (after the commit), so their claim never lands on a taken spot.
        var withdrawn = new List<Guid>();
        var holds = await dbContext.QueueEntries
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId == spot.Id
                && q.StartUtc < dayEnd && q.EndUtc > dayStart)
            .ToListAsync(cancellationToken);
        foreach (var hold in holds)
        {
            hold.WithdrawOffer();
            withdrawn.Add(hold.UserId);
        }

        var reservation = new Reservation(spot.Id, userId, dayStart, dayEnd, false, now);
        reservation.CheckIn(now);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var waiterId in withdrawn)
        {
            await notifications.NotifyAsync(waiterId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_QueueHoldReclaimed_Title"],
                messages["Parking_Notify_QueueHoldReclaimed_Body", spot.Code],
                cancellationToken);
        }

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

        // The monthly-cap read (inside PlanReleaseRewardsAsync) and the reward inserts must be one
        // atomic step: at read-committed two parallel releases of disjoint ranges both count zero
        // rewarded days and both award up to the full allowance — the unique (SpotId, Date) index
        // cannot catch that because the dates differ. Serializable range-locks the owner's release
        // rows, the same pattern the daily caps in ReservationService use.
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

        // No serializable transaction here: the preview is advisory, and the actual award is
        // re-derived (and cap-enforced) inside ReleaseCoreAsync's transaction anyway.
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
    /// "not a planned-use weekday"); it is applied inside the loop so the monthly quota is spent on
    /// the days actually released, not on the ones filtered out afterwards.
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

        var plan = new List<(DateOnly Date, int Points)>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (alreadyReleased.Contains(date) || claimedDays.Contains(date) || include?.Invoke(date) == false)
            {
                continue;
            }

            var monthKey = (date.Year, date.Month);
            var points = rewardedPerMonth.GetValueOrDefault(monthKey) < allowance
                ? policy.ComputeShareReward(policy.ResidentShareCutoff(date, timeZone), now, allowance)
                : 0;
            // A day computed to zero (e.g. released past the cutoff) must not consume quota,
            // exactly as the award path never wrote a ledger entry for it.
            if (points > 0)
            {
                rewardedPerMonth[monthKey] = rewardedPerMonth.GetValueOrDefault(monthKey) + 1;
            }

            plan.Add((date, points));
        }

        return plan;
    }

    public Task<ParkingResult> ReclaimAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReclaimCoreAsync(userId, fromDate, toDate, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ReclaimCoreAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
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

        // The guest-booking check and the release deletion must be one atomic step, or a guest
        // booking the day between them would sit on a spot the resident just took back. Same
        // pattern (and the same range locks) as ReserveCoreAsync on the other side of the race.
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
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);

        // A firm guest booking is never evicted (the same conflict rule the auto-share follows) —
        // such days simply stay shared. Everything else is the resident's to take back.
        ParkerScore? score = null;
        var reclaimed = new List<SpotRelease>();
        foreach (var release in releases)
        {
            var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
            if (guestBookings.Any(b => b.StartUtc < dayEnd && b.EndUtc > dayStart))
            {
                continue;
            }

            reclaimed.Add(release);
            if (release.AwardedPoints > 0)
            {
                score ??= await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
                score.RevokeSharePoints(release.AwardedPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ResidentShareReclaimed, -release.AwardedPoints, null, now,
                    $"{spot.Code} {release.Date:yyyy-MM-dd}"));
            }
        }

        if (reclaimed.Count == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToReclaim");
        }

        dbContext.SpotReleases.RemoveRange(reclaimed);

        // A waitlist hold is only a pending offer, not a booking — the resident's right of first
        // refusal outranks it. The withdrawn waiter keeps their queue position (no requeue) and
        // the maintenance loop hands them the next freed spot.
        var reclaimedDays = reclaimed.Select(r => r.Date).ToHashSet();
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
        // nobody can book — the same reason the hold reminders and auto-share notices skip them.
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
        // and a manual release running together would each award up to the full allowance.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        // Re-checked inside the transaction: the scan is a separate read, and between the two the
        // admin may have deactivated or reassigned the spot, or the resident switched the plan off.
        if (spot is null || !spot.IsActive || spot.OwnerId is not { } ownerId || !spot.AutoReleaseUnplannedDays)
        {
            return 0;
        }

        // Today is deliberately left out: it belongs to the hold / confirm-arrival flow, where the
        // resident can still take the spot back. A plan release would close that door.
        var fromDate = spot.PlanAppliedThrough is { } appliedThrough
            ? Max(today.AddDays(1), appliedThrough.AddDays(1))
            : today.AddDays(1);
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
            messages["Parking_Notify_PlanReleased_Body", spot.Code, plan.Count, plan.Sum(day => day.Points)],
            cancellationToken);

        return plan.Count;
    }

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;

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
