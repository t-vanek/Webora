namespace D3Parking.Domain.Oversight;

/// <summary>
/// What kind of signal a case was opened for. The kind decides two things that nothing else does:
/// which evidence panel the review shows, and which permission may see the case at all — the
/// mismatch evidence includes photographs of third parties' cars, the collusion evidence names
/// two colleagues, and neither warrants seeing the other.
/// </summary>
public enum OversightCaseKind
{
    /// <summary>A driver reported they could not park at their reserved spot.</summary>
    OccupancyMismatch,

    /// <summary>The nightly scan flagged a pair whose sharing is concentrated on each other.</summary>
    CollusionRing,
}
