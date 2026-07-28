using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// Records that a resident proactively released their reserved spot into the shared pool for a
/// specific day. One row per spot and day; drives both availability and the awarded reward.
/// </summary>
public class SpotRelease : Entity
{
    public Guid SpotId { get; private set; }

    public Guid OwnerId { get; private set; }

    public DateOnly Date { get; private set; }

    public DateTimeOffset ReleasedAtUtc { get; private set; }

    public int AwardedPoints { get; private set; }

    /// <summary>When this release was reconciled (reward kept or clawed back); null = still pending.</summary>
    public DateTimeOffset? ReconciledAtUtc { get; private set; }

    private SpotRelease() { }

    public SpotRelease(Guid spotId, Guid ownerId, DateOnly date, DateTimeOffset releasedAtUtc, int awardedPoints)
    {
        SpotId = spotId;
        OwnerId = ownerId;
        Date = date;
        ReleasedAtUtc = releasedAtUtc;
        AwardedPoints = awardedPoints;
    }

    public void MarkReconciled(DateTimeOffset at) => ReconciledAtUtc ??= at;
}
