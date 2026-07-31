namespace D3Parking.Domain.Oversight;

/// <summary>The case workflow: which status transitions are permitted.</summary>
/// <remarks>
/// <code>
/// New ──▶ InProgress ──▶ Resolved
///  │  ◀──      │            │
///  └───────────┴────────────┘
/// </code>
/// A case can be resolved straight from <see cref="OversightCaseStatus.New"/> — approving a
/// voucher off a clear photograph is one click and forcing a claim first would only add a
/// second. Reopening lands in <see cref="OversightCaseStatus.InProgress"/> rather than
/// <see cref="OversightCaseStatus.New"/>: whoever reopened it is holding it.
/// </remarks>
public static class OversightCaseStatusTransitions
{
    private static readonly IReadOnlyDictionary<OversightCaseStatus, IReadOnlySet<OversightCaseStatus>> Allowed =
        new Dictionary<OversightCaseStatus, IReadOnlySet<OversightCaseStatus>>
        {
            [OversightCaseStatus.New] = new HashSet<OversightCaseStatus>
            {
                OversightCaseStatus.InProgress,
                OversightCaseStatus.Resolved,
            },
            [OversightCaseStatus.InProgress] = new HashSet<OversightCaseStatus>
            {
                OversightCaseStatus.New,
                OversightCaseStatus.Resolved,
            },
            [OversightCaseStatus.Resolved] = new HashSet<OversightCaseStatus>
            {
                OversightCaseStatus.InProgress,
            },
        };

    public static bool IsAllowed(OversightCaseStatus from, OversightCaseStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}
