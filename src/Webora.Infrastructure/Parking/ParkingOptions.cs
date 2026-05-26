using Webora.Domain.Parking.Incentives;

namespace Webora.Infrastructure.Parking;

/// <summary>
/// Configurable parking and incentive settings, bound from the "Parking" configuration section so
/// the rules can be tuned without recompiling. Defaults mirror <see cref="IncentivePolicy.Default"/>.
/// </summary>
public sealed class ParkingOptions
{
    public const string SectionName = "Parking";

    /// <summary>Points awarded for releasing a reservation early enough to free the spot.</summary>
    public int ReleasePoints { get; set; } = 10;

    /// <summary>Points awarded for booking outside the peak window.</summary>
    public int OffPeakBonusPoints { get; set; } = 5;

    /// <summary>Points deducted for a no-show (stored positive, applied as a deduction).</summary>
    public int NoShowPenaltyPoints { get; set; } = 20;

    /// <summary>How far ahead of the start a release must happen to earn the reward.</summary>
    public TimeSpan ReleaseCutoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Grace period after the start before an un-used reservation becomes a no-show.</summary>
    public TimeSpan NoShowGracePeriod { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long before the start to remind the holder to confirm arrival or release.</summary>
    public TimeSpan ReminderLeadTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Start of the daily high-demand window (local time).</summary>
    public TimeOnly PeakStart { get; set; } = new(7, 30);

    /// <summary>End of the daily high-demand window (local time).</summary>
    public TimeOnly PeakEnd { get; set; } = new(10, 0);

    /// <summary>How often the background maintenance runs (reminders and no-show resolution).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

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
}
