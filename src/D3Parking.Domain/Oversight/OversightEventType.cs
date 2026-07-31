namespace D3Parking.Domain.Oversight;

/// <summary>
/// What a timeline entry records. The type carries the meaning; <see cref="OversightCaseEvent.Body"/>
/// only carries what the type cannot (a comment's text, the note behind a verdict).
/// </summary>
public enum OversightEventType
{
    /// <summary>The case was opened for a signal.</summary>
    Opened,

    /// <summary>A reviewer took the case.</summary>
    Claimed,

    /// <summary>A reviewer put the case back in the queue.</summary>
    Released,

    PriorityChanged,

    /// <summary>The lot raised the priority itself, and says on what grounds.</summary>
    Escalated,

    /// <summary>The deadline for this case's priority passed with the case still open.</summary>
    SlaBreached,

    /// <summary>
    /// The signal behind the case moved after the case was opened: the same spot was reported
    /// again, or the nightly scan re-measured the pair. Without it the numbers on the evidence
    /// panel change under the reviewer with nothing to say when, or why.
    /// </summary>
    SignalUpdated,

    /// <summary>A note, internal or shown to the participants.</summary>
    Comment,

    /// <summary>
    /// A reviewer opened the prefilled mail to a participant. Recorded because the follow-up
    /// otherwise happens entirely outside the system and the next reviewer cannot tell whether
    /// anyone has already asked.
    /// </summary>
    ContactedByEmail,

    VoucherApproved,

    VoucherRejected,

    Resolved,

    Reopened,
}
