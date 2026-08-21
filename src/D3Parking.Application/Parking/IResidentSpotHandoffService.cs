namespace D3Parking.Application.Parking;

public interface IResidentSpotHandoffService
{
    Task<IReadOnlyList<ResidentSpotHandoffDto>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResidentSpotHandoffUserDto>> SearchRecipientsAsync(
        Guid residentId, string? search, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResidentSpotHandoffUserDto>> SearchResidentsAsync(
        Guid requesterId, string? search, CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateOfferAsync(
        Guid residentId, Guid recipientId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateRequestAsync(
        Guid requesterId, Guid residentId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts a resident offer as its recipient, or approves a user request as its resident. Both
    /// paths create the recipient's reservation atomically.
    /// </summary>
    Task<ParkingResult> AcceptAsync(Guid actorId, Guid handoffId, CancellationToken cancellationToken = default);

    Task<ParkingResult> DeclineAsync(Guid actorId, Guid handoffId, CancellationToken cancellationToken = default);

    Task<ParkingResult> CancelAsync(Guid actorId, Guid handoffId, CancellationToken cancellationToken = default);
}
