using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Administration of the physical parking spots that make up the lot.</summary>
public interface IParkingSpotService
{
    Task<IReadOnlyList<ParkingSpotDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<ParkingSpotDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateAsync(string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> UpdateAsync(Guid id, string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>Assigns a resident to the spot, or clears ownership when ownerId is null.</summary>
    Task<ParkingResult> AssignOwnerAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken = default);
}
