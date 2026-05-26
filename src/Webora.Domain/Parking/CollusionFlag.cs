using Webora.Domain.Common;

namespace Webora.Domain.Parking;

/// <summary>
/// A pair of users whose sharing interactions are suspiciously concentrated on each other — a possible
/// collusion ring. Detected automatically; hard action is left to an administrator. The pair is stored
/// with <see cref="UserA"/> &lt; <see cref="UserB"/> so each pair has one stable flag.
/// </summary>
public class CollusionFlag : Entity
{
    public Guid UserA { get; private set; }

    public Guid UserB { get; private set; }

    /// <summary>Number of shared-spot transactions between the two.</summary>
    public int MutualInteractions { get; private set; }

    /// <summary>Percent of UserA's interactions that are with UserB.</summary>
    public int ConcentrationAPercent { get; private set; }

    /// <summary>Percent of UserB's interactions that are with UserA.</summary>
    public int ConcentrationBPercent { get; private set; }

    public CollusionFlagStatus Status { get; private set; } = CollusionFlagStatus.New;

    public DateTimeOffset DetectedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private CollusionFlag() { }

    public CollusionFlag(Guid first, Guid second, int mutualInteractions, int concentrationAPercent, int concentrationBPercent, DateTimeOffset at)
    {
        // Normalise the pair so (X,Y) and (Y,X) are the same flag.
        (UserA, UserB) = first.CompareTo(second) <= 0 ? (first, second) : (second, first);
        MutualInteractions = mutualInteractions;
        ConcentrationAPercent = concentrationAPercent;
        ConcentrationBPercent = concentrationBPercent;
        DetectedAtUtc = at;
        UpdatedAtUtc = at;
    }

    /// <summary>The ordered key for a pair, matching how the flag stores its users.</summary>
    public static (Guid A, Guid B) Key(Guid x, Guid y) => x.CompareTo(y) <= 0 ? (x, y) : (y, x);

    /// <summary>Refreshes the metrics on a re-scan (does not revive a dismissed flag).</summary>
    public void Update(int mutualInteractions, int concentrationAPercent, int concentrationBPercent, DateTimeOffset at)
    {
        MutualInteractions = mutualInteractions;
        ConcentrationAPercent = concentrationAPercent;
        ConcentrationBPercent = concentrationBPercent;
        UpdatedAtUtc = at;
    }

    public void MarkReviewed(DateTimeOffset at)
    {
        Status = CollusionFlagStatus.Reviewed;
        UpdatedAtUtc = at;
    }

    public void Dismiss(DateTimeOffset at)
    {
        Status = CollusionFlagStatus.Dismissed;
        UpdatedAtUtc = at;
    }
}
