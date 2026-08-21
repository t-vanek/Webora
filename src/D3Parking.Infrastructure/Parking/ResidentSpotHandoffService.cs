using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
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

public sealed class ResidentSpotHandoffService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IReservationService reservations,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : IResidentSpotHandoffService
{
    private const int SearchLimit = 20;
    private static readonly TimeSpan HandoffLifetime = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<ResidentSpotHandoffDto>> GetMineAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ExpireDueAsync(dbContext, now, cancellationToken);

        var handoffs = await dbContext.ResidentSpotHandoffs.AsNoTracking()
            .Where(h => h.ResidentId == userId || h.RecipientId == userId)
            .OrderByDescending(h => h.Status == ResidentSpotHandoffStatus.PendingResident
                || h.Status == ResidentSpotHandoffStatus.Offered)
            .ThenBy(h => h.StartUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        if (handoffs.Count == 0)
        {
            return [];
        }

        var userIds = handoffs.SelectMany(h => new[] { h.ResidentId, h.RecipientId }).Distinct().ToList();
        var names = await dbContext.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty, cancellationToken);
        var spotIds = handoffs.Select(h => h.SpotId).Distinct().ToList();
        var spots = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        return handoffs.Select(h => new ResidentSpotHandoffDto(
            h.Id, h.SpotId, spots.GetValueOrDefault(h.SpotId, string.Empty),
            h.ResidentId, names.GetValueOrDefault(h.ResidentId, string.Empty),
            h.RecipientId, names.GetValueOrDefault(h.RecipientId, string.Empty),
            h.Kind, h.Status, h.StartUtc, h.EndUtc, h.CreatedAtUtc, h.ExpiresAtUtc,
            h.MaxCreditsAuthorized, h.ReservationId)).ToList();
    }

    public async Task<IReadOnlyList<ResidentSpotHandoffUserDto>> SearchRecipientsAsync(
        Guid residentId, string? search, CancellationToken cancellationToken = default)
    {
        var term = NormalizeSearch(search);
        if (term is null)
        {
            return [];
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var eligible = EligibleParkerIds(dbContext);
        return await dbContext.Users.AsNoTracking()
            .Where(u => u.Id != residentId && u.Status == AccountStatus.Active && eligible.Contains(u.Id)
                && ((u.DisplayName != null && u.DisplayName.Contains(term))
                    || (u.Email != null && u.Email.Contains(term))))
            .OrderBy(u => u.DisplayName ?? u.Email)
            .Take(SearchLimit)
            .Select(u => new ResidentSpotHandoffUserDto(
                u.Id, u.DisplayName ?? u.Email ?? string.Empty, u.Email, null))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ResidentSpotHandoffUserDto>> SearchResidentsAsync(
        Guid requesterId, string? search, CancellationToken cancellationToken = default)
    {
        var term = NormalizeSearch(search);
        if (term is null)
        {
            return [];
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var activeMemberships =
            from membership in dbContext.ParkingSpotResidents.AsNoTracking()
            join user in dbContext.Users.AsNoTracking() on membership.UserId equals user.Id
            join spot in dbContext.ParkingSpots.AsNoTracking() on membership.SpotId equals spot.Id
            where membership.RemovedAtUtc == null && user.Id != requesterId
                && user.Status == AccountStatus.Active && spot.IsActive
                && ((user.DisplayName != null && user.DisplayName.Contains(term))
                    || (user.Email != null && user.Email.Contains(term))
                    || spot.Code.Contains(term))
            select new ResidentSpotHandoffUserDto(
                user.Id, user.DisplayName ?? user.Email ?? string.Empty, user.Email, spot.Code);

        var rows = await activeMemberships
            .OrderBy(r => r.DisplayName)
            .Take(SearchLimit)
            .ToListAsync(cancellationToken);

        // Legacy owner pointers are retained for upgraded installations. Add only residents not
        // already represented by a current membership.
        if (rows.Count < SearchLimit)
        {
            var existing = rows.Select(r => r.Id).ToList();
            var legacy = await (
                from spot in dbContext.ParkingSpots.AsNoTracking()
                join user in dbContext.Users.AsNoTracking() on spot.OwnerId equals user.Id
                where spot.OwnerId != null && user.Id != requesterId && !existing.Contains(user.Id)
                    && user.Status == AccountStatus.Active && spot.IsActive
                    && ((user.DisplayName != null && user.DisplayName.Contains(term))
                        || (user.Email != null && user.Email.Contains(term))
                        || spot.Code.Contains(term))
                orderby user.DisplayName ?? user.Email
                select new ResidentSpotHandoffUserDto(
                    user.Id, user.DisplayName ?? user.Email ?? string.Empty, user.Email, spot.Code))
                .Take(SearchLimit - rows.Count)
                .ToListAsync(cancellationToken);
            rows.AddRange(legacy);
        }

        return rows;
    }

    public Task<ParkingResult> CreateOfferAsync(
        Guid residentId, Guid recipientId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(
            () => CreateCoreAsync(residentId, recipientId, ResidentSpotHandoffKind.ResidentOffer,
                startUtc, endUtc, null, cancellationToken), cancellationToken);

    public async Task<ParkingResult> CreateRequestAsync(
        Guid requesterId, Guid residentId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var quote = await reservations.GetQuoteAsync(requesterId, startUtc, endUtc, cancellationToken);
        return await OptimisticConcurrency.RetryAsync(
            () => CreateCoreAsync(residentId, requesterId, ResidentSpotHandoffKind.UserRequest,
                startUtc, endUtc, quote.Cost, cancellationToken), cancellationToken);
    }

    private async Task<ParkingResult> CreateCoreAsync(
        Guid residentId, Guid recipientId, ResidentSpotHandoffKind kind,
        DateTimeOffset startUtc, DateTimeOffset endUtc, int? maxCreditsAuthorized,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (residentId == recipientId)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_Self");
        }

        if (endUtc <= startUtc || endUtc <= now.AddMinutes(1))
        {
            return ParkingResult.Failure("Parking_Handoff_Error_InvalidWindow");
        }

        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var windowError = ValidateWindow(policy, timeZone, startUtc, endUtc, now);
        if (windowError is not null)
        {
            return ParkingResult.Failure(windowError);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        await ExpireDueAsync(dbContext, now, cancellationToken);
        var residentSpot = await FindResidentSpotAsync(dbContext, residentId, cancellationToken);
        if (residentSpot is null || !residentSpot.IsActive)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_NoResidentSpot");
        }

        if (!await IsEligibleParkerAsync(dbContext, recipientId, cancellationToken))
        {
            return ParkingResult.Failure("Parking_Handoff_Error_RecipientUnavailable");
        }

        var firstDate = SiteTime.Today(startUtc, timeZone);
        var lastDate = SiteTime.Today(endUtc.AddTicks(-1), timeZone);
        var assigned = await ResidentAllocation.AssignedDatesAsync(
            dbContext, residentSpot, residentId, firstDate, lastDate, cancellationToken);
        if (assigned.Count != lastDate.DayNumber - firstDate.DayNumber + 1)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_NotAssigned");
        }

        var publiclyReleased = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == residentSpot.Id
            && r.Date >= firstDate && r.Date <= lastDate, cancellationToken);
        if (publiclyReleased)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_PubliclyReleased");
        }

        var occupied = await dbContext.Reservations.AnyAsync(r => r.SpotId == residentSpot.Id
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < endUtc && r.EndUtc > startUtc, cancellationToken);
        if (occupied)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_SpotConflict");
        }

        var existing = await dbContext.ResidentSpotHandoffs.AnyAsync(h => h.SpotId == residentSpot.Id
            && (h.Status == ResidentSpotHandoffStatus.PendingResident || h.Status == ResidentSpotHandoffStatus.Offered)
            && h.ExpiresAtUtc > now && h.StartUtc < endUtc && h.EndUtc > startUtc, cancellationToken);
        if (existing)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_AlreadyPending");
        }

        var expiresAt = now + HandoffLifetime < endUtc ? now + HandoffLifetime : endUtc;
        var handoff = kind == ResidentSpotHandoffKind.ResidentOffer
            ? ResidentSpotHandoff.CreateOffer(
                residentSpot.Id, residentId, recipientId, startUtc, endUtc, now, expiresAt)
            : ResidentSpotHandoff.CreateRequest(
                residentSpot.Id, residentId, recipientId, startUtc, endUtc, now, expiresAt,
                maxCreditsAuthorized ?? 0);
        dbContext.ResidentSpotHandoffs.Add(handoff);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var residentName = await UserNameAsync(residentId, cancellationToken);
        var recipientName = await UserNameAsync(recipientId, cancellationToken);
        var targetId = kind == ResidentSpotHandoffKind.ResidentOffer ? recipientId : residentId;
        var baseUrl = await siteSettings.GetCanonicalBaseUrlAsync(cancellationToken);
        var actionUrl = baseUrl is null ? null : $"{baseUrl.TrimEnd('/')}/parking";
        var deadlineLocal = TimeZoneInfo.ConvertTime(expiresAt, timeZone).ToString("g");
        await notifications.NotifyAsync(
            targetId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages[kind == ResidentSpotHandoffKind.ResidentOffer
                ? "Parking_Notify_HandoffOffer_Title"
                : "Parking_Notify_HandoffRequest_Title"],
            messages[kind == ResidentSpotHandoffKind.ResidentOffer
                ? "Parking_Notify_HandoffOffer_Body"
                : "Parking_Notify_HandoffRequest_Body",
                kind == ResidentSpotHandoffKind.ResidentOffer ? residentName : recipientName,
                residentSpot.Code],
            email: true,
            new NotificationEmailOptions(
                ActionText: actionUrl is null ? null : messages["Email_Handoff_Action"].Value,
                ActionUrl: actionUrl,
                DeadlineText: messages["Email_Handoff_Deadline", deadlineLocal].Value),
            cancellationToken);

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> AcceptAsync(
        Guid actorId, Guid handoffId, CancellationToken cancellationToken = default)
    {
        await using var previewContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preview = await previewContext.ResidentSpotHandoffs.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == handoffId, cancellationToken);
        var result = await reservations.AcceptHandoffAsync(actorId, handoffId, cancellationToken);
        if (!result.Succeeded)
        {
            // The resident approving a request must not learn whether the requester is short on
            // credits, over quota, or already booked elsewhere. Those are the requester's private
            // planning facts; the resident only needs to know that the automatic booking could not
            // be completed now.
            return preview is { Kind: ResidentSpotHandoffKind.UserRequest, ResidentId: var residentId }
                && residentId == actorId
                ? ParkingResult.Failure("Parking_Handoff_Error_RequestCannotComplete")
                : result;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var handoff = await dbContext.ResidentSpotHandoffs.AsNoTracking()
            .FirstAsync(h => h.Id == handoffId, cancellationToken);
        var spotCode = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == handoff.SpotId).Select(s => s.Code).FirstAsync(cancellationToken);

        if (handoff.Kind == ResidentSpotHandoffKind.ResidentOffer)
        {
            var recipientName = await UserNameAsync(handoff.RecipientId, cancellationToken);
            await notifications.NotifyAsync(handoff.ResidentId, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_HandoffAccepted_Title"],
                messages["Parking_Notify_HandoffAccepted_Body", recipientName, spotCode], cancellationToken);
        }

        return result;
    }

    public Task<ParkingResult> DeclineAsync(
        Guid actorId, Guid handoffId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(actorId, handoffId, decline: true, cancellationToken);

    public Task<ParkingResult> CancelAsync(
        Guid actorId, Guid handoffId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(actorId, handoffId, decline: false, cancellationToken);

    private async Task<ParkingResult> ChangeStatusAsync(
        Guid actorId, Guid handoffId, bool decline, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var handoff = await dbContext.ResidentSpotHandoffs
            .FirstOrDefaultAsync(h => h.Id == handoffId, cancellationToken);
        if (handoff is null || !handoff.IsActive)
        {
            return ParkingResult.Failure("Parking_Handoff_Error_NotActive");
        }
        if (handoff.ExpiresAtUtc <= now)
        {
            handoff.Expire(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Failure("Parking_Handoff_Error_NotActive");
        }

        var mayDecline = handoff.Kind == ResidentSpotHandoffKind.ResidentOffer
            ? actorId == handoff.RecipientId
            : actorId == handoff.ResidentId;
        var initiator = handoff.Kind == ResidentSpotHandoffKind.ResidentOffer
            ? handoff.ResidentId
            : handoff.RecipientId;
        if ((decline && !mayDecline) || (!decline && actorId != initiator))
        {
            return ParkingResult.Failure("Parking_Handoff_Error_NotAllowed");
        }

        if (decline)
        {
            handoff.Decline(now);
        }
        else
        {
            handoff.Cancel(now);
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var notifyUserId = decline ? initiator : (actorId == handoff.ResidentId ? handoff.RecipientId : handoff.ResidentId);
        await notifications.NotifyAsync(notifyUserId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages[decline ? "Parking_Notify_HandoffDeclined_Title" : "Parking_Notify_HandoffCancelled_Title"],
            messages[decline ? "Parking_Notify_HandoffDeclined_Body" : "Parking_Notify_HandoffCancelled_Body"],
            cancellationToken);
        return ParkingResult.Success;
    }

    private static string? ValidateWindow(
        IncentivePolicy policy,
        TimeZoneInfo timeZone,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset now)
    {
        if (!ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone))
            return "Parking_Error_ReservationTimeModeChanged";
        if (!policy.IsWithinReservationHorizon(startUtc, now, timeZone))
            return "Parking_Error_ReservationHorizon";
        if (!policy.IsReservationWeekdayAllowed(startUtc, timeZone))
            return "Parking_Error_ReservationWeekdayNotAllowed";
        if (!policy.IsPublicHolidayReservationAllowed(startUtc, timeZone))
            return "Parking_Error_PublicHolidayNotAllowed";
        return null;
    }

    private static IQueryable<Guid> EligibleParkerIds(D3ParkingDbContext dbContext) =>
        (from userRole in dbContext.UserRoles
         join claim in dbContext.RoleClaims on userRole.RoleId equals claim.RoleId
         where claim.ClaimType == D3ParkingClaimTypes.Permission
            && claim.ClaimValue == Permissions.Parking.Reserve
         select userRole.UserId).Distinct();

    private static async Task<bool> IsEligibleParkerAsync(
        D3ParkingDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var active = await dbContext.Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.Status == AccountStatus.Active, cancellationToken);
        return active && await EligibleParkerIds(dbContext).ContainsAsync(userId, cancellationToken);
    }

    private static async Task<ParkingSpot?> FindResidentSpotAsync(
        D3ParkingDbContext dbContext, Guid residentId, CancellationToken cancellationToken)
    {
        var spotId = await dbContext.ParkingSpotResidents.AsNoTracking()
            .Where(r => r.UserId == residentId && r.RemovedAtUtc == null)
            .OrderBy(r => r.AssignedAtUtc)
            .Select(r => (Guid?)r.SpotId)
            .FirstOrDefaultAsync(cancellationToken);
        return spotId is { } id
            ? await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            : await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == residentId, cancellationToken);
    }

    private static async Task ExpireDueAsync(
        D3ParkingDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await dbContext.ResidentSpotHandoffs
            .Where(h => (h.Status == ResidentSpotHandoffStatus.PendingResident
                    || h.Status == ResidentSpotHandoffStatus.Offered)
                && h.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken);
        foreach (var handoff in due)
        {
            handoff.Expire(now);
        }
        if (due.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<string> UserNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName ?? u.Email ?? string.Empty)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    private static string? NormalizeSearch(string? search)
    {
        var term = search?.Trim();
        return term is { Length: >= 2 } ? term : null;
    }
}
