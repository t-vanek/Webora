using D3Parking.Domain.Common;
using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Domain.Parking;

/// <summary>
/// Persisted, admin-editable parking and incentive configuration. A single instance identified by
/// <see cref="SingletonId"/>. Defaults mirror <see cref="IncentivePolicy.Default"/>.
/// </summary>
public class ParkingSettings : Entity, IAggregateRoot
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000000b1");

    public int ReleasePoints { get; private set; } = 10;

    public int OffPeakBonusPoints { get; private set; } = 5;

    public int NoShowPenaltyPoints { get; private set; } = 20;

    public TimeSpan ReleaseCutoff { get; private set; } = TimeSpan.FromHours(1);

    public TimeSpan NoShowGracePeriod { get; private set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ReminderLeadTime { get; private set; } = TimeSpan.FromMinutes(5);

    public TimeOnly PeakStart { get; private set; } = new(7, 30);

    public TimeOnly PeakEnd { get; private set; } = new(10, 0);

    public TimeSpan SweepInterval { get; private set; } = TimeSpan.FromMinutes(5);

    public TimeOnly ResidentHoldUntil { get; private set; } = new(8, 0);

    public int ResidentReleasePointsPerHour { get; private set; } = 2;

    public int ResidentReleaseMaxPoints { get; private set; } = 40;

    public int ResidentMaxShareAllowance { get; private set; } = 30;

    public int ResidentSharePercentPerAllowance { get; private set; } = 5;

    public int ResidentWastedShareClawbackPercent { get; private set; } = 25;

    public int ResidentPlanHorizonDays { get; private set; } = 14;

    public double? LotLatitude { get; private set; }

    public double? LotLongitude { get; private set; }

    public int SharedTakenBasePoints { get; private set; } = 5;

    public int SharedTakenReferenceKm { get; private set; } = 10;

    public int SharedTakenMaxMultiplier { get; private set; } = 3;

    public bool AutoVerifyHomeAddress { get; private set; }

    public int AutoVerifyMaxDistanceKm { get; private set; } = 50;

    public int MaxRewardedReleasesPerDay { get; private set; } = 2;

    public int MaxReleaseRangeDays { get; private set; } = 92;

    public int BaseReservationCost { get; private set; } = 10;

    public int PeakPricePercent { get; private set; } = 200;

    public int OccupancyPricePercent { get; private set; } = 100;

    public int MaxReservationCost { get; private set; } = 40;

    public int MonthlyCreditAllowance { get; private set; } = 100;

    public int QueueOfferMinutes { get; private set; } = 15;

    public int QueueNoShowPenaltyPoints { get; private set; } = 50;

    public int QueueNoShowCreditPenalty { get; private set; } = 30;

    public int QueueNoShowBanDays { get; private set; } = 14;

    public int QueueNoShowAllowancePenalty { get; private set; } = 30;

    public int DemandReleaseOccupancyPercent { get; private set; } = 100;

    public int DemandReleaseQueueBonus { get; private set; } = 5;

    public int MaxReleaseReward { get; private set; } = 40;

    public int StreakBonusPerLevel { get; private set; } = 2;

    public int StreakBonusCap { get; private set; } = 20;

    public int TierSilverPoints { get; private set; } = 50;

    public int TierGoldPoints { get; private set; } = 150;

    public int TierPlatinumPoints { get; private set; } = 300;

    public int QueuePriorityPerTier { get; private set; } = 30;

    public int TierAllowanceBonus { get; private set; } = 20;

    public int TierDiscountPercent { get; private set; } = 5;

    public int ReputationDecayPercent { get; private set; } = 10;

    public int ReputationDecayIntervalDays { get; private set; } = 30;

    // --- Adaptive pricing controller (self-tunes the peak surcharge toward a target occupancy) ---

    public bool AdaptivePricingEnabled { get; private set; }

    public int AdaptiveTargetOccupancyPercent { get; private set; } = 85;

    public int AdaptiveGainPercent { get; private set; } = 100;

    public int AdaptiveDeadbandPercent { get; private set; } = 5;

    public int AdaptiveStepMaxPercent { get; private set; } = 25;

    public int AdaptivePeakMinPercent { get; private set; } = 100;

    public int AdaptivePeakMaxPercent { get; private set; } = 400;

    public int AdaptiveIntervalMinutes { get; private set; } = 60;

    /// <summary>Controller state: when the peak surcharge was last auto-adjusted.</summary>
    public DateTimeOffset? LastAdaptiveAdjustUtc { get; private set; }

    // --- Trust graph (EigenTrust/PageRank over sharing interactions) ---

    public bool TrustEnabled { get; private set; } = true;

    public int TrustIntervalHours { get; private set; } = 24;

    public int TrustedBadgeThreshold { get; private set; } = 60;

    /// <summary>State: when the trust graph was last recomputed.</summary>
    public DateTimeOffset? LastTrustComputeUtc { get; private set; }

    // --- Anti-collusion (reciprocal sharing-ring detection) ---

    public int MaxPairTrustWeight { get; private set; } = 3;

    public bool AntiCollusionEnabled { get; private set; } = true;

    public int CollusionMinInteractions { get; private set; } = 4;

    public int CollusionConcentrationPercent { get; private set; } = 70;

    public int CollusionScanIntervalHours { get; private set; } = 24;

    /// <summary>State: when the collusion scan last ran.</summary>
    public DateTimeOffset? LastCollusionScanUtc { get; private set; }

    // --- Availability campaigns (proactive "the lot is wide open" tips, bell + push only) ---

    public bool AvailabilityCampaignsEnabled { get; private set; } = true;

    /// <summary>How many days ahead the projected occupancy is scanned.</summary>
    public int AvailabilityLookaheadDays { get; private set; } = 14;

    /// <summary>A day counts as "wide open" below this projected occupancy.</summary>
    public int AvailabilityFreeThresholdPercent { get; private set; } = 25;

    /// <summary>Minimum run of consecutive wide-open days before a campaign fires.</summary>
    public int AvailabilityMinConsecutiveDays { get; private set; } = 3;

    /// <summary>Local hour of day at which a due campaign is sent (weekdays only).</summary>
    public int AvailabilitySendHourLocal { get; private set; } = 9;

    private ParkingSettings() { }

    public static ParkingSettings CreateDefault()
    {
        var settings = new ParkingSettings();
        settings.Id = SingletonId;
        return settings;
    }

    public void Update(
        int releasePoints,
        int offPeakBonusPoints,
        int noShowPenaltyPoints,
        TimeSpan releaseCutoff,
        TimeSpan noShowGracePeriod,
        TimeSpan reminderLeadTime,
        TimeOnly peakStart,
        TimeOnly peakEnd,
        TimeSpan sweepInterval,
        TimeOnly residentHoldUntil,
        int residentReleasePointsPerHour,
        int residentReleaseMaxPoints,
        int residentMaxShareAllowance,
        int residentSharePercentPerAllowance,
        int residentWastedShareClawbackPercent,
        int residentPlanHorizonDays,
        double? lotLatitude,
        double? lotLongitude,
        int sharedTakenBasePoints,
        int sharedTakenReferenceKm,
        int sharedTakenMaxMultiplier,
        bool autoVerifyHomeAddress,
        int autoVerifyMaxDistanceKm,
        int maxRewardedReleasesPerDay,
        int maxReleaseRangeDays,
        int baseReservationCost,
        int peakPricePercent,
        int occupancyPricePercent,
        int maxReservationCost,
        int monthlyCreditAllowance,
        int queueOfferMinutes,
        int queueNoShowPenaltyPoints,
        int queueNoShowCreditPenalty,
        int queueNoShowBanDays,
        int queueNoShowAllowancePenalty,
        int demandReleaseOccupancyPercent,
        int demandReleaseQueueBonus,
        int maxReleaseReward,
        int streakBonusPerLevel,
        int streakBonusCap,
        int tierSilverPoints,
        int tierGoldPoints,
        int tierPlatinumPoints,
        int queuePriorityPerTier,
        int tierAllowanceBonus,
        int tierDiscountPercent,
        int reputationDecayPercent,
        int reputationDecayIntervalDays,
        bool adaptivePricingEnabled,
        int adaptiveTargetOccupancyPercent,
        int adaptiveGainPercent,
        int adaptiveDeadbandPercent,
        int adaptiveStepMaxPercent,
        int adaptivePeakMinPercent,
        int adaptivePeakMaxPercent,
        int adaptiveIntervalMinutes,
        bool trustEnabled,
        int trustIntervalHours,
        int trustedBadgeThreshold,
        int maxPairTrustWeight,
        bool antiCollusionEnabled,
        int collusionMinInteractions,
        int collusionConcentrationPercent,
        int collusionScanIntervalHours,
        bool availabilityCampaignsEnabled,
        int availabilityLookaheadDays,
        int availabilityFreeThresholdPercent,
        int availabilityMinConsecutiveDays,
        int availabilitySendHourLocal)
    {
        ReleasePoints = Math.Max(0, releasePoints);
        OffPeakBonusPoints = Math.Max(0, offPeakBonusPoints);
        NoShowPenaltyPoints = Math.Max(0, noShowPenaltyPoints);
        ReleaseCutoff = Clamp(releaseCutoff);
        NoShowGracePeriod = Clamp(noShowGracePeriod);
        ReminderLeadTime = Clamp(reminderLeadTime);
        PeakStart = peakStart;
        PeakEnd = peakEnd;
        // A floor keeps the maintenance loop from busy-spinning on a misconfigured tiny interval.
        SweepInterval = sweepInterval < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : sweepInterval;
        ResidentHoldUntil = residentHoldUntil;
        ResidentReleasePointsPerHour = Math.Max(0, residentReleasePointsPerHour);
        ResidentReleaseMaxPoints = Math.Max(0, residentReleaseMaxPoints);
        ResidentMaxShareAllowance = Math.Max(0, residentMaxShareAllowance);
        ResidentSharePercentPerAllowance = Math.Max(0, residentSharePercentPerAllowance);
        ResidentWastedShareClawbackPercent = Math.Clamp(residentWastedShareClawbackPercent, 0, 100);
        ResidentPlanHorizonDays = Math.Clamp(residentPlanHorizonDays, 1, 366);
        LotLatitude = lotLatitude;
        LotLongitude = lotLongitude;
        SharedTakenBasePoints = Math.Max(0, sharedTakenBasePoints);
        SharedTakenReferenceKm = Math.Max(1, sharedTakenReferenceKm);
        SharedTakenMaxMultiplier = Math.Max(1, sharedTakenMaxMultiplier);
        AutoVerifyHomeAddress = autoVerifyHomeAddress;
        AutoVerifyMaxDistanceKm = Math.Max(0, autoVerifyMaxDistanceKm);
        MaxRewardedReleasesPerDay = Math.Max(0, maxRewardedReleasesPerDay);
        MaxReleaseRangeDays = Math.Clamp(maxReleaseRangeDays, 1, 366);
        BaseReservationCost = Math.Max(0, baseReservationCost);
        PeakPricePercent = Math.Max(100, peakPricePercent);
        OccupancyPricePercent = Math.Max(0, occupancyPricePercent);
        MaxReservationCost = Math.Max(BaseReservationCost, maxReservationCost);
        MonthlyCreditAllowance = Math.Max(0, monthlyCreditAllowance);
        QueueOfferMinutes = Math.Max(1, queueOfferMinutes);
        QueueNoShowPenaltyPoints = Math.Max(0, queueNoShowPenaltyPoints);
        QueueNoShowCreditPenalty = Math.Max(0, queueNoShowCreditPenalty);
        QueueNoShowBanDays = Math.Max(0, queueNoShowBanDays);
        QueueNoShowAllowancePenalty = Math.Max(0, queueNoShowAllowancePenalty);
        DemandReleaseOccupancyPercent = Math.Max(0, demandReleaseOccupancyPercent);
        DemandReleaseQueueBonus = Math.Max(0, demandReleaseQueueBonus);
        MaxReleaseReward = Math.Max(ReleasePoints, maxReleaseReward);
        StreakBonusPerLevel = Math.Max(0, streakBonusPerLevel);
        StreakBonusCap = Math.Max(0, streakBonusCap);
        TierSilverPoints = Math.Max(0, tierSilverPoints);
        TierGoldPoints = Math.Max(TierSilverPoints, tierGoldPoints);
        TierPlatinumPoints = Math.Max(TierGoldPoints, tierPlatinumPoints);
        QueuePriorityPerTier = Math.Max(0, queuePriorityPerTier);
        TierAllowanceBonus = Math.Max(0, tierAllowanceBonus);
        TierDiscountPercent = Math.Clamp(tierDiscountPercent, 0, 30);
        ReputationDecayPercent = Math.Clamp(reputationDecayPercent, 0, 100);
        ReputationDecayIntervalDays = Math.Max(1, reputationDecayIntervalDays);
        AdaptivePricingEnabled = adaptivePricingEnabled;
        AdaptiveTargetOccupancyPercent = Math.Clamp(adaptiveTargetOccupancyPercent, 1, 100);
        AdaptiveGainPercent = Math.Max(0, adaptiveGainPercent);
        AdaptiveDeadbandPercent = Math.Clamp(adaptiveDeadbandPercent, 0, 100);
        AdaptiveStepMaxPercent = Math.Max(0, adaptiveStepMaxPercent);
        AdaptivePeakMinPercent = Math.Max(100, adaptivePeakMinPercent);
        AdaptivePeakMaxPercent = Math.Max(AdaptivePeakMinPercent, adaptivePeakMaxPercent);
        AdaptiveIntervalMinutes = Math.Max(1, adaptiveIntervalMinutes);
        TrustEnabled = trustEnabled;
        TrustIntervalHours = Math.Max(1, trustIntervalHours);
        TrustedBadgeThreshold = Math.Clamp(trustedBadgeThreshold, 0, 100);
        MaxPairTrustWeight = Math.Max(1, maxPairTrustWeight);
        AntiCollusionEnabled = antiCollusionEnabled;
        CollusionMinInteractions = Math.Max(2, collusionMinInteractions);
        CollusionConcentrationPercent = Math.Clamp(collusionConcentrationPercent, 1, 100);
        CollusionScanIntervalHours = Math.Max(1, collusionScanIntervalHours);
        AvailabilityCampaignsEnabled = availabilityCampaignsEnabled;
        AvailabilityLookaheadDays = Math.Clamp(availabilityLookaheadDays, 1, 60);
        AvailabilityFreeThresholdPercent = Math.Clamp(availabilityFreeThresholdPercent, 1, 100);
        AvailabilityMinConsecutiveDays = Math.Clamp(availabilityMinConsecutiveDays, 1, 30);
        AvailabilitySendHourLocal = Math.Clamp(availabilitySendHourLocal, 0, 23);
    }

    /// <summary>Records when the trust graph was last recomputed.</summary>
    public void MarkTrustComputed(DateTimeOffset at) => LastTrustComputeUtc = at;

    /// <summary>Records when the collusion scan last ran.</summary>
    public void MarkCollusionScanned(DateTimeOffset at) => LastCollusionScanUtc = at;

    /// <summary>Applies an adaptive-controller adjustment to the peak surcharge and records the time.</summary>
    public void ApplyAdaptiveAdjustment(int newPeakPricePercent, DateTimeOffset at)
    {
        PeakPricePercent = Math.Max(100, newPeakPricePercent);
        LastAdaptiveAdjustUtc = at;
    }

    public IncentivePolicy ToPolicy() => new()
    {
        ReleasePoints = ReleasePoints,
        OffPeakBonusPoints = OffPeakBonusPoints,
        NoShowPenaltyPoints = NoShowPenaltyPoints,
        ReleaseCutoff = ReleaseCutoff,
        NoShowGracePeriod = NoShowGracePeriod,
        ReminderLeadTime = ReminderLeadTime,
        PeakStart = PeakStart,
        PeakEnd = PeakEnd,
        ResidentHoldUntil = ResidentHoldUntil,
        ResidentReleasePointsPerHour = ResidentReleasePointsPerHour,
        ResidentReleaseMaxPoints = ResidentReleaseMaxPoints,
        ResidentMaxShareAllowance = ResidentMaxShareAllowance,
        ResidentSharePercentPerAllowance = ResidentSharePercentPerAllowance,
        ResidentWastedShareClawbackPercent = ResidentWastedShareClawbackPercent,
        ResidentPlanHorizonDays = ResidentPlanHorizonDays,
        SharedTakenBasePoints = SharedTakenBasePoints,
        SharedTakenReferenceKm = SharedTakenReferenceKm,
        SharedTakenMaxMultiplier = SharedTakenMaxMultiplier,
        AutoVerifyHomeAddress = AutoVerifyHomeAddress,
        AutoVerifyMaxDistanceKm = AutoVerifyMaxDistanceKm,
        MaxRewardedReleasesPerDay = MaxRewardedReleasesPerDay,
        MaxReleaseRangeDays = MaxReleaseRangeDays,
        BaseReservationCost = BaseReservationCost,
        PeakPricePercent = PeakPricePercent,
        OccupancyPricePercent = OccupancyPricePercent,
        MaxReservationCost = MaxReservationCost,
        MonthlyCreditAllowance = MonthlyCreditAllowance,
        QueueOfferMinutes = QueueOfferMinutes,
        QueueNoShowPenaltyPoints = QueueNoShowPenaltyPoints,
        QueueNoShowCreditPenalty = QueueNoShowCreditPenalty,
        QueueNoShowBanDays = QueueNoShowBanDays,
        QueueNoShowAllowancePenalty = QueueNoShowAllowancePenalty,
        DemandReleaseOccupancyPercent = DemandReleaseOccupancyPercent,
        DemandReleaseQueueBonus = DemandReleaseQueueBonus,
        MaxReleaseReward = MaxReleaseReward,
        StreakBonusPerLevel = StreakBonusPerLevel,
        StreakBonusCap = StreakBonusCap,
        TierSilverPoints = TierSilverPoints,
        TierGoldPoints = TierGoldPoints,
        TierPlatinumPoints = TierPlatinumPoints,
        QueuePriorityPerTier = QueuePriorityPerTier,
        TierAllowanceBonus = TierAllowanceBonus,
        TierDiscountPercent = TierDiscountPercent,
        ReputationDecayPercent = ReputationDecayPercent,
        ReputationDecayIntervalDays = ReputationDecayIntervalDays,
        AdaptivePricingEnabled = AdaptivePricingEnabled,
        AdaptiveTargetOccupancyPercent = AdaptiveTargetOccupancyPercent,
        AdaptiveGainPercent = AdaptiveGainPercent,
        AdaptiveDeadbandPercent = AdaptiveDeadbandPercent,
        AdaptiveStepMaxPercent = AdaptiveStepMaxPercent,
        AdaptivePeakMinPercent = AdaptivePeakMinPercent,
        AdaptivePeakMaxPercent = AdaptivePeakMaxPercent,
        AdaptiveIntervalMinutes = AdaptiveIntervalMinutes,
        TrustEnabled = TrustEnabled,
        TrustIntervalHours = TrustIntervalHours,
        TrustedBadgeThreshold = TrustedBadgeThreshold,
        MaxPairTrustWeight = MaxPairTrustWeight,
        AntiCollusionEnabled = AntiCollusionEnabled,
        CollusionMinInteractions = CollusionMinInteractions,
        CollusionConcentrationPercent = CollusionConcentrationPercent,
        CollusionScanIntervalHours = CollusionScanIntervalHours,
        AvailabilityCampaignsEnabled = AvailabilityCampaignsEnabled,
        AvailabilityLookaheadDays = AvailabilityLookaheadDays,
        AvailabilityFreeThresholdPercent = AvailabilityFreeThresholdPercent,
        AvailabilityMinConsecutiveDays = AvailabilityMinConsecutiveDays,
        AvailabilitySendHourLocal = AvailabilitySendHourLocal,
    };

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
