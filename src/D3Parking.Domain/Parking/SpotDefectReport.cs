using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// Something wrong with the lot, reported by whoever ran into it. The first signal the lot does not
/// detect for itself — a burnt-out light or a skip parked across two bays is invisible to every
/// sweep and every scan, and until this it reached the spot manager only if somebody happened to
/// mention it.
/// </summary>
/// <remarks>
/// Deliberately not a complaint about a person: there is no accused party and nothing to rule on,
/// only something to fix. It carries no photograph either — the photo storage the lot has is built
/// around the one-picture-proves-one-mismatch fingerprint, and stretching it to a kind of report
/// that has nothing to prove would weaken the rule that makes it worth having.
/// </remarks>
public class SpotDefectReport : Entity
{
    /// <summary>The spot it is about, or null when it is the lot itself (the barrier, the lighting).</summary>
    public Guid? SpotId { get; private set; }

    public Guid ReporterId { get; private set; }

    public SpotDefectCategory Category { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public DateTimeOffset ReportedAtUtc { get; private set; }

    private SpotDefectReport() { }

    public SpotDefectReport(Guid reporterId, SpotDefectCategory category, string description, DateTimeOffset reportedAtUtc, Guid? spotId = null)
    {
        ReporterId = reporterId;
        Category = category;
        Description = description;
        ReportedAtUtc = reportedAtUtc;
        SpotId = spotId;
    }
}
