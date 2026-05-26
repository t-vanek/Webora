namespace Webora.Domain.Parking.Incentives;

/// <summary>
/// A user's running incentive standing. Keyed by user id; the point balance and the behaviour
/// counters are kept in step with the <see cref="PointsLedgerEntry"/> stream and feed the
/// leaderboard and badge rules.
/// </summary>
public class ParkerScore
{
    public Guid UserId { get; private set; }

    public int Points { get; private set; }

    public int ReservationsCompleted { get; private set; }

    public int ReservationsReleased { get; private set; }

    public int OffPeakReservations { get; private set; }

    public int NoShows { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private ParkerScore() { }

    public ParkerScore(Guid userId) => UserId = userId;

    public void RewardRelease(int points, DateTimeOffset at)
    {
        Points += points;
        ReservationsReleased++;
        UpdatedAtUtc = at;
    }

    public void RewardOffPeak(int points, DateTimeOffset at)
    {
        Points += points;
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
        ReservationsReleased++;
        UpdatedAtUtc = at;
    }

    /// <summary>Claws back part of a share reward when the guest wasted the shared spot.</summary>
    public void RevokeSharePoints(int points, DateTimeOffset at)
    {
        Points -= Math.Abs(points);
        UpdatedAtUtc = at;
    }

    /// <summary>A far-commuting user took a shared spot; rewarded by distance.</summary>
    public void RewardSharedSpotTaken(int points, DateTimeOffset at)
    {
        Points += points;
        UpdatedAtUtc = at;
    }

    /// <summary>A manual administrative correction; does not touch behaviour counters.</summary>
    public void Adjust(int delta, DateTimeOffset at)
    {
        Points += delta;
        UpdatedAtUtc = at;
    }
}
