using D3Parking.Application.Parking;
using D3Parking.Domain.Oversight;

namespace D3Parking.Application.Oversight;

/// <summary>
/// The oversight desk: one queue over every signal the lot flags for a human, and one history per
/// case. Every read takes an <see cref="OversightScope"/> and every write takes the reviewer's id —
/// the service is the only place that decides what a caller may see and what lands on the timeline.
/// </summary>
public interface IOversightService
{
    /// <summary>
    /// Opens a case for every signal that does not have one yet, and returns how many were opened.
    /// Idempotent, so it can run both on the queue's own load (a reviewer never waits a sweep to
    /// see a fresh report) and in the maintenance loop. It is also the migration: the signals that
    /// predate cases get theirs on the first run, backdated to when they were raised.
    /// </summary>
    Task<int> EnsureCasesAsync(CancellationToken cancellationToken = default);

    Task<OversightQueueDto> GetQueueAsync(OversightQuery query, OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>How many open cases the scope can see — the badge in the page head and the nav.</summary>
    Task<int> GetOpenCountAsync(OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>The case with its evidence and history, or null when it does not exist or the scope hides it.</summary>
    Task<OversightCaseDetailDto?> GetCaseAsync(Guid caseId, OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>Takes the case. Refuses one that another reviewer already holds.</summary>
    Task<ParkingResult> ClaimAsync(Guid caseId, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>Puts the case back in the queue.</summary>
    Task<ParkingResult> ReleaseAsync(Guid caseId, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    Task<ParkingResult> SetPriorityAsync(Guid caseId, OversightCasePriority priority, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>Adds a note. Internal unless the reviewer chose to show it to the participants.</summary>
    Task<ParkingResult> CommentAsync(Guid caseId, string body, bool visibleToParticipants, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>Records that the reviewer opened the prefilled mail to <paramref name="recipient"/>.</summary>
    Task<ParkingResult> RecordEmailContactAsync(Guid caseId, string recipient, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rules on the apology voucher the case's mismatch granted, and resolves the case with it —
    /// approving is the same act as finding the report founded, and splitting them into two clicks
    /// would only let the two answers disagree.
    /// </summary>
    Task<ParkingResult> ReviewVoucherAsync(Guid caseId, bool approve, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    Task<ParkingResult> ResolveAsync(Guid caseId, OversightResolution resolution, string? note, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    Task<ParkingResult> ReopenAsync(Guid caseId, string reason, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);
}
