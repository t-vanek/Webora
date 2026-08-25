using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;

namespace D3Parking.Domain.Parking.Incentives;

/// <summary>
/// The tunable rules that translate parking behaviour into points and decide what counts as
/// off-peak, an in-time release, or a no-show. Captured as an immutable value so it can later be
/// surfaced as a site setting; <see cref="Default"/> supplies sensible starting values.
/// </summary>
public sealed record IncentivePolicy
{
    /// <summary>Points awarded for releasing a reservation early enough to free the spot.</summary>
    public int ReleasePoints { get; init; }

    /// <summary>Points awarded for booking outside the peak window.</summary>
    public int OffPeakBonusPoints { get; init; }

    /// <summary>Points deducted for a no-show (stored positive, applied as a deduction).</summary>
    public int NoShowPenaltyPoints { get; init; }

    /// <summary>How far ahead of the start a release must happen to earn the reward.</summary>
    public TimeSpan ReleaseCutoff { get; init; }

    /// <summary>Grace period after the start before an un-used reservation becomes a no-show.</summary>
    public TimeSpan NoShowGracePeriod { get; init; }

    /// <summary>How long before the planned start to remind the holder about the reservation.</summary>
    public TimeSpan ReminderLeadTime { get; init; }

    public ReservationTimeMode ReservationTimeMode { get; init; } = ReservationTimeMode.TimeWindow;

    public int ReservationHorizonDays { get; init; } = 14;

    /// <summary>Whether a new booking may start on the current local calendar day.</summary>
    public bool SameDayReservationsAllowed { get; init; } = true;

    public Weekday AllowedReservationWeekdays { get; init; } = Weekday.Everyday;

    public HolidayCalendarRegion HolidayCalendarRegion { get; init; } = HolidayCalendarRegion.CzechRepublic;

    public bool PublicHolidayReservationsAllowed { get; init; }

    public bool WeeklyReservationLimitEnabled { get; init; } = true;

    public int WeeklyReservationLimit { get; init; } = 2;

    /// <summary>
    /// Weekly quota bounded by the weekdays on which the calendar permits reservations. The
    /// defensive bound also makes policies loaded from older, inconsistent rows safe immediately.
    /// </summary>
    public int EffectiveWeeklyReservationLimit =>
        AllowedReservationWeekdays.ClampWeeklyReservationLimit(WeeklyReservationLimit);

    /// <summary>Legacy persisted setting. Weekly limits no longer have a close-in bypass.</summary>
    public int LastMinuteUnlimitedHours { get; init; }

    /// <summary>Start of the daily high-demand window (local time of the reservation).</summary>
    public TimeOnly PeakStart { get; init; } = new(7, 30);

    /// <summary>End of the daily high-demand window (local time of the reservation).</summary>
    public TimeOnly PeakEnd { get; init; } = new(10, 0);

    /// <summary>Daily time until which a reserved spot is held for its resident before auto-sharing.</summary>
    public TimeOnly ResidentHoldUntil { get; init; } = new(8, 0);

    /// <summary>Points per hour of advance notice when a resident proactively releases their spot.</summary>
    public int ResidentReleasePointsPerHour { get; init; }

    /// <summary>Cap on the advance-notice part of a resident's release reward.</summary>
    public int ResidentReleaseMaxPoints { get; init; }

    /// <summary>Legacy setting retained for reading older configuration rows.</summary>
    public int ResidentMaxShareAllowance { get; init; }

    /// <summary>Legacy setting retained for reading older configuration rows.</summary>
    public int ResidentSharePercentPerAllowance { get; init; }

    /// <summary>Percent of a share's reward the resident gives back when the guest no-shows on it.</summary>
    public int ResidentWastedShareClawbackPercent { get; init; } = 25;

    /// <summary>
    /// How many days ahead a resident's usage plan releases the days they do not need. Bounded by
    /// <see cref="MaxReleaseRangeDays"/> — the planner may never reach further than a manual release.
    /// </summary>
    public int ResidentPlanHorizonDays { get; init; } = 14;

    public ResidentReclaimPolicy ResidentReclaimPolicy { get; init; } = ResidentReclaimPolicy.AdvanceOrReplacement;

    public bool ManualReleasesAreBinding { get; init; } = true;

    public ResidentProtectionDeadlineMode ResidentProtectionDeadlineMode { get; init; } = ResidentProtectionDeadlineMode.PreviousDayAtTime;

    public int ResidentProtectionLeadHours { get; init; } = 24;

