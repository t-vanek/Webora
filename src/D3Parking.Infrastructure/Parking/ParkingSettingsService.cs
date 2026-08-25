using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Parking;
using D3Parking.Application.Parking.Maps;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class ParkingSettingsService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IMemoryCache cache,
    // For the site time zone only: the adaptive controller runs once per *local* day.
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    ILogger<ParkingSettingsService> logger) : IParkingSettingsService
{
    private const string PolicyCacheKey = "d3parking:parking-policy";
    private const string OrientationMapCacheKey = "d3parking:parking-orientation-map";

    public async Task<IncentivePolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(PolicyCacheKey, out var cached) && cached is IncentivePolicy policy)
        {
            return policy;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        policy = (await GetOrCreateAsync(dbContext, cancellationToken)).ToPolicy();

        using var entry = cache.CreateEntry(PolicyCacheKey);
        entry.Value = policy;
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
        return policy;
    }

    public async Task<TimeSpan> GetSweepIntervalAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).SweepInterval;
    }

    public async Task<GeoPoint?> GetLotLocationAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var s = await GetOrCreateAsync(dbContext, cancellationToken);
        return s is { LotLatitude: { } lat, LotLongitude: { } lon } ? new GeoPoint(lat, lon) : null;
    }

    public async Task<ParkingSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var s = await GetOrCreateAsync(dbContext, cancellationToken);
        return new ParkingSettingsDto(
            s.ReleasePoints, s.OffPeakBonusPoints, s.NoShowPenaltyPoints,
            s.ReleaseCutoff, s.NoShowGracePeriod, s.ReminderLeadTime,
            s.ReservationTimeMode,
            s.ReservationHorizonDays, s.SameDayReservationsAllowed, s.AllowedReservationWeekdays, s.WeeklyReservationLimitEnabled,
            s.AllowedReservationWeekdays.ClampWeeklyReservationLimit(s.WeeklyReservationLimit), s.LastMinuteUnlimitedHours,
            s.PeakStart, s.PeakEnd, s.SweepInterval,
            s.ResidentHoldUntil, s.ResidentReleasePointsPerHour, s.ResidentReleaseMaxPoints,
            s.ResidentWastedShareClawbackPercent,
            s.ResidentPlanHorizonDays,
            s.LotLatitude, s.LotLongitude, s.SharedTakenBasePoints, s.SharedTakenReferenceKm, s.SharedTakenMaxMultiplier,
            s.AutoVerifyHomeAddress, s.AutoVerifyMaxDistanceKm, s.MaxRewardedReleasesPerDay, s.MaxReleaseRangeDays,
            s.BaseReservationCost, s.PeakPricePercent, s.OccupancyPricePercent, s.MaxReservationCost, s.MonthlyCreditAllowance,
            s.BudgetRenewalPeriod,
            s.QueueOfferMinutes, s.QueueNoShowPenaltyPoints, s.QueueNoShowCreditPenalty, s.QueueNoShowBanDays, s.QueueNoShowAllowancePenalty,
            s.DemandReleaseOccupancyPercent, s.DemandReleaseQueueBonus, s.MaxReleaseReward,
            s.StreakBonusPerLevel, s.StreakBonusCap, s.TierSilverPoints, s.TierGoldPoints, s.TierPlatinumPoints,
            s.QueuePriorityPerTier, s.TierAllowanceBonus, s.TierDiscountPercent,
            s.ReputationDecayPercent, s.ReputationDecayIntervalDays,
            s.AdaptivePricingEnabled, s.AdaptiveTargetOccupancyPercent, s.AdaptiveGainPercent, s.AdaptiveDeadbandPercent,
            s.AdaptiveStepMaxPercent, s.AdaptivePeakMinPercent, s.AdaptivePeakMaxPercent, s.AdaptiveIntervalMinutes,
            s.TrustEnabled, s.TrustIntervalHours, s.TrustedBadgeThreshold,
            s.MaxPairTrustWeight, s.AntiCollusionEnabled, s.CollusionMinInteractions,
            s.CollusionConcentrationPercent, s.CollusionScanIntervalHours,
            s.AvailabilityCampaignsEnabled, s.AvailabilityLookaheadDays, s.AvailabilityFreeThresholdPercent,
            s.HighOccupancyCampaignsEnabled, s.AvailabilityBusyThresholdPercent,
            s.AvailabilityMinConsecutiveDays, s.AvailabilitySendHourLocal,
            s.OversightSlaCriticalHours, s.OversightSlaHighHours, s.OversightSlaNormalHours, s.OversightSlaLowHours,
            s.OversightRecurrenceWindowDays, s.OversightRecurrenceThreshold, s.OversightDigestHourLocal,
            s.OversightInfoDeadlineDays, s.OversightAllowUserReports, s.OversightDisputeWindowDays,
            s.ResidentReclaimPolicy, s.ManualReleasesAreBinding, s.ResidentProtectionDeadlineMode,
            s.ResidentProtectionLeadHours, s.ResidentProtectionPreviousDayTime, s.ResidentNoReplacementAction,
            s.ResidentAlternativeBookingPolicy,
            s.HolidayCalendarRegion, s.PublicHolidayReservationsAllowed);
    }

    public Task<ParkingResult> UpdateAsync(
        ParkingSettingsDto dto,
        Guid actingUserId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(dto, actingUserId, false, cancellationToken);

    public async Task<ParkingCalendarChangeImpactDto> GetCalendarChangeImpactAsync(
        ParkingSettingsDto dto,
        CancellationToken cancellationToken = default)
    {
        if (ParkingSettingsValidator.Validate(dto) is not null)
        {
            return ParkingCalendarChangeImpactDto.None;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        if (!BookingRulesChanged(settings, dto))
        {
            return ParkingCalendarChangeImpactDto.None;
        }

        var now = timeProvider.GetUtcNow();
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var impact = await FindCalendarImpactAsync(
            dbContext, ProposedCalendarPolicy(settings, dto), now, timeZone, cancellationToken);
        return impact.ToDto();
    }

    public async Task<ParkingResult> UpdateAsync(
        ParkingSettingsDto dto,
        Guid actingUserId,
        bool confirmCalendarInvalidation,
        CancellationToken cancellationToken = default)
    {
        if (ParkingSettingsValidator.Validate(dto) is { } validationError)
        {
            return ParkingResult.Failure(validationError);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var bookingRulesChanged = BookingRulesChanged(settings, dto);
        var calendarChanged = bookingRulesChanged
            || settings.ResidentPlanHorizonDays != dto.ResidentPlanHorizonDays;
        var now = timeProvider.GetUtcNow();
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        CalendarImpact? impact = null;
        if (bookingRulesChanged)
        {
            impact = await FindCalendarImpactAsync(
                dbContext, ProposedCalendarPolicy(settings, dto), now, timeZone, cancellationToken);
            if (impact.ToDto().RequiresConfirmation && !confirmCalendarInvalidation)
            {
                return ParkingResult.Failure("Parking_Settings_CalendarChangeConfirmationRequired");
            }
        }

        settings.Update(
            dto.ReleasePoints, dto.OffPeakBonusPoints, dto.NoShowPenaltyPoints,
            dto.ReleaseCutoff, dto.NoShowGracePeriod, dto.ReminderLeadTime,
            dto.ReservationTimeMode,
            dto.ReservationHorizonDays, dto.SameDayReservationsAllowed, dto.AllowedReservationWeekdays, dto.WeeklyReservationLimitEnabled,
            dto.WeeklyReservationLimit, dto.LastMinuteUnlimitedHours,
            dto.PeakStart, dto.PeakEnd, dto.SweepInterval,
            dto.ResidentHoldUntil, dto.ResidentReleasePointsPerHour, dto.ResidentReleaseMaxPoints,
            0, 0, dto.ResidentWastedShareClawbackPercent,
            dto.ResidentPlanHorizonDays,
            dto.LotLatitude, dto.LotLongitude, dto.SharedTakenBasePoints, dto.SharedTakenReferenceKm, dto.SharedTakenMaxMultiplier,
            dto.AutoVerifyHomeAddress, dto.AutoVerifyMaxDistanceKm, dto.MaxRewardedReleasesPerDay, dto.MaxReleaseRangeDays,
            dto.BaseReservationCost, 100, dto.OccupancyPricePercent, dto.MaxReservationCost, dto.MonthlyCreditAllowance,
            dto.BudgetRenewalPeriod,
            dto.QueueOfferMinutes, dto.QueueNoShowPenaltyPoints, dto.QueueNoShowCreditPenalty, dto.QueueNoShowBanDays, dto.QueueNoShowAllowancePenalty,
            dto.DemandReleaseOccupancyPercent, dto.DemandReleaseQueueBonus, dto.MaxReleaseReward,
            dto.StreakBonusPerLevel, dto.StreakBonusCap, dto.TierSilverPoints, dto.TierGoldPoints, dto.TierPlatinumPoints,
            dto.QueuePriorityPerTier, dto.TierAllowanceBonus, dto.TierDiscountPercent,
            dto.ReputationDecayPercent, dto.ReputationDecayIntervalDays,
            false, dto.AdaptiveTargetOccupancyPercent, dto.AdaptiveGainPercent, dto.AdaptiveDeadbandPercent,
            dto.AdaptiveStepMaxPercent, dto.AdaptivePeakMinPercent, dto.AdaptivePeakMaxPercent, dto.AdaptiveIntervalMinutes,
            dto.TrustEnabled, dto.TrustIntervalHours, dto.TrustedBadgeThreshold,
            dto.MaxPairTrustWeight, dto.AntiCollusionEnabled, dto.CollusionMinInteractions,
            dto.CollusionConcentrationPercent, dto.CollusionScanIntervalHours,
            dto.AvailabilityCampaignsEnabled, dto.AvailabilityLookaheadDays, dto.AvailabilityFreeThresholdPercent,
            dto.HighOccupancyCampaignsEnabled, dto.AvailabilityBusyThresholdPercent,
            dto.AvailabilityMinConsecutiveDays, dto.AvailabilitySendHourLocal,
            dto.OversightSlaCriticalHours, dto.OversightSlaHighHours, dto.OversightSlaNormalHours, dto.OversightSlaLowHours,
            dto.OversightRecurrenceWindowDays, dto.OversightRecurrenceThreshold, dto.OversightDigestHourLocal,
            dto.OversightInfoDeadlineDays, dto.OversightAllowUserReports, dto.OversightDisputeWindowDays,
            dto.ResidentReclaimPolicy, dto.ManualReleasesAreBinding, dto.ResidentProtectionDeadlineMode,
            dto.ResidentProtectionLeadHours, dto.ResidentProtectionPreviousDayTime, dto.ResidentNoReplacementAction,
            dto.ResidentAlternativeBookingPolicy,
            dto.HolidayCalendarRegion, dto.PublicHolidayReservationsAllowed);

        if (impact is not null && impact.ToDto().RequiresConfirmation)
        {
            await ReconcileCalendarImpactAsync(dbContext, impact, now, cancellationToken);
        }

        if (calendarChanged)
        {
            foreach (var spot in await dbContext.ParkingSpots
                         .Where(s => s.AutoReleaseUnplannedDays && s.PlanAppliedThrough != null)
                         .ToListAsync(cancellationToken))
            {
                spot.ResetPlanApplication();
            }

            foreach (var resident in await dbContext.ParkingSpotResidents
                         .Where(r => r.RemovedAtUtc == null && r.AutoReleaseUnplannedDays && r.PlanAppliedThrough != null)
                         .ToListAsync(cancellationToken))
            {
                resident.ResetPlanApplication();
            }
        }

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            $"Parking planner: mode={settings.ReservationTimeMode} horizon={settings.ReservationHorizonDays}d " +
            $"sameDay={settings.SameDayReservationsAllowed} " +
            $"weekdays={settings.AllowedReservationWeekdays} holidays={settings.HolidayCalendarRegion}:" +
            $"{(settings.PublicHolidayReservationsAllowed ? "allowed" : "blocked")} " +
            $"weeklyLimit={(settings.WeeklyReservationLimitEnabled ? settings.WeeklyReservationLimit : 0)} " +
            $"occupancyAlerts=low:{settings.AvailabilityCampaignsEnabled}@{settings.AvailabilityFreeThresholdPercent}%/" +
            $"high:{settings.HighOccupancyCampaignsEnabled}@{settings.AvailabilityBusyThresholdPercent}% " +
            $"credits={settings.BaseReservationCost > 0} budgetPeriod={settings.BudgetRenewalPeriod} " +
            $"residentReclaim={settings.ResidentReclaimPolicy} deadline={settings.ResidentProtectionDeadlineMode} " +
            $"alternative={settings.ResidentAlternativeBookingPolicy} " +
            $"fallback={settings.ResidentNoReplacementAction}",
            now));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        cache.Remove(PolicyCacheKey);

        logger.LogInformation("Parking settings changed by {AdminId}.", actingUserId);
        return ParkingResult.Success;
    }

    public async Task<PlannerCapacityDto> GetPlannerCapacityAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var activeSpots = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor)
            .Select(s => new { s.Id, s.OwnerId })
            .ToListAsync(cancellationToken);
        var activeSpotIds = activeSpots.Select(s => s.Id).ToList();
        var memberships = await dbContext.ParkingSpotResidents.AsNoTracking()
            .Where(r => r.RemovedAtUtc == null && activeSpotIds.Contains(r.SpotId))
            .Select(r => new { r.SpotId, r.UserId })
            .ToListAsync(cancellationToken);

        var residentSpotIds = memberships.Select(r => r.SpotId)
            .Concat(activeSpots.Where(s => s.OwnerId != null).Select(s => s.Id))
            .Distinct()
            .ToHashSet();
        var residentUserIds = memberships.Select(r => r.UserId)
            .Concat(activeSpots.Where(s => s.OwnerId != null).Select(s => s.OwnerId!.Value))
            .Distinct()
            .ToList();
        var activeResidents = await dbContext.Users.AsNoTracking()
            .CountAsync(u => u.Status == AccountStatus.Active && residentUserIds.Contains(u.Id), cancellationToken);

        var userPlates = await dbContext.Users.AsNoTracking()
            .Where(u => u.Status == AccountStatus.Active && u.LicensePlate != null && u.LicensePlate != "")
            .Select(u => u.LicensePlate!)
            .ToListAsync(cancellationToken);
        var fleetPlates = await dbContext.CompanyVehicles.AsNoTracking()
            .Where(v => v.IsActive)
            .Select(v => v.Plate)
            .ToListAsync(cancellationToken);
        var registeredVehicles = userPlates.Concat(fleetPlates)
            .Select(PlateNormalizer.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new PlannerCapacityDto(
            activeSpots.Count,
            residentSpotIds.Count,
            activeSpots.Count - residentSpotIds.Count,
            activeResidents,
            registeredVehicles);
    }

    public async Task<ParkingMapImageDto?> GetOrientationMapAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(OrientationMapCacheKey, out var cached) && cached is ParkingMapImageDto map)
        {
            return map;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await dbContext.ParkingSettings.AsNoTracking()
            .Where(s => s.Id == ParkingSettings.SingletonId && s.OrientationMap != null)
            .Select(s => new { s.OrientationMap, s.OrientationMapContentType })
            .FirstOrDefaultAsync(cancellationToken);

        if (stored?.OrientationMap is null)
        {
            return null;
        }

        map = new ParkingMapImageDto(stored.OrientationMap, stored.OrientationMapContentType ?? ImageContentType.Jpeg);
        using var entry = cache.CreateEntry(OrientationMapCacheKey);
        entry.Value = map;
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
        return map;
    }

    public async Task<ParkingResult> SetOrientationMapAsync(byte[] content, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0)
        {
            return ParkingResult.Failure("Parking_Settings_MapEmpty");
        }

        if (content.Length > ParkingSettings.MaxOrientationMapBytes)
        {
            return ParkingResult.Failure("Parking_Settings_MapTooLarge");
        }

        var detected = ImageContentType.Detect(content);
        if (detected is null)
        {
            return ParkingResult.Failure("Parking_Settings_MapNotImage");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.SetOrientationMap(content, detected);
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            $"Parking map uploaded: type={detected} bytes={content.Length}",
            timeProvider.GetUtcNow()));

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(OrientationMapCacheKey);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> ClearOrientationMapAsync(Guid actingUserId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.ClearOrientationMap();
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            "Parking map removed",
            timeProvider.GetUtcNow()));

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(OrientationMapCacheKey);
        return ParkingResult.Success;
    }

    public async Task<bool> AdaptPeakSurchargeAsync(double measuredOccupancy, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        if (!settings.AdaptivePricingEnabled)
        {
            return false;
        }

        // One adjustment per local day: the measurement is "today's finished peak window", a value
        // that stays constant for the rest of the day — re-applying the controller to it every
        // AdaptiveIntervalMinutes would compound the same step over and over (the interval setting
        // remains for the admin UI but no longer drives extra in-day adjustments).
        var now = timeProvider.GetUtcNow();
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        if (settings.LastAdaptiveAdjustUtc is { } last
            && SiteTime.Today(last, timeZone) == SiteTime.Today(now, timeZone))
        {
            return false;
        }

        var newPeak = settings.ToPolicy().ComputeAdaptivePeak(measuredOccupancy);
        var changed = newPeak != settings.PeakPricePercent;

        settings.ApplyAdaptiveAdjustment(newPeak, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(PolicyCacheKey);

        if (changed)
        {
            logger.LogInformation(
                "Adaptive pricing: peak surcharge set to {Peak}% (peak occupancy {Occupancy:P0}, target {Target}%).",
                newPeak, measuredOccupancy, settings.AdaptiveTargetOccupancyPercent);
        }

        return changed;
    }

    private static bool BookingRulesChanged(ParkingSettings settings, ParkingSettingsDto dto) =>
        settings.ReservationTimeMode != dto.ReservationTimeMode
        || settings.ReservationHorizonDays != dto.ReservationHorizonDays
        || settings.SameDayReservationsAllowed != dto.SameDayReservationsAllowed
        || settings.AllowedReservationWeekdays != dto.AllowedReservationWeekdays.Sanitize()
        || settings.HolidayCalendarRegion != dto.HolidayCalendarRegion
        || settings.PublicHolidayReservationsAllowed != dto.PublicHolidayReservationsAllowed;

    private static IncentivePolicy ProposedCalendarPolicy(ParkingSettings settings, ParkingSettingsDto dto) =>
        settings.ToPolicy() with
        {
            ReservationTimeMode = dto.ReservationTimeMode,
            ReservationHorizonDays = dto.ReservationHorizonDays,
            SameDayReservationsAllowed = dto.SameDayReservationsAllowed,
            AllowedReservationWeekdays = dto.AllowedReservationWeekdays.Sanitize(),
            HolidayCalendarRegion = dto.HolidayCalendarRegion,
            PublicHolidayReservationsAllowed = dto.PublicHolidayReservationsAllowed,
        };

    private static async Task<CalendarImpact> FindCalendarImpactAsync(
        D3ParkingDbContext dbContext,
        IncentivePolicy proposedPolicy,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var today = SiteTime.Today(now, timeZone);
        var reservations = (await dbContext.Reservations
                .Where(r => r.Status == ReservationStatus.Reserved && r.EndUtc > now)
                .ToListAsync(cancellationToken))
            .Where(r => WindowIsInvalid(r.StartUtc, r.EndUtc, proposedPolicy, now, timeZone))
            .ToList();
        var queueEntries = (await dbContext.QueueEntries
                .Where(q => (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
                    && q.EndUtc > now)
                .ToListAsync(cancellationToken))
            .Where(q => WindowIsInvalid(q.StartUtc, q.EndUtc, proposedPolicy, now, timeZone))
            .ToList();
        var handoffs = (await dbContext.ResidentSpotHandoffs
                .Where(h => (h.Status == ResidentSpotHandoffStatus.PendingResident
                             || h.Status == ResidentSpotHandoffStatus.Offered)
                    && h.EndUtc > now)
                .ToListAsync(cancellationToken))
            .Where(h => WindowIsInvalid(h.StartUtc, h.EndUtc, proposedPolicy, now, timeZone))
            .ToList();
        var visitorBookings = (await dbContext.VisitorBookings
                .Where(v => v.Status == VisitorBookingStatus.Booked && v.EndUtc > now)
                .ToListAsync(cancellationToken))
            .Where(v => CoveredDates(v.StartUtc, v.EndUtc, timeZone)
                .Any(date => proposedPolicy.GetReservationDateAvailability(date, today)
                    != ReservationDateAvailability.Allowed))
            .ToList();
        var spotReleases = (await dbContext.SpotReleases
                .Where(r => r.Date >= today)
                .ToListAsync(cancellationToken))
            .Where(r => proposedPolicy.GetReservationDateAvailability(r.Date, today)
                != ReservationDateAvailability.Allowed)
            .ToList();

        return new CalendarImpact(reservations, queueEntries, handoffs, visitorBookings, spotReleases);
    }

    private static bool WindowIsInvalid(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IncentivePolicy policy,
        DateTimeOffset now,
        TimeZoneInfo timeZone) =>
        !ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone)
        || policy.GetReservationDateAvailability(startUtc, now, timeZone)
            != ReservationDateAvailability.Allowed;

    private static IEnumerable<DateOnly> CoveredDates(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeZoneInfo timeZone)
    {
        var first = SiteTime.Today(startUtc, timeZone);
        var last = SiteTime.Today(endUtc.AddTicks(-1), timeZone);
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static async Task ReconcileCalendarImpactAsync(
        D3ParkingDbContext dbContext,
        CalendarImpact impact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var reservation in impact.Reservations)
        {
            reservation.Cancel(now);
            if (reservation.CreditsCharged > 0)
            {
                var score = await GetOrCreateScoreAsync(dbContext, reservation.UserId, cancellationToken);
                score.RefundCredits(reservation.CreditsCharged, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId,
                    IncentiveReason.ReservationRefund,
                    reservation.CreditsCharged,
                    reservation.Id,
                    now,
                    "calendar configuration change"));
            }

            await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);
        }

        foreach (var entry in impact.QueueEntries)
        {
            entry.Cancel();
        }

        foreach (var handoff in impact.Handoffs)
        {
            handoff.Cancel(now);
        }

        foreach (var booking in impact.VisitorBookings)
        {
            booking.Cancel();
        }

        dbContext.SpotReleases.RemoveRange(impact.SpotReleases);
    }

    private static async Task<ParkerScore> GetOrCreateScoreAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var score = await dbContext.ParkerScores.FindAsync([userId], cancellationToken);
        if (score is not null)
        {
            return score;
        }

        score = new ParkerScore(userId);
        dbContext.ParkerScores.Add(score);
        return score;
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

    private sealed record CalendarImpact(
        IReadOnlyList<Reservation> Reservations,
        IReadOnlyList<QueueEntry> QueueEntries,
        IReadOnlyList<ResidentSpotHandoff> Handoffs,
        IReadOnlyList<VisitorBooking> VisitorBookings,
        IReadOnlyList<SpotRelease> SpotReleases)
    {
        public ParkingCalendarChangeImpactDto ToDto() => new(
            Reservations.Count,
            QueueEntries.Count,
            Handoffs.Count,
            VisitorBookings.Count,
            SpotReleases.Count);
    }

    private static async Task<ParkingSettings> GetOrCreateAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ParkingSettings
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = ParkingSettings.CreateDefault();
        dbContext.ParkingSettings.Add(settings);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(settings).State = EntityState.Detached;
            return await dbContext.ParkingSettings.FirstAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);
        }
    }
}
