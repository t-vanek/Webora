namespace Webora.Application.Parking;

/// <summary>Read access to the incentive standings: a user's score, the leaderboard and point history.</summary>
public interface IIncentiveService
{
    Task<ParkerScoreDto> GetScoreAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(int take = 20, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PointsLedgerEntryDto>> GetHistoryAsync(Guid userId, int take = 50, CancellationToken cancellationToken = default);
}