    public TimeOnly ResidentProtectionPreviousDayTime { get; init; } = new(18, 0);

    public ResidentNoReplacementAction ResidentNoReplacementAction { get; init; } = ResidentNoReplacementAction.CancelAndQueue;

    public ResidentAlternativeBookingPolicy ResidentAlternativeBookingPolicy { get; init; } = ResidentAlternativeBookingPolicy.AutoRelease;

    /// <summary>Base points for taking a shared reserved spot, before the distance multiplier.</summary>
    public int SharedTakenBasePoints { get; init; }

    /// <summary>Commute distance (km) at which the distance multiplier reaches 1.0.</summary>
    public int SharedTakenReferenceKm { get; init; } = 10;

    /// <summary>Cap on the distance multiplier so very far commuters don't earn unbounded points.</summary>
    public int SharedTakenMaxMultiplier { get; init; } = 3;

    /// <summary>Auto-verify a home address (skip manual admin approval) when within the distance cap.</summary>
    public bool AutoVerifyHomeAddress { get; init; }

    /// <summary>Largest commute distance (km) eligible for auto-verification; farther needs admin review.</summary>
    public int AutoVerifyMaxDistanceKm { get; init; } = 50;

    /// <summary>Most releases a user can be rewarded for in a day; bounds reserve/release farming.</summary>
    public int MaxRewardedReleasesPerDay { get; init; }

    /// <summary>Largest day range a resident may release in one action.</summary>
    public int MaxReleaseRangeDays { get; init; } = 92;

    /// <summary>Base credit cost to book a spot off-peak in an empty lot, before peak/occupancy surcharges.</summary>
    public int BaseReservationCost { get; init; }

    /// <summary>
    /// Whether the parking economy is enabled. A zero base cost is the persisted switch used by
    /// administration; keeping the interpretation here gives UI, pricing and notifications one
    /// source of truth without adding a second setting that could disagree with the price.
    /// </summary>
    public bool CreditsEnabled => BaseReservationCost > 0;

    /// <summary>Percent of the base cost charged during the peak window (200 = double the off-peak price).</summary>
    public int PeakPricePercent { get; init; } = 200;

    /// <summary>Extra percent of the base cost added at full occupancy, scaled linearly with how full the lot is.</summary>
    public int OccupancyPricePercent { get; init; }

    /// <summary>Hard cap on the credit cost of a single reservation, however high peak/occupancy push it.</summary>
    public int MaxReservationCost { get; init; }

    /// <summary>Credits used as the wallet top-up target for each configured budget period.</summary>
    public int MonthlyCreditAllowance { get; init; }

    /// <summary>How often the wallet is topped up to <see cref="MonthlyCreditAllowance"/>.</summary>
    public BudgetRenewalPeriod BudgetRenewalPeriod { get; init; } = BudgetRenewalPeriod.Monthly;

    /// <summary>Minutes a freed spot is held for the next in the waitlist before the offer lapses.</summary>
    public int QueueOfferMinutes { get; init; } = 30;

    /// <summary>Reputation points deducted for a no-show on a spot claimed from the waitlist (harsher than a normal no-show).</summary>
    public int QueueNoShowPenaltyPoints { get; init; }

    /// <summary>Extra credits fined from the wallet for a no-show on a spot claimed from the waitlist.</summary>
    public int QueueNoShowCreditPenalty { get; init; }

    /// <summary>Days the user is barred from the waitlist after a no-show on a spot claimed from it.</summary>
    public int QueueNoShowBanDays { get; init; }

    /// <summary>Credits cut from the user's next monthly allowance after a waitlist-claim no-show.</summary>
    public int QueueNoShowAllowancePenalty { get; init; }

    /// <summary>Extra percent added to the release reward at full occupancy (mirrors the occupancy price surcharge).</summary>
    public int DemandReleaseOccupancyPercent { get; init; }

    /// <summary>Extra release-reward points per person waiting in the queue for the freed window.</summary>
    public int DemandReleaseQueueBonus { get; init; }

    /// <summary>Cap on the demand-scaled release reward, however high occupancy and the queue push it.</summary>
    public int MaxReleaseReward { get; init; }

    /// <summary>Points/credits added per consecutive completion (streak), rewarding reliability.</summary>
    public int StreakBonusPerLevel { get; init; }

    /// <summary>Cap on a single streak bonus, so an endless run doesn't pay unboundedly.</summary>
    public int StreakBonusCap { get; init; }

    /// <summary>Reputation points needed to reach the Silver loyalty tier.</summary>
    public int TierSilverPoints { get; init; } = 50;

