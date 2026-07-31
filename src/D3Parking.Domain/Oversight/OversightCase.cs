using D3Parking.Domain.Common;

namespace D3Parking.Domain.Oversight;

/// <summary>
/// One piece of work on the oversight desk: a signal the lot raised, plus everything a human
/// needs to act on it — who owns it, how urgent it is, and how it ended.
/// </summary>
/// <remarks>
/// The case is a shell around a signal, never a copy of it. <see cref="SubjectId"/> points at the
/// record that holds the evidence (the reported mismatch, the flagged pair) and the review reads
/// that record at query time, so the photograph, the plate match and the concentration percentages
/// have exactly one home. What lives here is only what the signal itself cannot answer: is anyone
/// working on this, and what was decided.
/// </remarks>
public class OversightCase : Entity
{
    /// <summary>
    /// The case number, from a database sequence. Cases get talked about out loud ("look at 142")
    /// and pasted into mail, so they need a short handle that survives translation — which the id
    /// (a guid) and the signal's own timestamps do not give.
    /// </summary>
    public int Number { get; private set; }

    public OversightCaseKind Kind { get; private set; }

    /// <summary>The signal this case was opened for: the mismatch report, or the collusion flag.</summary>
    public Guid SubjectId { get; private set; }

    /// <summary>The spot the case is about, when it is about one. The axis recurring reports group on.</summary>
    public Guid? SpotId { get; private set; }

    /// <summary>The user whose report opened the case; null when the lot raised it by itself.</summary>
    public Guid? ReporterId { get; private set; }

    public OversightCaseStatus Status { get; private set; } = OversightCaseStatus.New;

    public OversightCasePriority Priority { get; private set; } = OversightCasePriority.Normal;

    public Guid? AssigneeId { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    /// <summary>Last activity of any kind — what the queue sorts a stale case to the top by.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public OversightResolution? Resolution { get; private set; }

    /// <summary>Why it ended that way. Required by the service whenever the verdict has consequences.</summary>
    public string? ResolutionNote { get; private set; }

    private OversightCase() { }

    public OversightCase(OversightCaseKind kind, Guid subjectId, DateTimeOffset openedAtUtc, Guid? spotId = null, Guid? reporterId = null)
    {
        Kind = kind;
        SubjectId = subjectId;
        SpotId = spotId;
        ReporterId = reporterId;
        OpenedAtUtc = openedAtUtc;
        UpdatedAtUtc = openedAtUtc;
    }

    /// <summary>True while the case still wants a human — what "open" means in the queue and the counts.</summary>
    public bool IsOpen => Status != OversightCaseStatus.Resolved;

    /// <summary>A reviewer takes the case. Taking one that someone else holds is the caller's call to refuse.</summary>
    public bool Claim(Guid reviewerId, DateTimeOffset at)
    {
        if (Status != OversightCaseStatus.New && Status != OversightCaseStatus.InProgress)
        {
            return false;
        }

        AssigneeId = reviewerId;
        Status = OversightCaseStatus.InProgress;
        Touch(at);
        return true;
    }

    /// <summary>Back into the queue, unowned.</summary>
    public bool Release(DateTimeOffset at)
    {
        if (Status != OversightCaseStatus.InProgress)
        {
            return false;
        }

        AssigneeId = null;
        Status = OversightCaseStatus.New;
        Touch(at);
        return true;
    }

    public bool SetPriority(OversightCasePriority priority, DateTimeOffset at)
    {
        if (Priority == priority)
        {
            return false;
        }

        Priority = priority;
        Touch(at);
        return true;
    }

    /// <summary>
    /// Records the verdict. The assignee is kept: "who decided this" is part of the answer, and
    /// clearing it would make a resolved case look like nobody ever touched it.
    /// </summary>
    public bool Resolve(OversightResolution resolution, string? note, Guid reviewerId, DateTimeOffset at)
    {
        if (!OversightCaseStatusTransitions.IsAllowed(Status, OversightCaseStatus.Resolved))
        {
            return false;
        }

        Status = OversightCaseStatus.Resolved;
        Resolution = resolution;
        ResolutionNote = note;
        ResolvedAtUtc = at;
        AssigneeId ??= reviewerId;
        Touch(at);
        return true;
    }

    /// <summary>
    /// The verdict was wrong or new evidence arrived. The previous resolution is cleared from the
    /// case — the timeline keeps it, which is where a superseded ruling belongs.
    /// </summary>
    public bool Reopen(Guid reviewerId, DateTimeOffset at)
    {
        if (!OversightCaseStatusTransitions.IsAllowed(Status, OversightCaseStatus.InProgress))
        {
            return false;
        }

        Status = OversightCaseStatus.InProgress;
        Resolution = null;
        ResolutionNote = null;
        ResolvedAtUtc = null;
        AssigneeId = reviewerId;
        Touch(at);
        return true;
    }

    /// <summary>Marks activity that changed nothing on the case itself, such as a comment.</summary>
    public void Touch(DateTimeOffset at) => UpdatedAtUtc = at;
}
