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

    // Daily cap on "I can't park" reports per user — see ReportBlockedSpotAsync.
    private const int MaxBlockedReportsPerDay = 2;

    // How long the apology compensation (one free reservation) stays redeemable — from the grant for
    // the pending window, restarted from the approval once the spot manager confirms. Together
    // with the one-unredeemed-voucher-per-user rule this caps what faked reports could ever mint.
    public static readonly TimeSpan ApologyVoucherValidity = TimeSpan.FromDays(90);

    // Formats the mandatory photo proof may come in — kept to what a browser renders inline,
    // so the spot manager's review never needs a download. Size is bounded by BlockedSpotPhoto.MaxBytes.

    public async Task<IReadOnlyList<ParkingSpotDto>> GetAvailableSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        if (endUtc <= startUtc
            || !ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone)
            || policy.GetReservationDateAvailability(startUtc, now, timeZone) != ReservationDateAvailability.Allowed)
        {
            return [];
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var available = await ParkingCapacity.AvailableAsync(dbContext, startUtc, endUtc, now, timeZone, true, cancellationToken);
        return available.Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId, null))
            .OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
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
    public async Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        bool confirmResidentRelease = false, CancellationToken cancellationToken = default)
    {
        var result = await OptimisticConcurrency.RetryAsync(
            () => ReserveCoreAsync(userId, spotId, startUtc, endUtc, false, null,
                confirmResidentRelease, null, null, cancellationToken), cancellationToken);
        if (result.ResidentSpotAutomaticallyReleased || result.Errors.Contains("Parking_Error_QueueHasPriority"))
            await ProcessQueueAsync(cancellationToken);
        return result;
    }

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

        // New requests use current calendar rules. Existing queue/handoff intent keeps its stored
        // window; its identity, ownership and live capacity are still checked in the transaction.
        if (!fromQueue && handoffId is null
            && !ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone))
        {
            return ParkingResult.Failure("Parking_Error_ReservationTimeModeChanged");
        }

        if (!fromQueue && handoffId is null
            && policy.GetReservationDateAvailability(startUtc, now, timeZone).ToParkingErrorKey() is { } dateError)
        {
            return ParkingResult.Failure(dateError);
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
            if (!(await parkingSettings.GetCurrentPolicyAsync(cancellationToken)).HandoffsEnabled)
                return ParkingResult.Failure("Parking_Handoff_Error_Disabled");
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

            // A request can sit pending for hours. Re-check both account state and the live
            // permission here: resident approval is a confused-deputy boundary and must not create
            // a reservation for somebody whose parking access was revoked after the handoff was
            // sent.
            var recipientEligible = await dbContext.Users.AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.Status == AccountStatus.Active, cancellationToken)
                && await (from userRole in dbContext.UserRoles
                          join claim in dbContext.RoleClaims on userRole.RoleId equals claim.RoleId
                          where userRole.UserId == userId
                              && claim.ClaimType == D3ParkingClaimTypes.Permission
                              && claim.ClaimValue == Permissions.Parking.Reserve
                          select userRole.UserId)
                    .AnyAsync(cancellationToken);
            if (!recipientEligible)
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

        if (await ParkingCapacity.IsBlockedAsync(dbContext, spotId, startUtc, endUtc, now, cancellationToken))
            return ParkingResult.Failure("Parking_Error_SpotTemporarilyBlocked");

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
                    var releasePolicy = await parkingSettings.GetCurrentPolicyAsync(cancellationToken);
                    var releaseFailure = datesToShare.Select(date => releasePolicy.ValidateResidentRelease(date, now, timeZone)).FirstOrDefault(error => error is not null);
                    if (releaseFailure is not null) return ParkingResult.Failure(releaseFailure);

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
        if (!assignedForWholeWindow)
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

        if (!fromQueue && !assignedForWholeWindow && handoff is null)
        {
            var waiting = await dbContext.QueueEntries.AsNoTracking()
                .Where(q => q.Status == QueueEntryStatus.Waiting && q.EndUtc > now
                    && q.StartUtc < endUtc && q.EndUtc > startUtc)
                .OrderBy(q => q.CreatedAtUtc).ThenBy(q => q.Id).ToListAsync(cancellationToken);
            foreach (var waiter in waiting)
            {
                if (waiter.RequiredSpotType is { } type && type != spot.Type) continue;
                if (!await QueueEntryIsEligibleAsync(dbContext, waiter, policy, timeZone, now, cancellationToken)) continue;
                var freeForWaiter = await ParkingCapacity.AvailableAsync(dbContext, waiter.StartUtc, waiter.EndUtc,
                    now, timeZone, true, cancellationToken);
                if (freeForWaiter.Any(s => s.Id == spotId))
                    return ParkingResult.Failure("Parking_Error_QueueHasPriority");
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
            voucher is null ? cost : 0, fromQueue, countsTowardWeeklyLimit: !assignedForWholeWindow,
            refundDeadlineUtc: startUtc - policy.ReleaseCutoff);
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
            if (entry is null || entry.Status != QueueEntryStatus.Offered || entry.OfferedSpotId != spotId
                || entry.StartUtc != startUtc || entry.EndUtc != endUtc
                || entry.RequiredSpotType is { } requiredType && spot.Type != requiredType)
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
        if (handoff is { Kind: ResidentSpotHandoffKind.ResidentOffer })
        {
            var recipientName = await dbContext.Users.Where(u => u.Id == userId)
                .Select(u => u.DisplayName ?? u.Email).FirstOrDefaultAsync(cancellationToken) ?? userId.ToString();
            await ParkingNotifications.EnqueueAsync(dbContext, now, handoff.ResidentId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_HandoffAccepted_Title"],
                messages["Parking_Notify_HandoffAccepted_Body", recipientName, spot.Code], cancellationToken);
        }

        // Achievements acknowledge only positive, observable outcomes. They are recorded in the
        // same transaction as the booking that proves them, so a retry cannot praise the same
        // contribution twice. Nothing here is read by booking, price, budget or queue decisions.
        var newAchievements = await RecordPositiveAchievementsAsync(
            dbContext, reservation, spot.Code, sharedByResidentId, fromQueue, now, cancellationToken);


        if (granted > 0)
        {
            await ParkingNotifications.EnqueueAsync(dbContext, now, userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_MonthlyCredit_Title"],
                messages["Parking_Notify_MonthlyCredit_Body", granted], cancellationToken);
        }

        await ParkingNotifications.EnqueueAsync(dbContext, now, userId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages.ForEconomy(policy, "Parking_Notify_Reserved_Title"),
            voucher is not null
                ? messages["Parking_Notify_Reserved_Body_FreeCompensation", spot.Code, cost]
                : messages.ForEconomy(policy, "Parking_Notify_Reserved_Body", spot.Code, cost),
            cancellationToken);

        await NotifyNewAchievementsAsync(dbContext, now, newAchievements, cancellationToken);

        foreach (var waiterId in withdrawnWaiters)
        {
            await ParkingNotifications.EnqueueAsync(dbContext, now, waiterId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_QueueHoldReclaimed_Title"],
                messages["Parking_Notify_QueueHoldReclaimed_Body", spot.Code], cancellationToken);
        }

        // Warn when the wallet can no longer cover even a base-price booking. Bell/push only: the
        // warning is neither actionable on a deadline nor a formal record, so an email would just
        // train people to ignore the sender.
        if (policy.CreditsEnabled && score.Credits < policy.BaseReservationCost)
        {
            await ParkingNotifications.EnqueueAsync(dbContext, now, userId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_LowBalance_Title"],
                messages["Parking_Notify_LowBalance_Body", score.Credits], cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ParkingNotifications.PublishAsync(dbContext, notifications, cancellationToken);

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

    private async Task<double> ComputeOccupancyAsync(D3ParkingDbContext dbContext, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var total = await dbContext.ParkingSpots.CountAsync(s => s.IsActive && s.Type != ParkingSpotType.Visitor, cancellationToken);
        if (total == 0) return 0;
        var free = await ParkingCapacity.AvailableAsync(dbContext, startUtc, endUtc, timeProvider.GetUtcNow(),
            await siteSettings.GetTimeZoneAsync(cancellationToken), true, cancellationToken);
        return 1.0 - (double)free.Count / total;
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
                && (r.Status != ReservationStatus.Cancelled && r.Status != ReservationStatus.Released
                    || r.Status == ReservationStatus.Released && r.ReleasedAtUtc >= r.StartUtc)
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

    public async Task<ReservationEndPreviewDto?> PreviewEndAsync(Guid userId, Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var booking = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId && r.UserId == userId, cancellationToken);
        if (booking is null || booking.EndUtc <= now || booking.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn)) return null;
        var policy = await parkingSettings.GetCurrentPolicyAsync(cancellationToken);
        var zone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var releaseError = policy.ValidateRelease(booking.StartUtc, now, zone);
        var deadline = booking.RefundDeadlineUtc ?? booking.StartUtc - policy.ReleaseCutoff;
        var timely = now <= deadline;
        var redeemed = timely ? await db.ApologyVouchers.AsNoTracking()
            .FirstOrDefaultAsync(v => v.RedeemedReservationId == reservationId, cancellationToken) : null;
        var voucher = redeemed is not null && redeemed.Status == ApologyVoucherStatus.Approved
            && redeemed.ExpiresAtUtc > now && !await HoldsAnotherUsableVoucherAsync(db, redeemed, now, cancellationToken);
        return new(booking.StartUtc <= now, timely ? booking.CreditsCharged : 0, voucher, deadline,
            policy.CreditsEnabled || booking.CreditsCharged > 0 || voucher,
            policy.WeeklyReservationLimitEnabled && booking.CountsTowardWeeklyLimit, releaseError,
            policy.ReleaseAllowedUntil(booking.StartUtc, zone));
    }

    // User-facing planner mutations run under optimistic-concurrency retry: a double click or a
    // simultaneous manager action is re-read and resolved as a friendly invalid-state result.
    public Task<ParkingResult> ReleaseAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReleaseCoreAsync(userId, reservationId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ReleaseCoreAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetCurrentPolicyAsync(cancellationToken);
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

        if (reservation.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn))
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        if (reservation.EndUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        if (policy.ValidateRelease(reservation.StartUtc, now, timeZone) is { } releaseError)
            return ParkingResult.Failure(releaseError);

        if (reservation.Status == ReservationStatus.CheckedIn) reservation.Complete(now);
        else reservation.Release(now);
        var residentSpotAutomaticallyReturned = await RestoreAlternativeResidentReleasesAsync(
            dbContext, reservation, userId, timeZone, cancellationToken);

        // An early enough release frees the spot for others, so the charge is refunded in full.
        var timely = now <= (reservation.RefundDeadlineUtc ?? reservation.StartUtc - policy.ReleaseCutoff);

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

        if (reservation.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn))
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

        // A holder ending an already-started plan retains its elapsed interval and quota day.
        policy = await parkingSettings.GetCurrentPolicyAsync(cancellationToken);
        if (policy.ValidateRelease(reservation.StartUtc, now, timeZone) is { } releaseError)
            return ParkingResult.Failure(releaseError);

        if (reservation.Status == ReservationStatus.CheckedIn) reservation.Complete(now);
        else if (reservation.StartUtc <= now) reservation.Release(now);
        else reservation.Cancel(now);
        var residentSpotAutomaticallyReturned = await RestoreAlternativeResidentReleasesAsync(
            dbContext, reservation, userId, timeZone, cancellationToken);

        // Cancelling early enough to re-let the spot refunds the charge (or restores the apology
        // voucher that paid for it); a late cancel forfeits them.
        var timely = now <= (reservation.RefundDeadlineUtc ?? reservation.StartUtc - policy.ReleaseCutoff);
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

    public Task<BlockedSpotOutcome> ReportBlockedResidentSpotAsync(Guid userId, Guid spotId, bool relocate,
        BlockedSpotPhoto? photo, string? blockerPlate = null, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ReportBlockedSpotCoreAsync(userId, Guid.Empty, relocate, photo,
            blockerPlate, cancellationToken, spotId), cancellationToken);

    private async Task<BlockedSpotOutcome> ReportBlockedSpotCoreAsync(Guid userId, Guid reservationId, bool relocate, BlockedSpotPhoto? photo, string? blockerPlate, CancellationToken cancellationToken, Guid? residentSpotId = null)
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

        var detectedContentType = D3Parking.Application.Parking.Maps.ImageContentType.Detect(photo.Content);
        if (detectedContentType is null)
        {
            return BlockedSpotOutcome.Failure("Parking_Error_PhotoType");
        }
        photo = photo with { ContentType = detectedContentType };

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

        var now = timeProvider.GetUtcNow();
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null && residentSpotId is { } assignedSpotId)
        {
            var ownSpot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == assignedSpotId, cancellationToken);
            var today = SiteTime.Today(now, timeZone);
            var (start, end) = SiteTime.Day(today, timeZone);
            if (ownSpot is null || !(await ResidentAllocation.AssignedDatesAsync(dbContext, ownSpot, userId,
                    today, today, cancellationToken)).Contains(today)
                || await dbContext.SpotReleases.AnyAsync(r => r.SpotId == assignedSpotId && r.Date == today, cancellationToken)
                || await dbContext.Reservations.AnyAsync(r => r.SpotId == assignedSpotId
                    && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                    && r.StartUtc < end && r.EndUtc > start, cancellationToken))
                return BlockedSpotOutcome.Failure("Parking_Error_NoOwnedSpot");
            // Materialize only the reporter's existing entitlement for the incident history.
            // This never charges credits or creates another overlapping right to capacity.
            reservation = new Reservation(assignedSpotId, userId, start, end, false, now, countsTowardWeeklyLimit: false);
            dbContext.Reservations.Add(reservation);
        }
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
        // report temporarily blocks the spot until the window ends or a manager clears it.
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
            var originalType = await dbContext.ParkingSpots.Where(s => s.Id == reservation.SpotId)
                .Select(s => s.Type).SingleAsync(cancellationToken);
            var compatible = await dbContext.ParkingSpots.Where(s => candidates.Contains(s.Id) && s.Type == originalType)
                .Select(s => s.Id).ToListAsync(cancellationToken);
            var replacementSpotId = compatible.FirstOrDefault(id => id != reservation.SpotId && !held.Contains(id));

            if (replacementSpotId != Guid.Empty)
            {
                // Carry the original charge over: the refund above plus an identical charge here
                // nets to zero for the wallet while the ledger keeps a clean trail of the move.
                replacement = new Reservation(replacementSpotId, userId, reservation.StartUtc, reservation.EndUtc,
                    reservation.IsOffPeak, now, reservation.CreditsCharged, reservation.FromQueue,
                    reservation.CountsTowardWeeklyLimit, reservation.RefundDeadlineUtc);
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

                if (residentSpotId is not null)
                    dbContext.SpotReleases.Add(new SpotRelease(reservation.SpotId, userId,
                        SiteTime.Today(now, timeZone), now, 0, SpotReleaseSource.AlternativeBooking));
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


        if (voucherGranted)
        {
            await ParkingNotifications.EnqueueAsync(dbContext, now, userId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages.ForEconomy(policy, "Parking_Notify_VoucherGranted_Title"),
                messages.ForEconomy(policy, "Parking_Notify_VoucherGranted_Body"), cancellationToken);

            // Calling the reviewers is the oversight desk's job, not this one's. It used to happen
            // here, addressed to every ManageSpots holder — the wrong audience, since the evidence
            // (a photograph of somebody's car) is gated behind ReviewMismatches. The desk opens a
            // case for this report within a sweep and tells the people who may actually judge it.
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ParkingNotifications.PublishAsync(dbContext, notifications, cancellationToken);

        var outcome = relocatedCode is null
            ? BlockedSpotOutcome.Recorded(voucherGranted)
            : BlockedSpotOutcome.Relocated(relocatedCode, voucherGranted);
        return outcome with
        {
            CreditsEnabled = policy.CreditsEnabled,
            RefundedCredits = policy.CreditsEnabled && replacement is null ? reservation.CreditsCharged : 0,
        };
    }
    public Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => SendDueRemindersCoreAsync(cancellationToken), cancellationToken);

    private async Task<int> SendDueRemindersCoreAsync(CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
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

        var reminded = 0;
        foreach (var reservation in due)
        {
            reminded++;
            var code = spotCodes.GetValueOrDefault(reservation.SpotId, string.Empty);
            await ParkingNotifications.EnqueueAsync(dbContext, now, reservation.UserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_Reminder_Title"],
                messages["Parking_Notify_Reminder_Body", code],
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ParkingNotifications.PublishAsync(dbContext, notifications, cancellationToken);
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
            .Where(q => q.Status == QueueEntryStatus.Waiting && q.EndUtc > now)
            .Select(q => new { q.Id, q.UserId, q.StartUtc, q.EndUtc, q.CreatedAtUtc, q.RequiredSpotType })
            .ToListAsync(cancellationToken);

        var spotIds = mine.Where(q => q.OfferedSpotId != null).Select(q => q.OfferedSpotId!.Value).ToList();
        var codes = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        return mine.Select(q =>
        {
            // Estimated position among older overlapping requests competing for compatible types.
            // Actual matching also checks availability and eligibility for each complete window.
            var position = q.Status == QueueEntryStatus.Offered
                ? 0
                : 1 + waiting.Count(w => w.StartUtc < q.EndUtc && w.EndUtc > q.StartUtc
                    && (w.RequiredSpotType == null || q.RequiredSpotType == null || w.RequiredSpotType == q.RequiredSpotType)
                    && (w.CreatedAtUtc < q.CreatedAtUtc || w.CreatedAtUtc == q.CreatedAtUtc && w.Id.CompareTo(q.Id) < 0));

            return new QueueEntryDto(
                q.Id, q.StartUtc, q.EndUtc, q.Status, position,
                q.OfferedSpotId,
                q.OfferedSpotId is { } sid ? codes.GetValueOrDefault(sid) : null,
                q.OfferExpiresAtUtc);
        }).ToList();
    }

    public Task<ParkingResult> JoinQueueAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default) =>
        JoinQueueAsync(userId, startUtc, endUtc, null, cancellationToken);

    public Task<ParkingResult> JoinQueueAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        ParkingSpotType? requiredSpotType, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => JoinQueueCoreAsync(userId, startUtc, endUtc, requiredSpotType, cancellationToken), cancellationToken);

    private async Task<ParkingResult> JoinQueueCoreAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        ParkingSpotType? requiredSpotType, CancellationToken cancellationToken)
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

        if (policy.GetReservationDateAvailability(startUtc, now, timeZone).ToParkingErrorKey() is { } dateError)
        {
            return ParkingResult.Failure(dateError);
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
        if (requiredSpotType == ParkingSpotType.Visitor || requiredSpotType is { } type && !Enum.IsDefined(type))
            return ParkingResult.Failure("Parking_Error_InvalidState");
        if (available.Any(s => requiredSpotType is null || s.Type == requiredSpotType))
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

        dbContext.QueueEntries.Add(new QueueEntry(userId, startUtc, endUtc, now, requiredSpotType));
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
        ClaimQueueOfferAsync(userId, queueEntryId, false, cancellationToken);

    public Task<ParkingResult> ClaimQueueOfferAsync(Guid userId, Guid queueEntryId, bool confirmResidentRelease,
        CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => ClaimQueueOfferCoreAsync(userId, queueEntryId, confirmResidentRelease, cancellationToken), cancellationToken);

    private async Task<ParkingResult> ClaimQueueOfferCoreAsync(Guid userId, Guid queueEntryId, bool confirmResidentRelease, CancellationToken cancellationToken)
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
            confirmResidentRelease, handoffId: null, handoffActorId: null, cancellationToken);
    }

    public async Task<int> ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        var matched = await OptimisticConcurrency.RetryAsync(
            () => ProcessQueueCoreAsync(cancellationToken), cancellationToken);

        return matched.Offers.Count;
    }

    private async Task<QueueMatchResult> ProcessQueueCoreAsync(CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var offerHold = TimeSpan.FromMinutes(policy.QueueOfferMinutes);

        // Queue rowversions alone cannot protect the availability snapshot: a booking or another
        // matcher can change a different row after it was read. Keep reservations, resident
        // releases, active spots and queue offers in the same SQL Server serializable decision,
        // just like ReserveCoreAsync. This also coordinates independent application processes.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var active = await dbContext.QueueEntries
            .Where(q => q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            return new QueueMatchResult(policy.QueueOfferMinutes, now + offerHold, timeZone, [], []);
        }

        // Expire passed windows; lapse stale offers back to waiting so the spot can move on.
        var lapsedOffers = new List<(QueueEntry Entry, Guid SpotId)>();
        var withdrawnOffers = new List<(QueueEntry Entry, Guid SpotId)>();
        var skipThisRound = new HashSet<Guid>();
        var deliveryIds = active.Where(q => q.OfferEmailDeliveryId != null).Select(q => q.OfferEmailDeliveryId!.Value).ToList();
        var deliveries = await dbContext.NotificationEmailDeliveries.AsNoTracking()
            .Where(d => deliveryIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        foreach (var entry in active)
        {
            if (entry.EndUtc <= now)
            {
                entry.Expire();
            }
            else if (entry.Status == QueueEntryStatus.Offered && entry.OfferExpiresAtUtc is { } expires && expires <= now)
            {
                if (entry.OfferEmailDeliveryId is { } emailId && deliveries.TryGetValue(emailId, out var delivery)
                    && (delivery.SentAtUtc is null || delivery.SentAtUtc >= expires))
                {
                    // Delivery failure is not the waiter's missed response. Release the capacity
                    // for others, retain FIFO position and retry an offer after delivery recovers.
                    withdrawnOffers.Add((entry, entry.OfferedSpotId!.Value));
                    entry.WithdrawOffer();
                    skipThisRound.Add(entry.Id);
                }
                else
                {
                    lapsedOffers.Add((entry, entry.OfferedSpotId!.Value));
                    entry.RequeueAfterMissedOffer(now);
                }
            }
            else if (entry.Status == QueueEntryStatus.Offered && entry.OfferedSpotId is { } offeredSpot)
            {
                var available = await ParkingCapacity.AvailableAsync(dbContext, entry.StartUtc, entry.EndUtc,
                    now, timeZone, false, cancellationToken);
                if (!available.Any(s => s.Id == offeredSpot && (entry.RequiredSpotType == null || s.Type == entry.RequiredSpotType)))
                {
                    withdrawnOffers.Add((entry, offeredSpot));
                    entry.WithdrawOffer();
                    entry.TrackOfferEmail(null);
                }
            }
        }

        // A hold protects its requested interval, not every date on the same physical spot.
        // Keep the entries so adjacent/non-overlapping windows can be offered independently.
        var heldEntries = active
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId is not null)
            .ToList();

        // Achievements never affect access. The queue remains first-come, first-served.
        var waiting = active
            .Where(q => q.Status == QueueEntryStatus.Waiting && q.EndUtc > now)
            .OrderBy(q => q.CreatedAtUtc)
            .ThenBy(q => q.Id)
            .ToList();
        // Load one consistent snapshot inside the transaction, avoiding two database round-trips
        // per waiter while retaining its locks until all offers have been saved.
        var earliestStart = waiting.Count == 0 ? now : waiting.Min(q => q.StartUtc);
        var latestEnd = waiting.Count == 0 ? now : waiting.Max(q => q.EndUtc);
        var queuedReservations = await dbContext.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < latestEnd && r.EndUtc > earliestStart)
            .Select(r => new QueueReservationSnapshot(r.SpotId, r.UserId, r.StartUtc, r.EndUtc))
            .ToListAsync(cancellationToken);
        var candidatesByWindow = new Dictionary<(DateTimeOffset Start, DateTimeOffset End), List<ParkingSpot>>();

        var offers = new List<(QueueEntry Entry, Guid SpotId)>();
        foreach (var entry in waiting)
        {
            if (skipThisRound.Contains(entry.Id)
                || entry.OfferEmailDeliveryId is { } emailId && deliveries.TryGetValue(emailId, out var pendingDelivery)
                    && pendingDelivery.SentAtUtc is null) continue;
            // A user who already holds a reservation for the window could never claim the offer
            // (own-conflict) — skip them rather than pinning a spot on an unclaimable hold.
            var hasOverlappingReservation = queuedReservations.Any(r => r.UserId == entry.UserId
                && r.StartUtc < entry.EndUtc && r.EndUtc > entry.StartUtc);
            if (hasOverlappingReservation)
            {
                continue;
            }

            if (!await QueueEntryIsEligibleAsync(dbContext, entry, policy, timeZone, now, cancellationToken)) continue;
            var key = (entry.StartUtc, entry.EndUtc);
            if (!candidatesByWindow.TryGetValue(key, out var candidates))
            {
                candidates = await ParkingCapacity.AvailableAsync(dbContext, entry.StartUtc, entry.EndUtc,
                    now, timeZone, false, cancellationToken);
                candidatesByWindow[key] = candidates;
            }
            var spotId = candidates.Where(s => entry.RequiredSpotType == null || s.Type == entry.RequiredSpotType)
                .Select(s => s.Id).FirstOrDefault(id => !heldEntries.Any(held =>
                    held.OfferedSpotId == id && held.Overlaps(entry.StartUtc, entry.EndUtc)));
            if (spotId == Guid.Empty)
            {
                continue;
            }

            entry.Offer(spotId, now + offerHold);
            heldEntries.Add(entry);
            offers.Add((entry, spotId));
        }

        // A lost race must retry the entire decision; dropping only the conflicting queue row
        // would leave other offers based on its stale reservation/hold snapshot.

        var codes = new Dictionary<Guid, string>();
        if (offers.Count > 0 || lapsedOffers.Count > 0 || withdrawnOffers.Count > 0)
        {
            var spotIdsToName = offers.Select(o => o.SpotId).Concat(lapsedOffers.Select(l => l.SpotId))
                .Concat(withdrawnOffers.Select(l => l.SpotId)).Distinct().ToList();
            codes = await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => spotIdsToName.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);
        }

        var matched = new QueueMatchResult(policy.QueueOfferMinutes, now + offerHold, timeZone,
            offers.Select(o => new QueueMatchNotification(o.Entry.UserId, codes.GetValueOrDefault(o.SpotId, string.Empty),
                o.Entry.OfferExpiresAtUtc, Entry: o.Entry)).ToList(),
            lapsedOffers.Select(o => new QueueMatchNotification(o.Entry.UserId, codes.GetValueOrDefault(o.SpotId, string.Empty)))
                .Concat(withdrawnOffers.Select(o => new QueueMatchNotification(o.Entry.UserId,
                    codes.GetValueOrDefault(o.SpotId, string.Empty), KeepsPosition: true))).ToList());
        await NotifyQueueMatchesAsync(dbContext, now, matched, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ParkingNotifications.PublishAsync(dbContext, notifications, cancellationToken);
        return matched;
    }

    private async Task NotifyQueueMatchesAsync(D3ParkingDbContext dbContext, DateTimeOffset now, QueueMatchResult matched, CancellationToken cancellationToken)
    {
        if (matched.Offers.Count > 0 || matched.LapsedOffers.Count > 0)
        {
            // The claim window is short, so the email carries a CTA deep link and an explicit
            // local-time deadline. Without a configured canonical URL (dev) the button is omitted.
            var baseUrl = await siteSettings.GetCanonicalBaseUrlAsync(cancellationToken);
            var claimUrl = baseUrl is null ? null : $"{baseUrl.TrimEnd('/')}/parking";

            foreach (var offer in matched.Offers)
            {
                var expires = offer.ExpiresAtUtc ?? matched.ExpiresAtUtc;
                var deadlineLocal = SiteTime.TimeOfDay(expires, matched.TimeZone).ToString("HH\\:mm");
                var deliveryId = await ParkingNotifications.EnqueueAsync(dbContext, now, offer.UserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                    messages["Parking_Notify_QueueOffer_Title"],
                    messages["Parking_Notify_QueueOffer_Body", offer.SpotCode, Math.Max(1, (int)Math.Ceiling((expires - now).TotalMinutes))],
                    email: true,
                    new NotificationEmailOptions(
                        ActionText: claimUrl is null ? null : messages["Email_QueueOffer_Action"].Value,
                        ActionUrl: claimUrl,
                        DeadlineText: messages["Email_QueueOffer_Deadline", deadlineLocal].Value),
                    cancellationToken);
                offer.Entry?.TrackOfferEmail(deliveryId);
            }

            // The demoted waiter learns why the spot is gone — bell/push only, no email needed.
            foreach (var lapsed in matched.LapsedOffers)
            {
                await ParkingNotifications.EnqueueAsync(dbContext, now, lapsed.UserId, NotificationCategory.SelfService, NotificationLevel.Info,
                    messages[lapsed.KeepsPosition ? "Parking_Notify_OfferWithdrawn_Title" : "Parking_Notify_OfferLapsed_Title"],
                    messages[lapsed.KeepsPosition ? "Parking_Notify_OfferWithdrawn_Body" : "Parking_Notify_OfferLapsed_Body", lapsed.SpotCode],
                    cancellationToken);
            }
        }
    }

    private sealed record QueueMatchNotification(Guid UserId, string SpotCode, DateTimeOffset? ExpiresAtUtc = null,
        bool KeepsPosition = false, QueueEntry? Entry = null);

    private static async Task<bool> QueueEntryIsEligibleAsync(D3ParkingDbContext db, QueueEntry entry,
        IncentivePolicy policy, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        if (entry.EndUtc <= now || entry.RequiredSpotType == ParkingSpotType.Visitor) return false;
        if (entry.Status == QueueEntryStatus.Waiting && entry.OfferEmailDeliveryId is { } deliveryId
            && await db.NotificationEmailDeliveries.AnyAsync(d => d.Id == deliveryId && d.SentAtUtc == null, ct)) return false;
        if (await db.Users.AnyAsync(u => u.Id == entry.UserId && u.Status != AccountStatus.Active, ct)) return false;
        if (await db.Reservations.AnyAsync(r => r.UserId == entry.UserId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < entry.EndUtc && r.EndUtc > entry.StartUtc, ct)) return false;
        if (await ValidateWeeklyPlannerLimitAsync(db, entry.UserId, entry.StartUtc, policy, zone, ct) is not null) return false;
        var score = await db.ParkerScores.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == entry.UserId, ct)
            ?? new ParkerScore(entry.UserId);
        if (score.QueueBannedUntilUtc > now) return false;
        if (policy.CreditsEnabled)
        {
            score.GrantCreditIfDue(policy.MonthlyCreditAllowance, ParkerScore.PeriodOf(now, policy.BudgetRenewalPeriod, zone), now);
            if (score.Credits < policy.ComputeReservationCost(0)
                && !await db.ApologyVouchers.AnyAsync(v => v.UserId == entry.UserId && v.Status == ApologyVoucherStatus.Approved
                    && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now, ct)) return false;
        }
        if (policy.ResidentAlternativeBookingPolicy == ResidentAlternativeBookingPolicy.Deny)
        {
            var own = await db.ParkingSpots.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == entry.UserId
                || db.ParkingSpotResidents.Any(r => r.SpotId == s.Id && r.UserId == entry.UserId && r.RemovedAtUtc == null), ct);
            if (own is not null)
            {
                var first = SiteTime.Today(entry.StartUtc, zone);
                var last = SiteTime.Today(entry.EndUtc.AddTicks(-1), zone);
                var assigned = await ResidentAllocation.AssignedDatesAsync(db, own, entry.UserId, first, last, ct);
                var released = await db.SpotReleases.Where(r => r.SpotId == own.Id && r.Date >= first && r.Date <= last)
                    .Select(r => r.Date).ToListAsync(ct);
                if (assigned.Any(d => !released.Contains(d))) return false;
            }
        }
        return true;
    }

    private sealed record QueueMatchResult(int OfferMinutes, DateTimeOffset ExpiresAtUtc, TimeZoneInfo TimeZone,
        IReadOnlyList<QueueMatchNotification> Offers, IReadOnlyList<QueueMatchNotification> LapsedOffers);

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

        if (voucher.Status == ApologyVoucherStatus.Approved && voucher.ExpiresAtUtc > now
            && !await HoldsAnotherUsableVoucherAsync(dbContext, voucher, now, cancellationToken))
        {
            voucher.Restore();
        }
    }

    private static Task<bool> HoldsAnotherUsableVoucherAsync(D3ParkingDbContext db, ApologyVoucher voucher,
        DateTimeOffset now, CancellationToken ct) => db.ApologyVouchers.AnyAsync(v =>
            v.UserId == voucher.UserId && v.Id != voucher.Id
            && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now
            && (v.Status == ApologyVoucherStatus.PendingApproval || v.Status == ApologyVoucherStatus.Approved), ct);

    // Raw availability for a window (active, unreserved, owned-spot visibility) without the waitlist
    // hold filter — the queue matcher manages holds itself in memory.
    private static async Task<List<Guid>> AvailableSpotIdsAsync(D3ParkingDbContext dbContext, IncentivePolicy policy,
        TimeZoneInfo timeZone, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await ParkingCapacity.AvailableAsync(dbContext, startUtc, endUtc, now, timeZone, false, cancellationToken))
            .Select(s => s.Id).ToList();

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

    private async Task NotifyNewAchievementsAsync(D3ParkingDbContext dbContext, DateTimeOffset now,
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
            await ParkingNotifications.EnqueueAsync(dbContext, now,
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
