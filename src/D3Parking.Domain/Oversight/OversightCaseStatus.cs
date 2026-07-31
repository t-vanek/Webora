namespace D3Parking.Domain.Oversight;

/// <summary>
/// Where a case stands as a piece of work. Deliberately small: every state here is reachable and
/// leavable today. The states the roadmap still holds back — merged into a recurring case,
/// auto-closed after an archive period — are not declared until something can put a case in them.
/// </summary>
public enum OversightCaseStatus
{
    /// <summary>Opened by the ingest, nobody has taken it.</summary>
    New,

    /// <summary>Someone owns it.</summary>
    InProgress,

    /// <summary>
    /// The reviewer asked the driver something and is waiting. Open, but not the reviewer's move —
    /// which is why the deadline stops running here.
    /// </summary>
    AwaitingInfo,

    /// <summary>A verdict was recorded; the case leaves the open queue.</summary>
    Resolved,
}
