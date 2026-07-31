using D3Parking.Domain.Oversight;

namespace D3Parking.Application.Oversight;

/// <summary>
/// Which case kinds the caller may see. The oversight queue is one screen over signals that are
/// gated apart on purpose — mismatch evidence is photographs of third parties' cars, collusion
/// evidence names two colleagues — so every read is bounded by a scope rather than by the page
/// remembering to filter. A kind outside the scope must not reach the caller in a list, in a
/// detail, or in a count.
/// </summary>
/// <param name="MaySanction">
/// Whether the caller may act against a person from a case. Carried here rather than checked in
/// the page, so the one object that says what a caller is allowed to do says all of it — a screen
/// that forgets the check cannot hand out a deduction the service would have refused.
/// </param>
/// <param name="MayAssign">Whether the caller may put a case on somebody else's desk.</param>
public sealed record OversightScope(
    IReadOnlyList<OversightCaseKind> Kinds,
    bool MaySanction = false,
    bool MayAssign = false)
{
    /// <summary>Sees nothing. What a caller without any review permission gets.</summary>
    public static readonly OversightScope None = new([]);

    /// <summary>Every kind — for the maintenance loop and other callers that are not a person.</summary>
    public static readonly OversightScope All = new(Enum.GetValues<OversightCaseKind>());

    /// <summary>
    /// The scope the reviewer's permissions add up to. Defects ride on managing the spots: they
    /// name nobody and hold no evidence about anyone, so the permission that maintains the lot is
    /// the one that should see them — and the ones that guard evidence about people should not
    /// have to be handed out to fix a broken light.
    /// </summary>
    public static OversightScope From(
        bool reviewMismatches,
        bool reviewCollusion,
        bool manageSpots = false,
        bool maySanction = false,
        bool mayAssign = false,
        bool manageReservations = false)
    {
        var kinds = new List<OversightCaseKind>(4);
        if (reviewMismatches)
        {
            kinds.Add(OversightCaseKind.OccupancyMismatch);
        }

        if (reviewCollusion)
        {
            kinds.Add(OversightCaseKind.CollusionRing);
        }

        if (manageSpots)
        {
            kinds.Add(OversightCaseKind.SpotDefect);
        }

        // A disputed no-show is a reservation decision, so it goes to the permission that already
        // owns them — the same people who can cancel or move somebody's booking.
        if (manageReservations)
        {
            kinds.Add(OversightCaseKind.NoShowDispute);
        }

        return new OversightScope(kinds, maySanction, mayAssign);
    }

    public bool IsEmpty => Kinds.Count == 0;

    public bool Allows(OversightCaseKind kind) => Kinds.Contains(kind);
}