    /// <summary>Reputation points needed to reach the Gold loyalty tier.</summary>
    public int TierGoldPoints { get; init; } = 150;

    /// <summary>Reputation points needed to reach the Platinum loyalty tier.</summary>
    public int TierPlatinumPoints { get; init; } = 300;

    /// <summary>Minutes of head start in the waitlist per loyalty tier rank (higher tiers are served sooner).</summary>
    public int QueuePriorityPerTier { get; init; }

    /// <summary>Extra monthly credit allowance per loyalty tier rank.</summary>
    public int TierAllowanceBonus { get; init; }

    /// <summary>Reservation price discount (percent) per loyalty tier rank, capped so a booking is never free.</summary>
    public int TierDiscountPercent { get; init; }

    /// <summary>Percent of reputation faded toward zero each decay interval (0 disables decay).</summary>
    public int ReputationDecayPercent { get; init; }

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
    public bool TrustEnabled { get; init; }

    /// <summary>Hours between trust-graph recomputations.</summary>
    public int TrustIntervalHours { get; init; } = 24;

    /// <summary>Trust score (0–100) at or above which the "Trusted" badge is awarded.</summary>
    public int TrustedBadgeThreshold { get; init; } = 60;

    /// <summary>Cap on how much a single counterpart contributes to the trust graph, blunting reciprocal pumping.</summary>
    public int MaxPairTrustWeight { get; init; } = 3;

    /// <summary>Whether suspicious reciprocal sharing pairs are detected and flagged.</summary>
    public bool AntiCollusionEnabled { get; init; }

    /// <summary>Minimum mutual interactions before a pair can be flagged for collusion.</summary>
    public int CollusionMinInteractions { get; init; } = 4;

    /// <summary>Percent of each party's interactions that must be with the other to flag the pair.</summary>
    public int CollusionConcentrationPercent { get; init; } = 70;

    /// <summary>Hours between collusion scans.</summary>
    public int CollusionScanIntervalHours { get; init; } = 24;

    // --- Availability campaigns (proactive "the lot is wide open" tips) ---

    public bool AvailabilityCampaignsEnabled { get; init; } = true;

    public int AvailabilityLookaheadDays { get; init; } = 14;

    public int AvailabilityFreeThresholdPercent { get; init; } = 60;

    public bool HighOccupancyCampaignsEnabled { get; init; }

    public int AvailabilityBusyThresholdPercent { get; init; } = 85;

    public int AvailabilityMinConsecutiveDays { get; init; } = 1;

    public int AvailabilitySendHourLocal { get; init; } = 9;

    public static IncentivePolicy Default { get; } = new();

    /// <summary>
    /// Evaluates the booking calendar without regard to the requesting user's role. This is the
    /// authoritative date rule for ordinary users, residents, visitors, handoffs and waitlists.
    /// </summary>
    public ReservationDateAvailability GetReservationDateAvailability(DateOnly date, DateOnly today)
    {
        if (date < today)
        {
            return ReservationDateAvailability.Past;
        }

        if (date > today.AddDays(Math.Clamp(ReservationHorizonDays, 1, 366)))
        {
            return ReservationDateAvailability.OutsideReservationHorizon;
        }

        if (date == today && !SameDayReservationsAllowed)
        {
            return ReservationDateAvailability.SameDayNotAllowed;
        }

        if (!PublicHolidayReservationsAllowed
            && HolidayCalendar.IsPublicHoliday(date, HolidayCalendarRegion))
        {
            return ReservationDateAvailability.PublicHolidayNotAllowed;
        }

        if (!AllowedReservationWeekdays.Sanitize().Includes(date))
        {
            return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? ReservationDateAvailability.WeekendNotAllowed
                : ReservationDateAvailability.WeekdayNotAllowed;
        }

        return ReservationDateAvailability.Allowed;
    }

    public ReservationDateAvailability GetReservationDateAvailability(
        DateTimeOffset start,
        DateTimeOffset now,
        TimeZoneInfo timeZone) =>
        GetReservationDateAvailability(SiteTime.Today(start, timeZone), SiteTime.Today(now, timeZone));

    public DateOnly FirstBookableDate(DateOnly today) =>
        SameDayReservationsAllowed ? today : today.AddDays(1);

    public bool IsReservationStartDateAllowed(DateTimeOffset start, DateTimeOffset now, TimeZoneInfo timeZone) =>
        SiteTime.Today(start, timeZone) >= FirstBookableDate(SiteTime.Today(now, timeZone));

