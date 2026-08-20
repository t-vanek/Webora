using D3Parking.Domain.Common;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Domain.Parking;

/// <summary>
/// Persisted, admin-editable parking and incentive configuration. A single instance identified by
/// <see cref="SingletonId"/>. Defaults mirror <see cref="IncentivePolicy.Default"/>.
/// </summary>
public class ParkingSettings : Entity, IAggregateRoot
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000000b1");

    public const int MaxOrientationMapBytes = 12 * 1024 * 1024;

    public int ReleasePoints { get; private set; }

    public int OffPeakBonusPoints { get; private set; }

    public int NoShowPenaltyPoints { get; private set; }

    public TimeSpan ReleaseCutoff { get; private set; }

    public TimeSpan NoShowGracePeriod { get; private set; }

    public TimeSpan ReminderLeadTime { get; private set; }

    public ReservationTimeMode ReservationTimeMode { get; private set; } = ReservationTimeMode.TimeWindow;

    /// <summary>How many local calendar days ahead a new plan may start.</summary>
    public int ReservationHorizonDays { get; private set; } = 14;

    /// <summary>Local weekdays on which employees may start a new reservation.</summary>
    public Weekday AllowedReservationWeekdays { get; private set; } = Weekday.Everyday;

    /// <summary>Whether advance plans are limited per user and local calendar week.</summary>
    public bool WeeklyReservationLimitEnabled { get; private set; } = true;

    /// <summary>Maximum advance plans in one local Monday-Sunday week.</summary>
    public int WeeklyReservationLimit { get; private set; } = 2;

    /// <summary>Inside this window unused capacity opens without the weekly limit.</summary>
    public int LastMinuteUnlimitedHours { get; private set; } = 24;

    public TimeOnly PeakStart { get; private set; } = new(7, 30);

    public TimeOnly PeakEnd { get; private set; } = new(10, 0);

    public TimeSpan SweepInterval { get; private set; } = TimeSpan.FromMinutes(5);

    public TimeOnly ResidentHoldUntil { get; private set; } = new(8, 0);

    public int ResidentReleasePointsPerHour { get; private set; }

    public int ResidentReleaseMaxPoints { get; private set; }

    /// <summary>Legacy persisted setting; release planning no longer has a monthly quota.</summary>
    public int ResidentMaxShareAllowance { get; private set; }

    /// <summary>Legacy persisted setting; release rewards no longer scale with a quota.</summary>
    public int ResidentSharePercentPerAllowance { get; private set; }

    public int ResidentWastedShareClawbackPercent { get; private set; } = 25;

    public int ResidentPlanHorizonDays { get; private set; } = 21;

    public ResidentReclaimPolicy ResidentReclaimPolicy { get; private set; } = ResidentReclaimPolicy.AdvanceOrReplacement;

    public bool ManualReleasesAreBinding { get; private set; } = true;

    public ResidentProtectionDeadlineMode ResidentProtectionDeadlineMode { get; private set; } = ResidentProtectionDeadlineMode.PreviousDayAtTime;

    public int ResidentProtectionLeadHours { get; private set; } = 24;

    public TimeOnly ResidentProtectionPreviousDayTime { get; private set; } = new(18, 0);

    public ResidentNoReplacementAction ResidentNoReplacementAction { get; private set; } = ResidentNoReplacementAction.CancelAndQueue;

    public double? LotLatitude { get; private set; }

    public double? LotLongitude { get; private set; }

    public int SharedTakenBasePoints { get; private set; }

    public int SharedTakenReferenceKm { get; private set; } = 10;

    public int SharedTakenMaxMultiplier { get; private set; } = 3;

    public bool AutoVerifyHomeAddress { get; private set; }

    public int AutoVerifyMaxDistanceKm { get; private set; } = 50;

    public int MaxRewardedReleasesPerDay { get; private set; }

    public int MaxReleaseRangeDays { get; private set; } = 92;

    public int BaseReservationCost { get; private set; }

    public int PeakPricePercent { get; private set; } = 200;

    public int OccupancyPricePercent { get; private set; }

    public int MaxReservationCost { get; private set; }

    public int MonthlyCreditAllowance { get; private set; }

    public BudgetRenewalPeriod BudgetRenewalPeriod { get; private set; } = BudgetRenewalPeriod.Monthly;

    public int QueueOfferMinutes { get; private set; } = 30;

    public int QueueNoShowPenaltyPoints { get; private set; }

    public int QueueNoShowCreditPenalty { get; private set; }

    public int QueueNoShowBanDays { get; private set; }

    public int QueueNoShowAllowancePenalty { get; private set; }

    public int DemandReleaseOccupancyPercent { get; private set; }

    public int DemandReleaseQueueBonus { get; private set; }

    public int MaxReleaseReward { get; private set; }

    public int StreakBonusPerLevel { get; private set; }

    public int StreakBonusCap { get; private set; }

    public int TierSilverPoints { get; private set; } = 50;

    public int TierGoldPoints { get; private set; } = 150;

    public int TierPlatinumPoints { get; private set; } = 300;

    public int QueuePriorityPerTier { get; private set; }

    public int TierAllowanceBonus { get; private set; }

    public int TierDiscountPercent { get; private set; }

    public int ReputationDecayPercent { get; private set; }

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

    public bool TrustEnabled { get; private set; }

    public int TrustIntervalHours { get; private set; } = 24;

    public int TrustedBadgeThreshold { get; private set; } = 60;

    /// <summary>State: when the trust graph was last recomputed.</summary>
    public DateTimeOffset? LastTrustComputeUtc { get; private set; }

    // --- Anti-collusion (reciprocal sharing-ring detection) ---

    public int MaxPairTrustWeight { get; private set; } = 3;

    public bool AntiCollusionEnabled { get; private set; }

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
    public int AvailabilityFreeThresholdPercent { get; private set; } = 60;

    /// <summary>Minimum run of consecutive wide-open days before a campaign fires.</summary>
    public int AvailabilityMinConsecutiveDays { get; private set; } = 1;

    /// <summary>Local hour of day at which a due campaign is sent (weekdays only).</summary>
    public int AvailabilitySendHourLocal { get; private set; } = 9;

    // --- Operations oversight (how long a case may sit, and when repetition is a pattern) ---

    /// <summary>
    /// How many hours a case of each priority may stay open before it counts as overdue. Hours
    /// rather than working days on purpose: a blocked spot is a today problem, and a deadline that
    /// skips weekends would quietly grant two extra days to exactly the reports that hurt most.
    /// </summary>
    public int OversightSlaCriticalHours { get; private set; } = 4;

    public int OversightSlaHighHours { get; private set; } = 24;

    public int OversightSlaNormalHours { get; private set; } = 72;

    public int OversightSlaLowHours { get; private set; } = 168;

    /// <summary>How far back repeated reports on one spot still count as the same pattern.</summary>
    public int OversightRecurrenceWindowDays { get; private set; } = 30;

    /// <summary>
    /// Reports on one spot within the window that make it a pattern rather than an incident. At the
    /// threshold a fresh case opens high; at twice it, critical — a spot reported that often is not
    /// a run of bad luck, it is something about the spot.
    /// </summary>
    public int OversightRecurrenceThreshold { get; private set; } = 3;

    /// <summary>Local hour of day at which the daily oversight digest is sent.</summary>
    public int OversightDigestHourLocal { get; private set; } = 8;

    /// <summary>
    /// How long a case may wait on a driver's answer before it goes back on the reviewer's desk. A
    /// question nobody answers must not park a case out of sight forever; the wait ends, the case
    /// comes back, and a human decides on what there is.
    /// </summary>
    public int OversightInfoDeadlineDays { get; private set; } = 7;

    /// <summary>
    /// Whether anybody may report something wrong with the lot. On by default — a lot where the
    /// lighting fails silently is worse than one with a few duplicate reports — but a manager who
    /// handles this elsewhere can close the channel rather than leave it unread.
    /// </summary>
    public bool OversightAllowUserReports { get; private set; } = true;

    /// <summary>
    /// How long after a no-show a driver may still dispute it. Bounded on purpose: the evidence a
    /// reviewer would need (who was on the spot, what the barrier logged) thins out fast, and a
    /// penalty has to become final at some point for the standings to mean anything.
    /// </summary>
    public int OversightDisputeWindowDays { get; private set; } = 30;

    public byte[]? OrientationMap { get; private set; }

    public string? OrientationMapContentType { get; private set; }

    /// <summary>State: when the oversight digest last went out.</summary>
    public DateTimeOffset? LastOversightDigestUtc { get; private set; }

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
        ReservationTimeMode reservationTimeMode,
        int reservationHorizonDays,
        Weekday allowedReservationWeekdays,
        bool weeklyReservationLimitEnabled,
        int weeklyReservationLimit,
        int lastMinuteUnlimitedHours,
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
        BudgetRenewalPeriod budgetRenewalPeriod,
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
        int availabilitySendHourLocal,
        int oversightSlaCriticalHours,
        int oversightSlaHighHours,
        int oversightSlaNormalHours,
        int oversightSlaLowHours,
        int oversightRecurrenceWindowDays,
        int oversightRecurrenceThreshold,
        int oversightDigestHourLocal,
        int oversightInfoDeadlineDays,
        bool oversightAllowUserReports,
        int oversightDisputeWindowDays,
        ResidentReclaimPolicy residentReclaimPolicy,
        bool manualReleasesAreBinding,
        ResidentProtectionDeadlineMode residentProtectionDeadlineMode,
        int residentProtectionLeadHours,
        TimeOnly residentProtectionPreviousDayTime,
        ResidentNoReplacementAction residentNoReplacementAction)
    {
        ReleasePoints = Math.Max(0, releasePoints);
        OffPeakBonusPoints = Math.Max(0, offPeakBonusPoints);
        NoShowPenaltyPoints = Math.Max(0, noShowPenaltyPoints);
        ReleaseCutoff = Clamp(releaseCutoff);
        NoShowGracePeriod = Clamp(noShowGracePeriod);
        ReminderLeadTime = Clamp(reminderLeadTime);
        ReservationTimeMode = reservationTimeMode;
        ReservationHorizonDays = Math.Clamp(reservationHorizonDays, 1, 366);
        AllowedReservationWeekdays = allowedReservationWeekdays.Sanitize() is Weekday.None
            ? Weekday.Everyday
            : allowedReservationWeekdays.Sanitize();
        WeeklyReservationLimitEnabled = weeklyReservationLimitEnabled;
        WeeklyReservationLimit = Math.Clamp(weeklyReservationLimit, 1, 7);
        LastMinuteUnlimitedHours = Math.Clamp(lastMinuteUnlimitedHours, 0, 168);
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
        ResidentReclaimPolicy = residentReclaimPolicy;
        ManualReleasesAreBinding = manualReleasesAreBinding;
        ResidentProtectionDeadlineMode = residentProtectionDeadlineMode;
        ResidentProtectionLeadHours = Math.Clamp(residentProtectionLeadHours, 1, 168);
        ResidentProtectionPreviousDayTime = residentProtectionPreviousDayTime;
        ResidentNoReplacementAction = residentNoReplacementAction;
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
        BudgetRenewalPeriod = Enum.IsDefined(budgetRenewalPeriod)
            ? budgetRenewalPeriod
            : BudgetRenewalPeriod.Monthly;
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

        // Kept ordered, so a "critical" case can never be given longer than a low-priority one —
        // an inversion here would silently invert the queue.
        OversightSlaCriticalHours = Math.Max(1, oversightSlaCriticalHours);
        OversightSlaHighHours = Math.Max(OversightSlaCriticalHours, oversightSlaHighHours);
        OversightSlaNormalHours = Math.Max(OversightSlaHighHours, oversightSlaNormalHours);
        OversightSlaLowHours = Math.Max(OversightSlaNormalHours, oversightSlaLowHours);
        OversightRecurrenceWindowDays = Math.Clamp(oversightRecurrenceWindowDays, 1, 365);
        OversightRecurrenceThreshold = Math.Max(2, oversightRecurrenceThreshold);
        OversightDigestHourLocal = Math.Clamp(oversightDigestHourLocal, 0, 23);
        OversightInfoDeadlineDays = Math.Clamp(oversightInfoDeadlineDays, 1, 90);
        OversightAllowUserReports = oversightAllowUserReports;
        OversightDisputeWindowDays = Math.Clamp(oversightDisputeWindowDays, 1, 365);
    }

    /// <summary>How long a case of this priority may stay open before it is overdue.</summary>
    public TimeSpan SlaFor(OversightCasePriority priority) => TimeSpan.FromHours(priority switch
    {
        OversightCasePriority.Critical => OversightSlaCriticalHours,
        OversightCasePriority.High => OversightSlaHighHours,
        OversightCasePriority.Low => OversightSlaLowHours,
        _ => OversightSlaNormalHours,
    });

    /// <summary>Records when the oversight digest last went out.</summary>
    public void MarkOversightDigestSent(DateTimeOffset at) => LastOversightDigestUtc = at;

    /// <summary>Records when the trust graph was last recomputed.</summary>
    public void MarkTrustComputed(DateTimeOffset at) => LastTrustComputeUtc = at;

    /// <summary>Records when the collusion scan last ran.</summary>
    public void MarkCollusionScanned(DateTimeOffset at) => LastCollusionScanUtc = at;

    public void SetOrientationMap(byte[] content, string contentType)
    {
        if (content.Length == 0)
        {
            OrientationMap = null;
            OrientationMapContentType = null;
            return;
        }

        if (content.Length > MaxOrientationMapBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content), "The orientation map is too large.");
        }

        OrientationMap = content;
        OrientationMapContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
    }

    public void ClearOrientationMap()
    {
        OrientationMap = null;
        OrientationMapContentType = null;
    }

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
        ReservationTimeMode = ReservationTimeMode,
        ReservationHorizonDays = ReservationHorizonDays,
        AllowedReservationWeekdays = AllowedReservationWeekdays,
        WeeklyReservationLimitEnabled = WeeklyReservationLimitEnabled,
        WeeklyReservationLimit = WeeklyReservationLimit,
        LastMinuteUnlimitedHours = LastMinuteUnlimitedHours,
        PeakStart = PeakStart,
        PeakEnd = PeakEnd,
        ResidentHoldUntil = ResidentHoldUntil,
        ResidentReleasePointsPerHour = ResidentReleasePointsPerHour,
        ResidentReleaseMaxPoints = ResidentReleaseMaxPoints,
        ResidentMaxShareAllowance = ResidentMaxShareAllowance,
        ResidentSharePercentPerAllowance = ResidentSharePercentPerAllowance,
        ResidentWastedShareClawbackPercent = ResidentWastedShareClawbackPercent,
        ResidentPlanHorizonDays = ResidentPlanHorizonDays,
        ResidentReclaimPolicy = ResidentReclaimPolicy,
        ManualReleasesAreBinding = ManualReleasesAreBinding,
        ResidentProtectionDeadlineMode = ResidentProtectionDeadlineMode,
        ResidentProtectionLeadHours = ResidentProtectionLeadHours,
        ResidentProtectionPreviousDayTime = ResidentProtectionPreviousDayTime,
        ResidentNoReplacementAction = ResidentNoReplacementAction,
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
        BudgetRenewalPeriod = BudgetRenewalPeriod,
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
