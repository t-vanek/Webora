namespace D3Parking.Application.Parking;

/// <summary>A recorded "could not park at the reserved spot" event, for the admin trend view.</summary>
public sealed record OccupancyMismatchDto(
    Guid Id,
    string SpotCode,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset ReportedAtUtc,
    string ReporterName,
    string? RelocatedToSpotCode);
