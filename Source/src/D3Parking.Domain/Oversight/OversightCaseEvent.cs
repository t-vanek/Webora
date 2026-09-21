using D3Parking.Domain.Common;

namespace D3Parking.Domain.Oversight;

/// <summary>
/// One line of a case's history. Append-only: a case is answerable for what was done to whom, so
/// entries are never edited or deleted — a mistaken ruling is superseded by a later entry, not
/// erased.
/// </summary>
public class OversightCaseEvent : Entity
{
    public Guid CaseId { get; private set; }

    public OversightEventType Type { get; private set; }

    public OversightActor Actor { get; private set; }

    /// <summary>Who acted, when it was a person. Null for <see cref="OversightActor.System"/>.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>What the type cannot say by itself: a comment's text, a verdict's note.</summary>
    public string? Body { get; private set; }

    public OversightVisibility Visibility { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    private OversightCaseEvent() { }

    public OversightCaseEvent(
        Guid caseId,
        OversightEventType type,
        OversightActor actor,
        DateTimeOffset occurredAtUtc,
        Guid? actorUserId = null,
        string? body = null,
        OversightVisibility visibility = OversightVisibility.Internal)
    {
        CaseId = caseId;
        Type = type;
        Actor = actor;
        ActorUserId = actorUserId;
        Body = body;
        Visibility = visibility;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>The lot wrote this line itself.</summary>
    public static OversightCaseEvent FromSystem(
        Guid caseId,
        OversightEventType type,
        DateTimeOffset at,
        string? body = null,
        OversightVisibility visibility = OversightVisibility.Internal) =>
        new(caseId, type, OversightActor.System, at, actorUserId: null, body, visibility);

    /// <summary>A reviewer wrote this line.</summary>
    public static OversightCaseEvent FromReviewer(
        Guid caseId,
        OversightEventType type,
        Guid reviewerId,
        DateTimeOffset at,
        string? body = null,
        OversightVisibility visibility = OversightVisibility.Internal) =>
        new(caseId, type, OversightActor.Reviewer, at, reviewerId, body, visibility);
}
