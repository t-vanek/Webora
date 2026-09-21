namespace D3Parking.Application.Parking;

/// <summary>Reads a user's private, positive-only planning achievements and contribution totals.</summary>
public interface IAchievementService
{
    Task<AchievementSummaryDto> GetSummaryAsync(
        Guid userId, CancellationToken cancellationToken = default);
}
