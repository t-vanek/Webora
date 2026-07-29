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

public sealed class ReservationService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : IReservationService
{
    // How early a driver may confirm presence for an upcoming window. Enough for early arrivals;
    // far-ahead check-ins would make the no-show sweep unreachable and its penalties dead letter.
    private static readonly TimeSpan EarlyCheckInWindow = TimeSpan.FromMinutes(15);

    // Serializes the no-show sweep and the queue matcher. Both are triggered from several places
    // at once (the maintenance timer, the admin button, release/cancel hooks) and neither runs in
    // a transaction — overlapping runs would double-apply no-show penalties and offer one spot to
    // two waiters. In-process locking suffices because the app is single-instance by design (the
    // same assumption Wolverine's in-process queues already make).
    private static readonly SemaphoreSlim MaintenanceGate = new(1, 1);

    public async Task<IReadOnlyList<ParkingSpotDto>> GetAvailableSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var requestDate = SiteTime.Today(startUtc, timeZone);
        var autoShareActive = policy.IsResidentAutoShareActive(requestDate, now, timeZone);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var blocked = dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                        && r.StartUtc < endUtc && r.EndUtc > startUtc)
            .Select(r => r.SpotId);

        var released = dbContext.SpotReleases.Where(r => r.Date == requestDate).Select(r => r.SpotId);

        // Spots currently held for a waitlist offer are off the table until the claim window lapses.
        var held = dbContext.QueueEntries
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferExpiresAtUtc > now
                        && q.OfferedSpotId != null && q.StartUtc < endUtc && q.EndUtc > startUtc)
            .Select(q => q.OfferedSpotId!.Value);

        // Owned spots are hidden from the pool unless released for that day, or it is today and the
        // hold cutoff has passed (auto-share). Once a guest books one, the block above excludes it.
        // Natural code order (D3-2 before D3-10) needs the comparer, so sort in memory.
        var available = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && !blocked.Contains(s.Id) && !held.Contains(s.Id)
                && (s.OwnerId == null || autoShareActive || released.Contains(s.Id)))
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId, null, s.MonthlyShareAllowance))
            .ToListAsync(cancellationToken);
        return available.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public async Task<IReadOnlyList<ReservationDto>> GetMyReservationsAsync(Guid userId, bool upcomingOnly = false, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var query = from r in dbContext.Reservations.AsNoTracking()
                    join s in dbContext.ParkingSpots on r.SpotId equals s.Id
                    where r.UserId == userId
                    select new { r, s.Code, s.Type };

        if (upcomingOnly)
        {
            query = query.Where(x => x.r.EndUtc >= now
                && (x.r.Status == ReservationStatus.Reserved || x.r.Status == ReservationStatus.CheckedIn));
        }

        return await query
            .OrderByDescending(x => x.r.StartUtc)
            .Take(200)
            .Select(x => new ReservationDto(
                x.r.Id, x.r.SpotId, x.Code, x.Type, x.r.UserId,
                x.r.StartUtc, x.r.EndUtc, x.r.Status, x.r.IsOffPeak, x.r.CreatedAtUtc,
                x.r.CheckedInAtUtc, x.r.ReleasedAtUtc, x.r.CompletedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default) =>
        ReserveCoreAsync(userId, spotId, startUtc, endUtc, fromQueue: false, queueEntryId: null, cancellationToken);

    private async Task<ParkingResult> ReserveCoreAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool fromQueue, Guid? queueEntryId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (endUtc <= startUtc)
        {
            return ParkingResult.Failure("Parking_Error_InvalidWindow");
        }

        if (endUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The conflict checks below and the insert have to be one atomic step: at plain read-committed
        // two concurrent bookings for the last free spot both pass the check and both insert, and no
        // constraint catches it (overlap is not something a unique index can express). Serializable
        // makes those checks take range locks, so the second request blocks and then fails cleanly.
        // Under contention this can surface as a deadlock — a failed request the user can retry is
        // still far better than two people sent to the same spot.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (!spot.IsActive)
        {
            return ParkingResult.Failure("Parking_Error_SpotInactive");
        }

        // Windows may legitimately start in the past (booking the rest of today, claiming a queue
        // offer mid-window). Everything sensitive to the time of start — the shared-release day and
        // the peak/off-peak classification that drives price and bonus — is evaluated at the
        // effective start, the moment parking can actually begin, so a stale early start can't buy
        // the off-peak rate for what is really a peak-time stay.
        var effectiveStartUtc = startUtc > now ? startUtc : now;

        // A reserved (owned) spot can only be booked by a non-owner once it is shared — and every
        // local day the window touches must be shared, not just the first. A Wed–Fri booking with
        // only Wednesday released would otherwise occupy the owner's spot on Thu and Fri.
        if (spot.OwnerId is { } owner && owner != userId)
        {
            var firstDay = SiteTime.Today(effectiveStartUtc, timeZone);
            var lastDay = SiteTime.Today(endUtc.AddTicks(-1), timeZone);
            var releasedDates = (await dbContext.SpotReleases
                .Where(r => r.SpotId == spotId && r.Date >= firstDay && r.Date <= lastDay)
                .Select(r => r.Date)
                .ToListAsync(cancellationToken)).ToHashSet();

            for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
            {
                if (!releasedDates.Contains(date) && !policy.IsResidentAutoShareActive(date, now, timeZone))
                {
                    return ParkingResult.Failure("Parking_Error_SpotReserved");
                }
            }
        }

        var spotTaken = await dbContext.Reservations.AnyAsync(r => r.SpotId == spotId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < endUtc && r.EndUtc > startUtc, cancellationToken);
        if (spotTaken)
        {
            return ParkingResult.Failure("Parking_Error_SpotConflict");
        }

        // A spot held for someone else's waitlist offer can't be booked out from under them.
        var heldByOther = await dbContext.QueueEntries.AnyAsync(q => q.Status == QueueEntryStatus.Offered
            && q.OfferedSpotId == spotId && q.UserId != userId && q.OfferExpiresAtUtc > now
            && q.StartUtc < endUtc && q.EndUtc > startUtc, cancellationToken);
        if (heldByOther)
        {
            return ParkingResult.Failure("Parking_Error_SpotHeld");
        }

        var ownConflict = await dbContext.Reservations.AnyAsync(r => r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < endUtc && r.EndUtc > startUtc, cancellationToken);
        if (ownConflict)
        {
            return ParkingResult.Failure("Parking_Error_OwnConflict");
        }

        // Off-peak and distance rewards are granted on completion (actual use), not at booking, so a
        // reserve/release loop earns nothing. IsOffPeak is captured now for use at completion.
        var isOffPeak = policy.IsOffPeak(effectiveStartUtc, timeZone);

        var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
        var tierRank = IncentivePolicy.TierRank(policy.TierFor(score.Points));

        // Higher loyalty tiers get a bigger monthly allowance.
        var granted = score.GrantMonthlyCreditIfDue(policy.AllowanceForTier(tierRank), ParkerScore.PeriodOf(now), now);
        if (granted > 0)
        {
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.MonthlyCreditGrant, granted, null, now));
        }

        // Dynamic price (peak × occupancy for the window), then a loyalty-tier discount.
        var occupancy = await ComputeOccupancyAsync(dbContext, startUtc, endUtc, cancellationToken);
        var cost = policy.ApplyTierDiscount(policy.ComputeReservationCost(!isOffPeak, occupancy), tierRank);

        if (score.Credits < cost)
        {
            return ParkingResult.Failure("Parking_Error_InsufficientCredit");
        }

        score.ChargeCredits(cost, now);
        var reservation = new Reservation(spotId, userId, startUtc, endUtc, isOffPeak, now, cost, fromQueue);
        dbContext.Reservations.Add(reservation);
        dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
            userId, IncentiveReason.ReservationCharge, -cost, reservation.Id, now, spot.Code));

        // A successful booking supersedes the user's own overlapping waitlist entries: they could
        // never claim a second spot for the same window (own-conflict), so a lingering entry would
        // only pin future offers on spots nobody can take.
        var superseded = await dbContext.QueueEntries
            .Where(q => q.UserId == userId && q.Id != queueEntryId
                && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
                && q.StartUtc < endUtc && q.EndUtc > startUtc)
            .ToListAsync(cancellationToken);
        foreach (var stale in superseded)
        {
            stale.Cancel();
        }

        // Claiming a waitlist offer marks the entry in the same transaction as the booking it creates,
        // so an offer can never stay open (holding the spot) against a reservation that succeeded.
        if (queueEntryId is { } entryId)
        {
            var entry = await dbContext.QueueEntries
                .FirstOrDefaultAsync(q => q.Id == entryId && q.UserId == userId, cancellationToken);
            if (entry is null || entry.Status != QueueEntryStatus.Offered || entry.OfferedSpotId != spotId)
            {
                return ParkingResult.Failure("Parking_Queue_Error_NoOffer");
            }

            if (entry.OfferExpiresAtUtc is { } offerExpires && offerExpires <= now)
            {
                return ParkingResult.Failure("Parking_Queue_Error_OfferExpired");
            }

            entry.Claim();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (granted > 0)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_MonthlyCredit_Title"],
                messages["Parking_Notify_MonthlyCredit_Body", granted], cancellationToken);
        }

        await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages["Parking_Notify_Reserved_Title"],
            messages["Parking_Notify_Reserved_Body", spot.Code, cost], cancellationToken);

        // Warn when the wallet can no longer cover even a base-price booking.
        if (score.Credits < policy.BaseReservationCost)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_LowBalance_Title"],
                messages["Parking_Notify_LowBalance_Body", score.Credits], email: true, cancellationToken);
        }

        return ParkingResult.Success;
    }

    public async Task<ReservationQuoteDto> GetQuoteAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Mirror ReserveCoreAsync: an in-progress window is classified (and priced) by its
        // effective start, so the quote always matches what reserving would actually charge.
        var now = timeProvider.GetUtcNow();
        var effectiveStartUtc = startUtc > now ? startUtc : now;
        var isPeak = policy.IsPeak(effectiveStartUtc, timeZone);
        var occupancy = endUtc > startUtc
            ? await ComputeOccupancyAsync(dbContext, startUtc, endUtc, cancellationToken)
            : 0.0;

        var score = await dbContext.ParkerScores.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        var tierRank = IncentivePolicy.TierRank(policy.TierFor(score?.Points ?? 0));
        var cost = policy.ApplyTierDiscount(policy.ComputeReservationCost(isPeak, occupancy), tierRank);

        // Reflect the (tier-boosted) monthly allowance the user would receive at booking, so affordability matches reserve.
        // PreviewAllowance applies any pending queue no-show penalty exactly as the grant will.
        var balance = score?.Credits ?? 0;
        if (score is null || score.LastCreditGrantPeriod < ParkerScore.PeriodOf(now))
        {
            var allowance = policy.AllowanceForTier(tierRank);
            balance += score?.PreviewAllowance(allowance) ?? allowance;
        }

        return new ReservationQuoteDto(cost, (int)Math.Round(occupancy * 100), isPeak, balance, balance >= cost);
    }

    private static async Task<double> ComputeOccupancyAsync(D3ParkingDbContext dbContext, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var activeSpots = await dbContext.ParkingSpots.CountAsync(s => s.IsActive, cancellationToken);
        if (activeSpots == 0)
        {
            return 0.0;
        }

        var occupied = await dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < endUtc && r.EndUtc > startUtc)
            .Select(r => r.SpotId)
            .Distinct()
            .CountAsync(cancellationToken);

        return Math.Min(1.0, (double)occupied / activeSpots);
    }

    public async Task<ParkingResult> CheckInAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        // Presence can only be confirmed around the reserved window itself. Unconstrained check-in
        // would let a holder "arrive" days ahead — dodging the no-show sweep entirely — or
        // resurrect a window that has already ended.
        var now = timeProvider.GetUtcNow();
        if (now < reservation.StartUtc - EarlyCheckInWindow)
        {
            return ParkingResult.Failure("Parking_Error_CheckInTooEarly");
        }

        if (now >= reservation.EndUtc)
        {
            return ParkingResult.Failure("Parking_Error_CheckInWindowOver");
        }

        reservation.CheckIn(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> CompleteAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.CheckedIn)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        var now = timeProvider.GetUtcNow();

        // Completing before the window starts would both bank the completion rewards from the couch
        // and put the reservation forever out of the no-show sweep's reach.
        if (now < reservation.StartUtc)
        {
            return ParkingResult.Failure("Parking_Error_CompleteTooEarly");
        }

        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        reservation.Complete(now);

        // Completion incentives pay out once per local day. Completing frees the window for an
        // immediate re-booking, so per-completion rewards would let a reserve→check-in→complete
        // loop mint streak points and credits all day; one package per day pays for what the streak
        // actually measures — turning up and parking. The 0-point ReservationCompleted ledger row
        // doubles as the "already rewarded today" marker.
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var (dayStart, dayEnd) = SiteTime.Day(SiteTime.Today(now, timeZone), timeZone);
        var rewardedToday = await dbContext.PointsLedgerEntries.AnyAsync(e =>
            e.UserId == userId && e.Reason == IncentiveReason.ReservationCompleted
            && e.OccurredAtUtc >= dayStart && e.OccurredAtUtc < dayEnd, cancellationToken);
        if (rewardedToday)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Success;
        }

        var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
        score.RecordCompletion(now);
        dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
            userId, IncentiveReason.ReservationCompleted, 0, reservation.Id, now));

        // Reward an unbroken run of completions (reliability), growing with the streak up to a cap.
        var streakBonus = policy.ComputeStreakBonus(score.CompletionStreak);
        if (streakBonus > 0)
        {
            score.RewardStreak(streakBonus, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.StreakBonus, streakBonus, reservation.Id, now));
        }

        // Reward off-peak use only once the spot was actually used.
        if (reservation.IsOffPeak)
        {
            score.RewardOffPeak(policy.OffPeakBonusPoints, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.OffPeakBonus, policy.OffPeakBonusPoints, reservation.Id, now));
        }

        // Reward taking a shared reserved spot, scaled by commute distance — also only on real use.
        var spot = await dbContext.ParkingSpots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == reservation.SpotId, cancellationToken);
        Guid? sharedSpotOwner = null;
        string? sharedSpotCode = null;
        if (spot is { OwnerId: { } owner } && owner != userId)
        {
            // Tell the resident their shared spot actually got used (their sharing paid off).
            sharedSpotOwner = owner;
            sharedSpotCode = spot.Code;

            // Only a verified home address counts, so a spoofed far address earns nothing.
            var home = await dbContext.Users.Where(u => u.Id == userId)
                .Select(u => new { u.CommuteDistanceKm, u.HomeVerified })
                .FirstOrDefaultAsync(cancellationToken);
            var distanceKm = home is { HomeVerified: true } ? home.CommuteDistanceKm : null;
            var takenPoints = policy.ComputeSharedTakenReward(distanceKm);
            if (takenPoints > 0)
            {
                score.RewardSharedSpotTaken(takenPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.SharedSpotTaken, takenPoints, reservation.Id, now, spot.Code));
            }
        }

        var newBadges = await ReevaluateBadgesAsync(dbContext, score, now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await NotifyNewBadgesAsync(userId, newBadges, cancellationToken);

        if (sharedSpotOwner is { } sharedOwnerId)
        {
            await notifications.NotifyAsync(sharedOwnerId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_ShareUsed_Title"],
                messages["Parking_Notify_ShareUsed_Body", sharedSpotCode!], cancellationToken);
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> ReleaseAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);

        // Same rationale as in ReserveCoreAsync: the daily-cap read and the reward insert below
        // must be one atomic step — at plain read-committed two concurrent releases both pass the
        // cap check and both collect the reward, sailing past MaxRewardedReleasesPerDay.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        reservation.Release(now);

        // An early enough release frees the spot for others, so the charge is refunded in full.
        var timely = policy.QualifiesForReleaseReward(reservation.StartUtc, now);

        // The reward applies on top when the user hasn't hit the daily cap on rewarded releases —
        // otherwise a reserve/release loop could farm points without ever parking. "Today" is the
        // local day at the lot, so the cap resets at local midnight rather than at UTC midnight.
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var (todayStart, _) = SiteTime.Day(SiteTime.Today(now, timeZone), timeZone);
        var rewardedToday = await dbContext.PointsLedgerEntries.CountAsync(
            e => e.UserId == userId && e.Reason == IncentiveReason.ReleasedReservation && e.OccurredAtUtc >= todayStart,
            cancellationToken);
        var rewardEligible = timely && rewardedToday < policy.MaxRewardedReleasesPerDay;

        var newBadges = new List<ParkingBadge>();
        if ((timely && reservation.CreditsCharged > 0) || rewardEligible)
        {
            var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);

            if (timely && reservation.CreditsCharged > 0)
            {
                score.RefundCredits(reservation.CreditsCharged, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ReservationRefund, reservation.CreditsCharged, reservation.Id, now));
            }

            if (rewardEligible)
            {
                // Scale the reward by how badly the spot was needed: lot occupancy + people queued for it.
                var occupancy = await ComputeOccupancyAsync(dbContext, reservation.StartUtc, reservation.EndUtc, cancellationToken);
                var waitingCount = await dbContext.QueueEntries.CountAsync(q => q.Status == QueueEntryStatus.Waiting
                    && q.StartUtc < reservation.EndUtc && q.EndUtc > reservation.StartUtc, cancellationToken);
                var releaseReward = policy.ComputeReleaseReward(occupancy, waitingCount);

                score.RewardRelease(releaseReward, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ReleasedReservation, releaseReward, reservation.Id, now));
                newBadges = await ReevaluateBadgesAsync(dbContext, score, now, cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await NotifyNewBadgesAsync(userId, newBadges, cancellationToken);

        // The freed spot may now satisfy someone on the waitlist.
        await ProcessQueueAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> CancelAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        reservation.Cancel();

        // Cancelling early enough to re-let the spot refunds the charge; a late cancel forfeits it.
        if (reservation.CreditsCharged > 0 && policy.QualifiesForReleaseReward(reservation.StartUtc, now))
        {
            var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
            score.RefundCredits(reservation.CreditsCharged, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.ReservationRefund, reservation.CreditsCharged, reservation.Id, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // The freed spot may now satisfy someone on the waitlist.
        await ProcessQueueAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<int> SweepNoShowsAsync(CancellationToken cancellationToken = default)
    {
        int swept;
        await MaintenanceGate.WaitAsync(cancellationToken);
        try
        {
            swept = await SweepNoShowsCoreAsync(cancellationToken);
        }
        finally
        {
            MaintenanceGate.Release();
        }

        // No-shows freed their spots; offer them to the waitlist (takes the gate itself).
        await ProcessQueueAsync(cancellationToken);
        return swept;
    }

    private async Task<int> SweepNoShowsCoreAsync(CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var threshold = now - policy.NoShowGracePeriod;

        // Grace runs from whichever is later: the window's start, or the moment the booking was
        // made. Booking a window already in progress (the rest of today, a just-claimed queue
        // offer) must leave the holder the full grace period to check in, not sweep them seconds
        // after a successful booking.
        var due = await dbContext.Reservations
            .Where(r => r.Status == ReservationStatus.Reserved
                && r.StartUtc <= threshold
                && r.CreatedAtUtc <= threshold)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var spotIds = due.Select(r => r.SpotId).Distinct().ToList();
        var spots = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        // Residents whose shared spot a guest wasted: notify them and claw back part of the reward.
        var ownerNotices = new List<(Guid OwnerId, string Code, int Clawback)>();

        foreach (var reservation in due)
        {
            reservation.MarkNoShow();
            var score = await GetOrCreateScoreAsync(dbContext, reservation.UserId, cancellationToken);

            if (reservation.FromQueue)
            {
                // Uncompromising penalty for wasting a scarce spot claimed off the waitlist: a much
                // bigger reputation hit, an extra credit fine, a queue ban and a cut to next month's allowance.
                score.PenalizeNoShow(policy.QueueNoShowPenaltyPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId, IncentiveReason.QueueNoShowPenalty, -policy.QueueNoShowPenaltyPoints, reservation.Id, now));

                if (policy.QueueNoShowCreditPenalty > 0)
                {
                    score.PenalizeCredits(policy.QueueNoShowCreditPenalty, now);
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        reservation.UserId, IncentiveReason.QueueNoShowFine, -policy.QueueNoShowCreditPenalty, reservation.Id, now));
                }

                if (policy.QueueNoShowBanDays > 0)
                {
                    score.BanFromQueue(now.AddDays(policy.QueueNoShowBanDays), now);
                }

                score.AddAllowancePenalty(policy.QueueNoShowAllowancePenalty, now);
            }
            else
            {
                score.PenalizeNoShow(policy.NoShowPenaltyPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId, IncentiveReason.NoShowPenalty, -policy.NoShowPenaltyPoints, reservation.Id, now));
            }

            if (spots.TryGetValue(reservation.SpotId, out var spot) && spot.OwnerId is { } ownerId && ownerId != reservation.UserId)
            {
                var date = DateOnly.FromDateTime(reservation.StartUtc.Date);
                var release = await dbContext.SpotReleases.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.SpotId == reservation.SpotId && r.Date == date, cancellationToken);
                var clawback = policy.ComputeShareClawback(release?.AwardedPoints ?? 0);
                if (clawback > 0)
                {
                    var ownerScore = await GetOrCreateScoreAsync(dbContext, ownerId, cancellationToken);
                    ownerScore.RevokeSharePoints(clawback, now);
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        ownerId, IncentiveReason.ResidentShareWasted, -clawback, reservation.Id, now, spot.Code));
                }

                ownerNotices.Add((ownerId, spot.Code, clawback));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var reservation in due)
        {
            var code = spots.TryGetValue(reservation.SpotId, out var s) ? s.Code : string.Empty;
            if (reservation.FromQueue)
            {
                await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
                    messages["Parking_Notify_QueueNoShow_Title"],
                    messages["Parking_Notify_QueueNoShow_Body", code, policy.QueueNoShowPenaltyPoints, policy.QueueNoShowCreditPenalty],
                    email: true, cancellationToken);
            }
            else
            {
                await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
                    messages["Parking_Notify_NoShow_Title"],
                    messages["Parking_Notify_NoShow_Body", code, policy.NoShowPenaltyPoints],
                    email: true, cancellationToken);
            }
        }

        foreach (var (ownerId, code, clawback) in ownerNotices)
        {
            var body = clawback > 0
                ? messages["Parking_Notify_ShareWasted_Body", code, clawback]
                : messages["Parking_Notify_ShareWastedNoPenalty_Body", code];
            await notifications.NotifyAsync(ownerId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_ShareWasted_Title"], body, cancellationToken);
        }

        return due.Count;
    }

    public async Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Remind once when the start is near (within the lead time) and the holder can still act
        // (before the no-show deadline at start + grace).
        var remindFrom = now - policy.NoShowGracePeriod;
        var remindTo = now + policy.ReminderLeadTime;

        var due = await dbContext.Reservations
            .Where(r => r.Status == ReservationStatus.Reserved && r.ReminderSentAtUtc == null
                && r.StartUtc > remindFrom && r.StartUtc <= remindTo)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var spotCodes = await GetSpotCodesAsync(dbContext, due, cancellationToken);

        foreach (var reservation in due)
        {
            reservation.MarkReminderSent(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var reservation in due)
        {
            var code = spotCodes.GetValueOrDefault(reservation.SpotId, string.Empty);
            await notifications.NotifyAsync(reservation.UserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_Reminder_Title"],
                messages["Parking_Notify_Reminder_Body", code],
                cancellationToken);
        }

        return due.Count;
    }

    public async Task<int> GrantDueMonthlyCreditsAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var period = ParkerScore.PeriodOf(now);

        var due = await dbContext.ParkerScores
            .Where(s => s.LastCreditGrantPeriod < period)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var granted = new List<(Guid UserId, int Amount)>();
        foreach (var score in due)
        {
            var rank = IncentivePolicy.TierRank(policy.TierFor(score.Points));
            var amount = score.GrantMonthlyCreditIfDue(policy.AllowanceForTier(rank), period, now);
            if (amount > 0)
            {
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    score.UserId, IncentiveReason.MonthlyCreditGrant, amount, null, now));
                granted.Add((score.UserId, amount));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var (userId, amount) in granted)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_MonthlyCredit_Title"],
                messages["Parking_Notify_MonthlyCredit_Body", amount], cancellationToken);
        }

        return due.Count;
    }

    public async Task<int> DecayReputationAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        if (policy.ReputationDecayPercent <= 0 || policy.ReputationDecayIntervalDays <= 0)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var threshold = now.AddDays(-policy.ReputationDecayIntervalDays);

        // Only scores due for a decay step (or never baselined yet).
        var due = await dbContext.ParkerScores
            .Where(s => s.LastDecayUtc == null || s.LastDecayUtc <= threshold)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var decayed = 0;
        foreach (var score in due)
        {
            var delta = score.DecayReputationIfDue(policy.ReputationDecayPercent, policy.ReputationDecayIntervalDays, now);
            if (delta != 0)
            {
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    score.UserId, IncentiveReason.ReputationDecay, delta, null, now));
                decayed++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return decayed;
    }

    public async Task<double> MeasurePeakOccupancyAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);
        var start = SiteTime.At(today, policy.PeakStart, timeZone);
        var end = SiteTime.At(today, policy.PeakEnd, timeZone);
        if (end <= start)
        {
            return 0.0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ComputeOccupancyAsync(dbContext, start, end, cancellationToken);
    }

    public async Task<IReadOnlyList<QueueEntryDto>> GetMyQueueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var mine = await dbContext.QueueEntries.AsNoTracking()
            .Where(q => q.UserId == userId && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered))
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (mine.Count == 0)
        {
            return [];
        }

        var waiting = await dbContext.QueueEntries.AsNoTracking()
            .Where(q => q.Status == QueueEntryStatus.Waiting)
            .Select(q => new { q.UserId, q.StartUtc, q.EndUtc, q.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var waitingUserIds = waiting.Select(w => w.UserId).Distinct().ToList();
        var pointsByUser = await dbContext.ParkerScores.AsNoTracking()
            .Where(s => waitingUserIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.Points, cancellationToken);

        int Priority(Guid uid, DateTimeOffset created) =>
            IncentivePolicy.TierRank(policy.TierFor(pointsByUser.GetValueOrDefault(uid))) * policy.QueuePriorityPerTier
            + (int)(now - created).TotalMinutes;

        var spotIds = mine.Where(q => q.OfferedSpotId != null).Select(q => q.OfferedSpotId!.Value).ToList();
        var codes = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        return mine.Select(q =>
        {
            // Position reflects the same tier+wait priority the matcher uses: how many overlapping
            // entries currently outrank me, plus one.
            var myPriority = Priority(q.UserId, q.CreatedAtUtc);
            var position = q.Status == QueueEntryStatus.Offered
                ? 0
                : 1 + waiting.Count(w => w.StartUtc < q.EndUtc && w.EndUtc > q.StartUtc
                    && Priority(w.UserId, w.CreatedAtUtc) > myPriority);

            return new QueueEntryDto(
                q.Id, q.StartUtc, q.EndUtc, q.Status, position,
                q.OfferedSpotId,
                q.OfferedSpotId is { } sid ? codes.GetValueOrDefault(sid) : null,
                q.OfferExpiresAtUtc);
        }).ToList();
    }

    public async Task<ParkingResult> JoinQueueAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (endUtc <= startUtc)
        {
            return ParkingResult.Failure("Parking_Error_InvalidWindow");
        }

        if (endUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // A queue no-show bars the user from the waitlist for a cooldown.
        var bannedUntil = await dbContext.ParkerScores.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.QueueBannedUntilUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (bannedUntil is { } until && until > now)
        {
            return ParkingResult.Failure("Parking_Queue_Error_Banned");
        }

        // One active waitlist entry per user per overlapping window.
        var alreadyQueued = await dbContext.QueueEntries.AnyAsync(q => q.UserId == userId
            && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
            && q.StartUtc < endUtc && q.EndUtc > startUtc, cancellationToken);
        if (alreadyQueued)
        {
            return ParkingResult.Failure("Parking_Queue_Error_Already");
        }

        var ownConflict = await dbContext.Reservations.AnyAsync(r => r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < endUtc && r.EndUtc > startUtc, cancellationToken);
        if (ownConflict)
        {
            return ParkingResult.Failure("Parking_Error_OwnConflict");
        }

        // The waitlist only opens when the window is genuinely full (nothing the user could book now).
        var available = await GetAvailableSpotsAsync(startUtc, endUtc, cancellationToken);
        if (available.Count > 0)
        {
            return ParkingResult.Failure("Parking_Queue_Error_NotFull");
        }

        dbContext.QueueEntries.Add(new QueueEntry(userId, startUtc, endUtc, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> LeaveQueueAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await dbContext.QueueEntries.FirstOrDefaultAsync(q => q.Id == queueEntryId && q.UserId == userId, cancellationToken);
        if (entry is null || !entry.IsActive)
        {
            return ParkingResult.Failure("Parking_Queue_Error_NotFound");
        }

        var wasOffered = entry.Status == QueueEntryStatus.Offered;
        entry.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);

        // Leaving while holding an offer frees that spot for the next in line.
        if (wasOffered)
        {
            await ProcessQueueAsync(cancellationToken);
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> ClaimQueueOfferAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await dbContext.QueueEntries.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == queueEntryId && q.UserId == userId, cancellationToken);
        if (entry is null || entry.Status != QueueEntryStatus.Offered || entry.OfferedSpotId is not { } spotId)
        {
            return ParkingResult.Failure("Parking_Queue_Error_NoOffer");
        }

        if (entry.OfferExpiresAtUtc is { } expires && expires <= now)
        {
            return ParkingResult.Failure("Parking_Queue_Error_OfferExpired");
        }

        // Claiming is just reserving the held spot — same dynamic price and balance check as any booking,
        // but flagged so a no-show on it is punished harder. The offer is re-checked and marked claimed
        // inside that booking's transaction, so the two can't come apart.
        return await ReserveCoreAsync(userId, spotId, entry.StartUtc, entry.EndUtc, fromQueue: true, queueEntryId, cancellationToken);
    }

    public async Task<int> ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        await MaintenanceGate.WaitAsync(cancellationToken);
        try
        {
            return await ProcessQueueCoreAsync(cancellationToken);
        }
        finally
        {
            MaintenanceGate.Release();
        }
    }

    private async Task<int> ProcessQueueCoreAsync(CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var active = await dbContext.QueueEntries
            .Where(q => q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            return 0;
        }

        // Expire passed windows; lapse stale offers back to waiting so the spot can move on.
        foreach (var entry in active)
        {
            if (entry.EndUtc <= now)
            {
                entry.Expire();
            }
            else if (entry.Status == QueueEntryStatus.Offered && entry.OfferExpiresAtUtc is { } expires && expires <= now)
            {
                // Missed offers demote: the entry rejoins at the back so the next freed spot goes
                // to the next in line, not back to the same unresponsive head of the queue.
                entry.RequeueAfterMissedOffer(now);
            }
        }

        // Spots still under a valid offer remain held for their entry and are not re-offered.
        var heldSpotIds = active
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId is not null)
            .Select(q => q.OfferedSpotId!.Value)
            .ToHashSet();

        // Serve by a blend of loyalty tier (head start) and how long they've waited, so higher tiers
        // are favoured but a long-waiting lower tier still catches up (no starvation).
        var waitingUserIds = active.Where(q => q.Status == QueueEntryStatus.Waiting).Select(q => q.UserId).Distinct().ToList();
        var pointsByUser = await dbContext.ParkerScores.AsNoTracking()
            .Where(s => waitingUserIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.Points, cancellationToken);

        int Priority(QueueEntry q) =>
            IncentivePolicy.TierRank(policy.TierFor(pointsByUser.GetValueOrDefault(q.UserId))) * policy.QueuePriorityPerTier
            + (int)(now - q.CreatedAtUtc).TotalMinutes;

        var offerHold = TimeSpan.FromMinutes(policy.QueueOfferMinutes);
        var offers = new List<(Guid UserId, Guid SpotId)>();
        foreach (var entry in active
            .Where(q => q.Status == QueueEntryStatus.Waiting && q.EndUtc > now)
            .OrderByDescending(Priority)
            .ThenBy(q => q.CreatedAtUtc))
        {
            // A user who already holds a reservation for the window could never claim the offer
            // (own-conflict) — skip them rather than pinning a spot on an unclaimable hold.
            var hasOverlappingReservation = await dbContext.Reservations.AnyAsync(r => r.UserId == entry.UserId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < entry.EndUtc && r.EndUtc > entry.StartUtc, cancellationToken);
            if (hasOverlappingReservation)
            {
                continue;
            }

            var candidates = await AvailableSpotIdsAsync(dbContext, policy, timeZone, entry.StartUtc, entry.EndUtc, now, cancellationToken);
            var spotId = candidates.FirstOrDefault(id => !heldSpotIds.Contains(id));
            if (spotId == Guid.Empty)
            {
                continue;
            }

            entry.Offer(spotId, now + offerHold);
            heldSpotIds.Add(spotId);
            offers.Add((entry.UserId, spotId));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (offers.Count > 0)
        {
            var codes = await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => offers.Select(o => o.SpotId).Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

            foreach (var (offerUserId, spotId) in offers)
            {
                await notifications.NotifyAsync(offerUserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                    messages["Parking_Notify_QueueOffer_Title"],
                    messages["Parking_Notify_QueueOffer_Body", codes.GetValueOrDefault(spotId, string.Empty), policy.QueueOfferMinutes],
                    email: true, cancellationToken);
            }
        }

        return offers.Count;
    }

    // Raw availability for a window (active, unreserved, owned-spot visibility) without the waitlist
    // hold filter — the queue matcher manages holds itself in memory.
    private static async Task<List<Guid>> AvailableSpotIdsAsync(D3ParkingDbContext dbContext, IncentivePolicy policy, TimeZoneInfo timeZone, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var requestDate = SiteTime.Today(startUtc, timeZone);
        var autoShareActive = policy.IsResidentAutoShareActive(requestDate, now, timeZone);

        var blocked = dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < endUtc && r.EndUtc > startUtc)
            .Select(r => r.SpotId);

        var released = dbContext.SpotReleases.Where(r => r.Date == requestDate).Select(r => r.SpotId);

        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && !blocked.Contains(s.Id)
                && (s.OwnerId == null || autoShareActive || released.Contains(s.Id)))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, string>> GetSpotCodesAsync(D3ParkingDbContext dbContext, IReadOnlyList<Reservation> reservations, CancellationToken cancellationToken)
    {
        var spotIds = reservations.Select(r => r.SpotId).Distinct().ToList();
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);
    }

    private static Task<Reservation?> FindOwnedAsync(D3ParkingDbContext dbContext, Guid userId, Guid reservationId, CancellationToken cancellationToken) =>
        dbContext.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId && r.UserId == userId, cancellationToken);

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

    private static async Task<List<ParkingBadge>> ReevaluateBadgesAsync(D3ParkingDbContext dbContext, ParkerScore score, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var newlyEarned = new List<ParkingBadge>();
        var earned = ParkingBadgeRules.Earned(score).ToHashSet();
        if (earned.Count == 0)
        {
            return newlyEarned;
        }

        var existing = await dbContext.UserBadges
            .Where(b => b.UserId == score.UserId)
            .Select(b => b.Badge)
            .ToListAsync(cancellationToken);

        foreach (var badge in earned)
        {
            if (!existing.Contains(badge))
            {
                dbContext.UserBadges.Add(new UserBadge(score.UserId, badge, now));
                newlyEarned.Add(badge);
            }
        }

        return newlyEarned;
    }

    private async Task NotifyNewBadgesAsync(Guid userId, IReadOnlyList<ParkingBadge> badges, CancellationToken cancellationToken)
    {
        foreach (var badge in badges)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_Badge_Title"],
                messages["Parking_Notify_Badge_Body", messages[$"Parking_BadgeName_{badge}"]], cancellationToken);
        }
    }
}
