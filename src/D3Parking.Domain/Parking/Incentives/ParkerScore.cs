namespace D3Parking.Domain.Parking.Incentives;

/// <summary>
/// A user's running incentive standing. Keyed by user id; the point balance and the behaviour
/// counters are kept in step with the <see cref="PointsLedgerEntry"/> stream and feed the
/// leaderboard and badge rules.
/// </summary>
public class ParkerScore
{
    public Guid UserId { get; private set; }

    /// <summary>Reputation score for the leaderboard and badges. Earned by good behaviour; spending credits never lowers it.</summary>
    public int Points { get; private set; }

    /// <summary>Spendable wallet balance: monthly allowance plus behaviour rewards, drawn down by reservation charges.</summary>
    public int Credits { get; private set; }

    /// <summary>The year×100+month for which the monthly allowance was last granted, so it is granted once per month.</summary>
    public int LastCreditGrantPeriod { get; private set; }

    public int ReservationsCompleted { get; private set; }

    public int ReservationsReleased { get; private set; }

    public int OffPeakReservations { get; private set; }

    public int NoShows { get; private set; }

    /// <summary>Consecutive completed reservations with no no-show in between; drives the streak bonus.</summary>
    public int CompletionStreak { get; private set; }

    /// <summary>Until when the user is barred from joining the waitlist (a queue no-show penalty).</summary>
    public DateTimeOffset? QueueBannedUntilUtc { get; private set; }

    /// <summary>Credits subtracted from the next monthly allowance (a queue no-show penalty), then cleared.</summary>
    public int NextAllowancePenalty { get; private set; }

    /// <summary>When reputation was last decayed; the decay sweep advances it one interval at a time.</summary>
    public DateTimeOffset? LastDecayUtc { get; private set; }

    /// <summary>Trust-graph standing (0–100, relative to the most trusted member); recomputed periodically.</summary>
    public int TrustScore { get; private set; }

    /// <summary>When the trust score was last recomputed.</summary>
    public DateTimeOffset? TrustComputedUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private ParkerScore() { }

    public ParkerScore(Guid userId) => UserId = userId;

    /// <summary>The budget period (year×100+month) an instant falls in.</summary>
    public static int PeriodOf(DateTimeOffset at) => at.Year * 100 + at.Month;

    /// <summary>
    /// What the next monthly grant would actually pay out: the allowance less any pending penalty.
    /// Price quotes use this so the balance they promise matches what <see cref="GrantMonthlyCreditIfDue"/>
    /// really grants.
    /// </summary>
    public int PreviewAllowance(int allowance) => Math.Max(0, Math.Max(0, allowance) - NextAllowancePenalty);

    /// <summary>
    /// Tops the wallet up with the monthly allowance, but only once per calendar month. Returns the
    /// amount actually granted (0 when this month's allowance was already given).
    /// </summary>
    public int GrantMonthlyCreditIfDue(int allowance, int period, DateTimeOffset at)
    {
        if (LastCreditGrantPeriod >= period)
        {
            return 0;
        }

        // A pending queue no-show penalty reduces this one grant, then is consumed.
        var amount = PreviewAllowance(allowance);
        NextAllowancePenalty = 0;
        Credits += amount;
        LastCreditGrantPeriod = period;
        UpdatedAtUtc = at;
        return amount;
    }

    /// <summary>Debits the wallet for a booking. Callers must check <see cref="Credits"/> first.</summary>
    public void ChargeCredits(int amount, DateTimeOffset at)
    {
        Credits -= Math.Max(0, amount);
        UpdatedAtUtc = at;
    }

    /// <summary>Returns credits to the wallet when a booking is given up early enough.</summary>
    public void RefundCredits(int amount, DateTimeOffset at)
    {
        Credits += Math.Max(0, amount);
        UpdatedAtUtc = at;
    }

    public void RewardRelease(int points, DateTimeOffset at)
    {
        Points += points;
        Credits += points;
        ReservationsReleased++;
        UpdatedAtUtc = at;
    }

    public void RewardOffPeak(int points, DateTimeOffset at)
    {
        Points += points;
        Credits += points;
        OffPeakReservations++;
        UpdatedAtUtc = at;
    }

