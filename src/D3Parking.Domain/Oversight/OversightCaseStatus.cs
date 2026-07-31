namespace D3Parking.Domain.Oversight;

/// <summary>
/// Where a case stands as a piece of work. Deliberately small: every state here is reachable and
/// leavable today. The states the roadmap adds with the features that produce them — waiting on a
/// participant's answer, merged into a recurring case, auto-closed after an archive period — are
/// not declared until something can actually put a case in them.
/// </summary>
public enum OversightCaseStatus
{
    /// <summary>Opened by the ingest, nobody has taken it.</summary>
    New,

    /// <summary>Someone owns it.</summary>
    InProgress,

    /// <summary>A verdict was recorded; the case leaves the open queue.</summary>
    Resolved,
}
