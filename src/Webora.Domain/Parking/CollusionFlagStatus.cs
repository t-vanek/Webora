namespace Webora.Domain.Parking;

/// <summary>Review state of a detected collusion flag.</summary>
public enum CollusionFlagStatus
{
    /// <summary>Newly detected, awaiting admin review.</summary>
    New,

    /// <summary>An admin looked at it and considered it real (kept for the record).</summary>
    Reviewed,

    /// <summary>An admin judged it a false positive; future scans leave it alone.</summary>
    Dismissed,
}
