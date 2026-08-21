using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
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
    // How early a driver may report that the reserved spot is physically blocked. This is a
    // reporting window only; planned reservations never require arrival confirmation.
    private static readonly TimeSpan EarlyBlockedReportWindow = TimeSpan.FromMinutes(15);

    // Serializes queue matching triggered from the timer and release/cancel hooks. In-process
    // locking suffices because the app is single-instance by design.
    private static readonly SemaphoreSlim MaintenanceGate = new(1, 1);

    // Daily cap on "I can't park" reports per user — see ReportBlockedSpotAsync.
    private const int MaxBlockedReportsPerDay = 2;

    // How long the apology compensation (one free reservation) stays redeemable — from the grant for
    // the pending window, restarted from the approval once the spot manager confirms. Together
    // with the one-unredeemed-voucher-per-user rule this caps what faked reports could ever mint.
    public static readonly TimeSpan ApologyVoucherValidity = TimeSpan.FromDays(90);

    // Formats the mandatory photo proof may come in — kept to what a browser renders inline,
    // so the spot manager's review never needs a download. Size is bounded by BlockedSpotPhoto.MaxBytes.
    private static readonly string[] AllowedPhotoContentTypes = ["image/jpeg", "image/png", "image/webp"];

    public async Task<IReadOnlyList<ParkingSpotDto>> GetAvailableSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        if (endUtc <= startUtc
            || !ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone)
            || !policy.IsReservationStartDateAllowed(startUtc, now, timeZone)
            || !policy.IsWithinReservationHorizon(startUtc, now, timeZone)
            || !policy.IsReservationWeekdayAllowed(startUtc, timeZone)
            || !policy.IsPublicHolidayReservationAllowed(startUtc, timeZone))
        {
            return [];
        }

        var requestDate = SiteTime.Today(startUtc, timeZone);

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

        // Owned spots are hidden from the pool unless the resident's plan explicitly releases that day.
        // Once a guest books one, the block above excludes it.
        // Visitor-type spots belong to the reception's visitor agenda, never to the employee pool.
        // Natural code order (D3-2 before D3-10) needs the comparer, so sort in memory.
        var available = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor
                && !blocked.Contains(s.Id) && !held.Contains(s.Id)
                && (s.OwnerId == null || released.Contains(s.Id)))
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId, null))
            .ToListAsync(cancellationToken);
        return available.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public async Task<IReadOnlyList<ReservationDto>> GetMyReservationsAsync(Guid userId, bool upcomingOnly = false, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var query = from r in dbContext.Reservations.AsNoTracking()
                    join s in dbContext.ParkingSpots on r.SpotId equals s.Id
                    // Deliberately filter only by reservation holder. A resident may have active or
                    // future bookings made before getting a spot, or on a shared spot after releasing
                    // their own; ownership must not make those plans disappear from "My reservations".
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
                x.r.CheckedInAtUtc, x.r.ReleasedAtUtc, x.r.CompletedAtUtc,
                x.r.CalendarSequence, x.r.CalendarUpdatedAtUtc))
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
                               r.CheckedInAtUtc, r.ReleasedAtUtc, r.CompletedAtUtc,
                               r.CalendarSequence, r.CalendarUpdatedAtUtc))
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
                          r.CheckedInAtUtc, r.ReleasedAtUtc, r.CompletedAtUtc,
                          r.CalendarSequence, r.CalendarUpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // RetryAsync turns a lost race under the serializable transaction (deadlock victim, stale
    // rowversion) into a fresh attempt whose checks re-run against the winner's committed state —
    // the user gets the friendly conflict failure instead of an error page.
    public Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        bool confirmResidentRelease = false, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(
            () => ReserveCoreAsync(userId, spotId, startUtc, endUtc, fromQueue: false, queueEntryId: null,
                confirmResidentRelease, handoffId: null, handoffActorId: null, cancellationToken),
            cancellationToken);

    public async Task<ParkingResult> AcceptHandoffAsync(
        Guid actorId, Guid handoffId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preview = await dbContext.ResidentSpotHandoffs.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == handoffId, cancellationToken);
        if (preview is null)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_NotActive");
        }

        return await OptimisticConcurrency.RetryAsync(
            () => ReserveCoreAsync(preview.RecipientId, preview.SpotId, preview.StartUtc, preview.EndUtc,
                fromQueue: false, queueEntryId: null, confirmResidentRelease: true,
                handoffId, actorId, cancellationToken), cancellationToken);
    }

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

    private async Task<ParkingResult> ReserveCoreAsync(Guid userId, Guid spotId, DateTimeOffset startUtc,
        DateTimeOffset endUtc, bool fromQueue, Guid? queueEntryId, bool confirmResidentRelease,
        Guid? handoffId, Guid? handoffActorId,
        CancellationToken cancellationToken)
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

        // The UI is not the authority here: an already-open browser and a pending queue offer can
        // both outlive an administrator's rule change. Every resulting booking is revalidated.
        if (!ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_ReservationTimeModeChanged");
        }

        if (!policy.IsWithinReservationHorizon(startUtc, now, timeZone))
        {
            return ParkingResult.Failure(
                !policy.IsReservationStartDateAllowed(startUtc, now, timeZone)
                    ? "Parking_Error_SameDayReservationsNotAllowed"
                    : "Parking_Error_ReservationHorizon");
        }

        if (!policy.IsReservationWeekdayAllowed(startUtc, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_ReservationWeekdayNotAllowed");
        }

        if (!policy.IsPublicHolidayReservationAllowed(startUtc, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_PublicHolidayNotAllowed");
        }

        if (!policy.IsPublicHolidayReservationAllowed(startUtc, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_PublicHolidayNotAllowed");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The conflict checks below and the insert have to be one atomic step: at plain read-committed
        // two concurrent bookings for the last free spot both pass the check and both insert, and no
        // constraint catches it (overlap is not something a unique index can express). Serializable
        // makes those checks take range locks, so the second request blocks and then fails cleanly.
        // Under contention this can surface as a deadlock — a failed request the user can retry is
        // still far better than two people sent to the same spot.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        ResidentSpotHandoff? handoff = null;
        if (handoffId is { } directHandoffId)
        {
            handoff = await dbContext.ResidentSpotHandoffs
                .FirstOrDefaultAsync(h => h.Id == directHandoffId, cancellationToken);
            var actorMayAccept = handoff is not null && handoff.IsActive
                && handoff.ExpiresAtUtc > now
                && handoff.RecipientId == userId
                && handoff.SpotId == spotId
                && handoff.StartUtc == startUtc
                && handoff.EndUtc == endUtc
                && handoffActorId is { } actor
                && (handoff.Kind == ResidentSpotHandoffKind.ResidentOffer
                    ? handoff.Status == ResidentSpotHandoffStatus.Offered && actor == handoff.RecipientId
                    : handoff.Status == ResidentSpotHandoffStatus.PendingResident && actor == handoff.ResidentId);
            if (!actorMayAccept)
            {
                return ParkingResult.Failure("Parking_Handoff_Error_NotActive");
            }

            var recipientActive = await dbContext.Users.AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.Status == AccountStatus.Active, cancellationToken);
            if (!recipientActive)
            {
                return ParkingResult.Failure("Parking_Handoff_Error_RecipientUnavailable");
            }
        }

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
        var firstResidentDay = SiteTime.Today(effectiveStartUtc, timeZone);
        var lastResidentDay = SiteTime.Today(endUtc.AddTicks(-1), timeZone);
        var assignedResidentDates = await ResidentAllocation.AssignedDatesAsync(
            dbContext, spot, userId, firstResidentDay, lastResidentDay, cancellationToken);
        var assignedForWholeWindow = assignedResidentDates.Count == lastResidentDay.DayNumber - firstResidentDay.DayNumber + 1;
        var hasResidentMemberships = spot.OwnerId is not null || await dbContext.ParkingSpotResidents
            .AnyAsync(r => r.SpotId == spot.Id && r.RemovedAtUtc == null, cancellationToken);

        Guid? sharedByResidentId = null;
        if (hasResidentMemberships && !assignedForWholeWindow)
        {
            if (handoff is not null)
            {
                var residentDates = await ResidentAllocation.AssignedDatesAsync(
                    dbContext, spot, handoff.ResidentId, firstResidentDay, lastResidentDay, cancellationToken);
                if (residentDates.Count != lastResidentDay.DayNumber - firstResidentDay.DayNumber + 1)
                {
                    return ParkingResult.Failure("Parking_Handoff_Error_NotAssigned");
                }

                var publiclyReleased = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spotId
                    && r.Date >= firstResidentDay && r.Date <= lastResidentDay, cancellationToken);
                if (publiclyReleased)
                {
                    return ParkingResult.Failure("Parking_Handoff_Error_PubliclyReleased");
                }

                sharedByResidentId = handoff.ResidentId;
            }
            else
            {
                var releaseRows = await dbContext.SpotReleases
                    .Where(r => r.SpotId == spotId && r.Date >= firstResidentDay && r.Date <= lastResidentDay)
                    .Select(r => new { r.Date, r.OwnerId })
                    .ToListAsync(cancellationToken);
                var releasedDates = releaseRows.Select(r => r.Date).ToHashSet();
                sharedByResidentId = releaseRows.OrderBy(r => r.Date).Select(r => (Guid?)r.OwnerId).FirstOrDefault();

                for (var date = firstResidentDay; date <= lastResidentDay; date = date.AddDays(1))
                {
                    if (!releasedDates.Contains(date))
                    {
                        return ParkingResult.Failure("Parking_Error_SpotReserved");
                    }
                }
            }
        }

        // Booking a different spot must never leave the caller's assigned resident capacity blocked.
        // Resolve and share those days inside this transaction, so either both the alternative booking
        // and the releases commit, or neither does. Days already shared by the resident need no change.
        var residentSpotAutomaticallyReleased = false;
        var residentMembership = await dbContext.ParkingSpotResidents.AsNoTracking()
            .Where(r => r.UserId == userId && r.RemovedAtUtc == null)
            .OrderBy(r => r.AssignedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var residentSpot = residentMembership is not null
            ? await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == residentMembership.SpotId, cancellationToken)
            : await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);

        if (residentSpot is not null && residentSpot.Id != spotId)
        {
            var assignedOwnDates = await ResidentAllocation.AssignedDatesAsync(
                dbContext, residentSpot, userId, firstResidentDay, lastResidentDay, cancellationToken);
            if (assignedOwnDates.Count > 0)
            {
                var alreadySharedDates = (await dbContext.SpotReleases
                        .Where(r => r.SpotId == residentSpot.Id && r.OwnerId == userId
                            && r.Date >= firstResidentDay && r.Date <= lastResidentDay)
                        .Select(r => r.Date)
                        .ToListAsync(cancellationToken))
                    .ToHashSet();
                var datesToShare = assignedOwnDates.Where(date => !alreadySharedDates.Contains(date)).ToList();

                if (datesToShare.Count > 0)
                {
                    if (policy.ResidentAlternativeBookingPolicy == ResidentAlternativeBookingPolicy.Deny)
                    {
                        return ParkingResult.Failure("Parking_Error_AlternativeSpotDenied");
                    }

                    if (policy.ResidentAlternativeBookingPolicy == ResidentAlternativeBookingPolicy.ConfirmRelease
                        && !confirmResidentRelease)
                    {
                        return ParkingResult.Failure("Parking_AlternativeSpot_ReleaseConfirmationRequired");
                    }

                    foreach (var date in datesToShare)
                    {
                        dbContext.SpotReleases.Add(new SpotRelease(
                            residentSpot.Id, userId, date, now, 0, SpotReleaseSource.AlternativeBooking));
                    }

                    residentSpotAutomaticallyReleased = true;
                }
            }
        }

        // A resident's own allocated spot is their entitlement, not a draw from the shared weekly
        // capacity. Direct plans on pool/shared spots consume the quota; queue claims were already
        // admitted under the same rule when the user joined the queue.
        if (!fromQueue && !assignedForWholeWindow)
        {
            var plannerError = await ValidateWeeklyPlannerLimitAsync(
                dbContext, userId, startUtc, policy, timeZone, cancellationToken);
            if (plannerError is not null)
            {
                return ParkingResult.Failure(plannerError);
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
        if (assignedForWholeWindow)
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

        // Peak pricing and off-peak rewards are retired. Keep the historical column false for new
        // rows so old reports remain readable without letting a removed rule affect new bookings.
        const bool isOffPeak = false;

        var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);

        // The optional budget is deliberately equal for everyone. With free planning we leave the
        // wallet untouched, including its grant watermark, so enabling it later starts normally.
        var granted = policy.CreditsEnabled
            ? score.GrantCreditIfDue(policy.MonthlyCreditAllowance,
                ParkerScore.PeriodOf(now, policy.BudgetRenewalPeriod, timeZone), now)
            : 0;
        if (granted > 0)
        {
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.MonthlyCreditGrant, granted, null, now));
        }

        // Occupancy remains useful context, but the optional planning price is fixed for everyone.
        var occupancy = await ComputeOccupancyAsync(dbContext, startUtc, endUtc, cancellationToken);
        var cost = policy.ComputeReservationCost(occupancy);

        if (handoff is { Kind: ResidentSpotHandoffKind.UserRequest, MaxCreditsAuthorized: { } maximum }
            && cost > maximum)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_PriceIncreased");
        }

        // The apology compensation automatically absorbs the next non-zero planning price instead
        // of the wallet. It is redeemed inside this transaction; a timely cancel/release restores
        // it (see RestoreVoucherAsync), the same terms under which credits would be refunded.
        // Only an approved compensation counts: one still pending the spot manager's review holds
        // no value yet, and a rejected one never will. A zero-cost booking never wastes it.
        ApologyVoucher? voucher = null;
        if (policy.CreditsEnabled && cost > 0)
        {
            voucher = await dbContext.ApologyVouchers
                .Where(v => v.UserId == userId && v.Status == ApologyVoucherStatus.Approved
                    && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now)
                .OrderBy(v => v.ExpiresAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (voucher is null && score.Credits < cost)
        {
            return ParkingResult.Failure("Parking_Error_InsufficientCredit");
        }

        var reservation = new Reservation(spotId, userId, startUtc, endUtc, isOffPeak, now,
            voucher is null ? cost : 0, fromQueue, countsTowardWeeklyLimit: !assignedForWholeWindow);
        reservation.AttributeSharedCapacity(sharedByResidentId);
        dbContext.Reservations.Add(reservation);
        if (voucher is not null)
        {
            voucher.Redeem(reservation.Id, cost, now);
        }
        else if (cost > 0)
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

        handoff?.Accept(reservation.Id, now);

        // Achievements acknowledge only positive, observable outcomes. They are recorded in the
        // same transaction as the booking that proves them, so a retry cannot praise the same
        // contribution twice. Nothing here is read by booking, price, budget or queue decisions.
        var newAchievements = await RecordPositiveAchievementsAsync(
            dbContext, reservation, spot.Code, sharedByResidentId, fromQueue, now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (granted > 0)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_MonthlyCredit_Title"],
                messages["Parking_Notify_MonthlyCredit_Body", granted], cancellationToken);
        }

        await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages.ForEconomy(policy, "Parking_Notify_Reserved_Title"),
            voucher is not null
                ? messages["Parking_Notify_Reserved_Body_FreeCompensation", spot.Code, cost]
                : messages.ForEconomy(policy, "Parking_Notify_Reserved_Body", spot.Code, cost),
            cancellationToken);

        await NotifyNewAchievementsAsync(newAchievements, cancellationToken);

        foreach (var waiterId in withdrawnWaiters)
        {
            await notifications.NotifyAsync(waiterId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_QueueHoldReclaimed_Title"],
                messages["Parking_Notify_QueueHoldReclaimed_Body", spot.Code], cancellationToken);
        }

        // Warn when the wallet can no longer cover even a base-price booking. Bell/push only: the
        // warning is neither actionable on a deadline nor a formal record, so an email would just
        // train people to ignore the sender.
        if (policy.CreditsEnabled && score.Credits < policy.BaseReservationCost)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_LowBalance_Title"],
                messages["Parking_Notify_LowBalance_Body", score.Credits], cancellationToken);
        }

        return new ParkingResult
        {
            Succeeded = true,
            AutomaticCompensationApplied = voucher is not null,
            ResidentSpotAutomaticallyReleased = residentSpotAutomaticallyReleased,
        };
    }

    public async Task<ReservationQuoteDto> GetQuoteAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Quote and booking share one fixed price. Occupancy is returned only as planning context.
        var now = timeProvider.GetUtcNow();
        var occupancy = endUtc > startUtc
            ? await ComputeOccupancyAsync(dbContext, startUtc, endUtc, cancellationToken)
            : 0.0;

        var score = await dbContext.ParkerScores.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        var cost = policy.ComputeReservationCost(occupancy);

        // Reflect the next configured top-up the user would receive at booking, so affordability matches reserve.
        // PreviewAllowance applies any pending queue no-show penalty exactly as the grant will.
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var period = ParkerScore.PeriodOf(now, policy.BudgetRenewalPeriod, timeZone);
        var balance = score?.Credits ?? 0;
        if (score is null || score.LastCreditGrantPeriod < period)
        {
            var allowance = policy.MonthlyCreditAllowance;
            balance += score?.PreviewAllowance(allowance) ?? allowance;
        }

        var automaticCompensationAvailable = policy.CreditsEnabled && cost > 0
            && await dbContext.ApologyVouchers.AsNoTracking().AnyAsync(v =>
                v.UserId == userId && v.Status == ApologyVoucherStatus.Approved
                && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now, cancellationToken);

        return new ReservationQuoteDto(
            cost,
            (int)Math.Round(occupancy * 100),
            IsPeak: false,
            balance,
            Affordable: automaticCompensationAvailable || balance >= cost,
            AutomaticCompensationAvailable: automaticCompensationAvailable);
    }

    private static async Task<double> ComputeOccupancyAsync(D3ParkingDbContext dbContext, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        // Visitor spots are outside the employee pool, so they must not dilute the occupancy
        // context shown to employees or any retained optional release-reward calculation.
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

    private static async Task<string?> ValidateWeeklyPlannerLimitAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        DateTimeOffset startUtc,
        IncentivePolicy policy,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        if (!policy.WeeklyReservationLimitEnabled)
        {
            return null;
        }

        var plannedDate = SiteTime.Today(startUtc, timeZone);
        var (weekStartDate, weekEndDate) = IncentivePolicy.WeekOf(plannedDate);
        var (weekStartUtc, _) = SiteTime.Day(weekStartDate, timeZone);
        var (weekEndUtc, _) = SiteTime.Day(weekEndDate, timeZone);

        var reservationStarts = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.UserId == userId
                && r.CountsTowardWeeklyLimit
                && r.Status != ReservationStatus.Cancelled
                && r.Status != ReservationStatus.Released
                && r.StartUtc >= weekStartUtc && r.StartUtc < weekEndUtc)
            .Select(r => r.StartUtc)
            .ToListAsync(cancellationToken);
        var queueStarts = await dbContext.QueueEntries.AsNoTracking()
            .Where(q => q.UserId == userId
                && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
                && q.StartUtc >= weekStartUtc && q.StartUtc < weekEndUtc)
            .Select(q => q.StartUtc)
            .ToListAsync(cancellationToken);

        var plannedDays = reservationStarts.Concat(queueStarts)
            .Select(start => SiteTime.Today(start, timeZone))
            .ToHashSet();
        plannedDays.Add(plannedDate);

        if (plannedDays.Count <= policy.EffectiveWeeklyReservationLimit)
        {
            return null;
        }

        return "Parking_Error_WeeklyReservationLimit_NoLastMinute";
    }

    // User-facing planner mutations run under optimistic-concurrency retry: a double click or a
    // simultaneous manager action is re-read and resolved as a friendly invalid-state result.
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

        if (reservation.EndUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        reservation.Release(now);
        var residentSpotAutomaticallyReturned = await RestoreAlternativeResidentReleasesAsync(
            dbContext, reservation, userId, timeZone, cancellationToken);

        // An early enough release frees the spot for others, so the charge is refunded in full.
        var timely = policy.QualifiesForReleaseReward(reservation.StartUtc, now);

        // A voucher-paid booking gets its voucher back on the same timely terms as a refund.
        if (timely)
        {
            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);
        }

        if (timely && reservation.CreditsCharged > 0)
        {
            var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
            score.RefundCredits(reservation.CreditsCharged, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.ReservationRefund, reservation.CreditsCharged, reservation.Id, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The freed spot may now satisfy someone on the waitlist.
        await ProcessQueueAsync(cancellationToken);
        return new ParkingResult
        {
            Succeeded = true,
            ResidentSpotAutomaticallyReturned = residentSpotAutomaticallyReturned,
        };
    }

    public Task<ParkingResult> CancelAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => CancelCoreAsync(userId, reservationId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> CancelCoreAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
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

        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        if (reservation.EndUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        reservation.Cancel(now);
        var residentSpotAutomaticallyReturned = await RestoreAlternativeResidentReleasesAsync(
            dbContext, reservation, userId, timeZone, cancellationToken);

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
        await transaction.CommitAsync(cancellationToken);

        // The freed spot may now satisfy someone on the waitlist.
        await ProcessQueueAsync(cancellationToken);
        return new ParkingResult
        {
            Succeeded = true,
            ResidentSpotAutomaticallyReturned = residentSpotAutomaticallyReturned,
        };
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

        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
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
        // in front of it. This does not confirm arrival or change the planned reservation's state.
        var now = timeProvider.GetUtcNow();
        if (now < reservation.StartUtc - EarlyBlockedReportWindow || now >= reservation.EndUtc)
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
        reservation.Cancel(now);
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
                    reservation.IsOffPeak, now, reservation.CreditsCharged, reservation.FromQueue,
                    reservation.CountsTowardWeeklyLimit);
                var replacementDate = SiteTime.Today(effectiveStartUtc, timeZone);
                replacement.AttributeSharedCapacity(await dbContext.SpotReleases.AsNoTracking()
                    .Where(r => r.SpotId == replacementSpotId && r.Date == replacementDate)
                    .Select(r => (Guid?)r.OwnerId)
                    .FirstOrDefaultAsync(cancellationToken));
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

        // The apology: one reservation free of charge, available only when the planning-credit
        // economy is enabled. It is granted pending the spot manager's review of the photo proof,
        // so value only materializes from a human-confirmed report. At most one pending-or-approved
        // unredeemed compensation per user and it expires, which caps what faked reports could ever
        // stage. Evaluated after any restore above, so a restored compensation counts against the
        // cap instead of stacking with a fresh one. Rejected compensations don't block a later one.
        var voucherGranted = false;
        if (policy.CreditsEnabled)
        {
            var holdsUsableVoucher = await dbContext.ApologyVouchers.AnyAsync(v =>
                v.UserId == userId && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now
                && (v.Status == ApologyVoucherStatus.PendingApproval || v.Status == ApologyVoucherStatus.Approved),
                cancellationToken);
            if (!holdsUsableVoucher)
            {
                dbContext.ApologyVouchers.Add(new ApologyVoucher(userId, mismatch.Id, now, now + ApologyVoucherValidity));
                voucherGranted = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (voucherGranted)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages.ForEconomy(policy, "Parking_Notify_VoucherGranted_Title"),
                messages.ForEconomy(policy, "Parking_Notify_VoucherGranted_Body"), cancellationToken);

            // Calling the reviewers is the oversight desk's job, not this one's. It used to happen
            // here, addressed to every ManageSpots holder — the wrong audience, since the evidence
            // (a photograph of somebody's car) is gated behind ReviewMismatches. The desk opens a
            // case for this report within a sweep and tells the people who may actually judge it.
        }

        return relocatedCode is null
            ? BlockedSpotOutcome.Recorded(voucherGranted)
            : BlockedSpotOutcome.Relocated(relocatedCode, voucherGranted);
    }
    public async Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Remind once when the planned start is near. Past starts are ignored: the reminder is
        // informational and never acts as a presence check or penalty deadline.
        var remindFrom = now;
        var remindTo = now + policy.ReminderLeadTime;

        var due = await dbContext.Reservations
            .Where(r => r.Status == ReservationStatus.Reserved && r.ReminderSentAtUtc == null
                && r.StartUtc > remindFrom && r.StartUtc <= remindTo)
            .ToListAsync(cancellationToken);

        // A timed reminder just before midnight is actively misleading for a calendar-day booking.
        due.RemoveAll(r => ReservationWindowRules.IsFullLocalDay(r.StartUtc, r.EndUtc, timeZone));

        if (due.Count == 0)
        {
            return 0;
        }

        var spotCodes = await GetSpotCodesAsync(dbContext, due, cancellationToken);

        foreach (var reservation in due)
        {
            reservation.MarkReminderSent(now);
        }

        // A conflicted row means the holder cancelled or a manager changed the booking while we
        // were reading; the save keeps the rest and detached rows are simply not notified.
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
        if (!policy.CreditsEnabled)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var period = ParkerScore.PeriodOf(now, policy.BudgetRenewalPeriod, timeZone);

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
            var amount = score.GrantCreditIfDue(policy.MonthlyCreditAllowance, period, now);
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

        // Count actual grants, not rows examined.
        return granted.Count;
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

        // The controller wants "how full was today's planned peak", which only exists once the
        // window is over. Measuring a partial future window would bias the controller downward.
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

        // Reserved is the planner's honoured outcome. Legacy CheckedIn/Completed rows still count;
        // released and cancelled plans do not.
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

        int Priority(DateTimeOffset created) => (int)(now - created).TotalMinutes;

        var spotIds = mine.Where(q => q.OfferedSpotId != null).Select(q => q.OfferedSpotId!.Value).ToList();
        var codes = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        return mine.Select(q =>
        {
            // Position reflects the same first-come, first-served priority the matcher uses: how many overlapping
            // entries currently outrank me, plus one.
            var myPriority = Priority(q.CreatedAtUtc);
            var position = q.Status == QueueEntryStatus.Offered
                ? 0
                : 1 + waiting.Count(w => w.StartUtc < q.EndUtc && w.EndUtc > q.StartUtc
                    && Priority(w.CreatedAtUtc) > myPriority);

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

        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        if (!ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_ReservationTimeModeChanged");
        }

        if (!policy.IsWithinReservationHorizon(startUtc, now, timeZone))
        {
            return ParkingResult.Failure(
                !policy.IsReservationStartDateAllowed(startUtc, now, timeZone)
                    ? "Parking_Error_SameDayReservationsNotAllowed"
                    : "Parking_Error_ReservationHorizon");
        }

        if (!policy.IsReservationWeekdayAllowed(startUtc, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_ReservationWeekdayNotAllowed");
        }

        if (!policy.IsPublicHolidayReservationAllowed(startUtc, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_PublicHolidayNotAllowed");
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


        var plannerError = await ValidateWeeklyPlannerLimitAsync(
            dbContext, userId, startUtc, policy, timeZone, cancellationToken);
        if (plannerError is not null)
        {
            return ParkingResult.Failure(plannerError);
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

        // Claiming is just reserving the held spot with the same fixed price, automatic compensation,
        // and balance check as any direct booking. The offer is re-checked and marked claimed inside
        // that booking's transaction, so the two cannot come apart.
        return await ReserveCoreAsync(userId, spotId, entry.StartUtc, entry.EndUtc, fromQueue: true, queueEntryId,
            confirmResidentRelease: false, handoffId: null, handoffActorId: null, cancellationToken);
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
            else if (!ReservationWindowRules.MatchesMode(
                         entry.StartUtc, entry.EndUtc, policy.ReservationTimeMode, timeZone)
                     || !policy.IsReservationStartDateAllowed(entry.StartUtc, now, timeZone)
                     || !policy.IsWithinReservationHorizon(entry.StartUtc, now, timeZone)
                     || !policy.IsReservationWeekdayAllowed(entry.StartUtc, timeZone)
                     || !policy.IsPublicHolidayReservationAllowed(entry.StartUtc, timeZone))
            {
                // A settings change may make an older queue request invalid. Do not let it keep a
                // spot held or turn into a booking that the current calendar would reject.
                entry.Cancel();
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

        // Achievements never affect access. The queue remains first-come, first-served.
        int Priority(QueueEntry q) => (int)(now - q.CreatedAtUtc).TotalMinutes;

        var waiting = active
            .Where(q => q.Status == QueueEntryStatus.Waiting && q.EndUtc > now)
            .OrderByDescending(Priority)
            .ThenBy(q => q.CreatedAtUtc)
            .ToList();
        // The matching decision is a snapshot: loading the reservations, releases and spots once
        // keeps a busy queue from turning into two database round-trips per waiter. The entries are
        // still saved with rowversions below, so a concurrent claim/cancel is rejected rather than
        // producing a stale offer.
        var earliestStart = waiting.Count == 0 ? now : waiting.Min(q => q.StartUtc);
        var latestEnd = waiting.Count == 0 ? now : waiting.Max(q => q.EndUtc);
        var queuedReservations = await dbContext.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < latestEnd && r.EndUtc > earliestStart)
            .Select(r => new QueueReservationSnapshot(r.SpotId, r.UserId, r.StartUtc, r.EndUtc))
            .ToListAsync(cancellationToken);
        var candidateSpots = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor)
            .Select(s => new QueueSpotSnapshot(s.Id, s.OwnerId))
            .ToListAsync(cancellationToken);
        var requestDates = waiting
            .Select(q => SiteTime.Today(q.StartUtc, timeZone))
            .Distinct()
            .ToList();
        List<QueueReleaseSnapshot> releases = requestDates.Count == 0
            ? []
            : await dbContext.SpotReleases.AsNoTracking()
                .Where(r => requestDates.Contains(r.Date))
                .Select(r => new QueueReleaseSnapshot(r.SpotId, r.Date))
                .ToListAsync(cancellationToken);
        var releasedByDate = releases.ToLookup(r => r.Date, r => r.SpotId);
        var candidatesByWindow = new Dictionary<(DateTimeOffset StartUtc, DateTimeOffset EndUtc), IReadOnlyList<Guid>>();

        IReadOnlyList<Guid> CandidatesFor(QueueEntry entry)
        {
            var key = (entry.StartUtc, entry.EndUtc);
            if (candidatesByWindow.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var blocked = queuedReservations
                .Where(r => r.StartUtc < entry.EndUtc && r.EndUtc > entry.StartUtc)
                .Select(r => r.SpotId)
                .ToHashSet();
            var released = releasedByDate[SiteTime.Today(entry.StartUtc, timeZone)].ToHashSet();
            var candidates = candidateSpots
                .Where(s => !blocked.Contains(s.Id) && (s.OwnerId == null || released.Contains(s.Id)))
                .Select(s => s.Id)
                .ToList();
            candidatesByWindow[key] = candidates;
            return candidates;
        }

        var offerHold = TimeSpan.FromMinutes(policy.QueueOfferMinutes);
        var offers = new List<(QueueEntry Entry, Guid SpotId)>();
        foreach (var entry in waiting)
        {
            // A user who already holds a reservation for the window could never claim the offer
            // (own-conflict) — skip them rather than pinning a spot on an unclaimable hold.
            var hasOverlappingReservation = queuedReservations.Any(r => r.UserId == entry.UserId
                && r.StartUtc < entry.EndUtc && r.EndUtc > entry.StartUtc);
            if (hasOverlappingReservation)
            {
                continue;
            }

            var candidates = CandidatesFor(entry);
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

    // Returns a redeemed voucher to its holder when the booking it paid for was given up early
    // enough to re-let the spot — the same terms under which credits are refunded. The restore
    // honors the one-unredeemed-voucher cap: if the holder meanwhile earned another usable
    // voucher, re-arming this one would let redeem→report→release cycles stockpile the very
    // value the cap exists to bound.
    private static async Task<bool> RestoreAlternativeResidentReleasesAsync(
        D3ParkingDbContext dbContext, Reservation alternativeReservation, Guid userId,
        TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var membership = await dbContext.ParkingSpotResidents.AsNoTracking()
            .Where(r => r.UserId == userId && r.RemovedAtUtc == null)
            .OrderBy(r => r.AssignedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var residentSpot = membership is not null
            ? await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == membership.SpotId, cancellationToken)
            : await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (residentSpot is null || residentSpot.Id == alternativeReservation.SpotId)
        {
            return false;
        }

        var firstDate = SiteTime.Today(alternativeReservation.StartUtc, timeZone);
        var lastDate = SiteTime.Today(alternativeReservation.EndUtc.AddTicks(-1), timeZone);
        var releases = await dbContext.SpotReleases
            .Where(r => r.SpotId == residentSpot.Id && r.OwnerId == userId
                && r.Source == SpotReleaseSource.AlternativeBooking
                && r.Date >= firstDate && r.Date <= lastDate)
            .ToListAsync(cancellationToken);
        if (releases.Count == 0)
        {
            return false;
        }

        var (rangeStart, _) = SiteTime.Day(firstDate, timeZone);
        var (_, rangeEnd) = SiteTime.Day(lastDate, timeZone);
        var guestBookings = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.SpotId == residentSpot.Id
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);
        var otherResidentBookings = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.Id != alternativeReservation.Id && r.UserId == userId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);
        var queueHolds = await dbContext.QueueEntries.AsNoTracking()
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId == residentSpot.Id
                && q.StartUtc < rangeEnd && q.EndUtc > rangeStart)
            .Select(q => new { q.StartUtc, q.EndUtc })
            .ToListAsync(cancellationToken);

        var restored = false;
        foreach (var release in releases)
        {
            var (dayStart, dayEnd) = SiteTime.Day(release.Date, timeZone);
            var stillNeeded = guestBookings.Any(r => r.StartUtc < dayEnd && r.EndUtc > dayStart)
                || otherResidentBookings.Any(r => r.StartUtc < dayEnd && r.EndUtc > dayStart)
                || queueHolds.Any(q => q.StartUtc < dayEnd && q.EndUtc > dayStart);
            if (stillNeeded)
            {
                continue;
            }

            dbContext.SpotReleases.Remove(release);
            restored = true;
        }

        return restored;
    }

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

        var blocked = dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < endUtc && r.EndUtc > startUtc)
            .Select(r => r.SpotId);

        var released = dbContext.SpotReleases.Where(r => r.Date == requestDate).Select(r => r.SpotId);

        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor
                && !blocked.Contains(s.Id)
                && (s.OwnerId == null || released.Contains(s.Id)))
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

    private sealed record QueueReservationSnapshot(Guid SpotId, Guid UserId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    private sealed record QueueSpotSnapshot(Guid Id, Guid? OwnerId);

    private sealed record QueueReleaseSnapshot(Guid SpotId, DateOnly Date);

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

    private sealed record AchievementAward(Guid UserId, ParkingBadge Badge);

    /// <summary>
    /// Records positive evidence produced by a newly-created reservation and returns only the
    /// achievements newly unlocked by that evidence. Existing achievements are permanent and
    /// missing/late actions never create a negative record.
    /// </summary>
    private static async Task<List<AchievementAward>> RecordPositiveAchievementsAsync(
        D3ParkingDbContext dbContext,
        Reservation reservation,
        string spotCode,
        Guid? sharedByResidentId,
        bool fromQueue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, HashSet<ParkingBadge>>();

        void AddCandidates(Guid userId, IEnumerable<ParkingBadge> badges)
        {
            if (!candidates.TryGetValue(userId, out var set))
            {
                set = [];
                candidates[userId] = set;
            }

            set.UnionWith(badges);
        }

        // The added reservation is not visible to the SQL count until SaveChanges.
        var planCount = 1 + await dbContext.Reservations.CountAsync(
            r => r.UserId == reservation.UserId, cancellationToken);
        AddCandidates(reservation.UserId, ParkingAchievementRules.ForPlans(planCount));

        if (sharedByResidentId is { } residentId && residentId != reservation.UserId)
        {
            var alreadyRecorded = await dbContext.ParkingContributions.AnyAsync(c =>
                c.UserId == residentId
                && c.Kind == ParkingContributionKind.ResidentShareUsed
                && c.SourceId == reservation.Id, cancellationToken);
            if (!alreadyRecorded)
            {
                dbContext.ParkingContributions.Add(new ParkingContribution(
                    residentId, ParkingContributionKind.ResidentShareUsed, reservation.Id,
                    reservation.UserId, now, spotCode));
                var usedCount = 1 + await dbContext.ParkingContributions.CountAsync(c =>
                    c.UserId == residentId && c.Kind == ParkingContributionKind.ResidentShareUsed,
                    cancellationToken);
                AddCandidates(residentId, ParkingAchievementRules.ForResidentSharesUsed(usedCount));

                if (fromQueue)
                {
                    dbContext.ParkingContributions.Add(new ParkingContribution(
                        residentId, ParkingContributionKind.QueueHelped, reservation.Id,
                        reservation.UserId, now, spotCode));
                    var queueCount = 1 + await dbContext.ParkingContributions.CountAsync(c =>
                        c.UserId == residentId && c.Kind == ParkingContributionKind.QueueHelped,
                        cancellationToken);
                    AddCandidates(residentId, ParkingAchievementRules.ForQueueHelps(queueCount));
                }
            }
        }
        else
        {
            // Credit the most recent person whose release made this capacity bookable. Selecting
            // one source prevents a chain of reserve/release actions from crediting every historic
            // holder for a single final booking.
            var released = await dbContext.Reservations
                .Where(r => r.SpotId == reservation.SpotId
                    && r.Status == ReservationStatus.Released
                    && r.UserId != reservation.UserId
                    && r.StartUtc < reservation.EndUtc
                    && r.EndUtc > reservation.StartUtc)
                .OrderByDescending(r => r.ReleasedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (released is not null)
            {
                var alreadyRecorded = await dbContext.ParkingContributions.AnyAsync(c =>
                    c.UserId == released.UserId
                    && c.Kind == ParkingContributionKind.UsefulRelease
                    && c.SourceId == released.Id, cancellationToken);
                if (!alreadyRecorded)
                {
                    dbContext.ParkingContributions.Add(new ParkingContribution(
                        released.UserId, ParkingContributionKind.UsefulRelease, released.Id,
                        reservation.UserId, now, spotCode));
                    var usefulCount = 1 + await dbContext.ParkingContributions.CountAsync(c =>
                        c.UserId == released.UserId && c.Kind == ParkingContributionKind.UsefulRelease,
                        cancellationToken);
                    AddCandidates(released.UserId, ParkingAchievementRules.ForUsefulReleases(usefulCount));

                    if (fromQueue)
                    {
                        dbContext.ParkingContributions.Add(new ParkingContribution(
                            released.UserId, ParkingContributionKind.QueueHelped, released.Id,
                            reservation.UserId, now, spotCode));
                        var queueCount = 1 + await dbContext.ParkingContributions.CountAsync(c =>
                            c.UserId == released.UserId && c.Kind == ParkingContributionKind.QueueHelped,
                            cancellationToken);
                        AddCandidates(released.UserId, ParkingAchievementRules.ForQueueHelps(queueCount));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var userIds = candidates.Keys.ToList();
        var existing = (await dbContext.UserBadges
                .Where(b => userIds.Contains(b.UserId))
                .Select(b => new { b.UserId, b.Badge })
                .ToListAsync(cancellationToken))
            .Select(b => (b.UserId, b.Badge))
            .ToHashSet();

        var awards = new List<AchievementAward>();
        foreach (var (userId, badges) in candidates)
        {
            foreach (var badge in badges.Where(ParkingAchievementRules.IsPositiveAchievement))
            {
                if (existing.Add((userId, badge)))
                {
                    dbContext.UserBadges.Add(new UserBadge(userId, badge, now));
                    awards.Add(new AchievementAward(userId, badge));
                }
            }
        }

        return awards;
    }

    private async Task NotifyNewAchievementsAsync(
        IReadOnlyCollection<AchievementAward> awards,
        CancellationToken cancellationToken)
    {
        if (awards.Count == 0)
        {
            return;
        }

        var baseUrl = await siteSettings.GetCanonicalBaseUrlAsync(cancellationToken);
        var achievementsUrl = baseUrl is null ? null : $"{baseUrl.TrimEnd('/')}/parking/achievements";
        foreach (var award in awards)
        {
            var name = messages[$"Parking_BadgeName_{award.Badge}"].Value;
            await notifications.NotifyAsync(
                award.UserId,
                NotificationCategory.SelfService,
                NotificationLevel.Info,
                messages["Parking_Notify_Achievement_Title", name],
                messages[$"Parking_AchievementBody_{award.Badge}"],
                email: true,
                new NotificationEmailOptions(
                    ActionText: achievementsUrl is null ? null : messages["Email_Achievement_Action"].Value,
                    ActionUrl: achievementsUrl),
                cancellationToken);
        }
    }

}
