using D3Parking.Application.Parking;
using D3Parking.Domain.Oversight;

namespace D3Parking.Application.Oversight;

/// <summary>The saved views of the queue. One row of tabs, one meaning each.</summary>
public enum OversightView
{
    /// <summary>Everything still wanting a human. The landing view.</summary>
    Open,

    /// <summary>Assigned to the reviewer asking.</summary>
    Mine,

    /// <summary>Open and nobody has taken it.</summary>
    Unassigned,

    /// <summary>Open and past the deadline its priority set.</summary>
    Overdue,

    /// <summary>Including resolved ones — the record, not the workload.</summary>
    All,
}

/// <summary>What the reviewer narrowed the queue down to.</summary>
/// <param name="ReviewerId">Who is asking — what <see cref="OversightView.Mine"/> means.</param>
/// <param name="View">The saved view.</param>
/// <param name="Kind">One kind only, when the reviewer wants a single queue back.</param>
/// <param name="Priority">One priority only.</param>
/// <param name="Search">Matches the case number, the spot code, and the names of the people involved.</param>
public sealed record OversightQuery(
    Guid ReviewerId,
    OversightView View = OversightView.Open,
    OversightCaseKind? Kind = null,
    OversightCasePriority? Priority = null,
    string? Search = null);

/// <summary>A case as the queue lists it: enough to triage, not enough to rule on.</summary>
/// <param name="Subject">The one line that says what this is about — a spot code, a pair of names.</param>
public sealed record OversightCaseListItemDto(
    Guid Id,
    int Number,
    OversightCaseKind Kind,
    OversightCaseStatus Status,
    OversightCasePriority Priority,
    string Subject,
    Guid? AssigneeId,
    string? AssigneeName,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DueAtUtc,
    bool IsOverdue,
    OversightResolution? Resolution);

/// <summary>The queue plus the numbers the tabs show, counted once for all of them.</summary>
public sealed record OversightQueueDto(
    IReadOnlyList<OversightCaseListItemDto> Cases,
    int OpenCount,
    int MineCount,
    int UnassignedCount,
    int OverdueCount,
    int TotalCount);

/// <summary>One line of a case's history, resolved for display.</summary>
public sealed record OversightTimelineEntryDto(
    Guid Id,
    OversightEventType Type,
    OversightActor Actor,
    string? ActorName,
    string? Body,
    OversightVisibility Visibility,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// A case opened for review: the case itself, its history, and whichever evidence panel its kind
/// calls for. Exactly one of the evidence properties is set — the other is null because the case
/// is not of that kind, never because the evidence went missing.
/// </summary>
public sealed record OversightCaseDetailDto(
    Guid Id,
    int Number,
    OversightCaseKind Kind,
    OversightCaseStatus Status,
    OversightCasePriority Priority,
    string Subject,
    Guid? AssigneeId,
    string? AssigneeName,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DueAtUtc,
    bool IsOverdue,
    DateTimeOffset? ResolvedAtUtc,
    OversightResolution? Resolution,
    string? ResolutionNote,
    OccupancyMismatchDto? Mismatch,
    CollusionFlagDto? Flag,
    IReadOnlyList<OversightTimelineEntryDto> Timeline);
