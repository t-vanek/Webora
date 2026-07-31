using D3Parking.Application.Parking;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;

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
/// Somebody a case is about, and therefore somebody a ruling on it could fall on. The list comes
/// from the case rather than from a search box: a sanction has to be aimed at a party to the thing
/// being decided, not at whoever the reviewer types.
/// </summary>
public sealed record OversightPartyDto(Guid UserId, string Name);

/// <summary>
/// Somebody who could be given this case: they hold the permission that may see its kind. The
/// distinction from a party matters — one is who the case is about, the other is who could work it,
/// and handing a case to somebody who cannot open it would be a dead end.
/// </summary>
public sealed record OversightReviewerDto(Guid UserId, string Name);

/// <summary>Something reported wrong with the lot, as the spot manager reads it.</summary>
public sealed record SpotDefectDto(
    Guid Id,
    string? SpotCode,
    SpotDefectCategory Category,
    string Description,
    string ReporterName,
    string? ReporterEmail,
    bool HasPhoto,
    DateTimeOffset ReportedAtUtc);

/// <summary>
/// A driver's own report, as they see it. Deliberately not the reviewer's view of the same case:
/// no internal notes, no reviewer's name — only what was decided, what has been asked of them, and
/// what they can still do about it.
/// </summary>
/// <param name="Question">The reviewer's open question, when the case is waiting on an answer.</param>
/// <param name="CanAppeal">
/// True when the ruling went against them and they have not already disputed it.
/// </param>
public sealed record MyOversightReportDto(
    Guid Id,
    int Number,
    OversightCaseKind Kind,
    string SpotCode,
    OversightCaseStatus Status,
    DateTimeOffset OpenedAtUtc,
    OversightResolution? Resolution,
    ApologyVoucherStatus? VoucherStatus,
    string? Question,
    bool CanAppeal,
    IReadOnlyList<OversightTimelineEntryDto> Timeline);

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
    SpotDefectDto? Defect,
    IReadOnlyList<OversightPartyDto> Parties,
    IReadOnlyList<OversightTimelineEntryDto> Timeline);
