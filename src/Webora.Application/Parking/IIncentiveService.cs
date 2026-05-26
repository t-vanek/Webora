namespace Webora.Application.Parking;

/// <summary>Read access to the incentive standings: a user's score, the leaderboard and point history.</summary>
public interface IIncentiveService
{
    Task<ParkerScoreDto> GetScoreAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(int take = 20, CancellationToken cancellationToken = default);

    /// <summary>Departments ranked by their members' average reputation (the team leaderboard).</summary>
    Task<IReadOnlyList<TeamLeaderboardEntryDto>> GetTeamLeaderboardAsync(int take = 20, CancellationToken cancellationToken = default);

    /// <summary>How the user stacks up against their department average, or null when they have no department.</summary>
    Task<PeerComparisonDto?> GetPeerComparisonAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PointsLedgerEntryDto>> GetHistoryAsync(Guid userId, int take = 50, CancellationToken cancellationToken = default);
}
