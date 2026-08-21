using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Application.Parking;

/// <summary>
/// A private achievement summary. Credits are included only so the shared header can display the
/// independent planning wallet; they never affect achievements and no score, rank or tier exists.
/// </summary>
public sealed record AchievementSummaryDto(
    Guid UserId,
    int Credits,
    IReadOnlyList<ParkingBadge> Achievements,
    int PlansCreated,
    int UsefulReleases,
    int QueueHelps,
    int SharedDaysUsed);