    public void PenalizeNoShow(int penalty, DateTimeOffset at)
    {
        Points -= Math.Abs(penalty);
        NoShows++;
        CompletionStreak = 0;
        UpdatedAtUtc = at;
    }

    /// <summary>A reliability bonus for an unbroken run of completions; tops up both reputation and wallet.</summary>
    public void RewardStreak(int points, DateTimeOffset at)
    {
        Points += points;
        Credits += points;
        UpdatedAtUtc = at;
    }

    /// <summary>An extra credit fine (may push the wallet negative — a debt to earn back).</summary>
    public void PenalizeCredits(int amount, DateTimeOffset at)
    {
        Credits -= Math.Max(0, amount);
        UpdatedAtUtc = at;
    }

    /// <summary>Bars the user from the waitlist until the given instant (extends an existing ban).</summary>
    public void BanFromQueue(DateTimeOffset until, DateTimeOffset at)
    {
        if (QueueBannedUntilUtc is not { } current || until > current)
        {
            QueueBannedUntilUtc = until;
        }

        UpdatedAtUtc = at;
    }

    /// <summary>Queues a reduction of the next monthly allowance.</summary>
    public void AddAllowancePenalty(int amount, DateTimeOffset at)
    {
        NextAllowancePenalty += Math.Max(0, amount);
        UpdatedAtUtc = at;
    }

    /// <summary>Whether the user is currently barred from joining the waitlist.</summary>
    public bool IsQueueBanned(DateTimeOffset now) => QueueBannedUntilUtc is { } until && until > now;

    public void RecordCompletion(DateTimeOffset at)
    {
        ReservationsCompleted++;
        CompletionStreak++;
        UpdatedAtUtc = at;
    }

    /// <summary>A resident shared their reserved spot with the pool; counts as a release.</summary>
    public void RewardSharing(int points, DateTimeOffset at)
    {
        Points += points;
        Credits += points;
        ReservationsReleased++;
        UpdatedAtUtc = at;
    }

    /// <summary>Claws back part of a share reward when the guest wasted the shared spot.</summary>
    public void RevokeSharePoints(int points, DateTimeOffset at)
    {
        var amount = Math.Abs(points);
        Points -= amount;
        Credits = Math.Max(0, Credits - amount);
        UpdatedAtUtc = at;
    }

    /// <summary>A far-commuting user took a shared spot; rewarded by distance.</summary>
    public void RewardSharedSpotTaken(int points, DateTimeOffset at)
    {
        Points += points;
        Credits += points;
        UpdatedAtUtc = at;
    }

    /// <summary>
    /// Fades reputation toward zero so it reflects recent behaviour (and old penalties heal). Decays
    /// once per elapsed interval since the last decay; the first call just sets the baseline. Returns
    /// the signed change to Points (0 when nothing decayed), for the ledger.
    /// </summary>
    public int DecayReputationIfDue(int percent, int intervalDays, DateTimeOffset now)
    {
        if (percent <= 0 || intervalDays <= 0)
        {
            return 0;
        }

        if (LastDecayUtc is not { } last)
        {
            LastDecayUtc = now;
            return 0;
        }

        var periods = (int)((now - last).TotalDays / intervalDays);
        if (periods <= 0)
        {
            return 0;
        }

        var factor = Math.Pow(1.0 - Math.Min(percent, 100) / 100.0, periods);
        var before = Points;
        Points = (int)Math.Round(Points * factor, MidpointRounding.AwayFromZero);
        LastDecayUtc = last.AddDays(periods * (double)intervalDays);
        UpdatedAtUtc = now;
        return Points - before;
    }

    /// <summary>Sets the recomputed trust-graph score (0–100).</summary>
    public void SetTrust(int trustScore, DateTimeOffset at)
    {
        TrustScore = Math.Clamp(trustScore, 0, 100);
        TrustComputedUtc = at;
        UpdatedAtUtc = at;
    }

    /// <summary>A manual administrative correction; does not touch behaviour counters.</summary>
    public void Adjust(int delta, DateTimeOffset at)
    {
        Points += delta;
        UpdatedAtUtc = at;
    }
}
