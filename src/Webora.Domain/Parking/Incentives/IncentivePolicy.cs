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

    /// <summary>How long before the start to remind the holder to confirm arrival or release.</summary>
    public TimeSpan ReminderLeadTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Start of the daily high-demand window (local time of the reservation).</summary>
    public TimeOnly PeakStart { get; init; } = new(7, 30);

    /// <summary>End of the daily high-demand window (local time of the reservation).</summary>
    public TimeOnly PeakEnd { get; init; } = new(10, 0);

    /// <summary>Daily time until which a reserved spot is held for its resident before auto-sharing.</summary>
    public TimeOnly ResidentHoldUntil { get; init; } = new(8, 0);

    /// <summary>Points per hour of advance notice when a resident proactively releases their spot.</summary>
    public int ResidentReleasePointsPerHour { get; init; } = 2;

    /// <summary>Cap on the advance-notice part of a resident's release reward.</summary>
    public int ResidentReleaseMaxPoints { get; init; } = 40;

    /// <summary>Largest monthly share allowance a resident may set on their spot.</summary>
    public int ResidentMaxShareAllowance { get; init; } = 30;

    /// <summary>Extra percent added to the release reward multiplier per allowed monthly share.</summary>
    public int ResidentSharePercentPerAllowance { get; init; } = 5;

    /// <summary>Percent of a share's reward the resident gives back when the guest no-shows on it.</summary>
    public int ResidentWastedShareClawbackPercent { get; init; } = 25;

    /// <summary>Base points for taking a shared reserved spot, before the distance multiplier.</summary>
    public int SharedTakenBasePoints { get; init; } = 5;

    /// <summary>Commute distance (km) at which the distance multiplier reaches 1.0.</summary>
    public int SharedTakenReferenceKm { get; init; } = 10;

    /// <summary>Cap on the distance multiplier so very far commuters don't earn unbounded points.</summary>
    public int SharedTakenMaxMultiplier { get; init; } = 3;

    public static IncentivePolicy Default { get; } = new();

    /// <summary>
    /// Points for taking a shared spot, scaled by the taker's commute distance: the farther they
    /// commute, the more they earn (capped). Unknown or zero distance earns nothing.
    /// </summary>
    public int ComputeSharedTakenReward(double? distanceKm)
    {
        if (distanceKm is not { } km || km <= 0)
        {
            return 0;
        }

        var reference = Math.Max(1, SharedTakenReferenceKm);
        var multiplier = Math.Min(km / reference, SharedTakenMaxMultiplier);
        return (int)Math.Round(SharedTakenBasePoints * multiplier, MidpointRounding.AwayFromZero);
    }

    /// <summary>How many points to claw back from a resident when a share they were rewarded for is wasted.</summary>
    public int ComputeShareClawback(int awardedPoints) =>
        (int)Math.Round(awardedPoints * Math.Clamp(ResidentWastedShareClawbackPercent, 0, 100) / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Points for a proactive resident release: an advance-notice bonus (earlier = more, capped)
    /// scaled by a multiplier that grows with the resident's monthly share allowance.
    /// </summary>
    public int ComputeShareReward(DateTimeOffset shareCutoff, DateTimeOffset releasedAt, int monthlyAllowance)
    {
        var hoursEarly = Math.Max(0d, (shareCutoff - releasedAt).TotalHours);
        var earlyBonus = Math.Min(ResidentReleaseMaxPoints, (int)Math.Ceiling(hoursEarly) * ResidentReleasePointsPerHour);
        var allowance = Math.Clamp(monthlyAllowance, 0, ResidentMaxShareAllowance);
        var multiplier = 1.0 + allowance * ResidentSharePercentPerAllowance / 100.0;
        return (int)Math.Round(earlyBonus * multiplier, MidpointRounding.AwayFromZero);
    }

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

    /// <summary>The instant on a given day after which an unclaimed reserved spot auto-shares.</summary>
    public DateTimeOffset ResidentShareCutoff(DateOnly date, TimeSpan offset) =>
        new DateTimeOffset(date.ToDateTime(ResidentHoldUntil), offset) + NoShowGracePeriod;

    /// <summary>Whether a reserved spot for the requested day has auto-shared: today and past cutoff.</summary>
    public bool IsResidentAutoShareActive(DateOnly requestDate, DateTimeOffset now) =>
        requestDate == DateOnly.FromDateTime(now.Date) && now >= ResidentShareCutoff(requestDate, now.Offset);
}
