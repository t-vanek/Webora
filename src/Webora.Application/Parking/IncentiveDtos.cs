using Webora.Domain.Parking.Incentives;

namespace Webora.Application.Parking;

/// <summary>A user's incentive standing: reputation points, spendable credit wallet and earned badges.</summary>
public sealed record ParkerScoreDto(
    Guid UserId,
    int Points,
    int Credits,
    int ReservationsCompleted,
    int ReservationsReleased,
    int OffPeakReservations,
    int NoShows,
    int CompletionStreak,
    LoyaltyTier Tier,
    int TrustScore,
    IReadOnlyList<ParkingBadge> Badges);

/// <summary>A department's standing on the team leaderboard (ranked by average reputation).</summary>
public sealed record TeamLeaderboardEntryDto(
    int Rank,
    string Department,
    int MemberCount,
    int AveragePoints,
    double AverageOffPeak);

/// <summary>How a user compares with their department: their figures next to the team average and their rank within it.</summary>
public sealed record PeerComparisonDto(
    string Department,
    int TeamSize,
    int RankInTeam,
    int MyPoints,
    int TeamAveragePoints,
    int MyOffPeak,
    double TeamAverageOffPeak);

/// <summary>A live price quote for booking a spot in a given window, plus the user's ability to pay.</summary>
public sealed record ReservationQuoteDto(
    int Cost,
    int OccupancyPercent,
    bool IsPeak,
    int Balance,
    bool Affordable);

/// <summary>A single row on the incentive leaderboard.</summary>
public sealed record LeaderboardEntryDto(
    int Rank,
    Guid UserId,
    string DisplayName,
    int Points,
    int ReservationsReleased,
    int OffPeakReservations,
    IReadOnlyList<ParkingBadge> Badges);

/// <summary>A single points award or deduction in a user's history.</summary>
public sealed record PointsLedgerEntryDto(
    Guid Id,
    IncentiveReason Reason,
    int Points,
    Guid? ReservationId,
    DateTimeOffset OccurredAtUtc,
    string? Detail);
