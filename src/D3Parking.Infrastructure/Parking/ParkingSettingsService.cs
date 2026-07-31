using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Parking;
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
            s.PeakStart, s.PeakEnd, s.SweepInterval,
            s.ResidentHoldUntil, s.ResidentReleasePointsPerHour, s.ResidentReleaseMaxPoints,
            s.ResidentMaxShareAllowance, s.ResidentSharePercentPerAllowance, s.ResidentWastedShareClawbackPercent,
            s.ResidentPlanHorizonDays,
            s.LotLatitude, s.LotLongitude, s.SharedTakenBasePoints, s.SharedTakenReferenceKm, s.SharedTakenMaxMultiplier,
            s.AutoVerifyHomeAddress, s.AutoVerifyMaxDistanceKm, s.MaxRewardedReleasesPerDay, s.MaxReleaseRangeDays,
            s.BaseReservationCost, s.PeakPricePercent, s.OccupancyPricePercent, s.MaxReservationCost, s.MonthlyCreditAllowance,
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
            s.AvailabilityMinConsecutiveDays, s.AvailabilitySendHourLocal,
            s.OversightSlaCriticalHours, s.OversightSlaHighHours, s.OversightSlaNormalHours, s.OversightSlaLowHours,
            s.OversightRecurrenceWindowDays, s.OversightRecurrenceThreshold, s.OversightDigestHourLocal);
    }

    public async Task<ParkingResult> UpdateAsync(ParkingSettingsDto dto, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        if (dto.PeakEnd <= dto.PeakStart)
        {
            return ParkingResult.Failure("Parking_Settings_Error_PeakRange");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.Update(
            dto.ReleasePoints, dto.OffPeakBonusPoints, dto.NoShowPenaltyPoints,
            dto.ReleaseCutoff, dto.NoShowGracePeriod, dto.ReminderLeadTime,
            dto.PeakStart, dto.PeakEnd, dto.SweepInterval,
            dto.ResidentHoldUntil, dto.ResidentReleasePointsPerHour, dto.ResidentReleaseMaxPoints,
            dto.ResidentMaxShareAllowance, dto.ResidentSharePercentPerAllowance, dto.ResidentWastedShareClawbackPercent,
            dto.ResidentPlanHorizonDays,
            dto.LotLatitude, dto.LotLongitude, dto.SharedTakenBasePoints, dto.SharedTakenReferenceKm, dto.SharedTakenMaxMultiplier,
            dto.AutoVerifyHomeAddress, dto.AutoVerifyMaxDistanceKm, dto.MaxRewardedReleasesPerDay, dto.MaxReleaseRangeDays,
            dto.BaseReservationCost, dto.PeakPricePercent, dto.OccupancyPricePercent, dto.MaxReservationCost, dto.MonthlyCreditAllowance,
            dto.QueueOfferMinutes, dto.QueueNoShowPenaltyPoints, dto.QueueNoShowCreditPenalty, dto.QueueNoShowBanDays, dto.QueueNoShowAllowancePenalty,
            dto.DemandReleaseOccupancyPercent, dto.DemandReleaseQueueBonus, dto.MaxReleaseReward,
            dto.StreakBonusPerLevel, dto.StreakBonusCap, dto.TierSilverPoints, dto.TierGoldPoints, dto.TierPlatinumPoints,
            dto.QueuePriorityPerTier, dto.TierAllowanceBonus, dto.TierDiscountPercent,
            dto.ReputationDecayPercent, dto.ReputationDecayIntervalDays,
            dto.AdaptivePricingEnabled, dto.AdaptiveTargetOccupancyPercent, dto.AdaptiveGainPercent, dto.AdaptiveDeadbandPercent,
            dto.AdaptiveStepMaxPercent, dto.AdaptivePeakMinPercent, dto.AdaptivePeakMaxPercent, dto.AdaptiveIntervalMinutes,
            dto.TrustEnabled, dto.TrustIntervalHours, dto.TrustedBadgeThreshold,
            dto.MaxPairTrustWeight, dto.AntiCollusionEnabled, dto.CollusionMinInteractions,
            dto.CollusionConcentrationPercent, dto.CollusionScanIntervalHours,
            dto.AvailabilityCampaignsEnabled, dto.AvailabilityLookaheadDays, dto.AvailabilityFreeThresholdPercent,
            dto.AvailabilityMinConsecutiveDays, dto.AvailabilitySendHourLocal,
            dto.OversightSlaCriticalHours, dto.OversightSlaHighHours, dto.OversightSlaNormalHours, dto.OversightSlaLowHours,
            dto.OversightRecurrenceWindowDays, dto.OversightRecurrenceThreshold, dto.OversightDigestHourLocal);

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            $"Parking: release={settings.ReleasePoints} offPeak={settings.OffPeakBonusPoints} noShow={settings.NoShowPenaltyPoints} " +
            $"peak={settings.PeakStart:HH\\:mm}-{settings.PeakEnd:HH\\:mm} sweep={settings.SweepInterval}",
            timeProvider.GetUtcNow()));

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(PolicyCacheKey);

        logger.LogInformation("Parking settings changed by {AdminId}.", actingUserId);
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
