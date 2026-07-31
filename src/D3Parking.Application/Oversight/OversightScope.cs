using D3Parking.Domain.Oversight;

namespace D3Parking.Application.Oversight;

/// <summary>
/// Which case kinds the caller may see. The oversight queue is one screen over signals that are
/// gated apart on purpose — mismatch evidence is photographs of third parties' cars, collusion
/// evidence names two colleagues — so every read is bounded by a scope rather than by the page
/// remembering to filter. A kind outside the scope must not reach the caller in a list, in a
/// detail, or in a count.
/// </summary>
public sealed record OversightScope(IReadOnlyList<OversightCaseKind> Kinds)
{
    /// <summary>Sees nothing. What a caller without either review permission gets.</summary>
    public static readonly OversightScope None = new([]);

    /// <summary>Every kind — for the maintenance loop and other callers that are not a person.</summary>
    public static readonly OversightScope All = new(Enum.GetValues<OversightCaseKind>());

    /// <summary>The scope the two review permissions add up to.</summary>
    public static OversightScope From(bool reviewMismatches, bool reviewCollusion)
    {
        var kinds = new List<OversightCaseKind>(2);
        if (reviewMismatches)
        {
            kinds.Add(OversightCaseKind.OccupancyMismatch);
        }

        if (reviewCollusion)
        {
            kinds.Add(OversightCaseKind.CollusionRing);
        }

        return new OversightScope(kinds);
    }

    public bool IsEmpty => Kinds.Count == 0;

    public bool Allows(OversightCaseKind kind) => Kinds.Contains(kind);
}
