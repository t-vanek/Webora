using Webora.Domain.Parking;

namespace Webora.Application.Parking;

/// <summary>A flagged pair of users whose sharing interactions are suspiciously concentrated on each other.</summary>
public sealed record CollusionFlagDto(
    Guid Id,
    string UserAName,
    string UserBName,
    int MutualInteractions,
    int ConcentrationAPercent,
    int ConcentrationBPercent,
    CollusionFlagStatus Status,
    DateTimeOffset DetectedAtUtc);

/// <summary>
/// Detects reciprocal sharing rings (pairs trading mostly with each other) and records them as flags
/// for an administrator to review. Hard action stays manual; the only automatic effect is the trust
/// graph's per-pair weight cap.
/// </summary>
public interface ICollusionService
{
    /// <summary>Scans the interaction graph and upserts flags if enabled and the interval elapsed. Returns flags touched.</summary>
    Task<int> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>Active flags (newest first) for the admin review page.</summary>
    Task<IReadOnlyList<CollusionFlagDto>> GetFlagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a flag as reviewed (kept on record).</summary>
    Task ReviewAsync(Guid flagId, CancellationToken cancellationToken = default);

    /// <summary>Dismisses a flag as a false positive; future scans leave it alone.</summary>
    Task DismissAsync(Guid flagId, CancellationToken cancellationToken = default);
}
