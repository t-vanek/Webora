using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>A recorded "could not park at the reserved spot" event, for the admin trend view.</summary>
public sealed record OccupancyMismatchDto(
    Guid Id,
    string SpotCode,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset ReportedAtUtc,
    string ReporterName,
    string? ReporterEmail,
    string? RelocatedToSpotCode,
    IReadOnlyList<MismatchRelatedReservationDto> RelatedReservations);

/// <summary>
/// A reservation that met the mismatch window on the same spot — for the admin, the shortlist of
/// who to ask about the blocked spot (typically whoever reserved it and never arrived).
/// </summary>
public sealed record MismatchRelatedReservationDto(string Name, string? Email, ReservationStatus Status);
