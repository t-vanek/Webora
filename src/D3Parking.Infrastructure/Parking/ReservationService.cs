using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Authorization;
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

    // Daily cap on "I can't park" reports per user — see ReportBlockedSpotAsync.
    private const int MaxBlockedReportsPerDay = 2;

    // How long the apology voucher (one free reservation) stays redeemable — from the grant for
    // the pending window, restarted from the approval once the spot manager confirms. Together
    // with the one-unredeemed-voucher-per-user rule this caps what faked reports could ever mint.
    public static readonly TimeSpan ApologyVoucherValidity = TimeSpan.FromDays(30);

    // Formats the mandatory photo proof may come in — kept to what a browser renders inline,
    // so the spot manager's review never needs a download. Size is bounded by BlockedSpotPhoto.MaxBytes.
    private static readonly string[] AllowedPhotoContentTypes = ["image/jpeg", "image/png", "image/webp"];

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
        // Visitor-type spots belong to the reception's visitor agenda, never to the employee pool.
        // Natural code order (D3-2 before D3-10) needs the comparer, so sort in memory.
        var available = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor
                && !blocked.Contains(s.Id) && !held.Contains(s.Id)
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

    public async Task<PagedResult<ReservationDto>> GetMyReservationsPageAsync(
        Guid userId, int pageIndex, int pageSize, CancellationToken cancellationToken = default)
    {
        // Clamped here, not trusted from the caller: the page size decides how much a single request
        // can ask the database to materialise.
        var size = Math.Clamp(pageSize, 1, 100);
        var index = Math.Max(0, pageIndex);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var mine = dbContext.Reservations.AsNoTracking().Where(r => r.UserId == userId);
        var total = await mine.CountAsync(cancellationToken);

        // A page past the end (the last booking on it was just cancelled away, say) walks back to the
        // last page that exists rather than rendering an empty table.
        var lastIndex = total == 0 ? 0 : (total - 1) / size;
        index = Math.Min(index, lastIndex);

        var items = await (from r in mine
                           join s in dbContext.ParkingSpots on r.SpotId equals s.Id
                           orderby r.StartUtc descending
                           select new ReservationDto(
                               r.Id, r.SpotId, s.Code, s.Type, r.UserId,
                               r.StartUtc, r.EndUtc, r.Status, r.IsOffPeak, r.CreatedAtUtc,
                               r.CheckedInAtUtc, r.ReleasedAtUtc, r.CompletedAtUtc))
            .Skip(index * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return new PagedResult<ReservationDto>(items, total, index, size);
    }

    public async Task<ReservationDto?> GetMyReservationAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await (from r in dbContext.Reservations.AsNoTracking()
                      join s in dbContext.ParkingSpots on r.SpotId equals s.Id
                      where r.Id == reservationId && r.UserId == userId
                      select new ReservationDto(
                          r.Id, r.SpotId, s.Code, s.Type, r.UserId,
                          r.StartUtc, r.EndUtc, r.Status, r.IsOffPeak, r.CreatedAtUtc,
                          r.CheckedInAtUtc, r.ReleasedAtUtc, r.CompletedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // RetryAsync turns a lost race under the serializable transaction (deadlock victim, stale
    // rowversion) into a fresh attempt whose checks re-run against the winner's committed state —
    // the user gets the friendly conflict failure instead of an error page.
    public Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool useVoucher = false, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(
            () => ReserveCoreAsync(userId, spotId, startUtc, endUtc, fromQueue: false, queueEntryId: null, useVoucher, cancellationToken),
            cancellationToken);

    public async Task<ApologyVoucherDto?> GetMyApologyVoucherAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        // An approved voucher (redeemable now) beats a pending one (informational only); the cap
        // allows at most one of the pair to exist, so ordering by status is just belt-and-braces.
        return await dbContext.ApologyVouchers.AsNoTracking()
            .Where(v => v.UserId == userId && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now
                && (v.Status == ApologyVoucherStatus.Approved || v.Status == ApologyVoucherStatus.PendingApproval))
            .OrderBy(v => v.Status == ApologyVoucherStatus.Approved ? 0 : 1)
            .ThenBy(v => v.ExpiresAtUtc)
            .Select(v => new ApologyVoucherDto(v.Id, v.Status, v.ExpiresAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ParkingResult> ReserveCoreAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool fromQueue, Guid? queueEntryId, bool useVoucher, CancellationToken cancellationToken)
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

        // Visitor spots are the reception's territory (see VisitorBookingService) — an employee
        // booking would collide with a guest whom the reservation tables know nothing about.
        if (spot.Type == ParkingSpotType.Visitor)
        {
            return ParkingResult.Failure("Parking_Visitor_Error_NotVisitorSpot");
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

        // A spot held for someone else's waitlist offer can't be booked out from under them —
        // except by the spot's own resident, whose right of first refusal outranks a pending
        // offer (a hold is not a booking). The withdrawn waiter keeps their queue position and
        // hears about it after the commit; the maintenance loop deals them the next freed spot.
        var withdrawnWaiters = new List<Guid>();
        if (spot.OwnerId == userId)
        {
            var holds = await dbContext.QueueEntries
                .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId == spotId
                    && q.UserId != userId && q.OfferExpiresAtUtc > now
                    && q.StartUtc < endUtc && q.EndUtc > startUtc)
                .ToListAsync(cancellationToken);
            foreach (var hold in holds)
            {
                hold.WithdrawOffer();
                withdrawnWaiters.Add(hold.UserId);
            }
        }
        else
        {
            var heldByOther = await dbContext.QueueEntries.AnyAsync(q => q.Status == QueueEntryStatus.Offered
                && q.OfferedSpotId == spotId && q.UserId != userId && q.OfferExpiresAtUtc > now
                && q.StartUtc < endUtc && q.EndUtc > startUtc, cancellationToken);
            if (heldByOther)
            {
                return ParkingResult.Failure("Parking_Error_SpotHeld");
            }
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

        // The apology voucher absorbs the whole dynamic price — peak surcharge included — instead
        // of the wallet. It is redeemed inside this transaction; a timely cancel/release restores
        // it (see RestoreVoucherAsync), the same terms under which credits would be refunded.
        // Only an approved voucher counts: one still pending the spot manager's review holds no
        // value yet, and a rejected one never will.
        ApologyVoucher? voucher = null;
        if (useVoucher)
        {
            voucher = await dbContext.ApologyVouchers
                .Where(v => v.UserId == userId && v.Status == ApologyVoucherStatus.Approved
                    && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now)
                .OrderBy(v => v.ExpiresAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (voucher is null)
            {
                return ParkingResult.Failure("Parking_Error_VoucherUnavailable");
            }
        }

        if (voucher is null && score.Credits < cost)
        {
            return ParkingResult.Failure("Parking_Error_InsufficientCredit");
        }

        var reservation = new Reservation(spotId, userId, startUtc, endUtc, isOffPeak, now, voucher is null ? cost : 0, fromQueue);
        dbContext.Reservations.Add(reservation);
        if (voucher is not null)
        {
            voucher.Redeem(reservation.Id, cost, now);
        }
        else
        {
            score.ChargeCredits(cost, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.ReservationCharge, -cost, reservation.Id, now, spot.Code));
        }

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

        foreach (var waiterId in withdrawnWaiters)
        {
            await notifications.NotifyAsync(waiterId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_QueueHoldReclaimed_Title"],
                messages["Parking_Notify_QueueHoldReclaimed_Body", spot.Code], cancellationToken);
        }

        // Warn when the wallet can no longer cover even a base-price booking. Bell/push only: the
        // warning is neither actionable on a deadline nor a formal record, so an email would just
        // train people to ignore the sender.
        if (score.Credits < policy.BaseReservationCost)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_LowBalance_Title"],
                messages["Parking_Notify_LowBalance_Body", score.Credits], cancellationToken);
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
        // Visitor spots are outside the employee pool, so they must not dilute the occupancy
        // (and with it the dynamic price and the release rewards).
        var activeSpots = await dbContext.ParkingSpots.CountAsync(
            s => s.IsActive && s.Type != ParkingSpotType.Visitor, cancellationToken);
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

    // The user-facing mutations below run under OptimisticConcurrency.RetryAsync: their status
    // guards validate an in-memory snapshot, and the rowversion token turns a concurrent commit
    // (double-click, second tab, the no-show sweep) into a retry that re-reads and re-decides —
    // typically landing on the friendly InvalidState failure instead of a double refund.
    public Task<ParkingResult> CheckInAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => CheckInCoreAsync(userId, reservationId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> CheckInCoreAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken)
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

    public Task<ParkingResult> CompleteAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => CompleteCoreAsync(userId, reservationId, cancellationToken), cancellationToken);

    // The once-per-day reward package check (rewardedToday) is not atomic with the reward inserts,
    // but every reward path also mutates the ParkerScore row: when two completions race, the
    // second save trips the score's rowversion, retries, and then sees the first one's marker.
    private async Task<ParkingResult> CompleteCoreAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken)
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
        var tierBefore = policy.TierFor(score.Points);
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
        await NotifyTierUpAsync(userId, tierBefore, policy.TierFor(score.Points), cancellationToken);

        if (sharedSpotOwner is { } sharedOwnerId)
        {
            await notifications.NotifyAsync(sharedOwnerId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_ShareUsed_Title"],
                messages["Parking_Notify_ShareUsed_Body", sharedSpotCode!], cancellationToken);
        }

        return ParkingResult.Success;
    }

    public Task<ParkingResult> ReleaseAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReleaseCoreAsync(userId, reservationId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ReleaseCoreAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Same rationale as in ReserveCoreAsync: the daily-cap read and the reward insert below
        // must be one atomic step — at plain read-committed two concurrent releases both pass the
        // cap check and both collect the reward, sailing past MaxRewardedReleasesPerDay. The
        // reservation load and its status guard sit inside the transaction too; read before it,
        // they would validate a snapshot the transaction never protects.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        reservation.Release(now);

        // An early enough release frees the spot for others, so the charge is refunded in full.
        var timely = policy.QualifiesForReleaseReward(reservation.StartUtc, now);

        // The reward applies on top when the user hasn't hit the daily cap on rewarded releases —
        // otherwise a reserve/release loop could farm points without ever parking. "Today" is the
        // local day at the lot, so the cap resets at local midnight rather than at UTC midnight.
        var (todayStart, _) = SiteTime.Day(SiteTime.Today(now, timeZone), timeZone);
        var rewardedToday = await dbContext.PointsLedgerEntries.CountAsync(
            e => e.UserId == userId && e.Reason == IncentiveReason.ReleasedReservation && e.OccurredAtUtc >= todayStart,
            cancellationToken);
        var rewardEligible = timely && rewardedToday < policy.MaxRewardedReleasesPerDay;

        // A voucher-paid booking gets its voucher back on the same timely terms as a refund.
        if (timely)
        {
            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);
        }

        var newBadges = new List<ParkingBadge>();
        LoyaltyTier? tierBefore = null;
        LoyaltyTier? tierAfter = null;
        if ((timely && reservation.CreditsCharged > 0) || rewardEligible)
        {
            var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
            tierBefore = policy.TierFor(score.Points);

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

            tierAfter = policy.TierFor(score.Points);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await NotifyNewBadgesAsync(userId, newBadges, cancellationToken);
        if (tierBefore is { } before && tierAfter is { } after)
        {
            await NotifyTierUpAsync(userId, before, after, cancellationToken);
        }

        // The freed spot may now satisfy someone on the waitlist.
        await ProcessQueueAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public Task<ParkingResult> CancelAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => CancelCoreAsync(userId, reservationId, cancellationToken), cancellationToken);

    // No explicit transaction: the single SaveChanges below is already atomic, and the rowversion
    // on the reservation makes the duplicate-cancel race (and cancel vs. release/sweep) retry
    // into the InvalidState failure instead of refunding twice.
    private async Task<ParkingResult> CancelCoreAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken)
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

        // Cancelling early enough to re-let the spot refunds the charge (or restores the apology
        // voucher that paid for it); a late cancel forfeits them.
        var timely = policy.QualifiesForReleaseReward(reservation.StartUtc, now);
        if (timely && reservation.CreditsCharged > 0)
        {
            var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
            score.RefundCredits(reservation.CreditsCharged, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.ReservationRefund, reservation.CreditsCharged, reservation.Id, now));
        }

        if (timely)
        {
            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // The freed spot may now satisfy someone on the waitlist.
        await ProcessQueueAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public Task<BlockedSpotOutcome> ReportBlockedSpotAsync(Guid userId, Guid reservationId, bool relocate, BlockedSpotPhoto? photo, string? blockerPlate = null, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReportBlockedSpotCoreAsync(userId, reservationId, relocate, photo, blockerPlate, cancellationToken), cancellationToken);

    private async Task<BlockedSpotOutcome> ReportBlockedSpotCoreAsync(Guid userId, Guid reservationId, bool relocate, BlockedSpotPhoto? photo, string? blockerPlate, CancellationToken cancellationToken)
    {
        // The photo proof is not optional: without it the report voids a booking penalty-free on
        // bare word, and the spot manager would have nothing to judge the apology voucher by.
        if (photo is null || photo.Content.Length == 0)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_PhotoRequired");
        }

        if (photo.Content.Length > BlockedSpotPhoto.MaxBytes)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_PhotoTooLarge");
        }

        if (!AllowedPhotoContentTypes.Contains(photo.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return BlockedSpotOutcome.Failure("Parking_Error_PhotoType");
        }

        // The SHA-256 fingerprint is the anti-reuse identity of the picture: the same file can
        // prove exactly one mismatch, ever — no matter who resubmits it or when.
        var photoHash = System.Security.Cryptography.SHA256.HashData(photo.Content);

        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The mismatch record, the void of the blocked reservation and the replacement booking
        // are one atomic decision — same serializable step (and rationale) as ReserveCoreAsync.
        // The reservation load, its status guard and the daily report cap all sit inside the
        // transaction: read before it, two concurrent reports would both pass the cap and both
        // void-and-refund the same reservation.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_InvalidState");
        }

        // Recording the spot's state only makes sense while the driver can actually be standing
        // in front of it — the same window in which check-in is allowed.
        var now = timeProvider.GetUtcNow();
        if (now < reservation.StartUtc - EarlyCheckInWindow || now >= reservation.EndUtc)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_BlockedReportWindow");
        }

        // The flow voids a reservation penalty-free with a full refund, so unlimited use would be
        // a free escape hatch from unwanted bookings after the refund cutoff. Two honest strikes
        // a day cover any realistic string of bad luck; admins see the rest in the trend view.
        var (dayStart, dayEnd) = SiteTime.Day(SiteTime.Today(now, timeZone), timeZone);
        var reportsToday = await dbContext.OccupancyMismatches.CountAsync(m =>
            m.ReporterId == userId && m.ReportedAtUtc >= dayStart && m.ReportedAtUtc < dayEnd, cancellationToken);
        if (reportsToday >= MaxBlockedReportsPerDay)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_BlockedReportLimit");
        }

        // A photo already backing any report — this user's or anyone else's — proves nothing
        // twice. Checked inside the serializable transaction; the unique index on the hash is the
        // backstop for the race two identical uploads could still win concurrently (the loser
        // retries, re-reads, and lands here on the friendly failure).
        var photoAlreadyUsed = await dbContext.MismatchPhotos.AnyAsync(
            p => p.ContentHash == photoHash, cancellationToken);
        if (photoAlreadyUsed)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_PhotoReused");
        }

        // The plate is read off a stranger's car in a hurry — keep it verbatim (trimmed, upper-
        // cased, capped to the column); the admin view does the tolerant matching.
        string? recordedPlate = null;
        if (!string.IsNullOrWhiteSpace(blockerPlate))
        {
            var trimmed = blockerPlate.Trim().ToUpperInvariant();
            recordedPlate = trimmed.Length > 16 ? trimmed[..16] : trimmed;
        }

        var mismatch = new OccupancyMismatch(
            reservation.SpotId, reservation.Id, userId, reservation.StartUtc, reservation.EndUtc, now, recordedPlate);
        dbContext.OccupancyMismatches.Add(mismatch);
        dbContext.MismatchPhotos.Add(new MismatchPhoto(mismatch.Id, photo.ContentType.ToLowerInvariant(), photo.Content, photoHash, now));

        // Void without penalty: the driver stands in front of an occupied spot through no fault
        // of their own, so the charge comes back in full no matter how close to the start. The
        // spot stays bookable in the system (the squatter may leave any minute); repeated
        // mismatches on one spot surface in the admin trend view instead.
        reservation.Cancel();
        var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
        if (reservation.CreditsCharged > 0)
        {
            score.RefundCredits(reservation.CreditsCharged, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.ReservationRefund, reservation.CreditsCharged, reservation.Id, now));
        }

        string? relocatedCode = null;
        Reservation? replacement = null;
        if (relocate)
        {
            var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
            var effectiveStartUtc = reservation.StartUtc > now ? reservation.StartUtc : now;
            var candidates = await AvailableSpotIdsAsync(
                dbContext, policy, timeZone, effectiveStartUtc, reservation.EndUtc, now, cancellationToken);

            // Skip the blocked spot itself and spots held for waitlist offers.
            var held = await dbContext.QueueEntries
                .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId != null
                    && q.OfferExpiresAtUtc > now
                    && q.StartUtc < reservation.EndUtc && q.EndUtc > effectiveStartUtc)
                .Select(q => q.OfferedSpotId!.Value)
                .ToListAsync(cancellationToken);
            var replacementSpotId = candidates.FirstOrDefault(id => id != reservation.SpotId && !held.Contains(id));

            if (replacementSpotId != Guid.Empty)
            {
                // Carry the original charge over: the refund above plus an identical charge here
                // nets to zero for the wallet while the ledger keeps a clean trail of the move.
                replacement = new Reservation(replacementSpotId, userId, reservation.StartUtc, reservation.EndUtc,
                    reservation.IsOffPeak, now, reservation.CreditsCharged, reservation.FromQueue);
                dbContext.Reservations.Add(replacement);
                if (reservation.CreditsCharged > 0)
                {
                    score.ChargeCredits(reservation.CreditsCharged, now);
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        userId, IncentiveReason.ReservationCharge, -reservation.CreditsCharged, replacement.Id, now));
                }

                mismatch.MarkRelocated(replacementSpotId);
                relocatedCode = await dbContext.ParkingSpots.AsNoTracking()
                    .Where(s => s.Id == replacementSpotId)
                    .Select(s => s.Code)
                    .FirstAsync(cancellationToken);
            }
        }

        // A voucher that paid for the voided booking follows the same no-fault principle as the
        // credit refund: relocated, it re-points at the replacement (so its restore-on-timely-
        // cancel promise stays fulfillable); not relocated, it comes back — the free reservation
        // was consumed with zero parking received.
        if (replacement is not null)
        {
            var redeemedVoucher = await dbContext.ApologyVouchers
                .FirstOrDefaultAsync(v => v.RedeemedReservationId == reservation.Id, cancellationToken);
            redeemedVoucher?.TransferRedemption(replacement.Id);
        }
        else
        {
            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);
        }

        // The apology: one reservation free of charge, peak included — granted pending the spot
        // manager's review of the photo proof, so value only materializes from a human-confirmed
        // report. At most one pending-or-approved unredeemed voucher per user and it expires,
        // which caps what faked reports could ever stage. Evaluated after any voucher restore
        // above, so a restored voucher counts against the cap instead of stacking with a fresh
        // one. Rejected vouchers don't block: a past unfounded report must not mute a real one.
        var voucherGranted = false;
        var holdsUsableVoucher = await dbContext.ApologyVouchers.AnyAsync(v =>
            v.UserId == userId && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now
            && (v.Status == ApologyVoucherStatus.PendingApproval || v.Status == ApologyVoucherStatus.Approved),
            cancellationToken);
        if (!holdsUsableVoucher)
        {
            dbContext.ApologyVouchers.Add(new ApologyVoucher(userId, mismatch.Id, now, now + ApologyVoucherValidity));
            voucherGranted = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (voucherGranted)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_VoucherGranted_Title"],
                messages["Parking_Notify_VoucherGranted_Body"], cancellationToken);

            // The pending voucher is a call to action for whoever manages the spots: tell every
            // holder of the ManageSpots permission there is proof waiting to be judged.
            var spotCode = await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => s.Id == reservation.SpotId)
                .Select(s => s.Code)
                .FirstAsync(cancellationToken);
            foreach (var managerId in await GetSpotManagerIdsAsync(dbContext, cancellationToken))
            {
                await notifications.NotifyAsync(managerId, NotificationCategory.Administrative, NotificationLevel.Info,
                    messages["Parking_Notify_VoucherReview_Title"],
                    messages["Parking_Notify_VoucherReview_Body", spotCode], cancellationToken);
            }
        }

        return relocatedCode is null
            ? BlockedSpotOutcome.Recorded(voucherGranted)
            : BlockedSpotOutcome.Relocated(relocatedCode, voucherGranted);
    }

    // Everyone whose role carries the ManageSpots permission — the "spot manager" audience the
    // voucher review notifications go to (same role-claim shape the authorization layer checks).
    private static async Task<List<Guid>> GetSpotManagerIdsAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken)
    {
        var managerRoleIds = dbContext.RoleClaims
            .Where(c => c.ClaimType == D3ParkingClaimTypes.Permission
                && c.ClaimValue == Permissions.Parking.ManageSpots)
            .Select(c => c.RoleId);
        return await dbContext.UserRoles
            .Where(ur => managerRoleIds.Contains(ur.RoleId))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
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
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
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

        // Each no-show is saved as its own unit (reservation + penalties + clawback together): a
        // holder who checks in or cancels between our read and our save trips the reservation's
        // rowversion, and then the whole unit — penalties included — must be thrown away, not just
        // the status flip. A skipped reservation is re-evaluated on the next sweep tick.
        var swept = new List<Reservation>();
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

            (Guid OwnerId, string Code, int Clawback)? ownerNotice = null;
            if (spots.TryGetValue(reservation.SpotId, out var spot) && spot.OwnerId is { } ownerId && ownerId != reservation.UserId)
            {
                // SpotRelease.Date is the site-local calendar day; DateOnly.FromDateTime on the
                // stored offset only matched by the accident of every writer using site-local
                // offsets — a UTC-normalized start near local midnight would miss the release
                // row and silently skip the owner's clawback.
                var date = SiteTime.Today(reservation.StartUtc, timeZone);
                // Tracked on purpose: the release accumulates ClawedBackPoints, which caps the
                // day's total clawback at its awarded reward — several no-shows on one shared day
                // must not stack percentages past what the owner ever earned. Within this batch
                // EF's identity map hands back the same instance, so the cap holds across the
                // loop as well as across sweeps.
                var release = await dbContext.SpotReleases
                    .FirstOrDefaultAsync(r => r.SpotId == reservation.SpotId && r.Date == date, cancellationToken);
                var clawback = release is null
                    ? 0
                    : release.RegisterClawback(policy.ComputeShareClawback(release.AwardedPoints));
                if (clawback > 0)
                {
                    var ownerScore = await GetOrCreateScoreAsync(dbContext, ownerId, cancellationToken);
                    ownerScore.RevokeSharePoints(clawback, now);
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        ownerId, IncentiveReason.ResidentShareWasted, -clawback, reservation.Id, now, spot.Code));
                }

                ownerNotice = (ownerId, spot.Code, clawback);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                OptimisticConcurrency.DiscardPendingChanges(dbContext);
                continue;
            }

            swept.Add(reservation);
            if (ownerNotice is { } notice)
            {
                ownerNotices.Add(notice);
            }
        }

        foreach (var reservation in swept)
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

        return swept.Count;
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

        // A conflicted row means the holder cancelled or checked in while we were reading — no
        // reminder needed; the save keeps the rest and the detached rows are simply not notified.
        await OptimisticConcurrency.SaveSkippingConflictsAsync(dbContext, cancellationToken);

        var reminded = 0;
        foreach (var reservation in due)
        {
            if (dbContext.Entry(reservation).State == EntityState.Detached)
            {
                continue;
            }

            reminded++;
            var code = spotCodes.GetValueOrDefault(reservation.SpotId, string.Empty);
            await notifications.NotifyAsync(reservation.UserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_Reminder_Title"],
                messages["Parking_Notify_Reminder_Body", code],
                cancellationToken);
        }

        return reminded;
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

        // Per-score save: a user who books at the same moment gets the grant inside their own
        // serializable booking transaction, which makes this batch's copy stale — its save then
        // trips the rowversion and the unit (grant + ledger row) is discarded instead of granting
        // twice and erasing the booking's charge.
        var granted = new List<(Guid UserId, int Amount)>();
        foreach (var score in due)
        {
            var rank = IncentivePolicy.TierRank(policy.TierFor(score.Points));
            var amount = score.GrantMonthlyCreditIfDue(policy.AllowanceForTier(rank), period, now);
            if (amount > 0)
            {
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    score.UserId, IncentiveReason.MonthlyCreditGrant, amount, null, now));
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                OptimisticConcurrency.DiscardPendingChanges(dbContext);
                continue;
            }

            if (amount > 0)
            {
                granted.Add((score.UserId, amount));
            }
        }

        foreach (var (userId, amount) in granted)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_MonthlyCredit_Title"],
                messages["Parking_Notify_MonthlyCredit_Body", amount], cancellationToken);
        }

        // The maintenance log reports this as "monthly credit grants", so count actual grants,
        // not rows examined.
        return granted.Count;
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

        // Per-score save, same shape as the monthly grant batch: a concurrent completion/refund
        // would otherwise be overwritten by this batch's stale absolute Points value, leaving the
        // ledger and the wallet disagreeing. A conflicted score keeps its old LastDecayUtc and is
        // decayed on the next tick instead.
        var decayed = 0;
        foreach (var score in due)
        {
            var delta = score.DecayReputationIfDue(policy.ReputationDecayPercent, policy.ReputationDecayIntervalDays, now);
            if (delta != 0)
            {
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    score.UserId, IncentiveReason.ReputationDecay, delta, null, now));
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                OptimisticConcurrency.DiscardPendingChanges(dbContext);
                continue;
            }

            if (delta != 0)
            {
                decayed++;
            }
        }

        return decayed;
    }

    public async Task<double?> MeasurePeakOccupancyAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);
        var start = SiteTime.At(today, policy.PeakStart, timeZone);
        var end = SiteTime.At(today, policy.PeakEnd, timeZone);
        if (end <= start)
        {
            return null;
        }

        // The controller wants "how full was today's peak", which only exists once the window is
        // over. Measured live, the number was near-zero every night and weekend (completed
        // reservations drop out of Reserved/CheckedIn), and the controller dutifully walked the
        // surcharge down to its minimum by every morning regardless of real peak demand.
        if (now < end)
        {
            return null;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var activeSpots = await dbContext.ParkingSpots.CountAsync(
            s => s.IsActive && s.Type != ParkingSpotType.Visitor, cancellationToken);
        if (activeSpots == 0)
        {
            return null;
        }

        // Completed counts — the spot was genuinely used during the window; no-shows and cancels
        // do not, they represent demand that never parked.
        var occupied = await dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn
                    || r.Status == ReservationStatus.Completed)
                && r.StartUtc < end && r.EndUtc > start)
            .Select(r => r.SpotId)
            .Distinct()
            .CountAsync(cancellationToken);

        return Math.Min(1.0, (double)occupied / activeSpots);
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

    public Task<ParkingResult> JoinQueueAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => JoinQueueCoreAsync(userId, startUtc, endUtc, cancellationToken), cancellationToken);

    private async Task<ParkingResult> JoinQueueCoreAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
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

        // The duplicate check above ran outside any transaction (deliberately — the availability
        // scan between it and here uses its own context and must not extend lock scope). Re-check
        // and insert as one serializable step: without it, a double-click passes the check twice
        // and leaves two active entries that can each pin a spot with an offer.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var queuedMeanwhile = await dbContext.QueueEntries.AnyAsync(q => q.UserId == userId
            && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
            && q.StartUtc < endUtc && q.EndUtc > startUtc, cancellationToken);
        if (queuedMeanwhile)
        {
            return ParkingResult.Failure("Parking_Queue_Error_Already");
        }

        dbContext.QueueEntries.Add(new QueueEntry(userId, startUtc, endUtc, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public Task<ParkingResult> LeaveQueueAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => LeaveQueueCoreAsync(userId, queueEntryId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> LeaveQueueCoreAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken)
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

    public Task<ParkingResult> ClaimQueueOfferAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ClaimQueueOfferCoreAsync(userId, queueEntryId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ClaimQueueOfferCoreAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken)
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
        // inside that booking's transaction, so the two can't come apart. Vouchers stay out of the
        // queue path on purpose — a scarce claimed spot carries the harsher no-show package, and a
        // "free" claim would make walking away from it painless.
        return await ReserveCoreAsync(userId, spotId, entry.StartUtc, entry.EndUtc, fromQueue: true, queueEntryId, useVoucher: false, cancellationToken);
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
        var lapsedOffers = new List<(QueueEntry Entry, Guid SpotId)>();
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
                lapsedOffers.Add((entry, entry.OfferedSpotId!.Value));
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
        var offers = new List<(QueueEntry Entry, Guid SpotId)>();
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
            offers.Add((entry, spotId));
        }

        // An entry the user cancelled or claimed mid-run trips its rowversion and is detached by
        // the save; its offer/demotion never took effect and must not be counted or notified. The
        // freed spot simply waits for the next matcher tick.
        await OptimisticConcurrency.SaveSkippingConflictsAsync(dbContext, cancellationToken);
        offers.RemoveAll(o => dbContext.Entry(o.Entry).State == EntityState.Detached);
        lapsedOffers.RemoveAll(l => dbContext.Entry(l.Entry).State == EntityState.Detached);

        if (offers.Count > 0 || lapsedOffers.Count > 0)
        {
            var spotIdsToName = offers.Select(o => o.SpotId).Concat(lapsedOffers.Select(l => l.SpotId)).Distinct().ToList();
            var codes = await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => spotIdsToName.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

            // The claim window is short, so the email carries a CTA deep link and an explicit
            // local-time deadline. Without a configured canonical URL (dev) the button is omitted.
            var baseUrl = await siteSettings.GetCanonicalBaseUrlAsync();
            var claimUrl = baseUrl is null ? null : $"{baseUrl.TrimEnd('/')}/parking";
            var deadlineLocal = SiteTime.TimeOfDay(now + offerHold, timeZone).ToString("HH\\:mm");

            foreach (var (offerEntry, spotId) in offers)
            {
                await notifications.NotifyAsync(offerEntry.UserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                    messages["Parking_Notify_QueueOffer_Title"],
                    messages["Parking_Notify_QueueOffer_Body", codes.GetValueOrDefault(spotId, string.Empty), policy.QueueOfferMinutes],
                    email: true,
                    new NotificationEmailOptions(
                        ActionText: claimUrl is null ? null : messages["Email_QueueOffer_Action"].Value,
                        ActionUrl: claimUrl,
                        DeadlineText: messages["Email_QueueOffer_Deadline", deadlineLocal].Value),
                    cancellationToken);
            }

            // The demoted waiter learns why the spot is gone — bell/push only, no email needed.
            foreach (var (lapsedEntry, spotId) in lapsedOffers)
            {
                await notifications.NotifyAsync(lapsedEntry.UserId, NotificationCategory.SelfService, NotificationLevel.Info,
                    messages["Parking_Notify_OfferLapsed_Title"],
                    messages["Parking_Notify_OfferLapsed_Body", codes.GetValueOrDefault(spotId, string.Empty)],
                    cancellationToken);
            }
        }

        return offers.Count;
    }

    // Reaching a higher loyalty tier is worth celebrating — it unlocks queue priority, a bigger
    // allowance and a price discount. Detected at the two user-facing reward moments (completion,
    // release); the decay sweep only ever lowers points, so promotions cannot happen there.
    private async Task NotifyTierUpAsync(Guid userId, LoyaltyTier before, LoyaltyTier after, CancellationToken cancellationToken)
    {
        if (after <= before)
        {
            return;
        }

        await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages["Parking_Notify_TierUp_Title", messages[$"Parking_TierName_{after}"].Value],
            messages["Parking_Notify_TierUp_Body"], cancellationToken);
    }

    // Returns a redeemed voucher to its holder when the booking it paid for was given up early
    // enough to re-let the spot — the same terms under which credits are refunded. The restore
    // honors the one-unredeemed-voucher cap: if the holder meanwhile earned another usable
    // voucher, re-arming this one would let redeem→report→release cycles stockpile the very
    // value the cap exists to bound.
    private static async Task RestoreVoucherAsync(D3ParkingDbContext dbContext, Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken)
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
            .Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor
                && !blocked.Contains(s.Id)
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
