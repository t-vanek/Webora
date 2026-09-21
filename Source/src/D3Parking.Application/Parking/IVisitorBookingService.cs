namespace D3Parking.Application.Parking;

/// <summary>
/// Reception-managed bookings of Visitor-type spots for guests without accounts. Visitor spots
/// are excluded from the employee pool, so this agenda is the only thing competing for them.
/// </summary>
public interface IVisitorBookingService
{
    /// <summary>Active visitor bookings that have not ended yet, soonest first.</summary>
    Task<IReadOnlyList<VisitorBookingDto>> ListUpcomingAsync(CancellationToken cancellationToken = default);

    /// <summary>Active Visitor-type spots free for the whole window.</summary>
    Task<IReadOnlyList<ParkingSpotDto>> GetFreeSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    Task<ParkingResult> BookAsync(Guid createdById, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        string visitorName, string? company, string? licensePlate, Guid? hostUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy signature retained for source compatibility. A cancellation without an authenticated
    /// actor cannot be authorized and always returns AccessDenied; use CancelCheckedAsync.
    /// </summary>
    Task<ParkingResult> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a current booking after checking the authenticated actor's current ManageVisitors
    /// permission and account status. The actor id must come from the authenticated principal.
    /// Existing implementations that do not implement this checked command safely decline it.
    /// </summary>
    Task<ParkingResult> CancelCheckedAsync(Guid bookingId, Guid actingUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Failure("Parking_Error_AccessDenied"));
}

/// <summary>One visitor booking for the reception agenda.</summary>
public sealed record VisitorBookingDto(
    Guid Id,
    string SpotCode,
    string VisitorName,
    string? Company,
    string? LicensePlate,
    string? HostName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CreatedByName);
