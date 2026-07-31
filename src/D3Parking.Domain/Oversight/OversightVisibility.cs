namespace D3Parking.Domain.Oversight;

/// <summary>
/// Who may read a timeline entry. One timeline with two readings, rather than two timelines: the
/// moment the reviewer's history and the participant's history are separate records they drift,
/// and the question "what was this person actually told" stops having one answer.
/// </summary>
public enum OversightVisibility
{
    /// <summary>Reviewers only. The default for everything — nothing reaches a participant by accident.</summary>
    Internal,

    /// <summary>Also shown to the people the case is about.</summary>
    Participants,
}
