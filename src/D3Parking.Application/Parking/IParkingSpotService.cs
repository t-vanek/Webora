using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Administration of the physical parking spots that make up the lot.</summary>
public interface IParkingSpotService
{
    Task<IReadOnlyList<ParkingSpotDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<ParkingSpotDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateAsync(string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Dry-run for a batch of codes: classifies each as new, duplicate of an existing spot, or invalid.</summary>
    Task<SpotBatchPlan> PreviewBatchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates every new code of the batch with the given type in one transaction; codes that
    /// already exist are skipped and reported so re-running a series stays idempotent.
    /// </summary>
    Task<SpotBatchResult> CreateBatchAsync(IReadOnlyList<string> codes, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> UpdateAsync(Guid id, string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>Assigns a resident to the spot, or clears ownership when ownerId is null.</summary>
    Task<ParkingResult> AssignOwnerAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken = default);
}
