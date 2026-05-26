using Webora.Domain.Parking;

namespace Webora.Application.Parking;

public sealed record ParkingSpotDto(
    Guid Id,
    string Code,
    ParkingSpotType Type,
    bool IsActive,
    string? Notes);
