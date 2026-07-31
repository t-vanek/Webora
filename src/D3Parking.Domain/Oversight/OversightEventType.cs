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
