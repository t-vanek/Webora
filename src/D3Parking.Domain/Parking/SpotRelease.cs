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

    public SpotReleaseSource Source { get; private set; } = SpotReleaseSource.Manual;

    public int AwardedPoints { get; private set; }

    /// <summary>
    /// Total reward points clawed back for wasted (no-showed) bookings on this day. Never exceeds
    /// <see cref="AwardedPoints"/> — the owner cannot control who shows up, so the worst case for
    /// a shared day is losing its reward, never going net negative.
    /// </summary>
    public int ClawedBackPoints { get; private set; }

    /// <summary>When this release was reconciled (reward kept or clawed back); null = still pending.</summary>
    public DateTimeOffset? ReconciledAtUtc { get; private set; }

    private SpotRelease() { }

    public SpotRelease(Guid spotId, Guid ownerId, DateOnly date, DateTimeOffset releasedAtUtc, int awardedPoints,
        SpotReleaseSource source = SpotReleaseSource.Manual)
    {
        SpotId = spotId;
        OwnerId = ownerId;
        Date = date;
        ReleasedAtUtc = releasedAtUtc;
        AwardedPoints = awardedPoints;
        Source = source;
    }

    public void MarkReconciled(DateTimeOffset at) => ReconciledAtUtc ??= at;

    /// <summary>
    /// Registers a clawback attempt and returns the amount that actually applies: whatever of the
    /// requested points still fits under the day's awarded reward. Each no-show requests its own
    /// percentage of the award, so several no-shows on one shared day would otherwise stack past
    /// 100 % and punish the owner beyond what the share ever earned.
    /// </summary>
    public int RegisterClawback(int requestedPoints)
    {
        var applicable = Math.Clamp(requestedPoints, 0, AwardedPoints - ClawedBackPoints);
        ClawedBackPoints += applicable;
        return applicable;
    }
}
