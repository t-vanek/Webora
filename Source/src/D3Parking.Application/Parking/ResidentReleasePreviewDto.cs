namespace D3Parking.Application.Parking;

public sealed record ResidentReleasePreviewDto(IReadOnlyList<DateOnly> Dates, DateOnly? NextAssignedDate, string? Error = null);
