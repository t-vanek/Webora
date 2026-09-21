namespace D3Parking.Application.Parking;

/// <summary>A flagged pair of users whose sharing interactions are suspiciously concentrated on each other.</summary>
public sealed record CollusionFlagDto(
    Guid Id,
    string UserAName,
    string UserBName,
    int MutualInteractions,
    int ConcentrationAPercent,
    int ConcentrationBPercent,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Detects reciprocal sharing rings (pairs trading mostly with each other) and records them as flags.
/// Detection only: every flag becomes an oversight case, which is where it is ruled on, and the only
/// automatic effect on the economy stays the trust graph's per-pair weight cap.
/// </summary>
public interface ICollusionService
{
    /// <summary>Scans the interaction graph and upserts flags if enabled and the interval elapsed. Returns flags raised.</summary>
    Task<int> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>One flag's measurements, for the case opened over it. Null when the flag is gone.</summary>
    Task<CollusionFlagDto?> GetFlagAsync(Guid flagId, CancellationToken cancellationToken = default);
}
