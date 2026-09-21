namespace D3Parking.Domain.Oversight;

/// <summary>
/// Who caused a timeline entry. Kept as three roles rather than "user id or null" because the
/// timeline is read as a story: a reviewer needs to see at a glance which lines the lot wrote
/// itself and which a person did.
/// </summary>
public enum OversightActor
{
    /// <summary>The lot itself: the ingest, the nightly scan, the maintenance sweep.</summary>
    System,

    /// <summary>An administrator working the queue.</summary>
    Reviewer,

    /// <summary>Someone the case is about — the reporter, a flagged colleague.</summary>
    Participant,
}
