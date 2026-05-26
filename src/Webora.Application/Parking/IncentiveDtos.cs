using Webora.Domain.Parking.Incentives;

namespace Webora.Application.Parking;

/// <summary>A user's incentive standing, including the badges they have earned.</summary>
public sealed record ParkerScoreDto(
    Guid UserId,
    int Points,
    int ReservationsCompleted,
    int ReservationsReleased,
    int OffPeakReservations,
    int NoShows,
    IReadOnlyList<ParkingBadge> Badges);

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
