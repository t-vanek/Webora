using Webora.Domain.Parking;

namespace Webora.Application.Parking;

public sealed record ReservationDto(
    Guid Id,
    Guid SpotId,
    string SpotCode,
    ParkingSpotType SpotType,
    Guid UserId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    ReservationStatus Status,
    bool IsOffPeak,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CheckedInAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    DateTimeOffset? CompletedAtUtc);
