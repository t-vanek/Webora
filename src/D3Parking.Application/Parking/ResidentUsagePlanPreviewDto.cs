namespace D3Parking.Application.Parking;

public sealed record ResidentUsagePlanPreviewDto(
    IReadOnlyList<DateOnly> NewlyReleased,
    IReadOnlyList<DateOnly> Returned,
    IReadOnlyList<DateOnly> PreservedBookings,
    string? Error = null);
