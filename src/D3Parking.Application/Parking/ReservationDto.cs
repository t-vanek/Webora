using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

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
    DateTimeOffset? CompletedAtUtc,
    int CalendarSequence,
    DateTimeOffset CalendarUpdatedAtUtc)
{
    /// <summary>The original window remains available; early ending exposes its elapsed part.</summary>
    public DateTimeOffset EffectiveEndUtc => ReleasedAtUtc is { } released && released < EndUtc
        ? (released > StartUtc ? released : StartUtc)
        : CompletedAtUtc is { } completed && completed < EndUtc ? completed : EndUtc;
}
