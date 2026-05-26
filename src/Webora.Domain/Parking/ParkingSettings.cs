using Webora.Domain.Common;
using Webora.Domain.Parking.Incentives;

namespace Webora.Domain.Parking;

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
        int queueNoShowAllowancePenalty)
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
    };

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
