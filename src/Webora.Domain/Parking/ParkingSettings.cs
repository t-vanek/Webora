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
        TimeSpan sweepInterval)
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
    };

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
