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

    /// <summary>
    /// When the case should have been dealt with, from its priority. Recomputed from the moment the
    /// priority changes rather than from when the case opened: raising an old case to critical is a
    /// reviewer saying "deal with this now", and a deadline already in the past would say nothing.
    /// </summary>
    public DateTimeOffset? DueAtUtc { get; private set; }

    /// <summary>
    /// When the lot noticed the deadline had passed. Stored so the breach is announced once — a
    /// sweep that re-announced it every five minutes would train reviewers to ignore it.
    /// </summary>
    public DateTimeOffset? SlaBreachedAtUtc { get; private set; }

    /// <summary>
    /// Since when the case has been waiting on the driver. The deadline is pushed by exactly this
    /// much when the answer arrives: the reviewer asked a question, which is work, and charging
    /// them for the time it takes somebody else to reply would punish asking.
    /// </summary>
    public DateTimeOffset? AwaitingSinceUtc { get; private set; }

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

    /// <summary>
    /// Open, the reviewer's move, and past its deadline. A case waiting on the driver is none of
    /// the reviewer's doing, so it is never overdue while it waits.
    /// </summary>
    public bool IsOverdue(DateTimeOffset now) =>
        IsOpen && Status != OversightCaseStatus.AwaitingInfo && DueAtUtc is { } due && due <= now;

    /// <summary>
    /// Sets the priority the lot itself judged the case to deserve, at the moment it is opened.
    /// Separate from <see cref="SetPriority"/> because there is no previous value to report and
    /// nothing to refuse: this is part of opening, not a change to an open case.
    /// </summary>
    public void OpenAt(OversightCasePriority priority, TimeSpan sla)
    {
        Priority = priority;
        DueAtUtc = OpenedAtUtc + sla;
    }

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

    /// <summary>
    /// Changes the priority and re-dates the deadline with it. A breach already announced is
    /// cleared, so a case pulled up to a longer deadline can be breached again on the new terms
    /// rather than staying permanently marked.
    /// </summary>
    public bool SetPriority(OversightCasePriority priority, TimeSpan sla, DateTimeOffset at)
    {
        if (Priority == priority)
        {
            return false;
        }

        Priority = priority;
        DueAtUtc = at + sla;
        SlaBreachedAtUtc = null;
        Touch(at);
        return true;
    }

    /// <summary>The reviewer asked the driver something; the clock stops until an answer comes.</summary>
    public bool AwaitAnswer(DateTimeOffset at)
    {
        if (!OversightCaseStatusTransitions.IsAllowed(Status, OversightCaseStatus.AwaitingInfo))
        {
            return false;
        }

        Status = OversightCaseStatus.AwaitingInfo;
        AwaitingSinceUtc = at;
        Touch(at);
        return true;
    }

    /// <summary>
    /// The wait is over — the driver answered, or it ran out. The deadline moves by however long
    /// the case spent waiting, so the reviewer gets back the time they were not spending.
    /// </summary>
    public bool ResumeWork(DateTimeOffset at)
    {
        if (Status != OversightCaseStatus.AwaitingInfo)
        {
            return false;
        }

        if (AwaitingSinceUtc is { } since && DueAtUtc is { } due)
        {
            DueAtUtc = due + (at - since);
        }

        Status = OversightCaseStatus.InProgress;
        AwaitingSinceUtc = null;
        Touch(at);
        return true;
    }

    /// <summary>Records that the deadline passed. Returns false when it was already announced.</summary>
    public bool MarkSlaBreached(DateTimeOffset at)
    {
        if (SlaBreachedAtUtc is not null || !IsOverdue(at))
        {
            return false;
        }

        SlaBreachedAtUtc = at;
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
        AwaitingSinceUtc = null;
        Touch(at);
        return true;
    }

    /// <summary>
    /// The verdict was wrong or new evidence arrived. The previous resolution is cleared from the
    /// case — the timeline keeps it, which is where a superseded ruling belongs.
    /// </summary>
    public bool Reopen(Guid reviewerId, TimeSpan sla, DateTimeOffset at)
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

        // A reopened case is work starting now, so it gets its full deadline again — carrying the
        // original one over would open it already overdue for a delay nobody caused.
        DueAtUtc = at + sla;
        SlaBreachedAtUtc = null;
        AwaitingSinceUtc = null;
        Touch(at);
        return true;
    }

    /// <summary>
    /// Drops an assignee that should never have stuck. <see cref="Resolve"/> credits an unheld
    /// case to whoever closed it, which is right for a reviewer and wrong for the driver
    /// withdrawing their own report — they did not work the case, they ended it.
    /// </summary>
    public void ClearAssigneeIf(Guid userId)
    {
        if (AssigneeId == userId)
        {
            AssigneeId = null;
        }
    }

    /// <summary>Marks activity that changed nothing on the case itself, such as a comment.</summary>
    public void Touch(DateTimeOffset at) => UpdatedAtUtc = at;
}
