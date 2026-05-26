namespace Webora.Domain.Parking.Incentives;

/// <summary>
/// The tunable rules that translate parking behaviour into points and decide what counts as
/// off-peak, an in-time release, or a no-show. Captured as an immutable value so it can later be
/// surfaced as a site setting; <see cref="Default"/> supplies sensible starting values.
/// </summary>
public sealed record IncentivePolicy
{
    /// <summary>Points awarded for releasing a reservation early enough to free the spot.</summary>
    public int ReleasePoints { get; init; } = 10;

    /// <summary>Points awarded for booking outside the peak window.</summary>
    public int OffPeakBonusPoints { get; init; } = 5;

    /// <summary>Points deducted for a no-show (stored positive, applied as a deduction).</summary>
    public int NoShowPenaltyPoints { get; init; } = 20;

    /// <summary>How far ahead of the start a release must happen to earn the reward.</summary>
    public TimeSpan ReleaseCutoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Grace period after the start before an un-used reservation becomes a no-show.</summary>
    public TimeSpan NoShowGracePeriod { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Start of the daily high-demand window (local time of the reservation).</summary>
    public TimeOnly PeakStart { get; init; } = new(7, 30);

    /// <summary>End of the daily high-demand window (local time of the reservation).</summary>
    public TimeOnly PeakEnd { get; init; } = new(10, 0);

    public static IncentivePolicy Default { get; } = new();

    /// <summary>A reservation is off-peak when its arrival falls outside the peak window.</summary>
    public bool IsOffPeak(DateTimeOffset start)
    {
        var arrival = TimeOnly.FromTimeSpan(start.TimeOfDay);
        return arrival < PeakStart || arrival >= PeakEnd;
    }

    /// <summary>Whether a release at the given moment is early enough to be rewarded.</summary>
    public bool QualifiesForReleaseReward(DateTimeOffset start, DateTimeOffset releasedAt) =>
        releasedAt <= start - ReleaseCutoff;

    /// <summary>Whether an un-used reservation has passed its grace period and is now a no-show.</summary>
    public bool IsNoShow(DateTimeOffset start, DateTimeOffset now) =>
        now >= start + NoShowGracePeriod;
}
