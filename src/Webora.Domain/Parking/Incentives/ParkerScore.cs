namespace Webora.Domain.Parking.Incentives;

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

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private ParkerScore() { }

    public ParkerScore(Guid userId) => UserId = userId;

    /// <summary>The budget period (year×100+month) an instant falls in.</summary>
    public static int PeriodOf(DateTimeOffset at) => at.Year * 100 + at.Month;

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

        var amount = Math.Max(0, allowance);
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
        UpdatedAtUtc = at;
    }

    public void RecordCompletion(DateTimeOffset at)
    {
        ReservationsCompleted++;
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

    /// <summary>A manual administrative correction; does not touch behaviour counters.</summary>
    public void Adjust(int delta, DateTimeOffset at)
    {
        Points += delta;
        UpdatedAtUtc = at;
    }
}