    /// <summary>Whether a plan starts inside the rolling local-calendar booking horizon.</summary>
    public bool IsWithinReservationHorizon(DateTimeOffset start, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var today = SiteTime.Today(now, timeZone);
        var startDate = SiteTime.Today(start, timeZone);
        return startDate >= FirstBookableDate(today)
            && startDate <= today.AddDays(Math.Clamp(ReservationHorizonDays, 1, 366));
    }

    /// <summary>Whether a reservation may start on this local weekday.</summary>
    public bool IsReservationWeekdayAllowed(DateTimeOffset start, TimeZoneInfo timeZone) =>
        AllowedReservationWeekdays.Sanitize().Includes(SiteTime.Today(start, timeZone));

    /// <summary>Whether the local date passes the shared weekday and public-holiday calendar.</summary>
    public bool IsReservationDateAllowed(DateOnly date) =>
        AllowedReservationWeekdays.Sanitize().Includes(date)
        && (PublicHolidayReservationsAllowed || !HolidayCalendar.IsPublicHoliday(date, HolidayCalendarRegion));

    public bool IsPublicHolidayReservationAllowed(DateTimeOffset start, TimeZoneInfo timeZone)
    {
        var date = SiteTime.Today(start, timeZone);
        return PublicHolidayReservationsAllowed || !HolidayCalendar.IsPublicHoliday(date, HolidayCalendarRegion);
    }

    /// <summary>The local Monday-Sunday week containing a date.</summary>
    public static (DateOnly Start, DateOnly End) WeekOf(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
        var start = date.AddDays(-daysFromMonday);
        return (start, start.AddDays(7));
    }

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

    /// <summary>The recurring allowance for a user of the given tier rank (base plus the tier bonus).</summary>
    public int AllowanceForTier(int tierRank) =>
        Math.Max(0, MonthlyCreditAllowance) + Math.Max(0, TierAllowanceBonus) * Math.Max(0, tierRank);

    /// <summary>
    /// Applies the loyalty-tier discount to a reservation cost. A paid booking never drops below
    /// 1 credit — the floor guards against discounts making parking free, not against the admin
    /// turning the credit economy off. With a zero base cost (economy disabled) the price stays 0.
    /// </summary>
    public int ApplyTierDiscount(int cost, int tierRank)
    {
        if (cost <= 0)
        {
            return 0;
        }

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
    /// Points for a proactive resident release: an advance-notice bonus (earlier = more, capped).
    /// </summary>
    public int ComputeShareReward(DateTimeOffset shareCutoff, DateTimeOffset releasedAt)
    {
        var hoursEarly = Math.Max(0d, (shareCutoff - releasedAt).TotalHours);
        return Math.Min(ResidentReleaseMaxPoints, (int)Math.Ceiling(hoursEarly) * ResidentReleasePointsPerHour);
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
    /// Fixed planning-budget cost. Occupancy is deliberately ignored: a higher price cannot create
    /// another physical space and would merely penalise the person who planned later. The parameter
    /// remains for compatibility with historical quote callers and analytics.
    /// </summary>
    public int ComputeReservationCost(double occupancyRatio)
    {
        _ = occupancyRatio;
        return Math.Max(0, BaseReservationCost);
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

    /// <summary>
    /// The last day a usage plan materializes into shared capacity. The standing weekday pattern is
    /// indefinite, but effective releases never extend beyond the same horizon as reservations.
    /// </summary>
    public DateOnly ResidentPlanHorizonEnd(DateOnly today) =>
        today.AddDays(Math.Clamp(Math.Min(ResidentPlanHorizonDays, ReservationHorizonDays), 1, 366));

    public DateTimeOffset ResidentProtectionDeadline(DateTimeOffset reservationStart, TimeZoneInfo timeZone)
    {
        if (ResidentProtectionDeadlineMode == ResidentProtectionDeadlineMode.HoursBeforeStart)
        {
            return reservationStart - TimeSpan.FromHours(Math.Clamp(ResidentProtectionLeadHours, 1, 168));
        }

        var localDate = SiteTime.Today(reservationStart, timeZone);
        return SiteTime.At(localDate.AddDays(-1), ResidentProtectionPreviousDayTime, timeZone);
    }

    public bool IsBeforeResidentProtectionDeadline(DateTimeOffset reservationStart, DateTimeOffset now, TimeZoneInfo timeZone) =>
        now < ResidentProtectionDeadline(reservationStart, timeZone);
}
