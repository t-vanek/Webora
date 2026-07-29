namespace D3Parking.Application.Parking;

/// <summary>
/// The company-vehicle registry and its pairing with user accounts. Pairing is the three-factor
/// handshake: the profile's plate matches a fleet vehicle, the vehicle's registered driver email
/// matches the account's email, and the user confirms a code sent to that email. A paired vehicle
/// with an assigned spot materializes as the user's residency (ParkingSpot.OwnerId).
/// </summary>
public interface IFleetService
{
    Task<IReadOnlyList<CompanyVehicleDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateAsync(string plate, string? name, string? driverEmail, Guid? spotId, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> UpdateAsync(Guid id, string plate, string? name, string? driverEmail, Guid? spotId, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    Task<ParkingResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Administrator pairing without the code ceremony (the admin is the authority).</summary>
    Task<ParkingResult> PairManuallyAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);

    Task<ParkingResult> UnpairAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PairingStatusDto> GetMyPairingStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Emails the confirmation code to the vehicle's registered driver address.</summary>
    Task<ParkingResult> RequestPairingCodeAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<ParkingResult> ConfirmPairingAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates the user's pairing after their profile plate changed: a paired vehicle whose
    /// plate no longer matches is unpaired and the residency it carried is released.
    /// </summary>
    Task SyncUserPlateAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Nudges a freshly activated user whose plate matches a pairable vehicle.</summary>
    Task NotifyPairableAsync(Guid userId, CancellationToken cancellationToken = default);
}
