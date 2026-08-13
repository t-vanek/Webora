using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

public sealed record ParkingSpotDto(
    Guid Id,
    string Code,
    ParkingSpotType Type,
    bool IsActive,
    string? Notes,
    Guid? OwnerId,
    string? OwnerName,
    int MonthlyShareAllowance);

public enum ParkingSpotStateFilter
{
    All,
    Active,
    Inactive,
}

public enum ParkingSpotOwnershipFilter
{
    All,
    Resident,
    Shared,
}

public sealed record ParkingSpotListQuery(
    string? Search = null,
    ParkingSpotStateFilter State = ParkingSpotStateFilter.All,
    ParkingSpotType? Type = null,
    ParkingSpotOwnershipFilter Ownership = ParkingSpotOwnershipFilter.All);

public sealed record ParkingSpotDirectorySummary(
    int Total,
    int Active,
    int Resident,
    int Shared,
    int Visitor);
