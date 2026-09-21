using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

public sealed record ResidentSpotHandoffUserDto(
    Guid Id,
    string DisplayName,
    string? Email,
    string? SpotCode = null);

public sealed record ResidentSpotHandoffDto(
    Guid Id,
    Guid SpotId,
    string SpotCode,
    Guid ResidentId,
    string ResidentName,
    Guid RecipientId,
    string RecipientName,
    ResidentSpotHandoffKind Kind,
    ResidentSpotHandoffStatus Status,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int? MaxCreditsAuthorized,
    Guid? ReservationId);
