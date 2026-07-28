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
