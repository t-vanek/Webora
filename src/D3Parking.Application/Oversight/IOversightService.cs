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

    /// <summary>
    /// The lot's own turn at the desk, run from the maintenance sweep: announces the deadlines that
    /// have passed, notices when a signal moved under an open case, and sends the daily digest.
    /// Returns how many cases it wrote something on.
    /// </summary>
    Task<int> RunDueCaseWorkAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Asks the driver something and stops the clock until they answer. Only for a report — a
    /// collusion case has no participant to ask, and telling a pair they are suspected is not a
    /// question, it is an accusation.
    /// </summary>
    Task<ParkingResult> RequestInfoAsync(Guid caseId, string question, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default);

    // --- the driver's side ------------------------------------------------------------------
    // Everything below is bounded by the caller being the case's own reporter rather than by a
    // permission: these are somebody's own report, and no scope makes one person's report another's.

    /// <summary>The driver's own reports, newest first.</summary>
    Task<IReadOnlyList<MyOversightReportDto>> GetMyReportsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The driver adds something to their own report — an answer to what was asked, or a detail
    /// they remembered. Either way the case goes back on the reviewer's desk.
    /// </summary>
    Task<ParkingResult> AddParticipantNoteAsync(Guid caseId, string body, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The driver disputes a ruling that went against them, once. The case reopens unassigned so
    /// whoever picks it up is deciding again rather than confirming themselves.
    /// </summary>
    Task<ParkingResult> AppealAsync(Guid caseId, string reason, Guid userId, CancellationToken cancellationToken = default);
}
