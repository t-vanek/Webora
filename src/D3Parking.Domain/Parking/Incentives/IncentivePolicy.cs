using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking.Incentives;

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

    /// <summary>Auto-verify a home address (skip manual admin approval) when within the distance cap.</summary>
    public bool AutoVerifyHomeAddress { get; init; }

    /// <summary>Largest commute distance (km) eligible for auto-verification; farther needs admin review.</summary>
    public int AutoVerifyMaxDistanceKm { get; init; } = 50;

    /// <summary>Most releases a user can be rewarded for in a day; bounds reserve/release farming.</summary>
    public int MaxRewardedReleasesPerDay { get; init; } = 2;

    /// <summary>Largest day range a resident may release in one action.</summary>
    public int MaxReleaseRangeDays { get; init; } = 92;

    /// <summary>Base credit cost to book a spot off-peak in an empty lot, before peak/occupancy surcharges.</summary>
    public int BaseReservationCost { get; init; } = 10;

    /// <summary>Percent of the base cost charged during the peak window (200 = double the off-peak price).</summary>
    public int PeakPricePercent { get; init; } = 200;

    /// <summary>Extra percent of the base cost added at full occupancy, scaled linearly with how full the lot is.</summary>
    public int OccupancyPricePercent { get; init; } = 100;

    /// <summary>Hard cap on the credit cost of a single reservation, however high peak/occupancy push it.</summary>
    public int MaxReservationCost { get; init; } = 40;

    /// <summary>Credits granted to each user's wallet at the start of every calendar month.</summary>
    public int MonthlyCreditAllowance { get; init; } = 100;

    /// <summary>Minutes a freed spot is held for the next in the waitlist before the offer lapses.</summary>
    public int QueueOfferMinutes { get; init; } = 15;

    /// <summary>Reputation points deducted for a no-show on a spot claimed from the waitlist (harsher than a normal no-show).</summary>
    public int QueueNoShowPenaltyPoints { get; init; } = 50;

    /// <summary>Extra credits fined from the wallet for a no-show on a spot claimed from the waitlist.</summary>
    public int QueueNoShowCreditPenalty { get; init; } = 30;

    /// <summary>Days the user is barred from the waitlist after a no-show on a spot claimed from it.</summary>
    public int QueueNoShowBanDays { get; init; } = 14;

    /// <summary>Credits cut from the user's next monthly allowance after a waitlist-claim no-show.</summary>
    public int QueueNoShowAllowancePenalty { get; init; } = 30;

    /// <summary>Extra percent added to the release reward at full occupancy (mirrors the occupancy price surcharge).</summary>
    public int DemandReleaseOccupancyPercent { get; init; } = 100;

    /// <summary>Extra release-reward points per person waiting in the queue for the freed window.</summary>
    public int DemandReleaseQueueBonus { get; init; } = 5;

    /// <summary>Cap on the demand-scaled release reward, however high occupancy and the queue push it.</summary>
    public int MaxReleaseReward { get; init; } = 40;

    /// <summary>Points/credits added per consecutive completion (streak), rewarding reliability.</summary>
    public int StreakBonusPerLevel { get; init; } = 2;

    /// <summary>Cap on a single streak bonus, so an endless run doesn't pay unboundedly.</summary>
    public int StreakBonusCap { get; init; } = 20;

    /// <summary>Reputation points needed to reach the Silver loyalty tier.</summary>
    public int TierSilverPoints { get; init; } = 50;

    /// <summary>Reputation points needed to reach the Gold loyalty tier.</summary>
    public int TierGoldPoints { get; init; } = 150;

    /// <summary>Reputation points needed to reach the Platinum loyalty tier.</summary>
    public int TierPlatinumPoints { get; init; } = 300;

    /// <summary>Minutes of head start in the waitlist per loyalty tier rank (higher tiers are served sooner).</summary>
    public int QueuePriorityPerTier { get; init; } = 30;

    /// <summary>Extra monthly credit allowance per loyalty tier rank.</summary>
    public int TierAllowanceBonus { get; init; } = 20;

    /// <summary>Reservation price discount (percent) per loyalty tier rank, capped so a booking is never free.</summary>
    public int TierDiscountPercent { get; init; } = 5;

    /// <summary>Percent of reputation faded toward zero each decay interval (0 disables decay).</summary>
    public int ReputationDecayPercent { get; init; } = 10;

    /// <summary>Days between reputation decay steps.</summary>
    public int ReputationDecayIntervalDays { get; init; } = 30;

    /// <summary>Whether the controller auto-tunes the peak surcharge toward a target occupancy.</summary>
    public bool AdaptivePricingEnabled { get; init; }

    /// <summary>Target peak-window occupancy (percent) the controller steers toward.</summary>
    public int AdaptiveTargetOccupancyPercent { get; init; } = 85;

    /// <summary>Controller gain: surcharge points adjusted per unit of occupancy error.</summary>
    public int AdaptiveGainPercent { get; init; } = 100;

    /// <summary>Occupancy error (percent) within which the controller leaves the surcharge alone.</summary>
    public int AdaptiveDeadbandPercent { get; init; } = 5;

    /// <summary>Largest change to the peak surcharge the controller may make in one step.</summary>
    public int AdaptiveStepMaxPercent { get; init; } = 25;

    /// <summary>Lower bound the controller keeps the peak surcharge at or above.</summary>
    public int AdaptivePeakMinPercent { get; init; } = 100;

    /// <summary>Upper bound the controller keeps the peak surcharge at or below.</summary>
    public int AdaptivePeakMaxPercent { get; init; } = 400;

    /// <summary>Minimum minutes between controller adjustments.</summary>
    public int AdaptiveIntervalMinutes { get; init; } = 60;

    /// <summary>Whether the trust graph is computed from sharing interactions.</summary>
    public bool TrustEnabled { get; init; } = true;

    /// <summary>Hours between trust-graph recomputations.</summary>
    public int TrustIntervalHours { get; init; } = 24;

    /// <summary>Trust score (0–100) at or above which the "Trusted" badge is awarded.</summary>
    public int TrustedBadgeThreshold { get; init; } = 60;

    /// <summary>Cap on how much a single counterpart contributes to the trust graph, blunting reciprocal pumping.</summary>
    public int MaxPairTrustWeight { get; init; } = 3;

    /// <summary>Whether suspicious reciprocal sharing pairs are detected and flagged.</summary>
    public bool AntiCollusionEnabled { get; init; } = true;

    /// <summary>Minimum mutual interactions before a pair can be flagged for collusion.</summary>
    public int CollusionMinInteractions { get; init; } = 4;

    /// <summary>Percent of each party's interactions that must be with the other to flag the pair.</summary>
    public int CollusionConcentrationPercent { get; init; } = 70;

    /// <summary>Hours between collusion scans.</summary>
    public int CollusionScanIntervalHours { get; init; } = 24;

    public static IncentivePolicy Default { get; } = new();

    /// <summary>
    /// The peak surcharge the adaptive controller would set given the measured peak occupancy:
    /// nudge proportionally to the error from target (outside a deadband), bounded per step and overall.
    /// </summary>
    public int ComputeAdaptivePeak(double measuredOccupancy)
    {
        var error = Math.Clamp(measuredOccupancy, 0.0, 1.0) - AdaptiveTargetOccupancyPercent / 100.0;
        if (Math.Abs(error) < AdaptiveDeadbandPercent / 100.0)
        {
            return PeakPricePercent;
        }

        var step = Math.Clamp((int)Math.Round(AdaptiveGainPercent * error, MidpointRounding.AwayFromZero),
            -AdaptiveStepMaxPercent, AdaptiveStepMaxPercent);
        return Math.Clamp(PeakPricePercent + step, AdaptivePeakMinPercent, Math.Max(AdaptivePeakMinPercent, AdaptivePeakMaxPercent));
    }

    /// <summary>The monthly allowance for a user of the given tier rank (base plus the tier bonus).</summary>
    public int AllowanceForTier(int tierRank) =>
        Math.Max(0, MonthlyCreditAllowance) + Math.Max(0, TierAllowanceBonus) * Math.Max(0, tierRank);

    /// <summary>Applies the loyalty-tier discount to a reservation cost (never below 1 credit).</summary>
    public int ApplyTierDiscount(int cost, int tierRank)
    {
        var percent = Math.Clamp(Math.Max(0, TierDiscountPercent) * Math.Max(0, tierRank), 0, 90);
        var discounted = (int)Math.Round(cost * (1.0 - percent / 100.0), MidpointRounding.AwayFromZero);
        return Math.Max(1, discounted);
    }

    /// <summary>The streak bonus for a run of <paramref name="streak"/> consecutive completions.</summary>
    public int ComputeStreakBonus(int streak) =>
        Math.Min(Math.Max(0, StreakBonusCap), Math.Max(0, streak) * Math.Max(0, StreakBonusPerLevel));

    /// <summary>The loyalty tier a reputation score falls in.</summary>
    public LoyaltyTier TierFor(int points)
    {
        if (points >= TierPlatinumPoints) return LoyaltyTier.Platinum;
        if (points >= TierGoldPoints) return LoyaltyTier.Gold;
        if (points >= TierSilverPoints) return LoyaltyTier.Silver;
        return LoyaltyTier.Bronze;
    }

    /// <summary>Rank of a tier (Bronze=0 … Platinum=3), used to scale tier perks.</summary>
    public static int TierRank(LoyaltyTier tier) => (int)tier;

    /// <summary>Whether a freshly geocoded address qualifies for automatic verification.</summary>
    public bool ShouldAutoVerify(double? distanceKm) =>
        AutoVerifyHomeAddress && distanceKm is { } km && km > 0 && km <= AutoVerifyMaxDistanceKm;

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

    /// <summary>
    /// A reservation is off-peak when its arrival falls outside the peak window. The window is a
    /// local wall-clock range at the lot, so the arrival is read in the site's time zone.
    /// </summary>
    public bool IsOffPeak(DateTimeOffset start, TimeZoneInfo timeZone)
    {
        var arrival = SiteTime.TimeOfDay(start, timeZone);
        return arrival < PeakStart || arrival >= PeakEnd;
    }

    /// <summary>A reservation is on-peak when its arrival falls inside the peak window.</summary>
    public bool IsPeak(DateTimeOffset start, TimeZoneInfo timeZone) => !IsOffPeak(start, timeZone);

    /// <summary>
    /// The credit cost to book a spot: the base cost multiplied by a peak surcharge and an occupancy
    /// surcharge that grows linearly with how full the lot is, then floored at the base and capped.
    /// </summary>
    public int ComputeReservationCost(bool isPeak, double occupancyRatio)
    {
        var ratio = Math.Clamp(occupancyRatio, 0.0, 1.0);
        var peakFactor = isPeak ? Math.Max(100, PeakPricePercent) / 100.0 : 1.0;
        var occupancyFactor = 1.0 + Math.Max(0, OccupancyPricePercent) / 100.0 * ratio;
        var raw = (int)Math.Ceiling(BaseReservationCost * peakFactor * occupancyFactor);
        var cap = Math.Max(BaseReservationCost, MaxReservationCost);
        return Math.Clamp(raw, BaseReservationCost, cap);
    }

    /// <summary>Whether a release at the given moment is early enough to be rewarded.</summary>
    public bool QualifiesForReleaseReward(DateTimeOffset start, DateTimeOffset releasedAt) =>
        releasedAt <= start - ReleaseCutoff;

    /// <summary>
    /// The release reward, scaled by how badly the freed spot was needed: an occupancy surcharge on the
    /// base reward plus a bonus per person waiting in the queue, capped. Freeing a spot when the lot is
    /// full and others are queued pays far more than freeing one nobody wanted.
    /// </summary>
    public int ComputeReleaseReward(double occupancyRatio, int waitingCount)
    {
        var ratio = Math.Clamp(occupancyRatio, 0.0, 1.0);
        var occupancyReward = ReleasePoints * (1.0 + Math.Max(0, DemandReleaseOccupancyPercent) / 100.0 * ratio);
        var queueReward = Math.Max(0, DemandReleaseQueueBonus) * Math.Max(0, waitingCount);
        var reward = (int)Math.Round(occupancyReward + queueReward, MidpointRounding.AwayFromZero);
        return Math.Clamp(reward, ReleasePoints, Math.Max(ReleasePoints, MaxReleaseReward));
    }

    /// <summary>Whether an un-used reservation has passed its grace period and is now a no-show.</summary>
    public bool IsNoShow(DateTimeOffset start, DateTimeOffset now) =>
        now >= start + NoShowGracePeriod;

    /// <summary>The instant on a given local day after which an unclaimed reserved spot auto-shares.</summary>
    public DateTimeOffset ResidentShareCutoff(DateOnly date, TimeZoneInfo timeZone) =>
        SiteTime.At(date, ResidentHoldUntil, timeZone) + NoShowGracePeriod;

    /// <summary>Whether a reserved spot for the requested day has auto-shared: today and past cutoff.</summary>
    public bool IsResidentAutoShareActive(DateOnly requestDate, DateTimeOffset now, TimeZoneInfo timeZone) =>
        requestDate == SiteTime.Today(now, timeZone) && now >= ResidentShareCutoff(requestDate, timeZone);
}
